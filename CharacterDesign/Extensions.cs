using Discord;
using Discord.Commands;
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

        public static string ToDesignPath(this string designName)
        {
            return $"data/char_design/{designName}/";
        }

        /// <summary>
        /// 檢查人設名稱是否可安全地當成資料夾名稱使用。
        /// 拒絕空字串、含路徑分隔符或 <c>..</c> 的名稱，並用 <see cref="Path.GetFullPath(string)"/>
        /// 確認解析後仍位於 data/char_design 底下，避免人設的新增／覆寫／刪除跑到其他目錄。
        /// </summary>
        public static bool IsValidDesignName(this string? designName)
        {
            if (string.IsNullOrWhiteSpace(designName) || designName.Contains(".."))
                return false;

            if (designName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;

            var root = Path.GetFullPath("data/char_design");
            var full = Path.GetFullPath(Path.Combine(root, designName));

            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
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
                                     embed: new EmbedBuilder().WithColor(Color.Green).WithDescription(text).Build(),
                                     components: cb.Build(),
                                     allowedMentions: model.SanitizeMentions,
                                     messageReference: model.MessageReference);

            await Task.WhenAll(yes.RunAsync(msg), no.RunAsync(msg));

            await Task.Delay(30_000);

            await msg.ModifyAsync(mp => mp.Components = new ComponentBuilder().Build());
        }
    }
}