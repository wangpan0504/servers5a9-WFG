# 开发 GM

本工具服务开发期调整已有角色的存档、金币、道具邮件及在线商城期限和疲劳调试。它不新增游戏 opcode，不代替正常升级、转职、任务领奖或技能学习。客户端没有 GM 面板；角色命令在服务进程内执行，或在服务停止后直接写 `game.db`，运行调试开关仅在线有效。

当前命令行、管理端口及网页均属于开发调试工具。面向用户的独立 GM 工具将在邮件系统实现后另行提供。

物品、任务名称和属性索引在管理入口启动时后台预热，查询期间继续显示已有的构建进度，并复用有效的磁盘缓存。索引任务归当前服务实例所有；管理入口停止、启动失败或双入口任一退出时，取消并等待全部已启动任务及管理监听，再释放借用的资源。停机后不能为该实例新建索引任务。

## 翻牌会员资格

会员资格由服务端账号到期表保存，不使用客户端上报标志。本机完整测试可通过当前GM端口执行以下命令；关闭服务时改为 `--gm-port 0` 离线执行。将“角色名”替换为该账号已有角色，其他账号在命令前加 `account <账号>`。

```powershell
./dist/win-x64/DfoServer.exe --gm-port 23102 --gm black-diamond 角色名 30
./dist/win-x64/DfoServer.exe --gm-port 23102 --gm gold-card 角色名 30
# 0表示关闭该项会员；不是充值累加，正数表示从现在起设置剩余天数
./dist/win-x64/DfoServer.exe --gm-port 23102 --gm gold-card 角色名 0
```

