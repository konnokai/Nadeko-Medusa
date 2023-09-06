using Discord;
using Discord.WebSocket;
using MuteReborn.Services;
using Nadeko.Snake;
using NadekoBot;
using NadekoBot.Extensions;
using NadekoBot.Modules.Administration.Services;
using NadekoBot.Modules.Gambling.Services;
using NadekoBot.Services;
using NadekoBot.Services.Currency;
using Serilog;
using System.Diagnostics;
using System.Text.Json;

namespace MuteReborn;

public sealed partial class MuteReborn : Snek
{
    private readonly MuteService _muteService;
    private readonly DiscordSocketClient _client;
    private readonly GamblingConfigService _gss;
    private readonly ICurrencyService _cs;
    private readonly MuteRebornService _service;
    private readonly IHttpClientFactory _httpClientFactory;

    private string CurrencySign => _gss.Data.Currency.Sign;

    public MuteReborn(MuteService MuteService, DiscordSocketClient client, GamblingConfigService gss, ICurrencyService cs, MuteRebornService service, IHttpClientFactory httpClientFactory)
    {
        _muteService = MuteService;
        _client = client;
        _gss = gss;
        _cs = cs;
        _service = service;
        _httpClientFactory = httpClientFactory;

        _client.SelectMenuExecuted += async (component) =>
        {
            if (component.HasResponded)
                return;

            if (component.Data.CustomId.StartsWith("bet_"))
            {
                try
                {
                    var hSRBetData = _service.RunningHSRBetList.FirstOrDefault((x) => x.BetGuid == component.Data.CustomId);
                    if (hSRBetData != null && hSRBetData.GamblingMessage.CreatedAt.AddMinutes(5) >= DateTimeOffset.UtcNow)
                    {
                        if (component.User.Id == hSRBetData.GamblingUser.Id)
                        {
                            await component.RespondAsync($"你不可選擇自己的賭局", ephemeral: true);
                            return;
                        }

                        string selectAffix = component.Data.Values.First();
                        if (hSRBetData.SelectedRankDic.ContainsKey(component.User.Id))
                        {
                            hSRBetData.SelectedRankDic[component.User.Id] = selectAffix;
                            await component.RespondAsync($"更改選擇: {_service.SubAffixList[selectAffix]}", ephemeral: true);
                        }
                        else
                        {
                            hSRBetData.SelectedRankDic.Add(component.User.Id, selectAffix);
                            await component.RespondAsync($"選擇: {_service.SubAffixList[selectAffix]}", ephemeral: true);
                        }

                        await hSRBetData.SelectRankMessage.ModifyAsync((act) =>
                            act.Embed = new EmbedBuilder()
                                .WithColor(Color.Green)
                                .WithTitle("詞條選擇清單")
                                .WithDescription(string.Join('\n', hSRBetData.SelectedRankDic.Select((x) => $"<@{x.Key}>: {_service.SubAffixList[x.Value]}")))
                                .Build()
                            );
                    }
                    else
                    {
                        await component.RespondAsync($"該賭局已結束或取消", ephemeral: true);
                        await DisableComponentAsync(component.Message);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"bet_: {component.User} - {component.Data.CustomId}. {component.Data.Value}");
                }

            }
            else if (component.Data.CustomId == "betend")
            {
                if (component.User is SocketGuildUser user && component.Channel is SocketGuildChannel channel)
                {
                    var guild = channel.Guild;
                    if (user.GetRoles().Any((x) => x.Permissions.Administrator) || guild.OwnerId == user.Id)
                    {
                        var jsonFile = component.Message.Attachments.FirstOrDefault((x) => x.Filename == "SPOILER_rank.json");
                        if (jsonFile == null)
                        {
                            await component.RespondAsync("缺少 `SPOILER_rank.json` 檔案", ephemeral: true);
                            await DisableComponentAsync(component.Message);
                            return;
                        }

                        await component.DeferAsync(false);

                        using var httpClient = _httpClientFactory.CreateClient();
                        var jsonText = await httpClient.GetStringAsync(jsonFile.Url);
                        var json = JsonSerializer.Deserialize<Dictionary<ulong, string>>(jsonText);

                        string selectAffix = component.Data.Values.First();
                        string result = "";
                        foreach (var item in json)
                        {
                            var addResult = await _service.AddRebornTicketNumAsync(guild, item.Key, item.Value == selectAffix ? 3 : item.Value == "banker" ? 1 : -1);
                            result += addResult.Item2;

                            if (!addResult.Item1)
                                break;
                        }

                        await component.FollowupAsync(embed: new EmbedBuilder().WithColor(Color.Green).WithDescription(result).Build());
                        await DisableComponentAsync(component.Message);
                    }
                    else
                    {
                        await component.RespondAsync("你無權使用本功能", ephemeral: true);
                        return;
                    }
                }
            }
        };
    }

