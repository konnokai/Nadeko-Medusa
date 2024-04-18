using Nadeko.Snake;
using NadekoBot;
using NadekoBot.Modules.Utility.Services;

namespace RemindInstantNoodles
{
    public class RemindInstantNoodlesCommand : Snek
    {
        private readonly RemindService _remindService;

        public RemindInstantNoodlesCommand(RemindService remindService)
        {
            _remindService = remindService;
        }

        [cmd(["RemindInstantNoodles", "泡麵"])]
        public async Task RemindInstantNoodles(AnyContext ctx, int minutes = 3)
        {
            minutes = Math.Max(2, Math.Min(10, minutes));

            ulong target = ctx.User.Id; ulong? guildId = null;
            bool isPrivate = true;
            if (ctx is GuildContext guildContext)
            {
                isPrivate = false;
                guildId = guildContext.Guild.Id;
                target = ctx.Channel.Id;
            }

            await _remindService.AddReminderAsync(ctx.User.Id, target, guildId, isPrivate, DateTime.UtcNow.Add(TimeSpan.FromMinutes(minutes)), ctx.User.Mention + "該吃泡麵囉！").ConfigureAwait(false);

            await ctx.SendConfirmAsync($"⏰ 我將會於 `{minutes}` 分鐘後在 <#{target}> 提醒: {ctx.User.Mention}該吃泡麵囉！");
        }
    }
}