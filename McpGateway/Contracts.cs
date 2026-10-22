using System.Text.Json;

// McpBridge 用 <Compile Include> 連結這個檔案，兩邊的協定只寫在這裡。
// 這裡只能用 net9.0 內建的 System.Text.Json API：McpBridge 跑在 bot 程序裡，拿到的是 bot 那一版。
namespace McpGateway.Contracts
{
    public static class GatewayProtocol
    {
        public const string KEY_HEADER = "X-Bridge-Key";

        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    }

    public sealed record ToolListResponse(
        string Etag,
        IReadOnlyList<ToolInfo> Tools,
        IReadOnlyList<ServerStatus> Servers,
        string? ConfigError);

    // Name 是 AI 看到的名稱（server__tool），OriginalName 是 MCP server 原本的名稱（mcp.json 的 toolPerms 用它）。
    // RequiredPerm 已經套用過 toolPerms，原樣轉給 McpBridge，由它對照 Discord 權限。
    public sealed record ToolInfo(
        string Name,
        string OriginalName,
        string Server,
        string Description,
        JsonElement InputSchema,
        bool DataTool,
        string? RequiredPerm);

    // HiddenTools：server 有、但被 enabledTools 關掉或撞名沒列出的工具。
    // UnknownToolPerms：toolPerms 裡對不到任何工具的名稱，通常是打錯字。
    public sealed record ServerStatus(
        string Name,
        bool Connected,
        int ToolCount,
        string? Error,
        IReadOnlyList<string> HiddenTools,
        IReadOnlyList<string> UnknownToolPerms);

    public sealed record CallRequest(string Name, JsonElement Arguments, CallContext Context);

    public sealed record CallContext(ulong GuildId, ulong ChannelId, ulong UserId);

    public sealed record CallResponse(bool IsError, string Text);
}