    public override ValueTask InitializeAsync()
    {
        using (var db = Database.DBContext.GetDbContext())
            db.Database.EnsureCreated();

        Log.Information("MuteReborn Medusa Start");

        return base.InitializeAsync();
    }

    public override ValueTask DisposeAsync()
    {
        Log.Information("MuteReborn Medusa Disposed");

        return base.DisposeAsync();
    }

    [cmd(new[] { "ToggleMuteReborn", "tmb" })]
    [user_perm(GuildPermission.Administrator)]
    public async Task ToggleMuteReborn(GuildContext ctx)
    {
        var result = _service.ToggleRebornStatus(ctx.Guild);
        await ctx.Channel.SendConfirmAsync(ctx, "死者蘇生已" + (result ? "開啟" : "關閉")).ConfigureAwait(false);
    }

    [cmd(new[] { "SettingMuteReborn", "smb" })]
    [user_perm(GuildPermission.Administrator)]
    public async Task SettingMuteReborn(GuildContext ctx, MuteRebornService.SettingType type = MuteRebornService.SettingType.GetAllSetting, int value = 0)
    {
        using var db = Database.DBContext.GetDbContext();
        var guild = db.GuildConfigs.SingleOrDefault((x) => x.GuildId == ctx.Guild.Id);

        if (guild == null)
        {
            await ctx.Channel.SendErrorAsync(ctx, "尚未設定死者蘇生");
            return;
        }

        switch (type)
        {
            case MuteRebornService.SettingType.BuyMuteRebornTicketCost:
                {
                    if (value == 0)
                    {
                        await ctx.Channel.SendConfirmAsync(ctx, $"購買甦生券需花費: {guild.BuyMuteRebornTicketCost}{CurrencySign}");
                        return;
                    }

                    if (value < 1000 || value > 100000)
                    {
                        await ctx.Channel.SendErrorAsync(ctx, "金額僅可限制在1000~100000內");
                        return;
                    }

                    guild.BuyMuteRebornTicketCost = value;
                    await ctx.Channel.SendConfirmAsync(ctx, $"購買甦生券需花費: {guild.BuyMuteRebornTicketCost}{CurrencySign}");
                }
                break;
            case MuteRebornService.SettingType.EachTicketIncreaseMuteTime:
                {
                    if (value == 0)
                    {
                        await ctx.Channel.SendConfirmAsync(ctx, $"每張甦生券可增加: {guild.EachTicketIncreaseMuteTime}分");
                        return;
                    }

                    if (value < 5 || value > 120)
                    {
                        await ctx.Channel.SendErrorAsync(ctx, "時間僅可限制在5~120內");
                        return;
                    }

                    guild.EachTicketIncreaseMuteTime = value;
                    await ctx.Channel.SendConfirmAsync(ctx, $"每張甦生券可增加: {guild.EachTicketIncreaseMuteTime}分" +
                        (guild.EachTicketIncreaseMuteTime > guild.MaxIncreaseMuteTime ? "\n請注意EachTicketIncreaseMuteTime數值比MaxIncreaseMuteTime大，將無法增加勞改時間" : ""));
                }
                break;
            case MuteRebornService.SettingType.EachTicketDecreaseMuteTime:
                {
                    if (value == 0)
                    {
                        await ctx.Channel.SendConfirmAsync(ctx, $"每張甦生券可減少: {guild.EachTicketDecreaseMuteTime}分");
                        return;
                    }

                    if (value < 5 || value > 120)
                    {
                        await ctx.Channel.SendErrorAsync(ctx, "時間僅可限制在5~120內");
                        return;
                    }

                    guild.EachTicketDecreaseMuteTime = value;
                    await ctx.Channel.SendConfirmAsync(ctx, $"每張甦生券可減少: {guild.EachTicketDecreaseMuteTime}分");
                }
                break;
            case MuteRebornService.SettingType.MaxIncreaseMuteTime:
                {
                    if (value == 0)
                    {
                        await ctx.Channel.SendConfirmAsync(ctx, $"最大可增加勞改時間: {guild.MaxIncreaseMuteTime}分");
                        return;
                    }

                    if (value < 10 || value > 360)
                    {
                        await ctx.Channel.SendErrorAsync(ctx, "時間僅可限制在10~360內");
                        return;
                    }

                    guild.MaxIncreaseMuteTime = value;
                    await ctx.Channel.SendConfirmAsync(ctx, $"最大可增加勞改時間: {guild.MaxIncreaseMuteTime}分" +
                        (guild.EachTicketIncreaseMuteTime > guild.MaxIncreaseMuteTime ? "\n請注意EachTicketIncreaseMuteTime數值比MaxIncreaseMuteTime大，將無法增加勞改時間" : ""));
                }
                break;
            case MuteRebornService.SettingType.GetAllSetting:
                {
                    await ctx.Channel.SendConfirmAsync(ctx, $"購買甦生券需花費: {guild.BuyMuteRebornTicketCost}{CurrencySign}\n" +
                        $"每張甦生券可增加: {guild.EachTicketIncreaseMuteTime}分\n" +
                        $"每張甦生券可減少: {guild.EachTicketDecreaseMuteTime}分\n" +
                        $"最大可增加勞改時間: {guild.MaxIncreaseMuteTime}分" +
                        (guild.EachTicketIncreaseMuteTime > guild.MaxIncreaseMuteTime ? "\n請注意EachTicketIncreaseMuteTime數值比MaxIncreaseMuteTime大，將無法增加勞改時間" : ""));
                }
                break;
        }

        db.SaveChanges();
    }


