using DfoServer.Development.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Development;
using DfoServer.Game.Auth;
using DfoServer.Network;
using DfoServer.Network.Management;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Inventory;
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
        GameWorld.Adventure.AdventureExpeditionCatalog? expeditionCatalog = null, TimeProvider? clock = null)
    {
        quests = quests.WithCreatures(itemResources.Creatures);
        itemResources.Creatures.PrepareEvolutions();
        var channels = LoadChannels(Path.Combine(AppContext.BaseDirectory, "Resources", "channel.txt"));
        var gameChannels = new GameChannelListeners(options, channels);
        log.Write("startup", new { Stage = "Runtime assembly", State = "starting" });
        var timer = Stopwatch.StartNew();
        var owners = new AccountOwners();
        var online = new OnlineSessionDirectory();
        var accounts = new AuthenticationService(new AuthenticationRepository(Path.Combine(options.DataDirectory, "game.db")));
        accounts.EnsureDevelopmentAccount();
        var presence = new TownPresence(towns, channels.GameIdentity);
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
        var adventureRepresentatives = new Game.Adventure.AdventureRepresentativeService(adventureCatalog);
        expeditionCatalog ??= new GameWorld.Adventure.AdventureExpeditionCatalog(pvf);
        var expeditionService = new Game.Adventure.AdventureExpeditionService(adventureCatalog, expeditionCatalog);
        var expeditionProjection = new AdventureExpeditionProjection(expeditionService, clock);
        var growth = CharacterGrowthCatalog.Load(pvf, learning);
        tutorials ??= new TutorialCatalog(pvf);
        var questProjection = new QuestProjection(quests,questParameters,itemResources.Items,itemResources.GoldLimits,itemResources.EpicPieces,growth);
        var stats = new Game.Characters.CharacterStatComputer(characters);
        // 建会认证申请与入城补投：入城序列只依赖窄投递接口（顺序模式：创建者与被邀请方不同时在线）。
        var guildPermits = new Game.Guild.GuildPermitRegistry();
        var guildPermitDelivery = new GuildPermitDelivery(guildPermits);
        var devil = new DevilContractService(itemResources.DevilContracts, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
        var collectBoxProjection = new CollectBoxProjection(itemResources.CollectBoxes, clock);
        var projection = new TownProjection(towns, stats, presence, itemResources.EpicPieces, mailboxProjection, new SettingsProjection(settings), questProjection, learning, titleProjection, adventureProjection, clock, guildPermitDelivery, devil, collectBoxProjection);
        var creatures = new Game.Creatures.CreatureService(itemResources.Creatures, clock, itemResources.EpicPieces);
        var creatureProjection = new CreatureProjection(creatures, presence, questProjection);
        var inventoryProjection = new InventoryProjection(stats, projection, itemResources.EpicPieces, creatureProjection);
        // 建会认证的跨连接投递：被邀请方连接收 0x2AD/0x2AF，创建者连接收金币差额与 0x2AE/0x47/0x3A。
        var guildProjection = new GuildProjection(inventoryProjection, presence);
        var debugSettings = new GameDebugSettings();
        var events = new Game.Events.ServerEventService(new ServerEventRepository(Path.Combine(options.DataDirectory, "game.db")), clock);
        var cashShop = new InventoryCashShopService(itemResources.Items, itemResources.CashShop, itemResources.GoldLimits, itemResources.EpicPieces,
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
            assaultMode = AssaultModeCatalog.Load(pvf, dungeonCatalog);
            var rooms = DungeonRoomCatalog.Load(pvf);
            tutorialScenes ??= new TutorialScenes(pvf, tutorials, dungeonCatalog);
            questRooms = rooms;
            var drops = DungeonMonsterDropCatalog.Load(pvf);
            var dropRules = DungeonDropRuleCatalog.Load(pvf);
            var dropCandidates = itemResources.DungeonDropItems;
            var specialDrops = itemResources.DungeonSpecialDrops;
            var resourceDrops = new DungeonResourceDropPolicy(dropRules, dropCandidates, drops.Get, itemResources.Items.Get, specialDrops);
            passiveDrops = new(dungeonCatalog.Get, itemResources.Items.Get, DungeonDropRuleCatalog.LoadObjects(pvf), dropCandidates);
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
            DungeonDropGenerationPlan PrepareDrops(DungeonEntryPlan entry, DungeonPlannedMonster monster)
            {
                var plan = resourceDrops.Prepare(monster.MonsterId, monster.CandidateLevel, monster.SpawnType, entry.Difficulty, entry.BasisLevel);
                // 实验性：深渊波次末位怪额外的 hellparty 装备骰；奖励规则文件缺失时保持普通掉落。
                return monster.HellRewardRolls > 0 && hellRewards is not null
                    ? hellRewards.Extend(plan, entry.BasisLevel ?? monster.CandidateLevel, entry.HellPartyMode, monster.HellRewardRolls, entry.Difficulty) : plan;
            }
            log.Write("dungeon_drop_policy", new { Rule = DungeonResourceDropPolicy.Rule,
                EquipmentWeights = "current_pvf_creation_rate", Retained = DungeonResourceDropPolicy.RetainedFields,
                FifthProbabilityRows = dropRules.ProbabilityRows.Count(row => row.Values[4] != 0),
                specialDrops.Compatibility });
            dungeon = new DungeonHandler(log, dungeonCatalog, rooms,
                growth, DungeonKillExperienceCatalog.Load(pvf), projection, levelProjection,
                drops, itemResources.Items, itemResources.EpicPieces, itemResources.GoldLimits, questProjection, PrepareDrops,tutorials,tutorialScenes);
            settlement = new DungeonSettlementHandler(log, dungeonCatalog, DungeonClearExperienceCatalog.Load(pvf), growth, projection,
                rooms, drops, itemResources.Items, levelProjection, itemResources.EpicPieces,
                new Game.Dungeons.DungeonCardRewardPolicy(dropRules, dropCandidates, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces,
                prices: DungeonCardPriceCatalog.Load(pvf)), inventoryProjection, itemResources.Rental, events);
            questReplay = new QuestReplayHandler(questConnections,dungeonCatalog,rooms,log,drops,itemResources.Items,itemResources.EpicPieces,PrepareDrops);
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
            new Game.Characters.LevelUpTicketService(itemResources.Items,growth,itemResources.EpicPieces,clock,characters,learning),
            quests,levelUpProjection,log);
        IGameCommandModule[] modules =
        [
            new SessionHandler(projection, log),
            new CharacterHandler(characters, projection, log, itemResources.GoldLimits, learning, tutorials, adventureProjection),
            new AdventureHandler(adventureProjection, adventureRepresentatives, expeditionProjection, clock),
            new AdventureExpeditionHandler(expeditionService, expeditionProjection, clock),
            new AdventureNameHandler(new Game.Adventure.AdventureNameService(adventureCatalog, itemResources.Items.AdventureNameTicket),
                adventureProjection, inventoryProjection, projection, clock),
            new TutorialHandler(tutorials, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, inventoryProjection),
            new AdventureShopHandler(new Game.Adventure.AdventureShopService(adventureCatalog, itemResources.Items,
                itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection, clock),
            new AdventureCapsuleHandler(new Game.Adventure.AdventureCapsuleService(adventureCatalog, growth), levelProjection, clock),
            new SkillHandler(learning, log),
            new TownHandler(projection, log),
            new TeleportHandler(new Game.Town.TeleportService(towns, itemResources.Teleports, itemResources.Items.Get, clock,
                options.TeleportPotionGates), projection, inventoryProjection, presence, log),
            new SettingsHandler(settings, log, projection),
            new MailboxHandler(mailbox, mailboxProjection, inventoryProjection),
            new TitleHandler(titleService, new Game.Titles.TitleBookService(titleCatalog, itemResources.Items, itemResources.Contracts), titleProjection, inventoryProjection, projection, online),
            new CollectBoxHandler(new Game.CollectBox.CollectBoxService(itemResources.CollectBoxes, itemResources.Items,
                itemResources.EpicPieces), collectBoxProjection, inventoryProjection, clock),
            new InventoryHandler(new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, itemResources.Contracts), inventoryProjection),
            new CubeContractHandler(new CubeContractService(itemResources.Contracts), inventoryProjection, log, clock),
            new ConsumableUseHandler(new InventoryConsumableUseService(itemResources.RecoveryConsumables), log, tutorials, itemResources.Items, inventoryProjection,
                new InventoryPremiumContractUseService(itemResources.Items, itemResources.Contracts, clock), creatures, creatureProjection),
            new RentalHandler(new RentalService(itemResources.Rental, itemResources.Items), inventoryProjection, log),
            new CurrencyHandler(new Game.Currency.CurrencyService(itemResources.GoldLimits), inventoryProjection),
            new CargoHandler(new InventoryCargoService(itemResources.Items, itemResources.Cargo, itemResources.GoldLimits), inventoryProjection),
            new ShopHandler(
                new InventoryShopService(itemResources.Items, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces),
                cashShop,
                inventoryProjection, log),
            new RepairHandler(new InventoryRepairService(itemResources.Items, itemResources.Repair), inventoryProjection, log),
            new RandomOptionHandler(new InventoryRandomOptionService(itemResources.Items, itemResources.RandomOptions), inventoryProjection, log),
            new LimitCubeHandler(new InventoryLimitCubeService(itemResources.Items, itemResources.LimitCubes,
                itemResources.EpicPieces, itemResources.Contracts, itemResources.Boosters.DirectActivationItems,
                debugSettings: debugSettings), inventoryProjection, log),
            new AvatarDyeHandler(new InventoryAvatarDyeService(itemResources.Items, itemResources.AvatarDyes), inventoryProjection, log),
            new RefinementHandler(new InventoryRefinementService(itemResources.Items, itemResources.Refinement), inventoryProjection),
            new EnchantmentHandler(new InventoryEnchantmentService(itemResources.Items, itemResources.Enchantments, clock), inventoryProjection, log),
            new EquipmentEmblemHandler(new InventoryEquipmentEmblemService(itemResources.Items, clock), inventoryProjection, log),
            new EnhancementHandler(new InventoryEnhancementService(itemResources.Items, itemResources.Enhancements, clock: clock), inventoryProjection, log),
            new CreatureSkinHandler(skinService, inventoryProjection, log),
            new CreatureEggHandler(new InventoryCreatureEggService(itemResources.CreatureEggs, itemResources.Items, clock), inventoryProjection, log),
            new CreatureHandler(creatures, creatureProjection, log),
            new DisjointHandler(new InventoryDisjointService(itemResources.Items, itemResources.Disjoint, itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection),
            new EpicBookHandler(new EpicPieceCraftService(itemResources.Items, itemResources.EpicPieces, itemResources.GoldLimits), inventoryProjection),
            new BoosterHandler(new InventoryBoosterService(itemResources.Items, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces,
                debugSettings: debugSettings, contracts: itemResources.Contracts), inventoryProjection,
                new InventoryAvatarPackageService(itemResources.Items, itemResources.AvatarPackages, itemResources.GoldLimits, itemResources.EpicPieces, debugSettings), log, clock),
            new MagicBoxHandler(new InventoryMagicBoxService(itemResources.Items, itemResources.MagicBoxes, itemResources.GoldLimits, itemResources.EpicPieces,
                contracts: itemResources.Contracts, debugSettings: debugSettings), inventoryProjection),
            new DungeonSelectionHandler(projection, log, tutorials),
            new AssaultModeHandler(log, assaultMode, itemResources.EpicPieces, clock, itemResources.Items, itemResources.GoldLimits),
            new QuestLifecycleHandler(quests, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, log, questProjection),
            new ImageCommunicationHandler(imageCommunication, quests, log),
            new QuestHiddenHandler(quests,log),
            levelUpItems,
            new ExperienceConsumableHandler(new Game.Characters.ExperienceConsumableService(itemResources.Items, growth, itemResources.EpicPieces, clock),
                inventoryProjection, levelProjection, log, levelUpItems),
            new QuestQuickClearHandler(quests,questConnections,questProjection,log,itemResources.Items,learning,levelUpProjection),
            new QuestTicketHandler(quests,itemResources.Items,questProjection,log,levelProjection,learning,skinService,inventoryProjection,
                new Game.Creatures.CreatureChangeService(itemResources.Items.Get, itemResources.Items.CreatureChange,
                    itemResources.Creatures, itemResources.EpicPieces, clock, groups: itemResources.CreatureGroups,
                    growthItems: itemResources.Items.CreatureGrowthItem),levelUpItems),
            new QuestConditionHandler(log, quests, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, questRooms, questParameters, growth),
            new QuestCompletionHandler(quests, questParameters, itemResources.Items,
                itemResources.GoldLimits, itemResources.EpicPieces, growth, log, levelProjection, questProjection, creatureProjection),
            new ClassChangeHandler(new Game.Characters.ClassChangeService(itemResources.Items, characters, learning, quests,
                itemResources.EpicPieces), levelProjection, inventoryProjection, questProjection, projection, log, clock),
            dungeon,
            new AgitWarHandler(),
            // 公会：创建窗口的两次查重（CMD 0x9C / 0x9D）、介绍校验（0x2E3）与建会认证链路（0x2E5 / 0x2E6 / 0x2E7）。
            // 顺序模式下创建者可能已经下线：接受认证时用离线账号租约提交，因此这里显式注入账号 owner 表。
            new GuildHandler(new Game.Guild.GuildDirectory(repository, guildParameters), guildPermits, online, owners, log, guildProjection),
            new AuctionHandler(itemResources.Items, auctions, inventoryProjection, auctionProbe.NextSearch, auctionProbe.NextAverage,
                auctionProbe.NextMyList, log),
            new ClientNoticeHandler(log)
        ];
        foreach (var module in modules)
            module.Register(commands);
        settlement?.Register(commands);
        questReplay?.Register(commands);
        commands.Seal();
        log.Write("startup", new { Stage = "Command registration", State = "complete", Commands = commands.Count });
        var login = new LoginHandler(options, owners, accounts, channels, log);
        var game = new GameProtocolHandler(options, login, commands, repository, towns, online, projection, log, createActivity, mailboxProjection, quests, debugSettings, adventureProjection,questEffects,itemResources.Contracts,stats,inventoryProjection,clock,guildProjection,passiveDrops,devil,creatures,creatureProjection);
        var channel = new ChannelProtocolHandler(options, gameChannels, channels, log);
        Func<CancellationToken, Task>? gmCommands = options.GmPort == 0 ? null : stop => DevelopmentGmServer.Run(options.GmPort, DevelopmentCommand, stop);
        var startedAt = DateTimeOffset.Now;
        Func<CancellationToken, Task>? gmWeb = options.GmWebPort == 0 ? null : stop => GmWebServer.RunAsync(options.GmWebPort,
            new GmWebData(options, startedAt, online, new GmAccountDirectory(Path.Combine(options.DataDirectory, "game.db")),
                DevelopmentCommand, mailbox, itemResources.GoldLimits, new Network.Management.GmItemExtras(pvf, null), new GmAudit(Path.Combine(options.DataDirectory, "gm-audit.jsonl"))), stop);
        // 两个管理入口任一退出或失败都取消整个实例，与其他监听一致。
        Func<CancellationToken, Task>? management = gmCommands is null && gmWeb is null ? null : RunManagement;
        log.Write("startup", new { Stage = "Runtime assembly", State = "complete", ElapsedMs = Math.Round(timer.Elapsed.TotalMilliseconds, 3) });
        return new ServerHost(options, channel, game, management, OnlineCharacters, Started, log, gameChannels);

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
                mailbox, itemResources.Items, progress, cashShop, debugSettings, events, developmentCatalog);
            log.Write("gm", new { Ok = result.StartsWith("OK ", StringComparison.Ordinal), Result = result.Length > 180 ? result[..180] : result });
            return result;
        }
    }
}
