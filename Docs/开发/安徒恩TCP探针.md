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
