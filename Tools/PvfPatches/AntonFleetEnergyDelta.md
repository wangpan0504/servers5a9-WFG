# 安徒恩舰炮能量球差量

舰炮216的64073守护兵由18734召唤体生成，但其Last只有两层死亡动画，没有产球动作。
同资源的 `aicharacter/gunner/ruin_anton/action/proc.act` 明确说明“打倒守护兵后可获得能量球”。
18736的动作在216创建18737跟随玩家；18739/18820吸收跟随球并逐次驱动18731炮台，第三次充能后炮台销毁65660终点怪。
本差量只给守护兵Last追加限定216、FRAME0、LIMIT1的18736生成动作，沿用跟随球落回地面的原生成参数。
保留原死亡动画、刷怪、吸收、充能及炮台动作，不添加计时通关或直接改Raid符号。

生成器只读当前累计PVF，只接受已知原Last或本补丁已应用状态；其它动作拒绝处理，避免重复产球。
它沿用原字符串池，输出单文件raw与带逐资源SHA256的 `replacements` 清单；输出已存在即拒绝。
交付时服务端和客户端必须使用由同一最新累计输入制作的候选；不能用旧整包替换累计资源或只更新服务端代码。

```powershell
./Tools/PvfPatches/New-AntonFleetEnergyDelta.ps1 -PvfPath '<累计Script.pvf>' -PvfLibPath '<已有PvfLib.dll>' -OutputDirectory '<不存在的差量目录>'
# 使用支持raw替换的写包器把清单列出的唯一文件应用到新的候选，不覆盖输入。
./Tools/PvfPatches/Test-AntonFleetEnergyDelta.ps1 -PvfPath '<原累计Script.pvf>' -PvfLibPath '<已有PvfLib.dll>' -CandidatePvfPath '<新候选Script.pvf>'
```

资源测试验证原动画、唯一产球动作、216范围、真实候选回读、幂等及未知输入拒绝。
`anton-raid-phase1`验证65660正式死亡后的单图状态和150秒重开，不能代替自然守护兵死亡、拾取、跟随、三次吸收及多人同步的客户端验收。
