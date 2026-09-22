using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using NadekoBot.Extensions;
using NadekoBot.Medusa;
using Serilog;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GuildLogConfigs = LogExtension.Database.Models.GuildLogConfigs;

namespace LogExtension.Service
{
    [svc(Lifetime.Singleton)]
    public class LogExtensionService
    {
        public enum LogType
        {
            AttachRemoved,
            ReactionRemoved,
        }

        private DiscordSocketClient _client;
        private IHttpClientFactory _httpFactory;

        private readonly Timer _autoDeleteAttachDir;
        private ConcurrentBag<GuildLogConfigs> guildLogConfigs;
        private ConcurrentBag<Database.Models.LogIgnores> logIgnores;

        /// <summary>
        /// 進行中的附件下載，key 為目標檔案路徑。
        /// 刪除事件靠它等待尚未寫完的檔案；下載結束後會自行移除。
        /// </summary>
        private readonly ConcurrentDictionary<string, Task> _pendingAttachments = new();

        public LogExtensionService()
        {
            // 刪除14天的附件紀錄
            _autoDeleteAttachDir = new Timer((obj) =>
            {
                try
                {
                    Regex regex = new Regex(@"(\d{4})(\d{2})(\d{2})");
                    var list = Directory.GetDirectories("attach_log", "202?????", SearchOption.TopDirectoryOnly);
                    foreach (var item in list)
                    {
                        var regexResult = regex.Match(item);
                        if (!regexResult.Success) continue;

                        if (DateTime.Now.Subtract(Convert.ToDateTime($"{regexResult.Groups[1]}/{regexResult.Groups[2]}/{regexResult.Groups[3]}")) > TimeSpan.FromDays(14))
                        {
                            Directory.Delete(item, true);
                            Log.Warning($"已刪除: {item}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                }
            }, null, TimeSpan.FromSeconds(Math.Round(Convert.ToDateTime($"{DateTime.Now.AddDays(1):yyyy/MM/dd 00:00:00}").Subtract(DateTime.Now).TotalSeconds) + 1), TimeSpan.FromDays(1));
        }

        public void Inject(DiscordSocketClient client, IHttpClientFactory factory)
        {
            _client = client;
            _httpFactory = factory;

            _client.MessageReceived += _client_MessageReceived;
            _client.MessageDeleted += _client_MessageDeleted;
            _client.ReactionRemoved += _client_ReactionRemoved;

            RefreshConfig();
        }

        public void CancelEvent()
        {
            _client.MessageReceived -= _client_MessageReceived;
            _client.MessageDeleted -= _client_MessageDeleted;
            _client.ReactionRemoved -= _client_ReactionRemoved;

            // 卸載時停止每日清理工作，否則重新載入後會有多個清理 timer 同時存在
            _autoDeleteAttachDir.Dispose();
        }

        public void RefreshConfig()
        {
            using var db = Database.DBContext.GetDbContext();
            guildLogConfigs = new ConcurrentBag<GuildLogConfigs>(db.GuildLogConfigs.AsNoTracking());
            logIgnores = new ConcurrentBag<Database.Models.LogIgnores>(db.LogIgnores.AsNoTracking());
        }

        /// <summary>
        /// 把訊息附件存到 attach_log，供訊息被刪除時補送紀錄。
        /// 不在 gateway 事件裡等待網路下載（Discord.Net 會等待事件 handler，慢速下載會拖住同 shard 的其他事件），
        /// 改為登記下載 Task，由刪除事件依檔案路徑等待對應的下載。
        /// </summary>
        private Task _client_MessageReceived(SocketMessage arg)
        {
            try
            {
                if (arg is not IUserMessage msg || msg.IsAuthor(_client))
                    return Task.CompletedTask;

                if (!msg.Attachments.Any())
                    return Task.CompletedTask;

                if (arg.Channel is not ITextChannel channel)
                    return Task.CompletedTask;

                if (!guildLogConfigs.Any((x) => x.GuildId == channel.GuildId) ||
                    logIgnores.Any((x) => x.ChannelId == channel.Id))
                    return Task.CompletedTask;

                foreach (var item in msg.Attachments)
                {
                    if (item.Size >= 25 * 1048576 || !item.Url.TryGetAttachmentFilePath(out var path))
                        continue;

                    var filePath = $"attach_log/{msg.CreatedAt:yyyyMMdd}/{path}";
                    var download = SaveAttachmentAsync(item.Url, filePath);
                    _pendingAttachments[filePath] = download;

                    // 下載結束（成功或失敗）就從登記表移除，避免無上限成長；
                    // 之後才發生的刪除事件直接看檔案是否存在即可。
                    _ = download.ContinueWith(_ => _pendingAttachments.TryRemove(filePath, out _), TaskScheduler.Default);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "LogExtensionService_Client_MessageReceived");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 下載附件並原子地寫入檔案。實際寫入交由 <see cref="Ext.SaveStreamAtomicallyAsync"/>。
        /// 本方法不拋例外，讓等待它的刪除事件只需處理「檔案不存在」。
        /// </summary>
        private async Task SaveAttachmentAsync(string url, string filePath)
        {
            try
            {
                using var httpClient = _httpFactory.CreateClient();
                await using var download = await httpClient.GetStreamAsync(url).ConfigureAwait(false);
                await Ext.SaveStreamAtomicallyAsync(download, filePath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "LogExtensionService: 附件下載失敗 {Url}", url);
            }
        }

        private Task _client_MessageDeleted(Cacheable<IMessage, ulong> cacheMsg, Cacheable<IMessageChannel, ulong> cacheCh)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (cacheMsg.Value is not IUserMessage msg || msg.IsAuthor(_client))
                        return;

                    var ch = cacheCh.Value;
                    if (ch is not ITextChannel channel)
                        return;

                    GuildLogConfigs guildLogConfig = guildLogConfigs.SingleOrDefault((x) => x.GuildId == channel.GuildId && x.AttachRemovedId != null);
                    if (guildLogConfig == null || logIgnores.Any(ilc => ilc.ChannelId == channel.Id))
                        return;

                    ITextChannel? logChannel;
                    if ((logChannel = await TryGetLogChannel(channel.Guild, guildLogConfig, LogType.AttachRemoved)) is null
                        || logChannel.Id == channel.Id)
                        return;

                    if (msg.Attachments.Any())
                    {
                        foreach (var item in msg.Attachments)
                        {
                            if (!item.Url.TryGetAttachmentFilePath(out string path))
                                continue;

                            var filePath = $"attach_log/{msg.CreatedAt:yyyyMMdd}/{path}";

                            // 下載可能還在進行中：先等它結束再判斷檔案是否存在（SaveAttachmentAsync 不拋例外）
                            if (_pendingAttachments.TryGetValue(filePath, out var download))
                                await download.ConfigureAwait(false);

                            if (!File.Exists(filePath))
                                continue;

                            await logChannel.SendFileAsync(filePath);
                            File.Delete(filePath);
                        }
                    }
                }
                catch (Exception ex) { Log.Error(ex, "LogExtensionService_Client_MessageDeleted"); }
            });
            return Task.CompletedTask;
        }

        private Task _client_ReactionRemoved(Cacheable<IUserMessage, ulong> cacheMsg, Cacheable<IMessageChannel, ulong> cacheCh, SocketReaction reaction)
        {
            var _ = Task.Run(async () =>
            {
                try
                {
                    if (cacheCh.Value is not ITextChannel channel)
                        return;

                    GuildLogConfigs guildLogConfig = guildLogConfigs.SingleOrDefault((x) => x.GuildId == channel.GuildId && x.ReactionRemovedId != null);
                    if (guildLogConfig == null || logIgnores.Any(ilc => ilc.ChannelId == channel.Id))
                        return;

                    ITextChannel? logChannel;
                    if ((logChannel = await TryGetLogChannel(channel.Guild, guildLogConfig, LogType.ReactionRemoved)) is null
                        || logChannel.Id == channel.Id)
                        return;

                    string context = "-";
                    var message = await cacheMsg.DownloadAsync().ConfigureAwait(false);
                    if (message.Content != "")
                        context = message.Content.TrimTo(50);
                    else if (message.Attachments.Any())
                        context = message.Attachments.First().Url;
                    else if (message.Stickers.Any())
                        context = $"(貼圖: {message.Stickers.First().Name})";

                    var embed = new EmbedBuilder()
                        .WithColor(Color.Green)
                        .WithTitle("🗑 表情移除")
                        .WithDescription(context)
                        .AddField(reaction.User.Value.Username, reaction.Emote, false)
                        .AddField("Id", cacheMsg.Id.ToString(), false)
                        .WithCurrentTimestamp()
                        .Build();

                    await logChannel.SendMessageAsync(embed: embed).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "LogExtensionService_Client_ReactionRemoved");
                }
            });
            return Task.CompletedTask;
        }

        private async Task<ITextChannel?> TryGetLogChannel(IGuild guild, GuildLogConfigs guildLogConfig, LogType logChannelType)
        {
            ulong? id = null;
            switch (logChannelType)
            {
                case LogType.AttachRemoved:
                    id = guildLogConfig.AttachRemovedId;
                    break;
                case LogType.ReactionRemoved:
                    id = guildLogConfig.ReactionRemovedId;
                    break;
            }

            if (id is null or 0)
                return null;

            var channel = await guild.GetTextChannelAsync(id.Value);

            if (channel is null)
            {
                UnsetLogSetting(guild.Id, logChannelType);
                return null;
            }

            return channel;
        }

        private void UnsetLogSetting(ulong guildId, LogType logChannelType)
        {
            using var db = Database.DBContext.GetDbContext();
            var guildLogConfig = db.GuildLogConfigs.SingleOrDefault((x) => x.GuildId == guildId);

            if (guildLogConfig == null)
                return;

            switch (logChannelType)
            {
                case LogType.AttachRemoved:
                    guildLogConfig.AttachRemovedId = null;
                    break;
                case LogType.ReactionRemoved:
                    guildLogConfig.ReactionRemovedId = null;
                    break;
            }

            db.SaveChanges();
            RefreshConfig();
        }
    }

