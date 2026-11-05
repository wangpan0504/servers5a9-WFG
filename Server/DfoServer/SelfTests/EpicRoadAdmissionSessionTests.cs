using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicRoadAdmissionAtomicTests
{
    // 普通路线隔离验证准入事务；真实活动路线由下方Session回归覆盖。
    public static void RunResources(PvfArchive pvf, ItemResources resources) =>
        EpicRoadAdmissionSessionTests.RunResourcesCore(pvf, resources, syntheticRoute: true);
}

internal static class EpicRoad2019SimulatedSessionTests
{
    public static void RunResources(PvfArchive pvf, ItemResources resources) =>
        EpicRoadAdmissionSessionTests.RunResourcesCore(pvf, resources, syntheticRoute: false, simulatedDrops: true);
}

internal static class EpicRoad2019NativeTownSessionTests
{
    public static void RunResources(PvfArchive pvf, ItemResources resources) =>
        EpicRoadAdmissionSessionTests.RunResourcesCore(pvf, resources, syntheticRoute: false,
            simulatedDrops: true, nativeTown: true);
}

internal static class EpicRoad2019ClosedRunSessionTests
{
    public static void RunResources(PvfArchive pvf, ItemResources resources) =>
        EpicRoadAdmissionSessionTests.RunResourcesCore(pvf, resources, syntheticRoute: false,
            simulatedDrops: true, nativeTown: true, closeBeforeBoss: true);
}

// 完整Session入场回归；合成城镇仅承载选图，不代表已恢复活动原入口。
internal static class EpicRoadAdmissionSessionTests
{
    public static void RunResources(PvfArchive pvf, ItemResources itemResources) =>
        RunResourcesCore(pvf, itemResources, syntheticRoute: false);

