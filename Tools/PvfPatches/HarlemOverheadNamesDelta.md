# 哈林深渊末波头顶名称

哈林深渊216–218组的九个末波敌人使用MOB模板，已有中文名称。普通敌对MOB缺少无参数`[enable states]`时，原生头顶绘制分支不填充名称。该标记由03E3ABD2设置模板+3A40，02C98C3E复制到演员+5ED0，02C47650使用原有名称及颜色绘制；整体隐藏与透明门禁保持。

`New-HarlemOverheadNamesDelta.ps1`仅为九个已确认编号生成raw替换清单。它按怪物索引定位资源，复用既有字符串池，保留每份MOB全部原字节，只在缺少标记时追加一个type3 token；已有标记时幂等。缺映射、重复映射、缺名字、重复标记或未知布局时拒绝。不会改品级、阵营、模板身份、奖励、服务端协议或其余怪物。

```powershell
pwsh -NoProfile -File Tools/PvfPatches/New-HarlemOverheadNamesDelta.ps1 `
  -PvfPath <当前累计候选> -PvfLibPath <匹配PvfLib.dll> -OutputDirectory <全新差量目录>
```

差量清单使用既有PVF raw替换工具，按来源单文件摘要复核后，在隔离路径生成新候选。不要覆盖运行PVF，不要以旧整包覆盖后续累计成果。头顶中文名、颜色与布局仍需客户端QA。
