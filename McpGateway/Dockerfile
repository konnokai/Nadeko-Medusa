FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY McpGateway.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0

# stdio 類型的 MCP server 大多用 npx 啟動。Debian 套件庫的 Node 太舊，直接從官方映像檔複製 Node 22。
# 鎖定版本，重新 build 才不會換到沒測過的 Node。要升級時兩行一起改
COPY --from=node:22.23.4-bookworm-slim /usr/local/bin/node /usr/local/bin/node
COPY --from=node:22.23.4-bookworm-slim /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
 && ln -s /usr/local/lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx

# Python 的 MCP server 用 uvx 啟動。映像檔裡沒有 Python，uvx 第一次執行時會自己下載。
# 鎖定版本的理由跟 Node 一樣
COPY --from=ghcr.io/astral-sh/uv:0.12.24 /uv /uvx /usr/local/bin/

WORKDIR /app
COPY --from=build /out .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    MCP_CONFIG=/app/config/mcp.json
EXPOSE 8080

# aspnet 映像檔內建的非 root 使用者。npx、uvx 的快取和 uvx 下載的 Python 都放在它的 /home/app，
# compose 把這個資料夾掛成 volume，重建容器時就不用重新下載
USER $APP_UID
ENTRYPOINT ["dotnet", "McpGateway.dll"]