    internal static void RunResourcesCore(PvfArchive pvf, ItemResources itemResources, bool syntheticRoute,
        bool simulatedDrops = false, bool nativeTown = false, bool closeBeforeBoss = false)
    {
        int checks = Count;
        byte requestDifficulty = syntheticRoute ? (byte)0 : (byte)2;
        var catalog = syntheticRoute ? DungeonCatalog.Load(pvf, new Dictionary<int, string>
        {
            [400000097] = Testing.SelfTestCatalogs.Dungeons(pvf).DungeonPath(3)!
        }) : DungeonCatalog.Load(pvf, EpicRoadResourcePaths.Dungeons, EpicRoadResourcePaths.Maps);
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        var characters = CharacterCatalog.Load(pvf, itemResources.Items);
        var learning = Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = Testing.SelfTestCatalogs.Growth(pvf, learning);
        var nativeTowns = nativeTown ? TownCatalog.Load(pvf, EpicRoad2019NpcRestoreTests.Geometry(pvf), epicRoad2019Enabled: true) : null;
        var quests = nativeTown ? QuestCatalog.Load(pvf, epicRoad2019Enabled: true).WithItems(itemResources.Items)
            : new QuestCatalog(new());
        var incounts = DungeonIncountCatalog.Load(pvf);
        var freeRow = incounts.FindAccount(400000097)!;
        var monsterDrops = simulatedDrops ? Testing.SelfTestCatalogs.MonsterDrops(pvf) : null;
        var dropPolicy = simulatedDrops ? new DungeonResourceDropPolicy(Testing.SelfTestCatalogs.DropRules(pvf),
            itemResources.DungeonDropItems, monsterDrops!.Get, itemResources.Items.Get,
            epicRoadSimulated: new EpicRoad2019SimulatedDropPolicy(
                DungeonHellEpicCatalog.Load(pvf).Pool(54, 2, itemResources.Items.Get), true)) : null;
        That(freeRow.Limit == 10 && incounts.ResetInfo[freeRow.ResetType] == -1 &&
            incounts.FindAccount(400000098) is null,
            "Session回归使用免费图真实账号每日10次，收费图不虚构账号上限");
        foreach (bool expires in new[] { false, true })
        foreach (int dungeon in syntheticRoute ? new[] { 400000097 } : new[] { 400000097, 400000098 })
            Verify(dungeon, expires);
        Console.WriteLine($"PASS {Count - checks} 星河Session准入晚期关闭、到期与次数费用原子性断言；syntheticRoute={syntheticRoute}; simulatedDrops={simulatedDrops}; nativeTown={nativeTown}");

        void Verify(int dungeon, bool expires)
        {
            string fixture = Path.Combine(Path.GetTempPath(), "S5A9-epic-entry-session-" + Guid.NewGuid().ToString("N"));
            bool passed = false;
            GameSession? session = null;
            AccountOwners.Lease? owner = null;
            try
            {
                string path = Path.Combine(fixture, "game.db");
                var repository = new CharacterRepository(path, quests);
                if (nativeTown)
                    new DfoServer.Game.Auth.AuthenticationService(new AuthenticationRepository(path))
                        .Create("epic-session", "fixture-password");
                var owners = new AccountOwners();
                owner = owners.Acquire("epic-session", () => { });
                var point = nativeTowns?.Room ?? new Location(1, 0, 100, 100);
                var bounds = new WalkRegion(new(0, 0, 1000, 1000), [], []);
                var map = new TownMap("synthetic/epic-admission.map", [bounds], [], false, [])
                    { DungeonGates = [new(new(900, 0, 20, 1000), [], [])] };
                var area = new TownArea(1, 0, 1, [], [], [], point, map, [], []) { DungeonWorldMap = 204 };
                var towns = nativeTowns ?? new TownCatalog(new() { [(1, 0)] = area }, point, point, new(0, [], []), [],
                    new Dictionary<uint, uint[]>(),
                    new Dictionary<int, TownDungeonMap>
                    {
                        [204] = new(204, "synthetic/epic-admission.wdm",
                            [new(400000097, -1, false), new(400000098, -1, false)])
                    });
                // 历史门票保留资源删除日；创建、选角和准入共用活动期间的虚拟时钟。
                var clock = new Testing.Inventory.DailyBoosterTestClock(
                    nativeTown ? new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(8))
                        : new DateTimeOffset(2019, 12, 10, 12, 0, 0, TimeSpan.FromHours(8)));
                var candidate = CharacterCreation.Prepare("EpicSession", characters.Get(0), point,
                    nativeTowns?.InitialReturn ?? point, learning.Get);
                repository.Create(owner, candidate with
                {
                    Level = 95,
                    InitialLearning = SkillPointLedger.Synchronize(candidate.Job, candidate.GrowType, 95,
                        candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(95) }, learning, learning.ExpertContractLevels),
                    DungeonState = CharacterDungeonState.NewOrdinary,
                    Inventory = InventorySnapshot.Empty with
                    {
                        Gold = 100,
                        Items = [new(0, new(InventoryListType.Main,
                            ItemSlotBoundService.Range(itemResources.Items.Get(490013728).Kind, InventorySnapshot.Empty).Start),
                            InventoryRewardGrantService.Create(itemResources.Items.Get(490013728), 2, clock.GetUtcNow()))]
                    }
                });
                var events = new ServerEventService(new ServerEventRepository(path), clock);
                if (expires) events.Schedule(ServerEventService.EpicRoadId, clock.GetUtcNow(), clock.GetUtcNow().AddSeconds(60));
                else events.Override(ServerEventService.EpicRoadId, true);
                var wrapper = new QuestCompletionRepositoryTests.LostResultRepository(repository) { LoseCompletionResult = false };
                session = new(wrapper, towns, owner, questCatalog: quests, timeProvider: clock,
                    incounts: incounts, serverEvents: events);
                session.Select(0);
                session.MarkReady();
                if (nativeTown)
                {
                    var start = session.Selected!;
                    var travel = new DfoServer.Game.Town.AreaRequest(123, 0, 1084, 90, 5,
                        start.Position.Town, start.Position.Area, 0, 400001103);
                    That(!session.Travel(travel) && session.TravelReason == "target-quest-required" &&
                        QuestCompletionRepositoryTests.Same(start, repository.LoadCharacter(owner, start.Slot)),
                        "真实活动城镇在完成8712之前拒绝NPC传送且不改资产");
                    That(session.ChangeQuest(8712, DfoServer.Game.Quests.QuestLifecycleAction.Accept, quests,
                        itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces)?.Commit is not null,
                        "同一入场Session在原赛丽亚房间接取8712");
                    That(session.ChangeQuest(8712, DfoServer.Game.Quests.QuestLifecycleAction.Condition, quests,
                        itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, new(null, 0, 0))?.Commit is not null,
                        "同一入场Session沿原NPC会面条件提交8712进度");
                    var finish = session.FinishQuest(8712, ushort.MaxValue, quests,
                        Testing.SelfTestCatalogs.QuestRewardParameters(pvf), itemResources.Items,
                        itemResources.GoldLimits, itemResources.EpicPieces, growth);
                    That(finish.Outcome == DfoServer.Game.Quests.QuestFinishOutcome.Committed &&
                        session.Travel(travel) && session.Selected!.Position is { Town: 123, Area: 0 },
                        "同一角色真实领奖后沿NPC传入123城镇，再进入原副本准入链：" + session.TravelReason);
                }
                var before = session.Selected!;
                That(before.Inventory.Items.Where(item => item.Core.ItemId == 490013728).Sum(item => item.Core.Count) == 2,
                    "准入夹具在选角清理后仍持有两张有效原版门票，不以过期门票验证费用");
                int period = DungeonIncountPolicy.AccountPeriod(incounts, freeRow, clock.GetUtcNow());
                int beforeUsed = repository.LoadAccountDungeonIncount(owner, 400000097, period);
                int callbacks = 0;
                int observedBeforeGuard = -1;
                wrapper.BeforeDungeonEntryCommit = charge =>
                {
                    callbacks++;
                    That(charge.DungeonId == dungeon && charge.RequiredEvent?.Schedule.EventId == ServerEventService.EpicRoadId,
                        "真实Session完成资源准备与install前置后，将本次10005资格送到原事务入口");
                    observedBeforeGuard = repository.LoadAccountDungeonIncount(owner, 400000097, period);
                    // 包装层只在真实事务开始之前注入；不持有SQLite事务或修改产品时序。
                    if (expires) clock.Advance(TimeSpan.FromSeconds(60));
                    else events.Override(ServerEventService.EpicRoadId, false);
                };
                That(DungeonEntryPlan.TryCreate(catalog, before, (uint)dungeon, 0, requestDifficulty, out var diagnosticEntry,
                    out var diagnosticReason, draw: _ => 0, quests: new QuestCatalog(new())),
                    "房间诊断保留相同真实DGN及所选路线：" + diagnosticReason);
                if (!syntheticRoute)
                    That(diagnosticEntry!.SuppressOrdinaryResultBoard,
                        "两张具名原DGN的已证无结果页合同冻结到Entry，保留叶删块时仍生效");
                foreach (var diagnosticMap in diagnosticEntry!.Maze.MapSpecification!
                    .Concat(diagnosticEntry.Maze.DummyStages).Concat(diagnosticEntry.Maze.LayeredMaps)
                    .Concat(diagnosticEntry.Maze.ConditionalMaps).Distinct())
                {
                    string stage = "rooms.Get";
                    try
                    {
                        var room = rooms.Get(diagnosticMap, requirePassiveDefinitions: false);
                        stage = "DungeonMonsterPlan.FromRoom";
                        var plan = DungeonMonsterPlan.FromRoom(room, before.Id,
                            checked((ushort)before.DungeonState!.NextObjectKey), diagnosticEntry.MonsterBasisLevel,
                            playerTeam: diagnosticEntry.PlayerTeam);
                        if (diagnosticEntry.ChampionPercents.IsEnabled)
                        {
                            stage = "WithChampionRolls";
                            _ = plan.WithChampionRolls(diagnosticEntry.ChampionPercents, diagnosticEntry.Difficulty,
                                _ => 0, diagnosticEntry.NamedMonsters);
                        }
                    }
                    catch (Exception error) when (error is InvalidDataException or NotSupportedException or
                        FileNotFoundException or OverflowException)
                    {
                        Console.WriteLine($"FAIL epic room: dungeon={dungeon}, map={diagnosticMap.Id}," +
                            $" path={diagnosticMap.Path}, stage={stage}, type={error.GetType().Name}, message={error.Message}");
                        throw;
                    }
                }
                var result = session.EnterFormalDungeon((uint)dungeon, 0, requestDifficulty, catalog, rooms,
                    pieces: itemResources.EpicPieces, draw: _ => 0);
                That(callbacks == 1 && result is null && session.LastFormalDungeonRejection == "epic-road-event-closed",
                    "准备成功后在受检事务前关闭或到期，真实Session返回原活动业务拒绝：" +
                    $" dungeon={dungeon}, expires={expires}, callbacks={callbacks}, result={result is not null}," +
                    $" reason={session.LastFormalDungeonRejection ?? "<null>"}, phase={session.Phase}," +
                    $" ready={session.Ready}, position={session.Selected?.Position}, observed={observedBeforeGuard}, before={beforeUsed}");
                That(observedBeforeGuard == beforeUsed && repository.LoadAccountDungeonIncount(owner, 400000097, period) == beforeUsed,
                    "晚期活动拒绝前后均不提前消耗免费图账号次数");
                That(QuestCompletionRepositoryTests.Same(before, repository.LoadCharacter(owner, before.Slot)) &&
                    ReferenceEquals(before, session.Selected) && session.DungeonSnapshot is null && session.Phase == SessionPhase.Town,
                    "拒绝保持全部材料、金币、疲劳、尝试号、revision及原会话，不安装半个实例");

                // 失败后正常重试：免费图只记一次，收费图只扣一张票和10疲劳。
                wrapper.BeforeDungeonEntryCommit = null;
                events.Override(ServerEventService.EpicRoadId, true);
                var entered = session.EnterFormalDungeon((uint)dungeon, 0, requestDifficulty, catalog, rooms,
                    drops: monsterDrops, items: simulatedDrops ? itemResources.Items : null,
                    pieces: itemResources.EpicPieces, draw: _ => 0,
                    prepareDrops: dropPolicy is null ? null : entry => dropPolicy.PrepareEntry(entry));
                That(entered is not null && session.Selected!.DungeonState!.LastAttempt == before.DungeonState!.LastAttempt + 1,
                    "晚期拒绝后可正常重试，实际实例尝试号只预留一次");
                int ticketCount = session.Selected!.Inventory.Items.Where(item => item.Core.ItemId == 490013728).Sum(item => item.Core.Count);
                That(repository.LoadAccountDungeonIncount(owner, 400000097, period) == beforeUsed + (dungeon == 400000097 ? 1 : 0) &&
                    ticketCount == (dungeon == 400000097 ? 2 : 1) &&
                    session.Selected.Fatigue!.Used == before.Fatigue!.Used + diagnosticEntry.StartFatigue,
                    "成功重试按两图各自真实次数、门票与疲劳规则仅提交一次");
                var paid = session.Selected;
                var loaded = session.CompleteFormalDungeonLoading();
                That(loaded is not null && session.Phase == SessionPhase.Dungeon &&
                    loaded.Snapshot.Identity.InstanceId == entered!.Snapshot.Identity.InstanceId &&
                    loaded.Snapshot.CurrentMap == entered.Snapshot.CurrentMap &&
                    loaded.Snapshot.ConsumedFatigue == diagnosticEntry.StartFatigue &&
                    QuestCompletionRepositoryTests.Same(paid!, repository.LoadCharacter(owner, paid!.Slot)),
                    "真实活动首房加载沿同一实例进入战斗，一次性疲劳只记统计、不再次扣票或扣费");
                That(session.CompleteFormalDungeonLoading() is null &&
                    QuestCompletionRepositoryTests.Same(paid!, session.Selected!) &&
                    repository.LoadAccountDungeonIncount(owner, 400000097, period) == beforeUsed + (dungeon == 400000097 ? 1 : 0),
                    "重复加载不重复计活动次数、门票、疲劳或角色revision");
                That(session.FormalDungeonResultFocus() is null && session.OpenFormalDungeonOtherSelection() is null,
                    "尚未清图的活动实例不能借结果菜单提前选图");
                if (!syntheticRoute && dungeon == 400000097)
                {
                    That(loaded!.Monsters.Monsters.Any(monster => monster.IsBlocking) && session.MoveFormalDungeon(1, 0) is null &&
                        session.Phase == SessionPhase.Dungeon && session.DungeonSnapshot!.CurrentMap == loaded.Snapshot.CurrentMap,
                        "原457首房存在阻塞怪，未提交死亡不能提前进入458");
                    var kills = DungeonKillExperienceCatalog.Load(pvf);
                    var current = loaded;
                    foreach (var monster in loaded.Monsters.Monsters.Where(monster => monster.IsBlocking))
                    {
                        var death = session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(current, monster), kills, growth);
                        That(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null,
                            "合成客户端死亡输入经过现有原生校验与奖励事务，不直接改Run清房位");
                        current = death.Receipt!;
                    }
                    paid = session.Selected;
                    var moved = session.MoveFormalDungeon(1, 0);
                    That(moved is not null && session.Phase == SessionPhase.Loading &&
                        moved.Snapshot.Identity.InstanceId == entered!.Snapshot.Identity.InstanceId &&
                        moved.Snapshot.CurrentMap.Id == 400000458 && moved.Entry.StartMap == moved.Snapshot.CurrentMap,
                        "原首房清怪后可沿真实路线进入458，沿用同一实例与目标地图回执：" +
                        $"source={loaded!.Snapshot.CurrentMap.Id}/{loaded.Snapshot.CurrentMap.Position};" +
                        $"living={string.Join(',',loaded.Snapshot.LivingMonsters)};" +
                        $"monsters={string.Join(',',loaded.Monsters.Monsters.Select(m => $"{m.MonsterId}:{m.IsBlocking}"))};" +
                        $"objects={string.Join(',',loaded.Monsters.SpecialObjects.Select(o => o.Source.ObjectId))};" +
                        $"result={moved is not null};map={moved?.Snapshot.CurrentMap.Id};phase={session.Phase}");
                    That(moved!.Monsters.Monsters is [{ MonsterId: 407000551, BattleLevel: 95, SpawnType: 1, SourceOrdinal: 0 }] &&
                        moved.Monsters.SpecialObjects.Count == 0 && moved.Monsters.Room.PassiveObjects.Count == 1,
                        "458保持原怪物与地图本地被动对象，不将本地对象重复发成特殊actor或恢复未消费后缀");
                    var mapBody = DungeonPackets.StartMap(moved.Entry, moved.Monsters, moved.MapSeed, passiveItems:moved.PassiveItems);
                    That(BinaryPrimitives.ReadUInt32LittleEndian(mapBody.AsSpan(14)) == 400000458 && mapBody[18] == 1 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(mapBody.AsSpan(21)) == 0 &&
                        BinaryPrimitives.ReadUInt16LittleEndian(mapBody.AsSpan(25)) == moved.Monsters.Monsters[0].ObjectKey &&
                        BinaryPrimitives.ReadUInt32LittleEndian(mapBody.AsSpan(27)) == 407000551 &&
                        mapBody[31] == 95 && mapBody[32] == 1 && mapBody[35] == byte.MaxValue,
                        "NOTI29编码当前458回执的原演员索引、物件键、等级与创建组");
                    var nextLoaded = session.CompleteFormalDungeonLoading();
                    That(nextLoaded is not null && session.Phase == SessionPhase.Dungeon &&
                        nextLoaded.Snapshot.Identity.InstanceId == moved.Snapshot.Identity.InstanceId &&
                        nextLoaded.Snapshot.CurrentMap == moved.Snapshot.CurrentMap &&
                        QuestCompletionRepositoryTests.Same(paid!, repository.LoadCharacter(owner, paid!.Slot)) &&
                        repository.LoadAccountDungeonIncount(owner, 400000097, period) == beforeUsed + 1,
                        "458完成加载不再扣票、疲劳或账号次数，非客户端自然消费证明");
                    current = nextLoaded!;
                    foreach (var monster in current.Monsters.Monsters.Where(monster => monster.IsBlocking).ToArray())
                    {
                        var death = session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(current, monster), kills, growth);
                        That(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null,
                            "458原缺rank怪物的精英死亡经过事务后可清房");
                        That(death.Reward is { Amount: > 0 } &&
                            (ulong)death.GainedExperience + death.ProficiencyGained > 0,
                            "活动精英击杀产生正经验并由成长事务实际记入角色或熟练度");
                        current = death.Receipt!;
                    }
                    var terminal = session.MoveFormalDungeon(2, 0);
                    That(terminal is not null && terminal.Snapshot.CurrentMap.Id == 400000459 &&
                        terminal.Snapshot.Identity.InstanceId == entered!.Snapshot.Identity.InstanceId,
                        "458清房后沿同一原路线到达459末房，不创建新尝试或另扣入场费用");
                    current = session.CompleteFormalDungeonLoading()!;
                    That(current is not null && current.Monsters.Monsters.Any(monster => monster.IsBoss && monster.MonsterId == 407000595),
                        "459原末房领主按当前回执完成加载");
                    if (closeBeforeBoss) events.Override(ServerEventService.EpicRoadId, false);
                    FormalDungeonDeathResult? lastDeath = null;
                    foreach (var monster in current!.Monsters.Monsters.Where(monster => monster.IsBlocking).ToArray())
                    {
                        var observation = FormalDungeonSessionTests.Observation(current, monster);
                        lastDeath = session.ObserveFormalDungeonDeath(observation, kills, growth);
                        That(lastDeath.Outcome == DungeonRunOutcome.Accepted && lastDeath.Receipt is not null,
                            "末房原演员死亡经过实际成长事务，不直接设置通关事实");
                        That(lastDeath.Reward is { Amount: > 0 } &&
                            (ulong)lastDeath.GainedExperience + lastDeath.ProficiencyGained > 0,
                            "末房怪物和领主击杀正经验实际到账，保留无普通结算合同");
                        current = lastDeath.Receipt!;
                        if (simulatedDrops && monster.IsBoss)
                        {
                            var equipment = lastDeath.Drops.Where(row => !row.IsGold &&
                                itemResources.Items.Get(row.Core.ItemId) is { Kind: DfoServer.GameWorld.Items.ItemKind.Equipment,
                                    Rarity: 4, MinimumLevel: 95 }).ToArray();
                            if (closeBeforeBoss)
                                That(equipment.Length == 0, "活动关闭后在场领主不再生成新的模拟装备");
                            else
                            {
                            That(equipment is [var gear] && gear.ResourceIdentity.Contains(EpicRoad2019SimulatedDropPolicy.Rule) &&
                                gear.Core.Count == 1 && !gear.Core.DisjointForbidden,
                                "合法末房领主经原死亡冻结生成一件允许分解的模拟95史诗");
                            if (nativeTown) events.Override(ServerEventService.EpicRoadId, false);
                            var saved = repository.LoadCharacter(owner, before.Slot);
                            That(session.ObserveFormalDungeonDeath(observation, kills, growth).Outcome == DungeonRunOutcome.Duplicate &&
                                QuestCompletionRepositoryTests.Same(saved!, repository.LoadCharacter(owner, before.Slot)),
                                "领主死亡重放不增加奖励或改写资产");
                            uint key = equipment[0].Reservation.GroundKey;
                            That(session.PickFormalDungeonItem(key, itemResources.Items, itemResources.EpicPieces,
                                itemResources.GoldLimits).Outcome == DungeonRunOutcome.Accepted,
                                "模拟装备沿原拾取事务成功入包");
                            That(session.PickFormalDungeonItem(key, itemResources.Items, itemResources.EpicPieces,
                                itemResources.GoldLimits).Outcome == DungeonRunOutcome.Rejected,
                                "同一地面模拟装备不能重复领取");
                            var received = repository.LoadCharacter(owner, before.Slot)!;
                            That(received.Inventory.Items.Count(row => row.Core.ItemId == equipment[0].Core.ItemId &&
                                !row.Core.DisjointForbidden) == 1, "模拟装备完整持久化且没有来源禁分标记");
                            }
                        }
                    }
                    That(lastDeath?.ClearFact is not null && session.Phase == SessionPhase.Settlement,
                        "原末房合法清场产生本实例唯一清图事实");
                    That(session.FormalDungeonResultFocus() is null && session.OpenFormalDungeonOtherSelection() is null,
                        "真实清图后尚未发布通关许可时不能提前操作结果菜单");
                    var enabled = session.EnableFormalDungeonSettlement();
                    That(enabled is not null && session.FormalDungeonResultFocus() is null,
                        "准备通关通知不等于已成功发布，仍不开放结果菜单");
                    session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
                    That(session.FormalDungeonResultFocus() is { } focus &&
                        focus.Snapshot.Identity == current.Snapshot.Identity &&
                        QuestCompletionRepositoryTests.Same(session.Selected!, repository.LoadCharacter(owner, before.Slot)),
                        "活动无普通翻牌结算，但真实清图和通关许可发布后仍可操作72结果菜单且不额外发奖");
                    paid = session.Selected;
                    var settlement = session.SettleFormalDungeon(paid!.Id, 99, 99, [1, 2, 3], catalog,
                        Testing.SelfTestCatalogs.ClearExperience(pvf), growth);
                    That(settlement.Outcome == DungeonRunOutcome.Rejected &&
                        QuestCompletionRepositoryTests.Same(paid, repository.LoadCharacter(owner, paid.Slot)) &&
                        ReferenceEquals(paid, session.Selected) && session.Phase == SessionPhase.Settlement &&
                        session.DungeonSnapshot!.Identity == current.Snapshot.Identity,
                        "原result board=0的活动不接受普通结果页报告、不发通关经验或创建普通翻牌");
                    That(session.SettleFormalDungeon(paid.Id, 99, 99, [1, 2, 3], catalog,
                        Testing.SelfTestCatalogs.ClearExperience(pvf), growth).Outcome == DungeonRunOutcome.Rejected &&
                        QuestCompletionRepositoryTests.Same(paid, repository.LoadCharacter(owner, paid.Slot)) &&
                        repository.LoadAccountDungeonIncount(owner, 400000097, period) == beforeUsed + 1,
                        "重复普通结果页报告不改变已提交死亡、资产或账号次数，仍可按原入口返城");
                }
                Character? returnedCharacter;
                if (!syntheticRoute && dungeon == 400000097 && expires)
                {
                    var selection = session.OpenFormalDungeonOtherSelection();
                    That(selection is not null && session.Phase == SessionPhase.MapSelect && session.DungeonSnapshot is null &&
                        session.FormalDungeonResultFocus() is null && session.OpenFormalDungeonOtherSelection() is null,
                        "已清图活动沿原菜单进入其他地下城选图，释放旧实例且重复菜单不能再次退场");
                    returnedCharacter = session.ReturnFromDungeonSelection()?.Character;
                }
                else returnedCharacter = session.ReturnFromFormalDungeon()?.Character;
                That(returnedCharacter is not null && session.Phase == SessionPhase.Town && session.DungeonSnapshot is null &&
                    returnedCharacter.Inventory.StaminaUntil == before.Inventory.StaminaUntil &&
                    returnedCharacter.Fatigue!.Used == paid!.Fatigue!.Used &&
                    returnedCharacter.Inventory.Items.Where(item => item.Core.ItemId == 490013728).Sum(item => item.Core.Count) == ticketCount &&
                    repository.LoadAccountDungeonIncount(owner, 400000097, period) == beforeUsed + (dungeon == 400000097 ? 1 : 0),
                    "活动放弃或通关菜单返回不加虚弱、不退款或重复扣费，并释放原实例");
                passed = true;
            }
            finally
            {
                session?.End();
                owner?.Dispose();
                if (passed) TestFiles.DeleteDirectory(fixture, true);
                else Console.WriteLine("FAIL fixture: " + fixture);
            }
        }
    }
}
