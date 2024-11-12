using Discord.WebSocket;
using LogExtension.Service;
using NadekoBot.Common;
using NadekoBot.Medusa;
using Serilog;

namespace LogExtension
{
    public class LogExtension : Snek
    {
        public override string Prefix => "logext";

        private readonly DiscordSocketClient _client;
        private readonly LogExtensionService _service;
        private readonly IHttpClientFactory _factory;
        private readonly ILogCommandService _logCommandService;

        public LogExtension(DiscordSocketClient client, LogExtensionService service, IHttpClientFactory factory, ILogCommandService logCommandService)
        {
            _client = client;
            _service = service;
            _factory = factory;
            _logCommandService = logCommandService;
        }

        public override ValueTask InitializeAsync()
        {
            using (var db = Database.DBContext.GetDbContext())
                db.Database.EnsureCreated();

            _service.Inject(_client, _factory);

            Log.Information("LogExtension Medusa Start");

            return base.InitializeAsync();
        }

        public override ValueTask DisposeAsync()
        {
            _service.CancelEvent();

            Log.Information("LogExtension Medusa Disposed");

            return base.DisposeAsync();
        }

        [cmd(["AttachRemove", "ar"])]
        [bot_owner_only]
        public async Task AttachRemove(GuildContext ctx)
        {
            using (var db = Database.DBContext.GetDbContext())
            {
                string result = "__附件刪除__";
                var GuildLogConfigs = db.GuildLogConfigs.SingleOrDefault((x) => x.GuildId == ctx.Guild.Id);
                if (GuildLogConfigs == null)
                {
                    db.GuildLogConfigs.Add(new Database.Models.GuildLogConfigs() { GuildId = ctx.Guild.Id, AttachRemovedId = ctx.Channel.Id });
                    result = $"在此頻道中記錄 {result} 事件。";
                }
                else if (GuildLogConfigs.AttachRemovedId != ctx.Channel.Id)
                {
                    GuildLogConfigs.AttachRemovedId = ctx.Channel.Id;
                    db.GuildLogConfigs.Update(GuildLogConfigs);
                    result = $"已變更在此頻道中記錄 {result} 事件。";
                }
                else
                {
                    GuildLogConfigs.AttachRemovedId = null;
                    db.GuildLogConfigs.Update(GuildLogConfigs);
                    result = $"已取消本頻道的 {result} 事件。";
                }

                await db.SaveChangesAsync();
                await ctx.SendConfirmAsync(result);

                _service.RefreshConfig();
            }
        }

        [cmd(["ReactionRemove", "rr"])]
        [bot_owner_only]
        public async Task ReactionRemove(GuildContext ctx)
        {
            using (var db = Database.DBContext.GetDbContext())
            {
                string result = "__移除反應__";
                var GuildLogConfigs = db.GuildLogConfigs.SingleOrDefault((x) => x.GuildId == ctx.Guild.Id);
                if (GuildLogConfigs == null)
                {
                    db.GuildLogConfigs.Add(new Database.Models.GuildLogConfigs() { GuildId = ctx.Guild.Id, ReactionRemovedId = ctx.Channel.Id });
                    result = $"在此頻道中記錄 {result} 事件。";
                }
                else if (GuildLogConfigs.ReactionRemovedId != ctx.Channel.Id)
                {
                    GuildLogConfigs.ReactionRemovedId = ctx.Channel.Id;
                    db.GuildLogConfigs.Update(GuildLogConfigs);
                    result = $"已變更在此頻道中記錄 {result} 事件。";
                }
                else
                {
                    GuildLogConfigs.ReactionRemovedId = null;
                    db.GuildLogConfigs.Update(GuildLogConfigs);
                    result = $"已取消本頻道的 {result} 事件。";
                }

                await db.SaveChangesAsync();
                await ctx.SendConfirmAsync(result);

                _service.RefreshConfig();
            }
        }

        [cmd(["LogIgnore", "ignore"])]
        [user_perm(Discord.GuildPermission.Administrator)]
        public async Task LogIgnore(GuildContext ctx)
        {
            // true if logignore is removed
            var isRemoved = _logCommandService.LogIgnore(ctx.Guild.Id, ctx.Channel.Id, NadekoBot.Db.Models.IgnoredItemType.Channel);

            using (var db = Database.DBContext.GetDbContext())
            {
                string result;
                var logIgnore = db.LogIgnores.SingleOrDefault((x) => x.ChannelId == ctx.Channel.Id);
                if (isRemoved)
                {
                    if (logIgnore != null)
                        db.LogIgnores.Remove(logIgnore);

                    result = "此頻道現在 __不會__ 被忽略紀錄";
                }
                else
                {
                    if (logIgnore == null)
                        db.LogIgnores.Add(new Database.Models.LogIgnores() { ChannelId = ctx.Channel.Id });

                    result = "此頻道現在 __會__ 被忽略紀錄";
                }

                await db.SaveChangesAsync();
                await ctx.SendConfirmAsync(result);

                _service.RefreshConfig();
            }
        }
    }
}