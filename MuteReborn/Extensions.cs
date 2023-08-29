using Discord;
using Discord.Net;
using Discord.WebSocket;
using Nadeko.Snake;
using NadekoBot;
using Serilog;

namespace MuteReborn
{
    public static class Extensions
    {
        private const string BUTTON_YES = "BUTTON_YES";
        private const string BUTTON_NO = "BUTTON_NO";

        private static readonly Emoji _okEmoji = new Emoji("✅");
        private static readonly Emoji _errorEmoji = new Emoji("❌");

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

        public static async Task<Dictionary<IEmote, List<ulong>>> GetEmojiCountAsync(this AnyContext ctx, string text)
        {
            var dic = new Dictionary<IEmote, List<ulong>>();
            var msg = await ctx.Channel.SendConfirmAsync(ctx, text).ConfigureAwait(false);
            await Task.Delay(30000).ConfigureAwait(false);

            try
            {
                msg = await ctx.Channel.GetMessageAsync(msg.Id).ConfigureAwait(false) as IUserMessage;

                foreach (var item in msg.Reactions)
                {
                    var list = await msg.GetReactionUsersAsync(item.Key, 30).FlattenAsync().ConfigureAwait(false);
                    foreach (var item2 in list)
                    {
                        if (dic.ContainsKey(item.Key))
                            dic[item.Key].Add(item2.Id);
                        else
                            dic.Add(item.Key, new List<ulong>() { item2.Id });
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
                await ctx.Channel.SendErrorAsync(ctx, "原訊息已刪除導致無法統計，略過加時");
            }

            return dic;
        }
    }
}
