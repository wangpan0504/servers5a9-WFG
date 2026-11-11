# 安徒恩攻坚商店分类兼容修复

旧 Prepare-Delta / Pack-Delta / Verify-Delta 已停用：旧布局遗漏 txtGuide_6，并把新增分类当成原生第七回调槽，导致商店无法打开。不得继续发行旧输出。

当前累计商品已恢复的输入使用以下三步，输出路径须为新路径：

```powershell
./Prepare-Compatibility.ps1 -PvfPath <累计PVF> -PvfLibPath <PvfLib.dll> -OutputDirectory <新差量目录>
./Pack-Compatibility.ps1 -ManifestPath <差量目录/delta.json> -CandidatePath <新PVF> -PvfLibPath <PvfLib.dll> -PvfReplacePath <pvfreplace.exe>
./Test-Compatibility.ps1 -PvfPath <新PVF> -PvfLibPath <PvfLib.dll> -ReceiptPath <合同收据.json>
./Verify-Compatibility.ps1 -ManifestPath <差量目录/delta.json> -CandidatePath <新PVF> -PvfLibPath <PvfLib.dll> -ReceiptPath <全raw收据.json>
```

只接受原恢复布局安徒恩3/卢克4/漩涡5/普雷6，迁移成原生卢克3/漩涡4/普雷5、新安徒恩6，补齐原生必需 guide6。只改目录四个分类整数和 UI 分类 ID/guide；商品、材料、限购、卢克特殊奖励、控件位置等全部保留。目录逐token对比、UI逆变换精确相同，全raw审计仅允许两个目标路径变化。摘要只针对目标raw，不计算整PVF摘要。

**必须配套客户端 RaidShopCategory 补丁**：原生六槽对象不能扩写，客户端以外部只读七槽表接管读表并修哨兵/事件边界；频道选择保留原生三团本映射，安徒恩23选分类6。仅更新资源不足以恢复商店。

原乌恩同版五页33件商品继续保留；实际购买由 shop TCP 的 anton-shop-exchange 验证。资源合同和合成DLL测试通过不等于游戏界面、自然购买已验收。工具不修改现场，不独立向玩家打包。

## 入场材料迁移

`Move-EntryMaterial.ps1 -PvfPath <最新累计PVF> -CandidatePath <新PVF> -PvfLibPath <PvfLib.dll> -PvfReplacePath <pvfreplace.exe> -OutputDirectory <新证据目录>` 将10096296“强力的魔能吸食阻断装置”从奈恩攻坚商店108移到安徒恩257第一页第一格，仅删除和插入一个商品token。原五页33件顺序、模板20万金币与两种材料费用、其它资源保持。脚本拒绝覆盖输入/已有候选，核对输入身份，重开候选并审计全raw，只允许两份目标shp变化，不计算整PVF哈希。`anton-entry-material` TCP组验证旧店拒绝、新店真实扣费、材料不足不部分扣费及重连存档。

横幅由客户端原生绘制分派决定：新增分类6必须接回原生背景帧9；不复制卢克图片，也不修改公共NPK或其它分类的横幅。
