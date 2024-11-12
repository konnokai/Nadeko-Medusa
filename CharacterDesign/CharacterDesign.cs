using CharacterDesign.Service;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using NadekoBot.Common;
using NadekoBot.Extensions;
using NadekoBot.Medusa;
using NadekoBot.Services;
using Serilog;

namespace CharacterDesign
{
    public sealed class CharacterDesign : Snek
    {
        private readonly DiscordSocketClient _client;
        private readonly CharacterDesignService _service;
        private readonly ResponseBuilder _response;

        public CharacterDesign(DiscordSocketClient client,
            CharacterDesignService service,
            IBotStrings botStrings,
            BotConfigService botConfigService,
            ImagesConfig ic,
            IHttpClientFactory factory,
            IImageCache images,
            DbService db)
        {
            _client = client;
            _service = service;
            _response = new ResponseBuilder(botStrings, botConfigService, client);

            _service.Inject(client, ic, factory, images, db);
        }

        public override ValueTask InitializeAsync()
        {
            Log.Information("CharacterDesign Medusa Start");

            return base.InitializeAsync();
        }

        public override ValueTask DisposeAsync()
        {
            Log.Information("CharacterDesign Medusa Disposed");

            return base.DisposeAsync();
        }

        [cmd(["AddCharDesign", "acd"])]
        [bot_owner_only]
        public async Task AddCharDesign(AnyContext ctx, string designName = "", string charAvatar = "", [leftover] string playingList = "")
        {
            if (string.IsNullOrEmpty(designName) || string.IsNullOrEmpty(charAvatar) || string.IsNullOrEmpty(playingList))
                return;

            (bool, CharacterDesignService.CharacterDesign?) result = await _service.AddCharDesignAsync(designName, charAvatar, playingList).ConfigureAwait(false);

            if (result.Item1 && result.Item2 != null)
            {
                var embed = new EmbedBuilder()
                    .WithColor(Color.Green)
                    .WithTitle("人設新增成功!")
                    .WithThumbnailUrl(charAvatar)
                    .WithDescription($"名稱: {designName}\n\n" +
                    $"PlayingStatus:\n" +
                    $"{string.Join('\n', result.Item2.PlayingList.Select(rs => $"{rs.Type} {rs.Status}"))}");

                await ctx.Channel.SendMessageAsync(null, false, embed.Build());
            }
            else
                await ctx.SendErrorAsync("人設已存在");
        }

        [cmd(["AddCharDesignPlayingStatus", "acdps"])]
        [bot_owner_only]
        public async Task AddCharDesignPlayingStatus(AnyContext ctx, string designName = "", [leftover] string playingList = "")
        {
            if (string.IsNullOrEmpty(designName) || string.IsNullOrEmpty(playingList))
                return;

            (bool, CharacterDesignService.CharacterDesign?) result = _service.AddCharDesignPlayingStatus(designName, playingList);

            if (result.Item1 && result.Item2 != null)
            {
                var embed = new EmbedBuilder()
                    .WithColor(Color.Green)
                    .WithTitle("人設狀態新增成功!")
                    .WithDescription($"名稱: {designName}\n\n" +
                    $"PlayingStatus:" +
                    $"\n{string.Join('\n', result.Item2.PlayingList.Select(rs => $"{rs.Type} {rs.Status}"))}");

                await ctx.Channel.SendMessageAsync(null, false, embed.Build());
            }
            else
                await ctx.SendErrorAsync("人設狀態新增失敗");
        }

        [cmd(["ChangeCharDesign", "ccd"])]
        [bot_owner_only]
        public async Task ChangeCharDesign(AnyContext ctx, [leftover] string designName = "")
        {
            if (string.IsNullOrEmpty(designName))
                return;

            if (await _service.ChangeCharDesignAsync(designName).ConfigureAwait(false))
                await ctx.SendConfirmAsync("人設切換成功!");
            else
                await ctx.SendErrorAsync("人設切換失敗");
        }

        [cmd(["ListCharDesign", "lcd"])]
        [bot_owner_only]
        public async Task ListCharDesign(AnyContext ctx)
        {
            var list = _service.ListCharDesign();

            if (list == null || list.Length == 0)
            {
                await ctx.SendErrorAsync("人設清單空白");
            }
            else
            {
                await _response
                    .Context(new SocketCommandContext(_client, (SocketUserMessage)ctx.Message))
                    .Paginated()
                    .Items(list)
                    .PageSize(10)
                    .AddFooter()
                    .Page((items, _) =>
                    {
                        return new EmbedBuilder()
                            .WithColor(Color.Green)
                            .WithTitle("可用人設")
                            .WithDescription(string.Join('\n', items));
                    })
                    .SendAsync();
            }
        }

        [cmd(["SaveCharDesign", "scd"])]
        [bot_owner_only]
        public async Task SaveCharDesign(AnyContext ctx, [leftover] string designName = "")
        {
            if (string.IsNullOrEmpty(designName))
                designName = _client.CurrentUser.Username;

            (bool, CharacterDesignService.CharacterDesign?) result = await _service.SaveCharDesignAsync(designName).ConfigureAwait(false);

            if (result.Item1 && result.Item2 != null)
            {
                var embed = new EmbedBuilder()
                    .WithColor(Color.Green)
                    .WithTitle("人設保存成功!")
                    .WithThumbnailUrl(_client.CurrentUser.GetAvatarUrl())
                    .WithDescription($"名稱: {designName}\n\n" +
                        $"PlayingStatus:" +
                        $"\n{string.Join('\n', result.Item2.PlayingList.Select(rs => $"{rs.Type} {rs.Status}"))}");

                await ctx.Channel.SendMessageAsync(null, false, embed.Build());
            }
            else
                await ctx.SendErrorAsync("人設保存失敗");
        }

        [cmd(["DeleteCharDesign", "dcd"])]
        [bot_owner_only]
        public async Task DeleteCharDesign(AnyContext ctx, [leftover] string designName = "")
        {
            if (string.IsNullOrEmpty(designName))
                designName = _client.CurrentUser.Username;

            await ctx.SendYesNoConfirmAsync(_response, _client, $"確定要刪除 {designName} 的人設嗎?", async (action) =>
            {
                if (action)
                {
                    if (_service.DeleteCharDesign(designName)) await ctx.SendConfirmAsync("人設刪除成功");
                    else await ctx.SendErrorAsync("人設刪除失敗");
                }
            }, ctx.User);
        }
    }
}