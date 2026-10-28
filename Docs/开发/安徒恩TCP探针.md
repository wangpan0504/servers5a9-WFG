# 安徒恩 TCP 探针

假客户端按隔离 TCP 的同一套帧，连本进程或另一进程的真实监听，核对安徒恩开战和阶段 0。它不启动 ARAD，也不改协议以外的玩法。

## 不连接

```text
DfoServer --anton-probe
```

没有 `--host` 时只打印用法，不监听、不连端口。

## 进程内回归

```text
dotnet run --project Server/DfoServer -c Debug --no-build -- --selftest-tcp anton-tcp-probe
```

本组在临时库打开 `StartMinimumOverride=1`、`AntonFormalEntryEnabled=true`、`AntonPhaseProgressEnabled=true`，然后：

1. 频道握手，确认目录里的频道 19 就是本进程的分类入口。
2. 85 级、任务 3779 与 12829、两张 `10096296`。
3. 833、664、669、666。成功后角色在 19/2，门票剩 1 张。
4. 实机 25 字节 CMD16。`Unknown21=1` 仍拒绝；`Unknown21=0xFFFFFFFF` 进入 210。
5. 39/45/117 按迷宫清 210，再清 212、214、213、215。211 保持关闭。
6. `server.jsonl` 有进图 210、`anton_phase_progress`（Reward，`PhaseSuccess=1`）和 opcode 586，没有 596/597。

探针支持 `--steps start,enter,phase,reward,card`，也可只写最后需要的步骤，例如 `--steps card`；前置步骤自动执行。步骤必须按上述顺序且不能重复，不能与旧参数 `--stop-after` 混用；默认仍停在 `phase`。每步打印 opcode、关键字段和耗时，失败时提示检查该隔离 Host 的日志、开关与角色资格。

