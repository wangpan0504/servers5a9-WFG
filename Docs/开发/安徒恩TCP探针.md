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

`anton-raid-gap`覆盖无票/队员票不足、非队长/未分队、关闭阶段推进及同分队双人阶段0；两人按29地图键/坐标、586完整6字节和全部14图状态核对。`anton-tcp-probe`重建独立Host核对门票、已提交阶段0奖励及同角色重新开战，不冒称活跃战况跨进程恢复。

`anton-raid-phase1`使用可控单调时钟按本服已有秒制约定验证211/216开放/重开，再沿真实清图、660奖励提交、218重开/HP0、219开220、220最终通关和Completed贯通阶段1。另有`anton-raid-reward-formal`验证资源政策与抽样首尾边界。随机孵化、阶段总超时及未知计时wire不包含在这些成功验收中。

`MissingFormalDungeons=14` 是主 `dungeon/dungeon.lst` 没有 210–216 与 218–224。团本目录用备用脚本和显式 MAP 登记，这 14 项记在开战日志里，不拒绝这次开战，也不拒绝 210。

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
