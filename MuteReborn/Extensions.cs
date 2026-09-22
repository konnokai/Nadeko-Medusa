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

        /// <summary>
        /// 送出一則是/否確認訊息並等待使用者選擇。
        /// 回呼採 <see cref="Func{T, TResult}"/> 而非 <c>Action</c>，避免呼叫端的 async lambda 變成 async void
        /// 而讓例外逃出 NadekoButtonInteractionHandler 的 try/catch（未處理的 async void 例外會直接終止行程）。
        /// 任一顆按鈕被按下時會一併結束另一顆的等待，否則沒被點到的那顆會持續佔用全域互動事件訂閱到逾時。
        /// </summary>
        public static async Task SendYesNoConfirmAsync(this AnyContext ctx, ResponseBuilder _builder, DiscordSocketClient client, string text, Func<bool, Task> action, IUser? user = null, bool withNo = true)
        {
            var model = await _builder.Context(new SocketCommandContext(client, (SocketUserMessage)(ctx.Message))).BuildAsync(true);

            user ??= ctx.User;

            NadekoButtonInteractionHandler? yes = null;
            NadekoButtonInteractionHandler? no = null;

            async Task HandleAsync(SocketMessageComponent smc, bool result)
            {
                yes?.SetCompleted();
                no?.SetCompleted();

                // 先回應互動：底層只在回呼完成後才 DeferAsync，而這裡的回呼會呼叫 Discord API／DB，
                // 超過 Discord 的 3 秒期限會讓使用者看到互動失敗、實際卻已完成
                if (!smc.HasResponded)
                    await smc.DeferAsync();

                await action(result);
            }

            var yesButton = new ButtonBuilder()
                             .WithStyle(ButtonStyle.Success)
                             .WithCustomId(BUTTON_YES)
                             .WithLabel("是");

            yes = new NadekoButtonInteractionHandler(client,
                user.Id,
                yesButton,
                async (smc) => await HandleAsync(smc, true),
                onlyAuthor: true,
                singleUse: true,
                clearAfter: true);

            var noButton = new ButtonBuilder()
                              .WithStyle(ButtonStyle.Danger)
                              .WithCustomId(BUTTON_NO)
                              .WithLabel("否");

            no = new NadekoButtonInteractionHandler(client,
                user.Id,
                noButton,
                async (smc) => await HandleAsync(smc, false),
                onlyAuthor: true,
                singleUse: true,
                clearAfter: true);

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
