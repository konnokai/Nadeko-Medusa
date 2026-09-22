using System.Collections.Concurrent;
using Discord;
using MuteReborn.Database;
using MuteReborn.Database.Models;
using NadekoBot.Medusa;
using Serilog;

namespace MuteReborn.Services;

[svc(Lifetime.Singleton)]
public class MuteRebornService
{
    public enum SettingType { BuyMuteRebornTicketCost, EachTicketIncreaseMuteTime, EachTicketDecreaseMuteTime, MaxIncreaseMuteTime, GetAllSetting }

    /// <summary>
    /// 正在勞改中的「GuildId-UserId」清單。指令與 Discord 事件會並行存取，故使用可並行集合。
    /// </summary>
    public ConcurrentDictionary<string, byte> MutingList { get; } = new();

    private readonly TicketReservations _reservations = new();

    private static string TicketKey(ulong guildId, ulong userId)
        => $"{guildId}-{userId}";

    /// <summary>
    /// 購買甦生券的總價。以 long 計算，避免 int 溢位讓總價變成 0 或負數而繞過餘額檢查。
    /// </summary>
    public static long CalculateBuyCost(int unitCost, int num)
        => (long)unitCost * num;

    /// <summary>
    /// 為發起者保留一張券。扣券在禁言成功後才執行，沒有先保留的話，
    /// 同一張券可以在加時階段先被花掉，結束後又被扣一次。
    /// </summary>
    public bool TryReserveTicket(IGuild guild, ulong userId)
        => _reservations.TryReserve(TicketKey(guild.Id, userId), GetRebornTicketNum(guild, userId));

    /// <summary>釋放先前保留的券；扣券完成或勞改失敗時都要呼叫。</summary>
    public void ReleaseTicket(IGuild guild, ulong userId)
        => _reservations.Release(TicketKey(guild.Id, userId));

    /// <summary>
    /// 該使用者是否有進行中的勞改所保留的券。
    /// 用來區分「真的沒券」與「券被自己另一場勞改佔住」這兩種失敗原因。
    /// </summary>
    public bool IsTicketReserved(IGuild guild, ulong userId)
        => _reservations.ReservedFor(TicketKey(guild.Id, userId)) > 0;

    /// <summary>
    /// 扣除進行中勞改所保留的額度後，實際可用的券數。
    /// 所有會消耗券的路徑（加時、蘇生確認）都應以此為準。
    /// </summary>
    public int GetAvailableTicketNum(IGuild guild, ulong userId)
        => Math.Max(0, GetRebornTicketNum(guild, userId) - _reservations.ReservedFor(TicketKey(guild.Id, userId)));

