using Discord;
using Discord.WebSocket;
using NadekoBot.Common;
using NadekoBot.Db;
using NadekoBot.Db.Models;
using NadekoBot.Medusa;
using NadekoBot.Modules.Utility.AiAgent.Prompts;
using NadekoBot.Services;
using Newtonsoft.Json;
using Serilog;

namespace CharacterDesign.Service
{
    [svc(Lifetime.Singleton)]
    public class CharacterDesignService
    {
        private const string SOUL_FILE = "SOUL.md";
        private const string OPERATOR_FILE = "OPERATOR.md";
        private const string BANNER_FILE = "banner.png";

        private DiscordSocketClient _client;
        private ImagesConfig _ic;
        private IHttpClientFactory _httpFactory;
        private IImageCache _images;
        private DbService _db;
        private PromptLibrary _promptLibrary;
        private BotConfigService _botConfig;
        private IReplacementService _replacement;

        // 暫時想不到好的方案讓 Nadeko 可以自動注入
        // 直接在建構式注入會有問題 InvalidOperationException: Unable to resolve service for type while attempting to activate
        internal void Inject(DiscordSocketClient client,
            ImagesConfig ic,
            IHttpClientFactory factory,
            IImageCache images,
            DbService db,
            PromptLibrary promptLibrary,
            BotConfigService botConfig,
            IReplacementService replacement)
        {
            _client = client;
            _ic = ic;
            _httpFactory = factory;
            _images = images;
            _db = db;
            _promptLibrary = promptLibrary;
            _botConfig = botConfig;
            _replacement = replacement;
        }

        public async Task<(bool, CharacterDesign?)> AddCharDesignAsync(string designName, string charAvatar, string playingList)
        {
            if (File.Exists(designName.ToDesignPath() + "design.json"))
                return (false, null);
            if (!Directory.Exists(designName.ToDesignPath()))
                Directory.CreateDirectory(designName.ToDesignPath());

            try
            {
                using (var http = _httpFactory.CreateClient())
                using (var sr = await http.GetAsync(charAvatar, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if (!sr.IsImage())
                        return (false, null);

                    var imgData = await sr.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    await File.WriteAllBytesAsync(designName.ToDesignPath() + "avatar.png", imgData).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "");
                return (false, null);
            }

            try
            {
                var xpByte = await _images.GetXpBackgroundImageAsync();
                if (xpByte != null)
                    await File.WriteAllBytesAsync(designName.ToDesignPath() + "xp_bg.png", xpByte).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "");
                return (false, null);
            }

            var @char = new CharacterDesign();
            var list = playingList.Split(['|']);
            foreach (string item in list)
            {
                var temp = item.Split([':']);
                if (Enum.TryParse(typeof(ActivityType), temp[0], true, out object type))
                    @char.PlayingList.Add(new RotatingPlayingStatus() { Type = (DbActivityType)type, Status = temp[1] });
            }

            try
            {
                await File.WriteAllTextAsync(designName.ToDesignPath() + "design.json", JsonConvert.SerializeObject(@char)).ConfigureAwait(false);
                return (true, @char);
            }
            catch
            {
                return (false, null);
            }
        }

        public (bool, CharacterDesign?) AddCharDesignPlayingStatus(string designName, string playingList)
        {
            if (!File.Exists(designName.ToDesignPath() + "design.json"))
                return (false, null);

            CharacterDesign? @char = JsonConvert.DeserializeObject<CharacterDesign>(File.ReadAllText(designName.ToDesignPath() + "design.json"));

            var list = playingList.Split(['|']);
            foreach (string item in list)
            {
                var playingStatus = item.Split([':']);
                if (Enum.TryParse(typeof(ActivityType), playingStatus[0], true, out object? type))
                    @char.PlayingList.Add(new RotatingPlayingStatus() { Type = (DbActivityType)type, Status = playingStatus[1] });
            }

            try
            {
                File.WriteAllText(designName.ToDesignPath() + "design.json", JsonConvert.SerializeObject(@char));
                return (true, @char);
            }
            catch
            {
                return (false, null);
            }
        }

        public async Task<bool> ChangeCharDesignAsync(string designName)
        {
            if (!File.Exists(designName.ToDesignPath() + "design.json") || !File.Exists(designName.ToDesignPath() + "avatar.png"))
                return false;
            if (_client.CurrentUser.Username == designName)
                return false;

            try
            {
                await _client.CurrentUser.ModifyAsync((x) => x.Username = designName).ConfigureAwait(false);
                await _client.CurrentUser.ModifyAsync((x) => x.Avatar = new Image(designName.ToDesignPath() + "avatar.png")).ConfigureAwait(false);

                var bannerPath = designName.ToDesignPath() + BANNER_FILE;
                if (File.Exists(bannerPath))
                    await _client.CurrentUser.ModifyAsync((x) => x.Banner = new Image(bannerPath)).ConfigureAwait(false);

                if (File.Exists($"{designName.ToDesignPath()}xp_bg.png"))
                    _ic.ModifyConfig((act) => act.Xp.Bg = new Uri($"file://{AppDomain.CurrentDomain.BaseDirectory}{designName.ToDesignPath()}xp_bg.png"));

                CharacterDesign? characterDesign = JsonConvert.DeserializeObject<CharacterDesign>(await File.ReadAllTextAsync(designName.ToDesignPath() + "design.json"));
                if (characterDesign != null && characterDesign.PlayingList.Count > 0)
                {
                    using (var uow = _db.GetDbContext())
                    {
                        var config = uow.RotatingStatus;
                        config.RemoveRange(config.ToArray());

                        foreach (var item in characterDesign.PlayingList.Select((x) => (x.Type, x.Status)))
                            config.Add(new RotatingPlayingStatus() { Type = item.Type, Status = item.Status });
                        await uow.SaveChangesAsync();
                    }

                    // 輪換開關可能被 .setgame / .setstream 關掉，切換人設時重新開啟並立即套用第一筆，不用等下一輪計時器
                    _botConfig.ModifyConfig(bs => bs.RotateStatuses = true);
                    await ApplyPlayingStatusAsync(characterDesign.PlayingList[0]).ConfigureAwait(false);
                }

                // SOUL.md / OPERATOR.md 是 PromptLibrary 的全域人格檔案，切換人設時一併套用該人設保存的版本
                var promptsUpdated = await TryApplyPromptAsync(PromptKind.Soul, designName).ConfigureAwait(false);
                promptsUpdated |= await TryApplyPromptAsync(PromptKind.Operator, designName).ConfigureAwait(false);
                if (promptsUpdated)
                    await _promptLibrary.ReloadAsync().ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "");
                return false;
            }
        }