    [cmd(new[] { "AddMuteRebornTicketNum", "amrtn" })]
    [user_perm(GuildPermission.Administrator)]
    [prio(0)]
    public async Task AddMuteRebornTicketNum(GuildContext ctx, int num, IGuildUser user)
    {
        var result = await _service.AddRebornTicketNumAsync(ctx.Guild, user, num).ConfigureAwait(false);
        await ctx.Channel.SendConfirmAsync(ctx, result.Item2).ConfigureAwait(false);
    }

    [cmd(new[] { "AddMuteRebornTicketNum", "amrtn" })]
    [user_perm(GuildPermission.Administrator)]
    [prio(1)]
    public async Task AddMuteRebornTicketNum(GuildContext ctx, int num, [leftover] string users)
    {
        var list = new List<string>(users.Replace("<@", "").Replace("!", "").Replace(">", "")
            .Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Distinct());
        var userList = new List<IGuildUser>();

        foreach (var item in list)
        {
            var user = await ctx.Guild.GetUserAsync(ulong.Parse(item));
            if (user != null)
                userList.Add(user);
        }

        var result = await _service.AddRebornTicketNumAsync(ctx.Guild, userList, num).ConfigureAwait(false);
        await ctx.Channel.SendConfirmAsync(ctx, result).ConfigureAwait(false);
    }

