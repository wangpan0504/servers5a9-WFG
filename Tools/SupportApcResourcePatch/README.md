# 四类支援兵资源差量

工具只读输入 PVF，输出隔离目录或独立候选，不操作运行资源。使用已有 PvfLib 读取 token，写包复用现有 pvf-companion 的字符串池追加与归档重建。

当前专用编号为光兵 9986、女圣骑士 74990、小魔女 19973；王富贵继续使用 20900。编号必须落在原生 APC 工厂对应职业范围内；旧拟定 20910—20912 不可使用。每次生成还会重新核对注册占用。

## 命令

```text
SupportApcResourcePatch inspect base.pvf --ids
SupportApcResourcePatch audit-migration historical-before.pvf historical-after.pvf current.pvf audit.json
SupportApcResourcePatch preview-maps current.pvf audit.json ignored
SupportApcResourcePatch generate-maps current.pvf audit.json new-output-directory
SupportApcResourcePatch prepare-templates current.pvf new-template-directory
SupportApcResourcePatch pair-resources map-manifest.json template-plan.json client-contract.json completed-manifest.json
SupportApcResourcePatch export-evidence current.pvf template-plan.json new-evidence-directory
SupportApcResourcePatch prepare-overdrive current.pvf Tools/SupportApcResourcePatch/OverdriveConfiguration.json new-output-directory
SupportApcResourcePatch prepare-combat current.pvf new-output-directory
```

## 两职业战斗动作差量

`prepare-combat` 只读最终累计输入，输出 `support-profile-delta-v1` 的 `delta.json` 和逐模板
`combat-report.json`，不重包、不覆盖PVF。只处理注册的奶爸20900、20903至20905与光兵9986、9600至9628；
每项先核对职业和原KEY完整顺序，任一缺失或不匹配即拒绝。保留全部原KEY下标，在末尾追加
`support_attack.key`，已存在同一追加项时不重复添加；输出目录必须不存在。

辅助技能优先级保持，光兵不再生成念气波12的施放分支。两职业在辅助和技能分支结束后增加
100×50范围的原生普攻KEY，先沿攻击目标转向，再使用`x`输入；没有近身有效敌人时保持待机，
不对远处目标挥空拳。原KEY中的念气波资源及学习等级保留，但新动作图没有调用。

战斗场景查询继续使用原生`enemy number of in attack area()`，其18号操作枚举当前场景的
战斗对象，检查类型掩码0x11、存活和敌对关系，不按普通、精英、绿名或Boss品级过滤。
两职业查询矩形扩为四向1,048,576单位的有限范围；原生将浮点边界转为有符号坐标，不能用
无穷或浮点最大值代替。觉醒决策取消额外600距离门禁，实际技能命中仍由原生技能决定。
视野0和跟随目的地5保持，未改变追随、三档属性、装备、技能等级、外观、地图或真人资源。

专项入口：`dotnet run --project Tools/SupportApcResourcePatch.Tests -c Release -- <最终累计PVF> <不存在的新输出目录>`。
它解释实际生成的规则及导入链，覆盖近身普攻、远敌觉醒、增益优先、无念气波、维持保护与
敌对/死亡/类型边界，并逐token证明34份AIC的非KEY内容不变。解释回放不是游戏实机验收；
最终PVF还须由资源owner累计装配、完整回读，客户端自然施放与绿名实战表现待QA。

## 当前注册模板强化差量

`prepare-overdrive` 只读当前累计候选，输出 `support-profile-delta-v1` 的 `delta.json` 和逐模板 `overdrive-report.json`，不重包或覆盖 PVF。持久配置为 `OverdriveConfiguration.json`，明确列出三套完整的五件防具、三件首饰、三件特殊装备及职业武器；每项还会重新校验当前资源的继承、95级史诗品质、职业、前置技能、部位和真实套装归属。首三项覆盖三个不同完整套装，后续有限循环职业武器组合；王富贵三个现存 profile 因而各有一套不同装备。三档强度通过目录根节 `[equal tiers] 1` 明确启用，profile 行与原外观名称原样保留。

当前配置覆盖主 Buff、一觉、二觉及所有已核实的持续 Buff、护盾、持续治疗/支援技能；单次治疗、单次净化、纯伤害和直接复活另行保留。主 Buff/觉醒最终有效等级到实际普通副本表长，其余目标以原学习等级加15后计入装备加级，超过实际表长则截顶并逐项报告，不将 `maximum level` 当作数据表长，也不越界读取返回零。技能表只接受第一张根表或显式 `dungeon` 表及其严格闭合的前置声明，不借用 PVP 等模式；原文件后续无关说明尾块不参与等级读取。

等级计算包含所保留时装、战斗装备和实际件数达到的套装效果，支持严格完整的职业/成长、技能编号直接加级，以及 `all skill item container` 的学习等级区间、普通主动技能和排除 EX 技能条件。每套装备单独计算，AIC 学习等级减去装备/套装加级，以保证最终有效等级不超过普通表长。未知条件分支、额外字段、重复阈值、坏继承和无效表直接拒绝。

`buff option` 按已核实的原生职业/成长门禁计级：14/1女圣骑士、3/5小魔女，以及未学习技能258的4/1圣骑士。普通直接和区间装备加级不会新学基础等级为0的258；奶爸逐模板确认258仍未学习，并拒绝装备/套装继承中独立授予技能的声明、显式258来源和未证明的嵌套条件。十字架108000538/108000558/108000559因此已与108000544一同纳入配置池。实际随机池受现有profile数量限制：王富贵3套组合、3把武器（544/538/558），光兵12套组合、4把武器，女圣骑士12套组合、4把武器，小魔女9套组合、3把武器。王富贵不扩充编号，不能将配置的12种潜在组合全部称为可抽取。

