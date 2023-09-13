using Discord.WebSocket;
using Nadeko.Snake;
using NadekoBot;
using NadekoBot.Common;
using Newtonsoft.Json;
using Serilog;

namespace HSRUtility
{
    public class HSRUtility : Snek
    {
        public override string Prefix => "hsr";

        private readonly DiscordSocketClient _client;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IBotCache _cache;

        public HSRUtility(DiscordSocketClient client, IHttpClientFactory httpClientFactory, IBotCache cache)
        {
            _client = client;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
        }

        public override ValueTask InitializeAsync()
        {
            using (var db = Database.DBContext.GetDbContext())
                db.Database.EnsureCreated();

            Log.Information("HSRUtility Medusa Start");

            return base.InitializeAsync();
        }

        public override ValueTask DisposeAsync()
        {
            Log.Information("HSRUtility Medusa Disposed");

            return base.DisposeAsync();
        }

        // Resource master Url: https://raw.githubusercontent.com/Mar-7th/StarRailRes/master/{path}
        // GitHub: https://github.com/Mar-7th/StarRailRes

        [cmd(new[] { "LinkUserId", "link" })]
        public async Task LinkUserId(AnyContext ctx, string userId = "")
        {
            try
            {
                using var db = Database.DBContext.GetDbContext();
                var playerIdLink = db.PlayerIdLink.FirstOrDefault((x) => x.UserId == ctx.User.Id);

                if (string.IsNullOrEmpty(userId))
                {
                    if (playerIdLink != null)
                    {
                        db.PlayerIdLink.Remove(playerIdLink);
                        await db.SaveChangesAsync();

                        await ctx.SendConfirmAsync("已移除你綁定的UID");
                        return;
                    }

                    await ctx.SendErrorAsync("尚未綁定UID");
                    return;
                }

                var (isSuccess, data) = await GetUserDataAsync(userId);
                if (!isSuccess)
                {
                    await ctx.SendErrorAsync($"綁定UID失敗，請確認UID `{userId}` 是否正確");
                    return;
                }

                if (playerIdLink == null)
                    playerIdLink = new Database.Models.PlayerIdLink() { UserId = ctx.User.Id, PlayerId = data.Player.Uid };
                else
                    playerIdLink.PlayerId = data.Player.Uid;

                db.PlayerIdLink.Update(playerIdLink);
                await db.SaveChangesAsync();

                await ctx.SendConfirmAsync($"綁定成功，玩家名稱: `{data.Player.Nickname}`");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "HSR-LinkUserId");
                await ctx.SendErrorAsync("未知的錯誤");
            }
        }

        [cmd(new[] { "GetUserDetail", "detail" })]
        public async Task GetUserDetail(AnyContext ctx, string userId = "")
        {
            if (string.IsNullOrEmpty(userId))
            {
                using var db = Database.DBContext.GetDbContext();
                var playerIdLink = db.PlayerIdLink.FirstOrDefault((x) => x.UserId == ctx.User.Id);
                if (playerIdLink != null)
                    userId = playerIdLink.PlayerId;
            }

            var (isSuccess, data) = await GetUserDataAsync(userId);
            if (!isSuccess)
            {
                await ctx.SendErrorAsync($"獲取資料失敗，請確認UID `{userId}` 是否正確");
                return;
            }

            await ctx.Channel.SendMessageAsync(embed: ctx.Embed()
                .WithOkColor()
                .WithTitle(data.Player.Nickname)
                .WithDescription(data.Player.Signature)
                .WithThumbnailUrl($"https://raw.githubusercontent.com/Mar-7th/StarRailRes/master/{data.Player.Avatar.Icon}")
                .AddField("等級", data.Player.Level)
                .WithFooter("玩家資料會快取半小時", "https://raw.githubusercontent.com/Mar-7th/StarRailRes/master/icon/sign/SettingsAccount.png")
                .Build());
        }

        private async Task<(bool isSuccess, SRInfoJson data)> GetUserDataAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId))
                throw new NullReferenceException(nameof(userId));

            try
            {
                var userInfo = await _cache.GetOrDefaultAsync(new TypedKey<SRInfoJson>($"hsr:{userId}"));

                if (userInfo == default)
                {
                    var httpClient = _httpClientFactory.CreateClient("HSR");
                    var json = await httpClient.GetStringAsync($"https://api.mihomo.me/sr_info_parsed/{userId}?lang=cht");
                    if (json == "{\"detail\":\"Invalid uid\"}")
                        return (false, null);

                    userInfo = JsonConvert.DeserializeObject<SRInfoJson>(json);
                    await _cache.AddAsync(new TypedKey<SRInfoJson>($"hsr:{userId}"), userInfo, TimeSpan.FromMinutes(30));
                }

                return (true, userInfo);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "HSR-GetUserData");
                return (false, null);
            }
        }
    }
}