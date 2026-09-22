using Discord;
using Discord.WebSocket;
using MuteReborn.Services;
using NadekoBot.Common;
using NadekoBot.Extensions;
using NadekoBot.Medusa;
using NadekoBot.Modules.Administration.Services;
using NadekoBot.Modules.Gambling.Services;
using NadekoBot.Services;
using NadekoBot.Services.Currency;
using Serilog;
using System.Diagnostics;

namespace MuteReborn;

public sealed partial class MuteReborn : Snek
{
    private readonly MuteService _muteService;
    private readonly DiscordSocketClient _client;
    private readonly GamblingConfigService _gss;
    private readonly ICurrencyService _cs;
    private readonly MuteRebornService _service;
    private readonly ResponseBuilder _response;

    private string CurrencySign => _gss.Data.Currency.Sign;

    public MuteReborn(MuteService MuteService,
        IBotStrings botStrings,
        IMessageSenderService messageSenderService,
        DiscordSocketClient client,
        GamblingConfigService gss,
        ICurrencyService cs,
        MuteRebornService service)
    {
        _muteService = MuteService;
        _client = client;
        _gss = gss;
        _cs = cs;
        _service = service;
        _response = new ResponseBuilder(botStrings, messageSenderService, client);
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

    [cmd(["ToggleMuteReborn", "tmb"])]
    [user_perm(GuildPermission.Administrator)]
    public async Task ToggleMuteReborn(GuildContext ctx)
    {
        var result = _service.ToggleRebornStatus(ctx.Guild);
        await ctx.SendConfirmAsync("死者蘇生已" + (result ? "開啟" : "關閉")).ConfigureAwait(false);
    }

    [cmd(["SettingMuteReborn", "smb"])]
    [user_perm(GuildPermission.Administrator)]
    public async Task SettingMuteReborn(GuildContext ctx, MuteRebornService.SettingType type = MuteRebornService.SettingType.GetAllSetting, int value = 0)
    {
        using var db = Database.DBContext.GetDbContext();
        var guild = db.MuteRebornGuildConfigs.SingleOrDefault((x) => x.GuildId == ctx.Guild.Id);

        if (guild == null)
        {
            await ctx.SendErrorAsync("尚未設定死者蘇生");
            return;
        }

        switch (type)
        {
            case MuteRebornService.SettingType.BuyMuteRebornTicketCost:
                {
                    if (value == 0)
                    {
                        await ctx.SendConfirmAsync($"購買甦生券需花費: {guild.BuyMuteRebornTicketCost}{CurrencySign}");
                        return;
                    }

                    if (value < 1000 || value > 100000)
                    {
                        await ctx.SendErrorAsync("金額僅可限制在 1000 ~ 100000 內");
                        return;
                    }

                    guild.BuyMuteRebornTicketCost = value;
                    await ctx.SendConfirmAsync($"購買甦生券需花費: {guild.BuyMuteRebornTicketCost}{CurrencySign}");
                }
                break;
            case MuteRebornService.SettingType.EachTicketIncreaseMuteTime:
                {
                    if (value == 0)
                    {
                        await ctx.SendConfirmAsync($"每張甦生券可增加: {guild.EachTicketIncreaseMuteTime}分");
                        return;
                    }

                    if (value < 5 || value > 120)
                    {
                        await ctx.SendErrorAsync("時間僅可限制在 5 ~ 120 內");
                        return;
                    }

                    guild.EachTicketIncreaseMuteTime = value;
                    await ctx.SendConfirmAsync($"每張甦生券可增加: {guild.EachTicketIncreaseMuteTime}分" +
                        (guild.EachTicketIncreaseMuteTime > guild.MaxIncreaseMuteTime ? "\n請注意EachTicketIncreaseMuteTime數值比MaxIncreaseMuteTime大，將無法增加勞改時間" : ""));
                }
                break;
            case MuteRebornService.SettingType.EachTicketDecreaseMuteTime:
                {
                    if (value == 0)
                    {
                        await ctx.SendConfirmAsync($"每張甦生券可減少: {guild.EachTicketDecreaseMuteTime}分");
                        return;
                    }

                    if (value < 5 || value > 120)
                    {
                        await ctx.SendErrorAsync("時間僅可限制在 5 ~ 120 內");
                        return;
                    }

                    guild.EachTicketDecreaseMuteTime = value;
                    await ctx.SendConfirmAsync($"每張甦生券可減少: {guild.EachTicketDecreaseMuteTime}分");
                }
                break;
            case MuteRebornService.SettingType.MaxIncreaseMuteTime:
                {
                    if (value == 0)
                    {
                        await ctx.SendConfirmAsync($"最大可增加勞改時間: {guild.MaxIncreaseMuteTime}分");
                        return;
                    }

                    if (value < 10 || value > 360)
                    {
                        await ctx.SendErrorAsync("時間僅可限制在 10 ~ 360 內");
                        return;
                    }

                    guild.MaxIncreaseMuteTime = value;
                    await ctx.SendConfirmAsync($"最大可增加勞改時間: {guild.MaxIncreaseMuteTime}分" +
                        (guild.EachTicketIncreaseMuteTime > guild.MaxIncreaseMuteTime ? "\n請注意 EachTicketIncreaseMuteTime 數值比 MaxIncreaseMuteTime 大，將無法增加勞改時間" : ""));
                }
                break;
            case MuteRebornService.SettingType.GetAllSetting:
                {
                    await ctx.SendConfirmAsync($"購買甦生券需花費: {guild.BuyMuteRebornTicketCost}{CurrencySign}\n" +
                        $"每張甦生券可增加: {guild.EachTicketIncreaseMuteTime}分\n" +
                        $"每張甦生券可減少: {guild.EachTicketDecreaseMuteTime}分\n" +
                        $"最大可增加勞改時間: {guild.MaxIncreaseMuteTime}分" +
                        (guild.EachTicketIncreaseMuteTime > guild.MaxIncreaseMuteTime ? "\n請注意 EachTicketIncreaseMuteTime 數值比 MaxIncreaseMuteTime 大，將無法增加勞改時間" : ""));
                }
                break;
        }

        db.SaveChanges();
    }


    [cmd(["AddMuteRebornTicketNum", "amrtn"])]
    [user_perm(GuildPermission.Administrator)]
    [prio(0)]
    public async Task AddMuteRebornTicketNum(GuildContext ctx, int num, IGuildUser user)
    {
        var result = await _service.AddRebornTicketNumAsync(ctx.Guild, user, num).ConfigureAwait(false);
        await ctx.SendConfirmAsync(result.Item2).ConfigureAwait(false);
    }

    [cmd(["AddMuteRebornTicketNum", "amrtn"])]
    [user_perm(GuildPermission.Administrator)]
    [prio(1)]
    public async Task AddMuteRebornTicketNum(GuildContext ctx, int num, [leftover] string users)
    {
        var list = new List<string>(users.Replace("<@", "").Replace("!", "").Replace(">", "")
            .Split([' '], StringSplitOptions.RemoveEmptyEntries).Distinct());
        var userList = new List<IGuildUser>();

        foreach (var item in list)
        {
            var user = await ctx.Guild.GetUserAsync(ulong.Parse(item));
            if (user != null)
                userList.Add(user);
        }

        var result = await _service.AddRebornTicketNumAsync(ctx.Guild, userList, num).ConfigureAwait(false);
        await ctx.SendConfirmAsync(result).ConfigureAwait(false);
    }

    [cmd(["ListMuteRebornTicketNum", "lmrtn"])]
    public async Task ListMuteRebornTicketNum(GuildContext ctx, int page = 0)
    {
        var resultReborn = _service.ListRebornTicketNum(ctx.Guild);
        if (resultReborn.Count == 0)
        {
            await ctx.SendErrorAsync("無資料，可能尚未設定死者蘇生或是還沒有人持有甦生券").ConfigureAwait(false);
            return;
        }

        // 顯示可用張數（扣除進行中勞改保留的額度），否則會出現「清單有券但 SiNe 說券不足」
        var result = resultReborn
            .OrderByDescending((x) => x.RebornTicketNum)
            .Skip(page * 15)
            .Take(15)
            .Select((x) => $"<@{x.UserId}>: {_service.GetAvailableTicketNum(ctx.Guild, x.UserId)}");

        await ctx.SendConfirmAsync("死者蘇生持有數\n" + string.Join('\n', result));
    }

    [cmd(["ShowMuteRebornTicketNum", "smrtn"])]
    public async Task ShowMuteRebornTicketNum(GuildContext ctx, IUser? user = null)
        => await ShowMuteRebornTicketNum(ctx, user == null ? ctx.User.Id : user.Id);

    [cmd(["ShowMuteRebornTicketNum", "smrtn"])]
    public async Task ShowMuteRebornTicketNum(GuildContext ctx, ulong userId = 0)
    {
        if (userId == 0)
            userId = ctx.User.Id;

        if (userId == 284989733229297664)
        {
            await ctx.SendConfirmAsync($"<@{userId}> 的次數為: 114514").ConfigureAwait(false);
            return;
        }

        var resultReborn = _service.ListRebornTicketNum(ctx.Guild);
        if (resultReborn.Count == 0)
        {
            await ctx.SendErrorAsync("無資料，可能尚未設定死者蘇生或是還沒有人持有甦生券").ConfigureAwait(false);
            return;
        }

        // 顯示可用張數（扣除進行中勞改保留的額度），與 CanReborn／SiNe 的判斷一致
        await ctx.SendConfirmAsync($"<@{userId}> 的次數為: {_service.GetAvailableTicketNum(ctx.Guild, userId)}").ConfigureAwait(false);
    }

    [cmd(["BuyMuteRebornTicket", "bmrt"])]
    public async Task BuyMuteRebornTicket(GuildContext ctx, int num = 1)
    {
        if (num <= 0)
        {
            await ctx.SendErrorAsync("購買數量需大於一張").ConfigureAwait(false);
            return;
        }

        using var db = Database.DBContext.GetDbContext();

        var guildConfig = db.MuteRebornGuildConfigs.SingleOrDefault((x) => x.GuildId == ctx.Guild.Id);
        if (guildConfig == null)
        {
            await ctx.SendErrorAsync("尚未設定死者蘇生");
            return;
        }

        var buyCost = MuteRebornService.CalculateBuyCost(guildConfig.BuyMuteRebornTicketCost, num);

        var currency = await _cs.GetBalanceAsync(ctx.User.Id);
        if (currency < buyCost)
        {
            await ctx.SendErrorAsync($"你的錢錢不夠，加油好嗎\n你還缺 {buyCost - currency}{CurrencySign} 才能購買").ConfigureAwait(false);
            return;
        }

        if (await _cs.RemoveAsync(ctx.User.Id, buyCost, new TxData("MuteRebornTicket", "Buy")))
        {
            var result = await _service.AddRebornTicketNumAsync(ctx.Guild, ctx.User, num);
            if (result.Item1)
            {
                await ctx.SendConfirmAsync(result.Item2).ConfigureAwait(false);
            }
            else
            {
                // 扣款成功但發券失敗時退款，否則使用者會白白損失貨幣。
                // AddAsync 會先更新餘額再寫交易紀錄並通知 tracker，後段失敗時錢可能已經退了，
                // 因此這裡無法斷定「沒退成功」，只能標示狀態待查，不要引導管理員再次補券。
                var refundUnknown = false;
                try
                {
                    await _cs.AddAsync(ctx.User.Id, buyCost, new TxData("MuteRebornTicket", "Refund")).ConfigureAwait(false);
                    Log.Warning("BuyMuteRebornTicket 發券失敗已退款: {GuildId} / {UserId} / {Num} / {Message}",
                        ctx.Guild.Id, ctx.User.Id, num, result.Item2);
                }
                catch (Exception ex)
                {
                    refundUnknown = true;
                    Log.Error(ex, "BuyMuteRebornTicket 退款結果待查: {GuildId} / {UserId} / {Amount}", ctx.Guild.Id, ctx.User.Id, buyCost);
                }

                // 回覆訊息獨立於退款結果，否則退款成功但回覆失敗會被誤記成退款失敗
                try
                {
                    await ctx.SendErrorAsync(refundUnknown
                        ? $"內部錯誤，退款狀態待查\n請向管理員回報並確認餘額（購買數量: {num}）"
                        : "內部錯誤，已將金額退回\n請稍後再試或向管理員回報").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "BuyMuteRebornTicket 回報失敗: {GuildId} / {UserId}", ctx.Guild.Id, ctx.User.Id);
                }
            }
        }
    }

