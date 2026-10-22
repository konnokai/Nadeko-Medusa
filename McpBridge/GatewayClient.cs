using System.Net.Http.Json;
using McpGateway.Contracts;
using Nadeko.Common;

namespace McpBridge
{
    public sealed class GatewayClient
    {
        private const int MAX_ERROR_BODY = 300;
        private static readonly TimeSpan _listTimeout = TimeSpan.FromSeconds(30);
        // reload 要等 gateway 把改過的 server 都重連完（gateway 每個連線最多等 2 分鐘），再加上等前一次重讀的時間
        private static readonly TimeSpan _reloadTimeout = TimeSpan.FromMinutes(3);
        // 每個 server 自己的 timeout 由 gateway 控制，這裡只防 gateway 本身卡住
        private static readonly TimeSpan _callTimeout = TimeSpan.FromMinutes(5);

        private readonly IHttpClientFactory _factory;
        private readonly Uri _baseUri;
        private readonly string _key;

        public GatewayClient(IHttpClientFactory factory, Uri baseUri, string key)
        {
            _factory = factory;
            _baseUri = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
            _key = key;
        }

        public Task<ToolListResponse> GetToolsAsync(CancellationToken ct)
            => SendAsync<ToolListResponse>(HttpMethod.Get, "tools", null, _listTimeout, ct);

        public Task<ToolListResponse> ReloadAsync(CancellationToken ct)
            => SendAsync<ToolListResponse>(HttpMethod.Post, "reload", null, _reloadTimeout, ct);

        public Task<CallResponse> CallAsync(CallRequest request, CancellationToken ct)
            => SendAsync<CallResponse>(HttpMethod.Post, "tools/call", request, _callTimeout, ct);

        private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, TimeSpan timeout, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            // owner-config：自架的 bot 可以連 compose 內部網路或區網的位址
            using var http = _factory.CreateClient(ExternalHttp.OWNER_CLIENT_NAME);
            http.Timeout = Timeout.InfiniteTimeSpan;

            using var req = new HttpRequestMessage(method, new Uri(_baseUri, path));
            req.Headers.Add(GatewayProtocol.KEY_HEADER, _key);
            if (body is not null)
                req.Content = JsonContent.Create(body, body.GetType(), options: GatewayProtocol.Json);

            using var res = await http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode)
            {
                var text = await res.Content.ReadAsStringAsync(cts.Token);
                if (text.Length > MAX_ERROR_BODY)
                    text = text[..MAX_ERROR_BODY] + "...";
                throw new HttpRequestException($"MCP gateway 回應 HTTP {(int)res.StatusCode}：{text}");
            }

            return await res.Content.ReadFromJsonAsync<T>(GatewayProtocol.Json, cts.Token)
                   ?? throw new HttpRequestException("MCP gateway 回應是空的");
        }
    }
}
