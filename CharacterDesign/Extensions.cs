using Discord;
using Discord.Net;
using Discord.WebSocket;
using Nadeko.Snake;
using NadekoBot;
using Serilog;

namespace CharacterDesign
{
    public static class Extensions
    {
        private const string BUTTON_YES = "BUTTON_YES";
        private const string BUTTON_NO = "BUTTON_NO";

        private static readonly Emoji _okEmoji = new Emoji("✅");
        private static readonly Emoji _errorEmoji = new Emoji("❌");

        public static string ToDesignPath(this string designName)
        {
            return $"data/char_design/{designName}/";
        }

        public static bool IsImage(this HttpResponseMessage msg)
            => IsImage(msg, out _);

        public static bool IsImage(this HttpResponseMessage msg, out string? mimeType)
        {
            mimeType = msg.Content.Headers.ContentType?.MediaType;
            if (mimeType is "image/png" or "image/jpeg" or "image/gif")
                return true;

            return false;
        }

        public static async Task SendYesNoConfirmAsync(this AnyContext ctx, IEmbedBuilder eb, DiscordSocketClient client, string text, Action<bool> action, IUser? user = null, bool withNo = true)
        {
            ComponentBuilder GetComponentBuilder()
            {
                var cb = new ComponentBuilder();

                cb.WithButton(new ButtonBuilder()
                    .WithStyle(ButtonStyle.Primary)
                    .WithCustomId(BUTTON_YES)
                    .WithEmote(_okEmoji));

                if (withNo)
                {
                    cb.WithButton(new ButtonBuilder()
                        .WithStyle(ButtonStyle.Danger)
                        .WithCustomId(BUTTON_NO)
                        .WithEmote(_errorEmoji));
                }

                return cb;
            }

            async Task RemoveComponentAsync(SocketMessageComponent smc)
            {
                await smc.ModifyOriginalResponseAsync(x =>
                {
                    x.Embed = eb.Build();
                    x.Components = new ComponentBuilder().Build(); // 按下後就移除掉按鈕
                });
            }

            eb.WithOkColor().WithDescription(text);

            var component = GetComponentBuilder().Build();
            var msg = await ctx.Channel.SendMessageAsync(null, embed: eb.Build(), components: component);
            bool isSelect = false;

            async Task OnInteractionAsync(SocketInteraction si)
            {
                try
                {
                    if (si.HasResponded)
                        return;

                    if (si is not SocketMessageComponent smc)
                        return;

                    if (smc.Message.Id != msg.Id)
                        return;

                    if (isSelect)
                        return;

                    if (user != null && smc.User.Id != user.Id)
                    {
                        await si.RespondAsync(embed: new EmbedBuilder().WithColor(Color.Red).WithDescription("你不可使用本按鈕").Build(), ephemeral: true);
                        return;
                    }
                    else if (user == null && smc.User.Id != ctx.User.Id)
                    {
                        await si.RespondAsync(embed: new EmbedBuilder().WithColor(Color.Red).WithDescription("你不可使用本按鈕").Build(), ephemeral: true);
                        return;
                    }

                    await si.DeferAsync();

                    if (smc.Data.CustomId == BUTTON_YES)
                    {
                        action(true);
                        isSelect = true;
                        _ = RemoveComponentAsync(smc);
                    }
                    else if (smc.Data.CustomId == BUTTON_NO)
                    {
                        action(false);
                        isSelect = true;
                        _ = RemoveComponentAsync(smc);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in SendYesNoConfirmAsync pagination: {ErrorMessage}", ex.Message);
                }
            }

            client.InteractionCreated += OnInteractionAsync;

            int i = 60;
            do
            {
                i--;
                await Task.Delay(500);
            } while (!isSelect && i >= 0);

            client.InteractionCreated -= OnInteractionAsync;

            try
            {
                await msg.ModifyAsync(mp => mp.Components = new ComponentBuilder().Build());
            }
            catch (HttpException discordEx) when (discordEx.DiscordCode == DiscordErrorCode.UnknownMessage)
            {
                Log.Information("訊息已刪除，略過");
            }
        }
    }
}
