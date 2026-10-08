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

namespace DfoServer.Infrastructure;
// 唯一装配入口：按领域增加模块及依赖，不在协议循环中维护业务分支。
internal static class ServerRuntime
{
    public static ServerHost Create(ServerOptions options, PvfArchive pvf, CharacterRepository repository, TownCatalog towns, CharacterCatalog characters,
        SkillLearningCatalog learning, ItemResources itemResources, ServerLog log)
    {
        var owners = new AccountOwners();
        var online = new OnlineSessionDirectory();
        var accounts = new AuthenticationService(new AuthenticationRepository(Path.Combine(options.DataDirectory, "game.db")));
        accounts.EnsureDevelopmentAccount();
        var presence = new TownPresence(towns);
        var mailbox = new Game.Mailbox.MailboxService(repository, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
        var mailboxProjection = new MailboxProjection(mailbox, online);
        var settings = new ClientSettingsService(new ClientSettingsRepository(Path.Combine(options.DataDirectory, "game.db")));
        var stats = new Game.Characters.CharacterStatComputer(characters);
        var projection = new TownProjection(towns, stats, presence, itemResources.EpicPieces, mailboxProjection, new SettingsProjection(settings));
        var inventoryProjection = new InventoryProjection(stats, projection, itemResources.EpicPieces);
        var levelProjection = new CharacterLevelProjection(stats);
        var quests = QuestCatalog.Load(pvf);
        var questParameters = QuestRewardParameterCatalog.Load(pvf);
        var commands = new GameCommandRegistry();
        var growth = CharacterGrowthCatalog.Load(pvf, learning);
        var progress = new Game.Characters.CharacterProgressReset(characters, learning, growth);
        IGameCommandModule dungeon;
        IGameCommandModule? settlement = null;
        Func<GameSession, IGameConnectionActivity>? createActivity = null;
        if (options.DevelopmentDungeonEntryProbe == DungeonEntryProbeMode.Disabled)
        {
            var dungeonCatalog = DungeonCatalog.Load(pvf);
            var rooms = DungeonRoomCatalog.Load(pvf);
            var drops = DungeonMonsterDropCatalog.Load(pvf);
            dungeon = new DungeonHandler(log, dungeonCatalog, rooms,
                growth, DungeonKillExperienceCatalog.Load(pvf), projection, levelProjection,
                drops, itemResources.Items, itemResources.EpicPieces);
            settlement = new DungeonSettlementHandler(log, dungeonCatalog, DungeonClearExperienceCatalog.Load(pvf), growth, projection,
                rooms, drops, itemResources.Items, levelProjection);
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
            new InventoryHandler(new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection),
            new RentalHandler(new RentalService(itemResources.Rental, itemResources.Items), inventoryProjection, log),
            new CurrencyHandler(new Game.Currency.CurrencyService(itemResources.GoldLimits), inventoryProjection),
            new CargoHandler(new InventoryCargoService(itemResources.Items, itemResources.Cargo, itemResources.GoldLimits), inventoryProjection),
            new ShopHandler(new InventoryShopService(itemResources.Items, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection),
            new RepairHandler(new InventoryRepairService(itemResources.Items, itemResources.Repair), inventoryProjection),
            new RefinementHandler(new InventoryRefinementService(itemResources.Items, itemResources.Refinement), inventoryProjection),
            new DisjointHandler(new InventoryDisjointService(itemResources.Items, itemResources.Disjoint, itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection),
            new EpicBookHandler(new EpicPieceCraftService(itemResources.Items, itemResources.EpicPieces, itemResources.GoldLimits), inventoryProjection),
            new BoosterHandler(new InventoryBoosterService(itemResources.Items, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces), inventoryProjection),
            new DungeonSelectionHandler(projection, log),
            new QuestConditionHandler(log),
            new QuestCompletionHandler(quests, questParameters, itemResources.Items,
                itemResources.GoldLimits, itemResources.EpicPieces, growth, log, levelProjection),
            dungeon,
            new AgitWarHandler(),
            new ClientNoticeHandler(log)
        ];
        foreach (var module in modules)
            module.Register(commands);
        settlement?.Register(commands);
        commands.Seal();
        var login = new LoginHandler(options, owners, accounts, log);
        var game = new GameProtocolHandler(options, login, commands, repository, towns, online, projection, log, createActivity, mailboxProjection);
        var channel = new ChannelProtocolHandler(options, log);
        Func<CancellationToken, Task>? management = options.GmPort == 0 ? null : stop => DevelopmentGmServer.Run(options.GmPort, DevelopmentCommand, stop);
        return new ServerHost(options, channel, game, management, Started, log);
        void Started() => log.Write("ready", new
        {
            options.ChannelPort,
            options.GamePort,
            options.GmPort,
            options.DevelopmentAccount,
            options.DevelopmentDungeonEntryProbe,
            Pid = Environment.ProcessId,
            Towns = towns.Areas.Keys.Select(key => key.Item1).Distinct().Count(),
            Areas = towns.Areas.Count,
            MissingMaps = towns.Areas.Values
                .SelectMany(area => area.Phases.Prepend(area.Default))
                .Where(map => map.Error is not null)
                .Select(map => new { map.Path, map.Error })
                .Distinct()
                .ToArray()
        });
        string DevelopmentCommand(string line)
        {
            string result = DevelopmentGm.Answer(line, options.DevelopmentAccount, repository, owners, online.Find,
                mailbox, itemResources.Items, progress);
            log.Write("gm", new { Ok = result.StartsWith("OK ", StringComparison.Ordinal), Result = result.Length > 180 ? result[..180] : result });
            return result;
        }
    }
}
