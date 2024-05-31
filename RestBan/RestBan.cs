using Discord;
using Discord.Net;
using Discord.WebSocket;
using Nadeko.Snake;
using NadekoBot;
using Newtonsoft.Json;
using RestBan.Service;
using Serilog;

namespace RestBan
{
    public class RestBan : Snek
    {
        private readonly DiscordSocketClient _client;
        private readonly RestBanService _service;

        public RestBan(DiscordSocketClient client, RestBanService service)
        {
            _client = client;
            _service = service;
        }

        public override ValueTask InitializeAsync()
        {
            Log.Information("RestBan Medusa Start");

            return base.InitializeAsync();
        }

        public override ValueTask DisposeAsync()
        {
            Log.Information("RestBan Medusa Disposed");

            return base.DisposeAsync();
        }


        [cmd(["RestBan"])]
        [bot_owner_only]
        public async Task RestBanAsync(AnyContext ctx, ulong userId = 0)
        {
            if (userId == 0)
            {
                await ctx.SendErrorAsync($"{userId} 使用者不存在");
                return;
            }

            int num = 0;
            var errorList = new List<string>();
            var tempList = new List<ulong>(_service.RestBanList);
            foreach (var item in tempList)
            {
                try
                {
                    var guild = await _client.Rest.GetGuildAsync(item);
                    await guild.AddBanAsync(userId, 0, "Rest Ban");
                    num++;
                }
                catch (NullReferenceException)
                {
                    Log.Error($"RestBan-伺服器不存在: {item}");
                    errorList.Add(item.ToString());

                    _service.RestBanList.Remove(item);
                    
                    File.WriteAllText(_service.FILE_PATH, JsonConvert.SerializeObject(_service.RestBanList));
                }
                catch (HttpException httpEx) when (httpEx.DiscordCode == DiscordErrorCode.UnknownUser)
                {
                    Log.Error($"RestBan-使用者不存在: {userId}");
                    await ctx.SendErrorAsync($"{userId} 使用者不存在");
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"RestBan-其他錯誤: {item}");
                    errorList.Add(item.ToString());
                }
            }

            var toSend = ctx.Embed().WithOkColor()
                .WithTitle("⛔️ 用戶已被封鎖")
                .AddField("ID", userId, true)
                .AddField("總共被Ban的伺服器數量", num.ToString(), true);

            if (errorList.Any())
                toSend.AddField("無法Ban的伺服器", string.Join('\n', errorList), false);

            await ctx.Channel.EmbedAsync(toSend)
                .ConfigureAwait(false);
        }

        [cmd]
        [bot_owner_only]
        public async Task AddRest(GuildContext ctx, ulong guildId = 0)
        {
            if (guildId == 0)
                guildId = ctx.Guild.Id;

            var guild = _client.Guilds.FirstOrDefault((x) => x.Id == guildId);
            if (guild == null)
                return;

            if (_service.RestBanList.Contains(guild.Id))
            {
                await ctx.SendErrorAsync($"{guildId} 已存在").ConfigureAwait(false);
                return;
            }

            _service.RestBanList.Add(guild.Id);
            File.WriteAllText(_service.FILE_PATH, JsonConvert.SerializeObject(_service.RestBanList));

            await ctx.SendConfirmAsync($"已新增 {guild.Name}").ConfigureAwait(false);
        }

        [cmd]
        [bot_owner_only]
        public async Task DelRest(GuildContext ctx, ulong guildId = 0)
        {
            if (guildId == 0)
                guildId = ctx.Guild.Id;

            if (_service.RestBanList.Contains(guildId))
            {
                _service.RestBanList.Remove(guildId);
                File.WriteAllText(_service.FILE_PATH, JsonConvert.SerializeObject(_service.RestBanList));
                await ctx.SendConfirmAsync($"已移除 {guildId}").ConfigureAwait(false);
            }
            else
            {
                await ctx.SendErrorAsync($"{guildId} 不存在").ConfigureAwait(false);
            }
        }
    }
}