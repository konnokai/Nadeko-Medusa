using Discord;
using Discord.WebSocket;
using Nadeko.Snake;
using NadekoBot;
using Serilog;

namespace CharacterDesign;

public static class MessageChannelExtensions
{
    public static Task SendPaginatedConfirmAsync(
        this AnyContext ctx,
        DiscordSocketClient client,
        int currentPage,
        Func<int, IEmbedBuilder> pageFunc,
        int totalElements,
        int itemsPerPage,
        bool addPaginatedFooter = true)
        => ctx.SendPaginatedConfirmAsync(client,
            currentPage,
            x => Task.FromResult(pageFunc(x)),
            totalElements,
            itemsPerPage,
            addPaginatedFooter);

    private const string BUTTON_LEFT = "BUTTON_LEFT";
    private const string BUTTON_RIGHT = "BUTTON_RIGHT";

    private static readonly IEmote _arrowLeft = Emote.Parse("<:x:969658061805465651>");
    private static readonly IEmote _arrowRight = Emote.Parse("<:x:969658062220701746>");

    public static Task SendPaginatedConfirmAsync(
        this AnyContext ctx,
        DiscordSocketClient client,
        int currentPage,
        Func<int, Task<IEmbedBuilder>> pageFunc,
        int totalElements,
        int itemsPerPage,
        bool addPaginatedFooter = true)
        => ctx.SendPaginatedConfirmAsync(client,
            currentPage,
            pageFunc,
            default(Func<int, ValueTask<SimpleInteraction<object>?>>),
            totalElements,
            itemsPerPage,
            addPaginatedFooter);

    public static async Task SendPaginatedConfirmAsync<T>(
        this AnyContext ctx,
        DiscordSocketClient client,
        int currentPage,
        Func<int, Task<IEmbedBuilder>> pageFunc,
        Func<int, ValueTask<SimpleInteraction<T>?>>? interFactory,
        int totalElements,
        int itemsPerPage,
        bool addPaginatedFooter = true)
    {
        var lastPage = (totalElements - 1) / itemsPerPage;

        var embed = await pageFunc(currentPage);

        if (addPaginatedFooter)
            embed.AddPaginatedFooter(currentPage, lastPage);

        SimpleInteraction<T>? maybeInter = null;
        async Task<ComponentBuilder> GetComponentBuilder()
        {
            var cb = new ComponentBuilder();

            cb.WithButton(new ButtonBuilder()
                .WithStyle(ButtonStyle.Primary)
                .WithCustomId(BUTTON_LEFT)
                .WithDisabled(lastPage == 0)
                .WithEmote(_arrowLeft)
                .WithDisabled(currentPage <= 0));

            if (interFactory is not null)
            {
                maybeInter = await interFactory(currentPage);

                if (maybeInter is not null)
                    cb.WithButton(maybeInter.Button);
            }

            cb.WithButton(new ButtonBuilder()
                .WithStyle(ButtonStyle.Primary)
                .WithCustomId(BUTTON_RIGHT)
                .WithDisabled(lastPage == 0 || currentPage >= lastPage)
                .WithEmote(_arrowRight));

            return cb;
        }

        async Task UpdatePageAsync(SocketMessageComponent smc)
        {
            var toSend = await pageFunc(currentPage);
            if (addPaginatedFooter)
                toSend.AddPaginatedFooter(currentPage, lastPage);

            var component = (await GetComponentBuilder()).Build();

            await smc.ModifyOriginalResponseAsync(x =>
            {
                x.Embed = toSend.Build();
                x.Components = component;
            });
        }

        var component = (await GetComponentBuilder()).Build();
        var msg = await ctx.Channel.SendMessageAsync(null, embed: embed.Build(), components: component);

        async Task OnInteractionAsync(SocketInteraction si)
        {
            try
            {
                if (si is not SocketMessageComponent smc)
                    return;

                if (smc.Message.Id != msg.Id)
                    return;

                await si.DeferAsync();
                if (smc.User.Id != ctx.User.Id)
                    return;

                if (smc.Data.CustomId == BUTTON_LEFT)
                {
                    if (currentPage == 0)
                        return;

                    --currentPage;
                    _ = UpdatePageAsync(smc);
                }
                else if (smc.Data.CustomId == BUTTON_RIGHT)
                {
                    if (currentPage >= lastPage)
                        return;

                    ++currentPage;
                    _ = UpdatePageAsync(smc);
                }
                else if (maybeInter is { } inter && inter.Button.CustomId == smc.Data.CustomId)
                {
                    await inter.TriggerAsync(smc);
                    _ = UpdatePageAsync(smc);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in pagination: {ErrorMessage}", ex.Message);
            }
        }

        if (lastPage == 0 && interFactory is null)
            return;

        client.InteractionCreated += OnInteractionAsync;

        await Task.Delay(30_000);

        client.InteractionCreated -= OnInteractionAsync;

        await msg.ModifyAsync(mp => mp.Components = new ComponentBuilder().Build());
    }

    public static IEmbedBuilder AddPaginatedFooter(this IEmbedBuilder embed, int curPage, int? lastPage)
    {
        if (lastPage is not null)
            return embed.WithFooter($"{curPage + 1} / {lastPage + 1}");
        return embed.WithFooter(curPage.ToString());
    }
}