#nullable disable

using Discord;
using MuteReborn.Bet.HSR;
using Nadeko.Snake;
using NadekoBot;
using NadekoBot.Extensions;
using System.Text.Json;

namespace MuteReborn;
public partial class MuteReborn
{
    [cmd(new[] { "HSRStartBetRelic", "hsrbs" })]
    public async Task HSRStartBetRelic(GuildContext ctx, [leftover] string text = "")
    {
        if (!ctx.Message.Attachments.Any())
        {
            await ctx.SendErrorAsync("需要包含圖片");
            return;
        }

        bool isCancel = false;
        var hSRBetData = _service.RunningHSRBetList.FirstOrDefault((x) => x.GamblingUser.Id == ctx.User.Id);
        if (hSRBetData != null)
        {
            await ctx.SendYesNoConfirmAsync(ctx.Embed(), _client, "你有賭局尚未結束，是否取消?", (act) =>
            {
                if (act)
                {
                    _service.RunningHSRBetList.Remove(hSRBetData);
                }
                else
                {
                    ctx.SendErrorAsync($"{ctx.User} 請上傳詞條截圖並同時輸入 `~brend` 以結束該賭局");
                    isCancel = true;
                }
            });
        }

        if (isCancel)
            return;

        string betGuid = "bet_" + Guid.NewGuid().ToString().Replace("-", "");

        var message = await ctx.Channel.SendMessageAsync("開賭啦",
               embed: ctx.Embed()
                   .WithColor(EmbedColor.Ok)
                   .WithTitle(ctx.User.ToString())
                   .WithDescription("附加訊息: " + (string.IsNullOrEmpty(text) ? "無" : text))
                   .WithImageUrl(ctx.Message.Attachments.First().Url)
                   .WithFooter("請在合成結束後截圖詞條，上傳截圖同時輸入 `~hsrbe` 以供管理員檢查")
                   .Build(),
               components: BuildAffixSelectMenu(betGuid, true));

        var message2 = await message.ReplyAsync(
             embed: ctx.Embed()
                 .WithColor(EmbedColor.Ok)
                 .WithTitle("詞條選擇清單")
                 .WithDescription("無")
                 .Build());

        _service.RunningHSRBetList.Add(new HSRBetData(ctx.User, message, message2, text, betGuid));
    }

    [cmd(new[] { "HSREndBetRelic", "hsrbe" })]
    public async Task HSREndBetRelic(GuildContext ctx)
    {
        var hSRBetData = _service.RunningHSRBetList.FirstOrDefault((x) => x.GamblingUser.Id == ctx.User.Id);
        if (hSRBetData == null)
        {
            await ctx.SendErrorAsync($"{ctx.User} 未開始賭局，請使用 `~hsrbs` 新增賭局");
            return;
        }

        bool isCancel = false;
        if (!hSRBetData.SelectedRankDic.Any())
        {
            await ctx.SendYesNoConfirmAsync(ctx.Embed(), _client, $"{ctx.User} 你的賭局尚無人選則，是否取消本賭局?", async (act) =>
            {
                if (act)
                {
                    await DisableComponentAsync(hSRBetData.GamblingMessage);
                    _service.RunningHSRBetList.Remove(hSRBetData);
                }
                isCancel = true;
            });
        }

        if (isCancel)
            return;

        if (!ctx.Message.Attachments.Any())
        {
            await ctx.SendErrorAsync("需要包含合成後的詞條截圖");
            return;
        }

        using var httpClient = _httpClientFactory.CreateClient();

        var bytes = await httpClient.GetByteArrayAsync(ctx.Message.Attachments.First().Url);
        using var imageStream = new MemoryStream(bytes);
        hSRBetData.SelectedRankDic.TryAdd(hSRBetData.GamblingUser.Id, "banker");
        using var stringStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hSRBetData.SelectedRankDic)));

        await ctx.Channel.SendFilesAsync(
            attachments: new List<FileAttachment>() { new FileAttachment(imageStream, "rank.jpg"), new FileAttachment(stringStream, "rank.json", isSpoiler: true) },
            embed: ctx.Embed()
                .WithColor(EmbedColor.Ok)
                .WithTitle(hSRBetData.GamblingUser.ToString() + " 的詞條賭局選擇清單")
                .WithDescription($"附加訊息: {hSRBetData.AddMessage}\n\n" +
                    $"詞條選擇清單:\n" +
                    $"{string.Join('\n', hSRBetData.SelectedRankDic.Take(hSRBetData.SelectedRankDic.Count - 1).Select((x) => $"<@{x.Key}>: {_service.SubAffixList[x.Value]}"))}\n\n" +
                    Format.Url($"賭局連結", hSRBetData.GamblingMessage.GetJumpUrl()))
                .WithImageUrl("attachment://rank.jpg")
                .Build(),
            components: BuildAffixSelectMenu("betend"));

        await DisableComponentAsync(hSRBetData.GamblingMessage);

        _service.RunningHSRBetList.Remove(hSRBetData);

        await hSRBetData.GamblingMessage.ReplyAsync(embed: ctx.Embed().WithColor(EmbedColor.Ok).WithDescription("已結束並封存本賭局").Build());
    }

    private async Task DisableComponentAsync(IUserMessage userMessage)
    {
        await userMessage.ModifyAsync((act) =>
        {
            act.Components = new Optional<MessageComponent>(new ComponentBuilder().Build());
        });
    }

    private MessageComponent BuildAffixSelectMenu(string customId = "dummy", bool canCancel = false)
    {
        var selectMenuOptionBuilders = new List<SelectMenuOptionBuilder>();
        foreach (var item in _service.SubAffixList)
        {
            selectMenuOptionBuilders.Add(new SelectMenuOptionBuilder(item.Value, item.Key));
        }

        if (canCancel)
            selectMenuOptionBuilders.Add(new SelectMenuOptionBuilder("取消選擇", "cancel"));

        return new ComponentBuilder()
            .WithSelectMenu(customId, selectMenuOptionBuilders, "請選擇詞條").Build();
    }
}