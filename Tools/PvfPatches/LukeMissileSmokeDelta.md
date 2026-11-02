# 卢克导弹烟雾路径差量

两份 `luke/gisellemissile1*/action/gisellemissile1.act` 的烟雾引用使用
`../../particle/BossMissileSmog.ptl`，会指向不存在的卢克共用目录。
工具将它改成 `../particle/BossMissileSmog.ptl`，引用各导弹自身已存在的资源。
每份 ACT 只修改 token 120 的字符串池值（原始偏移 601 起的 4 字节）；不追加字符串，
不改其它粒子、音效、被动对象 ID 或烟雾参数（寿命 4500、创建频率 20）。

使用 PowerShell 7 和已有的兼容 `PvfLib.dll`，无需构建服务器：

```powershell
./Tools/PvfPatches/New-LukeMissileSmokeDelta.ps1 `
  -PvfPath '<当前累计候选 Script.pvf>' `
  -PvfLibPath '<已有 PvfLib.dll>' `
  -OutputDirectory '<尚不存在的差量输出目录>'
```

工具只读 PVF，先验证两份 ACT 的已知布局、烟雾 PTL 参数与 ANI 引用，以及目标 ANI
是否存在且为 token 类型，再在同级临时
目录生成两份 `.raw` 和 `manifest.json`，最后整体发布目录。输出目录已存在即拒绝；
未知布局或缺依赖时不生成差量。清单包含 `replacements`，每项使用
`path`、`raw_file`、`expected_before_sha256`，可交给支持此格式的写包器另行处理；
其中 `raw_file` 为最终输出目录内文件的绝对路径，附加 `after_sha256` 和 `state` 供回读。
已应用和两目标混合状态均受支持：两份 raw 始终输出供核验，清单只列实际修改项，
全部已应用时 `replacements` 为空。本工具不调用写包器、不覆盖运行资源、不部署。
已应用项逐字保留原 raw，即使字符串池中相同文本有多个偏移，也不改写为其它偏移。
工具未检查 ANI 内的图片引用或 NPK 图片内容，这部分视觉效果由客户端 QA 验证。

定向测试同样只读真实候选，已应用、损坏布局和缺依赖通过内存代理模拟：

```powershell
./Tools/PvfPatches/Test-LukeMissileSmokeDelta.ps1 `
  -PvfPath '<当前累计候选 Script.pvf>' `
  -PvfLibPath '<已有 PvfLib.dll>' `
  -TemporaryDirectory '<尚不存在的测试产物目录>'
```

这修复的是已证实的资源引用错误。对玩家普遍掉帧的改善仍须独立客户端验收，
不能把该路径修正视为所有性能问题的唯一根因或已验证解决。
