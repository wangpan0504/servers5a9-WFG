using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal readonly record struct SqliteMigration(int Version, string Name, Action<SqliteConnection, SqliteTransaction> Upgrade);

internal static class SqliteMigrations
{
    public const string BaselineId = "s5a9-database-v1";
    public const int BaselineVersion = 1;
    // 首次发布直接建立完整 v1。后续结构变化从 v2 起连续追加，并同步更新完整 SQL。
    private static readonly SqliteMigration[] Steps =
    [
        new(2, "account_wallet_rental_points", static (db, tx) =>
            Execute(db, tx, "ALTER TABLE account_wallets ADD COLUMN rental_points INTEGER NOT NULL DEFAULT 0 CHECK(rental_points BETWEEN 0 AND 2147483647)")),
        new(3, "副本成长事件账本", static (db, tx) => Execute(db, tx, DungeonGrowthSchema)),
        new(4, "mailbox", static (db, tx) =>
        {
            foreach (string statement in MailboxSchema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Execute(db, tx, statement);
        }),
        new(5, "成长奖励资源来源", static (db, tx) =>
            Execute(db, tx, "ALTER TABLE character_dungeon_growth_receipts RENAME COLUMN resource_sha256 TO resource_source")),
        new(6, "客户端设置", static (db, tx) =>
        {
            foreach (string statement in ClientSettingsSchema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Execute(db, tx, statement);
        }),
        new(7, "角色主设置快照", static (db, tx) => Execute(db, tx, CharacterGameSettingsSchema)),
        new(8, "角色副本身份及成长附属owner", static (db, tx) => Execute(db, tx, DungeonOwnerSchema)),
        new(9, "副本冻结掉落与拾取账本", static (db, tx) =>
        {
            Execute(db, tx, DungeonLootSchema);
            Execute(db, tx, DungeonPickupSchema);
        }),
        new(10, "副本通关经验与冻结报告账本", static (db, tx) => Execute(db, tx, DungeonSettlementSchema)),
        new(11, "技能额外SP与达人契约来源", static (db, tx) =>
        {
            Execute(db, tx, SkillPointSourceSchema);
            Execute(db, tx, ExpertContractSchema);
        }),
        new(12, "TP来源、技能指令和连招", static (db, tx) =>
        {
            foreach (string sql in SkillMetadataSchema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Execute(db, tx, sql);
        }),
        new(13, "任务接取身份、清图及完整领奖账本", static (db, tx) =>
        {
            Execute(db, tx, "ALTER TABLE character_active_main_quests ADD COLUMN acceptance_id TEXT NOT NULL DEFAULT '00000000000000000000000000000000' CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*')");
            // 为已有接取建立稳定身份，不修改原条件、不补造清图或发奖。
            Execute(db, tx, "UPDATE character_active_main_quests SET acceptance_id=lower(hex(randomblob(16)))");
            Execute(db, tx, "CREATE UNIQUE INDEX character_active_quest_acceptance ON character_active_main_quests(character_id,acceptance_id) WHERE acceptance_id<>'00000000000000000000000000000000'");
            Execute(db, tx, QuestClearSchema);
            Execute(db, tx, QuestCompletionSchema);
        }),
        // PR12保留了v13的完整领奖表；v14已在现场发布，兼容升级只确认原表，不再重复建表。
        new(14, "已发布完整领奖结构兼容", static (db, tx) =>
            Execute(db, tx, "SELECT character_id,acceptance_id,event_id,rewards,revision FROM character_quest_completion_receipts LIMIT 0")),
        new(15, "已发布疲劳owner兼容", static (db, tx) =>
        {
            Execute(db, tx, FatigueSchema);
            // 接管此前对正式owner实际固定投影的0/156，未知旧角色不补记录、不推进revision。
            Execute(db, tx, "INSERT INTO character_fatigue SELECT c.id,c.account,0,156,0,156,0,0 FROM characters c JOIN character_dungeon_state d ON d.character_id=c.id AND d.account=c.account");
        }),
        new(16, "普通副本独立身份与房间账本", UpgradeNormalDungeonReceipts),
        new(17, "任务系统与已发布普通副本兼容", UpgradeQuestSystem),
        new(18, "任务回执与任务券完整结构", CompleteQuestSystemV18),
        new(19, "补齐旧副本角色的疲劳初始状态", BackfillDungeonFatigue),
        new(20, "完整地面掉落集合与旧掉落分支v18及v19兼容", static (db, tx) =>
        {
            // 旧掉落v19尚未执行master的疲劳v19；按同一资格补齐，已有值和历史回执保持原样。
            BackfillDungeonFatigue(db, tx);
            // 已部署掉落分支v18尚未执行master的任务v18；精确接管已知结构且保留原回执。
            CompleteQuestSystemV18(db, tx);
            DungeonDropCollectionSchema.Upgrade(db, tx);
        }),
        new(21, "普通免费翻牌奖励与永久邮件回执", static (db, tx) => Execute(db, tx, DungeonCardRewardSchema.Sql)),
        new(22, "付费翻牌、黑钻及黄金牌每日权益", DungeonPremiumCardSchema.Create),
        new(23, "成长经验封顶与实际到账回执", ExperienceCapSchema.Upgrade),
        new(24, "删除角色时保留账号任务完成事实", AccountQuestSourceSchema.Upgrade),
        new(25, "称号簿实体位置、三计数进度与章节奖励", static (db, tx) => Execute(db, tx, TitleSchema)),
        new(26, "新角色训练状态及原子检查点", static (db, tx) => Execute(db, tx, TutorialSchema)),
        new(27, "账号分配置键位", static (db, tx) => Execute(db, tx, HotkeySettingsSchema)),
        new(28, "角色基础心情", static (db, tx) =>
            Execute(db, tx, "ALTER TABLE characters ADD COLUMN emotion INTEGER NOT NULL DEFAULT 0 CHECK(emotion BETWEEN 0 AND 32767)")),
        new(29, "账号冒险团基础进度与名称", static (db, tx) => Execute(db, tx, AdventureSchema)),
        new(30, "冒险团代表职业选择", static (db, tx) => Execute(db, tx, AdventureRepresentativeSchema)),
        new(31, "账号远征及冻结成员", static (db, tx) => Execute(db, tx, AdventureExpeditionSchema)),
        new(32, "冒险团商店账号限购", static (db, tx) => Execute(db, tx, AdventurePurchasesSchema)),
        new(33, "账号赛丽亚幸运值", AddSeriaLuckColumn),
        new(34, "任务跳过实际效果账本", AddQuestEffects),
        new(35, "账号契约账本及已知旧分支v34兼容", UpgradePremiumContracts),
        new(36, "翻牌领取、活动与统一会员契约来源", DungeonCardClaimSchema.Upgrade),
        // 拍卖行挂牌表与保证金列合成同一步（本步在任何已发布版本里都不存在，编号在合并上游后重新占用 37）：
        // 一步建出含 `deposit_gold` 的完整表，不再有「先建表、后加列」的两步，也就没有中途半成品的窗口。
        new(37, "拍卖行挂牌与保证金", static (db, tx) =>
        {
            foreach (string statement in AuctionListingSchema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Execute(db, tx, statement);
        }),
        new(38, "冒险团首次命名资格与名称查询索引", static (db, tx) =>
        {
            Execute(db, tx, "ALTER TABLE account_adventures ADD COLUMN name_initialized INTEGER NOT NULL DEFAULT 1 CHECK(name_initialized IN (0,1))");
            Execute(db, tx, "CREATE INDEX account_adventure_name ON account_adventures(name COLLATE NOCASE)");
        }),
        new(39, "角色药剂与光环传送冷却", static (db, tx) => Execute(db, tx, TeleportCooldownSchema)),
        new(40, "物件终末场景清图来源", UpgradeQuestSceneClear),
        new(41, "角色晶体契约选择", static (db, tx) =>
        {
            Execute(db, tx, CubePremiumSchema);
        }),
        new(42, "租赁任务原生完成来源", UpgradeQuestRentalCompletion),
        new(43, "物件零击杀结算与完整契约经验", DungeonSceneSettlementSchema.Upgrade),
        // 公会只追加空表，不改写主线已发布的迁移或角色资产。
        new(44, "公会与成员基础表", static (db, tx) =>
        {
            foreach (string statement in GuildSchema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Execute(db, tx, statement);
        }),
        new(45, "特殊物品拾取与普通库存UID范围", static (db, tx) =>
        {
            UpgradeDungeonPickupUids(db, tx);
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='character_dungeon_passive_pickups'";
            if (command.ExecuteScalar() is not string)
                Execute(db, tx, CharacterRepository.PassiveDropSchema);
        }),
        new(46, "副本列表通关的原生任务完成来源", UpgradeQuestDungeonAlternatives),
        new(47, "账号角色栏扩展", UpgradeCharacterSlots),
        new(48, "角色装扮栏扩展", UpgradeAvatarCapacity),
        new(49, "魔王八项次数与虚弱期限", (db, tx) =>
        {
            Execute(db, tx, "ALTER TABLE characters ADD COLUMN devil_usage TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(devil_usage) AND json_type(devil_usage)='array')");
            Execute(db, tx, "ALTER TABLE characters ADD COLUMN stamina_until INTEGER NOT NULL DEFAULT 0 CHECK(stamina_until>=0)");
        }),
        new(50, "强袭模式每日通关次数", static (db, tx) => Execute(db, tx, AssaultDailySchema)),
        new(51, "角色宝珠收集箱选择与模板槽", UpgradeCollectBoxState),
        new(52, "角色宝珠收集箱期限", UpgradeCollectBoxExpiration),
        new(53, "真实主线阶段资格与双唯一消费", static (db, tx) =>
        {
            Execute(db, tx, QuestStageSchema.WitnessSql);
            Execute(db, tx, QuestStageSchema.ClaimSql);
        }),
        new(54, "角色Buff强化登记", UpgradeSkillSwitchInventory),
        new(55, "角色秘药经验状态", static (db, tx) => Execute(db, tx, ExpBonusSchema)),
        new(56, "每日保留礼盒次数", static (db, tx) => Execute(db, tx,
            "ALTER TABLE characters ADD COLUMN daily_booster_usage TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(daily_booster_usage) AND json_type(daily_booster_usage)='array')")),
        new(57, "角色副职业资格", static (db, tx) => Execute(db, tx, ExpertJobSchema)),
        new(58, "副职业教程领奖原生类型17", UpgradeExpertJobTutorialCompletion),
        new(59, "副职业熟练度", AddExpertJobExperience),
        new(60, "副职业放弃阶与分解机", static (db, tx) => Execute(db, tx, ExpertJobMetaSchema)),
        new(61, "异次元裂缝每日通关奖励", static (db, tx) => Execute(db, tx, CrackDailySchema)),
        new(62, "眩惑贮藏箱领奖类型60", UpgradeDazzlementQuestCompletion),
        new(63, "技能窗职业变更次数", static (db, tx) => Execute(db, tx, GrowUpChangeSchema)),
        new(64, "消耗品持续效果与冷却", static (db, tx) => Execute(db, tx, ConsumableStatesSchema)),
        new(65, "角色被拉黑有效计分", AddBlacklistCountColumn),
        // 上游 #543：通关经验其他加成与冻结分项；保持上游已发布编号。
        new(66, "通关经验其他加成与冻结分项", DungeonExperienceBonusSchema.Upgrade),
        // 上游 #561 的皮肤仓库与选择。
        new(67, "角色皮肤仓库与选择", AddSkinStorageStateColumn),
        // 上游 #566 的属性石永久来源。
        new(68, "道具永久属性来源", static (db, tx) =>
        {
            // 旧皮肤测试分支曾占用v66；复用精确结构校验补齐主线经验列，不改版本冒充降级。
            DungeonExperienceBonusSchema.Upgrade(db, tx);
            Execute(db, tx, PermanentStatSchema.Sql.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal));
        }),
        // 成长胶囊累积经验按公告每周四06:00初始化；保存上次结算的周期边界，0 表示升级前未记录。
        new(69, "冒险团成长胶囊周重置边界", AddAdventureCapsuleWeek),
        // 通关回执补齐秘药与冒险团加成；旧行两项按0补齐，CHECK同步为五项之和。
        new(70, "通关回执秘药与冒险团经验分项", SettlementBreakdownSchema.Upgrade),
        // 冒险团基本信息：连续登录按06:00周期持久化，推荐地下城通关按角色累计后账号求和。
        new(71, "冒险团连续登录与推荐地下城计数", AddAdventureActivity),
        // 每周地下城信息页：客户端提交5槽位排列，按账号保存；不改写账号revision。
        new(72, "冒险团每周地下城信息配置", EnsureAdventureWeeklyConfig),
        // 国服95模型：账号累计所有角色经验；开启总量、排除累计与角色里程一次性建出，开启在启动时执行。
        new(73, "冒险团经验账本", EnsureAdventureLedger),
        // 满级入口线后角色经验到账为0，回执需保留同一笔经验记入熟练度的额度，结算显示与重复报告才能回放有效经验。
        new(74, "熟练度到账额度", SettlementProficiencySchema.Upgrade),
        new(75, "账号商城积分", AddMileageColumn),
        // 冒险团每周地下城信息：按角色保存本周各列实际入场次数，与副本身份预留同一事务累加。
        new(76, "冒险团每周地下城入场计数", EnsureWeeklyDungeonUsage),
        new(77, "角色制作熟练度", AddItemMakingSkills),
        new(78, "零经验副本死亡与通关回执", ZeroExperienceDungeonSchema.Upgrade),
        new(79, "普通副本任务每日通关计数", static (db, tx) => Execute(db, tx, QuestDailyClearSchema)),
        new(80, "副职业已学设计图", AddExpertJobRecipes),
        new(81, "光环传送独立冷却", AddAuraCooldowns),
        // 客户端368按银行分别提交300B快捷聊天原文；账号级保存，173按两段长度回放。
        new(82, "账号快捷聊天银行", static (db, tx) => Execute(db, tx, QuickChatSchema)),
        new(83, "悲叹账号日次数与角色挑战账本", GriefTowerSchema.Upgrade),
        new(84, "悲叹冻结奖励与通关回执", GriefTowerSchema.UpgradeClear),
        new(85, "塔与祭坛原子入场与结算账本", DungeonSpecialEntrySchema.Upgrade),
        // 装扮预设：默认容量1、上限6（客户端槽数组6项），扩展券CMD515 action216累加；
        // 预设按槽位存JSON，槽位是身份且允许空洞，不另建平行在线状态。
        new(86, "装扮预设容量与预设", AddAvatarPresetStateColumn),
        new(87, "副本地面对象及拾取账本按实例隔离", DungeonObjectKeySchema.Upgrade),
        // 副本次数两表曾有六种非主线编号：合并前本地构建的 v62/v63，主线 v67 之前合并分支的 v67/v68，主线 v68（永久属性）之前本分支的 v68/v69，
        // 主线 v69–v73（冒险团）之前本分支的 v69/v70，主线 v74–v82 之前本分支的 v74/v75，以及主线 v83–v87（悲叹、塔与祭坛、装扮预设、对象key）之前本分支的 v83/v84；见 Apply。
        new(88, "副本日周进入次数", static (db, tx) => Execute(db, tx, DungeonIncountSchema)),
        new(89, "副本账号进入次数", static (db, tx) => Execute(db, tx, AccountDungeonIncountSchema)),
        new(90, "玩家社交关系", SocialSchema.Upgrade),
        new(91, "冒险团好友申请者分组与备注", SocialSchema.UpgradeRequests),
        // 护石与符文使用独立容器46（护石0–69、符文70–209、已装210–215），只放开既有槽位表的分类约束。
        new(92, "护石与符文库存容器", static (db, tx) => ReplaceTableConstraint(db, tx, "character_inventory_slots",
            "CHECK(list_type IN (0,1,2,3,7,12,38))", "CHECK(list_type IN (0,1,2,3,7,12,38,46))")),
        new(93, "角色史诗制作进度与周次数", static (db,tx) => Execute(db,tx,EpicProductionSchema.Replace("CREATE TABLE ","CREATE TABLE IF NOT EXISTS ",StringComparison.Ordinal))),
        new(94, "首次任务资格邮件投递账本", static (db,tx) => Execute(db,tx,QuestEligibilityMailSchema.Replace("CREATE TABLE ","CREATE TABLE IF NOT EXISTS ",StringComparison.Ordinal))),
        new(95, "独立训练技能快照与事务身份", static (db,tx) => Execute(db,tx,PvpTrainingSkillsSchema.Replace("CREATE TABLE ","CREATE TABLE IF NOT EXISTS ",StringComparison.Ordinal))),
        new(96, "决斗专用角色身份与账号重建冷却", static (db,tx) => Execute(db,tx,PvpPrivateCharacterSchema.Replace("CREATE TABLE ","CREATE TABLE IF NOT EXISTS ",StringComparison.Ordinal))),
        new(97, "代号希望按实例与接取去重领奖", static (db,tx) => Execute(db,tx,OperationHopeRewardSchema.Definition.Replace("CREATE TABLE ","CREATE TABLE IF NOT EXISTS ",StringComparison.Ordinal))),
        new(98, "组队任务物件助战场景回执", UpgradePartyQuestLifecycle),
        new(99, "角色称号替换动画", AddCloneTitleColumn),
        new(100, "角色技能方案开通及当前方案", static (db, tx) => Execute(db, tx,
            "ALTER TABLE characters ADD COLUMN selected_skill_tree INTEGER NOT NULL DEFAULT -1 CHECK(typeof(selected_skill_tree)='integer' AND selected_skill_tree BETWEEN -1 AND 1)")),
        new(101, "再战魔界账号编队配置", TagEvildomConfigurationSchema.Upgrade),
        new(102, "角色NPC好感度", AddNpcFavorColumn),
        new(103, "再战魔界固定准入与原子领奖回执", TagEvildomRunSchema.Upgrade),
        new(104, "白斯追击药水次数效果", static (db, tx) => Execute(db, tx, BossSelectionBuffSchema)),
        new(105, "普通卢克单人每日顺序进度", OrdinaryLukeProgressSchema.Upgrade),
        new(106, "普通卢克单人首击掉落日资格", OrdinaryLukeDropSchema.Upgrade),
        new(107, "眩惑之塔账号周绑定与角色日入场", DazzlementTowerSchema.Upgrade),
        new(108, "攻坚阶段奖励原子回执", static (db, tx) => Execute(db, tx, RaidRewardSchema.Definition)),
        new(109, "团本类型隔离与卢克两阶段生命周期", RaidLifecycleSchema.Upgrade),
        new(110, "角色疲劳每日恢复日戳", FatigueDailyResetSchema.Upgrade),
        new(111, "全局物品限次和周期补给", ServerItemUsageSchema.Upgrade),
        new(112, "婚姻关系与共享资产账本", MarriageSchema.Upgrade),
        // 帝国竞技场分支的临时号 126 已被本地栈占用到 112，接在队尾顺延为 113。
        new(113, "帝国竞技场编队、日券与领奖账本", ImperialArenaSchema.Upgrade),
        // 再战魔界挑战分支的临时号 126 已被本地栈占用到 113，接在队尾顺延为 114。
        new(114, "再战魔界固定挑战路线与跨日尝试", TagEvildomChallengeSchema.Upgrade),
        // 魔界大战分支的临时号 126..130 已被本地栈占用到 114，接在队尾顺延为 115..119。
        new(115, "魔界大战实际奖励与永久邮件回执", static (db, tx) => Execute(db, tx, EvildomWarRewardSchema.Definition)),
        new(116, "魔界大战战役检查点与付费席位", static (db, tx) => Execute(db, tx, EvildomWarRecoverySchema.Battles + EvildomWarRecoverySchema.Seats)),
        new(117, "魔界大战引导模式账号周绑定", static (db, tx) => Execute(db, tx, EvildomWarGuideSchema.Table)),
        new(118, "魔界大战实验室独立奖励回执", static (db, tx) => Execute(db, tx, EvildomWarHiddenRewardSchema.Definition)),
        new(119, "副本每日每周追加入场额度", static (db, tx) => Execute(db, tx, DungeonIncountExtraSchema.Definition)),
        // 以下为本阶段新增：公会九个分支、决斗场与总力、魔兽每日次数。
        // 前面各分支的临时号与阶段1-8 的 107..119 冲突，统一接在队尾连续排到 143。
        new(120, "公会成员管理与加入申请", static (db, tx) =>
        {
            Execute(db, tx, GuildManagementSchema.Sql);
            Execute(db, tx, GuildApplicationsSchema.Sql);
        }),
        new(121, "公会仓库资产与持久回执", static (db, tx) => Execute(db, tx, GuildCargoSchema.Sql)),
        new(122, "公会容量福利与公共积分", static (db, tx) => Execute(db, tx, GuildEconomySchema.Sql)),
        new(123, "公会外观与自然出勤", static (db, tx) =>
        {
            Execute(db, tx, GuildAppearanceSchema.Sql);
            Execute(db, tx, GuildAttendanceSchema.Sql);
        }),
        new(124, "公会组队传送持久回执", static (db, tx) => Execute(db, tx, GuildTeleportSchema.Sql)),
        new(125, "公会资产请求统一去重", static (db, tx) => Execute(db, tx, GuildRequestSchema.Sql)),
        new(126, "公会经验提交分项回执", static (db, tx) => Execute(db, tx, GuildExperienceSchema.Sql)),
        new(127, "公会自然贡献与周结算", static (db, tx) => Execute(db, tx, GuildContributionSchema.Sql)),
        new(128, "公会NPC升级与契约", static (db, tx) => Execute(db, tx, GuildNpcSchema.Sql)),
        new(129, "公会经验道具持久回执", static (db, tx) => Execute(db, tx, GuildGrowthItemSchema.Sql)),
        new(130, "守护石结晶定时制造", static (db, tx) => Execute(db, tx, GuildNpcCraftSchema.Sql)),
        new(131, "公会推荐频道", static (db, tx) => Execute(db, tx, GuildRecommendedChannelSchema.Sql)),
        new(132, "整局决斗结果事实回执", PvpArenaResultSchema.Upgrade),
        new(133, "角色决斗评分与逐局结算来源", PvpRatingSchema.Upgrade),
        new(134, "账号总力队伍及稳定角色阵容", PvpTotalTeamSchema.Upgrade),
        new(135, "时间广场魔兽每日入场次数", static (db, tx) => Execute(db, tx, BeastDungeonEntrySchema)),
        new(136, "公会副本净化次数与实例通关回执", static (db, tx) => Execute(db, tx, GuildDungeonProgressSchema.Definition)),
        new(137, "角色公会红包每日购买额度", static (db, tx) => Execute(db, tx, GuildHongbaoPurchaseSchema.Definition)),
        new(138, "总力队伍独立评分与逐局来源", PvpTotalRatingSchema.Upgrade),
        new(139, "段位任务类型5原子交付", UpgradePvpRankQuest),
        new(140, "公会红包发行领取与退款账本", static (db, tx) => Execute(db, tx, GuildHongbaoSchema.Definition)),
        new(141, "公会组队排行事实与月榜快照", static (db, tx) => Execute(db, tx, GuildRankingSchema.Definition)),
        new(142, "公会副本通关奖励与永久邮件回执", static (db, tx) => Execute(db, tx, GuildDungeonRewardSchema.Definition)),
        new(143, "公会副本独立有效净化与闲置周期", GuildDungeonEffectiveSchema.Upgrade),
    ];

    internal const string BeastDungeonEntrySchema = """
        CREATE TABLE IF NOT EXISTS character_beast_dungeon_entries(
            character_id INTEGER NOT NULL,
            account TEXT NOT NULL,
            day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day>0),
            dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 310 AND 314),
            used INTEGER NOT NULL CHECK(used BETWEEN 0 AND 2),
            PRIMARY KEY(character_id,day,dungeon_id),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal const string BossSelectionBuffSchema = """
        CREATE TABLE IF NOT EXISTS character_boss_selection_buff(
            character_id INTEGER PRIMARY KEY,
            account TEXT NOT NULL,
            state_json TEXT NOT NULL CHECK(json_valid(state_json) AND json_type(state_json)='object'),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal const string PvpPrivateCharacterSchema = """
        CREATE TABLE account_pvp_private_characters(
            account TEXT NOT NULL,
            job INTEGER NOT NULL CHECK(job BETWEEN 0 AND 15),
            grow_type INTEGER NOT NULL CHECK(grow_type BETWEEN 0 AND 5),
            character_id INTEGER UNIQUE REFERENCES characters(id) ON DELETE SET NULL,
            blocked_until INTEGER NOT NULL CHECK(blocked_until>=0),
            recreate_days INTEGER NOT NULL CHECK(recreate_days BETWEEN 0 AND 36500),
            PRIMARY KEY(account,job,grow_type)
        );
        """;

    internal const string PvpTrainingSkillsSchema = """
        CREATE TABLE character_pvp_training_skills(
            character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
            state_json TEXT NOT NULL CHECK(json_valid(state_json) AND json_type(state_json)='object'),
            operation_id TEXT NOT NULL CHECK(length(operation_id)=32),
            before_revision INTEGER NOT NULL CHECK(before_revision>=0)
        );
        """;

    internal const string QuestEligibilityMailSchema = """
        CREATE TABLE quest_eligibility_mail_dispatches(
            account TEXT NOT NULL,
            scope_character_id INTEGER NOT NULL CHECK(scope_character_id>=0),
            quest_id INTEGER NOT NULL CHECK(quest_id>0),
            recipient_id INTEGER NOT NULL CHECK(recipient_id>0),
            mail_id INTEGER NOT NULL CHECK(mail_id>0),
            resource_path TEXT NOT NULL,
            dispatched_at INTEGER NOT NULL CHECK(dispatched_at>=0),
            PRIMARY KEY(account,scope_character_id,quest_id)
        );
        """;

    internal const string EpicProductionSchema = """
        CREATE TABLE character_epic_production(
            character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
            weapon_id INTEGER NOT NULL CHECK(weapon_id>=0),
            charge INTEGER NOT NULL CHECK(charge>=0 AND (weapon_id<>0 OR charge=0)),
            service_week INTEGER NOT NULL CHECK(service_week>=-1),
            weekly_uses INTEGER NOT NULL CHECK(weekly_uses BETWEEN 0 AND 255),
            since_big_chance INTEGER NOT NULL CHECK(since_big_chance>=0),
            special_uses INTEGER NOT NULL CHECK(special_uses>=0),
            additional_weekly_uses INTEGER NOT NULL CHECK(additional_weekly_uses>=0),
            intermediate_material_remainder INTEGER NOT NULL CHECK(intermediate_material_remainder>=0),
            ordinary_material_remainder INTEGER NOT NULL CHECK(ordinary_material_remainder>=0)
        );
        """;

    internal static string PartyQuestLifecycleSql => QuestLifecycleSchema
        .Replace("CHECK(quest_id BETWEEN 1 AND 29999)", "CHECK(quest_id BETWEEN 0 AND 29999 AND (quest_id<>0 OR (action='Condition' AND scene_event IS NOT NULL AND before_active=after_active AND changed_items='[]')))", StringComparison.Ordinal)
        .Replace("CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000')", "CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND ((quest_id=0 AND acceptance_id='00000000000000000000000000000000') OR (quest_id<>0 AND acceptance_id<>'00000000000000000000000000000000')))", StringComparison.Ordinal);

    static void UpgradePartyQuestLifecycle(SqliteConnection db, SqliteTransaction tx)
    {
        const string table = "character_quest_lifecycle_receipts";
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        query.Parameters.AddWithValue("$table", table);
        if (query.ExecuteScalar() is not string actual) throw new InvalidDataException("缺少任务场景回执表");
        if (NormalizeTable(actual, table) == NormalizeTable(PartyQuestLifecycleSql, table)) return;
        if (NormalizeTable(actual, table) != NormalizeTable(QuestLifecycleSchema, table))
            throw new InvalidDataException("组队任务回执迁移拒绝未知约束");
        ReplaceTableConstraint(db, tx, table, "CHECK(quest_id BETWEEN 1 AND 29999)", "CHECK(quest_id BETWEEN 0 AND 29999 AND (quest_id<>0 OR (action='Condition' AND scene_event IS NOT NULL AND before_active=after_active AND changed_items='[]')))");
        ReplaceTableConstraint(db, tx, table, "CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000')", "CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND ((quest_id=0 AND acceptance_id='00000000000000000000000000000000') OR (quest_id<>0 AND acceptance_id<>'00000000000000000000000000000000')))");
    }

    static void AddAuraCooldowns(SqliteConnection db, SqliteTransaction tx)
    {
        AddAuraCooldown(db, tx, "upgrade_aura_until");
        AddAuraCooldown(db, tx, "party_aura_until");
    }

    static void AddAuraCooldown(SqliteConnection db, SqliteTransaction tx, string column)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT count(*) FROM pragma_table_info('character_teleport_cooldowns') WHERE name='{column}'";
        if (Convert.ToInt32(command.ExecuteScalar()) == 0)
        {
            Execute(db, tx, $"ALTER TABLE character_teleport_cooldowns ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0 CHECK({column}>=0)");
            return;
        }
        command.CommandText = $"SELECT count(*) FROM pragma_table_info('character_teleport_cooldowns') WHERE name='{column}' AND upper(type)='INTEGER' AND \"notnull\"=1 AND dflt_value='0'";
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidDataException("光环传送冷却列结构不符：" + column);
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name='character_teleport_cooldowns' AND type='table'";
        string sql = string.Concat(((string)command.ExecuteScalar()!).Where(value => !char.IsWhiteSpace(value)));
        if (!sql.Contains($"CHECK({column}>=0)", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("光环传送冷却列缺少范围约束：" + column);
    }

    internal const string QuestDailyClearSchema = """
        CREATE TABLE IF NOT EXISTS character_quest_daily_clear(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          quest_id INTEGER NOT NULL CHECK(quest_id IN(13200,8683)),
          day INTEGER NOT NULL CHECK(day BETWEEN 19000101 AND 99991231),
          event_id TEXT NOT NULL CHECK(length(event_id)=32 AND event_id NOT GLOB '*[^0-9a-f]*'),
          revision INTEGER NOT NULL CHECK(revision>0),
          PRIMARY KEY(character_id,quest_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    const string ExpertRecipesColumn = "learned_recipes TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(learned_recipes) AND json_type(learned_recipes)='array' AND json_array_length(learned_recipes)<=255)";

    static void AddExpertJobRecipes(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('character_expert_job') WHERE name='learned_recipes'";
        if (query.ExecuteScalar() is 0L)
            Execute(db, tx, "ALTER TABLE character_expert_job ADD COLUMN " + ExpertRecipesColumn);
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE name='character_expert_job'";
        if (query.ExecuteScalar() is not string sql || !sql.Contains(ExpertRecipesColumn, StringComparison.Ordinal))
            throw new InvalidDataException("副职业已学设计图列结构不兼容");
    }


    static void AddItemMakingSkills(SqliteConnection db, SqliteTransaction tx)
    {
        // 未发布的制作测试库曾占用v76。仅在制作表精确匹配已知定义时，补齐同号正式迁移而保留已有点数。
        if (ItemMakingSkillSchema.Upgrade(db, tx)) EnsureWeeklyDungeonUsage(db, tx);
    }

    static void AddMileageColumn(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='mileage'";
        if (Convert.ToInt32(query.ExecuteScalar()) == 0)
        {
            Execute(db, tx, "ALTER TABLE account_wallets ADD COLUMN mileage INTEGER NOT NULL DEFAULT 0 CHECK(mileage BETWEEN 0 AND 2147483647)");
            return;
        }
        // 回退迁移夹具只接受同一正式列，不吞掉未知同名字段或重置已有余额。
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='mileage' AND upper(type)='INTEGER' AND \"notnull\"=1 AND dflt_value='0'";
        if (Convert.ToInt32(query.ExecuteScalar()) != 1) throw new InvalidDataException("未知商城积分列定义");
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='account_wallets'";
        if (query.ExecuteScalar() is not string sql || !sql.Contains("CHECK(mileage BETWEEN 0 AND 2147483647)", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("商城积分列缺少正式数量约束");
    }

    // v76首次引入角色每周地下城计数表；从当前schema回退重放的夹具可能已带同结构表，
    // 校验主键与外键后补建索引，不改既有行。同名视图或不同结构整笔失败回滚。
    static void EnsureWeeklyDungeonUsage(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='character_weekly_dungeon'";
        if (query.ExecuteScalar() is string sql)
        {
            if (!sql.Contains("PRIMARY KEY(character_id,week,slot)", StringComparison.Ordinal) ||
                !sql.Contains("FOREIGN KEY(character_id,account) REFERENCES characters(id,account)", StringComparison.Ordinal))
                throw new InvalidDataException("每周地下城计数迁移拒绝未知表结构");
            Execute(db, tx, "CREATE INDEX IF NOT EXISTS character_weekly_dungeon_account ON character_weekly_dungeon(account,week)");
            return;
        }
        Execute(db, tx, WeeklyDungeonUsageSchema);
    }

    // 早期真实库没有该表（v72首次引入），直接建出；从当前schema回退重放的夹具可能已带同结构表，
    // 校验目标结构后跳过，不重复建表。同名视图或不同结构仍整笔失败回滚。
    static void EnsureAdventureWeeklyConfig(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='account_adventure_weekly_config'";
        if (query.ExecuteScalar() is string sql)
        {
            if (!sql.Contains("account TEXT PRIMARY KEY REFERENCES account_adventures(account)", StringComparison.Ordinal) ||
                !sql.Contains("slots BLOB NOT NULL CHECK(typeof(slots)='blob' AND length(slots)=5)", StringComparison.Ordinal))
                throw new InvalidDataException("每周地下城配置迁移拒绝未知表结构");
            return;
        }
        Execute(db, tx, AdventureWeeklyConfigSchema);
    }

    // 与 v47/v48/v59/v69 相同，先查列再补；旧库两列按0补齐，首次登录重新计1。
    static void AddAdventureActivity(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='login_streak_days'";
        if (query.ExecuteScalar() is 0L)
            Execute(db, tx, "ALTER TABLE account_adventures ADD COLUMN login_streak_days INTEGER NOT NULL DEFAULT 0 CHECK(login_streak_days>=0)");
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='last_login_day'";
        if (query.ExecuteScalar() is 0L)
            Execute(db, tx, "ALTER TABLE account_adventures ADD COLUMN last_login_day INTEGER NOT NULL DEFAULT 0 CHECK(last_login_day>=0)");
        query.CommandText = "SELECT count(*) FROM pragma_table_info('character_dungeon_state') WHERE name='recommended_dungeon_clears'";
        if (query.ExecuteScalar() is 0L)
            Execute(db, tx, "ALTER TABLE character_dungeon_state ADD COLUMN recommended_dungeon_clears INTEGER NOT NULL DEFAULT 0 CHECK(recommended_dungeon_clears BETWEEN 0 AND 65535)");
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='account_adventures'";
        if (query.ExecuteScalar() is not string account ||
            !account.Contains("login_streak_days INTEGER NOT NULL DEFAULT 0 CHECK(login_streak_days>=0)", StringComparison.Ordinal) ||
            !account.Contains("last_login_day INTEGER NOT NULL DEFAULT 0 CHECK(last_login_day>=0)", StringComparison.Ordinal))
            throw new InvalidDataException("冒险团登录周期迁移拒绝未知列结构");
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='character_dungeon_state'";
        if (query.ExecuteScalar() is not string state ||
            !state.Contains("recommended_dungeon_clears INTEGER NOT NULL DEFAULT 0 CHECK(recommended_dungeon_clears BETWEEN 0 AND 65535)", StringComparison.Ordinal))
            throw new InvalidDataException("推荐地下城计数迁移拒绝未知列结构");
    }

    const string AdventureOpeningColumn = "opening_experience TEXT NOT NULL DEFAULT '0' CHECK(typeof(opening_experience)='text')";
    const string AdventureExcludedColumn = "excluded_experience TEXT NOT NULL DEFAULT '0' CHECK(typeof(excluded_experience)='text')";

    // 旧库按0补齐开启量与排除额；账本两张表首次建出，从当前 schema 回退重放的夹具已带同结构时校验后跳过。
    // 角色里程表以 character_id 为主键，账号列只用于按账号排查；删除角色时由 Delete 先折算贡献再删行。
    static void EnsureAdventureLedger(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='opening_experience'";
        if (query.ExecuteScalar() is 0L) Execute(db, tx, "ALTER TABLE account_adventures ADD COLUMN " + AdventureOpeningColumn);
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='excluded_experience'";
        if (query.ExecuteScalar() is 0L) Execute(db, tx, "ALTER TABLE account_adventures ADD COLUMN " + AdventureExcludedColumn);
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='account_adventures'";
        if (query.ExecuteScalar() is not string account ||
            !account.Contains(AdventureOpeningColumn, StringComparison.Ordinal) ||
            !account.Contains(AdventureExcludedColumn, StringComparison.Ordinal))
            throw new InvalidDataException("冒险团经验账本迁移拒绝未知列结构");
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_adventure_credits'";
        if (query.ExecuteScalar() is 0L) Execute(db, tx, AdventureCreditsSchema);
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='adventure_ledger_meta'";
        if (query.ExecuteScalar() is 0L) Execute(db, tx, AdventureLedgerMetaSchema);
        if (!KnownTable(db, tx, "character_adventure_credits", AdventureCreditsSchema) ||
            !KnownTable(db, tx, "adventure_ledger_meta", AdventureLedgerMetaSchema))
            throw new InvalidDataException("冒险团经验账本迁移拒绝未知表结构");
    }

    const string AdventureCreditsSchema = """
        CREATE TABLE character_adventure_credits(
          character_id INTEGER PRIMARY KEY,account TEXT NOT NULL,
          baseline_experience INTEGER NOT NULL CHECK(baseline_experience>=0),
          position_experience INTEGER NOT NULL CHECK(position_experience>=0),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE)
        """;

    const string AdventureLedgerMetaSchema = """
        CREATE TABLE adventure_ledger_meta(
          key TEXT NOT NULL PRIMARY KEY,value TEXT NOT NULL)
        """;

    // SQLite 没有 ADD COLUMN IF NOT EXISTS；与 v47/v48/v59 相同，先查列再补，并拒绝未知列结构。
    static void AddAdventureCapsuleWeek(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='capsule_week'";
        if (query.ExecuteScalar() is 0L)
            Execute(db, tx, "ALTER TABLE account_adventures ADD COLUMN capsule_week INTEGER NOT NULL DEFAULT 0 CHECK(capsule_week>=0)");
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='account_adventures'";
        if (query.ExecuteScalar() is not string sql ||
            !sql.Contains("capsule_week INTEGER NOT NULL DEFAULT 0 CHECK(capsule_week>=0)", StringComparison.Ordinal))
            throw new InvalidDataException("冒险团胶囊周期迁移拒绝未知列结构");
    }

    const string BlacklistCountColumn = "blacklist_count INTEGER NOT NULL DEFAULT 0 CHECK(typeof(blacklist_count)='integer' AND blacklist_count BETWEEN 0 AND 2147483647)";
    const string SkinStorageStateColumn = "weapon_skin_state TEXT NOT NULL DEFAULT '{\"Owned\":[],\"Selected\":0}' CHECK(json_valid(weapon_skin_state) AND json_type(weapon_skin_state)='object')";

    static void AddSkinStorageStateColumn(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('characters') WHERE name='weapon_skin_state'";
        if (query.ExecuteScalar() is 0L)
            Execute(db, tx, "ALTER TABLE characters ADD COLUMN " + SkinStorageStateColumn);
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='characters'";
        if (query.ExecuteScalar() is not string sql || !sql.Contains(SkinStorageStateColumn, StringComparison.Ordinal))
            throw new InvalidDataException("角色武器幻化列结构不兼容");
    }

    const string AvatarPresetStateColumn = "avatar_preset_state TEXT NOT NULL DEFAULT '{\"Capacity\":1,\"Presets\":[]}' CHECK(json_valid(avatar_preset_state) AND json_type(avatar_preset_state)='object')";

    const string NpcFavorColumn = "npc_favor TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(npc_favor) AND json_type(npc_favor)='array')";

    static void AddNpcFavorColumn(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('characters') WHERE name='npc_favor'";
        if (query.ExecuteScalar() is 0L) Execute(db, tx, "ALTER TABLE characters ADD COLUMN " + NpcFavorColumn);
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='characters'";
        if (query.ExecuteScalar() is not string sql || !sql.Contains(NpcFavorColumn, StringComparison.Ordinal))
            throw new InvalidDataException("角色NPC好感度列结构不兼容");
    }

    static void AddAvatarPresetStateColumn(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('characters') WHERE name='avatar_preset_state'";
        if (query.ExecuteScalar() is 0L)
            Execute(db, tx, "ALTER TABLE characters ADD COLUMN " + AvatarPresetStateColumn);
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='characters'";
        if (query.ExecuteScalar() is not string sql || !sql.Contains(AvatarPresetStateColumn, StringComparison.Ordinal))
            throw new InvalidDataException("角色装扮预设列结构不兼容");
    }

    const string CloneTitleColumn = "clone_title_item_id INTEGER NOT NULL DEFAULT 0 CHECK(typeof(clone_title_item_id)='integer' AND clone_title_item_id BETWEEN 0 AND 4294967295)";

    static void AddCloneTitleColumn(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('characters') WHERE name='clone_title_item_id'";
        if (query.ExecuteScalar() is 0L)
            Execute(db, tx, "ALTER TABLE characters ADD COLUMN " + CloneTitleColumn);
        // 已有同名列只接受完整正式定义，保留回退迁移夹具的原值，不吞掉未知结构。
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='characters'";
        if (query.ExecuteScalar() is not string sql || !sql.Contains(CloneTitleColumn, StringComparison.Ordinal))
            throw new InvalidDataException("角色称号动画列结构不兼容");
    }

    static void AddBlacklistCountColumn(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('characters') WHERE name='blacklist_count'";
        if (query.ExecuteScalar() is 0L)
            Execute(db, tx, "ALTER TABLE characters ADD COLUMN " + BlacklistCountColumn);
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='characters'";
        if (query.ExecuteScalar() is not string sql || !sql.Contains(BlacklistCountColumn, StringComparison.Ordinal))
            throw new InvalidDataException("角色被拉黑计分列结构不兼容");
    }

    static void AddExpertJobExperience(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('character_expert_job') WHERE name='expertness'";
        if (query.ExecuteScalar() is 0L)
            Execute(db, tx, "ALTER TABLE character_expert_job ADD COLUMN expertness INTEGER NOT NULL DEFAULT 0 CHECK(expertness>=0)");
    }

    static void UpgradeCollectBoxState(SqliteConnection db, SqliteTransaction tx)
    {
        if (KnownTable(db, tx, "character_collectbox_state", CollectBoxStateTableSchema) &&
            KnownTable(db, tx, "character_collectbox_slots", CollectBoxSlotsTableSchema))
            return;
        Execute(db, tx, CollectBoxStateSchema);
    }

    static void UpgradeCollectBoxExpiration(SqliteConnection db, SqliteTransaction tx)
    {
        if (KnownTable(db, tx, "character_collectbox_expirations", CollectBoxExpirationSchema))
            return;
        Execute(db, tx, CollectBoxExpirationSchema);
    }

    static void UpgradeSkillSwitchInventory(SqliteConnection db, SqliteTransaction tx)
    {
        if (KnownTable(db, tx, "character_skill_switch_inventory", SkillSwitchInventoryStateSchema) &&
            KnownTable(db, tx, "character_skill_switch_inventory_items", SkillSwitchInventoryItemsSchema))
            return;
        Execute(db, tx, SkillSwitchInventorySchema);
    }

    static bool KnownTable(SqliteConnection db, SqliteTransaction tx, string table, string expected)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$name";
        query.Parameters.AddWithValue("$name", table);
        return query.ExecuteScalar() is string actual && NormalizeTable(actual, table) == NormalizeTable(expected, table);
    }

    internal static bool IsAcceptanceAssaultV47(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT (SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_assault_daily')*10+(SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='character_slots')";
        return query.ExecuteScalar() is 10L;
    }

    static void UpgradeCharacterSlots(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='character_slots'";
        if ((long)query.ExecuteScalar()! == 0)
            Execute(db, tx, "ALTER TABLE account_wallets ADD COLUMN character_slots INTEGER NOT NULL DEFAULT 32 CHECK(character_slots BETWEEN 32 AND 62)");
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='account_wallets'";
        string sql = (string)query.ExecuteScalar()!;
        if (!sql.Contains("character_slots INTEGER NOT NULL DEFAULT 32 CHECK(character_slots BETWEEN 32 AND 62)", StringComparison.Ordinal))
            throw new InvalidDataException("角色栏迁移拒绝未知容量列");
        ReplaceTableConstraint(db, tx, "characters", "CHECK(slot BETWEEN 0 AND 31)", "CHECK(slot BETWEEN 0 AND 61)");
    }

    static void UpgradeAvatarCapacity(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('characters') WHERE name='avatar_expansion'";
        if ((long)query.ExecuteScalar()! == 0)
            Execute(db, tx, "ALTER TABLE characters ADD COLUMN avatar_expansion INTEGER NOT NULL DEFAULT 105 CHECK(avatar_expansion BETWEEN 0 AND 315 AND avatar_expansion%7=0)");
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='characters'";
        if (query.ExecuteScalar() is not string sql || !sql.Contains("avatar_expansion INTEGER NOT NULL DEFAULT 105 CHECK(avatar_expansion BETWEEN 0 AND 315 AND avatar_expansion%7=0)", StringComparison.Ordinal))
            throw new InvalidDataException("装扮扩展迁移拒绝未知容量列");
        ReplaceTableConstraint(db, tx, "character_inventory_slots", "CHECK(list_type<>1 OR slot_index<210)", "CHECK(list_type<>1 OR slot_index<420)");
        ReplaceTableConstraint(db, tx, "character_inventory_slots", "CHECK(slot_index BETWEEN 0 AND 351)",
            "CHECK(slot_index BETWEEN 0 AND 351 OR (list_type=1 AND slot_index BETWEEN 352 AND 419))");
    }

    internal static void ReplaceTableConstraint(SqliteConnection db, SqliteTransaction tx, string table, string oldLimit, string newLimit)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        query.Parameters.AddWithValue("$table", table);
        string sql = (string?)query.ExecuteScalar() ?? throw new InvalidDataException("缺少角色表");
        if (sql.Contains(newLimit, StringComparison.Ordinal)) return;
        if (sql.Split(oldLimit, StringSplitOptions.None).Length != 2)
            throw new InvalidDataException("角色栏迁移拒绝未知槽位约束");
        query.CommandText = "PRAGMA schema_version";
        long schemaVersion = (long)query.ExecuteScalar()!;
        // 只放宽已知CHECK，不改行布局、外键、索引或自增高水位；与新增权益列同事务。
        // SQLite的无数据布局变化迁移流程：https://www.sqlite.org/lang_altertable.html#otheralter
        Execute(db, tx, "PRAGMA writable_schema=ON");
        try
        {
            query.CommandText = "UPDATE sqlite_schema SET sql=$sql WHERE type='table' AND name=$table";
            query.Parameters.AddWithValue("$sql", sql.Replace(oldLimit, newLimit, StringComparison.Ordinal));
            if (query.ExecuteNonQuery() != 1) throw new InvalidDataException("角色栏约束更新失败");
            Execute(db, tx, $"PRAGMA schema_version={checked(schemaVersion + 1)}");
        }
        finally { Execute(db, tx, "PRAGMA writable_schema=OFF"); }
        Execute(db, tx, $"SELECT * FROM {table} LIMIT 0");
    }

    // 在已发布v18和经验封顶声明上只扩展native19，不改写历史迁移。
    internal static string QuestCompletionSchemaV42 => ExperienceCapSchema.Definition(
        QuestCompletionSchemaV18, "character_quest_completion_receipts").Replace("12,25", "12,19,25", StringComparison.Ordinal);

    internal static string QuestCompletionSchemaV46 => QuestCompletionSchemaV42
        .Replace("CHECK(condition_type IN(0,1,2,3,4,6,12,19,25,31,39,40,42,44,49,64,65,67,70))",
            "CHECK(condition_type IN(0,1,2,3,4,6,12,19,25,31,39,40,42,44,45,49,64,65,67,70))", StringComparison.Ordinal)
        .Replace("condition_type IN(1,3) AND", "condition_type IN(1,3,45) AND (condition_type<>45 OR (clear_acceptance_id IS NOT NULL AND plan_snapshot IS NOT NULL)) AND", StringComparison.Ordinal);

    internal static string QuestCompletionSchemaV58 => QuestCompletionSchemaV46
        .Replace("12,19,25", "12,17,19,25", StringComparison.Ordinal);

    internal static string QuestCompletionSchemaV62 => QuestCompletionSchemaV58
        .Replace("64,65,67,70", "60,64,65,67,70", StringComparison.Ordinal);

    internal static string QuestCompletionSchemaV127 => QuestCompletionSchemaV62.Replace(",4,6,",",4,5,6,",StringComparison.Ordinal);

    static void UpgradePvpRankQuest(SqliteConnection db,SqliteTransaction tx)
    {
        const string table="character_quest_completion_receipts";
        using var query=db.CreateCommand();query.Transaction=tx;
        query.CommandText="SELECT sql FROM sqlite_schema WHERE type='table' AND name='character_quest_completion_receipts'";
        string actual=query.ExecuteScalar() as string ?? throw new InvalidDataException("段位任务迁移缺少领奖账本");
        if(NormalizeTable(actual,table)==NormalizeTable(QuestCompletionSchemaV127,table)) return;
        if(NormalizeTable(actual,table)!=NormalizeTable(QuestCompletionSchemaV62,table))
            throw new InvalidDataException("段位任务迁移拒绝未知领奖结构");
        ReplaceTableConstraint(db,tx,table,"condition_type IN(0,1,2,3,4,6,","condition_type IN(0,1,2,3,4,5,6,");
        ReplaceTableConstraint(db,tx,table,"condition_type IN(0,2,4,6,","condition_type IN(0,2,4,5,6,");
    }

    static void UpgradeDazzlementQuestCompletion(SqliteConnection db, SqliteTransaction tx)
    {
        const string table = "character_quest_completion_receipts";
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.Parameters.AddWithValue("$table", table);
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        string actual = query.ExecuteScalar() as string ?? throw new InvalidDataException("眩惑贮藏箱领奖迁移缺少原领奖账本");
        if (ReceiptShapeApplied(actual, table, QuestCompletionSchemaV62, QuestCompletionSchemaV127)) return;
        if (!ReceiptShapeApplied(actual, table, QuestCompletionSchemaV58))
            throw new InvalidDataException("眩惑贮藏箱领奖迁移不是已确认的 v58 领奖结构");
        string updated = actual.Replace("64,65,67,70", "60,64,65,67,70", StringComparison.Ordinal);
        if (NormalizeTable(updated, table) != NormalizeTable(QuestCompletionSchemaV62, table))
            throw new InvalidDataException("眩惑贮藏箱领奖迁移不能得到已确认的 v62 领奖结构");
        query.CommandText = "PRAGMA schema_version";
        long schemaVersion = (long)query.ExecuteScalar()!;
        Execute(db, tx, "PRAGMA writable_schema=ON");
        try
        {
            query.CommandText = "UPDATE sqlite_schema SET sql=$sql WHERE type='table' AND name=$table";
            query.Parameters.AddWithValue("$sql", updated);
            if (query.ExecuteNonQuery() != 1) throw new InvalidDataException("眩惑贮藏箱领奖约束更新失败");
            Execute(db, tx, $"PRAGMA schema_version={checked(schemaVersion + 1)}");
        }
        finally { Execute(db, tx, "PRAGMA writable_schema=OFF"); }
        Execute(db, tx, $"SELECT * FROM {table} LIMIT 0");
        query.CommandText = "PRAGMA foreign_key_check";
        using var failures = query.ExecuteReader();
        if (failures.Read()) throw new InvalidDataException("眩惑贮藏箱领奖迁移不能保全旧完成外键");
    }

    static void UpgradeExpertJobTutorialCompletion(SqliteConnection db, SqliteTransaction tx)
    {
        const string table = "character_quest_completion_receipts";
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.Parameters.AddWithValue("$table", table);
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        string actual = query.ExecuteScalar() as string ?? throw new InvalidDataException("副职业教程领奖迁移缺少原领奖账本");
        if (ReceiptShapeApplied(actual, table, QuestCompletionSchemaV58, QuestCompletionSchemaV62,
                QuestCompletionSchemaV127)) return;
        if (!ReceiptShapeApplied(actual, table, QuestCompletionSchemaV46))
            throw new InvalidDataException("副职业教程领奖迁移不是已确认的 v46 领奖结构");
        // v53 阶段旁表已外键指向领奖账本，不能 DROP 重建；只把已确认的 IN 列表扩到原生类型 17。
        string updated = actual.Replace("12,19,25", "12,17,19,25", StringComparison.Ordinal);
        if (NormalizeTable(updated, table) != NormalizeTable(QuestCompletionSchemaV58, table))
            throw new InvalidDataException("副职业教程领奖迁移不能得到已确认的 v58 领奖结构");
        query.CommandText = "PRAGMA schema_version";
        long schemaVersion = (long)query.ExecuteScalar()!;
        Execute(db, tx, "PRAGMA writable_schema=ON");
        try
        {
            query.CommandText = "UPDATE sqlite_schema SET sql=$sql WHERE type='table' AND name=$table";
            query.Parameters.AddWithValue("$sql", updated);
            if (query.ExecuteNonQuery() != 1) throw new InvalidDataException("副职业教程领奖约束更新失败");
            Execute(db, tx, $"PRAGMA schema_version={checked(schemaVersion + 1)}");
        }
        finally { Execute(db, tx, "PRAGMA writable_schema=OFF"); }
        Execute(db, tx, $"SELECT * FROM {table} LIMIT 0");
        query.CommandText = "PRAGMA foreign_key_check";
        using var failures = query.ExecuteReader();
        if (failures.Read()) throw new InvalidDataException("副职业教程领奖迁移不能保全旧完成外键");
    }

    static void UpgradeQuestDungeonAlternatives(SqliteConnection db, SqliteTransaction tx)
    {
        const string table = "character_quest_completion_receipts";
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.Parameters.AddWithValue("$table",table);
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        string actual = query.ExecuteScalar() as string ?? throw new InvalidDataException("副本列表任务迁移缺少原领奖账本");
        if (ReceiptShapeApplied(actual,table,QuestCompletionSchemaV46,QuestCompletionSchemaV58,
                QuestCompletionSchemaV62,QuestCompletionSchemaV127)) return;
        if (!ReceiptShapeApplied(actual,table,QuestCompletionSchemaV42))
            throw new InvalidDataException("副本列表任务迁移不是已确认的v45领奖结构");
        query.CommandText = """
            SELECT count(*) FROM sqlite_schema s JOIN pragma_foreign_key_list(s.name) f
              WHERE s.type='table' AND f."table"=$table
            """;
        if (Convert.ToInt32(query.ExecuteScalar()) != 0)
            throw new InvalidDataException("副本列表任务迁移发现未知领奖依赖表");
        RebuildReceipt(db,tx,table,QuestCompletionSchemaV46);
        query.CommandText = "PRAGMA foreign_key_check";
        using var failures = query.ExecuteReader();
        if (failures.Read()) throw new InvalidDataException("副本列表任务迁移破坏原领奖外键");
    }

    internal static string DungeonPickupSchemaV45 => DungeonPickupSchema.Replace(
        "item_uid BETWEEN 1 AND 2147483647", "item_uid>0", StringComparison.Ordinal);

    static void UpgradeDungeonPickupUids(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$table", "");
        foreach (var (table, previous, current) in new[]
        {
            ("character_dungeon_pickup_receipts", DungeonPickupSchema, DungeonPickupSchemaV45),
            ("character_dungeon_drop_pickups", DungeonDropCollectionSchema.PreviousPickupDefinition, DungeonDropCollectionSchema.PickupDefinition)
        })
        {
            query.Parameters["$table"].Value = table;
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            string actual = query.ExecuteScalar() as string ?? throw new InvalidDataException("UID迁移缺少原拾取回执: " + table);
            string canonical = NormalizeTable(actual, table);
            if (canonical == NormalizeTable(current, table) ||
                canonical == NormalizeTable(DungeonObjectKeySchema.Definition(current, table), table)) continue;
            if (canonical != NormalizeTable(previous, table))
                throw new InvalidDataException("UID迁移不是已确认的原拾取结构: " + table);
            query.CommandText = """
                SELECT count(*) FROM sqlite_schema s JOIN pragma_foreign_key_list(s.name) f
                  WHERE s.type='table' AND f."table"=$table
                """;
            if (Convert.ToInt32(query.ExecuteScalar()) != 0)
                throw new InvalidDataException("UID迁移发现未知拾取依赖表: " + table);
            RebuildReceipt(db, tx, table, current);
        }
        query.CommandText = "PRAGMA foreign_key_check";
        using var failures = query.ExecuteReader();
        if (failures.Read()) throw new InvalidDataException("UID迁移破坏原拾取外键");
    }

    static void UpgradeQuestRentalCompletion(SqliteConnection db, SqliteTransaction tx)
    {
        const string table = "character_quest_completion_receipts";
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        query.Parameters.AddWithValue("$table", table);
        string actual = query.ExecuteScalar() as string ?? throw new InvalidDataException("租赁任务迁移缺少原领奖账本");
        if (ReceiptShapeApplied(actual, table, QuestCompletionSchemaV42, QuestCompletionSchemaV46,
                QuestCompletionSchemaV58, QuestCompletionSchemaV62, QuestCompletionSchemaV127)) return;
        string previous = ExperienceCapSchema.Definition(QuestCompletionSchemaV18, table);
        if (!ReceiptShapeApplied(actual, table, previous))
            throw new InvalidDataException("租赁任务迁移不是已确认的v41领奖结构");
        query.CommandText = """
            SELECT count(*) FROM sqlite_schema s JOIN pragma_foreign_key_list(s.name) f
              WHERE s.type='table' AND f."table"=$table
            """;
        if (Convert.ToInt32(query.ExecuteScalar()) != 0)
            throw new InvalidDataException("租赁任务迁移发现未知领奖依赖表");
        RebuildReceipt(db, tx, table, QuestCompletionSchemaV42);
        query.CommandText = "PRAGMA foreign_key_check";
        using var failures = query.ExecuteReader();
        if (failures.Read()) throw new InvalidDataException("租赁任务迁移破坏原领奖外键");
    }
    const string CubePremiumSchema = """
        CREATE TABLE cube_premium(
            charac_no INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
            selected INTEGER NOT NULL CHECK(selected IN (0,1)),
            cube_type INTEGER NOT NULL CHECK(cube_type BETWEEN -1 AND 5)
        );
        """;

    internal const string QuestSceneClearSchema = """
        CREATE TABLE character_dungeon_scene_clear_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,operation_id TEXT NOT NULL,
          source_type TEXT NOT NULL CHECK(source_type='PassiveObject'),
          instance_id TEXT NOT NULL,quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 0 AND 29999),
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),before_epoch INTEGER NOT NULL CHECK(before_epoch>0),
          epoch INTEGER NOT NULL CHECK(epoch=before_epoch+1),dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),map_id INTEGER NOT NULL CHECK(map_id>0),
          template_id INTEGER NOT NULL CHECK(template_id>0),source_ordinal INTEGER NOT NULL CHECK(source_ordinal>=0),
          fingerprint TEXT NOT NULL CHECK(length(fingerprint)=64),
          death_events BLOB NOT NULL CHECK(typeof(death_events)='blob' AND length(death_events)>0 AND length(death_events)%16=0),
          generation INTEGER NOT NULL CHECK(generation>0),revision INTEGER NOT NULL CHECK(revision>0),
          PRIMARY KEY(character_id,operation_id),UNIQUE(character_id,instance_id),
          FOREIGN KEY(character_id,operation_id) REFERENCES character_quest_lifecycle_receipts(character_id,operation_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    internal static readonly string QuestClearSchemaV40 = QuestClearSchemaV14
        .Replace("PRIMARY KEY(character_id,acceptance_id),", """
            source_type TEXT NOT NULL CHECK(source_type IN('MonsterDeath','PassiveObject')),
            growth_event_id TEXT,scene_operation_id TEXT,
            CHECK((source_type='MonsterDeath' AND growth_event_id=event_id AND growth_event_id IS NOT NULL AND scene_operation_id IS NULL)
              OR (source_type='PassiveObject' AND scene_operation_id=event_id AND scene_operation_id IS NOT NULL AND growth_event_id IS NULL)),
            PRIMARY KEY(character_id,acceptance_id),
            """)
        .Replace("FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),", """
            FOREIGN KEY(character_id,growth_event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
            FOREIGN KEY(character_id,scene_operation_id) REFERENCES character_dungeon_scene_clear_receipts(character_id,operation_id),
            """);

    static void UpgradeQuestSceneClear(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='character_quest_clear_receipts'";
        string actual = command.ExecuteScalar() as string ?? throw new InvalidDataException("场景清图迁移缺少原清图表");
        bool current = NormalizeTable(actual,"character_quest_clear_receipts") == NormalizeTable(QuestClearSchemaV40,"character_quest_clear_receipts");
        if (!current && NormalizeTable(actual,"character_quest_clear_receipts") != NormalizeTable(QuestClearSchemaV14,"character_quest_clear_receipts"))
            throw new InvalidDataException("场景清图迁移拒绝未知清图定义");
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='character_dungeon_scene_clear_receipts'";
        if (command.ExecuteScalar() is string scene)
        {
            if (NormalizeTable(scene,"character_dungeon_scene_clear_receipts") != NormalizeTable(QuestSceneClearSchema,"character_dungeon_scene_clear_receipts"))
                throw new InvalidDataException("场景清图迁移拒绝未知场景来源定义");
        }
        else Execute(db,tx,QuestSceneClearSchema);
        if (current) return;
        Execute(db,tx,"PRAGMA defer_foreign_keys=ON");
        RebuildReceipt(db,tx,"character_quest_clear_receipts",QuestClearSchemaV40,
            "source_type,growth_event_id,scene_operation_id","'MonsterDeath',event_id,NULL");
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("场景清图迁移不能保全旧清图或完成外键");
    }

    // CMD785 已成功变更次数；无行即0次。旧库此前没有实现该菜单，升级时不回填推断历史。
    internal const string GrowUpChangeSchema = """
        CREATE TABLE IF NOT EXISTS character_growup_change(
          character_id INTEGER PRIMARY KEY,
          account TEXT NOT NULL,
          change_count INTEGER NOT NULL CHECK(change_count BETWEEN 1 AND 127),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal const string DungeonIncountSchema = """
        CREATE TABLE IF NOT EXISTS character_dungeon_incount(
          character_id INTEGER NOT NULL,
          account TEXT NOT NULL,
          dungeon INTEGER NOT NULL,
          day INTEGER NOT NULL,
          daily_used INTEGER NOT NULL CHECK(daily_used BETWEEN 0 AND 1000000),
          week INTEGER NOT NULL,
          weekly_used INTEGER NOT NULL CHECK(weekly_used BETWEEN 0 AND 1000000),
          PRIMARY KEY(character_id,dungeon),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    // DungeonIncountInfo [account dungeon incount info]：每账号每副本一行，period 为当前周期键（日键或周起始日键），跨周期即视为 0。
    internal const string AccountDungeonIncountSchema = """
        CREATE TABLE IF NOT EXISTS account_dungeon_incount(
          account TEXT NOT NULL,
          dungeon INTEGER NOT NULL,
          period INTEGER NOT NULL,
          used INTEGER NOT NULL CHECK(used BETWEEN 0 AND 1000000),
          PRIMARY KEY(account,dungeon)
        );
        """;

    internal const string CrackDailySchema = """
        CREATE TABLE IF NOT EXISTS character_crack_daily(
          character_id INTEGER PRIMARY KEY,
          account TEXT NOT NULL,
          day INTEGER NOT NULL,
          clear_count INTEGER NOT NULL CHECK(clear_count BETWEEN 0 AND 1000000),
          daily_reward INTEGER NOT NULL CHECK(daily_reward IN (0,1)),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal const string AssaultDailySchema = """
        CREATE TABLE IF NOT EXISTS character_assault_daily(
          character_id INTEGER PRIMARY KEY,
          account TEXT NOT NULL,
          day INTEGER NOT NULL,
          clear_count INTEGER NOT NULL CHECK(clear_count BETWEEN 0 AND 255),
          hidden_count INTEGER NOT NULL CHECK(hidden_count BETWEEN 0 AND 255),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal const string ConsumableStatesSchema = """
        CREATE TABLE IF NOT EXISTS character_consumable_states(
          character_id INTEGER NOT NULL,
          account TEXT NOT NULL,
          kind INTEGER NOT NULL CHECK(kind IN(0,1)),
          item_id INTEGER NOT NULL CHECK(item_id>0),
          until_ms INTEGER NOT NULL CHECK(until_ms>0),
          maintenance INTEGER NOT NULL CHECK(maintenance BETWEEN 0 AND 15 AND (kind=0 OR maintenance<=7)),
          cool_group INTEGER NOT NULL,
          PRIMARY KEY(character_id,kind,item_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal const string ExpBonusSchema = """
        CREATE TABLE IF NOT EXISTS character_exp_bonus(
          character_id INTEGER PRIMARY KEY,
          account TEXT NOT NULL,
          until_unix INTEGER NOT NULL CHECK(until_unix>=0),
          permille INTEGER NOT NULL CHECK(permille BETWEEN 0 AND 10000),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal const string ExpertJobSchema = """
        CREATE TABLE IF NOT EXISTS character_expert_job(
          character_id INTEGER PRIMARY KEY,
          account TEXT NOT NULL,
          job_type INTEGER NOT NULL CHECK(job_type BETWEEN 1 AND 4),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal const string ExpertJobMetaSchema = """
        CREATE TABLE IF NOT EXISTS character_expert_meta(
          character_id INTEGER PRIMARY KEY,
          account TEXT NOT NULL,
          giveup_state INTEGER NOT NULL DEFAULT 0 CHECK(giveup_state BETWEEN 0 AND 3),
          machine_grade INTEGER NOT NULL DEFAULT 1 CHECK(machine_grade BETWEEN 0 AND 11),
          machine_endurance INTEGER NOT NULL DEFAULT 300 CHECK(machine_endurance>=0),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal const string TeleportCooldownSchema = """
        CREATE TABLE IF NOT EXISTS character_teleport_cooldowns(
          character_id INTEGER PRIMARY KEY,
          account TEXT NOT NULL,
          potion_until INTEGER NOT NULL CHECK(potion_until>=0),
          aura_until INTEGER NOT NULL CHECK(aura_until>=0),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    // 拍卖行挂牌跨账号可见，不归属单个角色；卖家为机器人时 seller_character_id 为 NULL，
    // 因此本表不建立指向 characters 的外键。item_entry 是物品条目的字节快照，长度与内容由物品条目写入方负责，本表只保存字节。
    // 本结构由 v37 一步建出（含 deposit_gold），与 `Sqlite/game_schema.sql` 的当前结构逐列一致。
    // 用 `IF NOT EXISTS`（与 v34/v36 的同款做法）：真正的老库（< v37）没有这张表，这里建出来；
    // 而「拿当前结构库改版本号回退」的迁移夹具、以及从 `game_schema.sql` 直接重建的开发库可能已经有了它，
    // 重复建表会撞名。列是否齐全由 `ValidateSchema` 的必需列检查兜底，不靠 CREATE 报错。
    internal const string AuctionListingSchema = """
        -- state: 0=在售 1=已成交 2=已下架 3=已过期；buyout_price 为 -1 表示无一口价。
        CREATE TABLE IF NOT EXISTS auction_listings(
          listing_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(listing_id BETWEEN 1 AND 2147483647),
          seller_character_id INTEGER,
          seller_name TEXT NOT NULL,
          item_template_id INTEGER NOT NULL CHECK(item_template_id BETWEEN 0 AND 2147483647),
          item_kind INTEGER NOT NULL CHECK(item_kind BETWEEN 0 AND 255),
          item_count INTEGER NOT NULL CHECK(item_count BETWEEN 1 AND 2147483647),
          item_entry BLOB NOT NULL CHECK(typeof(item_entry)='blob' AND length(item_entry)>0),
          starting_price INTEGER NOT NULL CHECK(starting_price BETWEEN 0 AND 2147483647),
          buyout_price INTEGER NOT NULL CHECK(buyout_price BETWEEN -1 AND 2147483647),
          current_bid INTEGER NOT NULL DEFAULT 0 CHECK(current_bid BETWEEN 0 AND 2147483647),
          current_bidder_character_id INTEGER,
          state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 3),
          posted_at_unix INTEGER NOT NULL CHECK(posted_at_unix>=0),
          expires_at_unix INTEGER NOT NULL CHECK(expires_at_unix>=posted_at_unix),
          settled_at_unix INTEGER,
          -- deposit_gold: 挂牌阶段收取的保证金（金币，逐行记录）。取消上架不退还、到期未售按此金额原额退还。
          deposit_gold INTEGER NOT NULL DEFAULT 0 CHECK(deposit_gold BETWEEN 0 AND 2147483647)
        );
        CREATE INDEX IF NOT EXISTS auction_listings_expiry ON auction_listings(state,expires_at_unix);
        CREATE INDEX IF NOT EXISTS auction_listings_seller ON auction_listings(seller_character_id,state);
        CREATE INDEX IF NOT EXISTS auction_listings_template ON auction_listings(state,item_template_id,listing_id);
        """;

    // 公会不归属单个角色：`guilds` 不建立指向 characters 的外键，会长删角由删除业务同事务解散公会。
    // `guild_members.character_id` 外键到 characters，普通成员删角只清理自己的成员行。
    // 名称唯一约束就是「公会名重复确认」的唯一依据，业务不得另建第二套内存去重。
    // 本结构由 v44 一步建出，与 `Sqlite/game_schema.sql` 的当前结构逐列一致；`IF NOT EXISTS` 的理由与 v37 相同。
    internal const string GuildSchema = """
        CREATE TABLE IF NOT EXISTS guilds(
          guild_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(guild_id BETWEEN 1 AND 2147483647),
          -- name: 客户端提交的 GBK 名称按 UTF-8 原样保存；唯一约束（默认 BINARY 比较）即查重判据。
          -- 长度上限取客户端提示「中文7字符或英文14字符」，按 CP936 字节数 14 折算到字符数即 ≤14。
          name TEXT NOT NULL UNIQUE CHECK(length(name) BETWEEN 1 AND 14),
          -- address: 创建窗口的「地址/域名」字段（客户端 0x9D CHECK_GUILD_ADDREASS_DOUBLE 同样要求唯一）。
          address TEXT NOT NULL UNIQUE CHECK(length(address) BETWEEN 1 AND 24),
          master_character_id INTEGER NOT NULL CHECK(master_character_id>0),
          -- tier: 0=low class 1=mid class 2=high class 3=guardian 4=conqueror，与 etc/guildreform.etc 的档位同名。
          tier INTEGER NOT NULL DEFAULT 0 CHECK(tier BETWEEN 0 AND 4),
          level INTEGER NOT NULL DEFAULT 1 CHECK(level BETWEEN 1 AND 50),
          experience INTEGER NOT NULL DEFAULT 0 CHECK(experience>=0),
          public_funds INTEGER NOT NULL DEFAULT 0 CHECK(public_funds BETWEEN 0 AND 2147483647),
          guild_crystal INTEGER NOT NULL DEFAULT 0 CHECK(guild_crystal BETWEEN 0 AND 2147483647),
          notice TEXT NOT NULL DEFAULT '',
          memo TEXT NOT NULL DEFAULT '',
          created_at_unix INTEGER NOT NULL CHECK(created_at_unix>=0)
        );
        CREATE TABLE IF NOT EXISTS guild_members(
          guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
          character_id INTEGER NOT NULL UNIQUE CHECK(character_id>0) REFERENCES characters(id) ON DELETE CASCADE,
          grade INTEGER NOT NULL DEFAULT 0 CHECK(grade BETWEEN 0 AND 255),
          contribution INTEGER NOT NULL DEFAULT 0 CHECK(contribution BETWEEN 0 AND 2147483647),
          member_memo TEXT NOT NULL DEFAULT '',
          joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
          last_login_unix INTEGER NOT NULL DEFAULT 0 CHECK(last_login_unix>=0),
          PRIMARY KEY(guild_id,character_id)
        );
        CREATE INDEX IF NOT EXISTS guild_members_grade ON guild_members(guild_id,grade);
        """;

    internal const string QuestEffectsSchema = """
        CREATE TABLE IF NOT EXISTS character_quest_effect_receipts(
          character_id INTEGER NOT NULL REFERENCES characters(id),operation_id TEXT NOT NULL CHECK(length(operation_id)=32),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          claimed_quests TEXT NOT NULL CHECK(json_valid(claimed_quests) AND json_type(claimed_quests)='array'),
          changed_items TEXT NOT NULL CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
          PRIMARY KEY(character_id,operation_id));
        CREATE TABLE IF NOT EXISTS character_quest_special_reward_claims(
          character_id INTEGER NOT NULL,quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),operation_id TEXT NOT NULL,
          PRIMARY KEY(character_id,quest_id),
          FOREIGN KEY(character_id,operation_id) REFERENCES character_quest_effect_receipts(character_id,operation_id));
        """;

    internal const string HotkeySettingsSchema = """
        CREATE TABLE account_hotkey_settings(
          account TEXT NOT NULL REFERENCES accounts(account_id),profile INTEGER NOT NULL CHECK(profile IN (0,1)),
          hotkeys BLOB NOT NULL CHECK(typeof(hotkeys)='blob' AND length(hotkeys) BETWEEN 1 AND 1399),
          PRIMARY KEY(account,profile));
        """;

    internal const string TutorialSchema = """
        CREATE TABLE character_tutorial_state(
          character_id INTEGER PRIMARY KEY,account TEXT NOT NULL,dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),
          completed INTEGER NOT NULL CHECK(completed IN(0,1)),reward_received INTEGER NOT NULL CHECK(reward_received IN(0,1)),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        CREATE TABLE character_tutorial_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL CHECK(length(event_id)=32),
          kind TEXT NOT NULL CHECK(kind IN('death','settlement','reward','skip','clear')),
          instance_id TEXT NOT NULL CHECK(length(instance_id)=32),task_attempt INTEGER NOT NULL CHECK(task_attempt>=0),
          epoch INTEGER NOT NULL CHECK(epoch>=0),map_id INTEGER NOT NULL CHECK(map_id>=0),subject INTEGER NOT NULL CHECK(subject>=0),
          experience INTEGER NOT NULL CHECK(experience BETWEEN 0 AND 4294967295),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          plan_snapshot TEXT NOT NULL CHECK(length(plan_snapshot)>0),
          PRIMARY KEY(character_id,event_id),FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
          FOREIGN KEY(character_id) REFERENCES character_tutorial_state(character_id));
        """;

    private static void BackfillDungeonFatigue(SqliteConnection db, SqliteTransaction tx) => Execute(db, tx, """
            INSERT INTO character_fatigue(character_id,account,used,maximum,used_maximum,remaining,context_auxiliary,selection_auxiliary)
            SELECT c.id,c.account,0,156,0,156,0,0 FROM characters c
            JOIN character_dungeon_state d ON d.character_id=c.id AND d.account=c.account
            WHERE NOT EXISTS(SELECT 1 FROM character_fatigue f WHERE f.character_id=c.id)
              AND NOT EXISTS(SELECT 1 FROM character_dungeon_fatigue_receipts r WHERE r.character_id=c.id)
            """);

    internal const string AdventurePurchasesSchema = """
        CREATE TABLE account_adventure_purchases(
          account TEXT NOT NULL REFERENCES account_adventures(account),item_id INTEGER NOT NULL CHECK(item_id>0),
          count INTEGER NOT NULL CHECK(count BETWEEN 1 AND 4294967295),PRIMARY KEY(account,item_id));
        """;

    internal const string AdventureWeeklyConfigSchema = """
        CREATE TABLE account_adventure_weekly_config(
          account TEXT PRIMARY KEY REFERENCES account_adventures(account),
          slots BLOB NOT NULL CHECK(typeof(slots)='blob' AND length(slots)=5));
        """;

    // 角色每周地下城入场次数：每个角色每周每列一行，旧周行保留用于历史排查；删除角色时级联删除。
    internal const string WeeklyDungeonUsageSchema = """
        CREATE TABLE character_weekly_dungeon(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,week INTEGER NOT NULL CHECK(week BETWEEN 0 AND 99999999),
          slot INTEGER NOT NULL CHECK(slot BETWEEN 0 AND 8),used INTEGER NOT NULL CHECK(used BETWEEN 0 AND 1000000),
          PRIMARY KEY(character_id,week,slot),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
        CREATE INDEX character_weekly_dungeon_account ON character_weekly_dungeon(account,week);
        """;

    internal const string AdventureExpeditionSchema = """
        CREATE TABLE account_adventure_expeditions(
          account TEXT NOT NULL REFERENCES account_adventures(account),area INTEGER NOT NULL CHECK(area BETWEEN 1 AND 255),
          run_id TEXT NOT NULL UNIQUE CHECK(length(run_id)=32),started INTEGER NOT NULL CHECK(started>=0),
          ends INTEGER NOT NULL CHECK(ends>started),reward INTEGER NOT NULL CHECK(reward BETWEEN 0 AND 4294967295),
          extra_reward INTEGER NOT NULL CHECK(extra_reward BETWEEN 0 AND 4294967295),
          attributes TEXT NOT NULL CHECK(json_valid(attributes)),PRIMARY KEY(account,area));
        CREATE TABLE account_adventure_expedition_members(
          account TEXT NOT NULL,area INTEGER NOT NULL,ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 3),
          character_id INTEGER NOT NULL,member_snapshot TEXT NOT NULL CHECK(json_valid(member_snapshot)),
          PRIMARY KEY(account,area,ordinal),UNIQUE(account,character_id),
          FOREIGN KEY(account,area) REFERENCES account_adventure_expeditions(account,area) ON DELETE CASCADE,
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        CREATE TABLE account_adventure_receipts(
          account TEXT NOT NULL REFERENCES account_adventures(account),event_id TEXT NOT NULL,
          character_id INTEGER,action TEXT NOT NULL,amount INTEGER NOT NULL CHECK(amount>=0),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision>before_revision),
          PRIMARY KEY(account,event_id),FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;

    internal const string AdventureRepresentativeSchema = """
        CREATE TABLE account_adventure_representatives(
          account TEXT NOT NULL REFERENCES account_adventures(account),job INTEGER NOT NULL CHECK(job BETWEEN 0 AND 15),
          grow INTEGER NOT NULL CHECK(grow BETWEEN 1 AND 15),character_id INTEGER,
          PRIMARY KEY(account,job,grow),FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
        """;

    internal const string AdventureSchema = """
        CREATE TABLE account_adventures(
          account TEXT PRIMARY KEY REFERENCES accounts(account_id),revision INTEGER NOT NULL CHECK(revision>=0),
          name TEXT NOT NULL CHECK(length(name)>0),created_date INTEGER NOT NULL CHECK(created_date BETWEEN 19000101 AND 99991231),
          level INTEGER NOT NULL CHECK(level BETWEEN 1 AND 255),experience INTEGER NOT NULL CHECK(experience>=0),
          remainder TEXT NOT NULL,capsule_experience INTEGER NOT NULL CHECK(capsule_experience>=0),
          brave_experience INTEGER NOT NULL CHECK(brave_experience>=0),points BLOB NOT NULL CHECK(length(points)=20),
          month INTEGER NOT NULL CHECK(month BETWEEN 190001 AND 999912));
        """;

    internal const string TitleSchema = """
        CREATE TABLE character_title_book_slots(
            character_id INTEGER NOT NULL,
            account TEXT NOT NULL,
            list_type INTEGER NOT NULL CHECK(list_type BETWEEN 18 AND 22),
            slot_index INTEGER NOT NULL CHECK(slot_index BETWEEN 0 AND 255),
            item_uid INTEGER NOT NULL UNIQUE,
            PRIMARY KEY(character_id,list_type,slot_index),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
            FOREIGN KEY(account,character_id,item_uid) REFERENCES character_inventory_items(account,owner_character_id,item_uid) ON DELETE CASCADE
        );
        CREATE TABLE character_title_progress(
            character_id INTEGER NOT NULL,
            account TEXT NOT NULL,
            quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
            remaining1 INTEGER NOT NULL CHECK(remaining1 BETWEEN 0 AND 65535),
            remaining2 INTEGER NOT NULL CHECK(remaining2 BETWEEN 0 AND 65535),
            remaining3 INTEGER NOT NULL CHECK(remaining3 BETWEEN 0 AND 65535),
            completed_revision INTEGER NOT NULL DEFAULT 0 CHECK(completed_revision>=0),
            PRIMARY KEY(character_id,quest_id),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
            CHECK((remaining1+remaining2+remaining3=0 AND completed_revision>0) OR (remaining1+remaining2+remaining3>0 AND completed_revision=0))
        );
        CREATE TABLE character_title_sections(
            character_id INTEGER NOT NULL,
            account TEXT NOT NULL,
            section_key INTEGER NOT NULL CHECK(section_key BETWEEN 0 AND 1279),
            revision INTEGER NOT NULL CHECK(revision>0),
            PRIMARY KEY(character_id,section_key),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        CREATE TRIGGER title_book_exclusive_insert BEFORE INSERT ON character_title_book_slots
        WHEN EXISTS(SELECT 1 FROM character_inventory_slots WHERE item_uid=NEW.item_uid) OR
             EXISTS(SELECT 1 FROM mailbox_attachments WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'title item already has a position'); END;
        CREATE TRIGGER title_book_exclusive_update BEFORE UPDATE ON character_title_book_slots
        WHEN EXISTS(SELECT 1 FROM character_inventory_slots WHERE item_uid=NEW.item_uid) OR
             EXISTS(SELECT 1 FROM mailbox_attachments WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'title item already has a position'); END;
        CREATE TRIGGER inventory_not_title_insert BEFORE INSERT ON character_inventory_slots
        WHEN EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in title book'); END;
        CREATE TRIGGER inventory_not_title_update BEFORE UPDATE ON character_inventory_slots
        WHEN EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in title book'); END;
        CREATE TRIGGER mail_not_title_insert BEFORE INSERT ON mailbox_attachments
        WHEN EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in title book'); END;
        CREATE TRIGGER mail_not_title_update BEFORE UPDATE ON mailbox_attachments
        WHEN EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in title book'); END;
        """;

    internal const string FatigueSchema = """
        CREATE TABLE character_fatigue(
          character_id INTEGER PRIMARY KEY,account TEXT NOT NULL,
          used INTEGER NOT NULL CHECK(used BETWEEN 0 AND maximum),
          maximum INTEGER NOT NULL CHECK(maximum BETWEEN 1 AND 65535),
          used_maximum INTEGER NOT NULL CHECK(used_maximum BETWEEN 0 AND 65535),
          remaining INTEGER NOT NULL CHECK(remaining BETWEEN 0 AND maximum),
          context_auxiliary INTEGER NOT NULL CHECK(context_auxiliary BETWEEN 0 AND 65535),
          selection_auxiliary INTEGER NOT NULL CHECK(selection_auxiliary BETWEEN 0 AND 65535),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        CREATE TABLE character_dungeon_fatigue_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),map_id INTEGER NOT NULL CHECK(map_id BETWEEN 76121 AND 76126),
          epoch INTEGER NOT NULL CHECK(epoch>0),generation INTEGER NOT NULL CHECK(generation>0),rule_id TEXT NOT NULL,
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          before_state TEXT NOT NULL CHECK(json_valid(before_state) AND json_type(before_state)='object'),
          after_state TEXT NOT NULL CHECK(json_valid(after_state) AND json_type(after_state)='object'),
          PRIMARY KEY(character_id,instance_id,map_id),UNIQUE(character_id,task_attempt,map_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;

    static void UpgradeNormalDungeonReceipts(SqliteConnection db, SqliteTransaction tx)
    {
        // 成长的子账本须一起保留并重建外键，不能关外键、删历史奖励或修改已发布迁移。
        var tables = new List<string> { "character_dungeon_growth_receipts", "character_dungeon_loot_receipts",
            "character_dungeon_pickup_receipts", "character_quest_clear_receipts", "character_quest_completion_receipts",
            "character_dungeon_settlement_receipts", "character_dungeon_fatigue_receipts" };
        // 集合账本由掉落模块登记；已存在时必须连同成长父表重建，保持原字段、数据及外键。
        using (var query = db.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = """
                SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN
                  ('character_dungeon_drop_batches','character_dungeon_drop_objects','character_dungeon_drop_pickups')
                """;
            int count = Convert.ToInt32(query.ExecuteScalar());
            if (count is not (0 or 3))
                throw new InvalidDataException("普通副本升级需要完整的已有掉落集合外键依赖");
            if (count == 3)
                tables.AddRange(["character_dungeon_drop_batches", "character_dungeon_drop_objects", "character_dungeon_drop_pickups"]);
            // 已发布任务v14也引用成长回执；有历史任务掉落时须先重建子表再替换父表。
            query.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='character_quest_drop_receipts'";
            if (Convert.ToInt32(query.ExecuteScalar()) == 1)
                tables.Add("character_quest_drop_receipts");
        }
        var schemas = new Dictionary<string, string>();
        var secondary = new List<string>();
        using (var query = db.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE sql IS NOT NULL";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                if (!tables.Contains(reader.GetString(2))) continue;
                if (reader.GetString(0) == "table") schemas.Add(reader.GetString(1), reader.GetString(3));
                else if (reader.GetString(0) is "index" or "trigger") secondary.Add(reader.GetString(3));
            }
        }
        foreach (string table in tables)
        {
            if (!schemas.TryGetValue(table, out string? sql))
                throw new InvalidDataException("普通副本迁移缺少原账本：" + table);
            if (table is "character_dungeon_growth_receipts" or "character_dungeon_settlement_receipts")
            {
                if (!sql.Contains("CHECK(quest_id>0)", StringComparison.Ordinal) && !sql.Contains("CHECK(quest_id>=0)", StringComparison.Ordinal))
                    throw new InvalidDataException("普通副本迁移的原任务身份约束不匹配");
                sql = sql.Replace("CHECK(quest_id>0)", "CHECK(quest_id>=0)", StringComparison.Ordinal);
            }
            if (table == "character_dungeon_fatigue_receipts")
            {
                if (!sql.Contains("CHECK(map_id BETWEEN 76121 AND 76126)", StringComparison.Ordinal) && !sql.Contains("CHECK(map_id>0)", StringComparison.Ordinal))
                    throw new InvalidDataException("普通副本迁移的原地图约束不匹配");
                sql = sql.Replace("CHECK(map_id BETWEEN 76121 AND 76126)", "CHECK(map_id>0)", StringComparison.Ordinal);
            }
            foreach (string name in tables)
                sql = sql.Replace(name, name + "_normal", StringComparison.Ordinal);
            Execute(db, tx, sql);
            using var columnsQuery = db.CreateCommand();
            columnsQuery.Transaction = tx;
            columnsQuery.CommandText = $"PRAGMA table_info({table})";
            var columns = new List<string>();
            using (var reader = columnsQuery.ExecuteReader())
                while (reader.Read()) columns.Add('"' + reader.GetString(1).Replace("\"", "\"\"") + '"');
            Execute(db, tx, $"INSERT INTO {table}_normal(rowid,{string.Join(',', columns)}) SELECT rowid,* FROM {table}");
        }
        foreach (string table in Enumerable.Reverse(tables)) Execute(db, tx, $"DROP TABLE {table}");
        foreach (string table in tables) Execute(db, tx, $"ALTER TABLE {table}_normal RENAME TO {table}");
        foreach (string sql in secondary) Execute(db, tx, sql);
        using var integrity = db.CreateCommand();
        integrity.Transaction = tx;
        integrity.CommandText = "PRAGMA foreign_key_check";
        using var failures = integrity.ExecuteReader();
        if (failures.Read()) throw new InvalidDataException("普通副本迁移破坏原账本外键");
    }

    static void UpgradeQuestSystem(SqliteConnection db, SqliteTransaction tx)
    {
        // master已发布的任务v14与普通分支的v14..v16结构不同，按实际字段辨认；两路均升级到17。
        using (var existing = db.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = """
                SELECT (SELECT count(*) FROM pragma_table_info('characters') WHERE name IN ('quest_cycle_day','equipment_slot_flags')) +
                  (SELECT count(*) FROM pragma_table_info('character_completed_quests') WHERE name IN ('daily_period','answer'))
                """;
            int columns = Convert.ToInt32(existing.ExecuteScalar());
            if (columns == 4) return;
            if (columns != 0) throw new InvalidDataException("已有任务结构不完整，拒绝兼容升级");
        }
        // 直接从已发布v13建立本版最终结构；已有账本最多重建一次，不改历史规则或回执内容。
        Execute(db, tx, "PRAGMA defer_foreign_keys=ON");
        Execute(db, tx, "ALTER TABLE characters ADD COLUMN quest_cycle_day INTEGER NOT NULL DEFAULT 0 CHECK(quest_cycle_day=0 OR quest_cycle_day BETWEEN 19000101 AND 99991231)");
        Execute(db, tx, "ALTER TABLE characters ADD COLUMN equipment_slot_flags INTEGER NOT NULL DEFAULT 0 CHECK(equipment_slot_flags BETWEEN 0 AND 255)");
        Execute(db, tx, "ALTER TABLE character_completed_quests ADD COLUMN daily_period INTEGER NOT NULL DEFAULT 0 CHECK(daily_period=0 OR daily_period BETWEEN 19000101 AND 99991231)");
        Execute(db, tx, "ALTER TABLE character_completed_quests ADD COLUMN answer INTEGER CHECK(answer BETWEEN 0 AND 254)");
        RebuildReceipt(db, tx, "character_quest_clear_receipts", QuestClearSchemaV14);
        RebuildReceipt(db, tx, "character_quest_completion_receipts", QuestCompletionSchemaV14, "clear_acceptance_id", "acceptance_id");
        Execute(db, tx, QuestLifecycleSchema);
        Execute(db, tx, AccountQuestSchema);
        Execute(db, tx, QuestDropSchema);
        Execute(db, tx, QuestSkipSchema);
        using var check = db.CreateCommand(); check.Transaction = tx; check.CommandText = "PRAGMA foreign_key_check";
        using var rows = check.ExecuteReader();
        if (rows.Read()) throw new InvalidDataException("任务系统迁移后的历史外键不完整");
    }

    static void RebuildReceipt(SqliteConnection db, SqliteTransaction tx, string table, string schema,
        string? addedColumn = null, string? addedValue = null)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        var columns = new List<string>(); var dependencies = new List<string>();
        command.CommandText = "PRAGMA table_info(" + table + ")";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add('"' + reader.GetString(1).Replace("\"", "\"\"") + '"');
        if (columns.Count == 0) throw new InvalidDataException("缺少已发布任务或副本账本: " + table);
        command.Parameters.AddWithValue("$table", table);
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE tbl_name=$table AND type IN ('index','trigger') AND sql IS NOT NULL ORDER BY name";
        using (var reader = command.ExecuteReader()) while (reader.Read()) dependencies.Add(reader.GetString(0));
        string backup = table + "_quest_migration";
        Execute(db, tx, "CREATE TEMP TABLE " + backup + " AS SELECT rowid AS migration_rowid,* FROM " + table);
        Execute(db, tx, "DROP TABLE " + table);
        Execute(db, tx, schema);
        string selected = string.Join(',', columns);
        Execute(db, tx, "INSERT INTO " + table + "(rowid," + selected + (addedColumn is null ? "" : "," + addedColumn) + ") SELECT migration_rowid," +
            selected + (addedValue is null ? "" : "," + addedValue) + " FROM temp." + backup);
        Execute(db, tx, "DROP TABLE temp." + backup);
        // 历史行恢复之后才重建触发器，避免重放已提交事件。
        foreach (string dependency in dependencies) Execute(db, tx, dependency);
    }

    private const string QuestSkipSchema = """
        CREATE TABLE character_quest_skip_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          parent_key INTEGER NOT NULL CHECK(parent_key BETWEEN 0 AND 2147483647),question_quest INTEGER NOT NULL CHECK(question_quest BETWEEN 1 AND 29999),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot) AND json_type(plan_snapshot)='object'),
          PRIMARY KEY(character_id,acceptance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    private const string QuestSkipSchemaV18 = """
        CREATE TABLE character_quest_skip_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          acceptance_id TEXT CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          parent_key INTEGER CHECK(parent_key BETWEEN 0 AND 2147483647),question_quest INTEGER CHECK(question_quest BETWEEN 1 AND 29999),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot) AND json_type(plan_snapshot)='object'),
          operation_id TEXT NOT NULL CHECK(length(operation_id)=32 AND operation_id NOT GLOB '*[^0-9a-f]*' AND operation_id<>'00000000000000000000000000000000'),
          operation_kind TEXT NOT NULL CHECK(operation_kind IN('QuickClear','Manual','Ticket')),
          CHECK((operation_kind='QuickClear' AND acceptance_id IS NOT NULL AND operation_id=acceptance_id AND parent_key IS NOT NULL AND question_quest IS NOT NULL)
            OR (operation_kind IN('Manual','Ticket') AND acceptance_id IS NULL AND parent_key IS NULL AND question_quest IS NULL)),
          PRIMARY KEY(character_id,operation_id),UNIQUE(character_id,acceptance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    // v18统一接管已发布master及旧任务分支的已知回执结构，不重写历史规则或奖励。
    static void CompleteQuestSystemV18(SqliteConnection db, SqliteTransaction tx)
    {
        const string completion = "character_quest_completion_receipts", skip = "character_quest_skip_receipts";
        bool updateCompletion = NeedsUpdate(completion, QuestCompletionSchemaV18, QuestCompletionSchemaV14,
            QuestCompletionSchemaV18.Replace("44,49,64", "44,64"));
        bool updateSkip = NeedsUpdate(skip, QuestSkipSchemaV18, QuestSkipSchema);
        if (!updateCompletion && !updateSkip) return;
        Execute(db, tx, "PRAGMA defer_foreign_keys=ON");
        if (updateCompletion) RebuildReceipt(db, tx, completion, QuestCompletionSchemaV18);
        if (updateSkip) RebuildReceipt(db, tx, skip, QuestSkipSchemaV18, "operation_id,operation_kind", "acceptance_id,'QuickClear'");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("v18任务账本保全对齐后的外键不完整");

        bool NeedsUpdate(string table, string current, params string[] legacy)
        {
            using var query = db.CreateCommand(); query.Transaction = tx;
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            query.Parameters.AddWithValue("$table", table);
            if (query.ExecuteScalar() is not string actual)
                throw new InvalidDataException("v18缺少任务账本: " + table);
            string canonical = NormalizeTable(actual, table);
            if (canonical == NormalizeTable(current, table)) return false;
            if (legacy.Any(sql => canonical == NormalizeTable(sql, table))) return true;
            throw new InvalidDataException("v18任务账本不是已确认的新旧结构: " + table);
        }
    }

    // 旧任务分支v14保留了已发布疲劳表，原v15步骤只在两表确实缺失时建表。
    static bool HasPublishedFatigue(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_fatigue','character_dungeon_fatigue_receipts')";
        int count = Convert.ToInt32(query.ExecuteScalar());
        if (count == 0) return false;
        if (count != 2) throw new InvalidDataException("已有疲劳owner结构不完整，拒绝兼容升级");
        foreach (string expected in FatigueSchema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string table = expected.StartsWith("CREATE TABLE character_fatigue(", StringComparison.Ordinal)
                ? "character_fatigue" : "character_dungeon_fatigue_receipts";
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            query.Parameters.Clear(); query.Parameters.AddWithValue("$table", table);
            if (query.ExecuteScalar() is not string actual ||
                (NormalizeTable(actual, table) != NormalizeTable(expected, table) &&
                 NormalizeTable(actual, table) != NormalizeTable(expected.Replace("CHECK(map_id BETWEEN 76121 AND 76126)", "CHECK(map_id>0)"), table)))
                throw new InvalidDataException("已有疲劳owner不是已发布结构: " + table);
        }
        return true;
    }

    // SQLite重命名表会为表名及外键引用补双引号，仍按相同已知声明比较。
    internal static string NormalizeTable(string sql, string table) => string.Concat(sql.Where(c => !char.IsWhiteSpace(c)))
        .TrimEnd(';').Replace("\"" + table + "\"", table)
        .Replace("\"character_quest_clear_receipts\"", "character_quest_clear_receipts")
        .Replace("\"character_dungeon_growth_receipts\"", "character_dungeon_growth_receipts")
        .Replace("\"character_dungeon_scene_clear_receipts\"", "character_dungeon_scene_clear_receipts")
        .Replace("\"character_quest_lifecycle_receipts\"", "character_quest_lifecycle_receipts")
        .Replace("\"character_dungeon_settlement_receipts\"", "character_dungeon_settlement_receipts")
        .Replace("\"character_dungeon_drop_objects\"", "character_dungeon_drop_objects")
        .Replace("\"character_dungeon_drop_batches\"", "character_dungeon_drop_batches")
        .Replace("\"character_dungeon_loot_receipts\"", "character_dungeon_loot_receipts")
        .Replace("\"characters\"", "characters");

    // 领奖账本的后续迁移只向上追加 condition_type，后一版结构必然包含前一版的全部变更。
    // 表已是任一后续已知结构时，本步直接视为已应用；只有未知结构才拒绝。
    internal static bool ReceiptShapeApplied(string actual, string table, params string[] shapes)
    {
        string canonical = NormalizeTable(actual, table);
        foreach (string shape in shapes) if (canonical == NormalizeTable(shape, table)) return true;
        return false;
    }

    private const string QuestCompletionSchemaV18 = """
        CREATE TABLE character_quest_completion_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),event_id TEXT,instance_id TEXT,
          task_attempt INTEGER CHECK(task_attempt>0),epoch INTEGER CHECK(epoch>0),
          generation INTEGER NOT NULL CHECK(generation>0),rule_id TEXT NOT NULL,
          resource_source TEXT NOT NULL,quest_source TEXT NOT NULL,
          native_index INTEGER NOT NULL CHECK(native_index>=0),base_experience INTEGER NOT NULL CHECK(base_experience>=0),
          raw_additional_percent INTEGER NOT NULL,difficulty_key TEXT NOT NULL,difficulty_weight INTEGER NOT NULL CHECK(difficulty_weight>=0),
          experience_amount INTEGER NOT NULL CHECK(experience_amount BETWEEN 0 AND 2147483647),
          gold_amount INTEGER NOT NULL CHECK(gold_amount BETWEEN 0 AND 2147483647),
          before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
          before_experience INTEGER NOT NULL CHECK(before_experience>=0),after_experience INTEGER NOT NULL CHECK(after_experience=before_experience+experience_amount),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          rewards TEXT NOT NULL CHECK(json_valid(rewards) AND json_type(rewards)='array' AND json_array_length(rewards)<=255),
          clear_acceptance_id TEXT,condition_type INTEGER NOT NULL DEFAULT 3 CHECK(condition_type IN(0,1,2,3,4,6,12,25,31,39,40,42,44,49,64,65,67,70)),
          selector INTEGER NOT NULL DEFAULT 65535 CHECK(selector BETWEEN 0 AND 65535),multiplier INTEGER NOT NULL DEFAULT 1 CHECK(multiplier BETWEEN 1 AND 65535),
          consumed TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(consumed) AND json_type(consumed)='array' AND json_array_length(consumed)<=255),
          changed_items TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
          plan_snapshot TEXT CHECK(plan_snapshot IS NULL OR (json_valid(plan_snapshot) AND json_type(plan_snapshot)='object')),
          CHECK((condition_type IN(1,3) AND clear_acceptance_id=acceptance_id AND event_id IS NOT NULL AND instance_id IS NOT NULL AND task_attempt IS NOT NULL AND epoch IS NOT NULL)
            OR (condition_type IN(0,2,4,6,12,25,31,39,40,42,44,49,64,65,67,70) AND clear_acceptance_id IS NULL AND event_id IS NULL AND instance_id IS NULL AND task_attempt IS NULL AND epoch IS NULL AND plan_snapshot IS NOT NULL)),
          PRIMARY KEY(character_id,acceptance_id),
          FOREIGN KEY(character_id,clear_acceptance_id) REFERENCES character_quest_clear_receipts(character_id,acceptance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;

    private const string QuestLifecycleSchema = """
        CREATE TABLE character_quest_lifecycle_receipts(
          character_id INTEGER NOT NULL, account TEXT NOT NULL, operation_id TEXT NOT NULL CHECK(length(operation_id)=32 AND operation_id NOT GLOB '*[^0-9a-f]*' AND operation_id<>'00000000000000000000000000000000'),
          action TEXT NOT NULL CHECK(action IN ('Accept','Giveup','Condition')),
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0), revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          before_active TEXT NOT NULL CHECK(json_valid(before_active) AND json_type(before_active)='array'),
          after_active TEXT NOT NULL CHECK(json_valid(after_active) AND json_type(after_active)='array'),
          changed_items TEXT NOT NULL CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
          scene_event TEXT CHECK(scene_event IS NULL OR json_valid(scene_event)),
          before_completed TEXT CHECK(before_completed IS NULL OR (json_valid(before_completed) AND json_type(before_completed)='array')),
          after_completed TEXT CHECK(after_completed IS NULL OR (json_valid(after_completed) AND json_type(after_completed)='array')),
          PRIMARY KEY(character_id,operation_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    private const string QuestDropSchema = """
        CREATE TABLE character_quest_drop_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL CHECK(length(event_id)=32 AND event_id NOT GLOB '*[^0-9a-f]*'),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot)),
          changed_items TEXT NOT NULL CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
          PRIMARY KEY(character_id,event_id),
          FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    private const string AccountQuestSchema = """
        CREATE TABLE account_completed_quests(
          account TEXT NOT NULL REFERENCES accounts(account_id),
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
          character_id INTEGER NOT NULL,
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          daily_period INTEGER NOT NULL DEFAULT 0 CHECK(daily_period=0 OR daily_period BETWEEN 19000101 AND 99991231),
          answer INTEGER CHECK(answer BETWEEN 0 AND 254),
          PRIMARY KEY(account,quest_id),FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    internal const string QuestClearSchemaV14 = """
        CREATE TABLE character_quest_clear_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
          quest_slot INTEGER NOT NULL CHECK(quest_slot BETWEEN 0 AND 29),
          event_id TEXT NOT NULL,instance_id TEXT NOT NULL,
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
          dungeon_id INTEGER NOT NULL,map_id INTEGER NOT NULL,
          generation INTEGER NOT NULL CHECK(generation>0),revision INTEGER NOT NULL CHECK(revision>0),
          PRIMARY KEY(character_id,acceptance_id),

          FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    private const string QuestCompletionSchemaV14 = """
        CREATE TABLE character_quest_completion_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),event_id TEXT,instance_id TEXT,
          task_attempt INTEGER CHECK(task_attempt>0),epoch INTEGER CHECK(epoch>0),
          generation INTEGER NOT NULL CHECK(generation>0),rule_id TEXT NOT NULL,
          resource_source TEXT NOT NULL,quest_source TEXT NOT NULL,
          native_index INTEGER NOT NULL CHECK(native_index>=0),base_experience INTEGER NOT NULL CHECK(base_experience>=0),
          raw_additional_percent INTEGER NOT NULL,difficulty_key TEXT NOT NULL,difficulty_weight INTEGER NOT NULL CHECK(difficulty_weight>=0),
          experience_amount INTEGER NOT NULL CHECK(experience_amount BETWEEN 0 AND 2147483647),
          gold_amount INTEGER NOT NULL CHECK(gold_amount BETWEEN 0 AND 2147483647),
          before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
          before_experience INTEGER NOT NULL CHECK(before_experience>=0),after_experience INTEGER NOT NULL CHECK(after_experience=before_experience+experience_amount),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          rewards TEXT NOT NULL CHECK(json_valid(rewards) AND json_type(rewards)='array' AND json_array_length(rewards)<=255),
          clear_acceptance_id TEXT,condition_type INTEGER NOT NULL DEFAULT 3 CHECK(condition_type IN(0,1,2,3,4,6,12,25,31,39,40,42,44,64,65,67)),
          selector INTEGER NOT NULL DEFAULT 65535 CHECK(selector BETWEEN 0 AND 65535),multiplier INTEGER NOT NULL DEFAULT 1 CHECK(multiplier BETWEEN 1 AND 65535),
          consumed TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(consumed) AND json_type(consumed)='array' AND json_array_length(consumed)<=255),
          changed_items TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
          plan_snapshot TEXT CHECK(plan_snapshot IS NULL OR (json_valid(plan_snapshot) AND json_type(plan_snapshot)='object')),
          CHECK((condition_type IN(1,3) AND clear_acceptance_id=acceptance_id AND event_id IS NOT NULL AND instance_id IS NOT NULL AND task_attempt IS NOT NULL AND epoch IS NOT NULL)
            OR (condition_type IN(0,2,4,6,12,25,31,39,40,42,44,64,65,67) AND clear_acceptance_id IS NULL AND event_id IS NULL AND instance_id IS NULL AND task_attempt IS NULL AND epoch IS NULL AND plan_snapshot IS NOT NULL)),
          PRIMARY KEY(character_id,acceptance_id),
          FOREIGN KEY(character_id,clear_acceptance_id) REFERENCES character_quest_clear_receipts(character_id,acceptance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;

    // 只新增事实账本，不补发存量角色经验或点数。
    private const string DungeonGrowthSchema = """
        CREATE TABLE character_dungeon_growth_receipts(
        character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
        instance_id TEXT NOT NULL,quest_id INTEGER NOT NULL CHECK(quest_id>0),
        task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
        map_id INTEGER NOT NULL,monster_key INTEGER NOT NULL CHECK(monster_key BETWEEN 0 AND 65535),
        combat_sequence INTEGER NOT NULL CHECK(combat_sequence>0),
        generation INTEGER NOT NULL CHECK(generation>0),amount INTEGER NOT NULL CHECK(amount>0),
        rule_id TEXT NOT NULL,resource_sha256 TEXT NOT NULL,
        monster_id INTEGER NOT NULL,monster_level INTEGER NOT NULL CHECK(monster_level BETWEEN 1 AND 255),
        before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),
        after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
        before_experience INTEGER NOT NULL CHECK(before_experience>=0),
        after_experience INTEGER NOT NULL CHECK(after_experience=before_experience+amount),
        revision INTEGER NOT NULL CHECK(revision>0),
        PRIMARY KEY(character_id,event_id),UNIQUE(character_id,instance_id,monster_key),
        UNIQUE(character_id,instance_id,combat_sequence),
        FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
    public static int CurrentVersion => Steps.Length == 0 ? BaselineVersion : Steps[^1].Version;

    static void UpgradePremiumContracts(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_quest_effect_receipts','character_quest_special_reward_claims')";
        int effects = Convert.ToInt32(query.ExecuteScalar());
        if (effects != 2)
        {
            // 只接管已发布本分支的完整v34形状，不能借迁移修补未知缺表库。
            query.CommandText = "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='premium_contracts' AND upper(type)='TEXT' AND \"notnull\"=1 AND dflt_value='''{}'''";
            if (effects != 0 || Convert.ToInt32(query.ExecuteScalar()) != 1)
                throw new InvalidDataException("未知v34任务或契约账本结构");
            AddQuestEffects(db, tx);
        }
        AddPremiumContractsColumn(db, tx);
    }

    static void AddQuestEffects(SqliteConnection db,SqliteTransaction tx)
    {
        using var query=db.CreateCommand(); query.Transaction=tx;
        // 本地旧v32使用任务账本编号，未执行master的商店v32；只接管这两张已存在的账本形状。
        query.CommandText="""
            SELECT count(*) FROM sqlite_schema WHERE type='table'
              AND name IN('character_quest_effect_receipts','character_quest_special_reward_claims')
              AND NOT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='account_adventure_purchases')
            """;
        if(Convert.ToInt32(query.ExecuteScalar())==2) Execute(db,tx,AdventurePurchasesSchema);
        Execute(db,tx,QuestEffectsSchema);
    }

    static void AddSeriaLuckColumn(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='seria_luck'";
        if ((long)query.ExecuteScalar()! == 0)
            Execute(db, tx, "ALTER TABLE account_wallets ADD COLUMN seria_luck INTEGER NOT NULL DEFAULT 0 CHECK(seria_luck BETWEEN 0 AND 8)");
    }

    static void AddPremiumContractsColumn(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='premium_contracts'";
        if ((long)query.ExecuteScalar()! == 0)
            Execute(db, tx, "ALTER TABLE account_wallets ADD COLUMN premium_contracts TEXT NOT NULL DEFAULT '{}' CHECK(json_valid(premium_contracts) AND json_type(premium_contracts)='object')");
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='premium_contracts' AND upper(type)='TEXT' AND \"notnull\"=1 AND dflt_value='''{}'''";
        if (Convert.ToInt32(query.ExecuteScalar()) != 1)
            throw new InvalidDataException("账号契约列结构不兼容");
        query.CommandText = "SELECT count(*) FROM account_wallets WHERE NOT json_valid(premium_contracts) OR json_type(premium_contracts)!='object'";
        if (Convert.ToInt32(query.ExecuteScalar()) != 0)
            throw new InvalidDataException("账号契约账本非法");
    }
    private const string SkillMetadataSchema = """
        CREATE TABLE character_tp_sources(
         character_id INTEGER PRIMARY KEY,bonus_tp INTEGER NOT NULL CHECK(bonus_tp BETWEEN 0 AND 65535),
         FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE);
        CREATE TABLE character_skill_commands(
         character_id INTEGER NOT NULL,context INTEGER NOT NULL CHECK(context IN(0,1)),skill_id INTEGER NOT NULL CHECK(skill_id BETWEEN 0 AND 65535),
         keys BLOB NOT NULL CHECK(typeof(keys)='blob' AND length(keys) BETWEEN 1 AND 255),
         PRIMARY KEY(character_id,context,skill_id),FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE);
        CREATE TABLE character_skill_combos(
         character_id INTEGER NOT NULL,context INTEGER NOT NULL CHECK(context IN(0,1)),root_id INTEGER NOT NULL CHECK(root_id BETWEEN 1 AND 32767),
         children BLOB NOT NULL CHECK(typeof(children)='blob' AND length(children)<=12 AND length(children)%2=0),
         PRIMARY KEY(character_id,context,root_id),FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE);
        """;
    private const string SkillPointSourceSchema = """
        CREATE TABLE character_skill_point_sources(
          character_id INTEGER PRIMARY KEY,bonus_sp INTEGER NOT NULL CHECK(bonus_sp BETWEEN 0 AND 65535),
          FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE);
        """;
    private const string ExpertContractSchema = """
        CREATE TABLE account_expert_contracts(
          account TEXT PRIMARY KEY,expires_at INTEGER NOT NULL CHECK(expires_at>=0),
          FOREIGN KEY(account) REFERENCES accounts(account_id) ON DELETE CASCADE);
        """;
    internal const string DungeonSettlementSchema = """
        CREATE TABLE character_dungeon_settlement_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
          instance_id TEXT NOT NULL,quest_id INTEGER NOT NULL CHECK(quest_id>0),
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
          dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),map_id INTEGER NOT NULL,
          death_events BLOB NOT NULL CHECK(typeof(death_events)='blob' AND length(death_events)>0 AND length(death_events)%16=0),
          report_body BLOB NOT NULL CHECK(typeof(report_body)='blob' AND length(report_body) BETWEEN 1 AND 4096),
          generation INTEGER NOT NULL CHECK(generation>0),
          base_experience INTEGER NOT NULL CHECK(base_experience BETWEEN 0 AND 4294967295),
          rank_experience INTEGER NOT NULL CHECK(rank_experience BETWEEN 0 AND 4294967295),
          total_experience INTEGER NOT NULL CHECK(total_experience BETWEEN 1 AND 4294967295 AND total_experience=base_experience+rank_experience),
          rank_bonus_index INTEGER NOT NULL CHECK(rank_bonus_index BETWEEN -1 AND 4),
          rule_id TEXT NOT NULL,resource_source TEXT NOT NULL,
          before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),
          after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
          before_experience INTEGER NOT NULL CHECK(before_experience>=0),
          after_experience INTEGER NOT NULL CHECK(after_experience=before_experience+total_experience),
          revision INTEGER NOT NULL CHECK(revision>0),
          PRIMARY KEY(character_id,event_id),UNIQUE(character_id,instance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
    private const string QuestClearSchema = """
        CREATE TABLE character_quest_clear_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
          quest_slot INTEGER NOT NULL CHECK(quest_slot BETWEEN 0 AND 29),
          event_id TEXT NOT NULL,instance_id TEXT NOT NULL,
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
          dungeon_id INTEGER NOT NULL,map_id INTEGER NOT NULL,
          generation INTEGER NOT NULL CHECK(generation>0),
          revision INTEGER NOT NULL CHECK(revision>0),
          PRIMARY KEY(character_id,acceptance_id),UNIQUE(character_id,event_id),
          UNIQUE(character_id,instance_id),
          FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
    private const string QuestCompletionSchema = """
        CREATE TABLE character_quest_completion_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,acceptance_id TEXT NOT NULL,
          quest_id INTEGER NOT NULL CHECK(quest_id=3145),event_id TEXT NOT NULL,instance_id TEXT NOT NULL,
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
          generation INTEGER NOT NULL CHECK(generation>0),rule_id TEXT NOT NULL,
          resource_source TEXT NOT NULL,quest_source TEXT NOT NULL,
          native_index INTEGER NOT NULL CHECK(native_index>=0),base_experience INTEGER NOT NULL CHECK(base_experience>0),
          raw_additional_percent INTEGER NOT NULL,difficulty_key TEXT NOT NULL,difficulty_weight INTEGER NOT NULL CHECK(difficulty_weight>0),
          experience_amount INTEGER NOT NULL CHECK(experience_amount BETWEEN 1 AND 2147483647),
          gold_amount INTEGER NOT NULL CHECK(gold_amount=0),
          before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
          before_experience INTEGER NOT NULL CHECK(before_experience>=0),after_experience INTEGER NOT NULL CHECK(after_experience=before_experience+experience_amount),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          rewards TEXT NOT NULL CHECK(json_valid(rewards) AND json_type(rewards)='array' AND json_array_length(rewards)=7),
          PRIMARY KEY(character_id,acceptance_id),UNIQUE(character_id,quest_id),
          FOREIGN KEY(character_id,acceptance_id) REFERENCES character_quest_clear_receipts(character_id,acceptance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
    private const string DungeonLootSchema = """
        CREATE TABLE character_dungeon_loot_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
          instance_id TEXT NOT NULL,task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
          epoch INTEGER NOT NULL CHECK(epoch>0),map_id INTEGER NOT NULL,
          monster_key INTEGER NOT NULL CHECK(monster_key BETWEEN 1 AND 65535),
          rule_id TEXT NOT NULL,resource_source TEXT NOT NULL,
          ground_key INTEGER CHECK(ground_key BETWEEN 1 AND 65535),
          core TEXT CHECK(core IS NULL OR (json_valid(core) AND json_type(core)='object')),
          x INTEGER CHECK(x BETWEEN 0 AND 65535),y INTEGER CHECK(y BETWEEN 0 AND 65535),
          record_slot INTEGER CHECK(record_slot BETWEEN -32768 AND 32767),
          CHECK((ground_key IS NULL AND core IS NULL AND x IS NULL AND y IS NULL AND record_slot IS NULL) OR
            (ground_key IS NOT NULL AND core IS NOT NULL AND x IS NOT NULL AND y IS NOT NULL AND record_slot IS NOT NULL)),
          PRIMARY KEY(character_id,event_id),UNIQUE(character_id,ground_key),
          FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
    private const string DungeonPickupSchema = """
        CREATE TABLE character_dungeon_pickup_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
          ground_key INTEGER NOT NULL CHECK(ground_key BETWEEN 1 AND 65535),
          instance_id TEXT NOT NULL,task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
          epoch INTEGER NOT NULL CHECK(epoch>0),destination_list INTEGER NOT NULL,
          destination_slot INTEGER NOT NULL CHECK(destination_slot BETWEEN 0 AND 351),
          item_uid INTEGER NOT NULL CHECK(item_uid BETWEEN 1 AND 2147483647),
          after_core TEXT NOT NULL CHECK(json_valid(after_core) AND json_type(after_core)='object'),
          revision INTEGER NOT NULL CHECK(revision>0),
          PRIMARY KEY(character_id,event_id),UNIQUE(character_id,ground_key),
          FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_loot_receipts(character_id,event_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
    private const string DungeonOwnerSchema = """
        CREATE TABLE character_dungeon_state(
          character_id INTEGER NOT NULL PRIMARY KEY,account TEXT NOT NULL,
          next_object_key INTEGER NOT NULL CHECK(next_object_key BETWEEN 1 AND 65536),
          last_attempt INTEGER NOT NULL CHECK(last_attempt>=0),
          capsule_experience INTEGER NOT NULL CHECK(capsule_experience BETWEEN 0 AND 4294967295),
          proficiency_level INTEGER NOT NULL CHECK(proficiency_level BETWEEN 0 AND 4294967295),
          proficiency_experience INTEGER NOT NULL CHECK(proficiency_experience>=0),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;

    public static void Apply(SqliteConnection db) => Apply(db, Steps);

    internal static void Apply(SqliteConnection db, IReadOnlyList<SqliteMigration> steps)
    {
        for (int i = 0; i < steps.Count; i++)
            if (steps[i].Version != BaselineVersion + i + 1 || string.IsNullOrWhiteSpace(steps[i].Name) || steps[i].Upgrade is null)
                throw new InvalidOperationException("数据库迁移必须从 v2 开始连续编号并提供名称和实现");
        int current = steps.Count == 0 ? BaselineVersion : steps[^1].Version;
        using var write = db.BeginWrite("SqliteMigrations.Apply");
        using var tx = write.Transaction;
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='schema_metadata'";
        if ((long)query.ExecuteScalar()! != 1)
            throw new InvalidDataException("数据库缺少 S5A9 基线标识；请使用独立目录创建新库");
        query.CommandText = "SELECT value FROM schema_metadata WHERE key='baseline_id'";
        if (query.ExecuteScalar() is not string baseline || baseline != BaselineId)
            throw new InvalidDataException("数据库基线不属于本服务端");
        query.CommandText = "PRAGMA user_version";
        long version = (long)query.ExecuteScalar()!;
        if (version < BaselineVersion || version > current)
            throw new InvalidDataException($"数据库版本 {version} 不在当前支持范围 {BaselineVersion}–{current}");
        // 第三批验收曾用v92保存助战回执，正式v92已用于护石；只识别完整旧组合再补跑。
        if (version == 92 && current >= 98 && IsPartyDungeonPreview(db, tx)) version = 91;
        // 两类预览库按互斥结构识别，不以同号版本替代已发布迁移。
        // 本地任务预览曾占用88/89；只有两张副本次数表均缺失且任务表精确匹配才回到87补齐。
        if (current >= 97 && Quest704PreviewBaseline(db, tx, version) is { } previewBaseline) version = previewBaseline;
        // 仅精确匹配已知预览结构时补齐正式迁移，保留原冷却与资产。
        if (version is 78 or 79 && current >= 81 && IsAuraCooldownPreview(db, tx, version)) version = 77;
        if (version is >= 78 and <= 80 && current >= 91 && IsPlayerInteractionPreview(db, tx, version)) version = 77;
        if (current >= 91 && IsSocialNumberingPreview(db, tx, version)) version = 87;
        // #233 验收构建曾把强袭表作为v47发布：该库v47有强袭表却缺master的角色栏扩展，视为v46，重跑v47起（v50强袭建表幂等）。
        if (version == 47 && current >= 50 && IsAcceptanceAssaultV47(db, tx)) version = 46;
        bool existingFatigue = version == 14 && HasPublishedFatigue(db, tx);
        if (version == 34 && current >= 36 && DungeonCardClaimSchema.IsPreviousLayout(db,tx))
        {
            query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_quest_effect_receipts','character_quest_special_reward_claims')";
            if (query.ExecuteScalar() is 0L) AddQuestEffects(db,tx);
        }
        // 本地构建曾把副职业表记成 v56，而 origin/master 的 v56 是每日礼盒列。
        // 这类库版本号已到 56 却没有 daily_booster_usage，按 55 重跑；v57 建表使用 IF NOT EXISTS。
        if (version >= 56)
        {
            query.CommandText = "SELECT count(*) FROM pragma_table_info('characters') WHERE name='daily_booster_usage'";
            if (query.ExecuteScalar() is 0L) version = 55;
        }
        // 合并前 fix/channel-town-entry 的本地构建把副本次数两表记成 v62/v63，而 master 的 v62–v64 是眩惑领奖约束、职业变更次数、消耗品状态。
        // 这类库版本号是 62/63、已有 character_dungeon_incount 却没有 master v63 的 character_growup_change：按 61 重跑 master v62 起；
        // 主线 v67 是角色武器幻化列；在它之前合并分支把副本次数两表记成 v67/v68。这类库版本号是 67/68、已有 character_dungeon_incount
        // 却没有 characters.weapon_skin_state：按 66 重跑主线 v67 起（该步按列存在性补列）。
        // 次数两表（现 v88/v89）建表使用 IF NOT EXISTS，已有次数行原样保留。按表判定，不只看版本号。
        // 主线 v68 是道具永久属性来源表；在它之前本分支把副本次数两表记成 v68/v69。这类库版本号是 68/69、已有 character_dungeon_incount
        // 却没有 character_permanent_stats：按 67 重跑主线 v68 起（该步 IF NOT EXISTS 建表、经验列按结构补齐）。
        // 主线 v69–v73 是冒险团胶囊周期、结算分项、登录与推荐计数、每周配置、经验账本；在它之前本分支把次数两表记成 v69/v70。这类库版本号是 69/70、
        // 已有 character_dungeon_incount 却没有 account_adventures.capsule_week：按 68 重跑主线 v69 起（各步按列/表结构判定，可重复执行）。
        // 主线 v74–v82 是熟练度到账、商城积分、每周入场计数、制作熟练度、零经验回执、任务每日通关、副职业设计图、光环冷却、快捷聊天；在它之前本分支把次数两表记成 v74/v75。
        // 这类库版本号是 74/75、已有 character_dungeon_incount 却没有 character_dungeon_settlement_receipts.proficiency_gain：按 73 重跑主线 v74 起（各步按列/表结构判定）。
        // 主线 v83–v87 是悲叹账号日次数、悲叹通关回执、塔与祭坛入场结算、装扮预设、对象key按实例隔离；在它之前本分支把次数两表记成 v83/v84。
        // 这类库版本号是 83/84、已有 character_dungeon_incount 却没有 account_grief_tower：按 82 重跑主线 v83 起（五步均按表/列结构判定，可重复执行），
        // 次数两表现为 v88/v89，建表使用 IF NOT EXISTS，已有次数行原样保留，最终与新库 v89 结构一致。
        if (version is 62 or 63 && current >= 84 && IsLocalIncountNumbering(db, tx)) version = 61;
        else if (version is 67 or 68 && current >= 84 && IsPreSkinIncountNumbering(db, tx)) version = 66;
        else if (version is 68 or 69 && current >= 84 && IsPrePermanentStatIncountNumbering(db, tx)) version = 67;
        else if (version is 69 or 70 && current >= 84 && IsPreAdventureIncountNumbering(db, tx)) version = 68;
        else if (version is 74 or 75 && current >= 84 && IsPreProficiencyIncountNumbering(db, tx)) version = 73;
        else if (version is 83 or 84 && current >= 89 && IsPreGriefIncountNumbering(db, tx)) version = 82;
        // 本地整合构建曾把冒险团四项记成v64–v67，上游随后把v64/v65用于消耗品与拉黑计分。
        // 临时编号库有胶囊周期列却缺blacklist_count时按v63重跑；v64–v69的升级均可重复执行。
        if (version is >= 64 and <= 70 && current >= 73)
        {
            query.CommandText = "SELECT (SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='capsule_week')*10+(SELECT count(*) FROM pragma_table_info('characters') WHERE name='blacklist_count')";
            if (query.ExecuteScalar() is 10L) version = 63;
            // 本地整合构建把冒险团迁移排在v64–v70：版本落在该段却有胶囊周期列、缺其他加成分项时，
            // 说明用的是旧编号，回到v65按上游 #543、再按本分支账本步骤重放。
            query.CommandText = "SELECT (SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='capsule_week')*10+(SELECT count(*) FROM pragma_table_info('character_dungeon_settlement_receipts') WHERE name='other_bonus_experience')";
            if (version >= 66 && query.ExecuteScalar() is 10L) version = 65;
        }
        foreach (var step in steps)
        {
            if (step.Version <= version)
                continue;
            if (step.Version != 15 || !existingFatigue) step.Upgrade(db, tx);
            Execute(db, tx, $"PRAGMA user_version={step.Version}");
        }
        ValidateSchema(db, tx);
        tx.Commit();
    }

    internal static bool IsLocalIncountNumbering(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_dungeon_incount'";
        if (query.ExecuteScalar() is not 1L) return false;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_growup_change'";
        return query.ExecuteScalar() is 0L;
    }
    internal static bool IsPrePermanentStatIncountNumbering(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_dungeon_incount'";
        if (query.ExecuteScalar() is not 1L) return false;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_permanent_stats'";
        return query.ExecuteScalar() is 0L;
    }
    internal static bool IsPreAdventureIncountNumbering(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_dungeon_incount'";
        if (query.ExecuteScalar() is not 1L) return false;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='capsule_week'";
        return query.ExecuteScalar() is 0L;
    }
    internal static bool IsPreProficiencyIncountNumbering(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_dungeon_incount'";
        if (query.ExecuteScalar() is not 1L) return false;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('character_dungeon_settlement_receipts') WHERE name='proficiency_gain'";
        return query.ExecuteScalar() is 0L;
    }
    internal static bool IsPreGriefIncountNumbering(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_dungeon_incount'";
        if (query.ExecuteScalar() is not 1L) return false;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='account_grief_tower'";
        return query.ExecuteScalar() is 0L;
    }
    internal static bool IsPreSkinIncountNumbering(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_dungeon_incount'";
        if (query.ExecuteScalar() is not 1L) return false;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('characters') WHERE name='weapon_skin_state'";
        return query.ExecuteScalar() is 0L;
    }
    internal static void MarkCurrent(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO schema_metadata(key,value) VALUES('baseline_id',$baseline)";
        command.Parameters.AddWithValue("$baseline", BaselineId);
        command.ExecuteNonQuery();
        Execute(db, tx, $"PRAGMA user_version={CurrentVersion}");
    }

    static bool IsPartyDungeonPreview(SqliteConnection db, SqliteTransaction tx)
    {
        if (!KnownTable(db, tx, "character_quest_lifecycle_receipts", PartyQuestLifecycleSql) ||
            SocialSchema.Tables.Any(row => !KnownTable(db, tx, row.Table, row.Sql)) ||
            !KnownTable(db, tx, "character_dungeon_incount", DungeonIncountSchema.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)) ||
            !KnownTable(db, tx, "account_dungeon_incount", AccountDungeonIncountSchema.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal))) return false;
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN
              ('character_epic_production','quest_eligibility_mail_dispatches','character_pvp_training_skills',
               'account_pvp_private_characters','character_operation_hope_reward_receipts')
            """;
        if (query.ExecuteScalar() is not 0L) return false;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='character_inventory_slots'";
        return query.ExecuteScalar() is string slots &&
            slots.Contains("CHECK(list_type IN (0,1,2,3,7,12,38))", StringComparison.Ordinal);
    }

    static bool IsSocialNumberingPreview(SqliteConnection db, SqliteTransaction tx, long version)
    {
        if (version is < 88 or > 90) return false;
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name IN('character_dungeon_incount','account_dungeon_incount')";
        long counts = (long)query.ExecuteScalar()!;
        if (counts != 0) return false;
        // 完整交互v90使用不同的组队回执合同，不能冒充顺延后的社交v90。
        if (version == 90) throw new InvalidDataException("本地完整交互v90库须使用完整交互版本升级");
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name='character_social_links'";
        if (query.ExecuteScalar() is 0L) return false;
        foreach (var (table, sql) in SocialSchema.Tables)
        {
            query.Parameters.Clear(); query.Parameters.AddWithValue("$table", table);
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            string expected = table == "account_social_requests" && version == 88 ? SocialSchema.LegacyRequestSql : sql;
            if (query.ExecuteScalar() is not string actual || NormalizeTable(actual, table) != NormalizeTable(expected, table))
                throw new InvalidDataException("社交旧编号库存在未知结构：" + table);
        }
        return true;
    }

    static bool IsPlayerInteractionPreview(SqliteConnection db, SqliteTransaction tx, long version)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        string? Table(string name)
        {
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            query.Parameters.Clear(); query.Parameters.AddWithValue("$table", name);
            return query.ExecuteScalar() as string;
        }
        if (Table("character_social_links") is null) return false;
        string? settlement = Table(SettlementProficiencySchema.Settlement);
        // 正式v78已经放宽经验下界；该结构不能冒充旧玩家交互库。
        if (settlement is null || NormalizeTable(settlement, SettlementProficiencySchema.Settlement) !=
            NormalizeTable(SettlementProficiencySchema.SettlementSql, SettlementProficiencySchema.Settlement)) return false;
        foreach (var (table, currentSql) in SocialSchema.Tables.Append(("player_trade_receipts", LegacyPlayerTradeSql)))
        {
            string expected = table == "account_social_requests" && version == 78 ? SocialSchema.LegacyRequestSql : currentSql;
            if (Table(table) is not { } actual || NormalizeTable(actual, table) != NormalizeTable(expected, table))
                throw new InvalidDataException("玩家交互旧库存在未知结构：" + table);
        }
        if (Table("character_quest_daily_clear") is not null || Table("character_expert_job") is not { } expert ||
            expert.Contains("learned_recipes", StringComparison.Ordinal))
            throw new InvalidDataException("玩家交互旧库与正式迁移结构混用");
        const string lifecycle = "character_quest_lifecycle_receipts";
        string expectedLifecycle = version == 80 ? PartyQuestLifecycleSql : QuestLifecycleSchema;
        if (Table(lifecycle) is not { } actualLifecycle || NormalizeTable(actualLifecycle, lifecycle) != NormalizeTable(expectedLifecycle, lifecycle))
            throw new InvalidDataException("玩家交互旧库任务回执结构不匹配");
        return true;
    }

    // 仅用于识别旧预览库并原样保留历史；不创建或写入交易表。
    const string LegacyPlayerTradeSql = """
        CREATE TABLE player_trade_receipts(
          trade_id TEXT NOT NULL PRIMARY KEY CHECK(length(trade_id)=32),
          left_id INTEGER NOT NULL,right_id INTEGER NOT NULL CHECK(left_id<>right_id),
          left_revision INTEGER NOT NULL,right_revision INTEGER NOT NULL,
          created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        ) STRICT;
        """;

    // 旧任务包与主线曾复用版本号；仅精确识别当时完整表组合，不能修补未知混合库。
    static long? Quest704PreviewBaseline(SqliteConnection db, SqliteTransaction tx, long version)
    {
        if (version is < 88 or > 97) return null;
        using var query = db.CreateCommand();
        query.Transaction = tx;
        bool Exists(string name)
        {
            query.Parameters.Clear(); query.Parameters.AddWithValue("$name", name);
            query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name=$name";
            return query.ExecuteScalar() is 1L;
        }
        (string Table, string Sql, int FirstVersion)[] tables =
        [
            ("character_epic_production", EpicProductionSchema, 90),
            ("quest_eligibility_mail_dispatches", QuestEligibilityMailSchema, 91),
            ("character_pvp_training_skills", PvpTrainingSkillsSchema, 92),
            ("account_pvp_private_characters", PvpPrivateCharacterSchema, 93),
            ("character_operation_hope_reward_receipts", OperationHopeRewardSchema.Definition, 94)
        ];
        if (!tables.Any(row => Exists(row.Table))) return null;
        bool social = SocialSchema.Tables.Any(row => Exists(row.Table));
        bool characterIncount = Exists("character_dungeon_incount");
        bool accountIncount = Exists("account_dungeon_incount");
        query.Parameters.Clear();
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='character_inventory_slots'";
        bool talisman = query.ExecuteScalar() is string slots &&
            slots.Contains("CHECK(list_type IN (0,1,2,3,7,12,38,46))", StringComparison.Ordinal);
        // 社交后的任务预览用92–96；正式护石占用92后，任务使用93–97。
        // 同时核对容器约束和完整任务表组合，不能仅凭版本号重放迁移。
        bool currentNumbering = social && talisman && version >= 93;
        bool combinedPreview = social && !talisman && version is >= 92 and <= 96;
        if (social && ((!currentNumbering && !combinedPreview) ||
            SocialSchema.Tables.Any(row => !KnownTable(db, tx, row.Table, row.Sql))))
            throw new InvalidDataException("任务旧编号库混用了社交结构");
        if (!social && (version > 94 || talisman))
            throw new InvalidDataException("任务旧编号库缺少正式社交结构");
        if (version <= 89 ? characterIncount || accountIncount : !characterIncount || !accountIncount)
            throw new InvalidDataException("任务旧编号库混用了副本次数结构");
        if (version >= 90 && (!KnownTable(db, tx, "character_dungeon_incount", DungeonIncountSchema.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)) ||
            !KnownTable(db, tx, "account_dungeon_incount", AccountDungeonIncountSchema.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal))))
            throw new InvalidDataException("任务旧编号库副本次数结构不匹配");
        foreach (var (table, sql, firstVersion) in tables)
        {
            bool expected = currentNumbering ? firstVersion + 3 <= version : combinedPreview ? firstVersion + 2 <= version :
                version <= 89 ? firstVersion <= version + 2 : firstVersion <= version;
            if (expected ? !KnownTable(db, tx, table, sql) : Exists(table))
                throw new InvalidDataException("任务旧编号库存在未知结构：" + table);
        }
        return currentNumbering ? null : combinedPreview ? 91 : version <= 89 ? 87 : 89;
    }

    static bool IsAuraCooldownPreview(SqliteConnection db, SqliteTransaction tx, long version)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='character_teleport_cooldowns'";
        string? actual = query.ExecuteScalar() as string;
        string party = version == 79 ? ",party_aura_until INTEGER NOT NULL DEFAULT 0 CHECK(party_aura_until>=0)" : "";
        string expected = """
            CREATE TABLE character_teleport_cooldowns(
              character_id INTEGER PRIMARY KEY,account TEXT NOT NULL,
              potion_until INTEGER NOT NULL CHECK(potion_until>=0),aura_until INTEGER NOT NULL CHECK(aura_until>=0),
              upgrade_aura_until INTEGER NOT NULL DEFAULT 0 CHECK(upgrade_aura_until>=0)
            """ + party + ",FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE)";
        if (actual is null || NormalizeTable(actual, "character_teleport_cooldowns") != NormalizeTable(expected, "character_teleport_cooldowns"))
            return false;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_quest_daily_clear'";
        if (query.ExecuteScalar() is not 0L) return false;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='character_dungeon_settlement_receipts'";
        return query.ExecuteScalar() is string settlement && NormalizeTable(settlement, SettlementProficiencySchema.Settlement) ==
            NormalizeTable(SettlementProficiencySchema.SettlementSql, SettlementProficiencySchema.Settlement);
    }

    internal static void Execute(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static void ValidateSchema(SqliteConnection db, SqliteTransaction tx)
    {
        // 基线标识不能代替必需列检查；损坏或残缺结构不得进入监听阶段。
        foreach (string sql in RequiredColumns)
        {
            using var query = db.CreateCommand();
            query.Transaction = tx;
            query.CommandText = sql;
            using var reader = query.ExecuteReader();
        }
        DungeonDropCollectionSchema.Validate(db, tx, required: true);
        GriefTowerSchema.Validate(db, tx);
        GriefTowerSchema.ValidateClear(db, tx);
        foreach (var (table, expected) in SocialSchema.Tables)
        {
            using var query = db.CreateCommand(); query.Transaction = tx;
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            query.Parameters.AddWithValue("$table", table);
            if (query.ExecuteScalar() is not string actual || NormalizeTable(actual, table) != NormalizeTable(expected, table))
                throw new InvalidDataException("社交表的约束或外键不是已确认结构: " + table);
        }
        foreach (var (table,expected) in new[] { ("character_quest_completion_receipts",QuestCompletionSchemaV127),
            ("character_boss_selection_buff",BossSelectionBuffSchema.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("character_quest_clear_receipts",QuestClearSchemaV40),
            ("character_quest_lifecycle_receipts",PartyQuestLifecycleSql),
            ("character_quest_daily_clear",QuestDailyClearSchema.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("character_dungeon_scene_clear_receipts",DungeonSceneSettlementSchema.SceneSql),
            ("character_dungeon_settlement_receipts",ZeroExperienceDungeonSchema.SettlementSql),
            ("character_dungeon_loot_receipts",DungeonLootSchema),
            ("character_dungeon_pickup_receipts",DungeonPickupSchemaV45),
            ("character_dungeon_drop_batches",DungeonDropCollectionSchema.Definition.Split(';').Single(sql => sql.Contains("CREATE TABLE character_dungeon_drop_batches(", StringComparison.Ordinal))),
            ("character_dungeon_drop_objects",DungeonDropCollectionSchema.Definition.Split(';').Single(sql => sql.Contains("CREATE TABLE character_dungeon_drop_objects(", StringComparison.Ordinal))),
            ("character_dungeon_drop_pickups",DungeonDropCollectionSchema.PickupDefinition),
            ("character_dungeon_passive_pickups",CharacterRepository.PassiveDropSchema),
            ("character_epic_production",EpicProductionSchema),
            ("character_pvp_training_skills",PvpTrainingSkillsSchema),
            ("account_pvp_private_characters",PvpPrivateCharacterSchema),
            ("character_operation_hope_reward_receipts",OperationHopeRewardSchema.Definition),
            ("character_raid_runs",RaidLifecycleSchema.Definition),
            ("character_raid_reward_receipts",RaidLifecycleSchema.RewardDefinition.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            (TagEvildomConfigurationSchema.Table,TagEvildomConfigurationSchema.Definition),
            (TagEvildomConfigurationSchema.ClearanceTable,TagEvildomConfigurationSchema.ClearanceDefinition),
            (TagEvildomRunSchema.Table,TagEvildomRunSchema.Definition),
            (TagEvildomChallengeSchema.Table,TagEvildomChallengeSchema.Definition),
            ("imperial_arena_daily_routes",ImperialArenaSchema.Routes),
            ("account_imperial_arena_configuration",ImperialArenaSchema.Configuration),
            ("account_imperial_arena_days",ImperialArenaSchema.Days),
            ("account_imperial_arena_runs",ImperialArenaSchema.Runs),
            ("account_imperial_arena_rewards",ImperialArenaSchema.Rewards),
            ("account_imperial_arena_challenge_claims",ImperialArenaSchema.Challenges),
            (OrdinaryLukeProgressSchema.Table,OrdinaryLukeProgressSchema.Definition),
            (OrdinaryLukeDropSchema.Table,OrdinaryLukeDropSchema.Definition),
            (DazzlementTowerSchema.Table,DazzlementTowerSchema.Definition),
            ("marriage_records",MarriageSchema.RecordsSql),
            ("marriage_active_members",MarriageSchema.ActiveMembersSql),
            (PvpArenaResultSchema.Table,PvpArenaResultSchema.Definition),
            (PvpRatingSchema.StateTable,PvpRatingSchema.StateDefinition),
            (PvpRatingSchema.ClaimTable,PvpRatingSchema.ClaimDefinition),
            (PvpTotalTeamSchema.Table,PvpTotalTeamSchema.Definition),
            (PvpTotalRatingSchema.StateTable,PvpTotalRatingSchema.StateDefinition),
            (PvpTotalRatingSchema.ClaimTable,PvpTotalRatingSchema.ClaimDefinition),
            (FatigueDailyResetSchema.Table,FatigueDailyResetSchema.Definition),
            (ServerItemUsageSchema.StateTable,ServerItemUsageSchema.StateDefinition),
            (ServerItemUsageSchema.AccountTable,ServerItemUsageSchema.AccountDefinition),
            ("marriage_records",MarriageSchema.RecordsSql),
            ("marriage_active_members",MarriageSchema.ActiveMembersSql),
            ("quest_eligibility_mail_dispatches",QuestEligibilityMailSchema),
            ("character_collectbox_state",CollectBoxStateTableSchema),
            ("character_collectbox_slots",CollectBoxSlotsTableSchema),
            ("character_collectbox_expirations",CollectBoxExpirationSchema),
            ("character_quest_stage_witnesses",QuestStageSchema.WitnessSql),
            ("character_quest_stage_claims",QuestStageSchema.ClaimSql),
            ("character_skill_switch_inventory",SkillSwitchInventoryStateSchema),
            ("character_permanent_stats",PermanentStatSchema.Sql),
            ("character_skill_switch_inventory_items",SkillSwitchInventoryItemsSchema),
            (EvildomWarRewardSchema.Table,EvildomWarRewardSchema.Definition.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("evildom_war_battles",EvildomWarRecoverySchema.Battles.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("character_evildom_war_seats",EvildomWarRecoverySchema.Seats.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("account_evildom_war_guides",EvildomWarGuideSchema.Table.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            (EvildomWarHiddenRewardSchema.Table,EvildomWarHiddenRewardSchema.Definition.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("guild_dungeon_progress",GuildDungeonProgressSchema.Definition.Split(';')[0].Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("guild_dungeon_clear_receipts",GuildDungeonProgressSchema.Definition.Split(';')[1].Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("character_guild_hongbao_purchases",GuildHongbaoPurchaseSchema.Definition.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            (DungeonIncountExtraSchema.Table,DungeonIncountExtraSchema.Definition.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("guild_hongbao_records",GuildHongbaoSchema.Records.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("guild_hongbao_claims",GuildHongbaoSchema.Claims.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("guild_ranking_party_clears",GuildRankingSchema.Parties.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("guild_ranking_months",GuildRankingSchema.Archives.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("guild_dungeon_reward_receipts",GuildDungeonRewardSchema.Table.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("guild_dungeon_effective_progress",GuildDungeonEffectiveSchema.Definition.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
            ("guild_recommended_channels",GuildRecommendedChannelSchema.Sql.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)) })
        {
            using var query = db.CreateCommand(); query.Transaction = tx;
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            query.Parameters.AddWithValue("$table",table);
            if (query.ExecuteScalar() is not string actual || NormalizeTable(actual,table) != NormalizeTable(DungeonObjectKeySchema.Definition(expected, table),table))
                throw new InvalidDataException("关键表的约束或外键不是已确认结构: " + table);
        }
        PvpRatingSchema.ValidateIndex(db, tx);
        PvpTotalRatingSchema.ValidateIndex(db, tx);
        using var constraints = db.CreateCommand();
        constraints.Transaction = tx;
        constraints.CommandText = """
            SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND name IN
              ('title_book_exclusive_insert','title_book_exclusive_update','inventory_not_title_insert',
               'inventory_not_title_update','mail_not_title_insert','mail_not_title_update');
            """;
        if (Convert.ToInt64(constraints.ExecuteScalar()) != 6)
            throw new InvalidDataException("称号簿与背包、邮件的实例互斥约束缺失");
    }

    private static readonly string[] RequiredColumns =
    [
        "SELECT guild_id,region,channel FROM guild_recommended_channels LIMIT 0",
        "SELECT owner_id,target_id,kind,created,memo FROM character_social_links LIMIT 0",
        "SELECT owner_account,target_account,created,memo,group_id FROM account_social_links LIMIT 0",
        "SELECT source_account,target_account,message,created,memo,group_id FROM account_social_requests LIMIT 0",
        "SELECT character_id,account,source_acceptance_id,source_quest_id,source_revision,target_level,rule_version,status,proof FROM character_quest_stage_witnesses LIMIT 0",
        "SELECT character_id,account,source_acceptance_id,trigger_acceptance_id,target_level,before_experience,after_experience,amount,revision FROM character_quest_stage_claims LIMIT 0",
        "SELECT character_id,account,ground_key,instance_id,task_attempt,epoch,picked_core,gold_amount,destination_slot,item_uid,after_core,currency_kind,amount,after_balance,account_revision,revision FROM character_dungeon_passive_pickups LIMIT 0",
        // 拍卖行挂牌表由 v37 建出（`IF NOT EXISTS`），列是否齐全在这里兜底：
        // 缺列或缺表都会在建库/升级阶段直接拒绝启动，而不是等到运行时读挂牌才发现。
        "SELECT listing_id,seller_character_id,seller_name,item_template_id,item_kind,item_count,item_entry,starting_price,buyout_price,current_bid,current_bidder_character_id,state,posted_at_unix,expires_at_unix,settled_at_unix,deposit_gold FROM auction_listings LIMIT 0",
        // 公会两表由 v44 建出（`IF NOT EXISTS`），同样缺表或缺列直接拒绝启动。
        "SELECT guild_id,name,address,master_character_id,tier,level,experience,public_funds,guild_crystal,notice,memo,created_at_unix FROM guilds LIMIT 0",
        "SELECT guild_id,character_id,grade,contribution,member_memo,joined_at_unix,last_login_unix FROM guild_members LIMIT 0",
        "SELECT guild_id,revision,promotion,tags,join_mode,webpage FROM guild_settings LIMIT 0",
        "SELECT guild_id,revision,capacity FROM guild_cargo_state LIMIT 0",
        "SELECT guild_id,slot_index,item_uid,account,owner_character_id FROM guild_cargo_slots LIMIT 0",
        "SELECT id,guild_id,created_at_unix,kind,name,item_core,moved_count FROM guild_cargo_history LIMIT 0",
        "SELECT request_id,fingerprint,account,character_id,guild_id,revision,destination_slot,created_at_unix FROM guild_cargo_receipts LIMIT 0",
        "SELECT request_id,kind,account,character_id FROM guild_request_claims LIMIT 0",
        "SELECT character_id,event_id,bonuses FROM guild_experience_receipts LIMIT 0",
        "SELECT event_id,character_id,guild_id,joined_at_unix,source,business_day_unix,week_start_unix,points,created_at_unix FROM guild_contribution_events LIMIT 0",
        "SELECT guild_id,character_id,joined_at_unix,week_start_unix,points,coins,settled_at_unix FROM guild_contribution_settlements LIMIT 0",
        "SELECT guild_id,npc_id,level,upgrade_target,upgrade_until,contract_until FROM guild_npc_progress LIMIT 0",
        "SELECT guild_id,npc_id,benefit_id,public_funds,reward_count,deadline,craft_id FROM guild_npc_crafts LIMIT 0",
        "SELECT request_id,account,character_id,guild_id,joined_at,action,benefit_id,craft_id,deadline FROM guild_npc_craft_receipts LIMIT 0",
        "SELECT craft_id,guild_id,benefit_id,reward_count,before_crystal,after_crystal,completed_at FROM guild_npc_craft_completions LIMIT 0",
        "SELECT request_id,account,character_id,guild_id,joined_at,npc_id,action,argument,deadline FROM guild_npc_receipts LIMIT 0",
        "SELECT request_id,account,character_id,guild_id,joined_at_unix,source_uid,source_slot,item_id,amount,before_experience,after_experience,before_level,after_level,created_at_unix FROM guild_growth_item_receipts LIMIT 0",
        "SELECT guild_id,member_limit FROM guild_economy LIMIT 0",
        "SELECT guild_id,type,value,expires_unix FROM guild_benefits LIMIT 0",
        "SELECT request_id,operation,content_id,source_uid,source_slot,amount FROM guild_economy_receipts LIMIT 0",
        "SELECT id,guild_id,character_id,character_name,kind,content_id,amount,created_at_unix FROM guild_mileage_history LIMIT 0",
        "SELECT event_id,character_id,guild_id,amount FROM guild_economy_events LIMIT 0",
        "SELECT guild_id,mark,aura_mark,aura_frame FROM guild_appearance LIMIT 0",
        "SELECT guild_id,character_id,joined_at_unix,business_day_unix,observed_at_unix FROM guild_attendance LIMIT 0",
        "SELECT request_id,account,character_id,guild_id,joined_at_unix,participants,destination FROM guild_teleport_receipts LIMIT 0",
        "SELECT guild_id,grade,permissions,name FROM guild_roles LIMIT 0",
        "SELECT character_id,until_unix FROM guild_membership_cooldowns LIMIT 0",
        "SELECT guild_id,character_id,message,region,created_at_unix FROM guild_applications LIMIT 0",
        "SELECT character_id,operation_id,before_revision,revision,claimed_quests,changed_items FROM character_quest_effect_receipts LIMIT 0",
        "SELECT character_id,quest_id,operation_id FROM character_quest_special_reward_claims LIMIT 0",
        "SELECT account,item_id,count FROM account_adventure_purchases LIMIT 0",
        "SELECT account,area,run_id,started,ends,reward,extra_reward,attributes FROM account_adventure_expeditions LIMIT 0",
        "SELECT account,area,ordinal,character_id,member_snapshot FROM account_adventure_expedition_members LIMIT 0",
        "SELECT account,event_id,character_id,action,amount,before_revision,revision FROM account_adventure_receipts LIMIT 0",
        "SELECT account,job,grow,character_id FROM account_adventure_representatives LIMIT 0",
        "SELECT account,slots FROM account_adventure_weekly_config LIMIT 0",
        "SELECT account,revision,name,created_date,level,experience,remainder,capsule_experience,brave_experience,points,month,name_initialized,capsule_week,login_streak_days,last_login_day,opening_experience,excluded_experience FROM account_adventures LIMIT 0",
        "SELECT character_id,account,baseline_experience,position_experience FROM character_adventure_credits LIMIT 0",
        "SELECT key,value FROM adventure_ledger_meta LIMIT 0",
        "SELECT emotion FROM characters LIMIT 0",
        "SELECT clone_title_item_id FROM characters LIMIT 0",
        "SELECT selected_skill_tree FROM characters LIMIT 0",
        "SELECT blacklist_count FROM characters LIMIT 0",
        "SELECT charac_no,selected,cube_type FROM cube_premium LIMIT 0",
        "SELECT account,profile,hotkeys FROM account_hotkey_settings LIMIT 0",
        "SELECT account,bank,quick_chat FROM account_quick_chat LIMIT 0",
        "SELECT character_id,account,dungeon_id,completed,reward_received FROM character_tutorial_state LIMIT 0",
        "SELECT character_id,account,event_id,kind,instance_id,task_attempt,epoch,map_id,subject,experience,before_revision,revision,plan_snapshot FROM character_tutorial_receipts LIMIT 0",
        "SELECT character_id,account,list_type,slot_index,item_uid FROM character_title_book_slots LIMIT 0",
        "SELECT character_id,account,quest_id,remaining1,remaining2,remaining3,completed_revision FROM character_title_progress LIMIT 0",
        "SELECT character_id,account,section_key,revision FROM character_title_sections LIMIT 0",
        "SELECT character_id,account,selected_box FROM character_collectbox_state LIMIT 0",
        "SELECT character_id,account,box_index,slot_index,item_id FROM character_collectbox_slots LIMIT 0",
        "SELECT character_id,account,box_index,expires_at FROM character_collectbox_expirations LIMIT 0",
        "SELECT character_id,account,skill_id FROM character_skill_switch_inventory LIMIT 0",
        "SELECT character_id,equipment_part,item_uid,item_id FROM character_skill_switch_inventory_items LIMIT 0",
        "SELECT account,black_diamond_expires,gold_card_expires,revision FROM account_card_memberships LIMIT 0",
        "SELECT character_id,account,service_day,used FROM character_gold_card_uses LIMIT 0",
        "SELECT character_id,account,instance_id,options_json,black_diamond_reward_json,bonus_mail_id,paid_reward_json,paid_mail_id,paid_cost,paid_revision,paid_day FROM character_dungeon_card_options LIMIT 0",
        "SELECT character_id,account,instance_id,plan_json,reward_json,mail_id,revision,claimed FROM character_dungeon_card_rewards LIMIT 0",
        "SELECT event_id,revision,starts_at,ends_at,override_active FROM server_events LIMIT 0",
        "SELECT character_id,account,acceptance_id,parent_key,question_quest,before_revision,revision,plan_snapshot,operation_id,operation_kind FROM character_quest_skip_receipts LIMIT 0",
        "SELECT character_id,account,event_id,before_revision,revision,plan_snapshot,changed_items FROM character_quest_drop_receipts LIMIT 0",
        "SELECT account,quest_id,character_id,acceptance_id,daily_period,answer FROM account_completed_quests LIMIT 0",
        "SELECT character_id,account,operation_id,action,quest_id,acceptance_id,before_revision,revision,before_active,after_active,changed_items,scene_event,before_completed,after_completed FROM character_quest_lifecycle_receipts LIMIT 0",
        "SELECT character_id,account,operation_id,source_type,instance_id,quest_id,task_attempt,before_epoch,epoch,dungeon_id,map_id,template_id,source_ordinal,fingerprint,death_events,generation,revision FROM character_dungeon_scene_clear_receipts LIMIT 0",
        "SELECT character_id,acceptance_id,event_id,source_type,growth_event_id,scene_operation_id FROM character_quest_clear_receipts LIMIT 0",
        "SELECT character_id,bonus_tp FROM character_tp_sources LIMIT 0",
        "SELECT character_id,account,max_hp,max_mp,strength,vitality,intelligence,spirit,move_speed,all_element_resistance FROM character_permanent_stats LIMIT 0",
        "SELECT character_id,context,skill_id,keys FROM character_skill_commands LIMIT 0",
        "SELECT character_id,context,root_id,children FROM character_skill_combos LIMIT 0",
        "SELECT character_id,bonus_sp FROM character_skill_point_sources LIMIT 0",
        "SELECT account,expires_at FROM account_expert_contracts LIMIT 0",
        "SELECT [key],[value] FROM [schema_metadata] LIMIT 0",
        "SELECT character_id,account,potion_until,aura_until,upgrade_aura_until,party_aura_until FROM character_teleport_cooldowns LIMIT 0",
        "SELECT character_id,account,until_unix,permille FROM character_exp_bonus LIMIT 0",
        "SELECT character_id,account,kind,item_id,until_ms,maintenance,cool_group FROM character_consumable_states LIMIT 0",
        "SELECT character_id,account,state_json FROM character_boss_selection_buff LIMIT 0",
        "SELECT character_id,account,job_type,expertness,learned_recipes FROM character_expert_job LIMIT 0",
        "SELECT character_id,account,giveup_state,machine_grade,machine_endurance FROM character_expert_meta LIMIT 0",
        "SELECT character_id,account,day,clear_count,hidden_count FROM character_assault_daily LIMIT 0",
        "SELECT character_id,account,day,clear_count,daily_reward FROM character_crack_daily LIMIT 0",
        "SELECT character_id,account,change_count FROM character_growup_change LIMIT 0",
        "SELECT character_id,account,dungeon,day,daily_used,week,weekly_used FROM character_dungeon_incount LIMIT 0",
        "SELECT account,dungeon,period,used FROM account_dungeon_incount LIMIT 0",
        "SELECT character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,dungeon_id,map_id,death_events,report_body,generation,base_experience,rank_experience,total_experience,rank_bonus_index,rule_id,resource_source,before_level,after_level,before_experience,after_experience,revision,potion_experience,adventure_experience,other_bonus_experience,bonus_json,proficiency_gain FROM character_dungeon_settlement_receipts LIMIT 0",
        "SELECT character_id,account,event_id,instance_id,task_attempt,epoch,map_id,monster_key,rule_id,resource_source,ground_key,core,x,y,record_slot FROM character_dungeon_loot_receipts LIMIT 0",
        "SELECT character_id,account,event_id,ground_key,instance_id,task_attempt,epoch,destination_list,destination_slot,item_uid,after_core,revision FROM character_dungeon_pickup_receipts LIMIT 0",
        "SELECT character_id,account,next_object_key,last_attempt,capsule_experience,proficiency_level,proficiency_experience,recommended_dungeon_clears FROM character_dungeon_state LIMIT 0",
        "SELECT character_id,account,instance_id,task_attempt,service_day,kind,dungeon_id,layer,gold,revision FROM dungeon_special_entry_receipts LIMIT 0",
        "SELECT character_id,account,instance_id,event_id,task_attempt,epoch,dungeon_id,completed_rounds,maximum_rounds,play_milliseconds,cleared,death_events,reward_json,gained_experience,proficiency_gained FROM dungeon_altar_clear_receipts LIMIT 0",
        "SELECT character_id,account,next_layer,last_clear_day,clear_count FROM dungeon_despair_progress LIMIT 0",
        "SELECT character_id,account,instance_id,event_id,task_attempt,epoch,dungeon_id,clear_day,play_milliseconds,death_events,assets_json FROM dungeon_despair_clear_receipts LIMIT 0",
        "SELECT character_id,account,used,maximum,used_maximum,remaining,context_auxiliary,selection_auxiliary FROM character_fatigue LIMIT 0",
        "SELECT character_id,account,instance_id,task_attempt,map_id,epoch,generation,rule_id,before_revision,revision,before_state,after_state FROM character_dungeon_fatigue_receipts LIMIT 0",
        "SELECT character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision,proficiency_gain FROM character_dungeon_growth_receipts LIMIT 0",
        "SELECT [account_id],[salt],[password_hash],[iterations],[disabled],[revision] FROM [accounts] LIMIT 0",
        "SELECT [account],[game_options] FROM [account_client_settings] LIMIT 0",
        "SELECT [character_id],[account],[character_options] FROM [character_client_settings] LIMIT 0",
        "SELECT [character_id],[account],[game_options] FROM [character_game_settings] LIMIT 0",
        "SELECT [id],[account],[slot],[name],[revision],[town],[area],[x],[y],[direction],[return_town],[return_area],[return_x],[return_y],[return_direction],[job],[grow_type],[level],[accepted_story_level],[gold],[experience],[inventory_expansion],[revive_coins],[win_points],[gold_carry_limit],[auction_gold_limit],[cargo_capacity],[quest_cycle_day],[equipment_slot_flags] FROM [characters] LIMIT 0",
        "SELECT [character_id],[quest_id],[daily_period],[answer] FROM [character_completed_quests] LIMIT 0",
        "SELECT [character_id],[skill_id],[level] FROM [character_skills] LIMIT 0",
        "SELECT [character_id],[slot],[quest_id],[condition_state],[acceptance_id] FROM [character_active_main_quests] LIMIT 0",
        "SELECT character_id,account,acceptance_id,quest_id,quest_slot,event_id,instance_id,task_attempt,epoch,dungeon_id,map_id,generation,revision FROM character_quest_clear_receipts LIMIT 0",
        "SELECT character_id,account,acceptance_id,quest_id,event_id,instance_id,task_attempt,epoch,generation,rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,difficulty_key,difficulty_weight,experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,before_revision,revision,rewards,clear_acceptance_id,condition_type,selector,multiplier,consumed,changed_items,plan_snapshot FROM character_quest_completion_receipts LIMIT 0",
        "SELECT [character_id],[context],[sp],[tp] FROM [character_skill_points] LIMIT 0",
        "SELECT [character_id],[context],[slot],[skill_id],[level] FROM [character_skill_bindings] LIMIT 0",
        "SELECT [audit_id],[created_at],[account],[character_id],[generation],[action_name],[list_type],[slot_index],[item_uid_before],[item_uid_after],[item_id_before],[item_id_after],[value_before],[value_after],[item_kind_before],[item_kind_after],[count_before],[count_after],[count_delta],[core_hash_before],[core_hash_after] FROM [inventory_audit_log] LIMIT 0",
        "SELECT [account],[revision],[rental_points],[mileage],[seria_luck],[premium_contracts],[character_slots] FROM [account_wallets] LIMIT 0",
        "SELECT charac_no,account,weapon,cloth,leather,light_armor,heavy_armor,plate,amulet,wrist,ring,support,magic_stone FROM item_making_skill_info LIMIT 0",
        "SELECT avatar_expansion FROM characters LIMIT 0",
        "SELECT devil_usage,stamina_until FROM characters LIMIT 0",
        "SELECT daily_booster_usage FROM characters LIMIT 0",
        "SELECT weapon_skin_state FROM characters LIMIT 0",
        "SELECT avatar_preset_state FROM characters LIMIT 0",
        "SELECT [account],[currency_kind],[balance] FROM [account_currency_balances] LIMIT 0",
        "SELECT [audit_id],[created_at],[account],[character_id],[generation],[action_name],[currency_kind],[account_scope],[balance_before],[balance_after] FROM [currency_audit_log] LIMIT 0",
        "SELECT [account],[revision],[capacity],[gold] FROM [account_cargo] LIMIT 0",
        "SELECT [item_uid],[character_id],[account],[owner_character_id],[item_core],[created_at],[updated_at] FROM [character_inventory_items] LIMIT 0",
        "SELECT [audit_id],[created_at],[account],[character_id],[generation],[action_name],[personal_capacity_before],[personal_capacity_after],[account_capacity_before],[account_capacity_after],[account_gold_before],[account_gold_after] FROM [cargo_audit_log] LIMIT 0",
        "SELECT [account],[character_id],[npc_id],[item_id],[reset_daily],[day],[count] FROM [shop_purchase_counts] LIMIT 0",
        "SELECT [account],[revision],[order_hash],[counts] FROM [account_epic_books] LIMIT 0",
        "SELECT [audit_id],[created_at],[account],[character_id],[generation],[action_name],[order_hash],[piece_index],[count_before],[count_after] FROM [epic_piece_audit_log] LIMIT 0",
        "SELECT [item_uid],[detail] FROM [inventory_avatar_details] LIMIT 0",
        "SELECT [item_uid],[detail] FROM [inventory_creature_details] LIMIT 0",
        "SELECT [character_id],[account],[list_type],[owner_character_id],[slot_index],[item_uid] FROM [character_inventory_slots] LIMIT 0",
        "SELECT [mail_id],[revision],[recipient_id],[recipient_account],[sender_id],[sender_name],[body],[gold],[created_at],[expires_at],[state],[system_mail],[delivery_key],[delivery_content] FROM [mailbox_messages] LIMIT 0",
        "SELECT [attachment_id],[mail_id],[ordinal],[item_uid],[source_uid],[source_list],[source_slot],[claimed] FROM [mailbox_attachments] LIMIT 0",
        "SELECT [character_id],[day],[amount] FROM [mailbox_sent_gold] LIMIT 0",
        "SELECT [audit_id],[mail_id],[account],[character_id],[generation],[action_name],[created_at],[assets] FROM [mailbox_audit_log] LIMIT 0",
    ];

    internal const string CollectBoxStateTableSchema = """
        CREATE TABLE character_collectbox_state(
            character_id INTEGER PRIMARY KEY,
            account TEXT NOT NULL,
            selected_box INTEGER NOT NULL CHECK(selected_box BETWEEN 0 AND 255),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal const string CollectBoxSlotsTableSchema = """
        CREATE TABLE character_collectbox_slots(
            character_id INTEGER NOT NULL,
            account TEXT NOT NULL,
            box_index INTEGER NOT NULL CHECK(box_index BETWEEN 1 AND 255),
            slot_index INTEGER NOT NULL CHECK(slot_index BETWEEN 0 AND 65535),
            item_id INTEGER NOT NULL CHECK(item_id BETWEEN 1 AND 2147483647),
            PRIMARY KEY(character_id,box_index,slot_index),
            UNIQUE(character_id,box_index,item_id),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal const string CollectBoxStateSchema = CollectBoxStateTableSchema + CollectBoxSlotsTableSchema;

    internal const string CollectBoxExpirationSchema = """
        CREATE TABLE character_collectbox_expirations(
            character_id INTEGER NOT NULL,
            account TEXT NOT NULL,
            box_index INTEGER NOT NULL CHECK(box_index BETWEEN 1 AND 255),
            expires_at INTEGER NOT NULL CHECK(expires_at>0),
            PRIMARY KEY(character_id,box_index),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal const string SkillSwitchInventoryStateSchema = """
        CREATE TABLE character_skill_switch_inventory(
            character_id INTEGER PRIMARY KEY,
            account TEXT NOT NULL,
            skill_id INTEGER NOT NULL CHECK(skill_id BETWEEN 0 AND 65534),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal const string SkillSwitchInventoryItemsSchema = """
        CREATE TABLE character_skill_switch_inventory_items(
            character_id INTEGER NOT NULL,
            equipment_part INTEGER NOT NULL CHECK(equipment_part BETWEEN 0 AND 35),
            item_uid INTEGER NOT NULL CHECK(item_uid>0),
            item_id INTEGER NOT NULL CHECK(item_id>0),
            PRIMARY KEY(character_id,equipment_part),
            UNIQUE(character_id,item_uid),
            FOREIGN KEY(character_id) REFERENCES character_skill_switch_inventory(character_id) ON DELETE CASCADE,
            FOREIGN KEY(item_uid) REFERENCES character_inventory_items(item_uid) ON DELETE CASCADE
        );
        """;

    internal const string SkillSwitchInventorySchema =
        SkillSwitchInventoryStateSchema + SkillSwitchInventoryItemsSchema;

    // 设置按账号与角色分别保存原始字节；缺省不建行，不改角色资产与 revision。
    internal const string ClientSettingsSchema = """
        CREATE TABLE account_client_settings(
            account TEXT NOT NULL PRIMARY KEY REFERENCES accounts(account_id),
            game_options BLOB NOT NULL CHECK(typeof(game_options)='blob' AND length(game_options)=204)
        );
        CREATE TABLE character_client_settings(
            character_id INTEGER PRIMARY KEY,
            account TEXT NOT NULL,
            character_options BLOB NOT NULL CHECK(typeof(character_options)='blob' AND length(character_options)=512),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    // 快捷聊天按银行存客户端原始300B；缺省不建行，两个银行互不影响。
    internal const string QuickChatSchema = """
        CREATE TABLE IF NOT EXISTS account_quick_chat(
            account TEXT NOT NULL REFERENCES accounts(account_id),
            bank INTEGER NOT NULL CHECK(bank IN (0,1)),
            quick_chat BLOB NOT NULL CHECK(typeof(quick_chat)='blob' AND length(quick_chat)=300),
            PRIMARY KEY(account,bank)
        );
        """;

    internal const string CharacterGameSettingsSchema = """
        CREATE TABLE character_game_settings(
            character_id INTEGER PRIMARY KEY,
            account TEXT NOT NULL,
            game_options BLOB NOT NULL CHECK(typeof(game_options)='blob' AND length(game_options)=204),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    // 邮箱从 v4 追加；新库完整 SQL 同时维护，升级逐条 DDL 参与原事务。
    internal const string MailboxSchema = """
        CREATE TABLE mailbox_messages(
            mail_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(mail_id BETWEEN 1 AND 1073741823),
            revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0),
            recipient_id INTEGER NOT NULL,
            recipient_account TEXT NOT NULL,
            sender_id INTEGER NOT NULL CHECK(sender_id BETWEEN 0 AND 65535),
            sender_name TEXT NOT NULL,
            body TEXT NOT NULL,
            gold INTEGER NOT NULL CHECK(gold BETWEEN 0 AND 2147483647),
            created_at INTEGER NOT NULL CHECK(created_at>=0),
            expires_at INTEGER,
            state INTEGER NOT NULL DEFAULT 1 CHECK(state BETWEEN 0 AND 3),
            system_mail INTEGER NOT NULL CHECK(system_mail IN (0,1)),
            delivery_key TEXT UNIQUE,
            delivery_content TEXT,
            FOREIGN KEY(recipient_id,recipient_account) REFERENCES characters(id,account),
            CHECK(expires_at IS NULL OR expires_at>created_at),
            CHECK((system_mail=0 AND delivery_key IS NULL AND delivery_content IS NULL) OR
                  (system_mail=1 AND delivery_key IS NOT NULL AND delivery_content IS NOT NULL))
        );
        CREATE INDEX mailbox_inbox ON mailbox_messages(recipient_id,state,created_at,mail_id);
        CREATE TABLE mailbox_attachments(
            attachment_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(attachment_id BETWEEN 1 AND 1073741823),
            mail_id INTEGER NOT NULL REFERENCES mailbox_messages(mail_id),
            ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 9),
            item_uid INTEGER UNIQUE REFERENCES character_inventory_items(item_uid),
            source_uid INTEGER NOT NULL CHECK(source_uid>=0),
            source_list INTEGER NOT NULL,
            source_slot INTEGER NOT NULL,
            claimed INTEGER NOT NULL DEFAULT 0 CHECK(claimed IN (0,1)),
            UNIQUE(mail_id,ordinal),
            CHECK((claimed=0 AND item_uid IS NOT NULL) OR (claimed=1 AND item_uid IS NULL))
        );
        CREATE TABLE mailbox_sent_gold(
            character_id INTEGER PRIMARY KEY REFERENCES characters(id),
            day INTEGER NOT NULL,
            amount INTEGER NOT NULL CHECK(amount BETWEEN 0 AND 9223372036854775807)
        );
        CREATE TABLE mailbox_audit_log(
            audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
            mail_id INTEGER NOT NULL REFERENCES mailbox_messages(mail_id),
            account TEXT NOT NULL,
            character_id INTEGER NOT NULL,
            generation INTEGER NOT NULL,
            action_name TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            assets TEXT NOT NULL CHECK(json_valid(assets))
        );
        """;
}
