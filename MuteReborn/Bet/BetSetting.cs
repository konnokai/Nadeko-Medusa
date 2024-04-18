using Discord;
using Discord.WebSocket;
using Nadeko.Snake;
using NadekoBot;

namespace MuteReborn;

public partial class MuteReborn
{
    [cmd(["SetRecordChannel", "src"])]
    [user_perm(GuildPermission.Administrator)]
    public async Task SetRecordChannel(GuildContext ctx, ITextChannel channel = null)
    {
        using var db = Database.DBContext.GetDbContext();
        var betGuildConfigs = db.BetGuildConfigs.FirstOrDefault((x) => x.GuildId == ctx.Guild.Id);

        if (channel == null)
        {
            if (betGuildConfigs == null)
            {
                await ctx.SendErrorAsync("此伺服器未設定賭博紀錄頻道");
                return;
            }

            db.BetGuildConfigs.Remove(betGuildConfigs);
            await db.SaveChangesAsync();

            await ctx.SendConfirmAsync("已移除本伺服器的賭博紀錄頻道設定");
            return;
        }

        if (channel is not SocketTextChannel textChannel)
        {
            await ctx.SendErrorAsync("僅可設定一般文字頻道");
            return;
        }

        var permissions = (await ctx.Guild.GetCurrentUserAsync()).GetPermissions(textChannel);
        if (!permissions.ViewChannel || !permissions.SendMessages)
        {
            await ctx.SendErrorAsync($"我在 `{textChannel}` 沒有 `讀取&編輯頻道` 的權限，請給予權限後再次執行本指令");
            return;
        }

        if (!permissions.EmbedLinks)
        {
            await ctx.SendErrorAsync($"我在 `{textChannel}` 沒有 `嵌入連結` 的權限，請給予權限後再次執行本指令");
            return;
        }

        if (!permissions.AttachFiles)
        {
            await ctx.SendErrorAsync($"我在 `{textChannel}` 沒有 `附加檔案` 的權限，請給予權限後再次執行本指令");
            return;
        }

        if (betGuildConfigs == null)
        {
            betGuildConfigs = new Database.Models.BetGuildConfigs() { GuildId = ctx.Guild.Id, BetRecordChannelId = channel.Id };
        }
        else
        {
            betGuildConfigs.BetRecordChannelId = channel.Id;
        }

        db.BetGuildConfigs.Update(betGuildConfigs);
        await db.SaveChangesAsync();

        await ctx.SendConfirmAsync($"已將賭博紀錄頻道設定至 `{channel}`");
    }

    [cmd(["SetHSRBetStartMessage", "shbsm"])]
    [user_perm(GuildPermission.Administrator)]
    public async Task SetHSRBetStartMessage(GuildContext ctx, [leftover] string message = null)
    {
        using var db = Database.DBContext.GetDbContext();
        var betGuildConfigs = db.BetGuildConfigs.FirstOrDefault((x) => x.GuildId == ctx.Guild.Id);
        if (betGuildConfigs == null)
        {
            await ctx.SendErrorAsync("請先使用 `~src #頻道` 指令設定紀錄頻道後再設定開始訊息");
            return;
        }

        betGuildConfigs.HSRBetStartMessage = message;
        db.BetGuildConfigs.Update(betGuildConfigs);
        await db.SaveChangesAsync();

        await ctx.SendConfirmAsync($"已更新詞條賭局開始的訊息:\n`{message}`");
    }
}
