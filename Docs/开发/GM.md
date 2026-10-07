# 开发 GM

本工具只服务开发期调整已有角色的存档。它不新增游戏 opcode，不代表已经实现升级、转职、任务领奖、技能学习或物品发放。客户端没有 GM 面板；命令在服务进程内执行，或在服务停止后直接写 `game.db`。

当前命令行、管理端口及网页均属于开发调试工具。面向用户的独立 GM 工具将在邮件系统实现后另行提供。

## 1. 能改什么

| 命令 | 写入 | 范围 |
|---|---|---|
| `list` | 否 | 当前账号的角色摘要 |
| `show <角色>` | 否 | 单个角色的 id、槽位、job、grow、等级、剧情接受等级、revision、位置、返回点、任务数、技能数 |
| `quests <角色>` | 否 | 已完成任务编号，按编号升序 |
| `skills <角色>` | 否 | `技能:等级`，按技能编号升序 |
| `level <角色> <等级>` | 是 | 1–95 |
| `story <角色> <等级>` | 是 | `accepted_story_level`，0–95 |
| `grow <角色> <字节>` | 是 | `grow_type`，0–127 |
| `quest-add <角色> <任务>` | 是 | 1–29999；重复添加保持一条 |
| `quest-del <角色> <任务>` | 是 | 删除一条；本来没有也成功 |
| `quest-clear <角色>` | 是 | 清空该角色的完成任务 |
| `skill <角色> <技能> <等级>` | 是 | 技能 0–65535，等级 1–255 为设置；等级 0 删除。新技能最多 255 条 |

默认账号是 `10000`，与原生登录的开发账号一致。其他账号在命令前加 `account <账号>`。账号只允许字母、数字、`_` 和 `-`，最长 64。角色名沿用 CP936 名称合同。

这些写入不改 job、位置、返回点或 revision。选角仍会把出生点设回 PVF 房间，所以这里不提供传送。等级和 grow 不重算 89 字节初始属性，也不自动补技能。本工具没有物品、金币或仓库资产的发放命令；这些资产的持久化和业务入口见[背包与装备](../模块/背包与装备.md)。

## 2. 在线与离线

省略 `--gm-port` 或传入 0 时，服务不监听开发端口。在线调整时，启动参数使用与频道、游戏端口都不同的端口，例如：

```powershell
./start-server.bat --gm-port 23102
```

服务只在 `127.0.0.1` 上接受一行 UTF-8 命令并回一行结果。它不跟随 `ListenAddress`，也不向游戏客户端发 GM 包。隔离测试默认关闭 GM，相关专项测试使用各自的空闲端口。

同一条命令的两条路径：

1. **服务正在运行且 GmPort 已打开。** 命令连到本机端口。角色若已在当前连接里选中，提交后的等级和任务会立刻用于服务端通行校验；客户端画面仍显示选角时收到的投影。回到选角并再次进入后，名册、Actor、Player、356 和 1382 才按新存档刷新。
2. **服务没有运行。** 命令取得程序目录下 `Data/server.lock` 后写 `Data/game.db`。下次进入游戏时从数据库加载。若锁被占用，说明实例仍在运行：命令前传入与实例一致的 `--gm-port`，或停止该实例后执行。

在线写入走该账号当前的 owner。命令使用 `Reject`，不会把正在游戏的连接挤下线。旧 generation 的会话不能写入。进度列和位置保存不共用一次 revision 递增，因此城镇中的移动仍按原来的 revision 提交；开发写入若发现 revision 或账号已经变化，则整次事务失败。

成功行以 `OK ` 开始。失败行以 `ERR ` 开始，进程退出码为 1。`help` 只打印用法。

代码按职责分为 [DevelopmentRequest](../../Server/DfoServer/Development/DevelopmentRequest.cs) 的行命令解析、[DevelopmentGm](../../Server/DfoServer/Development/DevelopmentGm.cs) 的执行与结果文本，以及 [Network/Management](../../Server/DfoServer/Network/Management) 的 TCP 传输。写入请求转换成 Game 层的 CharacterProgressEdit，在线状态和 Repository 不依赖 GM 文本命令类型。

## 3. 命令

以下为离线命令，从仓库根目录执行；在线命令在 `--gm` 前加上 `--gm-port 23102`（与服务启动端口一致）：

```powershell
dotnet run --project Server/DfoServer -- --gm help
dotnet run --project Server/DfoServer -- --gm list
dotnet run --project Server/DfoServer -- --gm show 测试角色
dotnet run --project Server/DfoServer -- --gm level 测试角色 50
dotnet run --project Server/DfoServer -- --gm story 测试角色 50
dotnet run --project Server/DfoServer -- --gm grow 测试角色 37
dotnet run --project Server/DfoServer -- --gm quest-add 测试角色 10100
dotnet run --project Server/DfoServer -- --gm skill 测试角色 179 7
dotnet run --project Server/DfoServer -- --gm account other-1 list
```

剑影这类实验角色仍用[离线准备](离线实验角色.md)。本工具只改已经存在的角色，不创建 Lv95 任务集，也不代替那次准备的备份和审计。

## 4. 单独的本机网页

[GmWeb](../../Tools/GmWeb/gm_web.py) 是独立进程。它只在 `127.0.0.1` 提供页面，把表单转成同一行 GM 命令。服务端须通过 `--gm-port` 打开本机端口，网页的 `--gm-port` 指向相同端口，默认 23102。网页不跟随 `ListenAddress`。

在仓库根目录执行：

```powershell
python3 Tools/GmWeb/gm_web.py --gm-port 23102
```

浏览器打开 `http://127.0.0.1:23103`。`--port` 可改网页端口，默认 23103，且不能与 `GmPort` 相同。自检不启动游戏服务：

```powershell
python3 Tools/GmWeb/gm_web.py --self-check
```

已有完整双技能树的角色，技能编辑只覆盖两树已存在的同ID等级且保留SP/TP；新增没有正式资源槽位资格的ID及删除最后绑定拒绝。未初始化旧角色保留原编辑合同。此工具不发放点数，不替代[在线学习](../模块/技能学习.md)或正常升级。
