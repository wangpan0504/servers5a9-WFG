using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using PvfLib;

namespace DfoServer;

// dgn [required item] 第三列非 0 的入场消耗泛化到所有计次范围入场（任意城镇、非练习普通入场）。数据读匹配的 Script.pvf，非实机。
// 客户端 018E6EDC：第三列非 0 → 47702「消耗%d个」，0 → 47703「持有%d个」；入场时服务端核对并扣除，缺物品拒绝（与 400000016 相同）。
internal static class RequiredItemConsumeTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int before = Count;
        var catalog = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var incounts = DungeonIncountCatalog.Load(pvf);
        var learning = SelfTestCatalogs.SkillLearning(pvf);

        // 普查：dungeon.lst 里写第三列非 0 的副本，及其中能从选图地图选到的。
        string dungeonList = pvf.ReadText("dungeon/dungeon.lst");
        var selectable = towns.DungeonMaps.Values.Where(map => map.Error is null).SelectMany(map => map.Options).Select(option => (int)option.DungeonId).ToHashSet();
        var flagged = new List<(int Id, DungeonDefinition Definition, DungeonRequiredItem Item)>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(dungeonList, @"(\d+)\s+`"))
        {
            int id = int.Parse(match.Groups[1].Value);
            DungeonDefinition dgn;
            try { dgn = catalog.Get(id); }
            catch (Exception) { continue; }
            if (dgn.RequiredItem is { Flag: not 0, Count: > 0 } required) flagged.Add((id, dgn, required));
        }
        var flaggedSelectable = flagged.Where(row => selectable.Contains(row.Id)).ToArray();
        That(flaggedSelectable.Length == 51 && flaggedSelectable.Any(row => row.Id == 400000016) &&
            flaggedSelectable.Where(row => row.Id is >= 5335 and <= 5339).Select(row => row.Id).Order()
                .SequenceEqual(Enumerable.Range(5335,5)) &&
            flaggedSelectable.Where(row => row.Id is >= 5335 and <= 5339)
                .All(row => row.Item is { ItemId:10303752, Count:1, Flag:1 }),
            $"累计候选可选消耗副本51个（含400000016及五个魔兽入口，每图10303752×1）: {flaggedSelectable.Length}，全表 {flagged.Count}");
        Console.WriteLine($"INFO required item consume: flagged {flagged.Count}, selectable {flaggedSelectable.Length}: " +
            string.Join(',', flaggedSelectable.Select(row => $"{row.Id}:{row.Item.ItemId}x{row.Item.Count}")));

        string path = Path.Combine(Path.GetTempPath(), "S5A9-reqitem-" + Guid.NewGuid().ToString("N"), "game.db");
        var repo = new CharacterRepository(path);
        var owners = new AccountOwners();
        int tried = 0, passed = 0;
        var tested = new List<string>();
        // 在普通城镇选图区域（无任务、无条件门）逐个试，至少两个副本完整通过：缺物品拒绝、有物品入场扣除、练习不扣。
        foreach (var (id, definition, required) in flaggedSelectable)
        {
            if (passed >= 3) break;
            if ((definition.MinimumLevel ?? 1) > 95) continue;
            // 选图区域：非频道专属城镇；把区域 [need quest]、任一 [need quest or]、选项任务与门任务都记为已完成，避免与本断言无关的任务条件挡住。
            HashSet<int> Done(TownArea row, int quest)
            {
                var set = new HashSet<int>(row.Quests);
                if (row.AnyQuests.Length != 0) set.Add(row.AnyQuests[0]);
                if (quest > 0) set.Add(quest);
                return set;
            }
            TownArea? area = null; HashSet<int> done = [];
            foreach (var row in towns.Areas.Values)
            {
                if (row.Level > 95 || ChannelAreaLanding.IsChannelBoundTown(towns, row.Town) || row.DungeonWorldMap is not { } world ||
                    !towns.DungeonMaps.TryGetValue(world, out var map) || map.Error is not null) continue;
                var option = map.Options.Where(o => o.DungeonId == id && !o.InProgress).OrderBy(o => o.QuestId == -1 ? 0 : 1).FirstOrDefault();
                if (option is null) continue;
                var set = Done(row, option.QuestId);
                var gate = row.Map(set).DungeonGates.FirstOrDefault(g => g.Conditions.Length == 0);
                if (gate is null) continue;
                foreach (int quest in gate.Quests) set.Add(quest);
                if (row.ExceptQuests.Any(set.Contains)) continue;
                area = row; done = set; break;
            }
            if (area is null) continue;
            var walk = area.Map(done).Bounds.FirstOrDefault(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains));
            if (walk is null) continue;
            tried++;
            // 每个副本用独立账号，避免角色栏位用满。
            using var owner = owners.Acquire("reqitem" + tried, () => { });
            var position = new Location(area.Town, area.Area, checked((ushort)(walk.Bounds.X + walk.Bounds.Width / 2)), checked((ushort)(walk.Bounds.Y + walk.Bounds.Height / 2)));
            var back = towns.ResolveRoomReturn(position, position, 95, done);
            int[] completed = [.. done.Order()];
            NewCharacter Role(string name, int count) =>
                CharacterCreation.Prepare(name, characters.Get(0), position, back, learning.Get) with { Level = 95, CompletedQuests = completed,
                    Inventory = count == 0 ? InventorySnapshot.Empty : InventorySnapshot.Empty with
                        { Items = [new(0, new(InventoryListType.Main, 121), new(required.ItemId, ItemKind.Material, count))] } };            var bare = repo.Create(owner, Role("Rq" + tried + "Bare", 0));
            var held = repo.Create(owner, Role("Rq" + tried + "Held", required.Count + 1));
            bool ok = true;
            string detail = "";

            var s = new GameSession(repo, towns, owner, incounts: incounts);
            s.Select(bare.Slot, resumeTown: true);
            s.MarkReady();
            var receipt = s.EnterFormalDungeon(checked((uint)id), 0, 0, catalog, rooms, null, null, _ => 0, itemResources.EpicPieces);
            ok &= receipt is null && s.LastFormalDungeonRejection == "dungeon-required-item:" + required.ItemId;
            detail += $" bare={(receipt is null ? s.LastFormalDungeonRejection : "entered")}";
            s.ReturnToRoster();

            s = new GameSession(repo, towns, owner, incounts: incounts);
            s.Select(held.Slot, resumeTown: true);
            s.MarkReady();
            int Held(GameSession session) => session.Selected!.Inventory.Items.Where(row => row.Core.ItemId == required.ItemId).Sum(row => row.Core.Value);
            // 难度按 0..4 依次试（部分副本不支持难度 0）；入场计划拒绝发生在提交前，不会扣物品。
            receipt = null;
            for (byte difficulty = 0; difficulty <= 4 && receipt is null; difficulty++)
            {
                receipt = s.EnterFormalDungeon(checked((uint)id), 0, difficulty, catalog, rooms, null, null, _ => 0, itemResources.EpicPieces);
                if (receipt is null && Held(s) != required.Count + 1) ok = false;
            }
            ok &= receipt is { } r && !r.ChannelConsumedSlots.IsDefaultOrEmpty && Held(s) == 1;
            detail += $" held={(receipt is null ? s.LastFormalDungeonRejection : "entered,left " + Held(s))}";
            s.ReturnToRoster();
            // 落库：重新载入后物品仍是扣过的数量。
            var reloaded = new GameSession(repo, towns, owner, incounts: incounts);
            reloaded.Select(held.Slot, resumeTown: true);
            ok &= reloaded.Selected!.Inventory.Items.Where(row => row.Core.ItemId == required.ItemId).Sum(row => row.Core.Value) == 1;
            reloaded.ReturnToRoster();
            tested.Add($"{id}@{area.Town}/{area.Area}({position.X},{position.Y}) q[{string.Join('/', completed)}]:{required.ItemId}x{required.Count}{detail}");
            if (ok) passed++;
        }
        Console.WriteLine($"INFO required item consume entries: passed {passed}/{tried}: {string.Join(" | ", tested)}");
        That(passed >= 2, $"普通城镇 [required item] 第三列非 0 的副本：缺物品拒绝、持有则入场扣 Count 个并落库（通过 {passed}/{tried}）: {string.Join(" | ", tested)}");

        // 未装载 DungeonIncountInfo 的旧夹具与频道专属城镇行为由 FiendwarChannelTests 覆盖（400000016 扣 1 个 490007844、缺物品拒绝）。
        Console.WriteLine($"PASS {Count - before} [required item] 入场消耗泛化断言（非实机）");
    }
}
