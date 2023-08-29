namespace MuteReborn.Database.Models;

public class MuteRebornTicket : DbEntity
{
    public ulong UserId { get; set; }
    public ulong GuildId { get; set; }
    public int RebornTicketNum { get; set; } = 0;
}