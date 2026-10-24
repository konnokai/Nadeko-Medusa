# McpGateway + McpBridge

讓 NadekoBot 的 AI agent 使用 MCP server 的工具。

```
Discord → NadekoBot
            └─ McpBridge（medusa）
                  │  HTTP + JSON（X-Bridge-Key）
                  ▼
            McpGateway（另一個容器，官方 ModelContextProtocol SDK）
                  │  MCP（stdio / HTTP）
                  ▼
            各個 MCP server
```

- **McpGateway**：獨立程序。讀 `mcp.json`，連線每個 MCP server，把它們的工具整理成一份清單。
- **McpBridge**：medusa。定期向 gateway 拿工具清單，把工具登記進 bot 的 AI 工具清單。AI 呼叫工具時轉給 gateway。

MCP SDK 需要 `System.Text.Json` 10。gateway 是 net10.0，內建這一版；bot 和 McpBridge 是 net9.0。SDK 放在 gateway 程序裡，就不會跟 bot 的型別衝突。McpBridge 只用 bot 已有的組件。

## 部署

### 1. 放檔案

把 `McpGateway/` 整個資料夾複製到伺服器上，放在 bot 的 `docker-compose.yml` 旁邊，改名成 `mcp-gateway/`。再建一個 `mcp/` 資料夾，放 `mcp.json`（可以從 `mcp.example.json` 複製）。

```
docker-compose.yml
.env
mcp-gateway/      ← McpGateway 的原始碼
mcp/mcp.json
data/medusae/McpBridge/   ← stage-medusae.ps1 的產物
```

### 2. `.env`

```
MCP_BRIDGE_KEY=換成一串隨機字串
```

### 3. `docker-compose.yml`

```yaml
services:
  nadeko:
    image: ghcr.io/nadeko-bot/nadekobot:7.6.7
    container_name: nadeko
    restart: unless-stopped
    environment:
      TZ: Asia/Taipei
      bot_token: [token]
      MCP_GATEWAY_URL: http://mcp-gateway:8080
      MCP_GATEWAY_KEY: ${MCP_BRIDGE_KEY}
    volumes:
      - ${PWD}/data:/app/data
      - ${PWD}/attach_log:/app/attach_log
    extra_hosts:
      - "host.docker.internal:host-gateway"
    depends_on:
      - mcp-gateway

  mcp-gateway:
    build: ./mcp-gateway
    container_name: mcp-gateway
    restart: unless-stopped
    environment:
      TZ: Asia/Taipei
      BRIDGE_KEY: ${MCP_BRIDGE_KEY}
    volumes:
      - ${PWD}/mcp:/app/config
      - mcp-cache:/home/app

volumes:
  mcp-cache:
```

- **`network_mode: bridge` 要拿掉。** Docker 預設的 bridge 網路不能用服務名稱找到其他容器，`http://mcp-gateway:8080` 會連不到。拿掉之後，compose 會建一個自己的網路，兩個容器都在裡面。`host.docker.internal` 和區網位址照樣能連。
- gateway 不要設 `ports:`。它只需要讓 bot 連，不要開到主機外面。
- 掛載的是 `mcp/` 資料夾，不是單一檔案。編輯器存檔時常會換掉整個檔案，只掛單一檔案的話，容器看不到新的內容。
- 連線時 gateway 最多等 60 秒判斷 server 用新版（2026-07-28）還是舊版的 MCP 協定，整個連線最多等 2 分鐘。第一次下載超過這個時間的話，server 會顯示連不上，5 分鐘後 gateway 會自動重試，那時套件已經在快取裡了。
- `mcp-cache` 保存 `npx`、`uvx` 下載的套件，還有 `uvx` 下載的 Python。沒有它的話，每次重建容器都要重新下載，MCP server 第一次啟動會很慢（gateway 每個連線最多等 2 分鐘）。
  - 用 named volume，不要改成 `${PWD}/...` 這種資料夾掛載。gateway 用 `app` 使用者（UID 1654）執行。Docker 自己建的資料夾屬於 root，gateway 寫不進去。一定要用資料夾掛載的話，先執行 `sudo chown -R 1654:1654 <資料夾>`。
  - 套件壞掉或想清空時：`docker compose down`，再 `docker volume rm <專案名稱>_mcp-cache`。

