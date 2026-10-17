# GM 迁移对照

内嵌 GM 网页的功能清单与进度。早期曾对照朋友工具 S5A9GmTool 的接口清单；该工具已被用户判定为不完善，后续不再作为依据，改以服务端自己的模型和 PvfLib 实现，能复用现有能力的优先复用。实际引用过其代码的只有 ImagePack（图标读取）与属性映射表（见仓库根的 THIRD-PARTY 说明，保留 MIT 许可）。下面“逐接口对照”表是早期状态记录，状态列以当前实现为准。

## 部署与启动

| 项目 | 状态 | 说明 |
| --- | --- | --- |
| 服务端内嵌 GM 网页（`--gm-web-port`，仅 127.0.0.1） | 已完成 | 七个模块页面 + 活动页；读取、邮件发放、入包发放、删除/清空、任务、SP/TP、钱包、等级、转职、改名、删除角色、活动、快捷物品、小退等写操作均二次确认并写审计；见下表 |
| `Tools/ServerLauncher` 传入 `--gm-web-port` 并打开内嵌网页 | 已完成 | 沿用 `launcher.local.json` 的 `GmPort`/`GmWebPort` |
| 启动器自带 `GmWebHost` | 已停用 | `GmWebHost.cs` 保留，仅被 `--selftest-gm` 旧测试引用，待用户确认后删除 |
| `Build-Server.ps1`、`start-server.bat/.sh`、`Start-Server.ps1` | 已完成 | 默认带 `--gm-web-port` 并说明地址 |
| `Tools/GmWeb`（`gm_web.py`、`index.html`） | 待删除 | 已标注，等用户确认；未删除 |
| 95JP-Launcher（独立仓库） | 已完成（分支 feat/builtin-gm-web） | 服务端 `--help` 含 `--gm-web-port` 时传参并打开内嵌网页，否则沿用旧逻辑 |

## 朋友工具接口对照（S5A9GmTool/Program.cs）

新接口前缀为 `/api/`，默认只绑 127.0.0.1。“邮件”指走 MailboxService.Deliver 的发放，“在线提交”指开发 GM 的 live/WithLease 路径。