生成仅修改已注册基础和变体的学习/快捷技能等级、完整战斗装备、移速零基37项乘二、`vision=0`、`warlike=100`，并删除完整 AIC `skill data up` 旧档倍率块。全部既有外观及辅助整数（王富贵为十件）、其它属性项、AI、KEY和其余资源继续使用原输入。视野0使用原生正常 APC 的不限距离分支，status5仍保留原生固定距离；好战100为当前已知安全高值，不宣称已证概率最大。最终体精/智力校准仍依赖配套客户端实现，差量生成不等于客户端实机验收。

迁移证明逐图保留历史原 APC 的 ordinal、六个整数、阵营与品级。只接受历史无 5409、之后按原写入器两条分支新增光兵的记录；当前行再次精确匹配才能迁移。新增女圣骑士和小魔女放在原块尾部，原光兵行仅修改编号，保持 SourceOrdinal。来源不明的 5409 原样保留，该图不追加新支援兵。

地图重复生成支持完整差量的幂等回读：重建原 MAP 后比较审计中的单资源哈希，拒绝额外改动、半套新行和编号冲突。预览先完成全部规划，生成使用新暂存目录，失败不覆盖目标。哈希只保护单资源并发变更，不计算整个 PVF 哈希。

`export-evidence` 从同一累计输入和实际模板清单导出四模板的外观/装备/图标索引、variation/layer/lay引用、技能图标与预载图动画资源，保留实际源路径和单资源身份，不生成假预览图。另导出43种主动SKL全文及静态数据、等级数据、等级属性等原token和位置；未知列不推断半径或单位。四源AI目录的目标/队友条件样本另列，只证明样本存在，不把敌方目标距离冒充真人辅助目标距离。

模板草案保留复制资源的原始 token 类型与既有字符串引用；新字符串由写包器追加。独立光兵不改 5409、2600261 或共享技能；女圣骑士借 71202 的职业外观，去除 FSM 与 PvP 队伍倍率。三新增模板的战斗装备在原槽目录选已注册普通/高级无触发物品；保留无触发外观，移除源称号和未注册条目。小魔女仅使用真实技能及其前置声明，武器选择已注册普通扫把。选择/移除原因与 SKL、装备、源 AIC 的单资源身份进入依赖清单。三新增仅继承王富贵的队友配置与两项生存状态节，不继承其装备，不添加无敌标志。

## 尚未闭合的施工合同

`prepare-templates` 输出 `release_ready=false`。四模板包含实际动作优先链及 KEY，分支使用原生 `[import ai]` 共享后续节点；每模板最多 64 条规则，每规则最多 256 token，不做递归文本展开。真人选择器仅在选择成功后查询血量、原生可用性与增益；复活另选死亡真人。烈日气息按原生开启态查询，不能按 5 秒冷却反复切换。来源未核证的辅助状态仍列为待闭合，不能将查询无效当增益缺失，也不能仅按冷却使用净化。

成长与觉醒字段已分别核证：成长值保留对应职业，觉醒阶段设为 2，成长外观布尔保留来源；奶萝成长为 5。生成学习表、觉醒阶段和 KEY 不代表已实机自然施放通过。

`pair-resources` 只接受生成器原始地图/模板清单，并要求 `support-apc-client-contract-v1` 客户端合同。合同的 `files` 项包含绝对 `path` 与单文件 `sha256`；`capabilities` 项包含 `id`、`implemented=true`、说明 `evidence` 和引用文件 `files` 数组。客户端必须交付独立 `SupportCompanion.dll` 及其源码输入，`95JP.dll` 不属于支援兵交付物。必须逐项闭合 `recipient-selection`、`effective-buff-state`、`maintained-actions`、`enchantress-puppet-availability`、`revival-native-limits`、`favoritism-stable-recipient`、`player-escort-and-offense-qualification`。实际实现文件发生变化即拒绝配套；不能手工翻转 `release_ready` 清除待办。此合同是本地交付证据，不是运行时 PVF 握手。

写包器仅接受完整 `support-apc-resource-delta-v1` 清单，要求 `release_ready=true`、无 pending、四类源模板和技能/装备依赖匹配，重新检查七项客户端能力及证据文件。它拒绝半套 MAP 或模板草案。全部输入检查通过后重包，再逐项比较候选和输入的非目标 raw、资源类型与文件数量，通过后才将隔离临时文件更名为候选。

```powershell
./Build-Packer.ps1 -PvfCompanionSource <existing-source-checkout> -ArtifactDirectory <new-isolated-directory>
<isolated-directory>/SupportApcPacker.exe base.pvf completed-manifest.json candidate.pvf
```

构建参数指定本地现有库，不固化机器路径；`GOTOOLCHAIN=local`、`GOPROXY=off`、`GOSUMDB=off` 禁止下载工具链和依赖。2026-11-02 启动器窗口回读确认：当前旧/新职业显示共用一份 169,189,153 字节累计 PVF，显示模式不切 PVF。最终差量基于这份公共累计输入的任务内快照；历史两模型产物只作隔离对照，不能覆盖现场累计资源。完整客户端补丁能力及服务端/客户端资源配套由交付预检确认，不假设存在远程 PVF 版本握手。