### 4. 啟動

```bash
docker compose up -d --build
```

在 Discord 下 `.meload McpBridge`（只要第一次）。之後 bot 重啟會自動載入。

## mcp.json

格式跟 Claude Desktop 的 `mcpServers` 一樣，另外多了幾個 bot 用的欄位。可以寫 `//` 註解。

```json
{
  "mcpServers": {
    "everything": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-everything"],
      "env": { "API_KEY": "${EVERYTHING_KEY}" },
      "requiredPerm": "ManageMessages"
    },
    "my-api": {
      "type": "http",
      "url": "http://192.168.90.21:9000/mcp",
      "headers": { "Authorization": "Bearer ${MY_API_TOKEN}" },
      "enabledTools": ["search", "lookup"],
      "dataTool": false,
      "requiredPerm": "None"
    }
  }
}
```

| 欄位 | 說明 |
|---|---|
| `command`、`args`、`env`、`cwd` | stdio server。gateway 會用子程序啟動它 |
| `type: "http"`、`url`、`headers` | HTTP server。有 `url` 時 `type` 可以省略 |
| `requiredPerm` | 要有這個 Discord 權限才能用（`GuildPermission` 的名稱，例如 `ManageMessages`）。**沒寫時是 `Administrator`**。只給 bot owner 用，寫 `"BotOwner"`。要開放給所有人，寫 `"None"` |
| `toolPerms` | 個別工具的權限，蓋過 `requiredPerm`。key 是 MCP server 原本的工具名稱，值的寫法跟 `requiredPerm` 一樣。例如 `{ "qdrant-find": "BotOwner", "qdrant-store": "ManageMessages" }`。名稱打錯字時 `.mcpstatus` 和 `.mcptools` 會警告。用 `.mcptools` 確認結果 |
| `dataTool` | 預設 `true`：工具放進資料工具，AI 要先用 `search_data_tools` 找到它。工具少、常用時可以設 `false`，每一輪都會送給 AI，但比較耗 token |
| `enabledTools` | 只開放這些工具（用 MCP server 原本的工具名稱）。沒寫就全部開放 |
| `timeoutSeconds` | 每次工具呼叫的 timeout，預設 60 秒。McpBridge 最多等 5 分鐘，設更長沒有用 |
| `disabled` | `true` 時不連線這個 server，也不檢查它用到的環境變數 |

- `${名稱}` 會換成 gateway 容器的環境變數。密鑰放在 compose 的 `environment:` 或 `.env`，不要直接寫在 `mcp.json`。
- stdio server 只拿得到 `PATH`、`HOME` 這類基本環境變數，加上自己 `env` 裡寫的。gateway 的其他環境變數（包括 `BRIDGE_KEY` 和別的 server 的密鑰）不會傳下去。需要 `TZ`、`HTTPS_PROXY`、`NODE_EXTRA_CA_CERTS` 之類的變數時，寫進那個 server 的 `env`，例如 `"env": { "TZ": "${TZ}" }`。
- 工具在 AI 那邊的名稱是 `server名稱__工具名稱`，例如 `everything__echo`。只會留英數字、`_`、`-`，超過 64 個字元會截斷並加上雜湊值。
- 工具名稱跟 bot 內建工具撞名時，McpBridge 會跳過這個工具，並記錄在 log。

## 刷新