`reward`在阶段0 Reward态重复117，并用乱序16请求未开放216，核对没有重复推进。`card`发送已观察的独立kind1/0、card0正式660并重复，核对成功ACK仅为`01`、两份真实资源奖励、幂等回放和进入阶段1；跨阶段回放不再向新窗口发送旧600。奖励的本服政策、来源与未实现边界见[阶段与结算执行入口](../模块/副本.md#安徒恩阶段与结算执行入口)。其它牌种、槽位及Luke16B奖励项不由这几个观察值推导；策略缺失的目标明确打印`UNIMPLEMENTED`。该流程不证明原厂翻牌抽样或客户端自然演出。

`card`还以未配置kind/槽位验证拒绝边界；正式600逐包核对`kind:u8,count:u8=0`且先于ACK660，全程禁止601。输出中的`600=empty; 601/display fields UNIMPLEMENTED`表示资产领取可用、完整展示字段仍缺证据。601固定连续读取10B，不能因已确认部分字段就发短前缀或用零补齐。上述检查复用`anton-tcp-probe`的同一Host与完整清图，不增加重复冷启动的测试组。

`anton-raid-gap`覆盖无票/队员票不足、非队长/未分队、关闭阶段推进及同分队双人阶段0；两人按29地图键/坐标、586完整6字节和全部14图状态核对。`anton-tcp-probe`重建独立Host核对门票、已提交阶段0奖励及同角色重新开战，不冒称活跃战况跨进程恢复。

`anton-raid-phase1`使用可控单调时钟按本服已有秒制约定验证211/216开放/重开，再沿真实清图、660奖励提交、218重开/HP0、219开220、220最终通关和Completed贯通阶段1。另有`anton-raid-reward-formal`验证资源政策与抽样首尾边界。随机孵化、阶段总超时及未知计时wire不包含在这些成功验收中。

`MissingFormalDungeons=14` 是主 `dungeon/dungeon.lst` 没有 210–216 与 218–224。团本目录用备用脚本和显式 MAP 登记，这 14 项记在开战日志里，不拒绝这次开战，也不拒绝 210。

## 安徒恩剩余缺口矩阵

基线为合入 !186 后的 `e068f6c2`。表内源码路径均相对于 `Server/DfoServer/`。测试组是源码已有覆盖入口，不表示本基线或实机已通过；缺口测试可能只验证拒绝或未发包。静态地址只适用于[副本协议](../协议/副本.md)所列匹配 x86 构建。PVF、服务端策略、客户端静态反汇编和实机包是不同证据，不能互相代替。

| 项目 | 主线实现位置及已有测试 | 证据和未实现原因 | 用户需采集的最小序列或断点 |
|---|---|---|---|
| 596/597 正式发包 | `Network/Projection/RaidCombatProjection.cs:PostState` 的597路径属于 Luke，Anton 不发；`Network/Builders/RaidCombatPackets.cs` 有既有编码。`anton-tcp-probe`、`anton-raid-phase-timer-221-224` 覆盖不发包边界。 | **UNIMPLEMENTED**：RE `02386120` 证实596读宽及 Anton 时钟消费者，597证实显示消费者，但发送时机、键与清理合同未闭合。KB-20261027T145946-bd09524b7c、KB-20261027T180344-d939ca0e3e。 | 采开战、210 clear、计时开始/重置/到期的双向包及时间；观察 `02386120`、`01DD06B0` 和 `01DD17E0` 输入、时钟与画面。 |
| 随机目标、权重和精英 | `Game/Raids/AntonRaidBattle.cs:CompleteDeferred` 仅接受显式目标；`Game/Raids/AntonRaidBattle.AntonClocks.cs:ExpireDueAntonClockLocked` 执行已支持计时。`anton-raid-phase-timer-221-224` 不证明自然随机或刷怪。 | **UNIMPLEMENTED**：PVF 声明随机孵化场及命名怪符号，但目标候选、权重、实例生成和精英行为未证。KB-20261025T235923-1472d54bb1、KB-20261028T103456-c42b646307。 | 多轮同阶段录219计时到期前后战况包、目标、精英出现时间及画面；定位随机选取、动作消费者和生成调用栈。 |
| PHASE BREAK TIME=600 休息转换 | `Game/Raids/AntonRaidBattle.cs:AdvanceAfterRewards` 按奖励确认推进；`anton-raid-phase-timer-221-224` 覆盖缺口，`anton-raid-phase1` 覆盖现有推进。 | **UNIMPLEMENTED**：PVF 有原值600；RE 的597 key1/state5仅证明待命显示，起算、结束或转换条件未证。KB-20261027T180344-d939ca0e3e、KB-20261028T000822-9015b0a350。 | 录阶段0结束、两次翻牌确认、休息开始/结束至阶段1的双向包、画面和时间；观察 `01DD17E0` 与阶段动作消费者。 |
| 客户端演出 | `Network/Projection/RaidCombatProjection.cs:Notice`、`PostState` 投影现有状态，`Game/Raids/AntonResourceRewards.cs` 设置 `PublishResult=false`；`anton-tcp-probe`、`anton-raid-card600-601` 不验收自然演出。 | **UNIMPLEMENTED**：完整翻牌、成功与失败演出缺实机序列及业务值；局部 RE 消费者不能证明全部交互。KB-20261026T035322-6856b70fc3、KB-20261028T104404-8d5d899a41。 | 分录成功及超时失败，从阶段终结经587、600、601、660/ACK到界面稳定的双向帧、动作、画面和资产变化；未观察到的包标缺失。 |
| 频道19到210 | `Game/Characters/CharacterSelection.cs`、`ChannelAreaLanding.cs:Place` 处理落点；`Network/Handlers/DungeonSelectionHandler.cs`、`Game/Raids/AntonRaidDungeonService.cs` 处理进图。`raid-channel-type`、`anton-tcp-probe` 不能证明自然操作。 | **根因未确定**：现场曾见 type `0x17`、19/0及赛丽亚画面，未达210；RE `03B87FE0` 有落点映射。#852 的 type11 不是该路径。KB-20261021T084252-621bd3de0b。 | 同连接录登录、频道目录、选角位置、1379/1426、移动、833/664/669/666、15/16到210及画面；对照成功与失败，观察 `03B87FE0`、选图发送及场景消费者。 |
| 668 成功恢复 wire | `Network/Handlers/RaidHandler.cs:RejoinAsync`、`Game/Raids/AntonRaidRejoin.Recovery.cs:RestoreAnton` 已接状态侧恢复，仍返回错误19；`anton-raid-rejoin`、`anton-raid-rejoin-recover`、`anton-raid-rejoin-client-notify` 不证明自然成功。 | **UNIMPLEMENTED**：RE 证835→条件668及 ACK 分支，成功通知、顺序和加载时机未知；现有实机材料无成功样本，主动37不是自然恢复。KB-20261028T104121-475bd2ccbe、KB-20261027T150809-274089169c。 | 两人同团分别采候战室、稳定房、加载、奖励阶段断线至重登稳定的双向包及幸存者状态，含835/668/ACK和可能的585/586/27/28/29/37/30；观察 `023AB820`、`023AB9FA`、`02344439`、`0055D87A`。详见[采集方案](../协议/668恢复序列证据.md)。 |
| 601 D/E/F 与 C 起止 | `Game/Raids/AntonResourceRewards.cs` 的正式策略不发布结果；`Network/Projection/RaidCombatProjection.cs:PostState` 只在存在结果时发送。`anton-raid-card600-601`、`anton-tcp-probe` 覆盖空600、禁601和回执边界。 | **UNIMPLEMENTED**：RE `023B2A54` 附近证实固定10B及显示消费，C显示分秒但起止未知，D/E/F业务来源未闭合；不能补零或发送短体。KB-20261026T063752-4691836ed4、KB-20261028T104404-8d5d899a41。 | 两阶段分别采完整587/600/601/660/ACK、所有角色画面与资产，记录开战及阶段起止时间；观察 `023B2A54` 读值和显示消费者。 |
| 原厂计时单位 | `Game/Raids/AntonRaidBattle.LukeClocks.cs:ExpireDueLukeClock` 先检查总期限，`Game/Raids/AntonRaidBattle.AntonClocks.cs:ExpireDueAntonClockLocked` 处理局部计时；`anton-raid-phase-timer-221-224` 测本服秒制及总期限，不证明原厂单位。 | **UNIMPLEMENTED**：PVF 的2400、300、150、20等是原值；RE `042AC35B`、`042AC04C` 只证明动作记录，执行消费者未闭合。秒制是本服策略。KB-20261028T000822-9015b0a350、KB-20261028T033030-cf592ac648。 | 采阶段开始、局部触发和到期的时间戳、完整帧及界面；追踪解析结果至执行消费者和时钟比较，比较多种原值实际耗时。 |

上述采集由用户在授权参考环境手动完成，先核对客户端构建，保留连接、方向及完整正文并脱敏；不可因本服探针主动发包或缺少样本推断原厂行为。总期限已在主线实现并由独立计时组覆盖，但不包含在 `anton-raid-phase1` 单组的验收范围内。

## 实机缺口与采集

隔离 TCP 探针能到 210，不证明客户端自然操作能到 210。既有现场记录显示频道 19 的 type 为 23（`0x17`），服务端位置为 19/0 时画面仍为赛丽亚房间；该记录没有达到开战和 210，原因未确定。远古频道 type 11 的落点修复（#852 / !178）不是频道 19 的证据，也不能据此认定此问题已修复。

由用户在授权的测试环境手动操作并采集，助手不操作现场客户端或服务。记录客户端构建身份、服务端提交、资源版本、时间与时区；原始抓包仅保存在授权的本地证据目录，分享前移除凭据、密钥及角色识别信息。每条记录保留连接标识、方向、时间、opcode、完整正文和关联界面截图，不把不同连接合成一个顺序。

1. 从登录、选角、切频道开始持续抓取完整双向帧，记录频道目录的编号、type 和实际连接，选角前后服务端位置、客户端画面和操作时间；特别保留 1379、1426 及位置相关消息，缺失时标为未观察到。
2. 在同一频道连接上记录从 19/0 到候战室的移动、界面变化和服务端位置日志；区分客户端没有发送移动、服务端拒绝或未改变位置、服务端位置改变而画面未切换。无法移动时保留原始失败现场，不通过修改数据库或代发请求制造成功。
3. 若自然操作可达，继续完整记录建团、分队、开战、选图到 210 的双向包及结果；对照探针的 833、664、669、666、15、25 字节 CMD16，明确请求是否出现、连接和响应，不把探针主动发送当作客户端自然行为。
4. 在匹配构建上，由具备授权的操作者按已有符号或反汇编定位频道位置投影、选图发送和场景消费路径，记录实际命中地址、调用栈、输入及前后状态；未定位的地址保持未知，不修改返回值。比较成功参考环境和失败现场，不把单条缺包当作根因。

668 成功重入仍为 **UNIMPLEMENTED**。候战室、稳定房、加载和奖励界面的双人完整抓包、835/668/ACK 前后候选通知及失败对照，按[恢复序列证据与采集方案](../协议/668恢复序列证据.md#用户实机取证方案)执行；客户端断点候选为 `023AB820`、`023AB9FA`、`02344439/7C/DE`、`0055D87A/881`，须先核对匹配构建，实际未命中或不适用均如实记录。普通进图和假客户端主动 37 均不能证明重入成功。

实机 CMD16 来自 `codex-runs/anton-live-20261028/pcap-manual-1235/packet_log.txt`：

```text
D2 00 00 00 02 00 00 00 00 FF FF 00 00 00 00 00 00 00 00 00 00 FF FF FF FF
```

## 连另一个进程

先在那个进程的 `--data-dir` 上执行 `--seed`（服务必须停着，因为它要独占 `server.lock`），写好 `raid-start.json` 和角色后再启动服务。然后：

```text
DfoServer --pvf-path <Script.pvf> --anton-probe --host 127.0.0.1 --channel-port <频道端口> --account antonprobe --character AntProbe --server-log <该进程 Logs/server.jsonl>
```

服务端自己的 `--game-port` 是频道 10。频道 19 的端口在频道目录里。`--game-port` 只在你要核对目录端口时才加，不一致会直接失败。

隔离服 `23100` 的命令留在当次 `REPORT.md`，未放行时不要执行。

## 668 状态恢复与通知缺口

`anton-raid-rejoin` 验证开关关闭时拒绝，以及开启后新 owner 恢复原攻坚队成员、槽位、分队、Battle 和持久候战室位置。恢复复用现有普通分队，不再扣票；重复、错误 key 和旧会话迟到清理不能替换新绑定。核心用例另测分队忙门禁和位置保存 SQL 回滚，失败不消费恢复候选。

`anton-raid-rejoin-recover` 使用正式 Runtime 两人进 210，队长先确认加载，队员在加载确认前断线。668 恢复存活的原 Run 与成员上下文后，新 owner 可在同一 Run 重新确认加载；没有再次发送16或扣费。恢复要求原分队、阶段、地图和已确认战斗事实仍匹配；原 Run 已释放、地图或死亡事实发生变化、未决事务尚未完成时不覆盖幸存者进度。候选仅在原攻坚仍存活的进程内有效，不代表 Host 重启战况恢复。

CMD668 的 `key:u32` 有静态证据，但成功后的场景通知顺序没有闭合。当前状态恢复仍回复已证错误19，日志用 `StateRestored` 和 `StateReason` 区分状态提交与 `wire-unimplemented`；不会凭猜测发送585/586或27/28/29/30。测试主动发送37验证状态能力，不表示真实客户端会在该拒绝后自行完成恢复。
