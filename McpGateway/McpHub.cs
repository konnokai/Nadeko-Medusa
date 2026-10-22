using System.Text;
using System.Text.Json;
using McpGateway.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpGateway
{
    // 管理所有 MCP server 的連線。重讀設定一次只做一個，工具呼叫讀的是不可變的快照，不用等鎖
    public sealed class McpHub : IAsyncDisposable
    {
        private const int MAX_TEXT_LENGTH = 20_000;
        // npx 第一次啟動要下載套件，給長一點
        private static readonly TimeSpan _connectTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan _discoverTimeout = TimeSpan.FromSeconds(60);

        private readonly string _configPath;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<McpHub> _log;
        private readonly SemaphoreSlim _reloadLock = new(1, 1);
        private readonly Dictionary<string, ServerHandle> _servers = new(StringComparer.Ordinal);

        private volatile Snapshot _snapshot = new(ToolTable.Empty, new Dictionary<string, ServerHandle>(), null);

        private sealed record Snapshot(
            ToolTable Table,
            IReadOnlyDictionary<string, ServerHandle> Handles,
            string? ConfigError);

        public McpHub(string configPath, ILoggerFactory loggerFactory)
        {
            _configPath = configPath;
            _loggerFactory = loggerFactory;
            _log = loggerFactory.CreateLogger<McpHub>();
        }

        public bool HasFailedServers => _snapshot.Handles.Values.Any(h => !h.Connected);

        public ToolListResponse Current
        {
            get
            {
                var snap = _snapshot;
                var servers = snap.Handles.Values
                                  .OrderBy(h => h.Config.Name, StringComparer.Ordinal)
                                  .Select(h => new ServerStatus(
                                      h.Config.Name,
                                      h.Connected,
                                      snap.Table.Tools.Count(t => t.Server == h.Config.Name),
                                      h.Error,
                                      snap.Table.Hidden.GetValueOrDefault(h.Config.Name) ?? [],
                                      snap.Table.UnknownToolPerms.GetValueOrDefault(h.Config.Name) ?? []))
                                  .ToList();
                return new ToolListResponse(snap.Table.Etag, snap.Table.Tools, servers, snap.ConfigError);
            }
        }

        public async Task<ToolListResponse> ReloadAsync(CancellationToken ct)
        {
            await _reloadLock.WaitAsync(ct);
            try
            {
                GatewayConfig config;
                try
                {
                    var json = await File.ReadAllTextAsync(_configPath, ct);
                    config = GatewayConfig.Parse(json, Environment.GetEnvironmentVariable);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 設定檔壞了就保留現有的連線，等使用者修好
                    _log.LogError("讀取 {Path} 失敗，沿用上一次的設定：{Error}", _configPath, ex.Message);
                    _snapshot = _snapshot with { ConfigError = ex.Message };
                    return Current;
                }

                var current = _servers.ToDictionary(
                    kv => kv.Key,
                    kv => (kv.Value.Config.Fingerprint, kv.Value.Connected));
                var plan = ReloadPlan.Compute(current, config.Servers);

                // 先連好新的再換掉舊的：npx 可能要下載好幾分鐘，這段時間舊的連線照常可用
                var configs = config.Servers.ToDictionary(s => s.Name, StringComparer.Ordinal);
                var connected = await Task.WhenAll(plan.Connect.Select(name => ConnectAsync(configs[name], ct)));

                // 有些 server 同時只能跑一個（鎖住的檔案、固定的 port），新的起不來時先關掉舊的再試一次
                for (var i = 0; i < connected.Length; i++)
                {
                    var name = connected[i].Config.Name;
                    if (connected[i].Connected || !_servers.TryGetValue(name, out var old) || !old.Connected)
                        continue;

                    _servers.Remove(name);
                    await old.DisposeAsync();
                    connected[i] = await ConnectAsync(configs[name], ct);
                }

                var retired = new List<ServerHandle>();
                foreach (var name in plan.Remove.Concat(plan.Connect))
                {
                    if (_servers.Remove(name, out var old))
                        retired.Add(old);
                }

                foreach (var handle in connected)
                    _servers[handle.Config.Name] = handle;

                Publish(configError: null);

                foreach (var old in retired)
                    await old.DisposeAsync();
                _log.LogInformation(
                    "重讀完成：保留 {Keep} 個、重連 {Connect} 個、移除 {Remove} 個 server，共 {Tools} 個工具",
                    plan.Keep.Count, plan.Connect.Count, plan.Remove.Count, _snapshot.Table.Tools.Count);
                return Current;
            }
            finally
            {
                _reloadLock.Release();
            }
        }

        public async Task<CallResponse> CallAsync(CallRequest request, CancellationToken ct)
        {
            var snap = _snapshot;
            if (!snap.Table.TryResolve(request.Name, out var serverName, out var original)
                || !snap.Handles.TryGetValue(serverName, out var handle)
                || handle.Client is null)
                return new CallResponse(true, $"Unknown MCP tool '{request.Name}'. The tool list may have changed.");

            var args = request.Arguments.ValueKind == JsonValueKind.Object
                ? request.Arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                : new Dictionary<string, object?>();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(handle.Config.TimeoutSeconds));

            try
            {
                _log.LogInformation(
                    "呼叫 {Server}/{Tool}（guild {Guild}、channel {Channel}、user {User}）",
                    serverName, original, request.Context.GuildId, request.Context.ChannelId, request.Context.UserId);
                var result = await handle.Client.CallToolAsync(original, args, cancellationToken: cts.Token);
                return new CallResponse(result.IsError == true, FormatContent(result));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return new CallResponse(true, $"The MCP tool timed out after {handle.Config.TimeoutSeconds} seconds.");
            }
            catch (McpProtocolException ex)
            {
                // server 回了 JSON-RPC 錯誤（例如參數不對），連線本身沒事
                return new CallResponse(true, ex.Message);
            }
            // 重讀設定時舊連線被關掉，進行中的呼叫也會拿到取消例外，這不是逾時
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "{Server} 呼叫失敗，標成斷線，下次重讀時重連", serverName);
                handle.MarkFailed(ex.Message);
                return new CallResponse(true, $"The MCP server '{serverName}' failed: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var handle in _servers.Values)
                await handle.DisposeAsync();
            _servers.Clear();
        }

        private void Publish(string? configError)
        {
            var handles = _servers.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var table = ToolTable.Build(handles.Values
                                               .Where(h => h.Connected)
                                               .OrderBy(h => h.Config.Name, StringComparer.Ordinal)
                                               .Select(h => (h.Config, h.Tools)));
            _snapshot = new Snapshot(table, handles, configError);

            foreach (var dropped in table.Dropped)
                _log.LogWarning("工具名稱整理後撞名，沒有列出：{Tool}", dropped);
            foreach (var (server, names) in table.UnknownToolPerms)
                _log.LogWarning("{Server} 的 toolPerms 有對不到工具的名稱，這些設定沒有作用：{Names}", server, string.Join(", ", names));
        }

        private async Task<ServerHandle> ConnectAsync(ServerConfig config, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_connectTimeout);

            McpClient? client = null;
            try
            {
                IClientTransport transport = config.IsHttp
                    ? new HttpClientTransport(new HttpClientTransportOptions
                    {
                        Name = config.Name,
                        Endpoint = new Uri(config.Url!),
                        AdditionalHeaders = config.Headers.ToDictionary(kv => kv.Key, kv => kv.Value)
                    }, _loggerFactory)
                    : new StdioClientTransport(new StdioClientTransportOptions
                    {
                        Name = config.Name,
                        Command = config.Command!,
                        Arguments = config.Args.ToList(),
                        // 不繼承 gateway 的環境變數，免得 BRIDGE_KEY 和別的 server 的密鑰被每個 npx 套件看到
                        InheritEnvironmentVariables = false,
                        EnvironmentVariables = ChildEnvironment(config),
                        WorkingDirectory = config.Cwd
                    }, _loggerFactory);

                var options = new McpClientOptions
                {
                    // SDK 預設只等 server/discover 5 秒，沒回應就改用舊的 initialize 握手。
                    // uvx、npx 第一次啟動要先下載，新版協定的 server 來不及回答，就會因為拒絕 initialize 而連不上。
                    // 舊版 server 收到不認得的方法通常馬上回錯誤，不會因此多等
                    DiscoverProbeTimeout = _discoverTimeout,
                    InitializationTimeout = _connectTimeout
                };
                client = await McpClient.CreateAsync(transport, options, _loggerFactory, cts.Token);
                var tools = await client.ListToolsAsync(cancellationToken: cts.Token);

                _log.LogInformation("已連上 {Server}，{Count} 個工具", config.Name, tools.Count);
                return ServerHandle.Ok(
                    config,
                    client,
                    tools.Select(t => new RemoteTool(t.Name, t.Description, t.JsonSchema)).ToList());
            }
            catch (Exception ex)
            {
                if (client is not null)
                    await client.DisposeAsync();

                var message = ex is OperationCanceledException && !ct.IsCancellationRequested
                    ? $"連線逾時（{_connectTimeout.TotalSeconds:0} 秒）"
                    : ex.Message;
                _log.LogWarning(ex, "連不上 {Server}：{Error}", config.Name, message);
                return ServerHandle.Failed(config, message);
            }
        }

        private static Dictionary<string, string?> ChildEnvironment(ServerConfig config)
        {
            var env = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            foreach (var (key, value) in config.Env)
                env[key] = value;
            return env;
        }

        private static string FormatContent(CallToolResult result)
        {
            var sb = new StringBuilder();
            foreach (var block in result.Content)
            {
                if (block is TextContentBlock text)
                    sb.AppendLine(text.Text);
                else
                    sb.AppendLine($"[{block.Type} content omitted]");
            }

            // 有些工具只回 structuredContent，沒有文字
            if (sb.Length == 0 && result.StructuredContent is { } structured)
                sb.Append(structured.ToString());

            var output = sb.ToString().TrimEnd();
            return output.Length > MAX_TEXT_LENGTH ? output[..MAX_TEXT_LENGTH] + "\n...(truncated)" : output;
        }
    }

    public sealed class ServerHandle : IAsyncDisposable
    {
        private volatile string? _error;

        public ServerConfig Config { get; }
        public McpClient? Client { get; }
        public IReadOnlyList<RemoteTool> Tools { get; }
        public bool Connected => Client is not null && _error is null;
        public string? Error => _error;

        private ServerHandle(ServerConfig config, McpClient? client, IReadOnlyList<RemoteTool> tools, string? error)
        {
            Config = config;
            Client = client;
            Tools = tools;
            _error = error;
        }

        public static ServerHandle Ok(ServerConfig config, McpClient client, IReadOnlyList<RemoteTool> tools)
            => new(config, client, tools, null);

        public static ServerHandle Failed(ServerConfig config, string error)
            => new(config, null, [], error);

        public void MarkFailed(string error)
            => _error = error;

        public async ValueTask DisposeAsync()
        {
            if (Client is null)
                return;

            try
            {
                await Client.DisposeAsync();
            }
            catch
            {
                // 已經斷掉的連線關閉時可能再丟例外，不影響重連
            }
        }
    }
}
