using Discord;
using Discord.WebSocket;
using HardMute.Services;
using Nadeko.Snake;
using NadekoBot;
using NadekoBot.Common.TypeReaders.Models;
using Serilog;

namespace HardMute
{
    public class HardMute : Snek
    {
        private readonly DiscordSocketClient _client;
        private readonly HardMuteService _service;

        public HardMute(DiscordSocketClient client, HardMuteService service)
        {
            _client = client;
            _service = service;
        }

        public override ValueTask InitializeAsync()
        {
            using (var db = Database.DBContext.GetDbContext())
                db.Database.EnsureCreated();

            _service.Inject(_client);

            Log.Information("HardMute Medusa Start");

            return base.InitializeAsync();
        }

        public override ValueTask DisposeAsync()
        {
            _service.CancelEvent();

            Log.Information("HardMute Medusa Disposed");

            return base.DisposeAsync();
        }

        [cmd(["HardMute"])]
        [bot_owner_only]
        public async Task HardMuteAsync(GuildContext ctx, StoopidTime time, [leftover] string user)
        {
            if (time.Time < TimeSpan.FromMinutes(1) || time.Time > TimeSpan.FromDays(1))
                return;

            user = user.Replace("<", "").Replace("@", "").Replace("!", "").Replace(">", "");
            var list = user.Trim().Split([' ']);

            foreach (var item in list)
            {
                IGuildUser target = await ctx.Guild.GetUserAsync(ulong.Parse(item));
                if (target == null)
                    continue;

                if (target.Id == 284989733229297664)
                    continue;

                try
                {
                    await _service.TimedHardMute(target, time.Time);
                    await ctx.SendConfirmAsync($"{Format.Bold(target.ToString())} 已被 **強制禁止** __文字聊天__，持續 {time.Time.TotalMinutes} 分鐘。");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "HardMuteAsync");
                    await ctx.SendErrorAsync($"錯誤: {ex.Message}");
                }
            }
        }

        [cmd(["UnHardMute"])]
        [bot_owner_only]
        public async Task UnHardMuteAsync(GuildContext ctx, IUser user)
        {
            try
            {
                await _service.UnmuteUser(ctx.Guild.Id, user.Id).ConfigureAwait(false);
                await ctx.SendConfirmAsync($"{Format.Bold(user.ToString())} 的 **強制禁止** __文字聊天__ 已被 **解除**。");
            }
            catch (Exception ex)
            {
                await ctx.SendErrorAsync($"錯誤: {ex.Message}");
            }
        }
    }
}