- gateway 監看 `mcp/` 資料夾。`mcp.json` 存檔後大約 1 秒會自動重讀：新增的 server 會連線，刪掉的會斷線，有改到的或之前連不上的會重連。有些環境（例如 Docker Desktop 的掛載資料夾）收不到檔案變動通知，所以 gateway 每 60 秒也會比對一次檔案的修改時間。
- 一個 server 的任何設定有改（包括 `requiredPerm`、`toolPerms`），那個 server 就會重連。
- 重連時會先連好新的，再換掉舊的。重連期間舊的連線照常可用。新的連不上時（例如 server 會鎖住檔案或 port），會先關掉舊的再試一次。
- 換掉舊連線的那一刻，還在跑的工具呼叫會中斷，AI 會收到錯誤。
- McpBridge 每 60 秒向 gateway 拿一次工具清單，清單有變才更新 bot。
- 所以改完 `mcp.json`，最多等 1 分鐘就會生效，不用重新編譯，也不用重啟。
- `mcp.json` 格式錯誤或讀不到時，gateway 會保留原本的連線，錯誤訊息會出現在 `.mcpstatus`。

## 指令（只有 bot owner 能用）

| 指令 | 說明 |
|---|---|
| `.mcpstatus` | 顯示每個 server 的連線狀態和工具數量 |
| `.mcpreload` | 叫 gateway 馬上重讀 `mcp.json`，然後馬上更新 bot 的工具清單 |
| `.mcptools <server>` | 列出這個 server 的每個工具、原本的名稱、需要的權限，以及沒開放的工具（被 `enabledTools` 關掉、整理名稱後撞名，或跟 bot 內建工具撞名）。不加 server 名稱時列出所有 server |

## 安全

- AI 呼叫 MCP 工具時，McpBridge 會先檢查發話者有沒有 `requiredPerm`（或 `toolPerms` 裡的設定）。MCP server 本身不知道 Discord 權限。
- `BotOwner` 看的是 bot creds 裡的 `OwnerIds`。bot owner 沒有特權：工具要求 `Administrator` 時，bot owner 在這個伺服器也要是管理員。
- 權限寫錯（不是 `None`、`BotOwner` 或 `GuildPermission` 的名稱）時，那個工具誰都不能用。`.mcptools` 會標出來。
- 能寫入或刪除資料的工具（例如記憶庫的 store、delete）要特別小心。開放給所有人寫入的話，別人存進去的內容之後會被查出來交給 AI，裡面可能藏著指令。用不到的工具用 `enabledTools` 關掉。
- 工具的回傳內容開頭會加上 `[UNTRUSTED MCP TOOL OUTPUT ...]`，提醒 AI 不要照著裡面的指令做。
- gateway 每個請求都要帶 `X-Bridge-Key`，值要跟 `BRIDGE_KEY` 一樣。沒設 `BRIDGE_KEY` 時 gateway 不會啟動。
- AI agent 設定裡如果有 `AllowedTools`，MCP 工具的名稱也要加進去，不然會被過濾掉。資料工具則是只要允許 `invoke_data_tool` 就能用。

## 限制

- McpBridge 用反射修改 bot 的私有成員（`AiToolRegistry`、`DataToolSearchService`）。bot 升級後這些成員可能改名。McpBridge 載入時會先檢查，找不到就記錄錯誤並停用，不會讓 bot 當掉。`Nadeko-Medusa.Tests` 也會檢查。
- gateway 的映像檔是 `aspnet:10.0`（Ubuntu 24.04），內建 Node.js 22（給 `npx` 用）和 uv（給 `uvx` 用），版本鎖在 `Dockerfile`。`uvx` 會自己下載 Python，第一次啟動要連得到網路。
- 映像檔沒有 .NET SDK，不能用 `dnx`。這類 server 改用 HTTP 模式另外跑一個容器，在 `mcp.json` 填 `url`。
- MCP 工具回傳的圖片、音訊等非文字內容不會轉給 AI，只會顯示「省略」。
- 資料工具要靠 bot 的語意搜尋模型才找得到。bot 的 log 出現 `Embedding service not ready` 或 ONNX 載入失敗時，資料工具都找不到，這時把 MCP server 設成 `"dataTool": false`。
