using System.Text;
using McpGateway.Contracts;
using NadekoBot;
using NadekoBot.Medusa;
using NadekoBot.Modules.Utility.AiAgent;
using Serilog;

namespace McpBridge
{
    public sealed class McpBridgeModule : Snek
    {
        private const string URL_ENV = "MCP_GATEWAY_URL";
        private const string KEY_ENV = "MCP_GATEWAY_KEY";
        private const int MAX_STATUS_LENGTH = 4000;
        private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(60);

        private readonly AiToolRegistry? _registry;
        private readonly DataToolSearchService _search;
        private readonly IHttpClientFactory _http;
        private readonly IBotCredsProvider _creds;

        // 輪詢、指令、卸載都會改工具清單，一次只讓一個做
        private readonly SemaphoreSlim _applyLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();

        private GatewayClient? _gateway;
        private Task? _pollLoop;
        private string? _etag;
        private string[] _owned = [];
        private string? _lastPollError;
        private bool _indexWaited;
        private bool _disposed;

        public McpBridgeModule(
            IAiToolRegistry registry,
            DataToolSearchService search,
            IHttpClientFactory http,
            IBotCredsProvider creds)
        {
            _registry = registry as AiToolRegistry;
            _search = search;
            _http = http;
            _creds = creds;
        }

        public override string Name => "mcpbridge";

