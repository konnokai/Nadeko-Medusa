using Discord;
using MuteReborn.Bet.HSR;
using MuteReborn.Database;
using MuteReborn.Database.Models;
using Nadeko.Snake;
using Serilog;

namespace MuteReborn.Services;

[svc(Lifetime.Singleton)]
public class MuteRebornService
{
    internal List<HSRBetData> RunningHSRBetList { get; private set; } = new();
    internal Dictionary<string, string> SubAffixList { get; } = new()
    {
        { "hp", "大/小生命" },
        { "defence" , "大/小防禦" },
        { "attack", "大/小攻擊" },
        { "critical", "爆率/爆傷" },
        { "status", "效果抗性/命中" },
        { "break_damage", "擊破特攻" },
        { "speed", "速度" },
        { "banker", "莊家"}
    };

    public enum SettingType { BuyMuteRebornTicketCost, EachTicketIncreaseMuteTime, EachTicketDecreaseMuteTime, MaxIncreaseMuteTime, GetAllSetting }
    public HashSet<string> MutingList = new();

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

        var muteReborn = db.MuteRebornTickets.FirstOrDefault((x) => x.GuildId == guild.Id && x.UserId == user.Id);
        if (muteReborn == null)
            return false;

        if (muteReborn.RebornTicketNum > 0)
            return true;

        return false;
    }

    public async Task<(bool, string)> AddRebornTicketNumAsync(IGuild guild, IUser user, int num)
    => await AddRebornTicketNumAsync(guild, user.Id, num);

    public async Task<(bool, string)> AddRebornTicketNumAsync(IGuild guild, ulong user, int num)
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

            var muteReborn = db.MuteRebornTickets.FirstOrDefault((x) => x.GuildId == guild.Id && x.UserId == user);
            if (muteReborn == null)
            {
                db.MuteRebornTickets.Add(new MuteRebornTicket() { GuildId = guild.Id, UserId = user, RebornTicketNum = num });
            }
            else
            {
                num += muteReborn.RebornTicketNum;
                muteReborn.RebornTicketNum = num;
                db.MuteRebornTickets.Update(muteReborn);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);

            return (true, $"<@{user}> 增加**{addNum}**，剩餘**{num}**次蘇生機會\n");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"AddRebornTicketNumAsyncSingel2: {guild.Name}({guild.Id})");
            return (false, "錯誤，請向 <@284989733229297664>(孤之界#1121) 詢問");
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

                result += $"<@{user.Id}> 增加**{num}**，剩餘**{tempNum}**次蘇生機會\n";
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"AddRebornTicketNumAsyncList: {guild.Name}({guild.Id})");
            return $"錯誤，請向 <@284989733229297664>(孤之界#1121) 詢問";
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

            return new List<MuteRebornTicket>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"ListRebornTicketNum: {guild.Name}({guild.Id})");
            return new List<MuteRebornTicket>();
        }
    }
}