# 协作开发指南

本指南面向开发者与 agent。只需要生成可运行文件时，直接使用 [README 的快速构建](README.md#快速构建可运行包)。开发目标为 S5A9，架构参考项目不提供本项目的协议编号、数据库结构或运行参数。

## 贡献许可

本项目的自有源码、工具和文档采用 **`AGPL-3.0-only`**，完整条款见 [LICENSE](LICENSE)。向本项目有意提交供合入的贡献，表示你同意将该贡献按此许可发布，并确认你有权作出这一授权。贡献者保留其著作权；本指南不要求著作权转让，也不授予额外的闭源再许可权。

引入或改写第三方代码时，须说明实际来源和许可证，保留原有版权、许可正文及必要的 NOTICE，并更新[第三方声明](THIRD-PARTY-NOTICES.md)和 [LICENSES](LICENSES)。只有参考算法或架构时，也应准确区分参考关系与实际代码复用。不得将他人的代码声明为本项目自有代码，或提交无权按相应许可分发的材料。

修改依赖或构建 SDK 后，核对实际解析版本和发布包中的组件，更新第三方清单与相应许可正文。自包含部署还须保留所用 .NET 运行时的第三方声明；升级运行时或更换 RID 时不能只沿用旧清单。

发布程序时，附带 `LICENSE`、`THIRD-PARTY-NOTICES.md`、`THIRD-PARTY.md` 和 `LICENSES/`，并按 AGPLv3 提供该版本的完整对应源码。直接执行 `dotnet publish` 的发布者须自行复制这些许可文件。修改版对外提供网络服务时，遵守 LICENSE 第 13 条的源码提供要求。欢迎向上游提交修复；履行 AGPLv3 并不要求必须将修改合入本仓库。

## 第一次开始开发

1. 阅读 [AGENTS](AGENTS.md)，确认当前目录、任务范围和已有修改。再读[服务端架构](Docs/规范/服务端架构.md)，按[文档索引](Docs/README.md)选择所属模块；只有改包体或资源规则时，才继续读对应协议和资源文档。涉及请求处理、锁、事务、批量计算或广播时，联查[并发、事务与响应延迟](Docs/规范/并发事务与响应延迟.md)。新增或修改流程时，按[日志约束](Docs/规范/服务端架构.md#日志约束)检查关键结果是否可定位，日志变更纳入对应功能的评审和验证。
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

采用 **Issue 登记 → 确认范围与认领 → 任务分支 → WIP PR → 验证与评审 → 合入关闭** 的流程。Issue 保存目标、负责人、依赖和进展，PR 保存实现与评审，模块文档保存最终行为。Codeberg 的负责人、标签和依赖功能见[Issue 使用说明](https://docs.codeberg.org/getting-started/issue-tracking-basics/)。

### 登记与范围

功能、行为修复、协议、数据库和架构调整先提 Issue。错别字、失效链接及纯文档整理可以直接提 PR，在描述中说明范围。登记前搜索当前代码、所属模块文档、开放及已关闭的 Issue 和 PR；已有同一问题时补充原 Issue，不重复建立实现任务。

使用[故障报告](.forgejo/ISSUE_TEMPLATE/bug_report.md)或[开发任务](.forgejo/ISSUE_TEMPLATE/development_task.md)模板，写清六项内容：目标、现状、范围、复用、依赖、验收。报告者不知道代码入口时可标注待排查，由认领者补全。涉及协议消费者或资源语义时区分已确认事实与待验证假设；性能任务给出测量输入和比较口径，不能只有“优化”作为验收标准。

维护者确认任务范围后标为可认领。跨模块、共享接口、schema 或新协议语义调整，先在 Issue 中写简短方案，说明状态所有者、事务边界、受影响调用方和验证方法；范围已经确认的普通任务可按现有合同实现，不必逐个实现细节等待审批。开发中新增目标或扩大公共边界时，先更新 Issue 并协调受影响负责人。

### 认领、状态与交接

每个可独立交付的 Issue 由一位主要负责人负责；使用 agent 的开发者仍对提交内容和验证结果负责。认领评论说明拟完成范围、影响模块、相关依赖和下一步。以维护者设置的负责人或明确确认的认领评论为准；没有分配权限时由维护者代为登记。多人同时认领由维护者协调，已有负责人时先协商分工。

开放的 Issue 使用以下状态，每次只保留一个；状态变更由负责人提出并同步记录，没有标签权限时用评论通知维护者更新。

| 状态标签 | 含义与下一步 |
|---|---|
| `状态/待确认` | 补复现、目标或方案，由维护者确认范围 |
| `状态/可认领` | 范围与验收明确，尚无负责人，可以申请认领 |
| `状态/开发中` | 已确认负责人，关联任务分支或 WIP PR，关键进展写回 Issue |
| `状态/待评审` | 当前范围实现完成，PR 与验证结果齐全，等待评审或约定的客户端验收 |
| `状态/阻塞` | 写明阻塞原因、依赖 Issue/PR 和恢复条件；仍保留负责人，解除后恢复原状态 |

暂停或退出任务时留下分支/PR、当前提交、已完成内容、未完成项、已运行检查及接手注意事项，再由维护者调整负责人。长期没有更新时先联系负责人，确认交接或明确通知释放认领后再分配；不能仅因几天没有回复就并行重做。完成后关闭 Issue，不额外维护“完成”标签；重复或不采纳的 Issue 关闭时说明原因并关联已有任务。

### 分支与并行开发

先检查 `git status --short --branch`、当前基线及他人修改；认领后从最新 `master` 建立任务分支，例如 `feat/123-mail-storage`、`fix/124-login`。外部贡献者可在 Fork 中开发并向本仓库提交 PR。同一需求的后续修复沿用原分支和 PR，首批有意义的提交后尽早创建标题带 `WIP:` 的 PR，并与 Issue 双向关联。未完成验收前保留 WIP 标记，不能为了开 PR 提交无关占位代码。

大功能使用总 Issue 跟踪可独立验收的子任务，不让多人各自实现整套系统。依赖未合并 PR 时写明基线、依赖关系及合入顺序；优先先合入公共能力，再开发调用方。明确采用依赖分支时，依赖更新或合入后须同步目标基线并重新检查最终差异，不能把依赖代码重复作为本次改动提交。

- 按模块和状态 owner 划分责任，不以“每人一个文件”替代业务边界。共享 Session、AccountOwners、Repository/schema、PacketTypes 和构建入口改动须在关联 Issue 中协调负责人。
- 两个任务需要同一公共能力时，拆出由一位负责人完成的依赖任务，调用方复用它；不要各建一套仓储、在线状态或工具框架。
- 数据库迁移按实际合入顺序协调编号；后合入的 PR 更新迁移序列和完整 SQL，并重新验证升级与回滚。已发布迁移不得重排。
- 并行开发使用独立 checkout/worktree、构建输出、程序目录和端口，Data/Logs 固定在各自程序目录下。保留他人修改，不用 reset、clean 或整目录覆盖清理未知内容。只有明确安排并行工作时才分派任务。

### 评审与合入

PR 使用[合入模板](.forgejo/PULL_REQUEST_TEMPLATE.md)，关联 Issue，说明最终行为、责任边界、协议/数据库影响及实际验证。记录被测提交、平台、命令、结果和未覆盖项；修改代码、同步基线或解决冲突后，按影响补测并更新结果，旧提交的日志不能冒充最终提交验证。

由作者之外的开发者或维护者审查；公共状态、数据库、协议和分层规则改动须由维护者复核，并按影响重跑关键检查。作者的测试结果和 agent 自评是评审材料，不能代替独立评审。所需检查按[验证](#验证)执行；缺资源或尚未实机验证时如实标注，由维护者确认是否满足该 Issue 的验收条件，不自动当作通过。

维护者确认范围、依赖、审查意见和验收条件全部闭合后合入，保持 `master` 可构建。一个独立任务通常使用 squash 合入，提交标题和正文用中文，保留 Issue 引用、最终行为与必要验证。只有完整解决目标时使用 `Closes #编号`；分阶段 PR 只关联对应子任务，不能提前关闭总 Issue。若需调整原验收范围，由维护者在 Issue 明确确认并拆出剩余任务。合入不自动等于部署、客户端验收或版本发布。

提交、推送、创建及更新 Issue/PR、合入均按任务授权执行。没有远端或工具时说明尚未登记的事项，不虚构认领、提交、Issue/PR 编号或 URL；可以先本地准备分析和草稿。交付通过 PR 合并，不直接向 `master` 提交或推送。不得夹带个人运行资产、凭据或无关格式化。

### 平台配置与模板

仓库管理员在 Codeberg 中配置五个状态标签，以及 `master` 的[分支保护](https://forgejo.org/docs/latest/user/repository/protection/)：限制直接推送和强推，仅维护者合入 PR，并要求独立评审。模块标签按实际任务需要增加，不预先建立无人维护的大量分类。平台权限、标签和保护规则须在平台单独设置，文档或模板存在不代表这些设置已经生效。

PR 创建、追加提交和重新打开时，由[无 PVF 检查工作流](.forgejo/workflows/tests.yml)使用 `codeberg-medium` 运行自动检查。工作流使用官方 .NET 10 Linux SDK 镜像，按 Debug、Release 分为两个独立配置 job，使用 `codeberg-medium`、每个 job 限时 8 分钟；该 runner 的公开规格上限为 10 分钟，避免完整构建、自测和收尾挤入 `codeberg-small` 的 5 分钟上限。两个配置可以并行调度，任一配置失败不跳过另一个，实际开始时间仍取决于公共 runner 队列。检出本次事件对应的 PR 源提交后，使用单个 MSBuild 节点构建，再分步骤从同一 `build/check/bin/<项目>/<配置小写>/` 产物直接运行分层正反例、PvfLib 合成测试、服务端 `--selftest-core` 和 `--selftest-tcp commands`，任一步失败均使该配置失败。CI 直接调用上述入口，不执行 `Tools/Check-Development.ps1` 的仓库链接/路径步骤；本地基础检查仍须按上述统一命令执行。检查只使用源码、合成输入和临时 SQLite，Fork PR 无需额外 secrets。该工作流不包含真实 PVF 的业务自测、完整业务 TCP、资源普查或客户端验收；本地验证仍按[检查范围](#检查范围)补齐。

完整 `--selftest` 与完整 TCP 需要真实 PVF，PVF 不入库，也不能放入公共 runner 或 secrets，因此不在 CI 中运行（仓库没有带 PVF 的自托管 runner），改由有 PVF 的本机手动执行：`pwsh -NoProfile -File Tools/Check-Development.ps1 -PvfPath <PVF>` 运行完整 `--selftest`，`pwsh -NoProfile -File Tools/Select-AffectedTests.ps1 -Run -Full -PvfPath <PVF>` 运行全量 TCP；具体命令见上方清单。合并前的验证结果写入 PR「验证」一节。

两个配置结束后，`no-pvf` 汇总 job 复用原有检查名称，使用 `codeberg-tiny`、轻量 Alpine 镜像和 `sh`，只在两个配置全部成功时通过；失败、取消、跳过或缺失结果均拒绝，不重复构建和自测。管理员须在仓库设置的 `Units > Overview` 中启用 Actions；工作流文件不会自动修改平台设置或分支保护。必需检查以实际配置为准，未启用或尚未验证可运行的 CI 状态不得作为必需检查阻塞所有 PR。

模板位于 `.forgejo/ISSUE_TEMPLATE/` 和 `.forgejo/PULL_REQUEST_TEMPLATE.md`，格式依据 [Forgejo 模板说明](https://forgejo.org/docs/latest/user/repository/issue-pull-request-templates/)；合入默认分支后供平台读取。Issue 模板不预设负责人或尚未建立的标签，也不自动完成认领。

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

重复开发检查可显式使用 `Tools/Check-Development.ps1 -Incremental`：仍检查全部仓库路径/链接、执行 MSBuild 增量构建、分层规则与 PvfLib 合成测试；只有无资源 `--selftest-core` 可以复用本工作树的成功记录。缓存同时核对全部已跟踪/未跟踪源码内容、配置、SDK/运行时与相关环境、实际输出文件内容，失败、中断、损坏记录或输入变化均不视为通过；运行中源码、产物或 SDK/环境改变则拒绝记录本轮成功。输出明确区分 `EXECUTED` 与 `CACHED`，复用时列出原收据路径和完成时间，开发缓存不是本轮完整验收。默认不加 `-Incremental` 仍实跑所有阶段，`-PvfPath` 与 `-Incremental` 不能同时使用。两个检查脚本对同一产物目录互斥，避免相互覆盖正在运行的程序集；不要在检查期间通过其他命令重建该目录。缓存保留在 `build/check`，不共享到 Git 或其他工作树。

脚本自身的缓存、失效、互斥及默认完整执行回归使用 `pwsh -NoProfile -File Tools/Test-DevelopmentChecks.ps1`，只使用临时合成仓库与命令替身，不需要 PVF，也不代表实际服务端测试已运行。

同时需要基础检查与完整资源自测时，传入 `-PvfPath <Script.pvf文件>`。脚本在构建和两个独立测试程序之后运行一次完整 `--selftest`，其中已包含 `--selftest-core`，无需再单独运行核心自测；各阶段输出实际耗时，任一步失败仍非零退出。此模式保留全部核心和资源覆盖，TCP 按影响范围另行执行。例如：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Check-Development.ps1 -PvfPath Server/DfoServer/Data/Pvf/Script.pvf
```

`--selftest-core` 覆盖纯协议、命令参数、owner、认证、数据库基线及背包变化索引；新增无资源测试接入 `SelfTestRunner.RunCoreAsync`。完整 `--selftest` 包含全部基础测试和业务资源集成，不能以基础检查替代下表要求的完整自测或 TCP。百万文件资源普查单独使用 `--selftest-tcp resource-survey`，扫描器或 PVF 格式、读取、缓存变化时补跑；其合成解析边界仍由基础自测覆盖。资源相关用例需要 `Server/DfoServer/Data/Pvf/Script.pvf`。从仓库根执行，固定目录和启动参数见[环境与配置](Docs/开发/环境与配置.md)：

```powershell
dotnet build ServerS5A9.sln -c Debug --nologo
dotnet run --project Tools/PvfLib.Tests -c Debug --no-build
dotnet run --project Server/DfoServer -c Debug --no-build -- --selftest-core
dotnet run --project Server/DfoServer -c Debug --no-build -- --selftest
dotnet run --project Server/DfoServer -c Debug --no-build -- --selftest-tcp
# 定向 TCP 回归；多个组用逗号连接。多组时按上次耗时分片到 DFO_TCP_PARALLEL 个驱动子进程并行（默认 min(4, 逻辑核/2)，=1 串行），每个分片共享一份只读资源。
dotnet run --project Server/DfoServer -c Debug --no-build -- --selftest-tcp "material-exchange,booster"
# 全量资源普查，按需执行。
dotnet run --project Server/DfoServer -c Debug --no-build -- --selftest-tcp resource-survey
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Check-Repository.ps1
# 以下命令仅适用于 Git 工作区
git diff --check
```

macOS/Linux 的仓库检查改用 `pwsh -NoProfile -File Tools/Check-Repository.ps1`。该脚本检查路径可移植性和文档本地链接；修改标题时还要检查引用的锚点。

### 增量结果与覆盖收据

跨工作树复用的隔离边界、TCP结构化收据与计时解释见[增量测试与收据](Docs/开发/增量测试.md)。`Passed` 不等于全部条件场景已执行：部分跳过或未知覆盖不能作为完整通过缓存。该流程不替代下述严格完整验证，也不允许共享活跃 `bin/obj`、数据库或测试实例。

### 检查范围

进程树预算需分别考虑共享资源驱动和真实子进程。`DFO_TCP_CHILD_GC_HEAP_HARD_LIMIT` 可为 fixture 创建的服务与 CLI 子进程覆盖 `DOTNET_GCHeapHardLimit`，使用正数十六进制字节数（可带 `0x`）。未设置或空值沿用原环境，非法值在启动前失败。驱动的 GC 设置仍由本轮启动配置决定；按相同输入和覆盖实际校准，并记录父子预算与进程树峰值。

**测试分层。** 开发内循环 = 增量 Debug 构建 + `--selftest-core` + 受影响 TCP 组：`pwsh Tools/Select-AffectedTests.ps1 [-Run -PvfPath <PVF>]` 按与 `origin/master`（不存在时为 `master`）的差异选组，并输出按历史耗时估算的串行/并行时间。映射依次使用注册表中的测试类、路径关键词，以及引用该类型的 TCP 测试类；非 TCP 的自测文件只由 `--selftest-core`/完整 `--selftest` 覆盖。公共测试框架或 TCP 入口（`TcpPeer`、`TcpTestFixture`、`TcpTestResources`、`TcpRegressionTests` 等）变化在开发期运行固定冒烟组；迁移或表结构变化运行 `database-schema`、`settings-storage`、`inventory-persistence` 与 `startup`；程序入口、`Application/`、`Infrastructure/`、`Network/Core/`、项目文件、PvfLib、自测入口、引用过广或无法映射的代码变化直接全量。`-Run` 始终执行 MSBuild 增量构建，仅在源码内容、SDK/环境及实际产物完全匹配时复用已通过的 `--selftest-core`；`-NoCache` 与 `-Full` 均强制重新执行核心自测。工具脚本变化提示执行对应脚本/参数回归，不以文档检查代替；共享项目配置、资源文件及无法映射的输入保守全量，纯文档仍不触发业务测试。TCP 运行期间的 `DFO_TCP_TIMINGS` 覆盖在成功或失败后恢复，不污染下一轮核心检查指纹。选择映射是开发期的保守辅助，不是完整的传递依赖证明，不能据此复用资源/TCP 测试通过结果。TCP 的历史耗时不是通过收据，目前不自动复用 TCP 或资源测试结果。同源码、同程序集的人工断点续跑须另行核对原始资源及配置证据，保留原失败与补跑的归属，不能报告为一次连续全量通过；缺可靠资源版本证据时不得自动命中，不能用文件长度、时间戳或整包 PVF 哈希代替。合入前用 `-Run -Full` 运行一次完整 TCP，并用 `Tools/Check-Development.ps1 -PvfPath <PVF>` 运行一次完整 `--selftest`；冒烟或定向选择不能代替这两项。同一台机器同时运行测试的工作树不超过 2 个；TCP 并行度按 `DFO_TCP_PARALLEL` 调低。

开发迭代先完成 Debug 构建、基础检查及修改点和受影响链路的定向测试；不因一次局部修改反复运行全量资源与 TCP。最终提交、推送或整项交付前按下表补齐验证；相同代码与输入已通过的检查可复用，后续修改、失败或影响范围不明时按影响补测。公共测试夹具、资源共享或测试入口变化在开发期运行冒烟组，交付前须运行完整 TCP，不能只测一个受益组。服务端构建要求 `0 warning / 0 error`。

| 改动范围 | 至少完成的验证 |
|---|---|
| 纯文档 | 核对内容、路径、链接与标题锚点；仓库检查；有 Git 时检查 diff |
| Parser、领域规则或单个功能 | Debug 构建、完整 `--selftest`、所属 TCP 组；补成功、拒绝与边界用例 |
| PvfLib 格式、读取、缓存或全量扫描器 | 独立合成测试、完整服务端 `--selftest`、`--selftest-tcp` 和 `resource-survey` 专项；性能改动按[库文档](Tools/PvfLib/README.md#构建与验证)同条件测量 |
| 公共 Session、owner、Repository、Builder、注册或广播路径 | Debug 构建、完整 `--selftest` 和完整 `--selftest-tcp`，复查共享调用方 |
| schema 或持久化变化 | 在上述范围外覆盖新建库、升级、失败回滚、重开库、存量恢复及较新版本拒绝 |
| 启动、脚本、依赖或发布配置 | 对应脚本/参数验证、Release 发布、输出目录及目标平台启动检查 |

测试以非零退出码表示失败，控制台给出用例结果；TCP 还会输出 fixture 路径及 `result.json`。资源样本断言失败时区分输入差异与实现回归，不以删除合法性检查或缩小原有用例来让测试通过。未准备资源、未运行的组、已有失败和未完成的实机验收都应在交付中明确。

### 测试产物保留与清理

每轮 TCP 回归生成独立的 `build/tcp-tests/<GUID>`，运行期间以 `.running` 占用标记保护。以下回收属于默认授权，无需另行确认：

- **通过的轮次**：记录各组耗时到 `build/tcp-tests/timings.json`（供并行分片均衡）后整轮删除；设置 `DFO_TCP_TIMINGS=<文件>` 时同时合并写入该共享文件，多个工作树共用历史耗时。`Tools/Select-AffectedTests.ps1` 默认使用 `%LOCALAPPDATA%/S5A9/tcp-timings.json`（其他平台为 `~/.cache/s5a9/tcp-timings.json`），不写入任何工作树。需要保留成功现场时设置 `DFO_KEEP_TCP_ARTIFACTS=1`。
- **失败、取消或中断的轮次**：保留现场。每轮 TCP 启动时自动裁剪：只保留最近 `DFO_TCP_KEEP_FAILED`（默认 5）个、`DFO_TCP_KEEP_DAYS`（默认 3）天内、合计不超过 `DFO_TCP_MAX_GB`（默认 5 GB，不计硬链接的 PVF）的失败现场；最新一个失败现场始终保留。旧版本遗留的已通过轮次一并删除。
- 手动清理用 `Tools/Clean-TestArtifacts.ps1`（`-WhatIf` 预览，`-All` 删除全部非运行中轮次），规则与自动回收一致。

回收只处理当前工作树 `build/tcp-tests` 下 GUID 命名的直接子目录，跳过运行中的轮次，不跟随符号链接或 junction，不触碰其他工作树、会话目录和正式运行的玩家数据；共享资源使用硬链接时只删除链接。清理失败会报告警告并保留剩余文件。

普通编译尽量复用同一任务的输出路径，避免为相同输入反复创建发布副本；`Tools/Build-Server.ps1` 固定使用 `build/publish` 并保留增量编译产物。测试不得为减少磁盘占用删减原有覆盖。目录文件长度相加会重复计入硬链接，评估实际占用和可释放空间时应按文件身份及剩余链接核对。

### 测试代码约束

以下要求适用于新增、修改和精简测试；评审以独有风险覆盖、可读性和执行成本为准，不以文件数、断言数或测试代码行数作为完成指标。

1. **先查覆盖，再加用例。** 每个新增用例应对应明确的失败行为或边界，优先扩展所属模块已有用例。纯规则、Parser、Builder 和 schema 使用最小合成输入；真实 PVF 只用于资源合同和必要业务集成。可逆、低风险且无独立行为的改动不机械新增测试，不为逐行复刻实现而写测试。
2. **按层分工，避免整套重复。** 必要组合矩阵放在纯规则层，事务与落库边界放在 Session/Repository 层；TCP 聚焦真实请求分派、完整帧/CRC/sequence、包序、保存回放和失败后的连接行为。TCP 不得再次调用 `--selftest-core` 或整套领域自测，也不搬运完整规则矩阵；仅对跨层交互选代表性成功、拒绝和关键边界。已在下层验证的条件，只有出现独立集成风险才增加 TCP 覆盖。
3. **保持代码精简。** 服务端用例使用 C#，沿用现有程序集和命令入口；复用 `TestCheck`、`TcpPeer`、`TcpTestFixture` 的断言、wire 和 SQL 辅助，不在各模块复制同类函数。相同流程用简短数据表表达，确有重复才抽取小辅助；不引入通用测试框架、DSL、空包装或另一套业务实现，也不为压行数牺牲可读性。协议预期须独立编码，禁止调用被测生产 Builder 生成同一包的预期值。
4. **公共只读资源按轮复用。** 相同 PVF 的归档、物品、角色、城镇和技能目录须通过现有 `TcpTestResources` 按需共用；自测中按同一 archive 构建的只读目录（任务、副本、房间、成长、技能学习等）通过 `SelfTestCatalogs` 取共享实例，需要改动定义时先复制再构建自己的目录，驱动与同进程服务不得各自重载一份。业务 TCP 复用 `ServerRuntime` 装配真实监听，每组隔离数据库、owner、会话、日志和端口；不能共享可变角色或账号状态、依赖前一组数据，也不能用跨轮全局缓存掩盖初始化问题。资源所有者在整轮结束后释放。
5. **按验证目标选择重连、服务重建或进程重启。** 提交回放优先断线重连；需要清空在线状态时重建服务、会话和仓储，仍共享只读资源。只有 CLI、固定目录、启动生命周期、抓包及代表性的持久化冷启动使用独立进程。当前公共背包、技能、正式副本、角色设置及启动组保留真实进程恢复；新增冷启动须说明现有用例不能覆盖的独立风险，不为每个功能重复启动。服务重建不得报告为跨进程恢复。
6. **等待须有可观测条件。** 用 ACK、就绪状态或同步信号确定先后关系，等待设置超时；不得用固定睡眠代替完成条件，或用超时无包推断静默。故障注入和压力规模按本次风险选择，不把长延时、多档相同等待或全资源扫描作为普通回归的默认成本；确需这些输入时保留其独立验证目的。
7. **精简必须保留风险覆盖。** 删除或合并用例时说明重复点与保留的覆盖位置；成功、拒绝、重复/过期请求、旧 generation/revision、真实 SQL 回滚、完整资产/UID、回放和迁移等受影响边界按层保留，不要求每层重复一遍。不得为提速跳过失败组、放宽期望、吞异常、随意缩小合法输入范围或隐藏覆盖减少；发现失败先区分测试缺陷、输入差异与业务回归。
8. **执行与资源必须有界。** 构建、完整资源自测和 TCP 分阶段执行，运行期间不得重建共享产物；TCP 组可按分片在独立驱动子进程中并行，组内仍串行；按[资源预算与停止边界](Docs/规范/并发事务与响应延迟.md#验证与停止边界)控制时间、内存和进程数，越界停止排查。子进程输出流式写日志，CLI 读取与错误摘要保持现有限额；成功、失败和取消均须回收本次连接、监听、任务及子进程。不得连接实际运行实例或启动 DNF 客户端，不以并发重复加载、强制 GC 或反复重跑掩盖成本和泄漏。

测试结构、资源复用或性能调整须在同配置、同输入、同选组条件下比较总耗时、进程树峰值内存及冷启动次数，同时报告测试有效代码量的增减（排除空行与纯注释）。代码增长应对应新增的独有覆盖或必要夹具支持，不设机械的测试/业务行数比例，也不把某台机器的一次耗时写成固定门槛。覆盖范围变化单独说明，不能当作等量提速。

定向组名以 [TCP 入口](Server/DfoServer/SelfTests/Tcp/TcpRegressionTests.cs)为准；省略组名执行全部网络集成组。原 `quests` 已拆为互不共享数据库的 `quest-lifecycle`、`quest-extras`、`quest-chains`、`quest-chain-settlement`、`quest-metro` 五组，`quests` 仍作为五组的别名；原 `packet-capture` 的 HTTP 只读入口部分拆为 `packet-http`。`dungeon-settlement`、`inventory-persistence` 是已有自测的定向入口，`resource-survey` 是全量普查，均需显式指定。每组结果记录断言数、耗时、本组触发的共享资源加载耗时（`ResourceLoadMs`）、fixture 启动到就绪的次数与耗时（`FixtureStarts`/`FixtureStartMs`）及驱动提交量、工作集、期间分配量，内含共享资源和同进程服务；独立进程阶段的 `memory.json` 记录工作集峰值与停服前提交量。这些指标不等价于泄漏证明或进程树硬上限。

`quest-hell-challenge` 默认保留深渊与魔兽资格任务的完整 TCP 矩阵。匹配资源缺少魔兽 5927 会面所需 town31 默认场景 NPC598 时，仅可显式选择 `quest-hell-challenge-without-beast-meeting`：它报告该正向场景 `SKIP`，仍执行深渊场景、5927 缺前置拒绝及哈林/阿尔伯特后续检查；若依赖存在则照常执行正例。该定向结果不等于默认完整组通过，不捕获或忽略解析、SQL、协议等其它失败。

Windows、macOS 和 Linux 共用入口，仅报告实际运行的平台。纯文档修改只检查链接、路径、锚点、内容与 diff，不要求构建、自测或服务重启；缺少匹配资产时说明未执行项。

模块文档维护测试入口和验收边界，不逐轮追加测试数量、日志路径或进程身份。构建、自测、TCP、客户端请求与界面确认分别报告；自动化通过不等于实机通过。

## 文档与交付

维护规则以[文档归属](Docs/README.md#文档归属与修改规则)为准。必要字段、协议约束及设计原因进入对应模块/协议/资源文档，计划、排错历程和开发记忆留在本地。普通功能修复不要求同步改写 README、索引、公共规范或状态总表。

交付与 PR/MR 描述面向未参与对话的审阅者，说明问题和最终行为、相关代码、协议/事务/迁移影响、实际验证及未覆盖项。按影响决定篇幅，不机械列举无变化的项目。运行配置、客户端/PVF、玩家库、日志、dump 和发布产物不入库，也不把个人数据库作为发行初始库。

交付前核对修改范围和输出文件。说明本次行为变化、实际执行的构建/自测/TCP 命令与结果、实机验证边界，以及是否提交或创建 PR/MR。一个改动围绕一个问题组织；避免混入无关格式化、临时研究文件和生成产物。agent 不因完成修改而自动获得部署、重启实际联调实例或对外发布权限。