    [cmd]
    public async Task SiNe(GuildContext ctx, IGuildUser user)
    {
        if (!_service.GetRebornStatus(ctx.Guild))
        {
            await ctx.SendErrorAsync("未設定過死者蘇生").ConfigureAwait(false);
            return;
        }

        var key = $"{ctx.Guild.Id}-{user.Id}";
        if (!_service.MutingList.TryAdd(key, 0))
        {
            await ctx.SendErrorAsync("該使用者正在勞改中，需等本次勞改結束").ConfigureAwait(false);
            return;
        }

        try
        {
            // 先保留發起者的一張券：扣券在禁言成功後才執行，
            // 沒有保留的話加時階段可以先把同一張券花掉，結束後再被扣一次。
            // 這裡同時取代了原本的 CanReborn 檢查，才能分辨「沒券」與「券被自己另一場勞改佔住」。
            if (!_service.TryReserveTicket(ctx.Guild, ctx.User.Id))
            {
                await ctx.SendErrorAsync(_service.IsTicketReserved(ctx.Guild, ctx.User.Id)
                    ? "你有一場勞改正在進行中，需等本次勞改結束才能再使用甦生券"
                    : "蘇生券不足阿🈹").ConfigureAwait(false);
                return;
            }

            await SiNeMute(ctx, TimeSpan.FromMinutes(_service.GetRebornSetting(ctx.Guild, MuteRebornService.SettingType.EachTicketIncreaseMuteTime)), user)
                .ConfigureAwait(false);
        }
        finally
        {
            // 不論成功或例外都要移除，否則該使用者會一直收到「需等本次勞改結束」
            _service.MutingList.TryRemove(key, out _);

            // 正常路徑在扣券後就已釋放，這裡只涵蓋還沒走到扣券就失敗的情況（重複釋放是 no-op）
            _service.ReleaseTicket(ctx.Guild, ctx.User.Id);
        }
    }