    public static class Ext
    {
        public static Regex AttchUrlRegex { get; private set; } = new(@"^https://cdn.discordapp.com/attachments/(?<path>[^\s/$.?#].[^\s]*)$", RegexOptions.Compiled);

        public static bool TryGetAttachmentFilePath(this string input, out string path)
        {
            input = input.Split('?')[0];
            var match = AttchUrlRegex.Match(input);
            if (match.Success)
            {
                path = match.Groups["path"].Value.Replace("/", "_").Replace("\\", "_");
                return true;
            }
            path = string.Empty;
            return false;
        }

        /// <summary>
        /// 把 <paramref name="source"/> 原子地寫入 <paramref name="filePath"/>：
        /// 先寫入 <c>.tmp</c>，完整複製並關閉後才搬到正式路徑。
        /// 串流中途失敗時只會留下（並刪除）暫存檔，正式檔案不會出現被視為下載成功的截斷內容。
        /// </summary>
        /// <returns>寫入成功回 true；失敗回 false 並記錄 warning。</returns>
        public static async Task<bool> SaveStreamAtomicallyAsync(Stream source, string filePath)
        {
            var tempPath = filePath + ".tmp";

            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                await using (var file = File.Create(tempPath))
                    await source.CopyToAsync(file).ConfigureAwait(false);

                File.Move(tempPath, filePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "LogExtensionService: 附件寫入失敗 {Path}", filePath);

                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception deleteEx)
                {
                    Log.Warning(deleteEx, "LogExtensionService: 刪除暫存檔失敗 {Path}", tempPath);
                }

                return false;
            }
        }
    }
}
