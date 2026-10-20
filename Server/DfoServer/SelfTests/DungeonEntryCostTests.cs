using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// #510/#511：选图准入的具体拒绝原因、普通入场票（[required item]「物品 1 1」）与入场一次性疲劳（[use fatigue only start dungeon] N）。
// 只锁定本服行为：协议拒绝包仍是 SelectionRejected [0,9,0,0]；扣票、扣疲劳与尝试号预留同一事务。
internal static class DungeonEntryCostTests
{
    const int Tayberrs = 291100320, TayberrsSecond = 291100322, RefinedStone = 10158964;

    internal static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);

        // 资源形状：只有唯一一行「物品 1 1」才是入场票；入场疲劳只认唯一一个正整数。
        var tayberrs = dungeons.Get(Tayberrs);
        That(tayberrs.OrdinaryEntryTicket == new DungeonRequiredItemRow(RefinedStone, 1, 1) && tayberrs.StartFatigue == 10 &&
            dungeons.Get(TayberrsSecond).OrdinaryEntryTicket == new DungeonRequiredItemRow(RefinedStone, 1, 1) && dungeons.Get(TayberrsSecond).StartFatigue == 10,
            "泰波尔斯 291100320/291100322：[required item] 10158964 1 1 是入场票，[use fatigue only start dungeon] 10");
        var guide = dungeons.Get(5349);
        That(guide.DeclaresRequiredItem && guide.OrdinaryEntryTicket is null && guide.StartFatigue == 10,
            "5349 的 [required item] 不是「物品 1 1」：不当作入场票，入场疲劳 10 照常解析");
        That(dungeons.Get(7301).DeclaresRequiredItem && dungeons.Get(7301).OrdinaryEntryTicket is null,
            "强袭 7301 的 [required item]（数量 10）不是入场票，仍走强袭自己的消耗");
        That(dungeons.Get(3600).OrdinaryEntryTicket?.ItemId == 10004322 && dungeons.Get(7100).OrdinaryEntryTicket?.ItemId == 3332 &&
            dungeons.Get(11008).OrdinaryEntryTicket?.ItemId == 4473,
            "受影响的其他「物品 1 1」副本按资源解析入场票（3600/7100/11008）");
        var twoSections = tayberrs with { RawSections = [.. tayberrs.RawSections,
            new DungeonRawSection("use fatigue only start dungeon", [new PvfToken(0, 3)])] };
        var twoItems = tayberrs with { RawSections = [.. tayberrs.RawSections.Where(row => row.Tag != "required item"),
            new DungeonRawSection("required item", [new PvfToken(0, RefinedStone), new PvfToken(0, 2), new PvfToken(0, 1)])] };
        That(twoSections.DeclaresStartFatigue && twoSections.StartFatigue is null && twoItems.DeclaresRequiredItem && twoItems.OrdinaryEntryTicket is null,
            "重复的入场疲劳段或数量不是 1 的 [required item] 不被猜测成入场费用");

        // #510 代号希望 8523：未完成 6170 时给出 selection-quest-required:6170。
        GateRejected(towns, characters, skills, growth, quests, dungeons, rooms, 8523, 6170);
        // #511 泰波尔斯：未完成 6116 时两个副本都给出 selection-quest-required:6116。
        GateRejected(towns, characters, skills, growth, quests, dungeons, rooms, Tayberrs, 6116);
        GateRejected(towns, characters, skills, growth, quests, dungeons, rooms, TayberrsSecond, 6116);

        var (done, position) = Selection(towns, Tayberrs);
        done.Add(6116);
        int[] completed = [.. done.Order()];
        // 泰波尔斯只开放指定难度；取资源允许的最低难度。
        byte difficulty = (byte)Enumerable.Range(0, 5).First(value => tayberrs.AllowsDifficulty((byte)value, false));
        // 缺入场票：以 entry-item-required 拒绝，不写库。
        using (var poor = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, completed, position: position))
        {
            var before = poor.Read();
            That(poor.Session.EnterFormalDungeon(Tayberrs, 0, difficulty, dungeons, rooms, pieces: items.EpicPieces) is null &&
                poor.Session.LastFormalDungeonRejection == "entry-item-required:" + RefinedStone &&
                poor.Read().Revision == before.Revision && poor.Read().Fatigue == before.Fatigue &&
                poor.Read().DungeonState == before.DungeonState,
                "没有精炼的时空石：拒绝原因 entry-item-required:10158964，revision、疲劳与尝试号不变：" + poor.Session.LastFormalDungeonRejection);
            That(poor.Session.EnterFormalDungeon(Tayberrs, 0, difficulty, dungeons, rooms) is null &&
                poor.Session.LastFormalDungeonRejection == "entry-item-catalog-unavailable",
                "无法规划入场票时保守拒绝，不免费放行");
        }
        // 疲劳不足 N：以 start-fatigue-insufficient 拒绝，票不动。
        using (var tired = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, completed, position: position,
            items: [(RefinedStone, 2)], fatigueRemaining: 9))
        {
            var before = tired.Read();
            That(before.Fatigue?.Remaining == 9 && tired.Session.EnterFormalDungeon(Tayberrs, 0, difficulty, dungeons, rooms, pieces: items.EpicPieces) is null &&
                tired.Session.LastFormalDungeonRejection == "start-fatigue-insufficient:10" && tired.Read().Revision == before.Revision &&
                Stones(tired.Read()) == 2 && tired.Read().Fatigue == before.Fatigue,
                "剩余疲劳 9 < 10：拒绝原因 start-fatigue-insufficient:10，票与疲劳不变：" + tired.Session.LastFormalDungeonRejection);
        }
        // 票够、疲劳够：入场提交一次扣 1 张票 + 10 点疲劳；重复请求与首次加载都不再扣。
        using (var rich = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, completed, position: position,
            items: [(RefinedStone, 2)]))
        {
            var before = rich.Read();
            var receipt = rich.Session.EnterFormalDungeon(Tayberrs, 0, difficulty, dungeons, rooms, pieces: items.EpicPieces);
            var after = rich.Read();
            That(receipt is not null && receipt.Entry.EntryItem == new DungeonRequiredItemRow(RefinedStone, 1, 1) && receipt.Entry.StartFatigue == 10 &&
                receipt.EntryFatigueCharged == 10 && !receipt.EntryItemConsumedSlots.IsDefaultOrEmpty && Stones(after) == 1 &&
                after.Fatigue?.Remaining == before.Fatigue!.Remaining - 10 && after.Fatigue?.Used == before.Fatigue.Used + 10 &&
                after.Revision == before.Revision + 2 && after.DungeonState?.LastAttempt == before.DungeonState!.LastAttempt + 1 &&
                receipt.Entry.CharacterRevision == after.Revision,
                "入场提交：扣 1 张 10158964、扣 10 点疲劳、尝试号 +1，同一事务：" + rich.Session.LastFormalDungeonRejection);
            That(Receipts(rich, CharacterFatigueState.StartDungeonRuleId) == 1 && Receipts(rich, CharacterFatigueState.NormalRoomRuleId) == 0,
                "入场一次性疲劳写一条 local-start-dungeon-fatigue-v1 账本，不写逐房账本");
            That(rich.Session.EnterFormalDungeon(Tayberrs, 0, difficulty, dungeons, rooms, pieces: items.EpicPieces) is null &&
                Stones(rich.Read()) == 1 && rich.Read().Fatigue == after.Fatigue && rich.Read().Revision == after.Revision,
                "重复入场请求被拒，不再扣票或疲劳");
            var loaded = rich.Session.CompleteFormalDungeonLoading();
            That(loaded is not null && loaded.Snapshot.ConsumedFatigue == 10 && rich.Read().Fatigue == after.Fatigue &&
                rich.Read().Revision == after.Revision && Receipts(rich, CharacterFatigueState.NormalRoomRuleId) == 0,
                "首次房间加载不再逐房扣点，当局应耗疲劳计 10");
            That(rich.Session.CompleteFormalDungeonLoading() is null && rich.Read().Fatigue == after.Fatigue && Stones(rich.Read()) == 1,
                "重复加载不重复扣");
        }
        // 仓储原子性：疲劳 owner 在事务内被发现已变化时，已写入的扣票随整笔回滚。
        using (var race = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, completed, position: position,
            items: [(RefinedStone, 2)]))
        {
            var expected = race.Read();
            var plan = InventoryMaterialConsumptionService.Plan(expected.Inventory, [new ItemMaterialCost(RefinedStone, 1)], items.EpicPieces,
                DateTimeOffset.UtcNow).Plan!;
            var identity = new DungeonRunIdentity(expected.Id, 0, expected.DungeonState!.LastAttempt + 1, Guid.NewGuid(), 1);
            race.Sql("UPDATE character_fatigue SET remaining=remaining-1");
            Reject<InvalidOperationException>(() => race.Repository.CommitDungeonEntry(race.Owner, expected, expected.DungeonState.NextObjectKey,
                new DungeonEntryCharge(plan, 10, identity, 1)), "疲劳原owner变化时整笔拒绝");
            var current = race.Read();
            That(Stones(current) == 2 && current.Revision == expected.Revision && current.DungeonState == expected.DungeonState &&
                current.Fatigue?.Remaining == expected.Fatigue!.Remaining - 1 && Receipts(race, CharacterFatigueState.StartDungeonRuleId) == 0,
                "提交失败不留半扣：票、revision、尝试号与账本都保持原状");
            Reject<InvalidDataException>(() => race.Repository.CommitDungeonEntry(race.Owner, current, current.DungeonState!.NextObjectKey,
                new DungeonEntryCharge(null, 10, identity with { TaskAttempt = current.DungeonState.LastAttempt + 2 }, 1)),
                "入场费用必须绑定本次待安装的尝试号");
        }
        Console.WriteLine($"Dungeon entry cost checks passed: {Count - checks}");
    }

    static void GateRejected(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog skills, CharacterGrowthCatalog growth,
        QuestCatalog quests, DungeonCatalog dungeons, DungeonRoomCatalog rooms, int dungeonId, int questId)
    {
        var (done, position) = Selection(towns, dungeonId);
        done.Remove(questId);
        using (var blocked = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, [.. done.Order()], position: position))
        {
            var before = blocked.Read();
            That(blocked.Session.EnterFormalDungeon(checked((uint)dungeonId), 0, 0, dungeons, rooms) is null &&
                blocked.Session.LastFormalDungeonRejection == "selection-quest-required:" + questId && blocked.Read().Revision == before.Revision,
                dungeonId + " 未完成 " + questId + "：拒绝原因 selection-quest-required:" + questId + "（实际 " + blocked.Session.LastFormalDungeonRejection + "）");
        }
        done.Add(questId);
        using var open = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, [.. done.Order()], position: position);
        open.Session.EnterFormalDungeon(checked((uint)dungeonId), 0, 0, dungeons, rooms);
        That(open.Session.LastFormalDungeonRejection?.StartsWith("selection-", StringComparison.Ordinal) != true,
            dungeonId + " 完成 " + questId + " 后不再被选图准入拒绝（实际 " + open.Session.LastFormalDungeonRejection + "）");
    }

    static (HashSet<int> Done, Location Position) Selection(TownCatalog towns, int dungeonId)
    {
        var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world && towns.DungeonMaps.TryGetValue(world, out var map) &&
            map.Error is null && map.Options.Any(option => option.DungeonId == dungeonId));
        var done = new HashSet<int>(area.Quests);
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        foreach (var gate in area.Map(done).DungeonGates.Where(gate => gate.Conditions.Length == 0).Take(1)) done.UnionWith(gate.Quests);
        var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        return (done, new Location(area.Town, area.Area, checked((ushort)(walk.X + walk.Width / 2)), checked((ushort)(walk.Y + walk.Height / 2))));
    }

    static int Stones(Character character) => character.Inventory.Items.Where(row => row.Core.ItemId == RefinedStone).Sum(row => row.Core.Count);

    static long Receipts(QuestSceneTests.Fixture fixture, string rule) =>
        (long)fixture.Query("SELECT count(*) FROM character_dungeon_fatigue_receipts WHERE rule_id='" + rule + "'")!;
}
