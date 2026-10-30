using DfoServer.Development.Dungeons;
using System.Collections.Immutable;
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
    // 超时空之战的世界地图次数显示用；PVF 缺这份表时不下发，解析失败照常报错。
    static DungeonIncountCatalog? LoadIncounts(PvfArchive pvf) => pvf.Contains(DungeonIncountCatalog.Path) ? DungeonIncountCatalog.Load(pvf) : null;

    // 网吧身份默认开：数据目录没有 pc-room.json 即为开，线上重启自动恢复为开；显式 {"enabled":false} 才关闭。
    // TCP 夹具在临时数据目录写入该文件，避免线上默认值改写既有的选角、经验和虚弱断言。
    static bool PremiumPcRoomDefault(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, "pc-room.json");
        if (!File.Exists(path)) return true;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return !(document.RootElement.TryGetProperty("enabled", out var enabled) &&
                enabled.ValueKind == System.Text.Json.JsonValueKind.False);
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new InvalidDataException("pc-room.json 必须是 JSON 对象：" + path, error);
        }
    }

    static Stream OpenShipped(string logicalName) =>
        typeof(ServerRuntime).Assembly.GetManifestResourceStream(logicalName)
        ?? throw new InvalidDataException("程序缺少内置资源：" + logicalName);

    // 外置文件优先，任何部署形态都一样：运维直接改安装目录里的配置即可生效，不必重建程序；
    // 文件缺失才回退同版本内置资源。外置文件同时是运维关闭某个频道的唯一开关
    // （2026-10-30 实机：单文件发行包若绕过它就会把已关闭的频道又带回来），不能按部署形态跳过。
    static Stream OpenDefaultOrShipped(string shippedFile, string logicalName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Resources", shippedFile);
        return File.Exists(path) ? File.OpenRead(path) : OpenShipped(logicalName);
    }

    // 把实际生效的来源写进日志：改了外置文件却没生效时，能一眼看出读的是哪一份。
    static void LogResourceSource(ServerLog log, string stage, string shippedFile)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Resources", shippedFile);
        log.Write("resource_source", new { Stage = stage, Source = File.Exists(path) ? "external" : "bundled", Path = path });
    }

    // 显式给出路径时始终按外置读取，供夹具与运维指定文件；缺失才回退内置脚本。
    internal static ChannelCatalog LoadChannels(string path) =>
        ParseChannels(File.Exists(path) ? File.OpenRead(path) : OpenShipped("DfoServer.Resources.channel.txt"));

    internal static ChannelCatalog LoadChannels() =>
        ParseChannels(OpenDefaultOrShipped("channel.txt", "DfoServer.Resources.channel.txt"));

    static ChannelCatalog ParseChannels(Stream stream)
    {
        using (stream)
        using (var reader = new StreamReader(stream))
            return ChannelCatalog.Parse(reader.ReadToEnd());
    }

    // 漏掉内置回退会让服务端在「Cash shop promotions」阶段直接以 FileNotFoundException 结束（2026-10-30 实机）。
    internal static string ReadShippedPromotionConfig()
    {
        using var stream = OpenDefaultOrShipped("cash-shop-promotions.json", "DfoServer.Resources.cash-shop-promotions.json");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static CashShopPromotionCatalog LoadCashShopPromotions(ItemResources items) =>
        CashShopPromotionCatalog.Parse(ReadShippedPromotionConfig(), items.CashShop, items.Items.Get);

    public static ServerHost Create(ServerOptions options, PvfArchive pvf, CharacterRepository repository, TownCatalog towns, CharacterCatalog characters,
        SkillLearningCatalog learning, ItemResources itemResources, ServerLog log, QuestCatalog quests,
        TutorialCatalog? tutorials = null, TutorialScenes? tutorialScenes = null,
        GameWorld.Adventure.AdventureCatalog? adventureCatalog = null,
        GameWorld.Adventure.AdventureExpeditionCatalog? expeditionCatalog = null, TimeProvider? clock = null,
        CharacterGrowthCatalog? growthCatalog = null, Game.Raids.LukeRaidStartPolicy? lukeStartPolicy = null,
        Func<int, int>? evildomHiddenDraw = null, Func<CashShopPromotionCatalog>? promotions = null)
    {
        quests = quests.WithCreatures(itemResources.Creatures);
        var questStages = QuestStageGraph.Build(quests);
        itemResources.Creatures.PrepareEvolutions();
        var channels = LoadChannels();
        var gameChannels = new GameChannelListeners(options, channels);
        log.Write("startup", new { Stage = "Runtime assembly", State = "starting" });
        LogResourceSource(log, "Channel script", "channel.txt");
        LogResourceSource(log, "Cash shop promotions", "cash-shop-promotions.json");
        LogResourceSource(log, "Independent attribute pairs", "independent-attribute-pairs.json");
        if (!itemResources.ItemMakingSkills.Available)
            log.Write("resource_warning", new { Stage = "Item making skills", Missing = "etc/itemmakingskill.etc", Disabled = "带制作熟练度声明的配方" });
        var timer = Stopwatch.StartNew();
        var attributePairs = IndependentAttributePairCatalog.Load(
            Path.Combine(AppContext.BaseDirectory, "Resources", "independent-attribute-pairs.json"),
            itemResources.Items, itemResources.Boosters, itemResources.LimitCubes, itemResources.Enchantments,
            itemResources.EpicPieces, itemResources.Contracts, id => Game.Currency.CurrencyWallet.RewardKind(id) is not null,
            useBundledDefault: true);
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
        var social = new Game.Social.SocialService(repository, online);
        var socialProjection = new SocialProjection(social);
        var parties = new Game.Party.PartyService(repository, clock, gameCache, guideStore: repository);
        var trades = new Game.Trade.TradeRegistry();
        var tradeHandler = new TradeHandler(trades, online, itemResources.Items, itemResources.GoldLimits, log);
        var pvpRatingCatalog = new GameWorld.Pvp.PvpRatingCatalog(pvf);
        var pvpRatingCalculator = new Game.Pvp.PvpRatingCalculator(pvpRatingCatalog);
        var pvpRatings = new Game.Pvp.PvpRatingSettlement(repository, pvpRatingCalculator);
        var totalRatings = new Game.Pvp.PvpTotalRatingSettlement(repository, pvpRatings, pvpRatingCalculator);
        var pvpMissionCatalog = new GameWorld.Pvp.PvpMissionCatalog(pvf);
        foreach (var reward in pvpMissionCatalog.Definitions.Concat(pvpMissionCatalog.DailyDefinitions).SelectMany(row => row.Rewards).DistinctBy(row => row.ItemId))
            _ = itemResources.Items.Get(reward.ItemId);
        var pvpMissionBook = new Game.Pvp.PvpMissionBook(pvpMissionCatalog, repository, pvpRatings,
            itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, clock);
        var pvpMissions = new PvpMissionProjection(pvpMissionBook);
        var arenaRules = new GameWorld.Pvp.PvpArenaCatalog(pvf);
        itemResources.Items.TryGet(Game.Pvp.PvpTotalTeamRename.CardId, out var totalRenameCard);
        var totalTeams = new Game.Pvp.PvpTotalTeamService(repository, arenaRules, totalRenameCard, clock);
        var arena = new Game.Pvp.PvpArenaService(gameCache, arenaRules, repository, ratings: pvpRatings,
            totalTeams: totalTeams, totalRatings: totalRatings, totalSkills: learning);
        // GM notice 的公告出口：与 GM 聊天帧注入共用在线连接目录，帧内编码由 ServerNoticePackets 负责。
        var noticeSender = new GmNoticeSender(online);
        var accounts = new AuthenticationService(new AuthenticationRepository(Path.Combine(options.DataDirectory, "game.db")));
        accounts.EnsureDevelopmentAccount();
        // 网吧身份线上默认常开：本进程启动即为开，重启后自动恢复为开；GM 的 pc-room on/off 只改当前进程（不落盘）。
        var debugSettings = new GameDebugSettings { PremiumPcRoom = PremiumPcRoomDefault(options.DataDirectory) };
        var monsterBattleLevels = new DungeonBattleLevelConfiguration(options.DataDirectory);
        monsterBattleLevels.Reload();
        log.Write("monster_battle_level_config", new { Rules = monsterBattleLevels.Settings.Current.Rules.Count });
        var monsterAi = new MonsterAiConfiguration(options.DataDirectory);
        monsterAi.Reload();
        log.Write("monster_ai_config", new { monsterAi.Settings.Current.Enabled, Rules = monsterAi.Settings.Current.Rules.Count });
        var expertStores = new ExpertJobStoreBoard();
        var marriageCatalog = GameWorld.Marriage.MarriageCatalog.Load(pvf);
        var presence = new TownPresence(towns, channels.GameIdentity, clock, debugSettings, expertStores,
            new Network.Builders.MarriageAppearance(marriageCatalog, itemResources.Items));
        var expertJobs = itemResources.ExpertJobs;
        string expertPolicyPath = Path.Combine(options.DataDirectory, "expert-job-compatibility.json");
        var expertPolicy = File.Exists(expertPolicyPath)
            ? System.Text.Json.JsonSerializer.Deserialize<Game.ExpertJob.ExpertJobCompatibility>(File.ReadAllText(expertPolicyPath),
                new System.Text.Json.JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidDataException("副职业兼容配置不能为空")
            : new Game.ExpertJob.ExpertJobCompatibility();
        expertPolicy.Validate();
        var machineDisassembly = new Game.ExpertJob.ExpertJobDisassembly(
            new InventoryDisjointService(itemResources.Items, itemResources.Disjoint, itemResources.GoldLimits, itemResources.EpicPieces, expertJobs: expertJobs, clock: clock),
            expertJobs, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, expertPolicy, clock);

        var enchantmentService = new InventoryEnchantmentService(itemResources.Items, itemResources.Enchantments, clock);
        var mailbox = new Game.Mailbox.MailboxService(repository, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
        var mailboxProjection = new MailboxProjection(mailbox, online);
        var creatureTransportMailPolicy = new CreatureTransportMailPolicy(1);
        var settings = new ClientSettingsService(new ClientSettingsRepository(Path.Combine(options.DataDirectory, "game.db")),
            CharacterEmotionCatalog.Load(pvf));
        var questParameters = QuestRewardParameterCatalog.Load(pvf);
        var questConnections = QuestConnectionCatalog.Load(pvf);
        var imageCommunication = ImageCommunicationCatalog.Load(pvf, towns);
        var titleCatalog = new TitleCatalog(pvf, quests);
        // 公会参数（建会花费、仓库格数、等级经验曲线）：数值只从 PVF 读，业务层不写死常数。
        var guildParameters = new GameWorld.Guilds.GuildParameterCatalog(pvf);
        var guildDungeons = new GameWorld.Guilds.GuildDungeonCatalog(pvf);
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
        var privateCharacters = new Game.Pvp.PvpPrivateCharacterService(characters,PvpPrivateCharacterCatalog.Load(pvf),
            learning,growth,itemResources.Items,towns);
        var experienceBonuses = new Game.Characters.ExperienceBonusService(itemResources.Items, itemResources.RecoveryConsumables,
            GameWorld.Characters.ExperienceBonusCatalog.Load(pvf), new Game.Guild.GuildExperienceBonusProvider(repository),
            missingTemplate: (request, item) => log.Write("resource_warning", new
            {
                Source = "experience_bonus", Reason = "equipped_item_template_missing", CharacterId = request.Character.Id,
                request.Event, request.DungeonId, ItemId = item.Core.ItemId, item.Uid, Slot = item.Position.Index
            }), clock: clock);
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
        var serverItemRules = ServerItemUsageCatalog.Load(pvf);
        var serverItemCycles = new ServerItemCycleService(serverItemRules, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, clock);
        var epicProduction = new EpicProductionCatalog(pvf);
        var epicAbilities = new EpicProductionAbilityCatalog(pvf);
        string epicPolicyPath=Path.Combine(options.DataDirectory,"epic-production-compatibility.json");
        var epicPolicy=File.Exists(epicPolicyPath)
            ? System.Text.Json.JsonSerializer.Deserialize<Game.EpicProduction.EpicProductionCompatibility>(File.ReadAllText(epicPolicyPath),
                new System.Text.Json.JsonSerializerOptions { UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidDataException("史诗加工兼容配置为空")
            : new Game.EpicProduction.EpicProductionCompatibility();
        epicPolicy.Validate();
        log.Write("startup",new { Stage="Epic production compatibility",State="complete",epicPolicy.GreatSuccessPercent,
            Policy="Independent per processing operation; not original min/max scheduling" });
        var epicProductionProjection=new EpicProductionProjection(clock);
        var collectBoxProjection = new CollectBoxProjection(itemResources.CollectBoxes, clock);
        var incounts = LoadIncounts(pvf);
        var weaponSkinService = new InventorySkinStorageService(itemResources.Items, itemResources.Skins,
            itemResources.GoldLimits, itemResources.EpicPieces, itemResources.Contracts, clock);
        log.Write("weapon_skin_catalog", new { Recipes = itemResources.Skins.Recipes.Count,
            Weapons = itemResources.Skins.Weapons.Count, DamageFonts = itemResources.Skins.DamageFonts.Count,
            Cutscenes = itemResources.Skins.Cutscenes.Count,
            Enabled = itemResources.Skins.Recipes.Count > 0 && itemResources.Skins.Weapons.Count > 0 });
        var weaponSkinProjection = new SkinStorageProjection(weaponSkinService, parties, presence);
        var dungeonCatalog = DungeonCatalog.Load(pvf);
        repository.MarriageResources = marriageCatalog;
        var marriageProjection = new MarriageProjection(repository, marriageCatalog, online, clock);
        var events = new Game.Events.ServerEventService(new ServerEventRepository(Path.Combine(options.DataDirectory, "game.db")), clock);
        var cashPromotions = promotions is null ? null : new Game.CashShopPromotions.CashShopPromotionService(promotions, itemResources.Items.Get);
        var projection = new TownProjection(towns, stats, presence, itemResources.EpicPieces, mailboxProjection, new SettingsProjection(settings), questProjection, learning, titleProjection, adventureProjection, clock, guildPermitDelivery, devil, collectBoxProjection, weaponSkinProjection, parties: parties, epicProduction: epicProductionProjection, dungeons: dungeonCatalog, marriage: marriageProjection, events: events, mileageShop: itemResources.CashShop.Mileage);
        var creatures = new Game.Creatures.CreatureService(itemResources.Creatures, clock, towns, itemResources.Items);
        var creatureProjection = new CreatureProjection(creatures, presence, questProjection);
        var inventoryProjection = new InventoryProjection(stats, projection, itemResources.EpicPieces, creatureProjection, weaponSkinProjection);
        var totalTeamProjection = new PvpTotalTeamProjection(learning, stats, log);
        var marriageHandler = new MarriageHandler(repository, marriageCatalog, itemResources.Items,
            new Game.Marriage.MarriageInventoryPlanner(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces),
            towns, projection, online, inventoryProjection, log, clock);
        var marriageScheduler = new MarriageWeddingScheduler(repository, marriageCatalog, itemResources.Items, online, marriageProjection, log, clock);
        // 装扮预设：容量/列表在选角就绪后下发；扩展券走CMD515 action216。
        var avatarPresetService = new Game.Inventory.Services.AvatarPresetService(itemResources.Items, itemResources.Contracts, towns, clock);
        var avatarPresetProjection = new Network.Projection.Inventory.AvatarPresetProjection();
        var avatarPresetHandler = new Network.Handlers.AvatarPresetHandler(avatarPresetService, avatarPresetProjection, inventoryProjection, log);
        var weaponSkinHandler = new SkinStorageHandler(weaponSkinService, inventoryProjection, weaponSkinProjection, log);
        // GM 团本时段：入城投影与 GM 网页共用同一实例，网页写入后下一次入城/换频道即按新表下发 EVENT_INFO。
        var raidWindows = new Game.Raids.RaidEntryStore(options.DataDirectory);
        projection.RaidWindows = raidWindows;
        var rentalHandler = new RentalHandler(new RentalService(itemResources.Rental, itemResources.Items, towns, clock), inventoryProjection, log);
        var creatureHandler = new CreatureHandler(creatures, creatureProjection, log, inventoryProjection);
        // 建会认证的跨连接投递：被邀请方连接收 0x2AD/0x2AF，创建者连接收金币差额与 0x2AE/0x47/0x3A。
        var guildDungeonProjection = new GuildDungeonProjection(guildDungeons);
        var guildPanels = new Game.Guild.GuildPanelService(repository, online);
        var guildHongbao = new Game.Guild.GuildHongbaoService(repository, itemResources.GuildHongbao, clock);
        var guildRanking = new Game.Guild.GuildRankingService(repository,
            new GameWorld.Guilds.GuildRankingCatalog(pvf), guildDungeons, pvpRatingCatalog, clock);
        var guildProjection = new GuildProjection(inventoryProjection, presence, guildPanels);
        var guildInvitations = new Game.Guild.GuildInvitations();
        var guildManagement = new Game.Guild.GuildManagementService(repository, online, guildInvitations, guildPermits);
        var guildApplications = new Game.Guild.GuildApplications(repository, guildManagement, clock, guildRanking);
        var guildManagementProjection = new GuildManagementProjection(guildManagement, guildPanels, presence) { Dungeons = guildDungeonProjection };
        var guildCargoService = new Game.Guild.GuildCargoService(repository, itemResources.Items, itemResources.GuildCargo, online,
            guildParameters.CargoSizeLimit, clock);
        var guildCargoProjection = new GuildCargoProjection(guildCargoService, inventoryProjection);
        var guildContents = itemResources.GuildContents;
        var guildEconomyService = new Game.Guild.GuildEconomyService(repository, guildContents, itemResources.Items, guildManagement, clock,
            state =>
            {
                foreach (var member in state.Members)
                    online.Find(member.Account)?.TryDeliverGuildCargo(member.Id, new(state.GuildId));
            });
        var guildContributionService = new Game.Guild.GuildContributionService(repository,
            itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, guildManagement, clock);
        var guildContribution = new GuildContributionProjection(guildContributionService, inventoryProjection, log, guildHongbao);
        var guildHallWait = new Game.Guild.GuildHallWaitService(repository,
            new GameWorld.Guilds.GuildHallWaitCatalog(pvf), online, guildContributionService, clock,
            error => log.Write("guild_hall_contribution_failed", new { Error = error.Message }), guildRanking);
        var guildNpcCatalog = new GameWorld.Guilds.GuildNpcCatalog(pvf);
        var guildNpcBenefits = new GameWorld.Guilds.GuildNpcBenefitCatalog(pvf);
        var guildNpcBenefitService = new Game.Guild.GuildNpcBenefitService(repository, guildNpcCatalog,
            guildNpcBenefits, itemResources.EpicPieces, guildManagement, clock, itemResources.Items, mailboxProjection.Arrived);
        var guildNpcService = new Game.Guild.GuildNpcService(repository, guildNpcCatalog,
            guildParameters, guildManagement, error => log.Write("guild_npc_completion_failed", new { Error = error.Message }), clock,
            crafts: new GameWorld.Guilds.GuildNpcCraftCatalog(pvf), benefits: guildNpcBenefits,
            completeItems: guildNpcBenefitService.CompleteItemCrafts);
        var guildEconomyProjection = new GuildEconomyProjection(guildEconomyService,
            error => log.Write("guild_benefit_expiry_failed", new { Error = error.Message }), guildNpcService);
        var guildEconomyHandler = new GuildEconomyHandler(guildEconomyService, guildEconomyProjection, inventoryProjection, log);
        var guildNpcProjection = new GuildNpcProjection(guildNpcService, guildNpcBenefitService);
        var guildExperienceHandler = new GuildExperienceHandler(new Game.Guild.GuildExperienceService(repository,
            itemResources.Items, guildParameters, guildManagement, clock), inventoryProjection, log);
        var guildTeleportService = new Game.Guild.GuildTeleportService(repository, parties, towns, guildManagement, clock,
            error => log.Write("guild_teleport_failed", new { Error = error.Message }));
        var guildTeleportHandler = new GuildTeleportHandler(guildTeleportService, projection, log);
        projection.GuildPanels = guildPanels;
        projection.GuildDungeons = guildDungeonProjection;
        projection.GuildEconomy = guildEconomyProjection;
        projection.GuildNpcs = guildNpcProjection;
        guildManagementProjection.AccountRefresh = context =>
        {
            if (context.Session.CaptureGuildManagement(requireTown: false) is not { } actor) return;
            var account = guildApplications.Account(actor);
            context.Sender.Post(1, (ushort)CmdPacketType.REQUEST_ACCOUNT_GUILD_LIST,
                Network.Builders.GuildApplicationPackets.AccountIndex(account), "guild_account_index", actor.IsCurrent);
            context.Sender.Post(1, (ushort)CmdPacketType.REQ_GUILD_INFO_OF_MY_CHARS,
                Network.Builders.GuildApplicationPackets.AccountApplications(account), "guild_account_applications", actor.IsCurrent);
        };
        var cashShop = new InventoryCashShopService(itemResources.Items, itemResources.CashShop, itemResources.GoldLimits, itemResources.EpicPieces, itemResources.Cargo,
            debugSettings, itemResources.Contracts, itemResources.AvatarPackages, devil, itemResources.GuildHongbao, serverItemRules, events, clock);
        var levelProjection = new CharacterLevelProjection(stats, questProjection);
        var levelUpProjection = new LevelUpTicketProjection(inventoryProjection,levelProjection);
        var questEffects=new Game.Quests.QuestSkipEffects(itemResources.Items,learning,itemResources.GoldLimits,itemResources.EpicPieces);
        var commands = new GameCommandRegistry();
        var tagEvildomProjection = new TagEvildomProjection(repository, stats, log, clock, learning, projection, itemResources.Items);
        var imperialArenaProjection = new ImperialArenaProjection(repository, repository, repository, new ImperialArenaCatalog(pvf, itemResources.Items),
            stats, log, learning, itemResources.Items, projection, clock);
        new ImperialArenaHandler(imperialArenaProjection, log).Register(commands);
        new TagEvildomHandler(tagEvildomProjection, projection, log).Register(commands);
        var tagEvildomRun = new TagEvildomRunProjection(new TagEvildomRewardCatalog(pvf, itemResources.Boosters),
            itemResources.Items, repository, tagEvildomProjection, log);
        string raidSettingsPath = Path.Combine(options.DataDirectory, "raid-start.json");
        var raidSettings = File.Exists(raidSettingsPath)
            ? System.Text.Json.JsonSerializer.Deserialize<Game.Raids.RaidStartSettings>(File.ReadAllText(raidSettingsPath),
                new System.Text.Json.JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidDataException("攻坚开战配置不能为空")
            : new Game.Raids.RaidStartSettings();
        // 仅显式Luke Run策略装配静态对象证据；与正式副本共用同一房间目录，不增加网络写入口。
        var lukeObjectRooms = lukeStartPolicy?.Run is null ? null : DungeonRoomCatalog.Load(pvf);
        var raidCatalog = raidSettings.Apply(GameWorld.Raids.RaidCatalog.Load(pvf)).PrepareResources(pvf, dungeonCatalog, itemResources.Items,
            lukeObjectRooms, lukeStartPolicy?.Run is { Mazes.IsDefaultOrEmpty: false } objectPolicy
                ? objectPolicy.Mazes.Where(row => row.Phase == 0).Select(row => (row.Dungeon, row.MazeOrdinal)).ToArray() : null,
            antonMapPath: GameWorld.Raids.AntonRaidMapPaths.Bind(dungeonCatalog.MapPath),
            antonMapPoolPaths: GameWorld.Raids.AntonRaidMapPaths.Paths,
            lukeResourceChoices: lukeStartPolicy?.Run?.FixedRouteOptions is { } routeOptions
                ? entry => Game.Raids.LukeFixedCardRoute.ResourceChoices(routeOptions, entry) : null);
        if (raidCatalog.Find(1)?.Resources?.LukeObjects is { Issues.IsEmpty: false } objectEvidence)
            log.Write("resource_warning", new { Source = "Luke static object evidence", Reasons = objectEvidence.Issues.ToArray(), objectEvidence.RoomFailures, objectEvidence.RouteWarnings, objectEvidence.BlockingRouteIssues });
        if (raidCatalog.Find(1)?.Resources?.LukeCardSet is { Issues.IsEmpty: false } cardSetEvidence)
            log.Write("resource_warning", new { Source = "Luke cardset metadata", Reasons = cardSetEvidence.Issues.ToArray() });
        if (lukeStartPolicy?.Run?.FixedRouteOptions is { } diagnosticOptions && raidCatalog.Find(1)?.Resources?.EntryCatalog is { } diagnosticCatalog)
            log.Write("startup", new { Stage = "Luke fixed route resource inspection", State = "diagnostic-only",
                Mazes = Game.Raids.LukeFixedCardRoute.InspectResources(diagnosticOptions, diagnosticCatalog, raidCatalog.Find(1)!.Resources!.LukeObjects) });
        lukeStartPolicy = Game.Raids.LukeFixedCardRoute.Resolve(lukeStartPolicy, raidCatalog.Find(1));
        if (lukeStartPolicy?.Run?.FixedRoute is { } fixedRoute)
            log.Write("startup", new { Stage = "Luke explicit fixed compatibility route", State = "complete",
                fixedRoute.Options.Version, fixedRoute.Options.ResourceFingerprint, fixedRoute.Options.Group,
                fixedRoute.Options.ColumnDungeons, fixedRoute.Options.StepOffsets, fixedRoute.Options.InitialHp,
                Failure = fixedRoute.Options.Failure.ToString(), Reset = fixedRoute.Options.Reset.ToString(), fixedRoute.Choices });
        log.Write("startup", new { Stage = "Raid start settings", State = "complete", raidSettings.StartMinimumOverride,
            raidSettings.AntonFormalEntryEnabled, raidSettings.AntonPhaseProgressEnabled, raidSettings.AntonRejoinEnabled,
            Reason = raidSettings.AntonFormalEntryEnabled ? "anton-entry-enabled-resource-gates-required" : "anton-entry-waiting-client-resources",
            PhaseProgress = raidSettings.AntonPhaseProgressEnabled ? "anton-phase-progress-enabled-local-seconds" : "anton-phase-progress-disabled",
            Rejoin = raidSettings.AntonRejoinEnabled ? "rejoin-recovery-sequence-unknown" : "anton-rejoin-disabled",
            AntonStartMinimum = raidCatalog.Find(0)?.StartMinimum, LukeStartMinimum = raidCatalog.Find(1)?.StartMinimum });
        lukeStartPolicy = Game.Raids.LukeResourceCompletion.Resolve(lukeStartPolicy, raidCatalog.Find(1), itemResources.Items);
        if (lukeStartPolicy?.ResourceCompletion is not null && lukeStartPolicy.Completion is { } resourceCompletion)
            log.Write("startup", new { Stage = "Luke resource completion policy", State = "complete", resourceCompletion.Source,
                resourceCompletion.WeeklyClearLimit, resourceCompletion.Cycle,
                Phases = resourceCompletion.Phases.Select((phase, index) => new { Phase = index, phase.MinimumCommittedRuns,
                    phase.WeeklyEligibilityLimit, phase.Rewards.Offers, phase.Rewards.Result }).ToArray() });
        var raidRegistry = new Game.Raids.RaidRegistry(clock);
        var antonRejoin = new Game.Raids.AntonRaidRejoin(raidSettings.AntonRejoinEnabled, lukeCandidatesEnabled: lukeStartPolicy is not null);
        raidRegistry.BindRejoin(antonRejoin);
        var antonRun = raidCatalog.Find(0)?.Resources?.EntryCatalog is { } antonCatalog
            ? new Game.Raids.AntonRaidDungeonService(raidRegistry, parties, antonCatalog, raidSettings.AntonFormalEntryEnabled) : null;
        if (!raidSettings.AntonFormalEntryEnabled)
            log.Write("resource_warning", new { Source = "Anton formal entry", Reason = "anton-entry-waiting-client-resources" });
        var lukeTimers = !raidSettings.AntonPhaseProgressEnabled && lukeStartPolicy?.Timers is null && lukeStartPolicy?.PhaseTimeout is null ? null : new Game.Raids.LukeRaidClocks(clock ?? TimeProvider.System,
            repository, (stage, error) => log.Write("raid_timer_failed", new { Stage = stage, Reason = error.GetType().Name, error.Message }));
        var antonRewards = raidSettings.AntonPhaseProgressEnabled && raidCatalog.Find(0)?.Resources?.Script is { } antonScript
            ? new Game.Raids.AntonResourceRewards(antonScript, itemResources.Items) : null;
        // 启动时解析全部奖励模板，避免开战持owner锁时首次读取PVF。
        if (antonRewards is not null) _ = antonRewards.Create(false);
        var lukeStart = lukeStartPolicy is null ? null : new Game.Raids.LukeRaidStart(raidRegistry, parties, repository, towns, itemResources.EpicPieces, lukeStartPolicy, clock, itemResources.Items, lukeTimers);
        if (lukeStart?.UnavailableReason(raidCatalog.Find(1)) is { } unavailable)
        {
            log.Write("resource_warning", new { Source = "Luke raid start policy", Reason = unavailable });
            lukeStart = null;
        }
        // 只捕获仍存活实例的断线身份，不启用任何Luke重返通知或成功应答。
        var lukeRejoin = lukeStart is null ? null : new Game.Raids.LukeRaidRejoin(raidRegistry, parties, antonRejoin);
        var lukeRun = lukeStart is not null && lukeStartPolicy?.Run is { } runPolicy &&
            raidCatalog.Find(1)?.Resources?.EntryCatalog is { } runCatalog
            ? new Game.Raids.LukeRaidDungeonService(raidRegistry, parties, runCatalog, runPolicy, raidCatalog.Find(1)?.Resources?.LukeObjects) : null;
        if (lukeRun?.UnavailableReason is { } runUnavailable)
        {
            log.Write("resource_warning", new { Source = "Luke raid run policy", Reason = runUnavailable });
            lukeRun = null;
        }
        var raidHandler = new RaidHandler(log, raidCatalog, raidRegistry, online,
            new Game.Raids.AntonRaidStart(raidRegistry, parties, repository, towns, itemResources.EpicPieces, clock, raidSettings.AntonPhaseProgressEnabled, antonRewards, raidSettings.AntonPhaseProgressEnabled ? lukeTimers : null), lukeStart, antonRejoin, parties);
        if (raidSettings.AntonRejoinEnabled)
            log.Write("resource_warning", new { Source = "Anton raid rejoin", Reason = "rejoin-recovery-sequence-unknown" });
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
        Game.Dungeons.PartyDungeonService? partyDungeons = null;
        EvildomWarCatalog? warCatalog = null;
        Game.Dungeons.EvildomWarRewardPolicy? warPolicy = null;
        IGameCommandModule? settlement = null;
        IGameCommandModule? questReplay = null;
        Func<GameSession, IGameConnectionActivity>? createActivity = null;
        DungeonPassiveDropPolicy? passiveDrops = null;
        EpicPieceShopService? epicPieceShop = null;
        AssaultModeCatalog? assaultMode = null;
        OperationHopeCatalog? operationHope = null;
        if (options.DevelopmentDungeonEntryProbe == DungeonEntryProbeMode.Disabled)
        {
            selectionDungeons = dungeonCatalog;
            assaultMode = StartupStage.Run(log, "Assault mode catalog", () => AssaultModeCatalog.Load(pvf, dungeonCatalog));
            var rooms = StartupStage.Run(log, "Dungeon room catalog", () => lukeObjectRooms ?? DungeonRoomCatalog.Load(pvf));
            operationHope = StartupStage.Run(log, "Operation Hope catalog", () => OperationHopeCatalog.Load(pvf,dungeonCatalog,quests,rooms));
            tutorialScenes ??= new TutorialScenes(pvf, tutorials, dungeonCatalog);
            questRooms = rooms;
            var drops = StartupStage.Run(log, "Dungeon monster drops", () => DungeonMonsterDropCatalog.Load(pvf));
            var dropRules = StartupStage.Run(log, "Dungeon drop rules", () => DungeonDropRuleCatalog.Load(pvf));
            var dropCandidates = StartupStage.Run(log, "Dungeon item candidates", () => itemResources.DungeonDropItems);
            var specialDrops = StartupStage.Run(log, "Dungeon special drops", () => itemResources.DungeonSpecialDrops);
            var amplificationDrops = new EquipmentAmplificationDropPolicy(itemResources.Amplifications.Rules);
            string? configuredMaterials = Environment.GetEnvironmentVariable("DFO_HARLEM_MATERIAL_RULES");
            var materialRules = HarlemMaterialDropRules.Parse(configuredMaterials ?? "{}");
            var harlemDungeons = towns.DungeonMaps.Values.Where(map => map.HellDungeon && map.HellQuests.Contains(6080))
                .SelectMany(map => map.Options).Select(option => checked((uint)option.DungeonId)).ToArray();
            var hellEpicTable = StartupStage.Run(log, "Hell epic catalog", () => DungeonHellEpicCatalog.Load(pvf));
            string? configuredEpics = Environment.GetEnvironmentVariable("DFO_HARLEM_HELL_EPIC_RULES");
            var epicRules = HarlemHellEpicRules.Parse(configuredEpics ?? "{}");
            var cosmoRewards = StartupStage.Run(log, "Cosmo reward catalog", () => DungeonCosmoRewardCatalog.Load(pvf, dungeonCatalog, rooms));
            var harlemEpics = StartupStage.Run(log, "Harlem epic pools", () => new HarlemHellEpicDropPolicy(harlemDungeons,
                hellEpicTable.Pool(HarlemHellEpicDropPolicy.Area, HarlemHellEpicDropPolicy.NormalList, itemResources.Items.Get),
                hellEpicTable.Pool(HarlemHellEpicDropPolicy.Area, HarlemHellEpicDropPolicy.WeaponList, itemResources.Items.Get),
                hellEpicTable.Pool(HarlemHellEpicDropPolicy.Area, HarlemHellEpicDropPolicy.TayberrsList, itemResources.Items.Get),
                hellEpicTable.Pool(HarlemHellEpicDropPolicy.Area, HarlemHellEpicDropPolicy.CosmoList, itemResources.Items.Get),
                epicRules, cosmoRewards,
                hellEpicTable.Pool(HarlemHellEpicDropPolicy.Area, HarlemHellEpicDropPolicy.SuperList, itemResources.Items.Get)));
            var materialDrops = new HarlemMaterialDropPolicy(materialRules, harlemDungeons, itemResources.Items.Get, harlemEpics);
            string? configuredBonuses = Environment.GetEnvironmentVariable("DFO_HARLEM_ABYSS_BONUS_RULES");
            var bonusRules = HarlemAbyssBonusRules.Parse(configuredBonuses ?? "{}");
            var abyssBonuses = new HarlemAbyssBonusDropPolicy(bonusRules, harlemEpics, itemResources.Items.Get);
            log.Write("startup", new { Stage = "Harlem material rewards", State = "complete", Rule = HarlemMaterialDropPolicy.Rule,
                materialRules.Enabled, Configured = configuredMaterials is not null });
            var hopeRewards = new OperationHopeRewardPolicy(new OperationHopeRewardCatalog(pvf,itemResources.Items),itemResources.Items);
            var dimensionRewards = new DimensionRewardPolicy(incounts?.Dimension ?? throw new InvalidDataException("Missing dimension dungeon catalog"),
                new DimensionRewardCatalog(pvf, itemResources.Items), itemResources.Items);
            log.Write("startup",new { Stage="Operation Hope rewards",State="complete",Rule=OperationHopeRewardPolicy.Rule });
            var resourceDrops = new DungeonResourceDropPolicy(dropRules, dropCandidates, drops.Get, itemResources.Items.Get,
                specialDrops, amplificationDrops, materialDrops, harlemEpics, abyssBonuses,
                new SleepyHollowMaterialDropPolicy(SleepyHollowRewardCatalog.Load(dungeonCatalog, rooms), itemResources.Items.Get),hopeRewards,dimensionRewards);
            passiveDrops = new(dungeonCatalog.Get, itemResources.Items.Get, DungeonDropRuleCatalog.LoadObjects(pvf), dropCandidates, amplificationDrops);
            int epicPermille = 1000;
            string? fragmentRate = Environment.GetEnvironmentVariable("DFO_HELL_EPIC_FRAGMENT_PERMILLE");
            if (fragmentRate is not null && (!int.TryParse(fragmentRate, out epicPermille) || epicPermille is < 0 or > 1000))
                throw new InvalidDataException("DFO_HELL_EPIC_FRAGMENT_PERMILLE 必须为0至1000，0显式关闭碎片掉落");
            var epicPool = epicPermille > 0 ? itemResources.EpicPieceDrops : null;
            if (itemResources.EpicPieceShop is { } shopRules)
            {
                string? appearance = Environment.GetEnvironmentVariable("DFO_EPIC_PIECE_SHOP_APPEAR_PERCENT");
                if (appearance is not null)
                {
                    if (!int.TryParse(appearance, out int percent) || percent is < 0 or > 100)
                        throw new InvalidDataException("DFO_EPIC_PIECE_SHOP_APPEAR_PERCENT 必须为0至100");
                    shopRules = shopRules with { AppearancePercent = percent };
                }
                epicPieceShop = new(shopRules, itemResources.EpicPieceDrops, itemResources.EpicPieces, harlemDungeons);
                log.Write("startup", new { Stage = "Epic piece shop", State = "complete", shopRules.NpcId,
                    shopRules.AppearancePercent, Override = appearance is not null });
            }
            var hellRewards = DungeonHellDropRules.TryLoad(pvf) is { } hellDropRules ? new DungeonHellRewardPolicy(hellDropRules, dropCandidates, epicPool, epicPermille) : null;
            Func<DungeonPlannedMonster, DungeonDropGenerationPlan> PrepareDrops(DungeonEntryPlan entry) => resourceDrops.PrepareEntry(entry, hellRewards);
            log.Write("dungeon_drop_policy", new { Rule = DungeonResourceDropPolicy.Rule,
                EquipmentWeights = "current_pvf_creation_rate", Retained = DungeonResourceDropPolicy.RetainedFields,
                SleepyHollowMaterialRule = SleepyHollowMaterialDropPolicy.Rule,
                FifthProbabilityRows = dropRules.ProbabilityRows.Count(row => row.Values[4] != 0),
                HellEpicRule = HarlemHellEpicDropPolicy.Rule, HellEpicSource = DungeonHellEpicCatalog.Path,
                HellEpicArea = HarlemHellEpicDropPolicy.Area, NormalEpicList = HarlemHellEpicDropPolicy.NormalList,
                LegendEpicList = HarlemHellEpicDropPolicy.NormalList, LegendRareRule = HarlemHellEpicDropPolicy.RareRule,
                EpicBranches = epicRules, EpicBranchesConfigured = configuredEpics is not null,
                AbyssBonusRule = HarlemAbyssBonusDropPolicy.Rule, AbyssBonusRules = bonusRules, AbyssBonusConfigured = configuredBonuses is not null,
                WeaponCandidates = harlemEpics.Weapons!.Entries.Length, TayberrsCandidates = harlemEpics.Tayberrs!.Entries.Length,
                SuperCandidates = harlemEpics.Super!.Entries.Length, CosmoCandidates = harlemEpics.Cosmo!.Entries.Length, CosmoRewardRolls = cosmoRewards.RewardRolls,
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
            partyDungeons = new(parties, repository, repository, hiddenDraw: evildomHiddenDraw, guideStore: repository);
            warCatalog = EvildomWarCatalog.Load(pvf,dungeonCatalog);
            warPolicy = new(warCatalog,itemResources.Items,itemResources.Boosters);
            dungeon = new DungeonHandler(log, dungeonCatalog, rooms,
                growth, DungeonKillExperienceCatalog.Load(pvf), projection, levelProjection,
                drops, itemResources.Items, itemResources.EpicPieces, itemResources.GoldLimits, questProjection, PrepareDrops,tutorials,tutorialScenes,crackRewards,altarRewards,
                despairRewards,titleService,titleProjection,inventoryProjection,partyDungeons,stats,parties,epicPieceShop,tagEvildomRun,
                evildomWar: warCatalog, lukeRaid: lukeRun, lukeLifecycle: repository, antonRaid: antonRun,
                evildomRewards: warPolicy, evildomRewardStore: repository, imperialArena: imperialArenaProjection);
            settlement = new DungeonSettlementHandler(log, dungeonCatalog, DungeonClearExperienceCatalog.Load(pvf), growth, projection,
                rooms, drops, itemResources.Items, levelProjection, itemResources.EpicPieces,
                new Game.Dungeons.DungeonCardRewardPolicy(dropRules, dropCandidates, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces,
                prices: DungeonCardPriceCatalog.Load(pvf),hopeRewards:hopeRewards,dimensionRewards:dimensionRewards), inventoryProjection, itemResources.Rental, events, adventureCatalog, partyDungeons, guildEconomyService, guildContributionService, goldLimits: itemResources.GoldLimits,
                imperialArena: imperialArenaProjection, parties: parties);
            questReplay = new QuestReplayHandler(questConnections,dungeonCatalog,rooms,log,drops,itemResources.Items,itemResources.EpicPieces,PrepareDrops,crackRewards);
        }
        else
        {
            var catalog = dungeonCatalog;
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
        var npcFavor = new NpcFavorCatalog(pvf);
        IGameCommandModule[] modules =
        [
            new SessionHandler(projection, log),
            new NpcFavorHandler(new NpcFavorService(npcFavor, towns, clock), inventoryProjection, log),
            // 客户端保活：0x04FE/0x06BB 每 10 秒一次，服务端必须回同 opcode 帧（见 KeepaliveHandler）。
            new KeepaliveHandler(log),
            new PvpArenaHandler(arena, new PvpTrainingHandler(new GameWorld.Pvp.PvpArcadeCatalog(pvf),new GameWorld.Pvp.PvpPracticeComboCatalog(pvf),quests,log,itemResources.Items,itemResources.GoldLimits,itemResources.EpicPieces,learning,pvpMissionBook,pvpMissions), log, clock),
            new PvpTotalTeamHandler(totalTeams, log, inventoryProjection, totalTeamProjection),
            new PvpMatchingHandler(arena, log, clock),
            new PvpChannelHandler(gameChannels, log),
            new PvpRecordHandler(new Game.Pvp.PvpRecordQuery(repository, repository), online, log),
            new CharacterHandler(characters, projection, log, itemResources.GoldLimits, learning, tutorials, adventureProjection, expertJobs,
                adventureWeeklyProjection, avatarPresetProjection, avatarPresetService, online, socialProjection, privateCharacters,
                guildContribution, tagEvildomProjection, clock, arena, totalTeams, repository, partyDungeons, warCatalog, warPolicy, imperialArenaProjection, raidHandler, totalTeamProjection, pvpMissions, pvpMissionBook, itemResources.Items),
            new SocialHandler(repository, social, online, log),
            new PlayerInteractionHandler(parties, online, log, partyDungeons, raidHandler, projection, arena, tradeHandler),
            tradeHandler,
            new AdventureHandler(adventureProjection, adventureRepresentatives, expeditionProjection, clock, online),
            new AdventureExpeditionHandler(expeditionService, expeditionProjection, clock),
            new AdventureNameHandler(new Game.Adventure.AdventureNameService(adventureCatalog, itemResources.Items.AdventureNameTicket),
                adventureProjection, inventoryProjection, projection, clock),
            new TutorialHandler(tutorials, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, inventoryProjection),
            new AdventureShopHandler(new Game.Adventure.AdventureShopService(adventureCatalog, itemResources.Items,
                itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection, clock),
            new AdventureCapsuleHandler(new Game.Adventure.AdventureCapsuleService(adventureCatalog, growth), levelProjection, clock),
            new AdventureWeeklyHandler(adventureWeeklyProjection),
            new SkillHandler(learning, log, inventoryProjection),
            new SkillSwitchInventoryHandler(skillSwitchInventory, log),
            new TownHandler(projection, log, tagEvildomProjection),
            new TeleportHandler(new Game.Town.TeleportService(towns, itemResources.Teleports, itemResources.Items.Get, clock,
                options.TeleportPotionGates, guildParameters.TeleportCost), projection, inventoryProjection, presence, log, guildTeleportHandler),
            guildTeleportHandler,
            new SettingsHandler(settings, log, projection),
            avatarPresetHandler,
            new MailboxHandler(mailbox, mailboxProjection, inventoryProjection),
            new TitleHandler(titleService, new Game.Titles.TitleBookService(titleCatalog, itemResources.Items, itemResources.Contracts), titleProjection, inventoryProjection, online, settings, log),
            new CollectBoxHandler(new Game.CollectBox.CollectBoxService(itemResources.CollectBoxes, itemResources.Items,
                itemResources.EpicPieces), collectBoxProjection, inventoryProjection, clock),
            new EpicProductionHandler(epicProduction,epicAbilities,epicPolicy,itemResources.Items,itemResources.GoldLimits,itemResources.EpicPieces,
                epicProductionProjection,inventoryProjection,clock),
            new InventoryHandler(new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, itemResources.Contracts, clock), inventoryProjection, log, tradeHandler),
            new CubeContractHandler(new CubeContractService(itemResources.Contracts), inventoryProjection, log, clock),
            new ConsumableUseHandler(new InventoryConsumableUseService(itemResources.RecoveryConsumables, clock), log, tutorials, itemResources.Items, inventoryProjection,
                new InventoryPremiumContractUseService(itemResources.Items, itemResources.Contracts, clock), creatures, creatureProjection, projection,
                new Game.ExpertJob.ExpertJobLearning(expertJobs, itemResources.Items, clock), expertJobs),
            weaponSkinHandler,
            rentalHandler,
            new CurrencyHandler(new Game.Currency.CurrencyService(itemResources.GoldLimits), inventoryProjection),
            new InventoryExpansionHandler(new InventoryExpansionService(itemResources.Items, clock), inventoryProjection, log),
            new CargoHandler(new InventoryCargoService(itemResources.Items, itemResources.Cargo, itemResources.GoldLimits, clock), inventoryProjection),
            new ShopHandler(
                new InventoryShopService(itemResources.Items, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces, clock, serverItemRules),
                cashShop,
                inventoryProjection, log,
                new Game.Marriage.MarriageShopService(itemResources.Items, itemResources.Shops,
                    itemResources.GoldLimits, itemResources.EpicPieces, marriageCatalog, clock), marriageProjection, promotions: cashPromotions),
            new RepairHandler(new InventoryRepairService(itemResources.Items, itemResources.Repair, towns, clock), inventoryProjection, log),
            new RandomOptionHandler(new InventoryRandomOptionService(itemResources.Items, itemResources.RandomOptions, clock: clock), inventoryProjection, log,
                new EquipmentResealService(itemResources.Items, itemResources.EquipmentReseals, itemResources.EpicPieces, clock)),
            new TextureChangeHandler(new InventoryTextureChangeService(itemResources.Items, itemResources.TextureChanges,
                itemResources.EpicPieces, clock), inventoryProjection, log),
            new DarkRevelationHandler(new InventoryDarkRevelationService(itemResources.DarkRevelations,
                itemResources.EpicPieces, clock), itemResources.DarkRevelations, inventoryProjection, log),
            new EchonEquipmentHandler(new InventoryEchonEquipmentService(itemResources.Items, itemResources.Echon,
                itemResources.EpicPieces), inventoryProjection, log),
            new EquipmentUpgradeHandler(new InventoryEquipmentUpgradeService(itemResources.Items,
                itemResources.EquipmentUpgrades, itemResources.EpicPieces, clock), inventoryProjection, log),
            new EquipmentTransmissionHandler(new InventoryEquipmentTransmissionService(itemResources.Items,
                itemResources.EquipmentTransmissions, itemResources.EpicPieces, clock), inventoryProjection, log),
            new LimitCubeHandler(new InventoryLimitCubeService(itemResources.Items, itemResources.LimitCubes,
                itemResources.EpicPieces, itemResources.Contracts, itemResources.Boosters.DirectActivationItems,
                debugSettings: debugSettings, attributePairs: attributePairs), inventoryProjection, log),
            new EmblemCompoundHandler(new InventoryEmblemCompoundService(itemResources.Items,
                itemResources.EmblemCompounds, itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection, log, guildNpcBenefitService),
            new MonsterCardBindHandler(new InventoryMonsterCardBindService(itemResources.Items,
                itemResources.MonsterCardBinds, itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection, log),
            new MonsterCardUpgradeHandler(new InventoryMonsterCardUpgradeService(itemResources.Items, itemResources.Enchantments,
                itemResources.MonsterCardBinds, itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection, log),
            new GuildCompoundHandler(new InventoryGuildCompoundService(itemResources.Items,
                itemResources.GuildCompounds, itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection, log),
            new AvatarCompoundHandler(new InventoryAvatarCompoundService(itemResources.Items,
                itemResources.AvatarCompounds, itemResources.AvatarPackages), inventoryProjection, log),
            new AvatarDyeHandler(new InventoryAvatarDyeService(itemResources.Items, itemResources.AvatarDyes), inventoryProjection, log),
            new AvatarAbilityChangeHandler(new InventoryAvatarAbilityChangeService(itemResources.Items, itemResources.AvatarPackages, clock), inventoryProjection, log),
            new AvatarSocketHandler(new InventoryAvatarSocketService(itemResources.Items, itemResources.AvatarPackages, clock), inventoryProjection, log),
            new RefinementHandler(new InventoryRefinementService(itemResources.Items, itemResources.Refinement, clock: clock), inventoryProjection,
                guildBenefits: guildNpcBenefitService),
            new EnchantmentHandler(enchantmentService, inventoryProjection, log),
            new EquipmentEmblemHandler(new InventoryEquipmentEmblemService(itemResources.Items, clock), inventoryProjection, log),
            new TalismanHandler(new InventoryTalismanService(itemResources.Items),
                new InventoryTalismanCompoundService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces),
                itemResources.Items, inventoryProjection, log, clock),
            new EnhancementHandler(new InventoryEnhancementService(itemResources.Items, itemResources.Enhancements,
                itemResources.MedalUpgrades, clock: clock, consumables: itemResources.RecoveryConsumables,
                amplification: new InventoryAmplifyUpgradeService(itemResources.Items, itemResources.AmplifyUpgrades, itemResources.Enhancements,
                    itemResources.Disjoint, itemResources.GoldLimits, itemResources.EpicPieces, towns, clock: clock,
                    consumables: itemResources.RecoveryConsumables)), inventoryProjection, log,
                guildNpcBenefitService),
            new AmplificationHandler(new InventoryAmplificationService(itemResources.Items, itemResources.Amplifications,
                itemResources.Enhancements, towns, clock), inventoryProjection, log),
            new CreatureSkinHandler(skinService, inventoryProjection, log),
            new CreatureEggHandler(new InventoryCreatureEggService(itemResources.CreatureEggs, itemResources.Items, clock), inventoryProjection, log),
            creatureHandler,
            new DisjointHandler(new InventoryDisjointService(itemResources.Items, itemResources.Disjoint, itemResources.GoldLimits, itemResources.EpicPieces, expertJobs: expertJobs, clock: clock), inventoryProjection),
            new ExpertJobHandler(quests, expertJobs, itemResources.Items,
                itemResources.GoldLimits, itemResources.EpicPieces, inventoryProjection, log, presence, expertStores, enchantmentService, machineDisassembly,
                new Game.ExpertJob.ExpertJobEnchanting(expertJobs, itemResources.Items, itemResources.Enchantments, expertPolicy, clock), online),
            new AvatarDisjointHandler(new InventoryAvatarDisjointService(itemResources.Items, itemResources.AvatarDisjoint,
                itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection, log),
            new EpicBookHandler(new EpicPieceCraftService(itemResources.Items, itemResources.EpicPieces, itemResources.GoldLimits), inventoryProjection),
            // 原生史诗交换：CMD1569，配置 etc/epicitemexchange.etc，回执同 opcode 0x621。
            new EpicExchangeHandler(new EpicExchangeService(itemResources.EpicExchanges, itemResources.Items,
                itemResources.EpicPieces, itemResources.GoldLimits), inventoryProjection),
            new ChronicleEnchantHandler(new InventoryChronicleEnchantService(itemResources.Items, itemResources.ChronicleEnchants,
                itemResources.Disjoint, itemResources.AmplifyUpgrades, itemResources.GoldLimits, itemResources.EpicPieces, clock: clock), inventoryProjection, log),
            // 设计图升级：CMD271，材料与产物来自设计图自身的 [emancipate group]。
            new UpgradeChronicleHandler(new InventoryUpgradeChronicleService(itemResources.UpgradeChronicles, itemResources.Items,
                itemResources.EpicPieces, clock), inventoryProjection, itemResources.Items),
            // 设计图制造：CMD25，配方来自 [recipe] 设计图的 [input item]/[output item]。
            new CompoundHandler(new InventoryRecipeCraftService(itemResources.ItemRecipes, itemResources.Items,
                itemResources.EpicPieces, itemResources.GoldLimits, itemResources.ItemMakingSkills, clock), inventoryProjection, log),
            new BoosterHandler(new InventoryBoosterService(itemResources.Items, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces,
                debugSettings: debugSettings, contracts: itemResources.Contracts, avatars: itemResources.AvatarPackages, clock: clock, randomOptions: itemResources.RandomOptions,
                attributePairs: attributePairs), inventoryProjection,
                new InventoryAvatarPackageService(itemResources.Items, itemResources.AvatarPackages, itemResources.GoldLimits, itemResources.EpicPieces, debugSettings, clock), log, clock),
            new VendingHandler(new InventoryVendingService(itemResources.VendingMachines, itemResources.Items, itemResources.GoldLimits,
                itemResources.EpicPieces, itemResources.Contracts, clock), inventoryProjection, log),
            new MagicBoxHandler(new InventoryMagicBoxService(itemResources.Items, itemResources.MagicBoxes, itemResources.GoldLimits, itemResources.EpicPieces,
                contracts: itemResources.Contracts, debugSettings: debugSettings, clock: clock), inventoryProjection),
            new DungeonSelectionHandler(projection, log, tutorials, selectionDungeons, partyDungeons, tagEvildomProjection, tagEvildomRun, lukeRun, antonRun, imperialArenaProjection),
            new OperationHopeHandler(operationHope,selectionDungeons,log),
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
                new InventoryEquipmentPeriodChangeService(itemResources.Items, towns, clock), avatarPresetHandler, guildEconomyHandler, guildExperienceHandler,
                new InventoryNpcFavorService(itemResources.Items.NpcFavorItem, npcFavor, towns, clock), marriageHandler,
                new DimensionEntryTicketService(itemResources.Items, clock),
                incounts is null ? null : new DungeonIncountTicketHandler(new Game.Dungeons.DungeonIncountTicketService(itemResources.Items, incounts, towns, clock), repository, inventoryProjection, log)),
            new CreatureTransportHandler(new Game.Creatures.CreatureTransportService(itemResources.Items.Get,
                itemResources.Items.ActionConsumable, itemResources.Creatures, repository, () => creatureTransportMailPolicy, clock), inventoryProjection, log,
                new EquipmentTransportService(itemResources.Items.Get, itemResources.Items.ActionConsumable,
                    itemResources.EquipmentTransports, itemResources.EpicPieces, repository, clock)),
            new QuestConditionHandler(log, quests, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, questRooms, questParameters, growth),
            new QuestCompletionHandler(quests, questParameters, itemResources.Items,
                itemResources.GoldLimits, itemResources.EpicPieces, growth, log, levelProjection, questProjection, creatureProjection, expertJobs),
            new ClassChangeHandler(classChange, levelProjection, inventoryProjection, questProjection, projection, log, clock),
            new GrowUpChangeHandler(new Game.Characters.GrowUpChangeService(GrowUpChangeCatalog.Load(pvf), classChange),
                levelProjection, inventoryProjection, questProjection, projection, log, clock),
            dungeon,
            marriageHandler,
            new AgitWarHandler(),
            raidHandler,
            new RaidCombatHandler(repository, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, inventoryProjection, log, clock, repository),
            // 公会：创建窗口的两次查重（CMD 0x9C / 0x9D）、介绍校验（0x2E3）与建会认证链路（0x2E5 / 0x2E6 / 0x2E7）。
            // 顺序模式下创建者可能已经下线：接受认证时用离线账号租约提交，因此这里显式注入账号 owner 表。
            new GuildHandler(new Game.Guild.GuildDirectory(repository, guildParameters), guildPermits, online, owners, log, guildProjection, guildInvitations),
            new GuildPanelHandler(guildPanels, log, guildNpcProjection, guildDungeonProjection, guildRanking),
            new GuildHongbaoHandler(guildHongbao, inventoryProjection, log),
            new GuildRankingHandler(guildRanking),
            new GuildManagementHandler(guildManagement),
            new GuildApplicationHandler(guildApplications, guildManagement, log),
            new GuildGovernanceHandler(new Game.Guild.GuildGovernanceService(repository, guildManagement, channels: channels), log),
            new GuildCargoHandler(guildCargoService, guildCargoProjection, log),
            guildEconomyHandler,
            new GuildNpcHandler(guildNpcService, guildNpcProjection, log, guildNpcBenefitService, inventoryProjection),
            new GuildAttendanceHandler(new Game.Guild.GuildAttendanceService(repository, clock)),
            new GuildAppearanceHandler(new Game.Guild.GuildAppearanceService(repository,
                new GameWorld.Guilds.GuildAppearanceCatalog(pvf), guildManagement), log),
            new AuctionHandler(itemResources.Items, auctions, inventoryProjection, auctionProbe.NextSearch, auctionProbe.NextAverage,
                auctionProbe.NextMyList, log),
            new ClientNoticeHandler(log),
            // 聊天：0x0011 入站按 CP936 解析；普通/公会/悄悄话投 0x0C（正文带 CP936 文本，客户端按长度读取），
            // 两种喇叭投已实测可见的 0x76 喇叭页签（见 Docs/模块/聊天.md）。
            // 成员/目标查询走只读存储合同（repository），在线定位与投递走 OnlineSessionDirectory，不另建在线状态。
            new ChatHandler(new Game.Chat.ChatService(repository, new Game.Chat.ChatDelivery(online), repository), inventoryProjection, log,
                new Game.Chat.NamedWhisperService(online, repository), guildContribution,
                new SelfCharacterScoreProjection(new Game.Characters.CharacterInvestmentService(itemResources.Items,
                    itemResources.CharacterInvestmentMetadata, itemResources.CollectBoxes, itemResources.RandomOptions)))
        ];
        foreach (var module in modules)
            module.Register(commands);
        settlement?.Register(commands);
        questReplay?.Register(commands);
        if (epicPieceShop is not null) new EpicPieceShopHandler(epicPieceShop, inventoryProjection, log).Register(commands);
        commands.Seal();
        log.Write("startup", new { Stage = "Command registration", State = "complete", Commands = commands.Count });
        var login = new LoginHandler(options, owners, accounts, channels, log);
        var native = new NativeChannel(options, online, log, new NpcFavorAppearanceService(npcFavor));
        var game = new GameProtocolHandler(options, login, commands, repository, towns, online, projection, log, createActivity, mailboxProjection, quests, debugSettings, adventureProjection,questEffects,itemResources.Contracts,stats,inventoryProjection,clock,guildProjection,passiveDrops,devil,creatures,creatureProjection,questStages,itemResources.Boosters,native,itemResources.Items,itemResources.GoldLimits,itemResources.EpicPieces,experienceBonuses,weaponSkinProjection,expertStores,presence,incounts: incounts,levels: levelProjection,social: social,parties: parties,operationHope: operationHope,prepareDungeon: dungeon is DungeonHandler formal ? formal.PreparePartyContext : null,guildManagement: guildManagementProjection,guildInvitations: guildInvitations,guildCargo: guildCargoProjection,guildEconomy: guildEconomyProjection,guildTeleport: guildTeleportHandler,guildTeleportService: guildTeleportService,guildNpcs: guildNpcProjection,enableLukeRaidStart: lukeStart is not null,arena: arena,guildDungeons: guildDungeons,pvpRatings: pvpRatings,tagEvildomRun: tagEvildomRun,imperialArena: imperialArenaProjection, monsterBattleLevels: monsterBattleLevels.Settings, monsterAi: monsterAi.Settings, itemCycles: serverItemCycles,lukeRejoin: lukeRejoin,guildNpcBenefits: guildNpcBenefitService, trade: tradeHandler, pvpMissions: pvpMissions, missionBook: pvpMissionBook, serverEvents: events, promotionStore: repository, consumables: itemResources.RecoveryConsumables);
        var channel = new ChannelProtocolHandler(options, gameChannels, channels, log);
        Func<CancellationToken, Task>? gmCommands = options.GmPort == 0 ? null : stop => DevelopmentGmServer.Run(options.GmPort, DevelopmentCommand, stop);
        var startedAt = DateTimeOffset.Now;
        Func<PacketCapture?, CancellationToken, Task>? gmWeb = options.GmWebPort == 0 ? null : (capture, stop) => GmWebServer.RunAsync(options.GmWebPort,
            new GmWebData(options, startedAt, online, new GmAccountDirectory(Path.Combine(options.DataDirectory, "game.db")),
                DevelopmentCommand, mailbox, itemResources.GoldLimits, new Network.Management.GmItemExtras(pvf, null) { AvatarLog = message => log.Write("gm-avatar", new { Message = message }) }, new GmAudit(Path.Combine(options.DataDirectory, "gm-audit.jsonl")), raidWindows), stop, capture, options);
        // 两个管理入口任一退出或失败都取消整个实例，与其他监听一致。
        // 决斗维护也使用此生命周期；关闭两个GM入口时仍须配对及回收预约。
        Func<PacketCapture?, CancellationToken, Task> management = RunManagement;
        log.Write("startup", new { Stage = "Runtime assembly", State = "complete", ElapsedMs = Math.Round(timer.Elapsed.TotalMilliseconds, 3) });
        // 存档 schema 版本在这里交给宿主写进 ready.json 与启动日志；Network 不直接依赖 Persistence。
        return new ServerHost(options, channel, game, management, OnlineCharacters, Started, log, gameChannels,native,gameCache,
            guildInvitations,guildEconomyProjection,guildTeleportService,guildNpcService,guildHallWait.RunAsync,
            runWorld: marriageScheduler.RunAsync, gameplayTimers: lukeTimers, saveVersion: SqliteMigrations.CurrentVersion,
            lukeRejoinCandidate: lukeRejoin is null ? null : lukeRejoin.DescribeCandidate);

        async Task RunManagement(PacketCapture? capture, CancellationToken stop)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var running = new List<Task>();
            Exception? failure = null;
            try
            {
                stop.ThrowIfCancellationRequested();
                // 仅在管理入口真正启动时预热；装配或前置监听失败不留下后台资源读取。
                if (gmCommands is not null || gmWeb is not null) developmentCatalog.StartWarmup();
                running.Add(MaintainPvp(lifetime.Token));
                if (gmCommands is not null) running.Add(gmCommands(lifetime.Token));
                if (gmWeb is not null) running.Add(gmWeb(capture, lifetime.Token));
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
        async Task MaintainPvp(CancellationToken stop)
        {
            var time = clock ?? TimeProvider.System;
            // 轮询使用实际服务节奏；注入时钟只判业务期限，不注册到其他玩法的专用定时器。
            using var pulse = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await pulse.WaitForNextTickAsync(stop))
                {
                    var expired = await arena.MaintainMatchingAsync(time.GetUtcNow(), stop, (room, exception) =>
                        log.Write("pvp_maintenance_failed", new { Room = room, Error = exception.Message }));
                    if (expired.Length != 0) arena.Publish(expired);
                    // 每轮有限配对；双方连接忙时跳过，不创建独立后台任务或等待socket。
                    for (int paired = 0; paired < 32; paired++)
                    {
                        var entry = await arena.PrepareNextOrdinaryMatchingAsync(time.GetUtcNow(), stop);
                        if (entry is null) break;
                        try
                        {
                            arena.Publish(entry.Initial.Select(notice => notice with
                            {
                                Kind = Game.Pvp.PvpArenaNoticeKind.Matching,
                                MatchingId = entry.Load.Id,
                                MatchingRatings = entry.Load.Members.Select((ticket, at) =>
                                    KeyValuePair.Create(ticket.Actor.Character.Id, entry.Ratings[at])).ToImmutableDictionary()
                            }));
                            log.Write("pvp_matching_pair", new { Matching = entry.Load.Id, Room = entry.Room.Id });
                        }
                        finally { entry.Room.Commands.Release(); }
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
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
            string result = monsterBattleLevels.Answer(line) ?? monsterAi.Answer(line) ?? DevelopmentGm.Answer(line, options.DevelopmentAccount, repository, owners, online.Find,
                mailbox, itemResources.Items, progress, cashShop, debugSettings, events, developmentCatalog,
                online.FindConnection, log, noticeSender);
            log.Write("gm", new { Ok = result.StartsWith("OK ", StringComparison.Ordinal), Result = result.Length > 180 ? result[..180] : result });
            return result;
        }
    }
}