    [cmd(new[] { "ListMuteRebornTicketNum", "lmrtn" })]
    public async Task ListMuteRebornTicketNum(GuildContext ctx, int page = 0)
    {
        var resultReborn = _service.ListRebornTicketNum(ctx.Guild);
        if (resultReborn.Count == 0)
        {
            await ctx.Channel.SendErrorAsync(ctx, "無資料，可能尚未設定死者蘇生或是還沒有人持有甦生券").ConfigureAwait(false);
            return;
        }

        var result = resultReborn.OrderByDescending((x) => x.RebornTicketNum).Select((x) => $"<@{x.UserId}>: {x.RebornTicketNum}");
        await ctx.SendConfirmAsync("死者蘇生持有數\n" +
             string.Join('\n', result.Skip(page * 15).Take(15)));
    }

    [cmd(new[] { "ShowMuteRebornTicketNum", "smrtn" })]
    public async Task ShowMuteRebornTicketNum(GuildContext ctx, IUser? user = null)
        => await ShowMuteRebornTicketNum(ctx, user == null ? ctx.User.Id : user.Id);

    [cmd(new[] { "ShowMuteRebornTicketNum", "smrtn" })]
    public async Task ShowMuteRebornTicketNum(GuildContext ctx, ulong userId = 0)
    {
        if (userId == 0)
            userId = ctx.User.Id;

        var resultReborn = _service.ListRebornTicketNum(ctx.Guild);
        if (resultReborn.Count == 0)
        {
            await ctx.Channel.SendErrorAsync(ctx, "無資料，可能尚未設定死者蘇生或是還沒有人持有甦生券").ConfigureAwait(false);
            return;
        }

        var muteReborn = resultReborn.FirstOrDefault((x) => x.UserId == userId);
        if (muteReborn == null)
        {
            await ctx.Channel.SendConfirmAsync(ctx, $"<@{userId}> 的次數為: 0").ConfigureAwait(false);
            return;
        }

        await ctx.Channel.SendConfirmAsync(ctx, $"<@{userId}> 的次數為: {muteReborn.RebornTicketNum}").ConfigureAwait(false);
    }

