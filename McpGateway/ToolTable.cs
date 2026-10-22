using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpGateway.Contracts;

namespace McpGateway
{
    public sealed record RemoteTool(string Name, string? Description, JsonElement InputSchema);

    public static class ToolNaming
    {
        // OpenAI 相容的工具名稱只能有英數字、_、-，最多 64 個字元
        private const int MAX_LENGTH = 64;
        private const int HASH_LENGTH = 8;

        public static string Expose(string server, string tool)
        {
            var name = Sanitize(server) + "__" + Sanitize(tool);
            if (name.Length <= MAX_LENGTH)
                return name;

            // 截斷後加上完整名稱的雜湊，兩個只差在尾巴的長名稱才不會撞在一起
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..HASH_LENGTH].ToLowerInvariant();
            return name[..(MAX_LENGTH - HASH_LENGTH - 1)] + "_" + hash;
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
            return sb.ToString();
        }
    }

    public sealed class ToolTable
    {
        // 要寫在 Empty 前面：靜態欄位照順序初始化
        private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _noNotes =
            new Dictionary<string, IReadOnlyList<string>>();

        public static readonly ToolTable Empty = new([], new Dictionary<string, (string, string)>(), "empty", [], _noNotes, _noNotes);

        public IReadOnlyList<ToolInfo> Tools { get; }
        public string Etag { get; }
        // 整理名稱後撞名而沒列出的工具，給 McpHub 寫進 log
        public IReadOnlyList<string> Dropped { get; }
        // 以下兩個以 server 名稱為 key，只收有內容的 server，給 .mcptools 顯示
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Hidden { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> UnknownToolPerms { get; }

        private readonly IReadOnlyDictionary<string, (string Server, string Original)> _lookup;

        private ToolTable(
            IReadOnlyList<ToolInfo> tools,
            IReadOnlyDictionary<string, (string, string)> lookup,
            string etag,
            IReadOnlyList<string> dropped,
            IReadOnlyDictionary<string, IReadOnlyList<string>> hidden,
            IReadOnlyDictionary<string, IReadOnlyList<string>> unknownToolPerms)
        {
            Tools = tools;
            _lookup = lookup;
            Etag = etag;
            Dropped = dropped;
            Hidden = hidden;
            UnknownToolPerms = unknownToolPerms;
        }

        public bool TryResolve(string exposedName, out string server, out string original)
        {
            if (_lookup.TryGetValue(exposedName, out var hit))
            {
                (server, original) = hit;
                return true;
            }

            server = original = "";
            return false;
        }

        public static ToolTable Build(IEnumerable<(ServerConfig Server, IReadOnlyList<RemoteTool> Tools)> sources)
        {
            var tools = new List<ToolInfo>();
            var lookup = new Dictionary<string, (string Server, string Original)>(StringComparer.Ordinal);
            var dropped = new List<string>();
            var hidden = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var unknownPerms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

            foreach (var (server, remote) in sources)
            {
                var serverHidden = new List<string>();

                foreach (var tool in remote)
                {
                    if (server.EnabledTools is { } enabled && !enabled.Contains(tool.Name))
                    {
                        serverHidden.Add(tool.Name);
                        continue;
                    }

                    var exposed = ToolNaming.Expose(server.Name, tool.Name);
                    // 不同的 server 名稱整理後可能變成同一個，先登記的留下
                    if (!lookup.TryAdd(exposed, (server.Name, tool.Name)))
                    {
                        var kept = lookup[exposed];
                        dropped.Add($"{server.Name}/{tool.Name} -> {exposed}（已經給 {kept.Server}/{kept.Original} 用了）");
                        serverHidden.Add(tool.Name);
                        continue;
                    }

                    tools.Add(new ToolInfo(
                        exposed,
                        tool.Name,
                        server.Name,
                        tool.Description ?? "",
                        tool.InputSchema,
                        server.DataTool,
                        server.ToolPerms.GetValueOrDefault(tool.Name) ?? server.RequiredPerm));
                }

                if (serverHidden.Count > 0)
                    hidden[server.Name] = serverHidden;

                // 打錯字的話工具會默默用回 requiredPerm，可能比原本想要的寬鬆，所以要讓人看得到
                var unknown = server.ToolPerms.Keys
                                    .Where(name => remote.All(t => t.Name != name))
                                    .Order(StringComparer.Ordinal)
                                    .ToList();
                if (unknown.Count > 0)
                    unknownPerms[server.Name] = unknown;
            }

            return new ToolTable(tools, lookup, ComputeEtag(tools), dropped, hidden, unknownPerms);
        }

        private static string ComputeEtag(IReadOnlyList<ToolInfo> tools)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(tools, GatewayProtocol.Json);
            return Convert.ToHexString(SHA256.HashData(json))[..16].ToLowerInvariant();
        }
    }

    public sealed record ReloadPlan(IReadOnlyList<string> Remove, IReadOnlyList<string> Connect, IReadOnlyList<string> Keep)
    {
        public static ReloadPlan Compute(
            IReadOnlyDictionary<string, (string Fingerprint, bool Connected)> current,
            IReadOnlyList<ServerConfig> desired)
        {
            var active = desired.Where(s => !s.Disabled).ToDictionary(s => s.Name, StringComparer.Ordinal);

            var remove = current.Keys.Where(name => !active.ContainsKey(name)).ToList();
            var connect = new List<string>();
            var keep = new List<string>();

            foreach (var server in active.Values)
            {
                // 設定沒變而且還連著才保留，之前連不上的趁這次重試
                if (current.TryGetValue(server.Name, out var cur) && cur.Fingerprint == server.Fingerprint && cur.Connected)
                    keep.Add(server.Name);
                else
                    connect.Add(server.Name);
            }

            return new ReloadPlan(remove, connect, keep);
        }
    }
}
