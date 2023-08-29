namespace MuteReborn.Database.Models
{
    public class GuildConfigs : DbEntity
    {
        public ulong GuildId { get; set; }
        public bool EnableMuteReborn { get; set; } = false;
        public int BuyMuteRebornTicketCost { get; set; } = 10000;
        public int EachTicketIncreaseMuteTime { get; set; } = 5;
        public int EachTicketDecreaseMuteTime { get; set; } = 30;
        public int MaxIncreaseMuteTime { get; set; } = 30;
    }
}
