# 厄伽勒及同类常规副本消耗品可用副本差量

匹配资源共241份声明`[action usable dungeon]`的STK：其中140份为专属玩法限定道具（遗忘之地4000、DPL、强者之路、公会地下城/公会战、安徒恩/卢克/魔兽/超时空漩涡/普雷团队、悲叹之塔、艾肯、超·星空裂缝、魔界裂缝等，按玩法保留原白名单），其余101份为常规地下城通用白名单道具（70份`厄伽勒变身药水`、9份`厄伽勒之眼/睁大的厄伽勒之眼/远古地下城助手及顾问厄伽勒`、4份其他常规副本骑乘变身道具`Jolin团长变身药水/10305901/10150930/10150931`，以及18份常规副本召唤/投掷/增益道具`黄蜂号战争领主/战争领主/多博特/空中战斗机器人/小树苗模型/力量之源泉`系列）。
在这101份常规副本道具中，17份95级版本`厄伽勒变身药水`（490010408等14份285图、490010680等3份含黎明裂缝530的286图）已包含现代异界（7165–7179）、哈林强袭（7300–7337）与哈林普通地下城（291100268–291100332）；其余84份早期条目（涵盖102/110/127/160/162/165/172/173/187/224/239/243/265/280/284图历史子集）缺失现代异界或哈林副本，导致原生客户端`029E3B20 -> 029B9DB0`在发送CMD44/CMD515前按`CStackableItem+0x55C`（`[action usable dungeon]`）本地拦截。
本工具将这84份早期条目的`[action usable dungeon]`统一补齐至95级285图基线并保留各条目原有的额外许可副本，不新增字符串池条目，且逐字节保留各条目名称、说明、风味文本、绑定、等级、冷却、堆叠上限及闭合标签后的全部原始数据。

使用PowerShell 7和兼容的PvfLib程序集，从仓库根执行：

```powershell
dotnet build Tools/PvfLib/PvfLib.csproj -c Release
pwsh -NoProfile -File Tools/PvfPatches/New-EkalRidingPotionDungeonDelta.ps1 `
  -PvfPath '<当前累计Script.pvf>' `
  -PvfLibPath Tools/PvfLib/bin/Release/net10.0/PvfLib.dll `
  -OutputDirectory '<不存在的差量目录>'
pwsh -NoProfile -File Tools/PvfPatches/Test-EkalRidingPotionDungeonDelta.ps1 `
  -PvfPath '<同一累计Script.pvf>' `
  -PvfLibPath Tools/PvfLib/bin/Release/net10.0/PvfLib.dll `
  -TemporaryDirectory '<不存在的验证输出目录>'
```

生成器输出101份`<itemId>.stk.raw`与`manifest.json`，不写包、不覆盖运行资源。
每份目标先核对`stackable/stackable.lst`精确路径、单段`[action usable dungeon] ... [/action usable dungeon]`整数token结构、已知历史/目标副本序列，以及还原初始副本表后的整份STK SHA256基线；任何段外字节改动、未知映射、重复标签或已存在输出目录均拒绝。
清单沿用`replacements`格式，仅列出84份实际更新项，17份95级已齐备条目保持`already_applied`；对已应用资源再次执行时`replacements`为空。

服务端`ActionConsumableDefinition.AllowsDungeon`已与普通STK入口统一，将`[action usable dungeon]`交客户端`029B9DB0`核对并继续保留`[limit dungeon]`与`raid dungeon`限制；客户端侧须合入本差量以解除早期编号在异界及哈林普通地下城的本地拦截，实机变身与道具表现由客户端验收确认。
