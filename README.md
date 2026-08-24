# StoneAge Online v0.1-01

第一個可執行骨架：PostgreSQL + TCP Game Server + TestClient。

## Requirements

- .NET 10 SDK
- Docker Desktop (建議) 或本機 PostgreSQL 18
- Visual Studio / Rider / VS Code 等支援 .NET 10 的 IDE（可選）

## 1. 啟動 PostgreSQL

```powershell
cd docker
docker compose up -d
cd ..
```

預設開發資料庫：

- Host: `127.0.0.1`
- Port: `5432`
- Database: `stoneage`
- Username: `stoneage`
- Password: `stoneage_dev`

> 僅限本機開發。正式環境不可沿用此密碼。

## 2. 建立 Solution / Restore / Build

```powershell
powershell -ExecutionPolicy Bypass -File .\bootstrap.ps1
```

.NET 10 的 `dotnet new sln` 預設產生 `.slnx`。

## 3. 啟動 Server

```powershell
.\run-server.ps1
```

預期看到：

```text
StoneAge Online Dev starting
TCP game server listening on 0.0.0.0:7021
```

Server 啟動時會測試 PostgreSQL，並以 EF Core `EnsureCreated` 建立 `accounts` 與 `characters` 表。

## 4. 啟動 TestClient

另外開一個 PowerShell：

```powershell
.\run-client.ps1
```

預期：

```text
StoneAge Online TestClient v0.1-01
Connecting to 127.0.0.1:7021 ...
Connected. Opcode=Hello Payload="StoneAge Online v0.1-01"
```

## v0.1-01 驗收

- [ ] Solution 可 Restore / Build
- [ ] PostgreSQL 正常啟動
- [ ] Server 能建立/連接資料庫
- [ ] TCP 7021 Listening
- [ ] TestClient 能連線
- [ ] Client 能收到 binary Hello packet
- [ ] Server 能記錄 connect/disconnect

## 下一階段 v0.1-02

- Session ID
- Packet Reader（黏包 / 拆包）
- LoginRequest / LoginResponse
- Account Repository
- Password Hash
- 登入狀態機
