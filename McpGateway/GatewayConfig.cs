using System.Text.Json;
using System.Text.RegularExpressions;

namespace McpGateway
{
    public sealed record ServerConfig(
        string Name,
        string? Command,
        IReadOnlyList<string> Args,
        IReadOnlyDictionary<string, string?> Env,
        string? Cwd,
        string? Url,
        IReadOnlyDictionary<string, string> Headers,
        string? RequiredPerm,
        IReadOnlyDictionary<string, string> ToolPerms,
        bool DataTool,
        IReadOnlySet<string>? EnabledTools,
        int TimeoutSeconds,
        bool Disabled,
        string Fingerprint)
    {
        public bool IsHttp => Url is not null;
    }

    public sealed partial record GatewayConfig(IReadOnlyList<ServerConfig> Servers)
    {
        private const int DEFAULT_TIMEOUT_SECONDS = 60;

        private static readonly JsonDocumentOptions _options = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
        private static partial Regex EnvRefRegex();

        // 格式錯誤一律丟 FormatException，呼叫端據此保留舊的連線
        public static GatewayConfig Parse(string json, Func<string, string?> getEnv)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json, _options);
            }
            catch (JsonException ex)
            {
                throw new FormatException($"mcp.json 不是有效的 JSON：{ex.Message}", ex);
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("mcpServers", out var servers)
                    || servers.ValueKind != JsonValueKind.Object)
                    throw new FormatException("mcp.json 缺少 mcpServers 物件");

                var list = new List<ServerConfig>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var prop in servers.EnumerateObject())
                {
                    if (!names.Add(prop.Name))
                        throw new FormatException($"server {prop.Name} 重複出現");
                    list.Add(ParseServer(prop.Name, prop.Value, getEnv));
                }

                return new GatewayConfig(list);
            }
        }

        private static ServerConfig ParseServer(string name, JsonElement el, Func<string, string?> getEnv)
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new FormatException($"server {name} 必須是物件");

            var disabled = el.TryGetProperty("disabled", out var disEl) && disEl.ValueKind == JsonValueKind.True;
            // 停用的 server 不會連線，範例設定裡停用的 server 用到的密鑰沒設也不該擋住整份設定
            if (disabled)
                getEnv = static _ => "";

            string? Str(string key)
                => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? Expand(v.GetString()!, name, getEnv)
                    : null;

            var command = Str("command");
            var url = Str("url");
            var type = Str("type");

            if ((type is "http" or "streamable-http" or "sse") && url is null)
                throw new FormatException($"server {name} 是 http 類型，但沒有 url");
            if (command is null && url is null)
                throw new FormatException($"server {name} 要有 command（stdio）或 url（http）");

            var args = el.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array
                ? argsEl.EnumerateArray().Select(a => Expand(StringValue(a, name, "args"), name, getEnv)).ToList()
                : [];

            var env = ReadMap(el, "env", name, getEnv).ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
            var headers = ReadMap(el, "headers", name, getEnv);

            IReadOnlySet<string>? enabled = el.TryGetProperty("enabledTools", out var enEl) && enEl.ValueKind == JsonValueKind.Array
                ? enEl.EnumerateArray().Select(x => StringValue(x, name, "enabledTools")).ToHashSet(StringComparer.Ordinal)
                : null;

            var dataTool = !el.TryGetProperty("dataTool", out var dtEl) || dtEl.ValueKind != JsonValueKind.False;
            var timeout = el.TryGetProperty("timeoutSeconds", out var toEl) && toEl.TryGetInt32(out var t) && t > 0
                ? t
                : DEFAULT_TIMEOUT_SECONDS;

            return new ServerConfig(
                name,
                command,
                args,
                env,
                Str("cwd"),
                url,
                headers,
                Str("requiredPerm"),
                ReadToolPerms(el, name),
                dataTool,
                enabled,
                timeout,
                disabled,
                // 用原始文字當指紋：同一個 server 的設定沒動就不用重連
                el.GetRawText());
        }

        private static Dictionary<string, string> ReadMap(JsonElement el, string key, string server, Func<string, string?> getEnv)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!el.TryGetProperty(key, out var mapEl) || mapEl.ValueKind != JsonValueKind.Object)
                return map;

            foreach (var p in mapEl.EnumerateObject())
                map[p.Name] = Expand(StringValue(p.Value, server, key), server, getEnv);

            return map;
        }

        // 權限名稱不會放密鑰，所以不做 ${} 替換
        private static Dictionary<string, string> ReadToolPerms(JsonElement el, string server)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!el.TryGetProperty("toolPerms", out var mapEl) || mapEl.ValueKind != JsonValueKind.Object)
                return map;

            foreach (var p in mapEl.EnumerateObject())
                map[p.Name] = StringValue(p.Value, server, "toolPerms");

            return map;
        }

        // 數字或布林寫錯地方時要回報格式錯誤，不能讓 GetString 丟出別種例外
        private static string StringValue(JsonElement el, string server, string key)
            => el.ValueKind == JsonValueKind.String
                ? el.GetString()!
                : throw new FormatException($"server {server} 的 {key} 只能放字串，例如 \"3000\"");

        private static string Expand(string value, string server, Func<string, string?> getEnv)
            => EnvRefRegex().Replace(value, m => getEnv(m.Groups[1].Value)
                                              ?? throw new FormatException(
                                                  $"server {server} 用到環境變數 {m.Groups[1].Value}，但 gateway 沒有設定它"));
    }
}
