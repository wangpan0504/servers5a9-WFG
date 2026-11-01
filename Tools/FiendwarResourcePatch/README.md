# 漩涡机制资源差量

工具只读用户自备基底，默认冰牢命令生成四份 raw 和标准 replacement manifest；不附带游戏资源、不覆盖 PVF、不计算整包哈希。通过仓库 PvfLib 读取，依赖 .NET 10。从仓库根运行：

```powershell
dotnet run --project Tools/FiendwarResourcePatch -- generate '<基底.pvf>' '<差量目录>'
dotnet run --project Tools/FiendwarResourcePatch -- verify '<基底.pvf>' '<候选.pvf>'
```

生成的 manifest 交给支持 `path/raw_file/expected_before_sha256` 的 PVF 写包器，输出独立候选。发布方须逐项比较写包前后所有资源，确认仅四个目标变化、metadata 及其余 raw 保持，再用本工具独立读取候选。旧模型、新模型分别从各自基底生成，不混用。`expected_before_sha256` 仅用于单资源的并发变更保护。

只修改 `fiendwar_kr/monster/boss_cosmofiend/icepattern1p` 至 `icepattern4p` 的 `_prison.act`。完整保留原始 raw 前缀、trigger/behavior 编号和每200毫秒扣1%HP；追加一个10秒触发器，调用既有 behavior 3，切到 CUSTOM 2 的 Last 动作，由原资源恢复父玩家碰撞和站立、销毁冰牢。队友原救援触发器不变。引导、普通超时空及其他机制不修改。

工具拒绝不匹配的行为结构和已经修改过的基底。自动读取通过不代替客户端实机验收：须验证四个角色槽均能在10秒后脱困、正常救援可提前解除、原扣血保持，以及官方引导不受影响。

## 宾柏追踪目标重选

团队及引导的`TargetChange2/3/4.act`成功分支分别选取角色槽1/2/3，原失败分支却都检查槽0。若选中槽不可用、首槽仍可用，失败条件不能对应选中槽；首槽不可用而选中槽可用时，两分支也可能同时满足。独立命令将失败检查改为本次选中的同一槽，保留原随机选择、存活过滤、追击动作、蹲伏和虚弱规则，不按人数分流：

```powershell
dotnet run --project Tools/FiendwarResourcePatch -- generate-targets '<基底.pvf>' '<目标重选差量目录>'
dotnet run --project Tools/FiendwarResourcePatch -- verify-targets '<基底.pvf>' '<目标重选差量目录或候选.pvf>'
```

仅六个ACT各一个整数token变化；同槽成功与失败成为互补条件。第1槽原文件、`TargetWait`随机池、官方引导玩法参数、普通超时空及DPL资源不变。工具只生成独立raw与manifest，不写包；既有`generate/verify`四冰牢命令保持原合同。两模型各自生成，单资源摘要仅保护对应基底，不依赖整包哈希。

离线字节验证不能证明原生索引选择的全部行为。QA须覆盖1/2/3/4人、各槽死亡/复活、选择不存在的槽后能重新追踪活人、活人被选中时不被另一槽死亡打断，以及团队和官方引导的自然追击与原虚弱流程。

## 统一差量与累计基底

`generate-all`从同一份自备基底一次生成四份团队冰牢及六份团队/引导目标重选raw，并输出唯一标准manifest，避免分别生成时覆盖清单：

```powershell
dotnet run --project Tools/FiendwarResourcePatch -- generate-all '<当前基底.pvf>' '<新的统一差量目录>'
dotnet run --project Tools/FiendwarResourcePatch -- verify-all '<当前基底.pvf>' '<统一差量目录或独立候选.pvf>'
```

统一生成只接受新建或空输出目录。先检查全部十份资源再写文件；失败输出不可用，不写运行资源。原有单项命令继续严格拒绝已修改基底；统一命令还识别已知修正，适用于累计后的基底：冰牢必须逐字节匹配本工具追加的完整末尾触发器，宾柏必须满足原两分支结构且失败槽与成功槽一致。未知时长、行为编号、分支结构仍拒绝，不凭单个标签或文件名视为已修。

已经应用的资源仍输出raw供完整核验，manifest只列本次确需替换的资源；全部已应用时为空清单，不能再次追加触发器。混合基底可以仅生成剩余差量。目录核验同时检查十份raw、manifest数量、唯一资源路径、对应raw路径及单资源原值摘要，损坏raw、重复/错基底/指向其他文件的清单均拒绝。候选PVF核验对十份实际raw逐字节比较，并核格式、文件总数、完整路径、资源类型和其余全部raw保持；字符串池及写包器内部metadata的保持仍须由发布方独立检查。两份模型各自生成和验证；不根据文件名推定兼容，不计算整份PVF摘要，不自动写包、安装或覆盖受保护运行PVF。