| 原接口 | 状态 | 新实现或原因 |
| --- | --- | --- |
| `GET /api/status` | 已完成 | `/api/status`：PID、端口、PVF 路径、存档目录、在线数 |
| `POST /api/auth/login`、`/logout` | 放弃 | 内嵌后只绑本机，用 Host 校验、X-GM-Request 头和同源 Origin 取代登录；远程访问已取消 |
| `GET/POST /api/environment`、`/environment/browse` | 放弃 | 不再切换数据源或浏览路径，直接使用服务端启动时已加载的 PVF 和同一存档 |
| `GET /api/currencies` | 待做 | 钱包只读已含货币列表（`/characters/{账号}/{角色}/wallet`），独立货币字典接口不需要 |
| `GET /api/accounts`（含搜索 q） | 已完成 | `/api/accounts`；搜索由前端过滤与 Ctrl+K 面板完成 |
| `GET /api/accounts/{account}/detail` | 已完成 | `/api/accounts/{账号}/characters` |
| `POST /api/accounts/{account}/currency` 账号级写入 | 已完成 | `/api/account-currency`：先读钱包确认该货币是账号级，再通过账号首个角色走 currency 命令；角色抽屉钱包表「修改」按钮 |
| `GET /api/characters?accountId=` | 已完成 | 同上 characters 接口 |
| `GET /api/characters/{id}` | 已完成 | `/characters/{账号}/{角色}/summary`（角色按账号+名称定位，不用数字 ID） |
| `GET .../items`（背包） | 已完成 | `.../inventory` 只读 |
| `GET .../growoptions` | 已完成 | `/api/characters/{账号}/{角色}/growth` |
| `GET .../stats`、`GET .../sptp` | 已完成 | `/api/characters/{账号}/{角色}/points`（命令 `points`，额外 SP/TP 与当前 SP/TP，只读）；属性面板仍未迁移 |
| `POST .../wallet`、`/currency` | 已完成 | `/api/currency`（在线提交/租约路径）、`/api/gold` |
| `POST .../level`、`/progress` | 已完成 | `/api/level`（等级重置，保留技能） |
| `POST .../growtype` | 已完成 | `/api/job`（分支+觉醒） |
| `POST .../sp`（额外 SP/TP） | 已完成 | `/api/sp`、`/api/tp`（0–50000），走命令 sp/tp → 在线 `UpdateProgress` 或离线租约，抽屉「操作」页 |
| `POST .../grant`（发放） | 已完成 | 邮件：`/api/mail/item`、`/mail/set`、`/mail/gold`；入包：`/api/direct/item`、`/direct/set`（命令层拒绝在游戏中的角色；在线时确认窗会提示并带 `returnFirst` 先强制小退再入包）。发放页有「发放方式」选择 |
| `POST .../items/delete`、`/items/…` 清空 | 已完成 | `/api/items/delete`、`/api/items/clear`（背包/时装/宠物/个人仓库，已锁定装备不删）；背包页抽屉内勾选删除，输入角色名二次确认；在线角色先强制小退（不直接改在线库） |
| `GET .../mail` | 已完成 | `.../mails` 只读 |
| `POST .../mail/delete`、`/clear` | 已完成 | `/api/mails/delete`（ID 或全部可删）；仅无附件、无金币且未过期的邮件可删；在线角色同样先强制小退 |
| `GET .../quests`、`/quests/{mode}` | 已完成 | `.../quest-search`（含状态：已完成/进行中/未接）与 `.../quests` |
| `POST .../quests/{quest}/{action}`、`complete-chain`、`complete-batch` | 已完成 | `/api/quests/add|remove|chain|batch|clear`（标记完成/取消完成/连锁/批量≤60/清空），命令层本身在线安全（在线 owner 内提交） |
| `GET /api/quests` | 已完成 | 由 quest-search 覆盖 |
| `GET /api/items`、`/items/browse`、`/items/categories` | 已完成 | `/api/items/search`、`/api/items/categories`（基于服务端已加载的物品目录；索引在后台构建，未就绪时提示重试） |
| `GET /api/items/{id}/preview` | 已完成（见待办） | `/api/items/{id}/preview`：按需读取 PVF 脚本（名称/类型/等级/稀有度/职业/说明/风味文字/有效期）和属性行（沿用朋友工具的属性映射表与显示顺序，对全脚本中 `[属性] 数值` 片段合并）。未迁移：附魔表/套装加成文本/头像属性/技能加成等特殊行（朋友的 PreviewScript 树解析未整体移植） |
| `GET /api/items/{id}/icon`、`/api/preview/chrome/window` | 图标已完成，窗口外框未做 | `/api/items/{id}/icon`：移植 ImagePack（NPK 读取/名称解密/解码/PNG，MIT）；目录取环境变量 `DFO_IMAGEPACKS2`，否则从程序目录向上搜索 `ImagePacks2`；找不到时返回 204，前端显示占位。外框预览窗口不需要 |
| （新增）强制小退 | 已完成 | `/api/kick` |
| （新增）审计日志 | 已完成 | `/api/audit`，数据目录 `gm-audit.jsonl` |

## 86 工具功能核对（S4A21GmTool，只读参考其行为，按 95 服务端自己的模型实现）

