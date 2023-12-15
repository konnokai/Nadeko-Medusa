using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Nadeko.Snake;
using NadekoBot.Extensions;
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
        }

        public void RefreshConfig()
        {
            using var db = Database.DBContext.GetDbContext();
            guildLogConfigs = new ConcurrentBag<GuildLogConfigs>(db.GuildLogConfigs.AsNoTracking());
            logIgnores = new ConcurrentBag<Database.Models.LogIgnores>(db.LogIgnores.AsNoTracking());
        }

        private Task _client_MessageReceived(SocketMessage arg)
        {
            var _ = Task.Run(async () =>
            {
                try
                {
                    if (arg is not IUserMessage msg || msg.IsAuthor(_client))
                        return;

                    if (!msg.Attachments.Any())
                        return;

                    if (arg.Channel is not ITextChannel channel)
                        return;

                    if (!guildLogConfigs.Any((x) => x.GuildId == channel.GuildId) ||
                        logIgnores.Any((x) => x.ChannelId == channel.Id))
                        return;

                    using var httpClient = _httpFactory.CreateClient();

                    foreach (var item in msg.Attachments)
                    {
                        if (item.Size < 25 * 1048576 && item.Url.TryGetAttachmentFilePath(out string path))
                        {
                            byte[] data = await httpClient.GetByteArrayAsync(item.Url);
                            if (!Directory.Exists($"attach_log/{msg.CreatedAt:yyyyMMdd}"))
                                Directory.CreateDirectory($"attach_log/{msg.CreatedAt:yyyyMMdd}");

                            try
                            {
                                File.WriteAllBytes($"attach_log/{msg.CreatedAt:yyyyMMdd}/{path}", data);
                            }
                            catch { }
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            });
            return Task.CompletedTask;
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
                            if (item.Url.TryGetAttachmentFilePath(out string path) && File.Exists($"attach_log/{msg.CreatedAt:yyyyMMdd}/{path}"))
                            {
                                await logChannel.SendFileAsync($"attach_log/{msg.CreatedAt:yyyyMMdd}/{path}");
                                File.Delete($"attach_log/{msg.CreatedAt:yyyyMMdd}{path}");
                            }
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
                catch { }
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
    }
}
