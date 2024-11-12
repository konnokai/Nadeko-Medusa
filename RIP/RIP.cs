using Discord;
using Discord.WebSocket;
using Nadeko.Common;
using NadekoBot.Medusa;
using NadekoBot.Services;
using RIP.Service;
using Serilog;

namespace RemindInstantNoodles
{
    // 不知道為啥把 RIP 指令給移除掉了，手動補成 Medusa
    // https://gitlab.com/Kwoth/nadekobot/-/commit/3d287b2afacce46f1d2f759a1a13c5932a7c672e
    public class RIP : Snek
    {
        private IImageCache _imgs;
        private IBotCache _c;
        private IHttpClientFactory _httpFactory;

        private readonly DiscordSocketClient _client;
        private readonly RIPService _service;

        public RIP(DiscordSocketClient client, RIPService service, IImageCache imageCache, IBotCache botCache, IHttpClientFactory httpClientFactory)
        {
            _client = client;
            _service = service;
            _imgs = imageCache;
            _c = botCache;
            _httpFactory = httpClientFactory;
        }

        public override ValueTask InitializeAsync()
        {
            Log.Information("RIP Medusa Start");

            _service.Inject(_imgs, _c, _httpFactory);

            return base.InitializeAsync();
        }

        public override ValueTask DisposeAsync()
        {
            Log.Information("RIP Medusa Disposed");

            return base.DisposeAsync();
        }


        [cmd(["rip", "rip"])]
        public async Task RIPAsync(GuildContext ctx, IGuildUser user)
        {
            var av = user.GetDisplayAvatarUrl();
            await using var picStream = await _service.GetRipPictureAsync(user.DisplayName ?? user.Username, new Uri(av));
            await ctx.Channel.SendFileAsync(picStream,
                "rip.png",
                $"Rip {Format.Bold(user.ToString())} \n\t- " + Format.Italics(ctx.User.ToString()));

        }
    }
}