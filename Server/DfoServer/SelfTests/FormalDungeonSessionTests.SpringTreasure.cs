using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Skills;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
namespace DfoServer;
internal static partial class FormalDungeonSessionTests
{
    public static void RunSpringTreasureProducer(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources resources)
    {
        var clock = new SpringTreasureClock();
        var now = clock.GetUtcNow();
        var catalog = SelfTestCatalogs.Dungeons(pvf);
        // 仅沿已有308哈林入口的实际world map有限选项选夹具，不猜图号或放宽推荐范围。
        var area = towns.Areas.Values.Where(row => row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 291100308))
            .OrderBy(row => row.Town).ThenBy(row => row.Area).First();
        var worldMap = towns.DungeonMaps[area.DungeonWorldMap!.Value];
        var candidates = worldMap.Options.Where(option => option.QuestId == -1).Select(option => option.DungeonId).Distinct().Order()
            .Select(id =>
            {
                var definition = catalog.Get(id);
                var levels = Enumerable.Range(85, 11).Where(level =>
                    DfoServer.GameWorld.Guilds.GuildDungeonMileageEligibility.IsEligible(definition, level)).ToArray();
                bool explicitActivityExclusion = id is 530 or SleepyHollowRewardCatalog.DungeonId;
                return new { Definition = definition, Levels = explicitActivityExclusion ? Array.Empty<int>() : levels, explicitActivityExclusion };
            }).ToArray();
        Console.WriteLine("SPRING_TREASURE_ENTRY_CANDIDATES " + System.Text.Json.JsonSerializer.Serialize(new
        {
            area.Town, area.Area, world = area.DungeonWorldMap,
            candidates = candidates.Select(row => new
            {
                row.Definition.Id, row.Definition.Path, row.Definition.MinimumLevel, row.Definition.BasisLevel, row.Definition.DungeonType,
                recommended = row.Definition.RawSections.Where(section => section.Tag == "recommended level")
                    .Select(section => section.Tokens.Select(token => new { token.Type, token.Value }).ToArray()).ToArray(),
                tags = row.Definition.RawSections.Select(section => section.Tag).Distinct().ToArray(),
                eligibleLevels = row.Levels, rejection = row.Levels.Length != 0 ? null : row.explicitActivityExclusion
                    ? "explicit-activity-special-dungeon" : "no-level85..95-passes-existing-typed-eligibility"
            }).ToArray()
        }));
        var selected = candidates.FirstOrDefault(row => row.Levels.Length > 0);
        Check(selected is not null, "已知哈林world实际普通选项中必须存在85至95级的原typed合法推荐配对；详见候选范围和拒绝诊断");
        int dungeonId = selected!.Definition.Id;
        byte level = checked((byte)selected.Levels[0]);
        Console.WriteLine($"SPRING_TREASURE_SELECTED dungeon={dungeonId} level={level} world={area.DungeonWorldMap}");
        var templates = new[] { 490705387, 490705425, 490705439 }.SelectMany(id => resources.AvatarPackages.Get(id).Categories)
            .SelectMany(category => category.Rows).Select(row => row.ItemId).Distinct().Select(resources.Items.Get)
            .Where(template => template.Kind == ItemKind.Avatar && template.EquipmentSlot < 8 && template.AllowsUse(level, 0) &&
                resources.CharacterInvestmentMetadata.ReadSet(template.Id)?.SetId is 30179 or 30180 or 30181)
            .GroupBy(template => template.EquipmentSlot).OrderBy(group => group.Key).Select(group => group.OrderBy(template => template.Id).First()).ToArray();
        Check(templates.Length == 8, "RED/GREEN使用原三源真实八槽，不猜ID或外观");
        var learning = SelfTestCatalogs.SkillLearning(pvf);
        var growth = SelfTestCatalogs.Growth(pvf, learning);
        var done = new HashSet<int>(area.Quests) { 6030, 6080, 6081, 6082, 10112 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        done.UnionWith(area.Map(done).DungeonGates.Where(gate => gate.Conditions.Length == 0).SelectMany(gate => gate.Quests));
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        var seed = CharacterCreation.Prepare("TreasureBoss", characters.Get(0), towns.Room, towns.InitialReturn, learning.Get);
        var worn = templates.Select(template => InventoryRewardGrantService.CreateItem(template,
            new(InventoryListType.Equipment, template.EquipmentSlot), InventoryRewardGrantService.Create(template, utcNow: now))).ToImmutableArray();
        seed = seed with { Level = level, Position = position, Return = position, CompletedQuests = done.Order().ToArray(),
            InitialLearning = SkillPointLedger.Synchronize(0, 0, level,
                seed.InitialLearning with { Experience = growth.GetLevelStartExperience(level) }, learning), Inventory = seed.Inventory with
            { Items = seed.Inventory.Items.Where(item => item.Position.List != InventoryListType.Equipment || item.Position.Index > 7).Concat(worn).ToImmutableArray() } };
        var expectedTreasure = InventoryRewardGrantService.Create(resources.Items.Get(490705287), 1, now,
            shopExpiration: true, ignoreUsablePeriod: false);
        var fixture = new Fixture("TreasureBoss", towns, characters, learning, SelfTestCatalogs.Quests(pvf), prepared: seed,
            timeProvider: clock, springTreasure: new(resources.SpringTreasureAvatars, resources.Items.Get(490705287)), debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true, IgnoreCashShopUsablePeriod = false });
        bool completed = false;
        try
        {
            fixture.Session.Select(fixture.Created.Slot, resumeTown: true);
            fixture.Session.MarkReady();
            Check(fixture.Session.Selected!.Position == position, "真实已解锁哈林入口选角恢复城镇，未退回默认入口");
            var rooms = SelfTestCatalogs.Rooms(pvf);
            var drops = SelfTestCatalogs.MonsterDrops(pvf);
            int draws = 0;
            var empty = new DungeonDropGenerationPlan("spring-treasure-ordinary-empty", []);
            var entered = fixture.Session.EnterFormalDungeon(checked((uint)dungeonId), 0, 0, catalog, rooms, drops, resources.Items,
                draw: _ => { draws++; return 0; }, pieces: resources.EpicPieces, prepareDrops: _ => _ => empty);
            Check(entered is not null, "真实普通哈林正式入场成功：" + fixture.Session.LastFormalDungeonRejection);
            var current = fixture.Session.CompleteFormalDungeonLoading()!;
            var path = QuestStoryRouteTests.Path(entered!.Entry.Maze.StaticTopology, entered.Entry.StartMap.Position, entered.Entry.BossMap.Position).ToList();
            var kills = SelfTestCatalogs.KillExperience(pvf);
            int bosses = 0, culledReplays = 0;
            foreach (var point in path)
            {
                Check(current.Snapshot.CurrentMap.Position == point, "沿真实地图相邻路线走到Boss");
                var culled = new Dictionary<ushort, DungeonRunSnapshot>();
                foreach (var monster in current.Monsters.Monsters.Where(row => row.IsBlocking))
                {
                    var observation = Observation(current, monster);
                    if (culled.TryGetValue(monster.ObjectKey, out var culledAt))
                    {
                        var beforeCullReplay = fixture.Session.DungeonSnapshot!;
                        int beforeCullDraws = draws;
                        var replay = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                        var afterCullReplay = fixture.Session.DungeonSnapshot!;
                        Check(beforeCullReplay.Identity == culledAt.Identity && beforeCullReplay.Phase == culledAt.Phase &&
                            beforeCullReplay.CurrentMap == current.Snapshot.CurrentMap && beforeCullReplay.CurrentMap == culledAt.CurrentMap &&
                            beforeCullReplay.LivingMonsters.SequenceEqual(culledAt.LivingMonsters) &&
                            !beforeCullReplay.LivingMonsters.Contains(monster.ObjectKey) &&
                            replay.Outcome == DungeonRunOutcome.Duplicate && replay.MonsterKey == monster.ObjectKey &&
                            replay.Reward is null && replay.Drop is null && replay.Drops.IsEmpty && replay.ClearFact is null &&
                            replay.CulledMonsterKeys.Count == 0 && replay.GainedExperience == 0 && replay.ProficiencyGained == 0 &&
                            draws == beforeCullDraws && afterCullReplay.Identity == beforeCullReplay.Identity &&
                            afterCullReplay.Phase == beforeCullReplay.Phase && afterCullReplay.CurrentMap == beforeCullReplay.CurrentMap &&
                            afterCullReplay.LivingMonsters.SequenceEqual(beforeCullReplay.LivingMonsters),
                            "仅同房已提交清退集合中的对象补报为Duplicate，原快照保持且不追加活动骰、掉落或经验");
                        culledReplays++;
                        continue;
                    }
                    bool realBoss = monster.IsBoss && !monster.IsFriendly && !monster.IsScriptedBoss && !monster.IsScriptedRetirement;
                    int before = draws;
                    if (realBoss)
                    {
                        fixture.Sql("CREATE TRIGGER spring_treasure_late AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'spring treasure late rollback'); END");
                        Reject<SqliteException>(() => fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth), "同真实Boss首次冻结后晚SQL失败");
                        fixture.Sql("DROP TRIGGER spring_treasure_late");
                        before = draws;
                    }
                    var result = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    Check(result.Outcome == DungeonRunOutcome.Accepted, "正式怪物权威死亡提交");
                    foreach (ushort key in result.CulledMonsterKeys)
                    {
                        Check(result.Receipt is not null && result.Receipt.Snapshot.CurrentMap == current.Snapshot.CurrentMap &&
                            current.Snapshot.LivingMonsters.Contains(key) && !result.Receipt.Snapshot.LivingMonsters.Contains(key),
                            "清退身份来自本房Accepted死亡回执，目标原先存活且提交后已退出存活集合");
                        culled.Add(key, result.Receipt!.Snapshot);
                    }
                    if (!realBoss) { Check(result.Drops.Length == 0 && draws == before, "小怪不追加活动骰"); continue; }
                    bosses++;
                    Check(draws == before && result.Drops.Length == 1 && result.Drops[0].Core is { ItemId: 490705287, Count: 1 } &&
                        result.Drops[0].Core == expectedTreasure && !result.Drops[0].Core.IsExpired(now.ToUnixTimeSeconds()),
                        "本人合法推荐等级原八槽Boss固定命中后应产实际287；当前日历与晚失败重试不能变空或重抽");
                    var ground = result.Drops[0];
                    Check(fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth).Outcome == DungeonRunOutcome.Duplicate && draws == before,
                        "Boss精确重复不再骰");
                    var picked = fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, resources.Items, resources.EpicPieces);
                    Check(picked.Outcome == DungeonRunOutcome.Accepted && picked.Commit!.ItemUid > 0 &&
                        fixture.Read().Inventory.Items.Single(item => item.Uid == picked.Commit.ItemUid).Core == ground.Core,
                        "真实287沿正式拾取同事务入库，保实际UID/Core");
                    Check(new DfoServer.Persistence.CharacterRepository(fixture.Path).LoadCharacter(fixture.Owner, fixture.Created.Slot).Inventory.Items
                        .Any(item => item.Uid == picked.Commit!.ItemUid && item.Core == ground.Core), "仓储重开保同一实际287");
                    Check(fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, resources.Items, resources.EpicPieces).Outcome == DungeonRunOutcome.Rejected,
                        "重复拾取不追加第二份资产");
                }
                if (point != path[^1])
                {
                    int index = path.IndexOf(point);
                    var next = path[index + 1];
                    Check(fixture.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y) is not null, "清房后合法相邻过图");
                    current = fixture.Session.CompleteFormalDungeonLoading()!;
                }
            }
            Check(bosses > 0, "真实路线必须消费至少一个原Boss");
            Check(culledReplays > 0, "真实路线必须覆盖Boss清退对象的权威补报，不以静态分支冒通过");
            completed = true;
        }
        finally
        {
            if (completed) fixture.Dispose();
            else { fixture.Session.End(); fixture.Owner.Dispose(); }
        }
    }
    sealed class SpringTreasureClock : TimeProvider
    {
        readonly DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
    }
}
