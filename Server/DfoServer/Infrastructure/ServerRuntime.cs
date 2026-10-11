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

namespace DfoServer.Infrastructure;
// 唯一装配入口：按领域增加模块及依赖，不在协议循环中维护业务分支。
internal static class ServerRuntime
{
    public static ServerHost Create(ServerOptions options, PvfArchive pvf, CharacterRepository repository, TownCatalog towns, CharacterCatalog characters,
        SkillLearningCatalog learning, ItemResources itemResources, ServerLog log, QuestCatalog quests)
    {
        log.Write("startup", new { Stage = "Runtime assembly", State = "starting" });
        var timer = Stopwatch.StartNew();
        var owners = new AccountOwners();
        var online = new OnlineSessionDirectory();
        var accounts = new AuthenticationService(new AuthenticationRepository(Path.Combine(options.DataDirectory, "game.db")));
        accounts.EnsureDevelopmentAccount();
        var presence = new TownPresence(towns);
        var mailbox = new Game.Mailbox.MailboxService(repository, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
        var mailboxProjection = new MailboxProjection(mailbox, online);
        var settings = new ClientSettingsService(new ClientSettingsRepository(Path.Combine(options.DataDirectory, "game.db")));
        var questParameters = QuestRewardParameterCatalog.Load(pvf);
        var questConnections = QuestConnectionCatalog.Load(pvf);
        var titleCatalog = new TitleCatalog(pvf, quests);
        var titleService = new Game.Titles.TitleAchievementService(titleCatalog, quests, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
        var titleProjection = new TitleProjection(titleService);
        var growth = CharacterGrowthCatalog.Load(pvf, learning);
        var questProjection = new QuestProjection(quests,questParameters,itemResources.Items,itemResources.GoldLimits,itemResources.EpicPieces,growth);
        var stats = new Game.Characters.CharacterStatComputer(characters);
        var projection = new TownProjection(towns, stats, presence, itemResources.EpicPieces, mailboxProjection, new SettingsProjection(settings), questProjection, learning, titleProjection);
        var inventoryProjection = new InventoryProjection(stats, projection, itemResources.EpicPieces);
        var debugSettings = new GameDebugSettings();
        var cashShop = new InventoryCashShopService(itemResources.Items, itemResources.CashShop, itemResources.GoldLimits, itemResources.EpicPieces, debugSettings);
        var levelProjection = new CharacterLevelProjection(stats, questProjection);
        var commands = new GameCommandRegistry();
        DungeonRoomCatalog? questRooms = null;
        var progress = new Game.Characters.CharacterProgressReset(characters, learning, growth);
        IGameCommandModule dungeon;
        IGameCommandModule? settlement = null;
        IGameCommandModule? questReplay = null;
        Func<GameSession, IGameConnectionActivity>? createActivity = null;
        if (options.DevelopmentDungeonEntryProbe == DungeonEntryProbeMode.Disabled)
        {
            var dungeonCatalog = DungeonCatalog.Load(pvf);
            var rooms = DungeonRoomCatalog.Load(pvf);
            questRooms = rooms;
            var drops = DungeonMonsterDropCatalog.Load(pvf);
            var dropRules = DungeonDropRuleCatalog.Load(pvf);
            var dropCandidates = itemResources.DungeonDropItems;
            var specialDrops = itemResources.DungeonSpecialDrops;
            var resourceDrops = new DungeonResourceDropPolicy(dropRules, dropCandidates, drops.Get, itemResources.Items.Get, specialDrops);
            DungeonDropGenerationPlan PrepareDrops(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
                resourceDrops.Prepare(monster.MonsterId, monster.CandidateLevel, monster.SpawnType, entry.Difficulty, entry.BasisLevel);
            log.Write("dungeon_drop_policy", new { Rule = DungeonResourceDropPolicy.Rule,
                EquipmentWeights = "current_pvf_creation_rate", Retained = DungeonResourceDropPolicy.RetainedFields,
                FifthProbabilityRows = dropRules.ProbabilityRows.Count(row => row.Values[4] != 0),
                specialDrops.Compatibility });
            dungeon = new DungeonHandler(log, dungeonCatalog, rooms,
                growth, DungeonKillExperienceCatalog.Load(pvf), projection, levelProjection,
                drops, itemResources.Items, itemResources.EpicPieces, itemResources.GoldLimits, questProjection, PrepareDrops);
            settlement = new DungeonSettlementHandler(log, dungeonCatalog, DungeonClearExperienceCatalog.Load(pvf), growth, projection,
                rooms, drops, itemResources.Items, levelProjection, itemResources.EpicPieces,
                new Game.Dungeons.DungeonCardRewardPolicy(dropRules, dropCandidates, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces,
                prices: DungeonCardPriceCatalog.Load(pvf)), inventoryProjection, itemResources.Rental);
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
        IGameCommandModule[] modules =
        [
            new SessionHandler(projection, log),
            new CharacterHandler(characters, projection, log, itemResources.GoldLimits, learning),
            new SkillHandler(learning, log),
            new TownHandler(projection, log),
            new SettingsHandler(settings, log, projection),
            new MailboxHandler(mailbox, mailboxProjection, inventoryProjection),
            new TitleHandler(titleService, new Game.Titles.TitleBookService(titleCatalog, itemResources.Items), titleProjection, inventoryProjection, projection, online),
            new InventoryHandler(new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection),
            new ConsumableUseHandler(new InventoryConsumableUseService(itemResources.RecoveryConsumables), log),
            new RentalHandler(new RentalService(itemResources.Rental, itemResources.Items), inventoryProjection, log),
            new CurrencyHandler(new Game.Currency.CurrencyService(itemResources.GoldLimits), inventoryProjection),
            new CargoHandler(new InventoryCargoService(itemResources.Items, itemResources.Cargo, itemResources.GoldLimits), inventoryProjection),
            new ShopHandler(
                new InventoryShopService(itemResources.Items, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces),
                cashShop,
                inventoryProjection),
            new RepairHandler(new InventoryRepairService(itemResources.Items, itemResources.Repair), inventoryProjection),
            new RandomOptionHandler(new InventoryRandomOptionService(itemResources.Items, itemResources.RandomOptions), inventoryProjection, log),
            new RefinementHandler(new InventoryRefinementService(itemResources.Items, itemResources.Refinement), inventoryProjection),
            new DisjointHandler(new InventoryDisjointService(itemResources.Items, itemResources.Disjoint, itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection),
            new EpicBookHandler(new EpicPieceCraftService(itemResources.Items, itemResources.EpicPieces, itemResources.GoldLimits), inventoryProjection),
            new BoosterHandler(new InventoryBoosterService(itemResources.Items, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces,
                debugSettings: debugSettings), inventoryProjection),
            new DungeonSelectionHandler(projection, log),
            new QuestLifecycleHandler(quests, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, log, questProjection),
            new QuestHiddenHandler(quests,log),
            new QuestQuickClearHandler(quests,questConnections,questProjection,log,itemResources.Items,learning),
            new QuestTicketHandler(quests,itemResources.Items,questProjection,log,levelProjection,learning),
            new QuestConditionHandler(log, quests, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, questRooms, questParameters, growth),
            new QuestCompletionHandler(quests, questParameters, itemResources.Items,
                itemResources.GoldLimits, itemResources.EpicPieces, growth, log, levelProjection, questProjection),
            dungeon,
            new AgitWarHandler(),
            new ClientNoticeHandler(log)
        ];
        foreach (var module in modules)
            module.Register(commands);
        settlement?.Register(commands);
        questReplay?.Register(commands);
        commands.Seal();
        log.Write("startup", new { Stage = "Command registration", State = "complete", Commands = commands.Count });
        var login = new LoginHandler(options, owners, accounts, log);
        var game = new GameProtocolHandler(options, login, commands, repository, towns, online, projection, log, createActivity, mailboxProjection, quests, debugSettings);
        var channel = new ChannelProtocolHandler(options, log);
        Func<CancellationToken, Task>? management = options.GmPort == 0 ? null : stop => DevelopmentGmServer.Run(options.GmPort, DevelopmentCommand, stop);
        log.Write("startup", new { Stage = "Runtime assembly", State = "complete", ElapsedMs = Math.Round(timer.Elapsed.TotalMilliseconds, 3) });
        return new ServerHost(options, channel, game, management, Started, log);
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
                mailbox, itemResources.Items, progress, cashShop, debugSettings);
            log.Write("gm", new { Ok = result.StartsWith("OK ", StringComparison.Ordinal), Result = result.Length > 180 ? result[..180] : result });
            return result;
        }
    }
}