    [cmd(new[] { "BuyMuteRebornTicket", "bmrt" })]
    public async Task BuyMuteRebornTicket(GuildContext ctx, int num = 1)
    {
        if (num <= 0)
        {
            await ctx.Channel.SendErrorAsync(ctx, "購買數量需大於一張").ConfigureAwait(false);
            return;
        }

        using var db = Database.DBContext.GetDbContext();

        var guildConfig = db.GuildConfigs.SingleOrDefault((x) => x.GuildId == ctx.Guild.Id);
        if (guildConfig == null)
        {
            await ctx.SendErrorAsync("尚未設定死者蘇生");
            return;
        }

        var currency = await _cs.GetBalanceAsync(ctx.User.Id);
        var buyCost = guildConfig.BuyMuteRebornTicketCost * num;
        if (currency < buyCost)
        {
            await ctx.Channel.SendErrorAsync(ctx, $"你的錢錢不夠，加油好嗎\n你還缺 {buyCost - currency}{CurrencySign} 才能購買").ConfigureAwait(false);
            return;
        }

        if (await _cs.RemoveAsync(ctx.User.Id, buyCost, new TxData("MuteRebornTicket", "Buy")))
        {
            var result = await _service.AddRebornTicketNumAsync(ctx.Guild, ctx.User, num);
            if (result.Item1)
                await ctx.Channel.SendConfirmAsync(ctx, result.Item2).ConfigureAwait(false);
            else
                await ctx.Channel.SendErrorAsync(ctx, $"內部錯誤，已扣除金額但無法購買\n請向管理員要求直接增加次數: {num}").ConfigureAwait(false);

            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    [cmd]
    public async Task SiNe(GuildContext ctx, IGuildUser user)
    {
        if (!_service.GetRebornStatus(ctx.Guild))
        {
            await ctx.Channel.SendErrorAsync(ctx, "未設定過死者蘇生").ConfigureAwait(false);
            return;
        }

        if (!_service.CanReborn(ctx.Guild, ctx.User))
        {
            await ctx.Channel.SendErrorAsync(ctx, "蘇生券不足阿🈹").ConfigureAwait(false);
            return;
        }

        if (!_service.MutingList.Add($"{ctx.Guild.Id}-{user.Id}"))
        {
            await ctx.Channel.SendErrorAsync(ctx, "正在勞改當中").ConfigureAwait(false);
            return;
        }

        var muteReborn = await _service.AddRebornTicketNumAsync(ctx.Guild, (IGuildUser)ctx.User, -1);
        if (muteReborn.Item1)
            await SiNeMute(ctx, TimeSpan.FromMinutes(_service.GetRebornSetting(ctx.Guild, MuteRebornService.SettingType.EachTicketIncreaseMuteTime)), user, muteReborn.Item2);
        else
            await ctx.Channel.SendErrorAsync(ctx, muteReborn.Item2).ConfigureAwait(false);
    }

    private async Task SiNeMute(GuildContext ctx, TimeSpan time, IGuildUser user, string str)
    {
        if (time < TimeSpan.FromMinutes(1) || time > TimeSpan.FromDays(1))
            return;

        try
        {
            try
            {
                if (_service.GetRebornStatus(ctx.Guild))
                {
                    int guildIncreaseMuteTime = _service.GetRebornSetting(ctx.Guild, MuteRebornService.SettingType.EachTicketIncreaseMuteTime);
                    int guildMaxIncreaseMuteTime = _service.GetRebornSetting(ctx.Guild, MuteRebornService.SettingType.MaxIncreaseMuteTime);
                    if (guildIncreaseMuteTime > guildMaxIncreaseMuteTime)
                    {
                        await ctx.Channel.SendConfirmAsync(ctx, $"{str}" +
                            $"因EachTicketIncreaseMuteTime({guildIncreaseMuteTime})設定數值比MaxIncreaseMuteTime({guildMaxIncreaseMuteTime})大\n" +
                            $"故無法增加勞改時間");
                    }
                    else
                    {
                        var dic = await ctx.GetEmojiCountAsync($"{str}30秒加時開始，勞改對象: {user.Mention}\n" +
                            $"每個表情可消耗一張蘇生券，來增加對方 {guildIncreaseMuteTime} 分鐘的勞改時間\n" +
                            $"最多可增加 {guildMaxIncreaseMuteTime} 分鐘").ConfigureAwait(false);

                        int addTime = 0;
                        string resultText = "";
                        Dictionary<ulong, int> dic2 = new Dictionary<ulong, int>();
                        foreach (var emoteList in dic)
                        {
                            foreach (var item in emoteList.Value)
                            {
                                var userNum = _service.GetRebornTicketNum(ctx.Guild, item);
                                if (userNum <= 0)
                                    continue;

                                if (dic2.ContainsKey(item) && userNum == dic2[item])
                                    continue;

                                if (dic2.ContainsKey(item))
                                    dic2[item]++;
                                else
                                    dic2.Add(item, 1);

                                if (addTime + guildIncreaseMuteTime >= guildMaxIncreaseMuteTime)
                                {
                                    addTime = guildMaxIncreaseMuteTime;
                                    break;
                                }

                                addTime += guildIncreaseMuteTime;
                            }

                            if (addTime >= guildMaxIncreaseMuteTime)
                                break;
                        }

                        foreach (var item in dic2)
                        {
                            var addResult = await _service.AddRebornTicketNumAsync(ctx.Guild, item.Key, -item.Value);
                            if (addResult.Item1)
                                resultText += addResult.Item2;
                            else
                                addTime -= guildIncreaseMuteTime;
                        }

                        if (addTime > 0)
                        {
                            time += TimeSpan.FromMinutes(addTime);
                            await ctx.Channel.SendConfirmAsync(ctx, resultText + $"總共被加了 {addTime} 分鐘").ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"AddMuteTIme: {ctx.Guild.Name}({ctx.Guild.Id}) / {user.Username}({user.Id})");
                await ctx.Channel.SendConfirmAsync(ctx, "錯誤，請向 <@284989733229297664>(孤之界#1121) 詢問");
            }

            if (user.Id == 284989733229297664)
            {
                _service.MutingList.Remove($"{ctx.Guild.Id}-{user.Id}");
                user = (IGuildUser)ctx.User;
                await ctx.Channel.SendMessageAsync(embed: ctx.Embed().WithColor(EmbedColor.Ok).WithImageUrl("https://konnokai.me/nadeko/potter.png").Build());
            }

            await _muteService.TimedMute(user, ctx.User, time, MuteType.Chat, "主動勞改").ConfigureAwait(false);
            await ctx.SendConfirmAsync($"{Format.Bold(user.ToString())} 已被禁言 {time.TotalMinutes} 分鐘").ConfigureAwait(false);

            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                if (_service.CanReborn(ctx.Guild, user))
                {
                    if (_muteService.UnTimers.TryGetValue(ctx.Guild.Id, out var keyValuePairs) && keyValuePairs.TryGetValue((user.Id, MuteService.TimerType.Mute), out var timer))
                    {
                        await ctx.SendYesNoConfirmAsync(ctx.Embed(), _client, $"{Format.Bold(user.ToString())} 剩餘 {_service.GetRebornTicketNum(ctx.Guild, user.Id)} 張甦生券，要使用嗎", async (result) =>
                        {
                            if (result)
                            {
                                int guildDecreaseMuteTime = _service.GetRebornSetting(ctx.Guild, MuteRebornService.SettingType.EachTicketDecreaseMuteTime);
                                var temp = time.Add(TimeSpan.FromMinutes(-guildDecreaseMuteTime)).Subtract(stopwatch.Elapsed);
                                string resultText = "";
                                if (temp > TimeSpan.FromSeconds(30))
                                {
                                    await _muteService.TimedMute(user, ctx.User, temp, reason: $"死者蘇生扣除 {guildDecreaseMuteTime} 分鐘").ConfigureAwait(false);
                                    resultText = $"已扣除 {guildDecreaseMuteTime} 分鐘\n你還需要勞改 {temp:hh\\時mm\\分ss\\秒}\n";
                                }
                                else
                                {
                                    await _muteService.UnmuteUser(ctx.Guild.Id, user.Id, ctx.User, reason: "死者蘇生");
                                    resultText = "歡迎回來\n";
                                }

                                resultText += (await _service.AddRebornTicketNumAsync(ctx.Guild, user, -1).ConfigureAwait(false)).Item2;
                                await ctx.Channel.SendConfirmAsync(ctx, resultText).ConfigureAwait(false);
                            }
                            else
                            {
                                await ctx.Channel.SendConfirmAsync(ctx, "好ㄅ，勞改愉快").ConfigureAwait(false);
                            }
                        }, user, false).ConfigureAwait(false);
                    }
                }

                stopwatch.Stop();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"SiNeMuteReborn: {ctx.Guild.Name}({ctx.Guild.Id}) / {user.Username}({user.Id})");
                await ctx.Channel.SendConfirmAsync(ctx, "錯誤，請向 <@284989733229297664>(孤之界#1121) 詢問");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "");
            await ctx.Channel.SendErrorAsync(ctx, $"錯誤: {ex.Message}");
        }

        _service.MutingList.Remove($"{ctx.Guild.Id}-{user.Id}");
    }
}