# ServerS5A9

面向 S5A9 的 2D 横版动作游戏服务端模拟器，使用 C# / .NET 10 实现，用于学习和研究网络游戏服务端架构、数据持久化及协议兼容性。

本仓库架构参考自 [ServerS4A21](https://gitgud.io/rewio/ServerS4A21)。

PVF 读取由独立的 [PvfLib](Tools/PvfLib/README.md) 提供，游戏资源定义和业务查询位于服务端 GameWorld。

## 免责声明

本项目为非官方的技术研究项目，与任何游戏开发商、发行商或运营商无关联，亦未获得其背书。相关名称、商标及第三方资源的权利归各自权利人所有。

仓库仅提供自有模拟器源码、开发工具和技术文档，不提供游戏客户端、PVF、原版服务端或玩家数据。使用者应自行确保所使用的第三方资源及使用方式具有合法授权，请勿用于商业运营、未经授权的公开服务或其他侵犯第三方权益的活动。

项目仍处于研究和完善阶段，存在未实现功能及已知或未知问题，不保证完整性、稳定性或适用性。使用前请自行评估，并妥善备份数据。

客户端补丁：[S5A9ClientPatch](https://codeberg.org/deadbeef7799/S5A9ClientPatch)。

## 从这里开始

- **只需要可运行文件**：按下面的[快速构建](#快速构建可运行包)生成自包含包，不必先阅读业务代码。
- **准备修改代码**：阅读[贡献指南](CONTRIBUTING.md)，完成开发构建，再选择对应模块和测试。
- **使用 agent 开发**：先读 [AGENTS](AGENTS.md)，再按[文档索引](Docs/README.md)选读；当前功能与未实现边界以所属模块文档为准。

下文命令默认从仓库根目录执行，也就是包含 `ServerS5A9.sln` 的目录。

## 快速构建可运行包

### 1. 准备构建环境

安装 [.NET 10 SDK](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)，在终端执行 `dotnet --list-sdks`，确认存在 `10.0.x`。构建需要 SDK，仅安装 Runtime 不够；编辑器不限，不要求安装 Visual Studio。

首次构建需要可访问的 NuGet 源，依赖会自动还原。**编译和发布不需要客户端或 PVF**；运行服务端和资源相关测试时才需要自行准备 `Script.pvf`。服务端使用 SQLite，不需要另装数据库服务。构建、自测和 TCP 回归不依赖 Python。

### 2. 发布目标平台的程序

Windows x64，在 PowerShell 中执行：

```powershell
dotnet publish Server/DfoServer/DfoServer.csproj -c Release -r win-x64 --self-contained true -o dist/win-x64
./dist/win-x64/DfoServer.exe --help
```

Linux x64，在目标 Linux 的终端中执行：

```bash
dotnet publish Server/DfoServer/DfoServer.csproj -c Release -r linux-x64 --self-contained true -o dist/linux-x64
chmod +x dist/linux-x64/DfoServer
./dist/linux-x64/DfoServer --help
```

`--help` 不读取资源、不创建数据库，可用于确认生成的程序能够启动。其他平台同时替换命令中的 `-r` 和输出目录：

| 目标系统与架构 | RID | 建议输出目录 |
|---|---|---|
| Windows x64 | `win-x64` | `dist/win-x64` |
| Linux x64（glibc） | `linux-x64` | `dist/linux-x64` |
| Linux ARM64（glibc） | `linux-arm64` | `dist/linux-arm64` |
| macOS Intel | `osx-x64` | `dist/osx-x64` |
| macOS Apple Silicon | `osx-arm64` | `dist/osx-arm64` |

RID 选择的是**运行程序的机器**，不是游戏客户端的架构。跨平台发布后，帮助命令和实际启动需在目标系统验证；其他系统条件见 [.NET RID 说明](https://learn.microsoft.com/zh-cn/dotnet/core/rid-catalog)。

输出为[自包含部署](https://learn.microsoft.com/zh-cn/dotnet/core/deploying/)，已带 .NET 运行时，目标机器无需另装 .NET，但仍需满足目标系统的原生依赖要求。将 `dist/<RID>/` **整个目录**复制或压缩后分发，保留依赖库、Resources、Sqlite、Data/Pvf 和启动脚本，不能只取 `DfoServer.exe` 或 `DfoServer`。公开分发包不包含 PVF、玩家数据库或日志；若源码目录已有 PVF，发布时会复制它，打包前需排除。

### 3. 放入资源并启动

把自行准备的 `Script.pvf` 放在**发布目录**的 `Data/Pvf/Script.pvf`，例如 `dist/win-x64/Data/Pvf/Script.pvf`。然后只执行当前平台对应的启动命令：

```powershell
# Windows x64
./dist/win-x64/start-server.bat
```

```bash
# Linux x64；macOS / ARM64 替换目录中的 RID
bash ./dist/linux-x64/start-server.sh
```

已有自包含包的用户可以跳过构建，解压后在包内放入同一相对路径的 PVF，运行包内的 `start-server.bat` 或 `bash ./start-server.sh` 即可。

首次成功启动会创建数据库，并在程序目录下生成 `Logs/ready.json`。确认其中 PID 和端口对应本次进程；启动失败先看终端输出及 `Logs/server.jsonl`。前台退出使用 **Ctrl+C**。

### 4. 本机或局域网连接

默认地址为 `127.0.0.1`，频道端口 **TCP 23100**、游戏端口 **TCP 23101**。本机连接直接使用默认值；其他机器连接时，指定该机器可访问的服务端 IPv4，并确保两个端口均可达：

```powershell
./dist/win-x64/start-server.bat --server-ip auto
# 多网卡时可手动指定地址，替换为服务端实际 IPv4
./dist/win-x64/start-server.bat --server-ip 192.168.0.10
```

macOS/Linux 的 `start-server.sh` 接受相同参数。`auto` 自动选择活动网卡地址，多网卡环境应核对实际选择结果。开发 GM 端口默认关闭；全部参数、后台运行和抓包方法见[环境与配置](Docs/开发/环境与配置.md)。

现有 [GM 工具](Docs/开发/GM.md)仅用于开发调试。邮件系统实现后，将另行提供独立的用户 GM 工具。

## 运行目录与数据

以下路径均相对于**服务端程序所在目录**，不是终端当前目录：

```text
程序目录/
  DfoServer.exe 或 DfoServer
  DfoServer.dll 及其余依赖文件
  Resources/
  Sqlite/game_schema.sql    首次建库使用的完整结构
  start-server.bat / start-server.sh
  Data/
    Pvf/Script.pvf           自行提供
    game.db                 账号与全部游戏数据，首次启动创建
  Logs/
    server.jsonl            服务日志
    ready.json              本次启动信息
```

启动只使用命令行参数，不读取 JSON 运行配置；PVF、Data、Logs 路径固定。重复登录固定为 `ReplaceExisting`，验证凭据后替换旧连接，不校验客户端来源 IP。

账号和游戏资产统一保存在 `Data/game.db`，当前结构为 **v2**，沿用 v1 基线标识。新库按完整 SQL 建立；已有 v1 库连续迁移升级，失败整体回滚。升级前正常停服并备份 `Data/`。没有本项目基线标识的旧库不自动迁移或覆盖，使用独立程序目录创建新库。多个实例分别使用独立目录和端口；完整规则见[数据库初始化与升级](Docs/开发/数据库.md)。

## 开发构建

无需准备 PVF 即可运行基础检查：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Check-Development.ps1`（macOS/Linux 使用 `pwsh`）。它包含构建期分层检查、合成资源测试和 `--selftest-core`；完整资源集成与 TCP 验证要求见[贡献指南](CONTRIBUTING.md#验证)。

准备运行或自测时，可先把 PVF 放进源码的 [Data/Pvf](Server/DfoServer/Data/Pvf/README.md)，构建会将它复制到输出目录。以下方式直接运行 Debug 构建：

```powershell
dotnet build ServerS5A9.sln -c Debug --nologo
dotnet run --project Server/DfoServer -c Debug --no-build -- --help
# 准备好 PVF 后启动，按需记录应用层包
dotnet run --project Server/DfoServer -c Debug --no-build -- --packet-capture
```

输出在 `Server/DfoServer/bin/Debug/net10.0/`。日常调试用 `build` / `run`，分发用上面的 `publish`。仓库根的启动脚本会优先选择 `dist/<平台>/`，其次才是 Debug；开发时用上述 `dotnet run` 可明确运行刚编译的代码。具体阅读顺序、代码入口与验证要求见[贡献指南](CONTRIBUTING.md)。

## 项目导航

| 路径 | 内容 |
|---|---|
| [Server/DfoServer](Server/DfoServer) | 服务端主程序、业务、协议、持久化与内置 C# 测试 |
| [Tools](Tools) | 后台启停、仓库检查和可选开发 GM 网页工具 |
| [Docs/README.md](Docs/README.md) | 按模块查找业务、协议、资源和开发文档 |
| [AGENTS.md](AGENTS.md) | agent 阅读顺序、修改边界与交付要求 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 开发准备、源码导航、验证及协作流程 |

## 常见问题

| 现象 | 处理方法 |
|---|---|
| 找不到 `dotnet`，或 SDK 不支持 `net10.0` | 安装 .NET 10 SDK，重新打开终端，用 `dotnet --list-sdks` 确认 |
| NuGet 还原失败 | 检查网络和配置的 NuGet 源；项目依赖以 csproj 为准 |
| 构建成功，启动提示缺少 PVF | 检查实际运行程序旁的 `Data/Pvf/Script.pvf`；Linux 路径大小写也须一致 |
| 启动提示缺少建库 SQL | 完整复制发布目录，确认程序旁存在 `Sqlite/game_schema.sql` |
| 数据库基线或版本不受支持 | 核对程序与数据是否匹配；保留不兼容的数据库原文件，另用独立目录建库 |
| 发布包缺少运行库或 SQLite 原生库 | 完整复制对应 RID 的发布目录，不单独复制可执行文件 |
| 端口被占用或只能本机连接 | 核对进程、`--server-ip`、监听地址及两个 TCP 端口；参数示例见环境文档 |
| 提示已有 `stop.request` | 确认旧实例已退出，再移除本实例 `Logs/stop.request` 后启动 |

提交问题时附上所用命令、运行平台、错误文本和必要日志片段，不上传账号凭据或玩家数据库。