| 86 功能 | 状态 | 说明（95 服务端实际情况） |
| --- | --- | --- |
| 角色改名 | 已完成 | `rename` 命令 + `/api/characters/rename`：复用 `CharacterName.IsValid`，`characters.name` NOCASE 唯一，同事务校验 revision，同步拍卖行卖家名；邮件历史发件人名保持快照。在线角色先小退（`returnFirst`），改名后推进选角纪元让旧名册作废。新增 `ICharacterRenamer`，不改现有仓库包装类 |
| 删除角色 | 已完成（注意不是软删） | 95 的 `CharacterRepository.Delete` 就是永久删除（没有软删字段），GM 复用它：`char-delete <角色> <确认名>` + `/api/characters/delete`，必须输入一致的角色名，审计保存删除前快照；冒险团远征中拒绝；在线先小退 |
| 副职业类型/等级/经验/一键满级 | 不适用 | 95 没有副职业（专家职业）进度存储，只有专家契约剩余天数（`contract` 命令，已有） |
| 称号簿与成就（一键称号簿/成就完成） | 已完成 | `title-list`/`title-complete` + `/api/titles/complete`、角色抽屉「称号」页。复用 `TitleAchievementService`（新增 `Complete`，不检查玩法条件，奖励/称号/章节/邮件沿用同一规划）与 `CommitTitleAchievement`；范围 all/book/book:分类/ID 列表；整批被拒时逐个试探并回报跳过数；仅选角界面可写，在线走 returnFirst 小退；二次确认并审计。不做“自动入簿”：称号由成就奖励直接入簿 |
| 角色属性块只读 | 部分 | `stats` + `/api/characters/{账号}/{角色}/stats` + 「属性」页，调用服务端 `CharacterStatComputer`。服务端本身只计算等级/转职的 CHR 基础属性；装备、技能、称号、套装等修正由客户端派生，95 服务端没有对应计算，所以不显示（页面明确标注） |
| 活动开关读写 | 已完成 | `/api/events`、活动页：服务端目前只注册 64 号（金币卡双倍）活动，支持强制开/关/自动与起止时间 |
| 物品库高级筛选 | 已完成 | `item-filter` + `/api/items/filter`：词条（GmItemStatSpecs 属性键，含时装可选属性）、期限（无/使用后计时/绝对到期日/任意）、技能职业（`[skill levelup]`/`[skill data up]` 职业或 `[all skill item]`）。索引在第一次高级筛选时才后台全量扫描 PVF 构建（约 260k 个脚本，数十秒，只保存有属性的条目），未就绪时提示稍后重试，不占启动内存/时间 |
| 装备发放高级选项 | 部分 | 已开放：强化(`Attr` 低 5 位)、锻造(`GenuineUpgrade`)、增幅(`AmplifyType/Value`)、封印(`seal:open`，仅模板 `[sealing]` 封装装备，直接发为已解封，与称号簿穿戴消封同一语义)。未开放及原因见下方 ItemCore 字段表 |
| 快捷物品（gm-quick-items.json） | 已完成 | `/api/quick-items`，文件在存档目录，原子写入，最多 100 项，发放页一键填入/保存 |
| 荣誉等级 | 不适用 | 95 服务端没有荣誉等级数据 |
| 成长胶囊 | 未做 | 95 有的是冒险团胶囊（冒险团商店体系），与 86 的成长胶囊概念不同，未对应 |
| 晶块/灵魂/史诗碎片 | 已覆盖 | 晶块、灵魂是钱包货币（`currency` 命令，角色抽屉钱包表可改）；史诗碎片作为物品发放，没有独立簿记写入 |
| 账号金库 | 已完成 | 金币：`currency <角色> vault-gold set/add/sub` + `/api/vault-gold`，上限取当前容量档位 `GoldLimit`，未开通拒绝，在线经 `CommitDevelopmentInventory` 同步；清空：容器「账号金库」走 `item-clear`（须选角，可 returnFirst）。扩容档位不做 |
| 明确不做 | — | 在线点券津贴、安全模块（封禁/观察/豁免）、重置密码、注册策略与邀请码、解锁全部副本难度、不兼容物品扫描、登录器更新、远程登录与数据源切换 |


## ItemCore 字段与发放选项（逐项核对）

