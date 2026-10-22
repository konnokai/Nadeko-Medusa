using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpGateway;
using McpGateway.Contracts;

var key = Environment.GetEnvironmentVariable("BRIDGE_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    // 沒有 key 的話任何連得到這個容器的程式都能呼叫 MCP 工具，寧可不啟動
    Console.Error.WriteLine("BRIDGE_KEY 沒有設定，gateway 不啟動");
    return 1;
}

var configPath = Environment.GetEnvironmentVariable("MCP_CONFIG") ?? "/app/config/mcp.json";

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddSingleton(sp => new McpHub(configPath, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddHostedService(sp => new HubWorker(
    sp.GetRequiredService<McpHub>(),
    configPath,
    sp.GetRequiredService<ILogger<HubWorker>>()));

var app = builder.Build();
var keyBytes = Encoding.UTF8.GetBytes(key);

app.MapGet("/health", () => "ok");

var api = app.MapGroup("").AddEndpointFilter(async (ctx, next) =>
{
    var given = Encoding.UTF8.GetBytes(ctx.HttpContext.Request.Headers[GatewayProtocol.KEY_HEADER].ToString());
    return CryptographicOperations.FixedTimeEquals(given, keyBytes)
        ? await next(ctx)
        : Results.Unauthorized();
});

api.MapGet("/tools", (McpHub hub) => Results.Json(hub.Current, GatewayProtocol.Json));

// 用程式的生命週期當取消來源：McpBridge 等太久斷線時，重連做到一半不能被中斷
api.MapPost("/reload", async (McpHub hub, IHostApplicationLifetime life)
    => Results.Json(await hub.ReloadAsync(life.ApplicationStopping), GatewayProtocol.Json));

api.MapPost("/tools/call", async (HttpRequest http, McpHub hub, CancellationToken ct) =>
{
    CallRequest? request;
    try
    {
        request = await http.ReadFromJsonAsync<CallRequest>(GatewayProtocol.Json, ct);
    }
    catch (Exception ex) when (ex is JsonException or InvalidOperationException)
    {
        // JSON 壞掉或 Content-Type 不對，是呼叫端的錯，不是 gateway 的錯
        return Results.BadRequest(ex.Message);
    }

    // 少了欄位時 System.Text.Json 會留 null，不擋的話後面會丟 NullReferenceException
    return request?.Name is null || request.Context is null
        ? Results.BadRequest("name and context are required")
        : Results.Json(await hub.CallAsync(request, ct), GatewayProtocol.Json);
});

// McpHub 是 DI 建立的 singleton，程式結束時容器會呼叫它的 DisposeAsync 關掉所有連線
app.Run();
return 0;
