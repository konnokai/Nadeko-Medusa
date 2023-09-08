namespace LogExtension.Database.Models;

public class GuildLogConfigs : DbEntity
{
    public ulong? GuildId { get; set; } = null;
    public ulong? AttachRemovedId { get; set; } = null;
    public ulong? ReactionRemovedId { get; set; } = null;
}