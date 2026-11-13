# 灿烂的徽章自选礼盒堆叠差量

赛丽亚幸运盒2682272的奖励490704572在匹配资源中声明`[stack limit] 1`，
导致每个礼盒占一格。按本服兼容政策将该上限改为1000，保留30天期限、账号绑定、
全部37种自选奖励及其它原始字节；1000不是原版资源数值。

使用PowerShell 7和兼容的PvfLib程序集，从仓库根执行：

```powershell
dotnet build Tools/PvfLib/PvfLib.csproj -c Release
pwsh -NoProfile -File Tools/PvfPatches/New-BrilliantEmblemBoxStackDelta.ps1 `
  -PvfPath '<当前累计Script.pvf>' `
  -PvfLibPath Tools/PvfLib/bin/Release/net10.0/PvfLib.dll `
  -OutputDirectory '<不存在的差量目录>'
pwsh -NoProfile -File Tools/PvfPatches/Test-BrilliantEmblemBoxStackDelta.ps1 `
  -PvfPath '<同一累计Script.pvf>' `
  -PvfLibPath Tools/PvfLib/bin/Release/net10.0/PvfLib.dll `
  -TemporaryDirectory '<不存在的验证输出目录>'
```

生成器仅输出`490704572.stk.raw`与`manifest.json`，不写包、不安装资源。
仅接受目标ID的精确路径、匹配的570字节单资源基线或已应用版本；
堆叠整数以外的变化、错误映射、未知上限、重复字段及已存在输出均拒绝。
清单沿用`replacements`格式，包含目标路径、raw文件及修改前后单资源SHA256；
幂等输入的清单为空。不计算整份PVF哈希。

资源整合方应在最新累计资源上按清单校验并只替换这一条STK，回读候选，确认上限1000、
期限30天及所有非目标条目不变，再将同一候选用于客户端与服务端。单改服务端上限会造成
双方声明不一致，不能代替资源整合。回滚使用部署前保留的对应累计资源；有其它后续差量时
只还原此STK，不能用旧整包覆盖新功能。

统一发奖和移动继续核对完整实例属性；相同到期时间才合堆，不同到期时间仍分堆。
不自动整理存量物品，不修改已有UID、数量或到期时间；历史同期限礼盒可手动合堆。
满堆、期限不同及满页继续沿原业务拒绝或溢出邮件合同，不截断奖励。
工具验证不等于游戏验收；部署后还须验证魔盒连续/批量领取、历史礼盒合堆、选择开启与重登。
