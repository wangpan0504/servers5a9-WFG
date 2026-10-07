using System.Collections;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class DungeonMonsterPlanTests
{
    public static void Run(PvfArchive pvf)
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            checks++;
            if (!condition)
                throw new Exception("FAIL: " + name);
        }

        void Reject<T>(Action action, string name) where T : Exception
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (T)
            {
                rejected = true;
            }

            Check(rejected, name);
        }

        var maze = DungeonCatalog.Load(pvf).Get(3).Mazes.Single(candidate => candidate.QuestConnectionTokens.Count == 3
            && candidate.QuestConnectionTokens[1] == new PvfToken(0, 3145));
        var catalog = DungeonRoomCatalog.Load(pvf);
        var firstMap = maze.MapSpecification!.Single(map => map.Id == 76121);
        var first = DungeonMonsterPlan.Create(catalog, firstMap, 11, 10);
        Check(first.Room.Map == firstMap && first.Monsters.Count == 4, "matching first room produces all four monsters");
        Check(first.Monsters.Select(item => item.ObjectKey).SequenceEqual(new ushort[] { 10, 12, 13, 14 }),
            "object allocation skips the current character without replacing a resource row");
        Check(first.Monsters.Select(item => item.SourceOrdinal).SequenceEqual([0, 1, 2, 3])
            && first.Monsters.All(item => item.MonsterId == 1 && item.CandidateLevel == 3),
            "source ordinals template IDs and candidate levels remain separate");
        Check(first.Monsters.Select((item, index) => ReferenceEquals(item.Source, first.Room.Monsters[index])).All(value => value)
            && first.Monsters[0].Source.RawValues.SequenceEqual([1, 0, 3, 573, 197, 0, 1, 1]),
            "plan retains the original resource reference and complete row");
        Check(first.Monsters is IList list && list.IsReadOnly, "planned monster collection is read only");

        var nextMap = maze.MapSpecification!.Single(map => map.Id == 76122);
        var next = DungeonMonsterPlan.Create(catalog, nextMap, 99, 20);
        Check(next.Room.Map == nextMap && next.Monsters.Count == 5
            && next.Monsters.Select(item => item.MonsterId).SequenceEqual([1, 1, 1, 1, 2])
            && next.Monsters.Select(item => item.ObjectKey).SequenceEqual(new ushort[] { 20, 21, 22, 23, 24 }),
            "next room uses its own five source rows rather than the first room roster");
        Check(next.Monsters[^1].Source.Path == "monster/goblin/goblinthrower.mob"
            && next.Monsters[^1].Source.RawValues.SequenceEqual([2, 0, 3, 522, 244, 0, 1, 1]),
            "next room thrower keeps its resource identity and full placement row");
        foreach (var expected in new[] { (Map: 76123, Count: 7), (Map: 76124, Count: 5) })
        {
            var map = maze.MapSpecification!.Single(value => value.Id == expected.Map);
            var plan = DungeonMonsterPlan.Create(catalog, map, 99, 100);
            Check(plan.Monsters.Count == expected.Count && plan.Monsters.All(value => value.CandidateLevel == 3),
                "3145中间真实房间保持完整固定普通怪物行");
        }
        var fifth = catalog.Get(maze.MapSpecification!.Single(map => map.Id == 76125));
        Check(fifth.Monsters.Count == 6 && fifth.Monsters[4].RawValues.SequenceEqual([1, 1, 0, 503, 233, 0, 1, 1]),
            "76125第五行特殊整数原样保留，不替换为前三整数1/0/3");
        var fifthPlan = DungeonMonsterPlan.FromRoom(fifth, 99, 100);
        Check(fifthPlan.Monsters.Count == 6 && fifthPlan.Monsters[4].CandidateLevel == 1 &&
            fifthPlan.Monsters[4].SourceOrdinal == 4 && fifthPlan.Monsters[4].SpawnType == 0 &&
            ReferenceEquals(fifthPlan.Monsters[4].Source, fifth.Monsters[4]) &&
            fifthPlan.Monsters.Where(monster => monster.SourceOrdinal != 4).All(monster => monster.CandidateLevel == 3),
            "76125完整六行保留，精确特殊资源行使用已指定的本服Lv1");
        var bossRoom = catalog.Get(maze.MapSpecification!.Single(map => map.Id == 76126));
        Check(bossRoom.Monsters.Count == 2 && bossRoom.Monsters[0].MonsterId == 107000903 &&
            bossRoom.Monsters[0].RankKind == "[boss]" && bossRoom.Monsters[1].MonsterId == 107000906 &&
            bossRoom.Monsters[1].RankKind == "[normal]", "76126真实Boss与普通怪物身份分别保留");
        var bossPlan = DungeonMonsterPlan.FromRoom(bossRoom, 99, 100);
        Check(bossPlan.Monsters.Count == 2 && bossPlan.Monsters.All(monster => monster.CandidateLevel == 3) &&
            bossPlan.Monsters.Select(monster => monster.SpawnType).SequenceEqual(new byte[] { 3, 0 }),
            "76126完整Boss与普通怪物计划分别保留类型3和0，等级均为资源Lv3");
        Check(DungeonMonsterPlan.FromRoom(first.Room, 99, 10).Monsters.Select(item => item.ObjectKey)
            .SequenceEqual(new ushort[] { 10, 11, 12, 13 }), "unoccupied keys remain consecutive");
        Check(DungeonMonsterPlan.FromRoom(first.Room, 10, 10).Monsters[0].ObjectKey == 11,
            "allocation skips a character collision at the initial key");
        Check(DungeonMonsterPlan.FromRoom(first.Room, 0, 65532).Monsters[^1].ObjectKey == ushort.MaxValue,
            "last ushort key is usable when it does not collide");
        Reject<InvalidDataException>(() => DungeonMonsterPlan.FromRoom(first.Room, 0, 65533), "allocation refuses overflow instead of wrapping");
        Reject<InvalidDataException>(() => DungeonMonsterPlan.FromRoom(first.Room, ushort.MaxValue, 65532),
            "skipping the last ushort character key refuses exhausted capacity");

        var source = first.Room.Monsters[0];
        DungeonRoomDefinition With(DungeonRoomMonster value) => first.Room with { Monsters = Array.AsReadOnly(new[] { value }) };
        void Unsupported(DungeonRoomMonster value, string name)
            => Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(With(value), 99, 10), name);
        Unsupported(source with { RawValues = [1, 0, 3, 573, 197, 0, 1] }, "unknown short row stays unsupported");
        Unsupported(source with { RawValues = [1, 0, 3, 573, 197, 0, 1, 1, 0] }, "unknown long row stays unsupported");
        Unsupported(source with { PlacementKind = "[random]" }, "random placement stays unsupported");
        Check(DungeonMonsterPlan.FromRoom(With(source with { RankKind = "[boss]" }), 99, 10).Monsters[0].SpawnType == 3,
            "匹配固定Boss品级进入独立类型3，不能仍当普通0");
        Unsupported(source with { RankKind = "[future]" }, "unknown rank stays unsupported");
        foreach (int field in new[] { 1, 6, 7 })
        {
            int[] raw = source.RawValues.ToArray();
            raw[field]++;
            Unsupported(source with { RawValues = Array.AsReadOnly(raw) }, "unknown monster condition " + field + " stays unsupported");
        }

        Unsupported(source with { MonsterId = 2 }, "inconsistent resource template ID stays unsupported");
        var special = fifth.Monsters[4];
        DungeonRoomDefinition ChangedSpecial(DungeonRoomMonster changed) => fifth with
        {
            Monsters = Array.AsReadOnly(fifth.Monsters.Select((monster, index) => index == 4 ? changed : monster).ToArray())
        };
        Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(fifth with
            { Map = fifth.Map with { Id = 76124 } }, 99, 100), "特殊Lv1规则不能移到其他地图");
        Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(fifth with
            { Map = fifth.Map with { Path = "map/fixture.map" } }, 99, 100), "特殊Lv1规则要求匹配地图路径");
        Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(fifth with
            { Monsters = Array.AsReadOnly(fifth.Monsters.Reverse().ToArray()) }, 99, 100), "特殊Lv1规则不能改变资源行顺序");
        foreach (int field in new[] { 1, 2, 3, 4, 5 })
        {
            int[] raw = special.RawValues.ToArray();
            raw[field]++;
            Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(ChangedSpecial(special with
                { RawValues = Array.AsReadOnly(raw) }), 99, 100), "篡改特殊完整资源行不能借本服Lv1例外 " + field);
        }
        Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(ChangedSpecial(special with
            { Path = "monster/fixture.mob" }), 99, 100), "特殊Lv1规则要求匹配怪物资源路径");
        Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(ChangedSpecial(special with
            { RankKind = "[boss]" }), 99, 100), "特殊Lv1规则不能把普通资源行改成Boss");
        var mixed = first.Room with { Monsters = Array.AsReadOnly(new[] { source, source with { RankKind = "[future]" } }) };
        Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(mixed, 99, 10),
            "mixed supported and unknown rows reject the whole plan rather than publishing a partial roster");
        var empty = first.Room with { Monsters = Array.Empty<DungeonRoomMonster>() };
        Check(DungeonMonsterPlan.FromRoom(empty, ushort.MaxValue, ushort.MaxValue).Monsters.Count == 0,
            "a genuinely empty resource roster needs no object keys");
        var candidate = source with { RawValues = [1, 0, int.MaxValue, 573, 197, -7, 1, 1] };
        Check(DungeonMonsterPlan.FromRoom(With(candidate), 99, 10).Monsters[0].CandidateLevel == int.MaxValue,
            "candidate level and unrelated opaque values are not narrowed or guessed");
        Console.WriteLine($"PASS {checks} dungeon monster plan assertions");
    }
}
