namespace McpGateway
{
    // 啟動時連線，之後 mcp.json 一改就重讀；連不上的 server 定期重試
    public sealed class HubWorker : BackgroundService
    {
        private static readonly TimeSpan _tick = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan _retryFailedEvery = TimeSpan.FromMinutes(5);
        // 編輯器存檔常會連續觸發好幾次事件，等安靜下來再讀
        private static readonly TimeSpan _debounce = TimeSpan.FromSeconds(1);

        private readonly McpHub _hub;
        private readonly string _configPath;
        private readonly ILogger<HubWorker> _log;
        private readonly SemaphoreSlim _changed = new(0, 1);

        public HubWorker(McpHub hub, string configPath, ILogger<HubWorker> log)
        {
            _hub = hub;
            _configPath = configPath;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await ReloadAsync(stoppingToken);

            using var watcher = CreateWatcher();
            var lastRetry = DateTime.UtcNow;
            var lastWrite = LastWriteTime();

            while (!stoppingToken.IsCancellationRequested)
            {
                bool changed;
                try
                {
                    changed = await _changed.WaitAsync(_tick, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Docker Desktop 的掛載資料夾常收不到變動通知，每次醒來都再比對一次修改時間
                var write = LastWriteTime();
                if (write != lastWrite)
                {
                    lastWrite = write;
                    changed = true;
                }

                if (changed)
                {
                    await Task.Delay(_debounce, stoppingToken);
                    _changed.Wait(0);
                    lastWrite = LastWriteTime();
                    _log.LogInformation("{Path} 有變動，重讀", _configPath);
                    await ReloadAsync(stoppingToken);
                }
                else if (_hub.HasFailedServers && DateTime.UtcNow - lastRetry >= _retryFailedEvery)
                {
                    lastRetry = DateTime.UtcNow;
                    await ReloadAsync(stoppingToken);
                }
            }
        }

        // BackgroundService 丟出例外會讓整個 gateway 停掉，容器就會一直重啟
        private async Task ReloadAsync(CancellationToken ct)
        {
            try
            {
                await _hub.ReloadAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "重讀 MCP 設定失敗");
            }
        }

        private DateTime LastWriteTime()
            => File.GetLastWriteTimeUtc(_configPath);

        private FileSystemWatcher? CreateWatcher()
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_configPath))!;
            if (!Directory.Exists(dir))
            {
                _log.LogWarning("找不到 {Dir}，不會自動偵測 mcp.json 的變動", dir);
                return null;
            }

            // 監看整個資料夾：編輯器存檔時常是寫新檔再改名，只監看單一檔案會漏掉
            var watcher = new FileSystemWatcher(dir, Path.GetFileName(_configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            watcher.Changed += (_, _) => Signal();
            watcher.Created += (_, _) => Signal();
            watcher.Renamed += (_, _) => Signal();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private void Signal()
        {
            try
            {
                _changed.Release();
            }
            catch (SemaphoreFullException)
            {
                // 已經有一個待處理的通知，合併成一次
            }
        }
    }
}