| 字段 | 处理 | 原因 |
| --- | --- | --- |
| `Attr`（低 5 位强化） | 已开放 | 语义清楚，0–31 |
| `GenuineUpgrade` | 已开放（“锻造”） | 上限取资源 `Refinement.MaximumLevel`，与服务端锻造一致 |
| `AmplifyType/Value` | 已开放（`amp:`） | 仅普通装备，类型 1–4 |
| `SealFlag` | 部分开放 | 仅 `[sealing]` 装备可发为已解封；其他物品 SealFlag 无布尔封装语义（宠物复用为类型） |
| `ExpireTime` | 不开放 | 期限由模板 `usable period`/`expiration date` 决定；自定义期限会绕过过期规则，且服务端没有 GM 改期限的既有语义 |
| `Durability`、`EnchantCardId/Upgrade`、`Emblem*`、`Rune`、`Random0-2/RandomChange*`、`Chronicle0/1` | 不开放 | 对应附魔、徽章、符文、随机词条、纪元选项，各有独立的规则与资源校验链，直接写字段会产生非法组合；95 无“品级”字段（模板 `Grade`/`Rarity` 只读） |
| `EmancipateLevel`、`TradeRestriction`、`EquipmentLockId`、`SortLockFlag`、`RemainUseCount` | 不开放 | 交易/锁定/次数状态由玩法产生，GM 手填会破坏账号/拍卖行规则；“传承”在 95 没有可确认的对应字段 |
| `Extension*`、`Marker16`、`GuardianGemKeys` | 不开放 | 客户端保留/未明确语义字段 |

## 深色模式验证

headless Edge（CDP）读取 computed style：系统浅色、系统深色、已保存深色、已保存浅色四种场景下 `data-theme` 与 `body` 背景色均符合预期（脚本 `build/gm-tmp/theme-check.js`）。


## 待做清单

1. 物品预览：附魔块、时装可选属性、技能加成、套装件数效果已从已加载 PVF 解析（`[enchant]`、`[avatar select ability]`、`[skill levelup]`/`[skill data up]`/`[all skill item]`、`etc/equipmentpartset.etc` + `[piece set ability]`）；技能只显示编号，技能名未解析；绝对到期日按文本读取（此前误按整数）。
2. 图标目录目前靠环境变量或自动搜索；如需页面内配置，可再加 `--gm-image-packs` 选项。
3. 邮件金币上限：发放时按收件人当前等级的携带上限拦截；领取时服务端仍按领取时的背包金币与上限再校验，超限则邮件保留，不会丢币。
4. 新邮件提醒：GM 发邮件成功后调用在线会话的 `NotifyMailbox`（MAILBOX_ALARM）；仅对该账号当前在线且已选中该角色的会话生效，离线角色在下次进入时由未读邮件数提示。
5. 删除 `Tools/GmWeb` 与 `GmWebHost.cs` 须等用户确认。

## 候选功能落地情况（第三轮）

