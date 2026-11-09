# 安徒恩攻坚商店同版资源恢复

当前累计 PVF 的 `raidshop_anton.shp` 两页均为空，商店目录安徒恩记录指向未登记的 `101298`，并与漩涡重复类别。工具只恢复三个现有资源，不改服务端业务、Runtime、NPC 设置或数据库迁移。

商品采用**同版仍存在的乌恩普通商店** `wuun_normal.shp` 的完整五页 33 件商品，包含装备、宝珠、设计图、其他及退还；这不是对历史安徒恩攻坚商店原始清单完全同源的声明。物品模板的材料价格、限购、礼盒内容和其他声明不改，不引用其他版本构造商品或价格。原乌恩商店继续保留。

目录保留战利品两项，改为类别 3 安徒恩（商店 257、乌恩 328）、4 卢克（258）、5 漩涡（274）、6 普雷（400000052）；卢克特殊奖励原样保留。UI 以同版 `raidshopwindowver2_prey.xui` 的原生安徒恩标签/图标和七类别声明为依据，在当前 `raidshopwindowver2.xui` 上做最小差量：插入安徒恩控件及两种状态，现有 3–5 攻坚页编号顺移 4–6、纵坐标加 52。隐藏页、材质和右侧控件不替换，不新增 NPK 或字符串池。

## 使用

使用 PowerShell 7 和已有匹配 `PvfLib.dll`，独立输出目录；每步输出均应不存在。准备和重打先与资源 owner 核对最新累计输入，资源密集重打及全 raw 审计按工作区预算协调。

```powershell
./Prepare-Delta.ps1 -PvfPath <最新累计PVF> -PvfLibPath <PvfLib.dll> -OutputDirectory <全新差量目录>
./Pack-Delta.ps1 -ManifestPath <差量目录/delta.json> -CandidatePath <全新候选PVF> -PvfLibPath <PvfLib.dll> -PvfReplacePath <既有pvfreplace.exe>
./Verify-Delta.ps1 -ManifestPath <差量目录/delta.json> -CandidatePath <候选PVF> -PvfLibPath <PvfLib.dll> -ReceiptPath <全新收据JSON>
```

重打复用既有 `pvf-companion/cmd/pvfreplace`，必须保持全部文件名/类型；差量生成保留原 token 和字符串引用。重打前校验三个目标 raw 与生成结果摘要。独立 `PvfLib` 验证六项目录映射、七类 UI、原乌恩商品页逐 token 文本一致、全部 33 商品模板原字节及材料成本、旧隐藏页和右栏、卢克特殊奖励。默认遍历全部 raw，非目标必须逐字节相同，不以整 PVF hash 或元数据相等代替内容比较。

`-SkipRawAudit` 仅供资源合同诊断，收据明确标记跳过，不能作为全 raw 保留证明。资源验证不等于实际服务端购买 TCP 或客户端显示/自然购买已验收；必要验证及未覆盖范围交主窗口累计整合，不单独部署或打包给玩家。

## 代表商品 TCP

现有 `shop` TCP 组追加 `ShopTcpTests.Anton.cs`，复用原有夹具及购买协议：真实 257 店、328 入口、10304374 荒古遗尘武器礼盒，9 个 10095702 灵魂碎片兑换后余 4 个、得到单件礼盒；再次兑换明确材料不足，资产、购买额度与审计不变，重连结果仍保存。检查成功 ACK、材料及奖励差量和数据库完整性。

原始资源的 257 店缺失或为空时输出 `anton-shop-exchange/not-present-in-resource`；非空店必须包含代表商品并实际执行，不得按商品缺失跳过。最终恢复候选必须在 TCP 输出见到 `anton-shop-exchange` 的 `status=executed`。本补充只交测试源码，由主窗统一构建并运行 `shop` 及全 TCP；未运行前不称通过，不因仅测试改动重跑同一生产源码及候选的完整资源检查。