        public override ValueTask InitializeAsync()
        {
            IReadOnlyList<string> missing = _registry is null ? ["AiToolRegistry"] : BotInternals.Validate();
            if (missing.Count > 0)
            {
                Log.Error("McpBridge: 這版 bot 找不到需要的內部成員（{Missing}），MCP 工具不會登記", string.Join(", ", missing));
                return default;
            }

            var url = Environment.GetEnvironmentVariable(URL_ENV);
            var key = Environment.GetEnvironmentVariable(KEY_ENV);
            if (string.IsNullOrWhiteSpace(url)
                || string.IsNullOrWhiteSpace(key)
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                Log.Warning("McpBridge: 沒有設定 {UrlEnv} 或 {KeyEnv}，MCP 工具不會登記", URL_ENV, KEY_ENV);
                return default;
            }

            _gateway = new GatewayClient(_http, uri, key);
            _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));
            return default;
        }

        public override async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            if (_pollLoop is not null)
            {
                try
                {
                    await _pollLoop;
                }
                catch (OperationCanceledException)
                {
                }
            }

            await _applyLock.WaitAsync();
            try
            {
                // 指令可能還在等 gateway，之後拿到清單也不能再登記
                _disposed = true;

                // 卸載後 bot 還抓著這些工具的話，medusa 的組件就放不掉，AI 也會呼叫到已卸載的程式碼
                if (_registry is not null && _owned.Length > 0)
                {
                    BotInternals.ReplaceTools(_registry, _owned, []);
                    await BotInternals.RebuildDataToolIndexAsync(_search, waitForStartupBuild: false, CancellationToken.None);
                    _owned = [];
                    _etag = null;
                }
            }
            finally
            {
                _applyLock.Release();
            }

            _cts.Dispose();
        }

        [cmd(["mcpstatus"])]
        [bot_owner_only]
        public async Task McpStatus(AnyContext ctx)
        {
            if (_gateway is null)
            {
                await ctx.SendErrorAsync(NotReadyMessage());
                return;
            }

            try
            {
                var list = await _gateway.GetToolsAsync(_cts.Token);
                var skipped = await ApplyAsync(list, force: false, _cts.Token);
                await ctx.SendConfirmAsync(FormatStatus(list, _owned.Length, skipped));
            }
            catch (Exception ex)
            {
                await ctx.SendErrorAsync($"連不上 MCP gateway：{ex.Message}");
            }
        }

        [cmd(["mcpreload"])]
        [bot_owner_only]
        public async Task McpReload(AnyContext ctx)
        {
            if (_gateway is null)
            {
                await ctx.SendErrorAsync(NotReadyMessage());
                return;
            }

            try
            {
                var list = await _gateway.ReloadAsync(_cts.Token);
                var skipped = await ApplyAsync(list, force: true, _cts.Token);
                await ctx.SendConfirmAsync(FormatStatus(list, _owned.Length, skipped));
            }
            catch (Exception ex)
            {
                await ctx.SendErrorAsync($"MCP gateway 重讀失敗：{ex.Message}");
            }
        }

        [cmd(["mcptools"])]
        [bot_owner_only]
        public async Task McpTools(AnyContext ctx, string server = "")
        {
            if (_gateway is null)
            {
                await ctx.SendErrorAsync(NotReadyMessage());
                return;
            }

            try
            {
                var list = await _gateway.GetToolsAsync(_cts.Token);
                // 先套用最新清單，_owned 才會是 bot 現在真的登記的工具
                await ApplyAsync(list, force: false, _cts.Token);
                await ctx.SendConfirmAsync(FormatTools(list, server, _owned.ToHashSet()));
            }
            catch (Exception ex)
            {
                await ctx.SendErrorAsync($"連不上 MCP gateway：{ex.Message}");
            }
        }

        // 列出一個 server 的工具和權限，方便改 mcp.json 的 toolPerms 前後對照
        public static string FormatTools(ToolListResponse list, string? server, IReadOnlySet<string> registered)
        {
            var status = string.IsNullOrWhiteSpace(server)
                ? null
                : list.Servers.FirstOrDefault(s => s.Name.Equals(server.Trim(), StringComparison.OrdinalIgnoreCase));

            var sb = new StringBuilder();
            if (status is null)
            {
                if (!string.IsNullOrWhiteSpace(server))
                    sb.AppendLine($"找不到 server `{server}`。").AppendLine();

                sb.AppendLine("可以查的 server：");
                foreach (var s in list.Servers)
                    sb.AppendLine($"- `{s.Name}`（{s.ToolCount} 個工具）");
                if (list.Servers.Count == 0)
                    sb.AppendLine("（沒有）");
                return Truncate(sb.ToString());
            }

            sb.AppendLine($"**{status.Name}**" + (status.Connected ? "" : $"：❌ {status.Error}"));
            sb.AppendLine();

            foreach (var tool in list.Tools.Where(t => t.Server == status.Name))
            {
                sb.AppendLine($"- `{tool.OriginalName}` → AI 看到的名稱 `{tool.Name}`");
                sb.AppendLine($"  權限：{McpAiTool.DescribePermission(tool.RequiredPerm)}；{(tool.DataTool ? "資料工具" : "核心工具")}"
                              + (registered.Contains(tool.Name) ? "" : "；⚠️ 沒有登記（跟 bot 內建工具撞名）"));
            }

            if (status.HiddenTools.Count > 0)
                sb.AppendLine().AppendLine($"沒有開放（enabledTools 沒列或撞名）：{string.Join("、", status.HiddenTools.Select(n => $"`{n}`"))}");

            if (status.UnknownToolPerms.Count > 0)
                sb.AppendLine().AppendLine($"⚠️ toolPerms 裡對不到工具的名稱，設定沒有作用：{string.Join("、", status.UnknownToolPerms.Select(n => $"`{n}`"))}");

            return Truncate(sb.ToString());
        }

        // 跟 bot 內建工具撞名的 MCP 工具會被跳過，不能蓋掉內建工具。
        public static IReadOnlyList<ToolInfo> SelectTools(
            IReadOnlyList<ToolInfo> tools,
            IReadOnlySet<string> builtinNames,
            out IReadOnlyList<string> skipped)
        {
            var selected = new List<ToolInfo>(tools.Count);
            var skippedList = new List<string>();

            foreach (var tool in tools)
            {
                if (builtinNames.Contains(tool.Name))
                    skippedList.Add(tool.Name);
                else
                    selected.Add(tool);
            }

            skipped = skippedList;
            return selected;
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var list = await _gateway!.GetToolsAsync(ct);
                    await ApplyAsync(list, force: false, ct);

                    if (_lastPollError is not null)
                        Log.Information("McpBridge: 已重新連上 MCP gateway");
                    _lastPollError = null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // gateway 停著的時候每分鐘都會失敗，同樣的錯誤只記一次
                    if (_lastPollError != ex.Message)
                        Log.Warning("McpBridge: 連不上 MCP gateway：{Error}", ex.Message);
                    _lastPollError = ex.Message;
                }

                try
                {
                    await Task.Delay(_pollInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task<IReadOnlyList<string>> ApplyAsync(ToolListResponse list, bool force, CancellationToken ct)
        {
            await _applyLock.WaitAsync(ct);
            try
            {
                if (_disposed || (!force && list.Etag == _etag))
                    return [];

                var builtin = _registry!.GetAllTools().Select(t => t.Name).Except(_owned).ToHashSet();
                var selected = SelectTools(list.Tools, builtin, out var skipped);
                foreach (var name in skipped)
                    Log.Warning("McpBridge: MCP 工具 {Tool} 跟 bot 內建工具撞名，跳過", name);

                var tools = selected.Select(IAiTool (t) => new McpAiTool(t, _gateway!, _creds)).ToList();
                BotInternals.ReplaceTools(_registry, _owned, tools);
                _owned = tools.Select(t => t.Name).ToArray();
                _etag = list.Etag;

                // 資料工具清單沒變時，bot 的索引會用雜湊判斷並跳過
                await BotInternals.RebuildDataToolIndexAsync(_search, waitForStartupBuild: !_indexWaited, ct);
                _indexWaited = true;

                Log.Information("McpBridge: 已登記 {Count} 個 MCP 工具", tools.Count);
                return skipped;
            }
            finally
            {
                _applyLock.Release();
            }
        }

        private string NotReadyMessage()
            => _registry is null || BotInternals.Validate().Count > 0
                ? "這版 bot 的內部結構跟 McpBridge 對不上，MCP 工具沒有啟用，詳情看 bot 的 log"
                : $"沒有設定 {URL_ENV} 或 {KEY_ENV}，MCP 工具沒有啟用";

        private static string FormatStatus(ToolListResponse list, int registered, IReadOnlyList<string> skipped)
        {
            var sb = new StringBuilder();

            if (list.ConfigError is not null)
                sb.AppendLine($"⚠️ mcp.json 有錯，沿用上一次的設定：{list.ConfigError}").AppendLine();

            if (list.Servers.Count == 0)
                sb.AppendLine("mcp.json 裡沒有啟用中的 server");

            foreach (var s in list.Servers)
            {
                sb.AppendLine(s.Connected
                    ? $"✅ **{s.Name}**：{s.ToolCount} 個工具"
                    : $"❌ **{s.Name}**：{s.Error}");
                if (s.UnknownToolPerms.Count > 0)
                    sb.AppendLine($"　⚠️ toolPerms 對不到工具：{string.Join("、", s.UnknownToolPerms)}");
            }

            sb.AppendLine().AppendLine($"AI agent 目前登記了 {registered} 個 MCP 工具");
            if (skipped.Count > 0)
                sb.AppendLine($"跟內建工具撞名而跳過：{string.Join("、", skipped)}");

            return Truncate(sb.ToString());
        }

        private static string Truncate(string text)
            => text.Length > MAX_STATUS_LENGTH ? text[..MAX_STATUS_LENGTH] + "..." : text;
    }
}