| 候选 | 结果 | 说明 |
|---|---|---|
| 技能名、物品名显示 | 已完成 | `skill-list <角色>` + `GET /api/characters/{账号}/{角色}/skill-list` + 抽屉「技能」页：名称取 `skill/<职业>list.lst` 指向的 `.skl` 的 `[name]` 段（`DevelopmentCatalog.SkillName`，读不到回退“技能 ID”），同页显示当前/额外 SP·TP。物品名此前已有。 |
| 经验与等级联动 | 已完成 | `exp-table` + `GET /api/exp-table`（Lv1..95 起始累计经验与升级所需经验，来自 `CharacterGrowthCatalog`，type9 高/低 DWORD 已由 Catalog 组合，u64 不截断）；`POST /api/exp` 按累计经验换算等级后走 `level`（等级内经验归零，与 `level` 命令一致）；抽屉「等级调整」显示该级起始经验与距下一级。 |
| 索引构建期自动重试 | 已完成 | 前端 `api.js`：GET 遇到“后台构建”指数退避重试（0.5s 起、最长 8s、最多 8 次）并在顶部显示进度条；GET 遇到“账号正在使用”(409) 短重试 3 次。 |
| 在线角色 SP/TP、任务、称号 | 已完成 | SP/TP、任务、等级早已走 `GameSession.UpdateProgress/ResetProgress`（提交后同步内存 Selected 与已完成任务集合）。本轮把称号成就改为在线可直接完成：`GameSession.CommitDevelopmentTitle` 在本会话 owner 内基于最新快照规划并提交，同步 Selected，有待确认的业务结果时拒绝；其余在线拒绝规则不变。客户端称号簿画面不推送，重新选角后刷新（与等级/任务一致，未发明未取证的推送包）。UI 在「操作」页标明“在线可直接改/需先小退”。 |
| 技能等级直写（单技能/整树/重置） | 部分 | 重置：转职面板即 `grow` 重建双技能树并恢复完整 SP/TP（在线可用）。单技能与整树直写**未做**：现有 `skill` 命令只改 `character_skills`/绑定表，不经 `SkillLearning` 账本，绕过费用、前置与 SP/TP 扣点；正确做法是新增基于 `SkillLearning.Plan` 的学习编辑器，并取证 NOTI19/NOTI20 的客户端刷新，当前没有这份证据，故不做。 |
| 审计页导出/筛选/撤销提示 | 已完成 | `GET /api/audit` 支持 `account/action/from/to/ok/limit`（最多 5000）与 `format=csv`（含 BOM，防公式注入前缀）；审计页提供筛选、JSON/CSV 导出，详情抽屉按操作给出反向命令提示（只提示，不自动执行）。 |
| 在线列表踢人 | 已完成 | 已有“强制小退”；新增 `POST /api/disconnect`（二次确认+审计 `disconnect`）：调用该账号会话的断线回调，即重复登录 `ReplaceExisting` 顶号所用的同一路径，账号所有在线角色掉线。 |
| 广播公告 | 不做 | 仓库协议文档与代码里只有 `NOTI SERVER_BROADCAST(86)` 等枚举名，没有正文布局、客户端消费者或抓包取证；公会公告板也标注“字节合同尚未取证”。无证据不发包，避免客户端解析错位。 |
| 装备属性预览（强化加成） | 不做 | `Docs/资源/角色属性.md` 明确装备、强化、附魔、增幅、精通、被动的最终属性规则尚未闭合；`stats` 页保持“仅 CHR 基础属性”。 |


### 第四轮 review 修复与单元测试

- 审计 CSV 防公式注入：前导空白后以 = + - @ 开头的单元格同样加 ' 前缀。
- 审计筛选的 to 仅填日期（YYYY-MM-DD）时包含当天整天。
- GmWebServerTests 新增覆盖：skill-list、exp-table、/api/exp（边界、越界、缺确认、缺请求头）、称号（不在游戏中无需小退）、/api/disconnect（成功、未知账号、缺确认、非法账号、缺头、跨站）、审计按账号/动作/结果/时间筛选、非法筛选参数、CSV 导出与转义。
- review 结论：写接口均经 loopback、Host、同源、X-GM-Request、confirm 与参数正则校验；踢人与断线均写审计；审计读取只取文件尾 16MB（最多 5000 行），性能可接受。

## GM 网页 UX 改造与索引/分页（phase 5）

### 索引“构建索引”提示的根因与修复
- 根因：物品索引是 `Lazy<Task>`，但开发目录（`DevelopmentCatalog`）每个请求路径各自惰性触发、重启后全部重建（约 16 秒），前端每次打开物品页都会轮询到“构建中”；没有启动预热，也没有落盘。
- 修复：服务启动（`GmWebPort`/`GmPort` 非 0）即后台预热物品、任务名、词条三份索引；每进程只构建一次；索引落盘到存档目录 `gm-index-items.json` / `gm-index-attrs.json`（信封含 PVF 文件大小 + 修改时间 Ticks，不匹配即重建；先写临时文件再原子移动）。缓存命中后重启约 7.6 秒即就绪（含服务启动）。
- 前端仅在 `/api/index-status` 返回未就绪时才显示横幅，就绪后不再轮询或提示。

### 分页
- 物品库（search/filter）、审计、账号、任务均为服务端分页：`page`/`pageSize` + `total`；审计保持返回数组并通过 `X-Total-Count` 头给出总数（`format=csv` 不变）。物品 pageSize 1–200（默认 50），审计 1–500。
- 前端分页器：上一页/下一页、页码按钮、跳页输入、每页条数；翻页与改条数保留搜索与筛选。