    public bool ToggleRebornStatus(IGuild guild)
    {
        try
        {
            using var db = DBContext.GetDbContext();
            var guildConfig = db.MuteRebornGuildConfigs.SingleOrDefault((x) => x.GuildId == guild.Id);

            if (guildConfig == null)
                guildConfig = new MuteRebornGuildConfigs() { GuildId = guild.Id };

            guildConfig.EnableMuteReborn = !guildConfig.EnableMuteReborn;
            db.MuteRebornGuildConfigs.Update(guildConfig);
            db.SaveChanges();

            return guildConfig.EnableMuteReborn;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"ToggleRebornStatus: {guild.Name}({guild.Id})");
            return false;
        }
    }

    public bool GetRebornStatus(IGuild guild)
    {
        try
        {
            using var db = DBContext.GetDbContext();
            var guildConfig = db.MuteRebornGuildConfigs.SingleOrDefault((x) => x.GuildId == guild.Id);

            if (guildConfig == null)
                return false;

            return guildConfig.EnableMuteReborn;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"GetRebornStatus: {guild.Name}({guild.Id})");
            return false;
        }
    }

    public int GetRebornSetting(IGuild guild, SettingType type)
    {
        try
        {
            using var db = DBContext.GetDbContext();

            var guildConfig = db.MuteRebornGuildConfigs.SingleOrDefault((x) => x.GuildId == guild.Id) ?? throw new NullReferenceException();

            switch (type)
            {
                case SettingType.BuyMuteRebornTicketCost:
                    return guildConfig.BuyMuteRebornTicketCost;
                case SettingType.EachTicketIncreaseMuteTime:
                    return guildConfig.EachTicketIncreaseMuteTime;
                case SettingType.EachTicketDecreaseMuteTime:
                    return guildConfig.EachTicketDecreaseMuteTime;
                case SettingType.MaxIncreaseMuteTime:
                    return guildConfig.MaxIncreaseMuteTime;
            }

            throw new NullReferenceException();
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"GetRebornSetting: {guild.Name}({guild.Id})");
            throw;
        }
    }

    public int GetRebornTicketNum(IGuild guild, ulong userId)
    {
        try
        {
            using var db = DBContext.GetDbContext();

            var guildConfig = db.MuteRebornGuildConfigs.SingleOrDefault((x) => x.GuildId == guild.Id);
            if (guildConfig == null)
                return 0;

            if (userId == 284989733229297664)
                return 114514;

            var muteReborn = db.MuteRebornTickets.FirstOrDefault((x) => x.GuildId == guild.Id && x.UserId == userId);
            if (muteReborn == null)
                return 0;

            return muteReborn.RebornTicketNum;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"GetRebornTicketNum: {guild.Name}({guild.Id})");
            return 0;
        }
    }

    public bool CanReborn(IGuild guild, IUser user)
    {
        using var db = DBContext.GetDbContext();
        var guildConfig = db.MuteRebornGuildConfigs.SingleOrDefault((x) => x.GuildId == guild.Id);
        if (guildConfig == null)
            return false;
        if (!guildConfig.EnableMuteReborn)
            return false;

        if (user.Id == 284989733229297664)
            return true;

        // 扣除進行中勞改保留的額度，避免同一張券被重複使用
        return GetAvailableTicketNum(guild, user.Id) > 0;
    }

    public async Task<(bool, string)> AddRebornTicketNumAsync(IGuild guild, IUser user, int num)
        => await AddRebornTicketNumAsync(guild, user.Id, num);

    public async Task<(bool, string)> AddRebornTicketNumAsync(IGuild guild, ulong userId, int num)
    {
        try
        {
            int addNum = num;

            using var db = DBContext.GetDbContext();
            var guildConfig = db.MuteRebornGuildConfigs.SingleOrDefault((x) => x.GuildId == guild.Id);

            if (guildConfig == null)
                return (false, "伺服器不在資料庫內");

            if (!guildConfig.EnableMuteReborn)
                return (false, "死者蘇生未開啟");

            if (userId == 284989733229297664)
                return (true, $"<@{userId}> 增加 **{addNum}**，剩餘 **114514** 次蘇生機會\n");

            var muteReborn = db.MuteRebornTickets.FirstOrDefault((x) => x.GuildId == guild.Id && x.UserId == userId);
            if (muteReborn == null)
            {
                db.MuteRebornTickets.Add(new MuteRebornTicket() { GuildId = guild.Id, UserId = userId, RebornTicketNum = num });
            }
            else
            {
                num += muteReborn.RebornTicketNum;
                muteReborn.RebornTicketNum = num;
                db.MuteRebornTickets.Update(muteReborn);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);

            return (true, $"<@{userId}> 增加 **{addNum}**，剩餘 **{num}** 次蘇生機會\n");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"AddRebornTicketNumAsyncSingel2: {guild.Name}({guild.Id})");
            return (false, "錯誤，請向 <@284989733229297664>(konnokai) 詢問");
        }
    }

    public async Task<string> AddRebornTicketNumAsync(IGuild guild, List<IGuildUser> users, int num)
    {
        try
        {
            using var db = DBContext.GetDbContext();
            var guildConfig = db.MuteRebornGuildConfigs.SingleOrDefault((x) => x.GuildId == guild.Id);

            if (guildConfig == null)
                return "伺服器不在資料庫內";

            if (!guildConfig.EnableMuteReborn)
                return "死者蘇生未開啟";

            string result = "";
            foreach (var user in users)
            {
                if (user.Id == 284989733229297664)
                {
                    result += $"<@{user.Id}> 增加 **{num}**，剩餘 **114514** 次蘇生機會\n";
                    continue;
                }

                int tempNum = num;
                var muteReborn = db.MuteRebornTickets.FirstOrDefault((x) => x.GuildId == guild.Id && x.UserId == user.Id);
                if (muteReborn == null)
                    db.MuteRebornTickets.Add(new MuteRebornTicket() { GuildId = guild.Id, UserId = user.Id, RebornTicketNum = tempNum });
                else
                {
                    tempNum += muteReborn.RebornTicketNum;
                    muteReborn.RebornTicketNum = tempNum;
                    db.MuteRebornTickets.Update(muteReborn);
                }

                await db.SaveChangesAsync().ConfigureAwait(false);

                result += $"<@{user.Id}> 增加 **{num}**，剩餘 **{tempNum}** 次蘇生機會\n";
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"AddRebornTicketNumAsyncList: {guild.Name}({guild.Id})");
            return $"錯誤，請向 <@284989733229297664>(konnokai) 詢問";
        }
    }

    public List<MuteRebornTicket> ListRebornTicketNum(IGuild guild)
    {
        try
        {
            using var db = DBContext.GetDbContext();
            var muteRebornTickets = db.MuteRebornTickets.Where((x) => x.GuildId == guild.Id);

            if (muteRebornTickets.Any())
                return muteRebornTickets.ToList();

            return [];
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"ListRebornTicketNum: {guild.Name}({guild.Id})");
            return [];
        }
    }
}