`black-diamond`设置统一账号契约类型56；`gold-card`是58魔王契约的兼容GM入口，当前启用其免费黄金牌子服务0，不启用其他七项服务，也不自动打开双倍活动。二者复用上游 `AccountWallet.PremiumContracts` 与库存/钱包事务，保留其他契约和余额，仅实际改变才推进账号与提交角色revision。0天删除该类型的正式期限，旧历史字段不能重新激活。设置不补发旧通关奖励、不修改金币或物品，不重置本角色当天已用次数；原子领取及每日限额见[副本翻牌](../模块/单人副本实例.md#通关经验与翻牌奖励)。

回归玩家只保留客户端分析，不提供开通或标记命令；不能用回归资格代替活动64的双倍开关。

## 黄金卡牌双倍活动

```powershell
./dist/win-x64/DfoServer.exe --gm-port 23102 --gm event status 64
./dist/win-x64/DfoServer.exe --gm-port 23102 --gm event on 64
./dist/win-x64/DfoServer.exe --gm-port 23102 --gm event off 64
./dist/win-x64/DfoServer.exe --gm-port 23102 --gm event schedule 64 2026-10-12T18:00:00+08:00 2026-10-19T06:00:00+08:00
./dist/win-x64/DfoServer.exe --gm-port 23102 --gm event auto 64
```

活动是全服配置，禁止account前缀；登记64黄金卡牌双倍奖励、266积分商城和2532诺兰常驻状态，公告编号74不是活动ID。64/266的时间必须带Z或显式时区，开始包含、结束不包含；on/off为持久立即覆盖，auto恢复时间表，schedule保存新区间并清除覆盖。64只影响下一份通关冻结计划，不修改已展示奖励。

2532只支持status/on/off/auto，拒绝schedule；未配置或auto时默认显示诺兰，显式off持久隐藏，on持久显示。默认值只用于读取，不自动补写配置或清除已有off；已保存的历史日期不参与2532判断，也不改变64的状态或版本。入城和换频道投影当前显示状态，保留2037与当前团本事件；仅控制NPC显示，春节活动功能仍禁用。重启保留配置，停服后的离线GM仍遵守原实例锁。

## 积分商城活动

沿同一GM命令管理266：`event status 266`、`event on 266`、`event off 266`、`event auto 266`和`event schedule 266 <开始> <结束>`。命令入口、显式时区和覆盖语义与64相同；未配置时间表且无强制开启时关闭。在线已选角色在开关及时间边界接收完整108和开启后的706，不要求重登；返回选角的连接在下次入城恢复。关闭会阻止新积分购买及消费返分，不删除原积分、购买记录或重置累计；普通点券购买仍可继续。GM网页的现有固定按钮未扩展，266使用上述命令入口。资源、消费与额度见[积分商城](../模块/背包与装备.md#积分商城)。

## 1. 能改什么

| 命令 | 写入 | 范围 |
|---|---|---|
| `list` | 否 | 当前账号的角色摘要 |
| `cash-shop-expiration [on\|off]` | 运行状态 | 不带参数查询`ignoreAbsoluteExpiration`；on只忽略固定`[expiration date]`，off保留，默认on。不改变相对期限 |
| `cash-shop-usable-period [on\|off]` | 运行状态 | 不带参数查询`ignoreUsablePeriod`；on忽略`[usable period]`及装扮所选相对天数，off保留，默认off。不改变绝对日期开关 |
| `infinite-fatigue [on\|off]` | 运行状态 | 不带参数查询；on显示满疲劳且不扣余额，服务端照常累计应耗疲劳；off恢复真实疲劳显示、准入和扣点。计数规则见[单人副本实例](../模块/单人副本实例.md#普通图疲劳owner) |
| `pc-room [on\|off]` | 运行状态 | 当前服务实例的全局网吧身份，默认on，数据目录`pc-room.json`写`{"enabled":false}`才默认off（缺文件即on，重启按此恢复）；不带参数查询。on独立发送网吧index2并豁免本次退图虚弱，off恢复普通判断；不授予或撤销账号黑钻index3，不改已有虚弱、疲劳余额或会员期限。下一客户端请求完成后刷新本人及同屏外观，Loading延后至37完成；其他网吧业务不因此自动实现。on/off只改当前进程、不写角色数据，account前缀不缩小作用范围 |
| `show <角色>` | 否 | 单个角色的 id、槽位、job、grow、等级、剧情接受等级、revision、位置、返回点、任务数、技能数和金币余额 |
| `quests <角色>` | 否 | 已完成任务编号，按编号升序 |
| `skills <角色>` | 否 | `技能:等级`，按技能编号升序 |
| `level <角色> <等级>` | 是 | 1–95，设为目标等级起始经验；保留已学技能、槽位（快捷栏）、指令与连招，固定/免费技能按新等级同步，双树SP/TP=新等级总额+额外点数−已学费用。降级时若任一已学技能高于新等级门槛或已用点数超过新总额则整笔拒绝并报告原因（先洗点或用`grow`重置）；额外来源未接管的旧角色也拒绝 |
| `story <角色> <等级>` | 是 | `accepted_story_level`，0–95 |
| `grow-options <角色>` | 否 | 当前基础职业的实际分支、一觉和二觉名称，以及当前选项；来自匹配CHR |
| `profession-options` | 否 | 匹配CHR加载的全部基础职业、分支及觉醒名称与编码；不依赖账号角色，返回`baseJobChange=true`能力标记 |
| `profession <角色> <基础职业> <分支> <觉醒>` | 是 | 基础职业0–15、分支0–5、觉醒0–2，组合须由实际资源声明。整个账号须离线；目标职业不能使用现有穿戴时明确拒绝，要求先卸装；决斗场专用角色拒绝。重建目标职业双技能树，保留全部资产及等级经验 |
| `grow <角色> <分支> <觉醒>` | 是 | 分支0–5、觉醒0–2，须为该职业实际声明的选项；重建双技能树与点数。兼容单个grow字节写法 |
| `quest-add <角色> <任务>` | 是 | 1–29999；重复添加保持一条 |
| `quest-del <角色> <任务>` | 是 | 删除一条；本来没有也成功 |
| `quest-clear <角色>` | 是 | 清空该角色的完成任务 |
| `skill <角色> <技能> <等级>` | 是 | 技能 0–65535，等级 1–255 为设置；等级 0 删除。新技能最多 255 条 |
| `sp <角色> <额外SP>` | 是 | 设置独立额外来源0–50000；需已有可核算来源，重登按总来源减技能费用派生余额 |
| `tp <角色> <额外TP>` | 是 | 设置独立额外TP来源（`character_tp_sources`）0–50000；需已有可核算来源，重登按总来源减技能费用派生余额 |
| `contract <角色> <秒数>` | 是 | 设置所属账号达人剩余时间，0表示到期；重登同步状态和超额等级，不消费游戏道具 |
| `gold <角色> <金额>` | 是 | 设置该角色金币余额，0–2147483647，0清零；开发设置不受等级携带上限限制，正常玩法仍按自身上限执行 |
| `item <角色> <道具ID> <数量> [强化]` | 是 | 发到该角色系统邮箱；装备及其他非堆叠物品按件创建，单封最多10件；堆叠数量不超过模板上限。可选强化0–31仅用于普通装备，其他种类拒绝 |
| `item-info <道具ID>` | 否 | 当前PVF模板的名称、种类、等级、稀有度、堆叠上限及能否强化；未知或无法解析的ID返回错误 |
| `equip-slots <角色> [位掩码]` | 带位掩码时是 | 扩展装备栏 `characters.equipment_slot_flags`。不带位掩码只读返回当前标志及各栏状态；位掩码只能由 1（槽22辅助装备）、2（槽23魔法石）、16（槽25耳环）按位或组成，如 19 全开，只追加不清除，已开启的位不提交也不递增revision。8（光环外观）与 32（宠物幻化）不开放，仍由对应道具开栏。写入须在选角界面，复用背包提交复核revision与原标志；客户端重新选择角色后由 Player 包 offset125 刷新，见[角色与入城](../协议/角色与入城.md) |
| `items <角色>` | 否 | 只读JSON：金币、复活币、胜点、栏位扩展及全部容器实例（容器、位置、UID、道具、名称、数量、强化、封装、期限） |
| `chat-inject <连接号> <L1\|L2\|L3\|P3E\|AAS\|AAS8\|AASU> <文本>` | 否 | 诊断工具：向指定在线连接注入一帧测试通知，不需要玩家在客户端打字；只发这一条连接，不进入正式投递、不改喇叭扣减。`L1`/`L2`/`L3` 是 `NOTI 0x000C`，正文 `[u8 type][u16 层 id][u8 v=0][u32 CP936 字节数][CP936 文本]`（长度 `1..255`，超限按字符边界截断并在回复标注 `(truncated)`），与正式投递共用同一段编码，约定分别传 ASCII / 中文 / 超长文本，用于核对聊天行文本显示与截断；`P3E` 换成 `NOTI 0x03E2` 制裁弹窗（正文 `[u32 A=1][u32 B=1][字符数][UTF-16LE 文本][CP936 字节数][CP936 文本]`，A/B 必须非 0 才走制裁分支），`AAS`/`AAS8`/`AASU` 换成 `NOTI 0x06AD` 弹框（布局相同：标题固定「系统公告」、正文取命令文本，上限为编码后标题 24 字节、正文 600 字节，超长按字符边界截断并在回复标注 `(truncated)`；三者只换这两段文本的编码——`AAS` 用 CP936、`AAS8` 用 UTF-8、`AASU` 用 UTF-16LE，长度字段都是字节数，`AASU` 不带 BOM、不加结尾 `\0`），用于验证这两个通知能否让客户端显示任意文本及客户端期望的文本编码（实测结论：`NOTI 0x06AD` + `AAS8` 的 UTF-8 文本可正常显示，已成为正式公告通道，见 `notice`；`AAS`/`AASU` 只保留为编码对照，`P3E` 仍只是探针）。层 id 只对 `L1`/`L2`/`L3` 有意义，取该连接最近一次聊天上报值，没有则用0并在回复里标注`(fallback)`；当前移动包没有层 id 字段，不能作为来源 |
| `inject-raw <连接号> <opcode> <body-hex\|->` | 否 | 诊断工具：把命令行给出的字节原样发送成一帧 `NOTI`（`Kind=0`），服务端不构造、不解析、不校验、不重试，用于客户端解析失败时逐个变体试长度字段、编码与结尾字节。`opcode` 支持十进制或 `0x` 十六进制（不超过 `0xFFFF`）；`body-hex` 允许内含空格、必须偶数长度且只含 `0-9a-f`、上限 4096 字节，`-` 表示空 body。只发这一条连接，不进入正式投递、不写库；连接不存在或未进入世界返回 `ERR`。示例：`inject-raw 3 12 0304000006000000544553542D41`（`NOTI 0x000C`，正文 `TEST-A`）、`inject-raw 3 0x0C 03040000 06000000 544553542D41`（同一帧分段书写）、`inject-raw 3 0x0C -`（空 body）。回复给出实际 `opcode`、字节数与规范化 `hex`，日志 `inject_raw` 记录 `Connection`/`Opcode`/`Body` |
| `notice <all\|角色名> <文本>` | 否 | 正式公告：用已实测可见的 `NOTI 0x06AD` 弹框向在线连接发公告。标题固定「系统公告」，正文取命令文本，均按 UTF-8 编码，标题 ≤24、正文 ≤600 字节，超长按字符边界截断并在回执标注 `(truncated)`。`all` 广播给所有已进入世界的在线连接（回执 `recipients` 为入队连接数）；角色名只投该角色当前连接，角色不在线返回 `ERR`。不接受 `account` 前缀，不写库、不改喇叭扣减 |

默认账号是 `10000`，与原生登录的开发账号一致。其他账号在命令前加 `account <账号>`。账号只允许字母、数字、`_` 和 `-`，最长 64。角色名沿用 CP936 名称合同。

调试状态在当前Runtime共享，不写玩家数据库，重启恢复各自默认：绝对日期忽略与无限疲劳为on，相对期限忽略为off。两个期限开关都保持true=忽略，不翻转成“启用期限”。未忽略的相对天数从本次获得/创建时刻计算；忽略相对后仍遵守未忽略的绝对截止。实际声明的期限都被忽略时使用统一安全截止，原永久物品仍为0；未忽略的有限期限也受同一上限约束。完整范围和优先级见[商城期限](../模块/背包与装备.md#npc-商店买卖与限购)，不追溯改写既有物品，也不放行已到期的使用源。

无限疲劳开启时选角与房间通知显示当前实际上限的满值，普通房间不执行疲劳扣点，真实存档及旧账本保留；关闭恢复原准入和扣点，重新选角或下一次房间加载刷新显示。未装配在线服务或离线执行返回错误，不创建数据库冒充设置成功。

进度写入只有`profession`允许改基础job，不改位置或返回点。**改等级保留已学技能**：等级、槽位（快捷栏）、自定义指令与自定义连招原样保留，固定/免费技能按新等级同步，双树 SP/TP 按「新等级总额 + 已登记额外来源 − 已用费用」派生。**只有降级**校验技能等级门限与点数总额，任一已学技能的需求等级高于目标等级、或已用点数超过新总额时整笔拒绝并报告原因（先洗点或用`grow`重置）；升级不做该门限校验，避免存量里技能等级高于角色等级时既不能降也不能升。**转职/觉醒**仍按目标分支与觉醒阶段重建两棵技能树，恢复CHR赠技、固定成长技能和默认槽位，清除付费加点、自定义指令及自定义连招；黑暗武士重新使用资源默认连招。未登记的旧额外来源不从历史余额猜测，明确GM重置时使用0；不会自动修复其它角色的布局。

等级、经验、job、grow、完整双树与主树镜像同事务提交并递增revision；规划在账号锁外进行，提交前复验owner、在线快照和持久学习前提，失败整笔回滚。改等级写目标等级起始经验，改分支/觉醒保留经验和等级。任务、单项技能等其他进度编辑仍保留原revision合同。等级和grow不重算89字节初始属性，选角仍会把出生点设回PVF房间；GM调整不代替正常升级或转职任务。

金币按指定角色执行背包资产事务及审计，实际变化递增角色 revision；同额设置保持只读。提交后同步当前 Selected，客户端重新选择角色后刷新，不即时推送金币差量；同账号其他角色只改其存档，不能覆盖当前 Selected，也不改账号货币。离线操作使用原 owner 拒绝接管合同。数量、归属和持久化规则见[背包与装备](../模块/背包与装备.md)。

道具复用完整创建策略，装备保持品级、耐久、封装和独立 UID，材料使用堆叠数量；时装和宠物保留对应详情。系统邮件最长十五天，物品期限更早时邮件一起到期。GM 投递后不即时提示，重新选择角色后加载邮箱；领取仍走[邮件](../模块/邮件.md)和统一 grant，容量不足保留未领取后缀。六色晶块可邮件托管，领取后进入真实账号钱包，不占背包格；其他未确认的钱包资产和碎片继续拒绝。`item-info`只查询单个ID，不建立全量名称索引；强化走同一创建策略写入Attr低五位，不设置锻造、附魔或增幅。本工具不提供名称搜索、全服发放、背包/仓库删除或直接入包。

`items`不写库：在线时经该账号当前owner读取，当前Selected返回已提交的在线快照，同账号其他角色读取持久状态；离线使用`Reject`租约，账号在线但未经当前服务时拒绝。名称按需取自已加载的物品模板，单行解析失败只留空名称。

## 2. 在线与离线

省略 `--gm-port` 或传入 0 时，服务不监听开发端口。在线调整时，启动参数使用与频道、游戏端口都不同的端口，例如：

```powershell
./start-server.bat --gm-port 23102
```

服务只在 `127.0.0.1` 上接受一行 UTF-8 命令并回一行结果。它不跟随 `ListenAddress`，开发修改后统一重新选角刷新客户端。隔离测试默认关闭 GM，相关专项测试使用各自的空闲端口。

同一条命令的两条路径：

1. **服务正在运行且 GmPort 已打开。** 命令连到本机端口。角色若已在当前连接里选中，提交后的等级和任务会立刻用于服务端通行校验；客户端画面仍显示选角时收到的投影。回到选角并再次进入后，名册、Actor、Player、356 和 1382 才按新存档刷新。
2. **服务没有运行。** 命令取得程序目录下 `Data/server.lock` 后写 `Data/game.db`。下次进入游戏时从数据库加载。若锁被占用，说明实例仍在运行：命令前传入与实例一致的 `--gm-port`，或停止该实例后执行。

在线写入走该账号当前的owner。命令使用`Reject`，不会把正在游戏的连接挤下线。旧generation的会话不能写入。重置提交后将实际角色和新revision安装到原Selected，之后移动使用新revision；同账号其他角色调整不会替换Selected。开发写入若发现revision、学习前提或账号已经变化，整次事务失败。

成功行以 `OK ` 开始。失败行以 `ERR ` 开始，进程退出码为 1。`help` 只打印用法。

代码按职责分为 [DevelopmentRequest](../../Server/DfoServer/Development/DevelopmentRequest.cs) 的行命令解析、[DevelopmentGm](../../Server/DfoServer/Development/DevelopmentGm.cs)及其[资产业务](../../Server/DfoServer/Development/DevelopmentGm.Assets.cs)的执行与结果文本，以及 [Network/Management](../../Server/DfoServer/Network/Management) 的 TCP 传输。进度请求转换成 CharacterProgressEdit，金币及物品使用 Game 的类型化资产和邮件入口；在线状态和 Repository 不依赖 GM 文本命令类型。

## 3. 命令

以下为离线命令，从仓库根目录执行；在线命令在 `--gm` 前加上 `--gm-port 23102`（与服务启动端口一致）：

```powershell
dotnet run --project Server/DfoServer -- --gm help
dotnet run --project Server/DfoServer -- --gm list
dotnet run --project Server/DfoServer -- --gm show 测试角色
dotnet run --project Server/DfoServer -- --gm-port 23102 --gm chat-inject 7 L2 TEST
dotnet run --project Server/DfoServer -- --gm level 测试角色 50
dotnet run --project Server/DfoServer -- --gm story 测试角色 50
dotnet run --project Server/DfoServer -- --gm grow-options 测试角色
dotnet run --project Server/DfoServer -- --gm grow 测试角色 1 2
dotnet run --project Server/DfoServer -- --gm quest-add 测试角色 10100
dotnet run --project Server/DfoServer -- --gm skill 测试角色 179 7
dotnet run --project Server/DfoServer -- --gm gold 测试角色 1000000
dotnet run --project Server/DfoServer -- --gm item 测试角色 18400 2
dotnet run --project Server/DfoServer -- --gm account other-1 list
dotnet run --project Server/DfoServer -- --gm appearance 测试角色
```

剑影这类实验角色仍用[离线准备](离线实验角色.md)。本工具只改已经存在的角色，不创建 Lv95 任务集，也不代替那次准备的备份和审计。

## 4. 单独的本机网页

GM 网页内嵌在服务进程中：服务端以 `--gm-web-port <端口>` 启动即在 `127.0.0.1` 提供页面（默认 0 = 关闭，须与频道、游戏、GM 命令端口都不同），复用已加载的 PVF 和同一存档仓库，无需 Python 或独立进程。通常 `--gm-port` 与 `--gm-web-port` 一起传，例如 `./start-server.bat --gm-port 23102 --gm-web-port 23103`，浏览器打开 `http://127.0.0.1:23103/`。无参数运行 `start-server.bat/.sh` 时默认带这两个端口并打印地址。

GM 网页同一 HTTP 端口同时提供收发包记录、客户端过滤与抓包状态查询，不需要额外开启包查询监听；路径、安全边界和保留上限见[包体抓包](环境与配置.md#包体抓包)。GM 命令端口仍为行文本 TCP，不承载 HTTP。

GM 网页的物品图标读取客户端 `ImagePacks2`：设置环境变量 `DFO_IMAGEPACKS2` 指向该目录，未设置时从程序目录向上搜索（含各级子目录与孙目录）；找不到时页面显示占位图标，不影响其他功能。删除物品/清空容器/删邮件/直接入包在命令层只允许离线（选角）角色，网页在角色在线时会在确认窗提示并先强制小退再执行，不会直接改在线角色的存档。邮件发放成功后会提醒在线的收件角色（MAILBOX_ALARM）。

Windows 启动器（`Tools/ServerLauncher`，即单 EXE 的界面）勾选“启用 GM 工具（仅本机）”后，启动服务时向服务端传入 `--gm-port` 和 `--gm-web-port`（沿用 `launcher.local.json` 的 `GmPort`/`GmWebPort`，默认 23102/23103，旧配置缺少 GM 字段时保持关闭），“打开 GM 网页”按钮打开 `http://127.0.0.1:{GmWebPort}/`。启动器不再自己托管网页，原 `GmWebHost.cs` 已停用，仅保留给 `--selftest-gm` 的旧测试，待确认后删除。停服后才能改变 GM 开关或端口。内嵌页面只监听 `127.0.0.1`；写命令返回不明时先读取状态核对，不自动重试。`--selftest-gm` 检查配置兼容、传给服务端的参数及旧代理的兼容测试，只使用隔离的合成监听，不操作玩家数据。

网页“商城调试”和“疲劳调试”卡片无需选择角色，初次读取服务端状态后才允许切换。应用后显示服务端返回的实际状态；“读取状态”可查看其他管理操作后的值，作用范围及重启恢复行为直接显示在卡片中。定向 `--selftest-tcp gm-debug` 验证两个默认值、关闭恢复疲劳规则及服务重建。

内嵌网页包含概览 #overview、账号与角色 #accounts、角色工作台 #char、物品库 #items、任务 #quests、成套发放 #grant、邮件 #mail、活动 #events、审计日志 #audit，左侧导航加 Ctrl+K 命令面板，点行弹出右侧角色抽屉。写操作（邮件发放物品/套装/金币、强制小退、金币/等级/转职）都在抽屉或确认框内二次确认，要求自定义请求头 X-GM-Request 与 confirm=true，并追加到数据目录 gm-audit.jsonl（时间、本机操作者、对象、命令、前后摘要、返回）。其余写操作列入 [GM迁移对照](GM迁移对照.md) 的待做清单。

“活动”页提供诺兰常驻的显示、隐藏及恢复默认显示，无需输入GM命令；设置保存后，重新选择角色或换频道刷新。黄金卡牌双倍保留独立开关和起止时间。`GET /api/events`兼容原64查询，`GET /api/events?id=2532`读取诺兰状态；`POST /api/events`仅接受64或2532，2532只允许on/off/auto，写操作的审计前后摘要读取本次活动编号。

**待删除：**[GmWeb](../../Tools/GmWeb/gm_web.py)（Python 版，含 `index.html`）已被服务端内嵌 GM 网页取代，保留仅供迁移期对照，待用户确认后删除（见 [GM迁移对照](GM迁移对照.md)）。下文仍可临时使用：它只在 `127.0.0.1` 提供页面，把表单转成同一行 GM 命令。服务端须通过 `--gm-port` 打开本机端口，网页的 `--gm-port` 指向相同端口，默认 23102。与内嵌网页同时使用时选择不同网页端口。

在仓库根目录执行：

```powershell
python3 Tools/GmWeb/gm_web.py --gm-port 23102
```

浏览器打开 `http://127.0.0.1:23103`。`--port` 可改网页端口，默认 23103，且不能与 `GmPort` 相同。自检不启动游戏服务：

```powershell
python3 Tools/GmWeb/gm_web.py --self-check
```

网页分为角色概览、物品与背包、等级与职业、任务与技能、会员与契约、全服调试六个页签，左侧固定账号/角色列表与结果区，结果区保留最近20条命令记录。物品页可先`item-info`查询名称、种类与堆叠上限，普通装备才开放强化输入；“读取背包”按容器筛选只读列表，点击行回填道具ID。等级/职业重置及清空任务执行前再次确认。会员页调用已有`contract`、`black-diamond`、`gold-card`，全服页额外提供活动64的状态/开/关/恢复时间表，时间区间仍用命令行`event schedule`。网页只拼接已有命令行，所有校验与事务仍在服务端。

网页选择角色后读取当前基础职业的分支及觉醒名称，分别下拉选择，不要求手工计算grow字节。普通基础分支只有未觉醒；特殊职业按实际CHR声明提供觉醒选项。界面沿用已有卡片、颜色、间距和控件样式，调整按钮标明重置技能，角色摘要显示主树SP/TP。此旧网页仅编辑当前基础职业分支；完整基础职业名册与跨job转换由下述内嵌HTTP接口提供，不伪造本版未声明的分支。

已有完整双技能树的角色，单项技能编辑只覆盖两树已存在的同ID等级且保留SP/TP；新增没有正式资源槽位资格的ID及删除最后绑定拒绝。未初始化旧角色保留原编辑合同。等级/职业重置使用前述完整重建事务，不替代[在线学习](../模块/技能学习.md)或正常升级。验证入口为DevelopmentGmTests、DevelopmentAssetTests（含`items`/`item-info`/强化发放）、CharacterProgressResetTests及隔离TCP组`gm-progress`、`gm-assets`；网页代理自检与当前客户端重新选角验收分别报告。

## 第二轮移植：检索、货币、发放、清理、邮箱与任务

以下命令同样只走本机 GM 端口，全部经各模块服务入口提交，不写裸 SQL。

| 命令 | 说明 |
| --- | --- |
| `wallet <角色>` | 读取金币、复活币、胜点、账号级货币（点券、代币券、晶块、灵魂等）及黑钻到期时间与是否有效 |
| `currency <角色> <键> <set\|add\|sub> <数量>` | 修改单项货币；结果越出 0–2147483647 时整笔拒绝 |
| `item-categories` / `item-search <偏移> <最低> <最高> <稀有度\|*> <分类\|*> [关键字]` | 按名称/ID、分类、等级、稀有度检索，每页 50 条 |
| `grant <角色> <ID> <数量> <强化> <锻造> <mail\|direct> [amp:<类型1-4>:<值>] [seal:open] [ability:<选项>] [period:off] [expire:<Unix秒>] [quality:<1-100>]` | 邮件或直接入包；锻造只允许普通武器且不超过资源上限。可选参数各自最多一次、可任意组合；`quality` 只对普通装备生效，百分比换算成实例品级种子（100 = 最上级）；非装备忽略有效的品级参数，错误范围仍拒绝 |
| `set-grant <角色> <套装> <强化> <锻造> <mail\|direct>` | 按当前职业每部位一件；邮件方式超过 10 件时拆成多封（单封邮件原子投递） |
| `item-delete <角色> <容器:位置:UID,…>` / `item-clear <角色> <容器>` | 按现有删除规划整批删除；UID 不符或锁定时整批保留，锁定物品保留 |
| `mails <角色>` / `mail-delete <角色> <ID,…\|all> [force]` | 只读查看邮箱，回包含 `deletable` 与附件明细（物品、数量、强化、增幅、是否过期）；删除走 `MailboxService.ChangeState`，有未领取资产或已过期的邮件默认拒绝删除，`force` 时连同附件与金币一起删除 |
| `quest-search` / `quest-chain` / `quest-batch` | 任务名称检索；整链按职业前置组展开；批量最多 3000 个，同一事务完成。`quest-batch`/`quest-chain`跳过每日和`[normaly repeat]`，避免写成 `daily_period=0` 的永久完成导致当天无法再接。单独 `quest-add` 对这类任务写入当天游戏日周期。网页“全部完成”的 `ids` 同样不含每日/重复。 |

### 在线路由规则

- 账号在线：命令交给该会话 owner 执行（`GameSession.Development.cs`）；离线：以 `DuplicateLoginPolicy.Reject` 取租约后提交。
- 货币与金币遵循旧约定，角色在游戏中也允许修改，提交后刷新会话快照与账号钱包。
- 背包实例写入（直接入包、删除、清空）与邮件删除会拒绝“正在游戏中”的角色，需先回到选角；邮件方式发放不受影响。

### 检索索引

`DevelopmentCatalog` 在首次请求时于后台线程构建物品索引（ID、名称、分类、等级、稀有度、套装、职业）与任务名称索引，
构建完成前命令返回“正在后台构建，请几秒后重试”，网页会自动重试；避免占满 GM 的 5 秒超时。索引只保存精简字段，常驻内存约几十 MB 以内。

### 未移植

- 物品图标：需要 ImagePack 解码与客户端资源，服务端不携带客户端资产，暂不实现；预览只显示 `item-info` 的基础字段。
- 不带角色的账号货币修改、跨邮件的整套原子投递暂不支持。

网页支持 `#pane-items` 这类地址片段直接打开对应页签。

## 增幅（红字）发放与修改

- 模型核对：`ItemCore` 已有 `AmplifyType:u8 / AmplifyValue:u16`，库存/邮件/地面/任务奖励/称号簿等封包均按完整核心输出，持久化随核心保存；强化与增幅共用 `Attr` 低五位等级，`AmplifyType` 非0即为增幅装备，因此二者天然互斥。
- 类型编号依据本版 `etc/amplifyitem.etc` 的 `[option mapping table]`：1 physical defense（体力）、2 magical defense（精神）、3 physical attack（力量）、4 magical attack（智力）。高位 0x80 为未净化状态，GM 不生成。客户端实际显示仍待实机确认。
- 异次元基础值：本版资源有 `[amplification rate by rarity]`、`[amplification weight by level]`、`[rarity weight]` 等声明，但随机生成公式尚未闭合（**待确认**），GM 不自动计算，须显式给出 1–65535。
- `grant … <mail|direct> amp:<类型1-4>:<值>`：等级参数即增幅等级；只允许普通装备。直接入包时只给本次新建的装备实例写入，与发放同一计划提交。
- `set-grant … amp:<类型>:<值>`：只作用于套装中的普通装备。
- `item-amplify <角色> <容器:位置:UID> amp:<类型0-4>:<值> <等级0-31>`：修改已有普通装备；类型0表示改回强化并清除红字（值须为0）。校验 UID、装备锁，保留 UID、Attr 高三位及其他核心字段；与其他背包实例写入一样拒绝正在游戏中的角色。

## 默认邮件发放

- 网页发放默认“系统邮件”，直接入包是显式的次选项；邮件发放不经角色会话，角色在游戏中也可发放，重新选择角色后领取。
- 整套邮件发放超过 10 件时按顺序拆成多封，每封各自原子投递；整套不是全有或全无，若中途某封失败，返回已投递的邮件编号并标记 `partial`。


## 称号簿、属性、金库与高级筛选（内嵌 GM 网页）

- 称号：命令 title-list <角色> [分类或关键字]（只读）、title-complete <角色> <成就ID,...|all|book|book:分类>（直接完成成就并发放称号、奖励与邮件；须在选角界面，网页可先强制小退）。
- 属性：stats <角色>，服务端只计算等级与转职的基础属性，不含装备、技能、称号、套装修正。
- 本服装备建设分：`equipment-score <角色>`，只读服务器穿戴快照，返回版本、捕获revision、12槽分项及Complete/Partial/Invalid和封顶提示；[规则与排除项](../模块/角色属性.md#本服装备建设分-v1)。不写存档，不是原版战斗力或玩家原生面板。
- 账号金库：currency <角色> vault-gold set|add|sub <数量>（上限为当前容量档位，在线安全）；item-clear 支持容器 AccountCargo。
- 高级筛选：item-filter <偏移> <最低> <最高> <稀有度|*> <分类|*> <词条|*> <期限*|none|rel|abs|any> <技能职业*|all|职业> [关键字]；属性索引首次使用时后台构建，未就绪会提示稍后重试。
- 发放：grant 可选 amp:<类型>:<值>、seal:open（仅 [sealing] 封装装备）、ability:<选项>、period:off、expire:<Unix秒> 与 quality:<1-100>（仅普通装备，100 = 最上级）。

## 第三轮补充

- `skill-list <角色>`：技能名称（PVF）、等级与 SP/TP，只读，在线离线都可。
- `exp-table`：Lv1..95 起始累计经验与升级所需经验，只读。
- 网页新增 `POST /api/exp`（按累计经验设等级）、`POST /api/disconnect`（断开账号连接）、`GET /api/audit` 的筛选与 CSV 导出。
- 称号成就对在线角色可直接完成（同步内存快照，客户端画面重新选角后刷新）。

## 扩展装备栏（GM 网页接口）

基础职业接口：`GET /api/professions`返回`{ok:true,data:{baseJobChange:true,jobs:[{job,name,branches:[{branch,name,awakenings:[{stage,name,growType}]}]}]}}`，名册来自本次实际加载的职业资源。`POST /api/job`继续要求`X-GM-Request: 1`及`confirm:true`；请求`{account,name,job,branch,awakening,confirm:true}`显式提供`job`时执行`profession`，省略时保留原`grow`行为。离线、穿戴和资源组合拒绝按原错误响应返回并写入审计，不先强制断线。

- `GET /api/characters/{account}/{name}/equip-slots`：只读，返回 `{ok, data:{name, rev, flags, added:0, changed:false, slots:{support, magicStone, earring, aura, creatureSkin}}}`。
- `POST /api/equip-slots`，请求头 `X-GM-Request: 1`，JSON `{account, name, flags, returnFirst?, confirm:true}`。`flags` 只能是 1、2、3、16、17、18、19 之一（1/2/16 的组合），其他值返回 400。角色在游戏中时默认返回 400「角色正在游戏中」；`returnFirst=true` 先强制小退再执行，响应带 `returned:true`。成功 `data` 同上，`added` 为本次新开的位，`changed=false` 表示所选位早已开启。小退与开栏各写一条审计（动作 `force-return`、`equip-slots`）。

## 黑钻会员（GM 网页接口）

- `POST /api/black-diamond`，请求头 `X-GM-Request: 1`，JSON `{account, name, days, confirm:true}`；`days` 0–3650，0 表示清空该账号黑钻到期时间。与命令行 `black-diamond <角色> <天数>` 同一提交入口，写入账号钱包契约类型 56，账号下所有角色共享；天数按「现在 + 天数」覆盖到期时间，不是叠加。审计动作 `black-diamond`，前后摘要取 `wallet`。
- `wallet <角色>` 的 JSON 增加 `blackDiamondExpiresAt`（Unix 秒）与 `blackDiamond`（当前是否有效）；网页“基础”钱包区与“快捷操作 → 黑钻会员”据此显示状态。

## 副职业类型与等级（GM 网页接口）

- 命令：`expert-job <角色>` 只读，返回 `{name, rev, type, typeLabel, expertness, level, maxLevel}`；`expert-job <角色> <类型1-4> <等级1-11>` 写入。类型编号取自 `character/expertjob.lst`：1 附魔师、2 炼金术师、3 分解师、4 控偶师。
- 服务端只持久化累计熟练度（`character_expert_job.expertness`），客户端按 PVF `[expertness exp]` 阈值换算显示等级。写入 N 级时落该等级的最低阈值；本版四职业阈值相同：Lv1=0、Lv2=20、Lv3=133、Lv4=246、Lv5=359、Lv6=472、Lv7=585、Lv8=698、Lv9=811、Lv10=939、Lv11=1067。没有资格时新建行，已有资格时改类型与熟练度，同一事务校验 revision；改类型保留已学附魔设计图。
- `POST /api/expert-job`，请求头 `X-GM-Request: 1`，JSON `{account, name, type, level, confirm:true}`；`type` 1–4，`level` 1–11。审计动作 `expert-job`，前后摘要取只读命令 `expert-job <角色>`。在线角色经会话 owner 提交并同步在线快照，客户端回到选角并再次进入后刷新等级显示。

## GM 网页：索引预热、分页与 UX（摘要）
- 启动即后台预热物品/任务/词条索引并缓存到存档目录（`gm-index-*.json`，按 PVF 大小+修改时间校验），前端只在真正构建时提示。
- 物品、审计、账号、任务均为服务端分页（`page`/`pageSize`，审计用 `X-Total-Count`）。
- 稀有度显示为 普通/高级/稀有/神器/史诗/勇者/传说；零命中词条置灰。
- 任务页：状态筛选、按 PVF `[grade]` 分类（无法归类为“其他”）、勾选批量完成、“全部完成”二次确认并审计。全部完成与跨页全选不含每日/重复；这两类请单独 `quest-add`（当天周期）或不要永久完成。
- 顶部全局角色选择器与整页角色工作台；只有危险操作二次确认。详见 `GM迁移对照.md`。

## 团本入场时段（团队频道卡）

- `GET /api/raid-windows` 读表；`POST /api/raid-windows`（需请求头 `X-GM-Request: 1`，JSON 正文里带 `"confirm": true`）整表替换，写入存档目录 `gm-raid-windows.json` 并审计。键为 `luke` / `hyperspace` / `prey`，每行 `{"weekday":1..7,"start":"HH:mm","end":"HH:mm"}`，UTC+8，结束时刻不含，`24:00` 表示到当天结束；空表全关。
- 时段内的卡在入城/换频道时通过 `NOTI EVENT_INFO` 下发事件 282 / 2449 / 414，去掉「非可入场时间」。已在城镇的角色要换一次频道或重新选角才刷新。
- 例：全天开放超时空漩涡（每天 00:00–24:00）：`{"luke":[],"hyperspace":[{"weekday":1,"start":"00:00","end":"24:00"},…,{"weekday":7,"start":"00:00","end":"24:00"}],"prey":[]}`。协议细节见[帧与频道](../协议/帧与频道.md#团队频道三张卡卢克--超时空漩涡--普雷伊希斯)。

## 纸娃娃（只读，GM 网页接口）

按角色**当前已保存**的外观合成站立帧 PNG，供 WPF 等前端显示。只读：不改库、不写审计；试穿只在内存里替换部位。需要客户端 `ImagePacks2`（`DFO_IMAGEPACKS2` 或自动搜索，同物品图标）。

| 方法与路径 | 参数 | 返回 |
| --- | --- | --- |
| `GET /api/characters/{account}/{name}/portrait` | `tryOn`（可选）：逗号分隔的物品编号，最多 16 个 | `image/png`；响应头 `X-Avatar-Layers: 画出层数/总层数`；没有任何可画图层时 `204`；角色不存在、ImagePacks2 不可用时 `400 {ok:false,error}` |
| `GET /api/characters/{account}/{name}/portrait/layers` | 同上 | `{ok:true,data:{width,height,frame,jobTag,drawn,layers:[{slot,itemId,source,layer,z,img,palette,frame,drawn,note}],notes:[…]}}` |

`source` 取值：`worn` 实穿、`clone` 克隆装扮（画 ClearAvatarId 外观）、`try-on` 试穿、`default` 职业默认外观、`weapon-skin` 武器幻化、`body` 无皮肤装扮时的 body0000。可试穿装扮列表直接用已有 `GET /api/items/search?tag=coat%20avatar&job=<职业>`（tag 依次为 hat/hair/face/coat/pants/shoes/breast/waist/skin/aurora/weapon avatar），未另加接口；`usable job` 允许但没有该职业图层的装扮会在 `notes` 里说明。

外观来源是开发 GM 只读命令 `appearance <角色>`：`{name,job,growType,rev,weaponSkin,equipment:[{slot,id,clear}]}`（0–12 号装备位；在线走原 owner，离线取租约）。

合成规则（全部来自 PVF 与 ImagePacks2）：

- 职业：`character/character.lst` → `.chr`。`[job]` 匹配装扮的 `[animation job]`；`[body image path]` 给出皮肤目录与文件前缀（如 `sm_`、AT 鬼剑 `sg_`）；`[waiting motion]` 第一项 `.ani` 第一帧 `[IMAGE]` 的帧号是合成帧（鬼剑士 90）。
- 装扮：`[variation] a b` → 编号 `a×100+b`，至少 4 位（`110 0` → `11000`，`0 1000` → `1000`）；该文件不存在且 b>0 时取 `a×100` 号文件的第 b 个调色板（v6）。依据：本机 swordman 1934 个武器脚本中能按单层格式解析的 1289 个：538 个直接命中 a×100+b，698 个属于“a×100+b 文件不存在、a×100 文件是调色板数大于 b 的 v6”，52 个找不到对应武器 NPK，仅 1 个两者都不成立（katana `58 9`，a×100 文件是 v4）；`[layer variation] z` 加层名 `coat_c` → `<皮肤目录同级>/coat/<前缀>coat<编号>c.img`。武器层 `[职业前缀_]核心+后缀`：`sswdc` → `Weapon/sswd/sswd0000c.img`，`at_sswda` → `Weapon/sswd/sg_sswd0000a.img`；没有职业前缀时先试不带前缀，再试皮肤前缀（魔法师 `rodc` → `mg_rod0000c.img`、圣职者 `crossc` → `pr_cross0000c.img`，资源里只有带前缀的文件）。对 swordman 抽查：variation≥100 的层 7824 个命中、247 个在全部 swordman NPK 里都不存在（资源本身缺失），未见另一种命名。
- 默认外观：`character/characteretc/<chr名>.etc` 的 `defaultEquipment`（`[equipment index]` 按位置、`[key value index]` 为首值加下标/值对两种写法），按物品自身 `[equipment type]` 归位，部位为空才使用。
- 克隆装扮画 `ClearAvatarId`；武器优先级：武器幻化（武器幻化存储的当前选择，且编号是武器）> 武器装扮(10) > 武器(12) > 默认武器。
- 层按 z 升序叠放，裁到非透明范围外加 4 像素。缺失的 IMG/帧、无法解析的层名跳过，写入 `layers[].note` 并记 `server.jsonl` 的 `gm-avatar`。
- IMG：v2 直接色；v4 单调色板、v6 多调色板（默认第 0 个），压缩后长度等于 w×h 的 0x0E 帧是 1 字节下标，调色板为 RGBA 字节序（按本机 `sm_body0000/0004`、`sm_coat*`、`sswd*` 样本核对）。未压缩帧（compress≠6）实际占 w×h×2（0x10 为 ×4）字节，头里声明的 size 不可信（`gn_coat0000c.img` 的 165 个 1×1 帧声明 4 字节，按 2 字节累计才与文件长度一致）。解码由本仓库自写的 `ImagePack/ImgFrameReader.cs` 完成，物品图标入口的 v4/v6 也转交给它（v2 图标仍走原移植代码）。

已知缺口（未从资源证明，不猜）：

- 皮肤层没有 `[layer variation]`，z 序暂定 1000（低于它的层如武器背面 650、发型后层 400 画在身体后面）；同 z 时先画皮肤。
- 帽子遮发、上衣遮裤等隐藏规则未处理，所有层都画。
- 魔枪士（demoniclancer）、圣职者（女，atpriest）、枪剑士（gunblader）在 PVF 中没有 `characteretc/*.etc`，空部位不画默认外观；觉醒默认外观表 `character/chn_1stawaken_defaultavatarinfo.chr` 未接入。
- 武器幻化只核对“编号是武器”，未复核到期与可穿戴（与 `InventorySkinStorageService.EffectiveSelection` 不同）。
- IMG v5（DDS 纹理表）未实现；`(tn)` 前缀资源、层名如 `hair_f1` 的变体未确认命名，缺失时按上面规则跳过。
- 光环/光环皮肤（9、11 号位）按同一规则尝试，其特效层多数不在装扮目录，常见为缺失跳过。

## 物品检索扩展选项与预览补充（feat/gm-item-filter）

用于让 GM 客户端按迁移前 90 轻装 GM 的规则筛选（多分类并集、武器子类、隐藏占位模板、按 ID 排序）。全部可省略，省略时结果与旧版一致。

- 命令：`item-search` / `item-filter` 的关键字区可追加（与 `job:` / `grow:` 同一写法，空格写作 `_`）：
  - `tags:<分类>[@e|@s],…`：多分类并集，`@e` 只要装备、`@s` 只要道具，也可写 `g:<分组>`；不能与位置参数的单个分类或 `sets` 同时使用。
  - `kind:e|s`、`sub:<武器子类>,…`（PVF 路径 `weapon/` 后的目录名，如 `katana`）、`real:1`（隐藏无名或 `name_数字` 占位模板）、`sort:id|-id|level|-level|rarity|-rarity|name`。
- 网页：`GET /api/items/search` 与 `/api/items/filter` 增加同名参数 `tags`、`kind`、`sub`、`real`、`sort`；结果每行多 `sub`，并回显 `options`（旧服务端没有此字段，客户端据此判断是否支持）。
- `item-categories` 每个分类多 `real`（去掉占位模板后的数量），并新增 `weaponSubs`（武器子类与数量）与 `searchOptions`。
- 修正：关键字只在为正整数时按 ID 匹配。以前非数字关键字 `TryParse` 失败留下 0，会把 ID 0（金币）混进任何中文关键字的结果。
- `GET /api/items/{id}/preview` 增加 `grade`、`durability`、`weight`、`stackLimit`、`price`、`value`、`attachType`（归一后的交易类型）、`trade`（中文交易限制说明，按服务端交易与仓库判定归纳）、`weaponSub`、`booster`（礼包内容：与开箱同一解析，最多 30 项，含数量区间；自选礼包只标注 `selection`）。
- `POST /api/story {account,name,level,confirm}`：设置剧情等级（0–95），调用已有 `story` 命令并写审计。
- 物品索引缓存版本升到 3（新增武器子类字段），首次启动会重建一次 `gm-index-items.json`。

`character-score <角色>`：只读查询 `character-investment/v2`，包含十二装备基础层及称号、时装、宠物、勋章、附魔、徽章、护石符文、收集箱、套装等逐来源建设明细。沿用账号/角色归属与owner快照边界，不修改资产。空模块为0；Partial说明已安装来源仍有缺失或非法定义，不可当完整排名。旧 `equipment-score` 仍返回十二装备v1。规则与限制见[角色属性](../模块/角色属性.md#本服角色建设分-v2)，此命令不是客户端自助查询或原生战斗力面板。

`character-score-notice <角色>`：管理触发当前在线就绪角色的建设分摘要，沿用账号归属验证，使用与 `character-score` 相同的真实 v2 计算。通过既有 AAS_TIPS 弹框投递，不接管聊天文本，也不自动在登录或换装时弹出。结果 `queued` 只表示发送队列已接受，`DeliveryConfirmed=false`，不确认客户端接收/显示；`queue_rejected`、`expired` 或身份变化状态需手动重新查询。摘要优先显示 Partial/封顶、分数及版本，UTF-8 正文最多600字节；完整逐来源明细仍查 JSON。

## 卢克历史已赢得奖励的显式领取

仅已授权开发/管理人员使用现有本机GM入口；不新增公网API、游戏opcode或玩家自助命令。管理监听仍只接受loopback，离线执行仍取得原`server.lock`；账号租约采用拒绝重复而非接管。检查和领取均要求账号没有已选角色（在线先回到选角，或完全离线），不能在同账号另一角色仍在线时修改共享钱包。

- `luke-reward-list <角色名> <角色ID> [limit:1..16] [after:N]`：首参数是角色名而不是账号；账号沿既有 `account <账号> luke-reward-list …` 前缀指定（省略时使用当前管理命令默认账号）。只读发现该账号角色的历史资格，默认最多16个实例，按实例主键分页；`NextAfter` 非空时原样用于下一页（允许末尾空页）。返回原阶段event/anchor、全部保存备选、已领取kind/card及原邮件回执；`AvailableOffers` 仅排除已领取kind，不自动选牌或调用领取。只依赖保存历史，不装载当前PVF；不是普通玩家聊天命令。
- `luke-reward-inspect <角色名> <角色ID> <instance:N> <phase:0|1> <kind> <card>`：只检查明确原资格与领取回执，返回原event、anchor、计划与是否已领。`N`为32位无连字符GUID；不搜索最近实例或自动选择牌面。
- `luke-reward-claim <角色名> <角色ID> <instance:N> <phase:0|1> <kind> <card> <event:N> <anchor>`：管理员确认检查结果后显式领取，event/anchor必须来自同一原资格。其他账号沿已有`account <账号>`前缀指定。

只使用已持久提交且账号/角色/实例/阶段完全一致的原资格，不签发资格、不恢复旧Battle、不重扣门票、不新增通关次数，不把状态关闭或跨周理解为应重造奖励。同kind只能选择原有一张card；另一kind保留其独立权益。库存及原满包邮件与奖励回执同一既有事务提交，不走额外补偿邮件。

已成功请求优先原样重放receipt/mailIDs，不再因模板变化或资源未装配重新规划；换card拒绝。尚未领取才基于原grants和当前合法物品目录规划，无法规划明确失败，不换物品/数量。账号接管、角色版本或原资格变化均拒绝。SQL成功后管理响应丢失可用同一完整请求重试，不重复发奖。

不发送猜测的600/601或自动选牌；正常重新选角后由既有库存载入显示资产。原生客户端660没有历史instance参数，仍不承担此恢复入口。完整卢克默认入口保护保持不变。