    /// <summary>
    /// 對指定使用者執行勞改，並在禁言成功後才扣除發起者的一張甦生券。
    /// 先禁言再扣券可避免 Discord API 失敗時已扣券卻沒有實際勞改；
    /// 中途的加時流程會另外扣除加時者自己的券。
    /// </summary>
    private async Task SiNeMute(GuildContext ctx, TimeSpan time, IGuildUser user)
    {
        if (time < TimeSpan.FromMinutes(1) || time > TimeSpan.FromDays(1))
            return;

        // 顯示用文字（實際扣除在禁言成功後才執行）。
        // 此時發起者的券已被自己這場勞改保留，GetAvailableTicketNum 正好就是扣券後的剩餘張數；
        // 硬編碼帳號的扣券是彩蛋、不會改動餘額，固定顯示 114514 以與 AddRebornTicketNumAsync 的回覆一致。
        var remainAfterDeduct = ctx.User.Id == 284989733229297664
            ? 114514
            : _service.GetAvailableTicketNum(ctx.Guild, ctx.User.Id);
        string str = $"<@{ctx.User.Id}> 增加 **-1**，剩餘 **{remainAfterDeduct}** 次蘇生機會\n";

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
                        await ctx.SendConfirmAsync($"{str}" +
                            $"因 EachTicketIncreaseMuteTime({guildIncreaseMuteTime}) 設定數值比 MaxIncreaseMuteTime({guildMaxIncreaseMuteTime}) 大\n" +
                            $"故無法增加勞改時間");
                    }
                    else
                    {
                        var dic = await ctx.GetEmojiCountAsync($"{str}30 秒加時開始，勞改對象: {user.Mention}\n" +
                            $"每個表情可消耗 1 張蘇生券，來增加對方 {guildIncreaseMuteTime} 分鐘的勞改時間\n" +
                            $"最多可增加 {guildMaxIncreaseMuteTime} 分鐘").ConfigureAwait(false);

                        int addTime = 0;
                        string resultText = "";
                        Dictionary<ulong, int> dic2 = new Dictionary<ulong, int>();
                        foreach (var emoteList in dic)
                        {
                            foreach (var item in emoteList.Value)
                            {
                                // 以可用張數判斷，排除進行中勞改已保留的額度
                                var userNum = _service.GetAvailableTicketNum(ctx.Guild, item);
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
                            await ctx.SendConfirmAsync(resultText + $"總共被加了 {addTime} 分鐘").ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"AddMuteTIme: {ctx.Guild.Name}({ctx.Guild.Id}) / {user.Username}({user.Id})");
                await ctx.SendConfirmAsync("錯誤，請向 <@284989733229297664>(konnokai) 詢問");
            }

            if (user.Id == 284989733229297664)
            {
                user = (IGuildUser)ctx.User;
                await ctx.Channel.SendMessageAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Green)
                    .WithImageUrl("https://konnokai.me/nadeko/potter.png")
                    .Build());
            }

            await _muteService.TimedMute(user, ctx.User, time, MuteType.Chat, "主動勞改").ConfigureAwait(false);

            var deduct = await _service.AddRebornTicketNumAsync(ctx.Guild, (IGuildUser)ctx.User, -1).ConfigureAwait(false);
            if (!deduct.Item1)
                Log.Warning("SiNe 扣除甦生券失敗: {GuildId} / {UserId} / {Message}", ctx.Guild.Id, ctx.User.Id, deduct.Item2);

            // 券的帳已結清，馬上釋放保留額度。
            // 留到整段流程結束才釋放的話，後面的蘇生確認（最長 10 分鐘）會讓發起者新買的券一直不能用。
            _service.ReleaseTicket(ctx.Guild, ctx.User.Id);

            await ctx.SendConfirmAsync($"{Format.Bold(user.ToString())} 已被禁言 {time.TotalMinutes} 分鐘").ConfigureAwait(false);

            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                if (_service.CanReborn(ctx.Guild, user))
                {
                    await ctx.SendYesNoConfirmAsync(_response, _client, $"{Format.Bold(user.ToString())} 剩餘 {_service.GetAvailableTicketNum(ctx.Guild, user.Id)} 張甦生券，要使用嗎", async (result) =>
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
                            await ctx.SendConfirmAsync(resultText).ConfigureAwait(false);
                        }
                        else
                        {
                            await ctx.SendConfirmAsync("好ㄅ，勞改愉快").ConfigureAwait(false);
                        }
                    }, user, false).ConfigureAwait(false);
                }

                stopwatch.Stop();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"SiNeMuteReborn: {ctx.Guild.Name}({ctx.Guild.Id}) / {user.Username}({user.Id})");
                await ctx.SendConfirmAsync("錯誤，請向 <@284989733229297664>(konnokai) 詢問");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "");
            await ctx.SendErrorAsync($"錯誤: {ex.Message}");
        }
    }
}
