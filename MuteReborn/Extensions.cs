using Discord;
using Discord.Commands;
using Discord.WebSocket;
using NadekoBot;
using NadekoBot.Extensions;
using NadekoBot.Medusa;

namespace MuteReborn
{
    public static class Extensions
    {
        private const string BUTTON_YES = "BUTTON_YES";
        private const string BUTTON_NO = "BUTTON_NO";

        public static async Task SendYesNoConfirmAsync(this AnyContext ctx, ResponseBuilder _builder, DiscordSocketClient client, string text, Action<bool> action, IUser? user = null, bool withNo = true)
        {
            var model = await _builder.Context(new SocketCommandContext(client, (SocketUserMessage)(ctx.Message))).BuildAsync(true);

            NadekoButtonInteractionHandler? yes;
            NadekoButtonInteractionHandler? no;

            (NadekoButtonInteractionHandler yes, NadekoButtonInteractionHandler no) GetInteractions()
            {
                var yesButton = new ButtonBuilder()
                                 .WithStyle(ButtonStyle.Success)
                                 .WithCustomId(BUTTON_YES)
                                 .WithLabel("是");

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
                    clearAfter: true);

                var noButton = new ButtonBuilder()
                                  .WithStyle(ButtonStyle.Danger)
                                  .WithCustomId(BUTTON_NO)
                                  .WithLabel("否");

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
            if (withNo)
            {
                no.AddTo(cb);
            }

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

        public static async Task<Dictionary<IEmote, List<ulong>>> GetEmojiCountAsync(this AnyContext ctx, string text)
        {
            var dic = new Dictionary<IEmote, List<ulong>>();
            var msg = await ctx.SendConfirmAsync(text).ConfigureAwait(false);
            await Task.Delay(30000).ConfigureAwait(false);

            try
            {
                msg = await ctx.Channel.GetMessageAsync(msg.Id).ConfigureAwait(false) as IUserMessage;

                foreach (var reacton in msg.Reactions)
                {
                    await foreach (var reactoinUsers in msg.GetReactionUsersAsync(reacton.Key, 30))
                    {
                        foreach (var user in reactoinUsers)
                        {
                            if (dic.ContainsKey(reacton.Key))
                                dic[reacton.Key].Add(user.Id);
                            else
                                dic.Add(reacton.Key, new List<ulong>() { user.Id });
                        }
                    }
                }

                try
                {
                    await msg.DeleteAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }
            catch
            {
                await ctx.SendErrorAsync("原訊息已刪除導致無法統計，略過加時");
            }

            return dic;
        }
    }
}