### 稀有度与词条筛选
- 稀有度名称取自 86 工具 `PvfIndexService.ItemDetail.cs`：0 普通 / 1 高级 / 2 稀有 / 3 神器 / 4 史诗 / 5 勇者 / 6 传说；界面显示名称，悬停显示 `PVF rarity=N`。真实 PVF 已知值：rarity=4 共 7174 件；rarity=5 且词条 separate_attack 共 3828 件；物品 18400 rarity=0。
- 词条筛选下拉显示每个词条的命中数，命中数为 0 的词条置灰不可选（真实 PVF 中有 8 个零命中词条），避免“选了没结果”的误判。词条键使用 `/api/items/stat-keys` 返回的 `key`。

### 任务页：状态与分类
- 状态：未完成 / 进行中 / 已完成，按状态筛选并显示数量（`statusCounts`）。
- 分类依据：PVF 任务文件的 `[grade]` 字段（`--scan-quests` 实测分布：epic 915、achievement 590、title 290、side 219、common unique 236、mission 107、sub 75、daily 66、normaly repeat 37、hidden 20、daily mission 15 …）。映射为 主线 / 支线 / 普通 / 每日 / 重复 / 成就 / 称号，**无法归类的归“其他”**。客户端任务界面自身的分类名称在客户端资源中未找到，所以这是按 grade 的近似分类，不是客户端原文分类。
- “全部完成”对当前筛选范围（上限 3000 个，成就/称号不含）执行，强制二次确认并写审计；勾选框多选（当前页全选、当前筛选全选、跨页保持，显示已选数量），批量完成/放弃按勾选执行。
- `DevelopmentRequest` 命令行长度上限由 512 提到 32768（`quest-batch` 需要放下上千个 id；Web 层仍限制 1–3000，且逐项校验）。

### 操作流程与确认策略
- 顶部全局角色选择器（账号/角色搜索，最近使用记录，Alt+C），选一次后发放、任务、技能、邮件、属性、工作台默认作用于当前角色。
- 角色工作台为整页（标签：基础/背包/技能/任务/称号/邮件/属性/快捷操作）；右侧抽屉仅用于轻量详情，默认约 56% 视口宽，可拖拽调宽、最大化，宽度记在 localStorage，窄屏全宽。
- 二次确认仅用于：删除物品/邮件/角色、清空、踢人/断线、任务清空、“全部完成”（以及称号 all/book*）；在线角色且需要小退的背包写入也会确认。其余操作直接执行，toast 给出结果与撤销提示，并写审计。
- 键盘：Esc 关闭抽屉/弹窗，方向键切换标签与表格行，Enter 确认，Ctrl+K 命令面板。骨架屏与空状态；操作后就地刷新，保留滚动位置与勾选。

### 与 86 工具对照
86 工具（`S4A21GmTool`）的物品页是“搜索 → 点选 → 在同页填数量 → 发送”，不需要先跳到账号页；本次采用同样的“搜索 + 勾选 + 底部操作栏 + 内联数量/强化”，并在其基础上增加跨页勾选与当前角色上下文（86 需要每次填账号）。

### 常用操作点击步数（按流程逐步统计，估算值）
| 操作 | 改造前 | 改造后（当前角色已选） |
|---|---|---|
| 发物品 | 7（进物品页、搜索、选物品、选账号、选角色、填数量、发送+确认） | 3（搜索、勾选、发送） |
| 设等级 | 7（账号页、选账号、选角色、开抽屉、运维页、填、执行+确认） | 2（工作台基础页、填写并应用） |
| 完成任务 | 8（…、任务页、输入 id、批量、确认） | 3（任务页勾选、完成选中；单个任务直接点“完成”） |
| 改称号 | 8 | 3（称号页、搜索、点“装备/获得”） |
| 发邮件 | 7 | 3（邮件页/工作台邮件页、填写、发送） |
| 加金币 | 7（含确认） | 2（填数量、应用；无确认） |
| 改技能点 | 7 | 2（技能点输入、应用） |
| 查看背包 | 5（账号页、选账号、选角色、开抽屉、背包页） | 1（工作台背包标签；首次选角色另计 2–3 步） |
首次选择角色需要 2–3 步（顶部选择器搜索并点选），之后所有页面共用。

