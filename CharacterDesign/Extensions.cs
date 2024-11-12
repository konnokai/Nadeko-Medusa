using Discord;
using Discord.WebSocket;
using NadekoBot;
using NadekoBot.Extensions;
using NadekoBot.Medusa;

namespace CharacterDesign
{
    public static class Extensions
    {
        private const string BUTTON_YES = "BUTTON_YES";
        private const string BUTTON_NO = "BUTTON_NO";

        private static readonly Emoji _okEmoji = new("✅");
        private static readonly Emoji _errorEmoji = new("❌");

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

        public static async Task SendYesNoConfirmAsync(this AnyContext ctx, ResponseBuilder _builder, DiscordSocketClient client, string text, Action<bool> action, IUser? user = null, bool withNo = true)
        {
            var model = await _builder.BuildAsync(true);

            NadekoButtonInteractionHandler? yes;
            NadekoButtonInteractionHandler? no;

            (NadekoButtonInteractionHandler yes, NadekoButtonInteractionHandler no) GetInteractions()
            {
                var yesButton = new ButtonBuilder()
                                 .WithStyle(ButtonStyle.Primary)
                                 .WithCustomId(BUTTON_YES)
                                 .WithEmote(_okEmoji);

                var yesBtnInter = new NadekoButtonInteractionHandler(client,
                    user?.Id ?? 0,
                    yesButton,
                    (smc) =>
                    {
                        action(true);
                        return Task.CompletedTask;
                    },
                    user != null,
                    singleUse: true,
                    clearAfter: false);

                var noButton = new ButtonBuilder()
                                  .WithStyle(ButtonStyle.Danger)
                                  .WithCustomId(BUTTON_NO)
                                  .WithEmote(_errorEmoji);

                var noBtnInter = new NadekoButtonInteractionHandler(client,
                    user?.Id ?? 0,
                    noButton,
                    (smc) =>
                    {
                        action(false);
                        return Task.CompletedTask;
                    },
                    user != null,
                    singleUse: true,
                    clearAfter: true);

                return (yesBtnInter, noBtnInter);
            }

            (yes, no) = GetInteractions();

            var cb = new ComponentBuilder();
            yes.AddTo(cb);
            no.AddTo(cb);

            var msg = await model.TargetChannel
                                 .SendMessageAsync(model.Text,
                                     embed: new EmbedBuilder().WithOkColor().WithDescription(text).Build(),
                                     components: cb.Build(),
                                     allowedMentions: model.SanitizeMentions,
                                     messageReference: model.MessageReference);

            await Task.WhenAll(yes.RunAsync(msg), no.RunAsync(msg));

            await Task.Delay(30_000);

            await msg.ModifyAsync(mp => mp.Components = new ComponentBuilder().Build());
        }
    }
}