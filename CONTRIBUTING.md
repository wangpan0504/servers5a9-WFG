# 协作开发指南

本指南面向开发者与 agent。只需要生成可运行文件时，直接使用 [README 的快速构建](README.md#快速构建可运行包)。开发目标为 S5A9，架构参考项目不提供本项目的协议编号、数据库结构或运行参数。

## 第一次开始开发

1. 阅读 [AGENTS](AGENTS.md)，确认当前目录、任务范围和已有修改。再读[服务端架构](Docs/规范/服务端架构.md)，按[文档索引](Docs/README.md)选择所属模块；只有改包体或资源规则时，才继续读对应协议和资源文档。涉及请求处理、锁、事务、批量计算或广播时，联查[并发、事务与响应延迟](Docs/规范/并发事务与响应延迟.md)。
2. 安装 .NET 10 SDK，确认 NuGet 源可访问。服务端使用 SQLite，无需独立数据库服务；构建和 C# 测试入口不依赖 Python。编辑器可直接打开根目录的 `ServerS5A9.sln`。
3. 从仓库根完成一次构建与命令行检查：

   ```powershell
   dotnet build ServerS5A9.sln -c Debug --nologo
   dotnet run --project Server/DfoServer -c Debug --no-build -- --help
   ```

4. 准备资源相关测试或启动服务时，将自行提供的 `Script.pvf` 放入 `Server/DfoServer/Data/Pvf/Script.pvf`，重新构建以复制到输出目录。缺少资源时仍能编译和查看帮助，但不能据此声称业务自测或启动通过。
5. 运行下面的基础验证。开始修改前记录已有失败与所用输入；改完按[验证](#验证)补齐本次影响范围。

   ```powershell
   dotnet run --project Server/DfoServer -c Debug --no-build -- --selftest
   dotnet run --project Server/DfoServer -c Debug --no-build -- --selftest-tcp "commands,startup,native-login"
   ```

6. 需要实际联调时，使用明确的开发输出启动，并查看该程序目录下的 `Logs/ready.json` 与 `Logs/server.jsonl`：

   ```powershell
   dotnet run --project Server/DfoServer -c Debug --no-build -- --packet-capture
   ```

`--no-build` 只运行已有输出；修改代码或新增源码目录下的 PVF 后必须先重新构建。根启动脚本会优先查找 `dist/<平台>/`，开发调试建议直接使用上面的 `dotnet run`，避免误跑较旧发布包。默认开发数据位于 `Server/DfoServer/bin/Debug/net10.0/Data/`；详细参数和实例管理见[环境与配置](Docs/开发/环境与配置.md)。

## 快速定位代码

以下目录均位于 `Server/DfoServer/`；此处提供查找入口，完整依赖与状态规则见[服务端架构](Docs/规范/服务端架构.md)。

| 要修改的内容 | 从哪里开始 |
|---|---|
| 启动命令与参数 | [Program.cs](Server/DfoServer/Program.cs) → [ServerApplication](Server/DfoServer/Application/ServerApplication.cs)、[ServerCommand](Server/DfoServer/Application/ServerCommand.cs)、[ServerOptions](Server/DfoServer/ServerOptions.cs) |
| 依赖装配、模块注册与退出 | [ServerRuntime](Server/DfoServer/Infrastructure/ServerRuntime.cs)、[ServerLifetime](Server/DfoServer/Infrastructure/ServerLifetime.cs) |
| 收发帧、opcode、请求分派 | [Network/Core](Server/DfoServer/Network/Core)、[PacketTypes.cs](Server/DfoServer/Network/Core/PacketTypes.cs)、[Network/Protocol](Server/DfoServer/Network/Protocol) |
| 请求解析与处理 | [Network/Parsers](Server/DfoServer/Network/Parsers)、[Network/Handlers](Server/DfoServer/Network/Handlers) |
| 会话、业务规则与资产计算 | [Game](Server/DfoServer/Game)，优先查所属领域的 Models / Services 及现有 Session 用例 |
| PVF 格式与资源目录 | 独立 [PvfLib](Tools/PvfLib/README.md)负责归档、token 和通用脚本读取；[GameWorld](Server/DfoServer/GameWorld)负责类型化资源、目录和查询 |
| 数据库、事务与迁移 | [Persistence](Server/DfoServer/Persistence)，初始化与迁移入口见[数据库文档](Docs/开发/数据库.md)；完整结构在 [Sqlite/game_schema.sql](Server/DfoServer/Sqlite/game_schema.sql) |
| 包体构造与发布顺序 | [Network/Builders](Server/DfoServer/Network/Builders)、[Network/Projection](Server/DfoServer/Network/Projection) |
| 自测注册与 TCP 分组 | [SelfTestRunner](Server/DfoServer/SelfTests/SelfTestRunner.cs)、[TcpRegressionTests](Server/DfoServer/SelfTests/Tcp/TcpRegressionTests.cs) |

第一次追请求可以看城镇移动：注册于 [TownHandler](Server/DfoServer/Network/Handlers/TownHandler.cs)，由 [TownRequests](Server/DfoServer/Network/Parsers/TownRequests.cs)解析，交给 [GameSession](Server/DfoServer/Game/Session/GameSession.cs)验证并保存位置，成功提交后通过 [TownProjection](Server/DfoServer/Network/Projection/TownProjection.cs)发布。对应回归在 [TownProtocolTcpTests](Server/DfoServer/SelfTests/Tcp/TownProtocolTcpTests.cs)。它展示分层调用，不代表所有请求都采用同一种回复形式。

背包、装备、仓库、商店、奖励和货币先读[背包与装备](Docs/模块/背包与装备.md)，扩展相关玩法再读[物品扩展规范](Docs/规范/物品扩展.md)。已有物品服务与提交入口优先复用，不从 Handler 直接改表或自行维护另一份在线背包。

## 任务和分支

有 Git 元数据时，先检查 `git status --short --branch`、当前分支与基线；每个独立代码任务使用任务分支，同一需求的修复沿用原分支及已有 PR/MR。依赖未合并分支时写明基线。从源码压缩包开始时，Git 命令仅在建立 Git 工作区后适用。保留他人修改，不用 reset、clean 或整目录覆盖清理未知内容。

按模块和状态 owner 分工；共享 Session、AccountOwners、Repository/schema、PacketTypes 和构建入口需协调。各开发实例使用独立程序目录和端口，Data/Logs 固定在各自程序目录下。只有明确安排并行工作时才分派任务。

提交、推送、创建及更新 PR/MR 以本次授权为准，不自动发布。提交应可独立审查，使用中文标题与正文，说明具体问题、最终行为和必要验证；不夹带无关格式化、个人运行资产或凭据。没有远端或工具时如实报告，不虚构提交、MR 编号或 URL。交付通过 PR/MR 合并，不直接向 master/main 提交或推送。

## 实现与审查

沿用[程序集与访问边界](Docs/规范/服务端架构.md#程序集与访问边界)：服务端保持模块化单体，分层修改落实在职责、合同与依赖方向，不要求拆分项目。评审同时检查新增跨层调用、访问范围、测试依赖和状态所有权；不能仅以类型可访问或构建通过认定业务边界正确。现有自测入口继续使用，分析器规则变更须保留正反例验证，不通过禁用或排除目录绕过约束。

先明确一次操作的输入、资格、费用、结果和失败行为，再沿现有同类用例接入。新增请求的通常落点为：

1. 从 `PacketTypes.cs` 确认枚举，在 Parser 保留完整字段并验证包体结构。
2. 在所属 Game 领域实现规则；新增资源规则放 GameWorld，不在 Handler 中扫描 PVF。
3. 经当前 Session/owner 调用持久化接口；同一次费用、资产和进度变化在一个事务提交。
4. Builder/Projection 使用提交结果构包，Handler 组织回复与发布；有新模块时在 `ServerRuntime` 显式装配并注册。
5. 将对应测试接入 `SelfTestRunner` 或 TCP 分组入口，同步所属模块文档；字节合同变化再改对应协议文档。

按 Parser → Handler → Session/领域 → Repository → Builder/Projection 接入有实际功能的代码。明确 owner/generation、角色 revision、同事务修改集合、失败恢复和提交后通知。跨表费用、资产、进度与一次性资格必须原子提交；发送失败不重跑已提交业务。

协议修改记录 direction/category/opcode、宽度、编码、包序、成功与失败分支和匹配消费者。不能因其他版本编号或名字相同就复制 wire。PVF 规则与已有行为应完整保留，不能擅自增加快捷栏类别、数量或职业限制；存在版本差异时用当前代码、匹配资源与客户端证据说明理由。

新增代码注释用中文，解释必要原因与约束。遵循 `.editorconfig`，展开多条操作与控制流，不为单一调用引入通用框架。改完检查共享调用方、旧错误和存量数据兼容。

账号和游戏共用 `Data/game.db`，当前数据库基线为 v1。后续持久化变更在 `SqliteMigrations` 从 v2 连续追加迁移，同时更新完整 SQL 与结构检查；具体维护规则见[数据库文档](Docs/开发/数据库.md)。保持已发布版本的数据，不重排迁移或通过删库绕过兼容错误。锁内只保留必要状态核对和提交；资源解析、批量规划及网络等待遵循并发规范，不能让旧 generation 发布新会话结果。未知字段、错误码和未实现请求保持明确边界，不返回通用成功。

## 验证

业务测试内置在服务端中，PvfLib 使用独立的合成测试程序；`dotnet test` 不是本项目自测入口。无需 PVF 即可运行统一基础检查：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Check-Development.ps1
# macOS/Linux
pwsh -NoProfile -File Tools/Check-Development.ps1
```

脚本依次检查仓库链接与路径、构建（含分层分析器）、分层规则正反例、PvfLib 合成数据和服务端 `--selftest-core`，失败即非零退出。默认产物写到 `build/check`，可用 `-Configuration Release` 或 `-ArtifactsPath <目录>` 调整；测试只使用合成输入与临时 SQLite，不要求运行数据，也不启动游戏监听。该命令可直接用于本地或 CI。

`--selftest-core` 覆盖纯协议、命令参数、owner、认证、数据库基线及背包变化索引；新增无资源测试接入 `SelfTestRunner.RunCoreAsync`。完整 `--selftest` 包含全部基础测试和资源集成，不能以基础检查替代下表要求的完整自测或 TCP。资源相关用例需要 `Server/DfoServer/Data/Pvf/Script.pvf`。从仓库根执行，固定目录和启动参数见[环境与配置](Docs/开发/环境与配置.md)：

```powershell
dotnet build ServerS5A9.sln -c Debug --nologo
dotnet run --project Tools/PvfLib.Tests -c Debug --no-build
dotnet run --project Server/DfoServer -c Debug --no-build -- --selftest-core
dotnet run --project Server/DfoServer -c Debug --no-build -- --selftest
dotnet run --project Server/DfoServer -c Debug --no-build -- --selftest-tcp
# 定向 TCP 回归；多个组用逗号连接，同一轮仍按注册顺序串行执行。
dotnet run --project Server/DfoServer -c Debug --no-build -- --selftest-tcp "material-exchange,booster"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Check-Repository.ps1
# 以下命令仅适用于 Git 工作区
git diff --check
```

macOS/Linux 的仓库检查改用 `pwsh -NoProfile -File Tools/Check-Repository.ps1`。该脚本检查路径可移植性和文档本地链接；修改标题时还要检查引用的锚点。

| 改动范围 | 至少完成的验证 |
|---|---|
| 纯文档 | 核对内容、路径、链接与标题锚点；仓库检查；有 Git 时检查 diff |
| Parser、领域规则或单个功能 | Debug 构建、完整 `--selftest`、所属 TCP 组；补成功、拒绝与边界用例 |
| PvfLib 格式、读取或缓存 | 独立合成测试、完整服务端 `--selftest` 和 `--selftest-tcp`；性能改动按[库文档](Tools/PvfLib/README.md#构建与验证)同条件测量 |
| 公共 Session、owner、Repository、Builder、注册或广播路径 | Debug 构建、完整 `--selftest` 和完整 `--selftest-tcp`，复查共享调用方 |
| schema 或持久化变化 | 在上述范围外覆盖新建库、升级、失败回滚、重开库、存量恢复及较新版本拒绝 |
| 启动、脚本、依赖或发布配置 | 对应脚本/参数验证、Release 发布、输出目录及目标平台启动检查 |

测试以非零退出码表示失败，控制台给出用例结果；TCP 还会输出 fixture 路径及 `result.json`。资源样本断言失败时区分输入差异与实现回归，不以删除合法性检查或缩小原有用例来让测试通过。未准备资源、未运行的组、已有失败和未完成的实机验收都应在交付中明确。

- 服务端构建要求 `0 warning / 0 error`；服务端用例全部使用 C#。公共 Session/Repository/Builder 变化运行完整自测和 TCP 回归；工具变化增加相应语法、参数或 fixture 检查。
- 状态变化覆盖成功、拒绝、重复请求、旧 generation、revision 冲突、数据库失败回滚、重登/重启及迁移。网络变化检查完整帧、CRC、sequence、包序和失败后的连接行为。
- [TCP 入口](Server/DfoServer/SelfTests/Tcp/TcpRegressionTests.cs)启动独立 .NET 进程，使用 loopback 空闲端口与独立程序目录，按固定布局隔离 Data/Logs，输出到忽略的 `build/tcp-tests`；不连接实际运行实例，不启动 DNF。客户端独立编码请求，用后续确定的 ACK 检测多发/漏发，不以超时推断静默。
- TCP 驱动按需共享一份 PVF/城镇/角色目录，不为每个用例重新打开归档；独立服务进程保留自身资源格式解析。子进程 stdout/stderr 固定缓冲写入 fixture 日志，CLI 返回值最多读入 64 KiB，错误摘要最多 4096 字符。启动失败也立即结束并等待本 fixture 的进程树与输出任务，不以 GC 或反复重跑解决资源累积。
- 定向组名以 [TCP 入口](Server/DfoServer/SelfTests/Tcp/TcpRegressionTests.cs)的注册表为准，所属模块文档列出对应命令；省略组名才执行全部。每组结果记录驱动提交量、工作集和累计分配量；各服务阶段的 `memory.json` 记录工作集峰值与停服前提交量。这些指标用于发现重复加载和增长，不等价于泄漏证明或进程树硬上限。资源不足立即停止并排查，不连续重试。
- 共享构建输出的测试串行运行。Windows、macOS 和 Linux 共用入口，macOS/Linux 的仓库检查使用 `pwsh`；仅报告实际运行的平台。发行或构建配置变更增加对应 Release 验证。
- **纯文档修改**只检查内部链接、路径、内容与源码的一致性及 diff，不要求 C# 测试或服务重启。缺少匹配资产时说明未执行的检查。

模块文档维护测试入口和验收边界，不逐轮追加测试数量、日志路径或进程身份。构建、自测、TCP、客户端请求与界面确认分别报告；自动化通过不等于实机通过。

## 文档与交付

维护规则以[文档归属](Docs/README.md#文档归属与修改规则)为准。必要字段、协议约束及设计原因进入对应模块/协议/资源文档，计划、排错历程和开发记忆留在本地。普通功能修复不要求同步改写 README、索引、公共规范或状态总表。

交付与 PR/MR 描述面向未参与对话的审阅者，说明问题和最终行为、相关代码、协议/事务/迁移影响、实际验证及未覆盖项。按影响决定篇幅，不机械列举无变化的项目。运行配置、客户端/PVF、玩家库、日志、dump 和发布产物不入库，也不把个人数据库作为发行初始库。

交付前核对修改范围和输出文件。说明本次行为变化、实际执行的构建/自测/TCP 命令与结果、实机验证边界，以及是否提交或创建 PR/MR。一个改动围绕一个问题组织；避免混入无关格式化、临时研究文件和生成产物。agent 不因完成修改而自动获得部署、重启实际联调实例或对外发布权限。
