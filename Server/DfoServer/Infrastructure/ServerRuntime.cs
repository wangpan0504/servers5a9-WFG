using DfoServer.Development.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using DfoServer.Game.Caching;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Development;
using DfoServer.Game.Auth;
using DfoServer.Network;
using DfoServer.Network.Management;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.GameWorld.Dungeons;
using PvfLib;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.Game.Rental;
using DfoServer.Game.Settings;
using DfoServer.Game.Dungeons;
using System.Diagnostics;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Infrastructure;
// 唯一装配入口：按领域增加模块及依赖，不在协议循环中维护业务分支。
internal static class ServerRuntime
{
    internal static ChannelCatalog LoadChannels(string path)
    {
        // 单文件发行包只携带内置脚本；目录部署仍优先使用外置配置。
        using var stream = File.Exists(path) ? File.OpenRead(path) :
            typeof(ServerRuntime).Assembly.GetManifestResourceStream("DfoServer.Resources.channel.txt")
            ?? throw new InvalidDataException("程序缺少内置频道脚本");
        using var reader = new StreamReader(stream);
        return ChannelCatalog.Parse(reader.ReadToEnd());
    }

    public static ServerHost Create(ServerOptions options, PvfArchive pvf, CharacterRepository repository, TownCatalog towns, CharacterCatalog characters,
        SkillLearningCatalog learning, ItemResources itemResources, ServerLog log, QuestCatalog quests,
        TutorialCatalog? tutorials = null, TutorialScenes? tutorialScenes = null,
        GameWorld.Adventure.AdventureCatalog? adventureCatalog = null,
        GameWorld.Adventure.AdventureExpeditionCatalog? expeditionCatalog = null, TimeProvider? clock = null,
        CharacterGrowthCatalog? growthCatalog = null)
    {
        quests = quests.WithCreatures(itemResources.Creatures);
        var questStages = QuestStageGraph.Build(quests);
        itemResources.Creatures.PrepareEvolutions();
        var channels = LoadChannels(Path.Combine(AppContext.BaseDirectory, "Resources", "channel.txt"));
        var gameChannels = new GameChannelListeners(options, channels);
        log.Write("startup", new { Stage = "Runtime assembly", State = "starting" });
        if (!itemResources.ItemMakingSkills.Available)
            log.Write("resource_warning", new { Stage = "Item making skills", Missing = "etc/itemmakingskill.etc", Disabled = "带制作熟练度声明的配方" });
        var timer = Stopwatch.StartNew();
        var attributePairs = IndependentAttributePairCatalog.Load(
            Path.Combine(AppContext.BaseDirectory, "Resources", "independent-attribute-pairs.json"),
            itemResources.Items, itemResources.Boosters, itemResources.LimitCubes, itemResources.Enchantments,
            itemResources.EpicPieces, itemResources.Contracts, id => Game.Currency.CurrencyWallet.RewardKind(id) is not null);
        log.Write("startup", new { Stage = "Independent attribute pairs", State = "complete",
            attributePairs.Revision, attributePairs.ValidatedFamilies, Reason = attributePairs.UnavailableReason });
        // 支持模板全集在监听就绪前准备，首位玩家选角不承担STK扫描。
        var boosterTimer = Stopwatch.StartNew();
        int dailyBoosterTemplates = itemResources.Boosters.DailyRetainedTemplateIds.Count;
        log.Write("startup", new { Stage = "Daily retained boosters", State = "complete",
            SupportedTemplates = dailyBoosterTemplates, ScannedTemplates = itemResources.Boosters.DailyRetainedScannedTemplates,
            ElapsedMs = Math.Round(boosterTimer.Elapsed.TotalMilliseconds, 3) });
        var gameCache = new GameCache();
        var owners = new AccountOwners(gameCache);
        var online = new OnlineSessionDirectory(gameCache);
        // GM notice 的公告出口：与 GM 聊天帧注入共用在线连接目录，帧内编码由 ServerNoticePackets 负责。
        var noticeSender = new GmNoticeSender(online);
        var accounts = new AuthenticationService(new AuthenticationRepository(Path.Combine(options.DataDirectory, "game.db")));
        accounts.EnsureDevelopmentAccount();
        var debugSettings = new GameDebugSettings();
        var expertStores = new ExpertJobStoreBoard();
        var presence = new TownPresence(towns, channels.GameIdentity, clock, debugSettings, expertStores);
        var expertJobs = itemResources.ExpertJobs;
        string expertPolicyPath = Path.Combine(options.DataDirectory, "expert-job-compatibility.json");
        var expertPolicy = File.Exists(expertPolicyPath)
            ? System.Text.Json.JsonSerializer.Deserialize<Game.ExpertJob.ExpertJobCompatibility>(File.ReadAllText(expertPolicyPath),
                new System.Text.Json.JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidDataException("副职业兼容配置不能为空")
            : new Game.ExpertJob.ExpertJobCompatibility();
        expertPolicy.Validate();
        var machineDisassembly = new Game.ExpertJob.ExpertJobDisassembly(
            new InventoryDisjointService(itemResources.Items, itemResources.Disjoint, itemResources.GoldLimits, itemResources.EpicPieces, expertJobs: expertJobs),
            expertJobs, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, expertPolicy, clock);

        var enchantmentService = new InventoryEnchantmentService(itemResources.Items, itemResources.Enchantments, clock);
        var mailbox = new Game.Mailbox.MailboxService(repository, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
        var mailboxProjection = new MailboxProjection(mailbox, online);
        var settings = new ClientSettingsService(new ClientSettingsRepository(Path.Combine(options.DataDirectory, "game.db")),
            CharacterEmotionCatalog.Load(pvf));
        var questParameters = QuestRewardParameterCatalog.Load(pvf);
        var questConnections = QuestConnectionCatalog.Load(pvf);
        var imageCommunication = ImageCommunicationCatalog.Load(pvf, towns);
        var titleCatalog = new TitleCatalog(pvf, quests);
        // 公会参数（建会花费、仓库格数、等级经验曲线）：数值只从 PVF 读，业务层不写死常数。
        var guildParameters = new GameWorld.Guilds.GuildParameterCatalog(pvf);
        var titleService = new Game.Titles.TitleAchievementService(titleCatalog, quests, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
        var titleProjection = new TitleProjection(titleService);
        adventureCatalog ??= new GameWorld.Adventure.AdventureCatalog(pvf);
        var adventureProjection = new AdventureProjection(adventureCatalog);
        var adventureWeeklyProjection = new AdventureWeeklyProjection(options.AdventureWeekly, log);
        var adventureRepresentatives = new Game.Adventure.AdventureRepresentativeService(adventureCatalog);
        expeditionCatalog ??= new GameWorld.Adventure.AdventureExpeditionCatalog(pvf);
        var expeditionService = new Game.Adventure.AdventureExpeditionService(adventureCatalog, expeditionCatalog);
        var expeditionProjection = new AdventureExpeditionProjection(expeditionService, clock);
        var growth = growthCatalog ?? CharacterGrowthCatalog.Load(pvf, learning);
        var experienceBonuses = new Game.Characters.ExperienceBonusService(itemResources.Items, itemResources.RecoveryConsumables,
            GameWorld.Characters.ExperienceBonusCatalog.Load(pvf));
        tutorials ??= new TutorialCatalog(pvf);
        var questProjection = new QuestProjection(quests,questParameters,itemResources.Items,itemResources.GoldLimits,itemResources.EpicPieces,growth);
        var stats = new Game.Characters.CharacterStatComputer(characters);
        var skillSwitchInventory = new Game.Skills.SkillSwitchInventoryService(
            itemResources.Items,
            SwitchingSupportSkillCatalog.Load(pvf),
            clock);
        // 建会认证申请与入城补投：入城序列只依赖窄投递接口（顺序模式：创建者与被邀请方不同时在线）。
        var guildPermits = new Game.Guild.GuildPermitRegistry();
        var guildPermitDelivery = new GuildPermitDelivery(guildPermits);
        var devil = new DevilContractService(itemResources.DevilContracts, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
        var collectBoxProjection = new CollectBoxProjection(itemResources.CollectBoxes, clock);
        var weaponSkinService = new InventorySkinStorageService(itemResources.Items, itemResources.Skins,
            itemResources.GoldLimits, itemResources.EpicPieces, itemResources.Contracts, clock);
        log.Write("weapon_skin_catalog", new { Recipes = itemResources.Skins.Recipes.Count,
            Weapons = itemResources.Skins.Weapons.Count, DamageFonts = itemResources.Skins.DamageFonts.Count,
            Cutscenes = itemResources.Skins.Cutscenes.Count,
            Enabled = itemResources.Skins.Recipes.Count > 0 && itemResources.Skins.Weapons.Count > 0 });
        var weaponSkinProjection = new SkinStorageProjection(weaponSkinService);
        var projection = new TownProjection(towns, stats, presence, itemResources.EpicPieces, mailboxProjection, new SettingsProjection(settings), questProjection, learning, titleProjection, adventureProjection, clock, guildPermitDelivery, devil, collectBoxProjection, weaponSkinProjection);
        var creatures = new Game.Creatures.CreatureService(itemResources.Creatures, clock, towns, itemResources.Items);
        var creatureProjection = new CreatureProjection(creatures, presence, questProjection);
        var inventoryProjection = new InventoryProjection(stats, projection, itemResources.EpicPieces, creatureProjection);
        // 装扮预设：容量/列表在选角就绪后下发；扩展券走CMD515 action216。
        var avatarPresetService = new Game.Inventory.Services.AvatarPresetService(itemResources.Items, itemResources.Contracts, towns, clock);
        var avatarPresetProjection = new Network.Projection.Inventory.AvatarPresetProjection();
        var avatarPresetHandler = new Network.Handlers.AvatarPresetHandler(avatarPresetService, avatarPresetProjection, inventoryProjection, log);
        var weaponSkinHandler = new SkinStorageHandler(weaponSkinService, inventoryProjection, weaponSkinProjection, log);
        var rentalHandler = new RentalHandler(new RentalService(itemResources.Rental, itemResources.Items, towns, clock), inventoryProjection, log);
        var creatureHandler = new CreatureHandler(creatures, creatureProjection, log, inventoryProjection);
        // 建会认证的跨连接投递：被邀请方连接收 0x2AD/0x2AF，创建者连接收金币差额与 0x2AE/0x47/0x3A。
        var guildProjection = new GuildProjection(inventoryProjection, presence);
        var events = new Game.Events.ServerEventService(new ServerEventRepository(Path.Combine(options.DataDirectory, "game.db")), clock);
        var cashShop = new InventoryCashShopService(itemResources.Items, itemResources.CashShop, itemResources.GoldLimits, itemResources.EpicPieces, itemResources.Cargo,
            debugSettings, itemResources.Contracts, itemResources.AvatarPackages, devil);
        var levelProjection = new CharacterLevelProjection(stats, questProjection);
        var levelUpProjection = new LevelUpTicketProjection(inventoryProjection,levelProjection);
        var questEffects=new Game.Quests.QuestSkipEffects(itemResources.Items,learning,itemResources.GoldLimits,itemResources.EpicPieces);
        var commands = new GameCommandRegistry();
        new StaminaHandler(new(itemResources.DevilContracts), inventoryProjection, log, clock).Register(commands);
        new DevilContractHandler(log, clock).Register(commands);
        var auctionProbe = Development.Auction.AuctionProbe.Parse(options.DevelopmentAuctionProbe);
        // 拍卖行挂牌：SQL 在 Persistence/AuctionListingRepository，读用例与种子在 Game/Auction；
        // 条目字节快照的编码入口来自物品模块的 Builder。
        // 分类 id 的翻译需要共享物品目录（装备部位要靠模板查一次缓存一次），因此解析器在装配处显式创建。
        // 上架规划（CMD 183）还需要物品目录、史诗碎片判定与条目编码入口：可上架判定复用背包模块的字段口径，
        // 不新建第二套；目录与编码入口都在这里显式注入，Game 层因此不引用 Network。
        // ★ 取消上架（CMD 184）要把挂牌保存的条目快照**读回**物品核心再还回背包，因此编解码两个方向一起注入：
        //   同一个文件里的 ItemEntrySnapshot / ItemCoreFromEntry 严格互逆，Game 层仍不引用 Network。
        // ★ 存储合同由角色仓储实现（`repository`）：上架/取消都要与背包写入共用同一个写事务，
        //   因此拍卖行不再持有第二个仓储实例 —— 与邮件（MailboxService(repository, …)）同一形态。
        var auctionCategories = new Game.Auction.AuctionCategoryResolver(itemResources.Items);
        var auctions = new Game.Auction.AuctionService(repository, auctionCategories,
            itemResources.Items, itemResources.EpicPieces, Network.Builders.AuctionPackets.ItemEntrySnapshot,
            Network.Builders.AuctionPackets.ItemCoreFromEntry);
        if (options.AuctionSeedCount > 0)
        {
            // 建库与迁移由上面的 CharacterRepository 构造完成，种子在开始监听之前写入。
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var seedItems = Game.Auction.AuctionService.ResolveSeedItems(itemResources.Items,
                Network.Builders.AuctionPackets.ItemEntrySnapshot, out var unresolved);
            var seed = auctions.Seed(options.AuctionSeedCount, now, seedItems);
            log.Write("auction_seed", new
            {
                Requested = options.AuctionSeedCount,
                seed.Created,
                ItemIds = seed.ItemIds.ToArray(),
                UnresolvedItemIds = unresolved.ToArray()
            });
        }
        DungeonRoomCatalog? questRooms = null;
        DungeonCatalog? selectionDungeons = null;
        var progress = new Game.Characters.CharacterProgressReset(characters, learning, growth);
        var developmentCatalog = new DevelopmentCatalog(pvf, itemResources, quests) { Stats = stats, Growth = growth, PvfPath = options.PvfPath, CacheDirectory = options.DataDirectory };
        IGameCommandModule dungeon;
        IGameCommandModule? settlement = null;
        IGameCommandModule? questReplay = null;
        Func<GameSession, IGameConnectionActivity>? createActivity = null;
        DungeonPassiveDropPolicy? passiveDrops = null;
        AssaultModeCatalog? assaultMode = null;
        if (options.DevelopmentDungeonEntryProbe == DungeonEntryProbeMode.Disabled)
        {
            var dungeonCatalog = DungeonCatalog.Load(pvf);
            selectionDungeons = dungeonCatalog;
            assaultMode = AssaultModeCatalog.Load(pvf, dungeonCatalog);
            var rooms = DungeonRoomCatalog.Load(pvf);
            tutorialScenes ??= new TutorialScenes(pvf, tutorials, dungeonCatalog);
            questRooms = rooms;
            var drops = DungeonMonsterDropCatalog.Load(pvf);
            var dropRules = DungeonDropRuleCatalog.Load(pvf);
            var dropCandidates = itemResources.DungeonDropItems;
            var specialDrops = itemResources.DungeonSpecialDrops;
            var amplificationDrops = new EquipmentAmplificationDropPolicy(itemResources.Amplifications.Rules);
            string? configuredMaterials = Environment.GetEnvironmentVariable("DFO_HARLEM_MATERIAL_RULES");
            var materialRules = HarlemMaterialDropRules.Parse(configuredMaterials ?? "{}");
            var harlemDungeons = towns.DungeonMaps.Values.Where(map => map.HellDungeon && map.HellQuests.Contains(6080))
                .SelectMany(map => map.Options).Select(option => checked((uint)option.DungeonId)).ToArray();
            var hellEpicTable = DungeonHellEpicCatalog.Load(pvf);
            string? configuredEpics = Environment.GetEnvironmentVariable("DFO_HARLEM_HELL_EPIC_RULES");
            var epicRules = HarlemHellEpicRules.Parse(configuredEpics ?? "{}");
            var cosmoRewards = DungeonCosmoRewardCatalog.Load(pvf, dungeonCatalog, rooms);
            var harlemEpics = new HarlemHellEpicDropPolicy(harlemDungeons,
                hellEpicTable.Pool(HarlemHellEpicDropPolicy.Area, HarlemHellEpicDropPolicy.NormalList, itemResources.Items.Get),
                hellEpicTable.Pool(HarlemHellEpicDropPolicy.Area, HarlemHellEpicDropPolicy.WeaponList, itemResources.Items.Get),
                hellEpicTable.Pool(HarlemHellEpicDropPolicy.Area, HarlemHellEpicDropPolicy.TayberrsList, itemResources.Items.Get),
                hellEpicTable.Pool(HarlemHellEpicDropPolicy.Area, HarlemHellEpicDropPolicy.CosmoList, itemResources.Items.Get),
                epicRules, cosmoRewards);
            var materialDrops = new HarlemMaterialDropPolicy(materialRules, harlemDungeons, itemResources.Items.Get, harlemEpics);
            string? configuredBonuses = Environment.GetEnvironmentVariable("DFO_HARLEM_ABYSS_BONUS_RULES");
            var bonusRules = HarlemAbyssBonusRules.Parse(configuredBonuses ?? "{}");
            var abyssBonuses = new HarlemAbyssBonusDropPolicy(bonusRules, harlemEpics, itemResources.Items.Get);
            log.Write("startup", new { Stage = "Harlem material rewards", State = "complete", Rule = HarlemMaterialDropPolicy.Rule,
                materialRules.Enabled, Configured = configuredMaterials is not null });
            var resourceDrops = new DungeonResourceDropPolicy(dropRules, dropCandidates, drops.Get, itemResources.Items.Get,
                specialDrops, amplificationDrops, materialDrops, harlemEpics, abyssBonuses);
            passiveDrops = new(dungeonCatalog.Get, itemResources.Items.Get, DungeonDropRuleCatalog.LoadObjects(pvf), dropCandidates, amplificationDrops);
            int epicPermille = int.TryParse(Environment.GetEnvironmentVariable("DFO_HELL_EPIC_FRAGMENT_PERMILLE"), out int configuredPermille) ? Math.Clamp(configuredPermille, 0, 1000) : 0;
            DungeonItemDropPool? epicPool = null;
            if (epicPermille > 0)
            {
                var fragmentTemplates = new List<DfoServer.GameWorld.Items.ItemTemplate>();
                foreach (int pieceId in itemResources.EpicPieces.ByPiece.Keys)
                {
                    try { var template = itemResources.Items.Get(pieceId); if (DungeonDropCreation.Supports(template.Kind) && template.StackLimit > 0) fragmentTemplates.Add(template); }
                    catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or FileNotFoundException) { }
                }
                epicPool = new DungeonItemDropPool(fragmentTemplates);
            }
            var hellRewards = DungeonHellDropRules.TryLoad(pvf) is { } hellDropRules ? new DungeonHellRewardPolicy(hellDropRules, dropCandidates, epicPool, epicPermille) : null;
            Func<DungeonPlannedMonster, DungeonDropGenerationPlan> PrepareDrops(DungeonEntryPlan entry) => resourceDrops.PrepareEntry(entry, hellRewards);
            log.Write("dungeon_drop_policy", new { Rule = DungeonResourceDropPolicy.Rule,
                EquipmentWeights = "current_pvf_creation_rate", Retained = DungeonResourceDropPolicy.RetainedFields,
                FifthProbabilityRows = dropRules.ProbabilityRows.Count(row => row.Values[4] != 0),
                HellEpicRule = HarlemHellEpicDropPolicy.Rule, HellEpicSource = DungeonHellEpicCatalog.Path,
                HellEpicArea = HarlemHellEpicDropPolicy.Area, NormalEpicList = HarlemHellEpicDropPolicy.NormalList,
                LegendEpicList = HarlemHellEpicDropPolicy.NormalList, LegendRareRule = HarlemHellEpicDropPolicy.RareRule,
                EpicBranches = epicRules, EpicBranchesConfigured = configuredEpics is not null,
                AbyssBonusRule = HarlemAbyssBonusDropPolicy.Rule, AbyssBonusRules = bonusRules, AbyssBonusConfigured = configuredBonuses is not null,
                WeaponCandidates = harlemEpics.Weapons!.Entries.Length, TayberrsCandidates = harlemEpics.Tayberrs!.Entries.Length,
                CosmoCandidates = harlemEpics.Cosmo!.Entries.Length, CosmoRewardRolls = cosmoRewards.RewardRolls,
                NormalEpicCandidates = harlemEpics.Normal.Entries.Length, LegendEpicCandidates = harlemEpics.Normal.Entries.Length,
                specialDrops.Compatibility });
            var crackRewards = new CrackDimensionRewardServices(
                CrackDimensionCatalog.Load(pvf, itemResources.Items.Contains),
                itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            var altarRewards = new DungeonAltarRewardPolicy(DungeonAltarRewardCatalog.Load(pvf), DungeonAltarClearItemCatalog.Load(pvf),
                itemResources.DungeonAltarItems, DungeonKillExperienceCatalog.Load(pvf),
                itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            var despairRewards = new DungeonDespairRewardPolicy(DungeonAltarClearItemCatalog.Load(pvf),
                itemResources.DungeonAltarItems, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            dungeon = new DungeonHandler(log, dungeonCatalog, rooms,
                growth, DungeonKillExperienceCatalog.Load(pvf), projection, levelProjection,
                drops, itemResources.Items, itemResources.EpicPieces, itemResources.GoldLimits, questProjection, PrepareDrops,tutorials,tutorialScenes,crackRewards,altarRewards,
                despairRewards,titleService,titleProjection,inventoryProjection);
            settlement = new DungeonSettlementHandler(log, dungeonCatalog, DungeonClearExperienceCatalog.Load(pvf), growth, projection,
                rooms, drops, itemResources.Items, levelProjection, itemResources.EpicPieces,
                new Game.Dungeons.DungeonCardRewardPolicy(dropRules, dropCandidates, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces,
                prices: DungeonCardPriceCatalog.Load(pvf)), inventoryProjection, itemResources.Rental, events, adventureCatalog);
            questReplay = new QuestReplayHandler(questConnections,dungeonCatalog,rooms,log,drops,itemResources.Items,itemResources.EpicPieces,PrepareDrops,crackRewards);
        }
        else
        {
            var catalog = DungeonCatalog.Load(pvf);
            var rooms = options.DevelopmentDungeonEntryProbe == DungeonEntryProbeMode.FinishLoadingMonsters
                ? DungeonRoomCatalog.Load(pvf) : null;
            dungeon = new DungeonProbeModule(log, catalog, options.DevelopmentDungeonEntryProbe, rooms, projection);
            createActivity = static session => new DungeonProbeState(session);
        }
        var skinService = new InventoryCreatureSkinService(itemResources.Items, clock);
        var levelUpItems = new LevelUpTicketHandler(
            new Game.Characters.LevelUpTicketService(itemResources.Items,growth,clock,characters,learning),
            quests,levelUpProjection,log);
        var classChange = new Game.Characters.ClassChangeService(itemResources.Items, characters, learning, quests);
        IGameCommandModule[] modules =
        [
            new SessionHandler(projection, log),
            // 客户端保活：0x04FE/0x06BB 每 10 秒一次，服务端必须回同 opcode 帧（见 KeepaliveHandler）。
            new KeepaliveHandler(log),
            new SoloPartyHandler(log),
            new CharacterHandler(characters, projection, log, itemResources.GoldLimits, learning, tutorials, adventureProjection, expertJobs,
                adventureWeeklyProjection, avatarPresetProjection, avatarPresetService),
            new AdventureHandler(adventureProjection, adventureRepresentatives, expeditionProjection, clock),
            new AdventureExpeditionHandler(expeditionService, expeditionProjection, clock),
            new AdventureNameHandler(new Game.Adventure.AdventureNameService(adventureCatalog, itemResources.Items.AdventureNameTicket),
                adventureProjection, inventoryProjection, projection, clock),
            new TutorialHandler(tutorials, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, inventoryProjection),
            new AdventureShopHandler(new Game.Adventure.AdventureShopService(adventureCatalog, itemResources.Items,
                itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection, clock),
            new AdventureCapsuleHandler(new Game.Adventure.AdventureCapsuleService(adventureCatalog, growth), levelProjection, clock),
            new AdventureWeeklyHandler(adventureWeeklyProjection),
            new SkillHandler(learning, log),
            new SkillSwitchInventoryHandler(skillSwitchInventory, log),
            new TownHandler(projection, log),
            new TeleportHandler(new Game.Town.TeleportService(towns, itemResources.Teleports, itemResources.Items.Get, clock,
                options.TeleportPotionGates), projection, inventoryProjection, presence, log),
            new SettingsHandler(settings, log, projection),
            avatarPresetHandler,
            new MailboxHandler(mailbox, mailboxProjection, inventoryProjection),
            new TitleHandler(titleService, new Game.Titles.TitleBookService(titleCatalog, itemResources.Items, itemResources.Contracts), titleProjection, inventoryProjection, online),
            new CollectBoxHandler(new Game.CollectBox.CollectBoxService(itemResources.CollectBoxes, itemResources.Items,
                itemResources.EpicPieces), collectBoxProjection, inventoryProjection, clock),
            new InventoryHandler(new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, itemResources.Contracts), inventoryProjection),
            new CubeContractHandler(new CubeContractService(itemResources.Contracts), inventoryProjection, log, clock),
            new ConsumableUseHandler(new InventoryConsumableUseService(itemResources.RecoveryConsumables, clock), log, tutorials, itemResources.Items, inventoryProjection,
                new InventoryPremiumContractUseService(itemResources.Items, itemResources.Contracts, clock), creatures, creatureProjection, projection,
                new Game.ExpertJob.ExpertJobLearning(expertJobs, itemResources.Items, clock), expertJobs),
            weaponSkinHandler,
            rentalHandler,
            new CurrencyHandler(new Game.Currency.CurrencyService(itemResources.GoldLimits), inventoryProjection),
            new InventoryExpansionHandler(new InventoryExpansionService(itemResources.Items, clock), inventoryProjection, log),
            new CargoHandler(new InventoryCargoService(itemResources.Items, itemResources.Cargo, itemResources.GoldLimits), inventoryProjection),
            new ShopHandler(
                new InventoryShopService(itemResources.Items, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces, clock),
                cashShop,
                inventoryProjection, log),
            new RepairHandler(new InventoryRepairService(itemResources.Items, itemResources.Repair, towns, clock), inventoryProjection, log),
            new RandomOptionHandler(new InventoryRandomOptionService(itemResources.Items, itemResources.RandomOptions), inventoryProjection, log),
            new TextureChangeHandler(new InventoryTextureChangeService(itemResources.Items, itemResources.TextureChanges,
                itemResources.EpicPieces), inventoryProjection, log),
            new LimitCubeHandler(new InventoryLimitCubeService(itemResources.Items, itemResources.LimitCubes,
                itemResources.EpicPieces, itemResources.Contracts, itemResources.Boosters.DirectActivationItems,
                debugSettings: debugSettings, attributePairs: attributePairs), inventoryProjection, log),
            new EmblemCompoundHandler(new InventoryEmblemCompoundService(itemResources.Items,
                itemResources.EmblemCompounds, itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection, log),
            new AvatarCompoundHandler(new InventoryAvatarCompoundService(itemResources.Items,
                itemResources.AvatarCompounds, itemResources.AvatarPackages), inventoryProjection, log),
            new AvatarDyeHandler(new InventoryAvatarDyeService(itemResources.Items, itemResources.AvatarDyes), inventoryProjection, log),
            new RefinementHandler(new InventoryRefinementService(itemResources.Items, itemResources.Refinement), inventoryProjection),
            new EnchantmentHandler(enchantmentService, inventoryProjection, log),
            new EquipmentEmblemHandler(new InventoryEquipmentEmblemService(itemResources.Items, clock), inventoryProjection, log),
            new EnhancementHandler(new InventoryEnhancementService(itemResources.Items, itemResources.Enhancements,
                itemResources.MedalUpgrades, clock: clock,
                amplification: new InventoryAmplifyUpgradeService(itemResources.Items, itemResources.AmplifyUpgrades, itemResources.Enhancements,
                    itemResources.Disjoint, itemResources.GoldLimits, itemResources.EpicPieces, towns, clock: clock)), inventoryProjection, log),
            new AmplificationHandler(new InventoryAmplificationService(itemResources.Items, itemResources.Amplifications,
                itemResources.Enhancements, towns, clock), inventoryProjection, log),
            new CreatureSkinHandler(skinService, inventoryProjection, log),
            new CreatureEggHandler(new InventoryCreatureEggService(itemResources.CreatureEggs, itemResources.Items, clock), inventoryProjection, log),
            creatureHandler,
            new DisjointHandler(new InventoryDisjointService(itemResources.Items, itemResources.Disjoint, itemResources.GoldLimits, itemResources.EpicPieces, expertJobs: expertJobs), inventoryProjection, expertJobs),
            new ExpertJobHandler(quests, expertJobs, itemResources.Items,
                itemResources.GoldLimits, itemResources.EpicPieces, inventoryProjection, log, presence, expertStores, enchantmentService, machineDisassembly,
                new Game.ExpertJob.ExpertJobEnchanting(expertJobs, itemResources.Items, itemResources.Enchantments, expertPolicy, clock), online),
            new AvatarDisjointHandler(new InventoryAvatarDisjointService(itemResources.Items, itemResources.AvatarDisjoint,
                itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection, log),
            new EpicBookHandler(new EpicPieceCraftService(itemResources.Items, itemResources.EpicPieces, itemResources.GoldLimits), inventoryProjection),
            // 设计图升级：CMD271，材料与产物来自设计图自身的 [emancipate group]。
            new UpgradeChronicleHandler(new InventoryUpgradeChronicleService(itemResources.UpgradeChronicles, itemResources.Items,
                itemResources.EpicPieces, clock), inventoryProjection),
            // 设计图制造：CMD25，配方来自 [recipe] 设计图的 [input item]/[output item]。
            new CompoundHandler(new InventoryRecipeCraftService(itemResources.ItemRecipes, itemResources.Items,
                itemResources.EpicPieces, itemResources.GoldLimits, itemResources.ItemMakingSkills, clock), inventoryProjection, log),
            new BoosterHandler(new InventoryBoosterService(itemResources.Items, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces,
                debugSettings: debugSettings, contracts: itemResources.Contracts, avatars: itemResources.AvatarPackages, clock: clock, randomOptions: itemResources.RandomOptions,
                attributePairs: attributePairs), inventoryProjection,
                new InventoryAvatarPackageService(itemResources.Items, itemResources.AvatarPackages, itemResources.GoldLimits, itemResources.EpicPieces, debugSettings, clock), log, clock),
            new MagicBoxHandler(new InventoryMagicBoxService(itemResources.Items, itemResources.MagicBoxes, itemResources.GoldLimits, itemResources.EpicPieces,
                contracts: itemResources.Contracts, debugSettings: debugSettings), inventoryProjection),
            new DungeonSelectionHandler(projection, log, tutorials, selectionDungeons),
            new AssaultModeHandler(log, assaultMode, itemResources.EpicPieces, clock, itemResources.Items, itemResources.GoldLimits),
            new QuestLifecycleHandler(quests, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, log, questProjection),
            new ImageCommunicationHandler(imageCommunication, quests, log),
            new QuestHiddenHandler(quests,log),
            levelUpItems,
            new ExperienceConsumableHandler(new Game.Characters.ExperienceConsumableService(itemResources.Items, growth, clock),
                inventoryProjection, levelProjection, log, levelUpItems,
                new PermanentGrowthHandler(new Game.Characters.PermanentGrowthService(itemResources.Items, stats, learning, clock),
                    inventoryProjection, levelProjection, log),
                new LegacyStatusConsumableHandler(new Game.Characters.LegacyStatusConsumableService(itemResources.Items,
                    itemResources.GoldLimits, clock), inventoryProjection, log)),
            new QuestQuickClearHandler(quests,questConnections,questProjection,log,itemResources.Items,learning,levelUpProjection),
            new StackableActionHandler(quests,itemResources.Items,questProjection,log,levelProjection,learning,skinService,inventoryProjection,
                new Game.Creatures.CreatureChangeService(itemResources.Items.Get, itemResources.Items.CreatureChange,
                    itemResources.Creatures, clock, groups: itemResources.CreatureGroups,
                    growthItems: itemResources.Items.CreatureGrowthItem, actionItems: itemResources.Items.ActionConsumable),levelUpItems,weaponSkinHandler,
                new FatigueConsumableHandler(new Game.Characters.FatigueConsumableService(itemResources.Items, towns, clock), inventoryProjection, log), rentalHandler, creatureHandler,
                new InventoryPeriodExtensionService(itemResources.Items, towns, clock), new InventoryMileageService(itemResources.Items, towns, clock),
                new InventoryEquipmentPeriodChangeService(itemResources.Items, towns, clock), avatarPresetHandler),
            new CreatureTransportHandler(new Game.Creatures.CreatureTransportService(itemResources.Items.Get,
                itemResources.Items.ActionConsumable, itemResources.Creatures, repository, clock), inventoryProjection, log),
            new QuestConditionHandler(log, quests, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, questRooms, questParameters, growth),
            new QuestCompletionHandler(quests, questParameters, itemResources.Items,
                itemResources.GoldLimits, itemResources.EpicPieces, growth, log, levelProjection, questProjection, creatureProjection, expertJobs),
            new ClassChangeHandler(classChange, levelProjection, inventoryProjection, questProjection, projection, log, clock),
            new GrowUpChangeHandler(new Game.Characters.GrowUpChangeService(GrowUpChangeCatalog.Load(pvf), classChange),
                levelProjection, inventoryProjection, questProjection, projection, log, clock),
            dungeon,
            new AgitWarHandler(),
            // 公会：创建窗口的两次查重（CMD 0x9C / 0x9D）、介绍校验（0x2E3）与建会认证链路（0x2E5 / 0x2E6 / 0x2E7）。
            // 顺序模式下创建者可能已经下线：接受认证时用离线账号租约提交，因此这里显式注入账号 owner 表。
            new GuildHandler(new Game.Guild.GuildDirectory(repository, guildParameters), guildPermits, online, owners, log, guildProjection),
            new AuctionHandler(itemResources.Items, auctions, inventoryProjection, auctionProbe.NextSearch, auctionProbe.NextAverage,
                auctionProbe.NextMyList, log),
            new ClientNoticeHandler(log),
            // 聊天：0x0011 入站按 CP936 解析；普通/公会/悄悄话投 0x0C（正文带 CP936 文本，客户端按长度读取），
            // 两种喇叭投已实测可见的 0x76 喇叭页签（见 Docs/模块/聊天.md）。
            // 成员/目标查询走只读存储合同（repository），在线定位与投递走 OnlineSessionDirectory，不另建在线状态。
            new ChatHandler(new Game.Chat.ChatService(repository, new Game.Chat.ChatDelivery(online)), inventoryProjection, log)
        ];
        foreach (var module in modules)
            module.Register(commands);
        settlement?.Register(commands);
        questReplay?.Register(commands);
        commands.Seal();
        log.Write("startup", new { Stage = "Command registration", State = "complete", Commands = commands.Count });
        var login = new LoginHandler(options, owners, accounts, channels, log);
        var native = new NativeChannel(options, online, log);
        var game = new GameProtocolHandler(options, login, commands, repository, towns, online, projection, log, createActivity, mailboxProjection, quests, debugSettings, adventureProjection,questEffects,itemResources.Contracts,stats,inventoryProjection,clock,guildProjection,passiveDrops,devil,creatures,creatureProjection,questStages,itemResources.Boosters,native,itemResources.Items,experienceBonuses,weaponSkinProjection,expertStores,presence,levelProjection);
        var channel = new ChannelProtocolHandler(options, gameChannels, channels, log);
        Func<CancellationToken, Task>? gmCommands = options.GmPort == 0 ? null : stop => DevelopmentGmServer.Run(options.GmPort, DevelopmentCommand, stop);
        var startedAt = DateTimeOffset.Now;
        Func<CancellationToken, Task>? gmWeb = options.GmWebPort == 0 ? null : stop => GmWebServer.RunAsync(options.GmWebPort,
            new GmWebData(options, startedAt, online, new GmAccountDirectory(Path.Combine(options.DataDirectory, "game.db")),
                DevelopmentCommand, mailbox, itemResources.GoldLimits, new Network.Management.GmItemExtras(pvf, null) { AvatarLog = message => log.Write("gm-avatar", new { Message = message }) }, new GmAudit(Path.Combine(options.DataDirectory, "gm-audit.jsonl"))), stop);
        // 两个管理入口任一退出或失败都取消整个实例，与其他监听一致。
        Func<CancellationToken, Task>? management = gmCommands is null && gmWeb is null ? null : RunManagement;
        log.Write("startup", new { Stage = "Runtime assembly", State = "complete", ElapsedMs = Math.Round(timer.Elapsed.TotalMilliseconds, 3) });
        return new ServerHost(options, channel, game, management, OnlineCharacters, Started, log, gameChannels,native,gameCache);

        async Task RunManagement(CancellationToken stop)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var running = new List<Task>();
            Exception? failure = null;
            try
            {
                stop.ThrowIfCancellationRequested();
                // 仅在管理入口真正启动时预热；装配或前置监听失败不留下后台资源读取。
                developmentCatalog.StartWarmup();
                if (gmCommands is not null) running.Add(gmCommands(lifetime.Token));
                if (gmWeb is not null) running.Add(gmWeb(lifetime.Token));
                await await Task.WhenAny(running);
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            finally
            {
                lifetime.Cancel();
                // 双入口任一退出时，等待另一入口与全部索引收尾后再结束本实例。
                try { await Task.WhenAll([.. running, developmentCatalog.StopWarmupAsync()]); }
                catch (OperationCanceledException) when (failure is not null && lifetime.IsCancellationRequested) { }
                catch (Exception cleanup) when (failure is not null && !ReferenceEquals(cleanup, failure))
                {
                    throw new AggregateException("GM入口失败且退出收尾失败", failure, cleanup);
                }
            }
        }
        OnlineCharacterView[] OnlineCharacters() => online.Snapshot()
            .Select(character => new OnlineCharacterView(character.Account, character.CharacterId, character.Name,
                character.Ready, character.Phase.ToString(), character.State.ToString())).ToArray();
        void Started()
        {
            var missingMaps = towns.Areas.Values.SelectMany(area => area.Phases.Prepend(area.Default))
                .Where(map => map.Error is not null).Select(map => new { map.Path, map.Error }).Distinct().ToArray();
            if (missingMaps.Length != 0)
                log.Write("resource_warning", new { Stage = "Town maps", Count = missingMaps.Length, MissingMaps = missingMaps });
            log.Write("ready", new
            {
                options.ChannelPort,
                options.GamePort,
                ChannelEntries = gameChannels.Endpoints,
                options.GmPort,
                options.DevelopmentAccount,
                options.DevelopmentDungeonEntryProbe,
                Pid = Environment.ProcessId,
                Towns = towns.Areas.Keys.Select(key => key.Item1).Distinct().Count(),
                Areas = towns.Areas.Count,
                MissingMaps = missingMaps
            });
        }
        string DevelopmentCommand(string line)
        {
            string result = DevelopmentGm.Answer(line, options.DevelopmentAccount, repository, owners, online.Find,
                mailbox, itemResources.Items, progress, cashShop, debugSettings, events, developmentCatalog,
                online.FindConnection, log, noticeSender);
            log.Write("gm", new { Ok = result.StartsWith("OK ", StringComparison.Ordinal), Result = result.Length > 180 ? result[..180] : result });
            return result;
        }
    }
}
