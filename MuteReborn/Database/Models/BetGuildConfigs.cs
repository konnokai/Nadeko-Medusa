namespace MuteReborn.Database.Models
{
    public class BetGuildConfigs : DbEntity
    {
        public ulong GuildId { get; set; }
        public ulong BetRecordChannelId { get; set; } = 0;
        public string HSRBetStartMessage { get; set; } = "";
    }
}
