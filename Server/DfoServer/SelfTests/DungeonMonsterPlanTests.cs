using static DfoServer.Testing.TestCheck;
using System.Collections;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class DungeonMonsterPlanTests
{
    public static void RunCore()
    {
        int checks = Count;
        var source = new DungeonRoomMonster(1, Array.AsReadOnly(new[] { 1, 0, 3, 100, 100, 0, 1, 1 }),
            "[fixed]", "[normal]", "monster/fixture.mob");
        var room = new DungeonRoomDefinition(new("boss", new(0, 0), 1, "map/fixture.map"),
            Array.AsReadOnly(new[] { source with { RankKind = "[boss]" }, source }), [], []);
        var defaultPlan = DungeonMonsterPlan.FromRoom(room, 99, 10);
        That(defaultPlan.Monsters.All(monster => monster.IsBlocking), "missing teams default every source row to blocking");
        That(DungeonMonsterPlan.FromRoom(room with { MonsterTeams = [] }, 99, 10).Monsters.All(monster => monster.IsBlocking),
            "empty team section keeps default blocking behavior");
        var teams = room with { MonsterTeams = Array.AsReadOnly(new[] { 100, 0 }) };
        var plan = DungeonMonsterPlan.FromRoom(teams, 99, 10);
        That(plan.Monsters.Select(monster => monster.IsBlocking).SequenceEqual([true, false]),
            "team zero alone excludes a friendly actor from the clear requirement");
        That(plan.Monsters.Select(monster => monster.ObjectKey).SequenceEqual(new ushort[] { 10, 11 }) &&
            plan.Monsters.Select(monster => monster.SourceOrdinal).SequenceEqual([0, 1]) &&
            plan.Monsters.Select(monster => monster.SpawnType).SequenceEqual(new byte[] { 3, 0 }) &&
            ReferenceEquals(plan.Monsters[1].Source, room.Monsters[1]), "friendly actor keeps its source ordinal key level and spawn type");
        That(DungeonMonsterPlan.FromRoom(room with { MonsterTeams = [0] }, 99, 10).Monsters.Select(monster => monster.IsBlocking)
            .SequenceEqual([false, true]), "missing trailing team defaults to blocking by its source ordinal");
        That(DungeonMonsterPlan.FromRoom(room with { MonsterTeams = [-1, int.MaxValue] }, 99, 10).Monsters.All(monster => monster.IsBlocking),
            "all nonzero team values remain blocking without guessed team enums");
        bool rejected = false;
        try { DungeonMonsterPlan.FromRoom(room with { MonsterTeams = [100, 0, 0] }, 99, 10); }
        catch (InvalidDataException) { rejected = true; }
        That(rejected, "synthetic metadata cannot add team indices beyond the resource roster");
        That(new DungeonPlannedMonster(0, 10, 1, 3, source).IsBlocking,
            "existing fixture constructors keep ordinary blocking behavior");
        var apcs = new[] { "[normal]", "[champion]", "[super champion]", "[boss]" }.Select((rank,index) =>
            new DungeonRoomAiCharacter(2000+index,[2000+index,100,200,0,0,0],"[monster]",rank,"aicharacter/fixture.aic",32)).ToArray();
        var mixed = DungeonMonsterPlan.FromRoom(room with { AiCharacters = apcs },99,10);
        That(mixed.Monsters.Select(row => row.SourceOrdinal).SequenceEqual([0,1,0,1,2,3]) &&
            mixed.Monsters.Skip(2).Select(row => row.SpawnType).SequenceEqual(new byte[] { 5,6,7,8 }) &&
            mixed.Monsters.Select(row => row.ObjectKey).SequenceEqual(new ushort[] { 10,11,12,13,14,15 }),
            "APC独立资源行索引和四种类型保留，全部演员共享唯一对象key空间");
        That(mixed.Monsters.Skip(2).All(row => row.Source is null && row.AiCharacter is not null && row.CanReportDeath && !row.IsBlocking) &&
            mixed.Monsters.Skip(2).Select(row => row.ExperienceType).SequenceEqual(new byte[] { 0,1,2,3 }) && mixed.Monsters[^1].IsBoss,
            "敌对APC拥有真实死亡资格和自身经验品级，同时保持普通门不阻塞合同");
        foreach (string faction in new[] { "[character]", "[neutral]" })
            That(!DungeonMonsterPlan.FromRoom(room with { AiCharacters = [apcs[^1] with { Faction = faction }] },99,10).Monsters[^1].CanReportDeath,
                "友方和中立APC不能通过死亡报告生成奖励或Boss通关事实");
        Console.WriteLine($"PASS {Count - checks} dungeon blocking monster plan assertions");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;

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

            That(rejected, name);
        }

        var definition = DungeonCatalog.Load(pvf).Get(3);
        var maze = definition.Mazes.Single(candidate => candidate.QuestConnectionTokens.Count == 3
            && candidate.QuestConnectionTokens[1] == new PvfToken(0, 3145));
        var catalog = DungeonRoomCatalog.Load(pvf);
        var apcBoss = catalog.Get(new("boss",new(0,0),53529,"map/Cataclysm/Behemoth/07_2ndBackbone/3213_53529.map"),requirePassiveDefinitions: false);
        var apcPlan = DungeonMonsterPlan.FromRoom(apcBoss,99,200);
        That(apcBoss.Monsters.Count == 0 && apcPlan.Monsters is [{ MonsterId: 2004, SourceOrdinal: 0, CandidateLevel: 32, SpawnType: 8,
            IsBlocking: false, CanReportDeath: true, IsBoss: true, AiCharacter.Faction: "[monster]" }],
            "53529真实敌对阿甘左APC Boss使用AIC声明Lv32，没有普通怪物替身或空房间冒充通关");
        var firstMap = maze.MapSpecification!.Single(map => map.Id == 76121);
        var first = DungeonMonsterPlan.Create(catalog, firstMap, 11, 10);
        That(first.Room.Map == firstMap && first.Monsters.Count == 4, "matching first room produces all four monsters");
        That(first.Monsters.Select(item => item.ObjectKey).SequenceEqual(new ushort[] { 10, 12, 13, 14 }),
            "object allocation skips the current character without replacing a resource row");
        That(first.Monsters.Select(item => item.SourceOrdinal).SequenceEqual([0, 1, 2, 3])
            && first.Monsters.All(item => item.MonsterId == 1 && item.CandidateLevel == 3),
            "source ordinals template IDs and candidate levels remain separate");
        That(first.Monsters.Select((item, index) => ReferenceEquals(item.Source, first.Room.Monsters[index])).All(value => value)
            && first.Monsters[0].Source!.RawValues.SequenceEqual([1, 0, 3, 573, 197, 0, 1, 1]),
            "plan retains the original resource reference and complete row");
        That(first.Monsters is IList list && list.IsReadOnly, "planned monster collection is read only");

        var nextMap = maze.MapSpecification!.Single(map => map.Id == 76122);
        var next = DungeonMonsterPlan.Create(catalog, nextMap, 99, 20);
        That(next.Room.Map == nextMap && next.Monsters.Count == 5
            && next.Monsters.Select(item => item.MonsterId).SequenceEqual([1, 1, 1, 1, 2])
            && next.Monsters.Select(item => item.ObjectKey).SequenceEqual(new ushort[] { 20, 21, 22, 23, 24 }),
            "next room uses its own five source rows rather than the first room roster");
        That(next.Monsters[^1].Source!.Path == "monster/goblin/goblinthrower.mob"
            && next.Monsters[^1].Source!.RawValues.SequenceEqual([2, 0, 3, 522, 244, 0, 1, 1]),
            "next room thrower keeps its resource identity and full placement row");
        foreach (var expected in new[] { (Map: 76123, Count: 7), (Map: 76124, Count: 5) })
        {
            var map = maze.MapSpecification!.Single(value => value.Id == expected.Map);
            var plan = DungeonMonsterPlan.Create(catalog, map, 99, 100);
            That(plan.Monsters.Count == expected.Count && plan.Monsters.All(value => value.CandidateLevel == 3),
                "3145中间真实房间保持完整固定普通怪物行");
        }
        var fifth = catalog.Get(maze.MapSpecification!.Single(map => map.Id == 76125));
        That(fifth.Monsters.Count == 6 && fifth.Monsters[4].RawValues.SequenceEqual([1, 1, 0, 503, 233, 0, 1, 1]),
            "76125第五行特殊整数原样保留，不替换为前三整数1/0/3");
        var fifthPlan = DungeonMonsterPlan.FromRoom(fifth, 99, 100, definition.BasisLevel);
        That(fifthPlan.Monsters.Count == 6 && fifthPlan.Monsters[4].CandidateLevel == definition.BasisLevel &&
            fifthPlan.Monsters[4].SourceOrdinal == 4 && fifthPlan.Monsters[4].SpawnType == 0 &&
            ReferenceEquals(fifthPlan.Monsters[4].Source, fifth.Monsters[4]) &&
            fifthPlan.Monsters.Where(monster => monster.SourceOrdinal != 4).All(monster => monster.CandidateLevel == 3),
            "76125完整六行保留，相对等级资源行按副本basis5加偏移0生成Lv5");
        var bossRoom = catalog.Get(maze.MapSpecification!.Single(map => map.Id == 76126));
        That(bossRoom.Monsters.Count == 2 && bossRoom.Monsters[0].MonsterId == 107000903 &&
            bossRoom.Monsters[0].RankKind == "[boss]" && bossRoom.Monsters[1].MonsterId == 107000906 &&
            bossRoom.Monsters[1].RankKind == "[normal]", "76126真实Boss与普通怪物身份分别保留");
        var bossPlan = DungeonMonsterPlan.FromRoom(bossRoom, 99, 100);
        That(bossPlan.Monsters.Count == 2 && bossPlan.Monsters.All(monster => monster.CandidateLevel == 3) &&
            bossPlan.Monsters.Select(monster => monster.SpawnType).SequenceEqual(new byte[] { 3, 0 }),
            "76126完整Boss与普通怪物计划分别保留类型3和0，等级均为资源Lv3");
        That(bossRoom.MonsterTeams!.SequenceEqual([100, 0]) &&
            bossPlan.Monsters.Select(monster => monster.IsBlocking).SequenceEqual([true, false]),
            "76126友方任务对象仍生成完整actor，但不作为清房敌人");
        That(DungeonMonsterPlan.FromRoom(first.Room, 99, 10).Monsters.Select(item => item.ObjectKey)
            .SequenceEqual(new ushort[] { 10, 11, 12, 13 }), "unoccupied keys remain consecutive");
        That(DungeonMonsterPlan.FromRoom(first.Room, 10, 10).Monsters[0].ObjectKey == 11,
            "allocation skips a character collision at the initial key");
        That(DungeonMonsterPlan.FromRoom(first.Room, 0, 65532).Monsters[^1].ObjectKey == ushort.MaxValue,
            "last ushort key is usable when it does not collide");
        Reject<InvalidDataException>(() => DungeonMonsterPlan.FromRoom(first.Room, 0, 65533), "allocation refuses overflow instead of wrapping");
        Reject<InvalidDataException>(() => DungeonMonsterPlan.FromRoom(first.Room, ushort.MaxValue, 65532),
            "skipping the last ushort character key refuses exhausted capacity");

        var source = first.Room.Monsters[0];
        DungeonRoomDefinition With(DungeonRoomMonster value) => first.Room with { Monsters = Array.AsReadOnly(new[] { value }), MonsterTeams = null };
        void Unsupported(DungeonRoomMonster value, string name)
            => Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(With(value), 99, 10), name);
        Unsupported(source with { RawValues = [1, 0, 3, 573, 197, 0, 1] }, "unknown short row stays unsupported");
        Unsupported(source with { RawValues = [1, 0, 3, 573, 197, 0, 1, 1, 0] }, "unknown long row stays unsupported");
        Unsupported(source with { PlacementKind = "[random]" }, "random placement stays unsupported");
        That(DungeonMonsterPlan.FromRoom(With(source with { RankKind = "[boss]" }), 99, 10).Monsters[0].SpawnType == 3,
            "匹配固定Boss品级进入独立类型3，不能仍当普通0");
        Unsupported(source with { RankKind = "[future]" }, "unknown rank stays unsupported");
        foreach (int field in new[] { 1, 6, 7 })
        {
            int[] raw = source.RawValues.ToArray();
            raw[field] = field == 1 ? 2 : 256;
            Unsupported(source with { RawValues = Array.AsReadOnly(raw) }, "unknown monster condition " + field + " stays unsupported");
        }
        int[] counts = source.RawValues.ToArray(); counts[6] = 2; counts[7] = 2;
        That(DungeonMonsterPlan.FromRoom(With(source with { RawValues = counts }),99,10).Monsters.Count == 1,
            "资源尾部掉落数量2保留，不能误解释成未知创建条件而拒绝合法怪物");

        Unsupported(source with { MonsterId = 2 }, "inconsistent resource template ID stays unsupported");
        var special = fifth.Monsters[4];
        DungeonRoomDefinition ChangedSpecial(DungeonRoomMonster changed) => fifth with
        {
            Monsters = Array.AsReadOnly(fifth.Monsters.Select((monster, index) => index == 4 ? changed : monster).ToArray())
        };
        Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(fifth,99,100), "相对等级缺少副本基础时整批拒绝");
        var relocated = fifth with { Map = fifth.Map with { Id = 100, Path = "map/fixture.map" },
            Monsters = Array.AsReadOnly(fifth.Monsters.Reverse().ToArray()) };
        That(DungeonMonsterPlan.FromRoom(relocated,99,100,7).Monsters.Single(row => row.Source!.RawValues[1] == 1).CandidateLevel == 7,
            "相对等级按副本基础计算，和地图编号、路径、模板及资源顺序无关");
        int[] relative = special.RawValues.ToArray(); relative[2] = -2;
        That(DungeonMonsterPlan.FromRoom(ChangedSpecial(special with { RawValues = relative }),99,100,7).Monsters[4].CandidateLevel == 5,
            "相对等级保留有符号偏移，不把负偏移截断");
        relative = special.RawValues.ToArray(); relative[1] = 2;
        Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(ChangedSpecial(special with { RawValues = relative }),99,100,7),
            "未知相对等级标志拒绝");
        Reject<InvalidDataException>(() => DungeonMonsterPlan.FromRoom(fifth,99,100,0),"相对等级计算为零时拒绝生成");
        var mixed = first.Room with { Monsters = Array.AsReadOnly(new[] { source, source with { RankKind = "[future]" } }), MonsterTeams = null };
        Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(mixed, 99, 10),
            "mixed supported and unknown rows reject the whole plan rather than publishing a partial roster");
        var empty = first.Room with { Monsters = Array.Empty<DungeonRoomMonster>(), MonsterTeams = null };
        That(DungeonMonsterPlan.FromRoom(empty, ushort.MaxValue, ushort.MaxValue).Monsters.Count == 0,
            "a genuinely empty resource roster needs no object keys");
        var candidate = source with { RawValues = [1, 0, int.MaxValue, 573, 197, -7, 1, 1] };
        That(DungeonMonsterPlan.FromRoom(With(candidate), 99, 10).Monsters[0].CandidateLevel == int.MaxValue,
            "candidate level and unrelated opaque values are not narrowed or guessed");
        Console.WriteLine($"PASS {Count - checks} dungeon monster plan assertions");
    }
}
