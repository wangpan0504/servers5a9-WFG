# 漩涡团队冰牢资源差量

工具只读用户自备基底，生成四份 raw 和标准 replacement manifest；不附带游戏资源、不覆盖 PVF、不计算整包哈希。通过仓库 PvfLib 读取，依赖 .NET 10。从仓库根运行：

```powershell
dotnet run --project Tools/FiendwarResourcePatch -- generate '<基底.pvf>' '<差量目录>'
dotnet run --project Tools/FiendwarResourcePatch -- verify '<基底.pvf>' '<候选.pvf>'
```

生成的 manifest 交给支持 `path/raw_file/expected_before_sha256` 的 PVF 写包器，输出独立候选。发布方须逐项比较写包前后所有资源，确认仅四个目标变化、metadata 及其余 raw 保持，再用本工具独立读取候选。旧模型、新模型分别从各自基底生成，不混用。`expected_before_sha256` 仅用于单资源的并发变更保护。

只修改 `fiendwar_kr/monster/boss_cosmofiend/icepattern1p` 至 `icepattern4p` 的 `_prison.act`。完整保留原始 raw 前缀、trigger/behavior 编号和每200毫秒扣1%HP；追加一个10秒触发器，调用既有 behavior 3，切到 CUSTOM 2 的 Last 动作，由原资源恢复父玩家碰撞和站立、销毁冰牢。队友原救援触发器不变。引导、普通超时空及其他机制不修改。

工具拒绝不匹配的行为结构和已经修改过的基底。自动读取通过不代替客户端实机验收：须验证四个角色槽均能在10秒后脱困、正常救援可提前解除、原扣血保持，以及官方引导不受影响。