        /// <summary>
        /// 立即套用單一 PlayingStatus 到 Discord 上線狀態。
        /// 流程與 BotActivityService 的輪換一致：先做替換字串處理，CustomStatus 走 SetCustomStatusAsync，其餘用 SetGameAsync。
        /// </summary>
        private async Task ApplyPlayingStatusAsync(RotatingPlayingStatus status)
        {
            var text = await _replacement.ReplaceAsync(status.Status, new ReplacementContext(client: _client)).ConfigureAwait(false)
                       ?? status.Status;

            if (status.Type == DbActivityType.CustomStatus)
                await _client.SetCustomStatusAsync(text).ConfigureAwait(false);
            else
                await _client.SetGameAsync(text, null, (ActivityType)status.Type).ConfigureAwait(false);
        }

        /// <summary>
        /// 把人設資料夾內對應的 PromptLibrary 檔案寫回全域 prompt。
        /// 檔案不存在時回傳 false 並保留目前內容；寫入失敗只記錄 warning，由呼叫端決定是否重新載入。
        /// </summary>
        private async Task<bool> TryApplyPromptAsync(PromptKind kind, string designName)
        {
            var fileName = kind == PromptKind.Soul ? SOUL_FILE : OPERATOR_FILE;
            var path = designName.ToDesignPath() + fileName;
            if (!File.Exists(path))
                return false;

            var content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            if (_promptLibrary.TryWrite(kind, content, out var error))
                return true;

            Log.Warning("CharacterDesign: failed to apply {FileName} for {DesignName}: {Error}", fileName, designName, error);
            return false;
        }

        public string[]? ListCharDesign()
        {
            if (!Directory.Exists("data/char_design"))
                return null;
            return [.. Directory.GetDirectories("data/char_design", "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy((x) => x)];
        }

        public async Task<(bool, CharacterDesign?)> SaveCharDesignAsync(string designName)
        {
            //if (File.Exists(designName.ToDesignPath() + "design.json")) return (false, null);
            if (!Directory.Exists(designName.ToDesignPath()))
                Directory.CreateDirectory(designName.ToDesignPath());

            try
            {
                using (var http = _httpFactory.CreateClient())
                using (var sr = await http.GetAsync(_client.CurrentUser.GetAvatarUrl(), HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if (!sr.IsImage())
                        return (false, null);

                    var imgData = await sr.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    await File.WriteAllBytesAsync(designName.ToDesignPath() + "avatar.png", imgData).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "");
                return (false, null);
            }

            // banner 是選配，抓不到或不存在都不影響人設保存
            try
            {
                var bannerPath = designName.ToDesignPath() + BANNER_FILE;
                var bannerId = _client.CurrentUser.BannerId;
                if (string.IsNullOrEmpty(bannerId))
                {
                    if (File.Exists(bannerPath))
                        File.Delete(bannerPath);
                }
                else
                {
                    var bannerUrl = CDN.GetUserBannerUrl(_client.CurrentUser.Id, bannerId, 1024, ImageFormat.Auto);
                    using var http = _httpFactory.CreateClient();
                    using var sr = await http.GetAsync(bannerUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (sr.IsImage())
                    {
                        var imgData = await sr.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        await File.WriteAllBytesAsync(bannerPath, imgData).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "CharacterDesign: failed to save banner for {DesignName}", designName);
            }

            try
            {
                var xpByte = await _images.GetXpBackgroundImageAsync();
                if (xpByte != null)
                    await File.WriteAllBytesAsync(designName.ToDesignPath() + "xp_bg.png", xpByte).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "");
                return (false, null);
            }

            try
            {
                using (var uow = _db.GetDbContext())
                {
                    var @char = new CharacterDesign() { PlayingList = uow.RotatingStatus.ToList() };
                    await File.WriteAllTextAsync(designName.ToDesignPath() + "design.json", JsonConvert.SerializeObject(@char)).ConfigureAwait(false);
                    // 一併保存當下生效中的 SOUL.md / OPERATOR.md，之後切換回此人設時可套用
                    await File.WriteAllTextAsync(designName.ToDesignPath() + SOUL_FILE, _promptLibrary.GetSoul()).ConfigureAwait(false);
                    await File.WriteAllTextAsync(designName.ToDesignPath() + OPERATOR_FILE, _promptLibrary.GetOperatorDoc()).ConfigureAwait(false);
                    return (true, @char);
                }
            }
            catch
            {
                return (false, null);
            }
        }

        public bool DeleteCharDesign(string designName)
        {
            try
            {
                Directory.Delete($"data/char_design/{designName}", true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public class CharacterDesign
        {
            public List<RotatingPlayingStatus> PlayingList { get; set; } = new();
        }
    }
}
