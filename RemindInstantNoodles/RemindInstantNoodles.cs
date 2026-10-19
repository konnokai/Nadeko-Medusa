using NadekoBot.Medusa;
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

            // 7.6 起提醒有數量上限，超過時 bot 不會新增，只回傳 false
            if (!await _remindService.AddReminderAsync(ctx.User.Id, target, guildId, isPrivate, DateTime.UtcNow.Add(TimeSpan.FromMinutes(minutes)), ctx.User.Mention + "該吃泡麵囉！", NadekoBot.Db.Models.ReminderType.User).ConfigureAwait(false))
            {
                await ctx.SendErrorAsync($"提醒數量已達上限（每人 {RemindService.MAX_REMINDERS_PER_USER} 個、每個伺服器 {RemindService.MAX_REMINDERS_PER_SERVER} 個），請先刪除舊的提醒");
                return;
            }

            await ctx.SendConfirmAsync($"⏰ 我將會於 `{minutes}` 分鐘後在 <#{target}> 提醒: {ctx.User.Mention}該吃泡麵囉！");
        }
    }
}