namespace HardMute.Database.Models;

public class UnmuteTimer : DbEntity
{
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public DateTime UnmuteAt { get; set; }
}