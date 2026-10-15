using static DfoServer.Testing.TestCheck;
using System.Collections;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Dungeons;
using DfoServer.Testing;
using PvfLib;

namespace DfoServer;

internal static class DungeonRoomCatalogTests
{
    public static void RunCore()
    {
        int checks = Count;
        var fixture = new Fixture();
        That(fixture.Parse([fixture.Tag("type"),fixture.String("[boss]")]).DeclaredType == "[boss]" &&
            fixture.Parse(fixture.Monster()).DeclaredType is null,
            "MAP真实类型独立保留，迷宫身份不补造缺失类型");
        That(fixture.Parse([fixture.Tag("type"),fixture.String("[boss]"),fixture.Tag("type"),fixture.String("[boss]")]).DeclaredType is null &&
            fixture.Parse([fixture.Tag("type"),fixture.String("[boss]"),new(0,1)]).DeclaredType is null,
            "重复或多值MAP类型不能授予末层终点资格");
        var npcTokens = fixture.Block("NPC", [new(0,1000),fixture.String("[left]"),new(0,0),new(0,0),new(0,0),
            new(0,1298),fixture.String("[left]"),new(0,485),new(0,284),new(0,0)]);
        var npcRoom = fixture.Parse(npcTokens);
        That(npcRoom.NpcMetadataError is null && npcRoom.Npcs.Select(row => row.Id).SequenceEqual([1000,1298]),
            "地图NPC投影保留不同演员的完整来源，不用位置字段匹配任务NPC");
        var unresolvedNpc = fixture.Parse([.. fixture.Monster(),.. npcTokens[..^1]]);
        That(unresolvedNpc.NpcMetadataError is not null && unresolvedNpc.Npcs.Count == 0 && unresolvedNpc.Monsters.Count == 1,
            "未闭合NPC不授予会面来源，同时保留原可用怪物与入场能力");
        var positions = fixture.Parse(fixture.Block("event monster position", [new(0, 330), new(0, -93), new(0, 0)]));
        That(positions.EventMonsterPositions.SequenceEqual([330, -93, 0]), "事件怪物位置保留三坐标与负值");
        var special = fixture.Block("special passive object", [new(0, 3), new(0, -20), new(0, 150), new(0, 0), new(0, 2),
            fixture.String("[trap]"), new(0, 40002), new(0, 40), new(0, 1), new(0, 10), new(0, -1),
            fixture.String("[monster]"), new(0, 1), new(0, 5), new(0, 3), new(0, 10), new(0, -1)]);
        var parsedSpecial = fixture.Parse(special).SpecialObjects;
        That(parsedSpecial.Count == 1 && parsedSpecial[0].SourceOrdinal == 0 && parsedSpecial[0].RawValues.SequenceEqual([3, -20, 150, 0, 2]) &&
            parsedSpecial[0].Children.Select(child => child.Kind).SequenceEqual(["[trap]", "[monster]"]) &&
            parsedSpecial[0].Children[1].SourceOrdinal == 1 && parsedSpecial[0].Children[1].Path == "monster/fixture.mob" &&
            parsedSpecial[0].Children[0].RawValues.SequenceEqual([40002, 40, 1, 10, -1]), "特殊对象保留父位置、原序与所有子规则，不遗漏非怪物机关");
        special[2] = new(0, 99);
        That(parsedSpecial[0].RawValues[1] == -20 && parsedSpecial is IList specialList && specialList.IsReadOnly,
            "特殊对象父子集合独立冻结原输入");
        var wrongCount = (PvfToken[])special.Clone(); wrongCount[5] = new(0, 3);
        Reject(wrongCount, "特殊对象数量越界拒绝，不发布局部子集合");
        void Reject(PvfToken[] tokens, string name)
        {
            bool rejected = false;
            try { fixture.Parse(tokens); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException) { rejected = true; }
            That(rejected, name);
        }
        Reject(fixture.Block("event monster position", [new(0, 330), new(0, 93)]), "事件怪物坐标不完整时拒绝");
        var row = fixture.Monster()[1..^1];
        var champion = fixture.Monster(rank: "[champion]")[1..^1];
        var withChampion = fixture.Parse(fixture.Block("monster", [.. champion, new(0, 0), .. row]));
        That(withChampion.Monsters.Count == 2 && withChampion.Monsters[0].RankParameterCount == 0 &&
            withChampion.Monsters[1].RankKind == "[normal]", "冠军空附加数量不吃掉下一行模板，不遗漏普通怪物");
        Reject(fixture.Block("monster", [.. champion, new(0, 1), .. row]), "未恢复的非空冠军附加不能被截断");
        Reject(fixture.Block("monster", champion), "冠军缺少数量字段不能当普通行");
        var monsters = fixture.Block("monster", [.. row, .. row]);
        var teamTokens = fixture.Block("monster team", [new(0, 100), new(0, 0)]);
        var parsed = fixture.Parse([.. monsters, .. teamTokens]);
        That(parsed.MonsterTeams!.SequenceEqual([100, 0]) && parsed.Monsters.Count == 2,
            "team values retain positional source order beside the complete actor roster");
        That(parsed.Monsters.Select(monster => monster.Team).SequenceEqual(new int?[] { 100,0 }) &&
            fixture.Parse(monsters).Monsters.All(monster => monster.Team is null),
            "每条源记录保留对应原始阵营，缺失不补0");
        teamTokens[1] = new(0, 0);
        That(parsed.MonsterTeams!.SequenceEqual([100, 0]) && parsed.MonsterTeams is IList teams && teams.IsReadOnly,
            "parsed teams are read only and do not retain mutable input tokens");
        That(fixture.Parse(monsters).MonsterTeams is { Count: 0 }, "absent team section preserves default blocking metadata");
        That(fixture.Parse([.. monsters, .. fixture.Block("monster team", [])]).MonsterTeams is { Count: 0 },
            "explicitly empty team section is valid");
        That(fixture.Parse([.. monsters, .. fixture.Block("monster team", [new(0, 0)])]).MonsterTeams!.SequenceEqual([0]),
            "missing trailing team entries remain absent instead of inventing friendly values");
        That(fixture.Parse([.. monsters, .. fixture.Block("monster team", [new(0, int.MinValue), new(0, int.MaxValue)])])
            .MonsterTeams!.SequenceEqual([int.MinValue, int.MaxValue]), "nonzero team integers stay opaque and complete");
        That(fixture.Parse([.. monsters, .. fixture.Block("monster team", [new(0,-1)])]).Monsters.Select(monster => monster.Team)
            .SequenceEqual(new int?[] { -1,null }), "原始负值与尾部缺项独立保留，由生成计划解释回退");
        Reject([.. monsters, .. fixture.Block("monster team", [new(6, fixture.String("0").Value)])], "nonnumeric team rejected");
        Reject([.. monsters, .. fixture.Block("monster team", [new(0, 100), new(0, 0), new(0, 0)])], "excess team index rejected");
        Reject(fixture.Block("monster team", [new(0, 0)]), "team without corresponding monster rejected");
        Reject([.. monsters, fixture.Tag("monster team"), new(0, 100)], "unclosed team section rejected");
        Reject([.. monsters, .. fixture.Block("monster team", []), .. fixture.Block("monster team", [])], "duplicate team sections rejected");
        That(fixture.Parse([.. monsters, .. fixture.Block("future room data", [new(6, fixture.String("opaque").Value)])]).Monsters.Count == 2,
            "unrelated unknown metadata keeps existing parsing behavior");
        var flags = fixture.Block("monster", [.. row[..^1], fixture.String("[dummy]"), row[^1], .. row]);
        var flagged = fixture.Parse(flags);
        That(flagged.Monsters is [{ PlacementKind: "[fixed]", RankKind: "[normal]", Modifiers: ["[dummy]"] }, { Modifiers.Count: 0 }] &&
            flagged.Monsters[0].Modifiers is IList modifiers && modifiers.IsReadOnly,
            "可变标记保留中间dummy与末尾品级，不吞掉下一行，也不暴露可写集合");
        TestCheck.Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(flagged,1,100),
            "未确认的dummy标记不能因资源解析成功而生成普通怪物");
        var npcValues = new[] { 62531, 1, 0, 912, 394, 0, 1, 1 };
        var npcPrefix = fixture.Monster(npcValues)[1..^2];
        var npcMarker = fixture.String("[NPC]");
        var dummyMarker = fixture.String("[dummy]");
        var displayMarker = fixture.String("[displayhuntdummy]");
        var bossMarker = fixture.String("[boss]");
        var npcMonsterTokens = fixture.Block("monster", [.. npcPrefix, npcMarker, new(0,0), dummyMarker, bossMarker, .. row]);
        var npcMonsters = fixture.Parse([.. npcMonsterTokens, .. npcTokens]);
        That(npcMonsters.Monsters is [{ MonsterId: 62531, NpcParameter: 0, RankKind: "[boss]", Modifiers: ["[NPC]", "[dummy]"] },
                { MonsterId: 1, NpcParameter: null, RankKind: "[normal]", Modifiers.Count: 0 }] &&
            npcMonsters.Monsters[0].RawValues.SequenceEqual(npcValues) && npcMonsters.Npcs.Select(npc => npc.Id).SequenceEqual([1000,1298]),
            "真实NPC0/dummy/Boss原行保留八整数与参数，下一普通怪和独立地图NPC节不混淆");
        npcMonsterTokens[npcPrefix.Length + 2] = new(0,9);
        That(npcMonsters.Monsters[0].NpcParameter == 0 && npcMonsters.Monsters[0].Modifiers is IList npcModifiers && npcModifiers.IsReadOnly,
            "原NPC参数及修饰独立于输入修改，缺省不会被补成0");
        TestCheck.Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(npcMonsters,1,100,62),
            "NPC与dummy组合的解析不授予普通演员生成、战斗或奖励资格");
        foreach (int parameter in new[] { -1, int.MinValue, int.MaxValue })
            That(fixture.Parse(fixture.Block("monster", [.. npcPrefix, npcMarker, new(0,parameter), bossMarker])).Monsters[0].NpcParameter == parameter,
                "NPC原整数保留显式负值及边界，不猜编号范围或布尔含义");
        var display = fixture.Parse(fixture.Block("monster", [.. npcPrefix, npcMarker, new(0,0), dummyMarker, displayMarker, bossMarker, .. row]));
        That(display.Monsters[0].Modifiers.SequenceEqual(["[NPC]", "[dummy]", "[displayhuntdummy]"]) && display.Monsters[1].NpcParameter is null,
            "NPC后的dummy与displayhuntdummy按原顺序保留，不吞下一行");
        That(fixture.Parse(fixture.Block("monster", [.. npcPrefix, npcMarker, new(0,0), displayMarker, bossMarker])).Monsters[0].Modifiers
            .SequenceEqual(["[NPC]", "[displayhuntdummy]"]), "独立displayhuntdummy声明不补造dummy标记");
        var npcChampion = fixture.Parse(fixture.Block("monster", [.. npcPrefix, npcMarker, new(0,0), fixture.String("[champion]"), new(0,0), .. row]));
        That(npcChampion.Monsters is [{ NpcParameter: 0, RankParameterCount: 0 }, { NpcParameter: null, RankKind: "[normal]" }],
            "NPC参数和冠军空附加数量分别消费，保留相邻普通行");
        var unknownMarker = fixture.String("[future modifier]");
        var existingOpaque = fixture.Parse(fixture.Block("monster", [.. row[..^1], unknownMarker, row[^1], .. row]));
        That(existingOpaque.Monsters[0].Modifiers.SequenceEqual(["[future modifier]"]) && existingOpaque.Monsters[1].Modifiers.Count == 0,
            "无NPC行保留既有不透明修饰和相邻行，不以新顺序规则收窄旧解析");
        TestCheck.Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(existingOpaque,1,100),
            "既有不透明修饰仍不能借结构保留生成普通怪物");
        var invalidNpcRows = new (string Name, PvfToken[] Tokens)[]
        {
            ("缺NPC整数", [.. npcPrefix, npcMarker, bossMarker]),
            ("NPC后整行截断", [.. npcPrefix, npcMarker]),
            ("NPC参数不是整数", [.. npcPrefix, npcMarker, new(4,0), bossMarker]),
            ("多余NPC整数", [.. npcPrefix, npcMarker, new(0,0), new(0,7), bossMarker]),
            ("重复NPC及参数", [.. npcPrefix, npcMarker, new(0,0), npcMarker, new(0,0), bossMarker]),
            ("NPC标记顺序错误", [.. npcPrefix, dummyMarker, npcMarker, new(0,0), bossMarker]),
            ("NPC后未知修饰", [.. npcPrefix, npcMarker, new(0,0), unknownMarker, bossMarker]),
            ("NPC后重复dummy", [.. npcPrefix, npcMarker, new(0,0), dummyMarker, dummyMarker, bossMarker]),
            ("NPC后display与dummy错序", [.. npcPrefix, npcMarker, new(0,0), displayMarker, dummyMarker, bossMarker]),
            ("缺NPC整数不能吞下一怪物ID", [.. npcPrefix, npcMarker, .. row]),
            ("缺NPC后品级不能吞下一普通行", [.. npcPrefix, npcMarker, new(0,0), .. row]),
            ("NPC冠军缺效果数量", [.. npcPrefix, npcMarker, new(0,0), fixture.String("[champion]"), .. row])
        };
        foreach (var invalid in invalidNpcRows)
            Reject(fixture.Block("monster", invalid.Tokens), invalid.Name);
        var elite = fixture.Parse(fixture.Block("monster",[.. row[..^1],fixture.String("[champion]"),new(0,0),.. row]));
        That(elite.Monsters is [{ RankKind: "[champion]" },{ MonsterId: 1, RankKind: "[normal]" }] &&
            DungeonMonsterPlan.FromRoom(elite,1,100).Monsters is [{ SpawnType: 1 },{ SpawnType: 0 }],
            "精英行后的空效果数量独立消费，下一怪物行及品级保持完整");
        Reject(fixture.Block("monster",[.. row[..^1],fixture.String("[champion]"),new(0,1),.. row]),
            "非空未知精英效果不能丢弃或吞掉下一怪物行");
        Console.WriteLine($"PASS {Count - checks} dungeon room team metadata assertions");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;

        void Reject(Action action, string name)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            {
                rejected = true;
            }

            That(rejected, name);
        }

        var dungeons = DungeonCatalog.Load(pvf);
        var maze = dungeons.Get(3).Mazes.Single(candidate => candidate.QuestConnectionTokens.Count == 3
            && candidate.QuestConnectionTokens[1] == new PvfToken(0, 3145));
        var first = maze.MapSpecification!.Single(map => map.Position == maze.Starts.Single());
        var catalog = DungeonRoomCatalog.Load(pvf);
        var room = catalog.Get(first);
        var storyMap = dungeons.Get(9).Mazes.SelectMany(candidate => candidate.MapSpecification ?? []).Single(map => map.Id == 76155);
        var story = catalog.Get(storyMap);
        That(story.Monsters is [{ MonsterId: 107000909, PlacementKind: "[fixed]", RankKind: "[boss]", Modifiers: ["[dummy]"] }] &&
            story.Monsters[0].RawValues.SequenceEqual([107000909,1,0,855,233,0,1,1]),
            "Lv8真实76155保留完整八整数及fixed/dummy/boss声明");
        Reject(() => DungeonMonsterPlan.FromRoom(story,1,100), "76155未知dummy/相对等级生成规则仍拒绝，不能补普通Boss冒充剧情");
        var mermanDungeon = dungeons.Get(162);
        var mermanMap = mermanDungeon.Mazes.Single(candidate => candidate.QuestConnectionTokens.SequenceEqual(
            [new PvfToken(0,0),new(0,3487),new(0,-1)])).MapSpecification!.Single(map => map.Id == 91548);
        var merman = catalog.Get(mermanMap,requirePassiveDefinitions: false);
        int[][] mermanRows = [[62531,1,0,912,394,0,1,1], [69022,1,0,662,316,0,1,1],
            [69022,1,0,663,392,0,1,1], [65314,1,0,532,318,0,1,1], [65314,1,0,511,430,0,1,1]];
        That(merman.Monsters.Count == 5 && merman.Monsters.Select((monster,index) => monster.RawValues.SequenceEqual(mermanRows[index])).All(value => value) &&
            merman.Monsters[0] is { MonsterId: 62531, NpcParameter: 0, PlacementKind: "[fixed]", RankKind: "[boss]", Modifiers: ["[NPC]", "[dummy]"] } &&
            merman.Monsters.Skip(1).All(monster => monster.NpcParameter is null && monster.PlacementKind == "[fixed]" &&
                monster.RankKind == "[normal]" && monster.Modifiers.Count == 0),
            "Q3487真实91548保留完整五怪原序、首NPC0/dummy/Boss及后续四行，不吞参数或下一怪物");
        That(merman.AiCharacters is [{ Id: 55430, Faction: "[character]", RankKind: "[normal]" }] &&
            merman.NpcMetadataError is null && merman.Npcs is [{ Id: 1000 }],
            "91548真实APC55430与独立地图NPC1000分别保留，不被monster内NPC参数替代");
        Reject(() => DungeonMonsterPlan.FromRoom(merman,1,100,mermanDungeon.BasisLevel),
            "91548真实NPC参数行解析成功仍拒绝未闭合生成资格，不冒充普通Boss或战斗死亡");
        That(room.Map.Id == 76121 && room.Map.Path == "map/cataclysm/granflores/01_mirkwood/3145_76121.map",
            "room resolves the matching task first-map path");
        int[][] expected = [[1, 0, 3, 573, 197, 0, 1, 1], [1, 0, 3, 557, 280, 0, 1, 1],
            [1, 0, 3, 643, 208, 0, 1, 1], [1, 0, 3, 647, 263, 0, 1, 1]];
        That(room.Monsters.Count == 4 && room.Monsters.Select((monster, index) => monster.RawValues.SequenceEqual(expected[index])).All(value => value),
            "first room retains all four full monster rows in source order");
        That(room.Monsters.All(monster => monster.MonsterId == 1 && monster.PlacementKind == "[fixed]" && monster.RankKind == "[normal]"
            && monster.Path == "monster/goblin/goblin.mob"), "monster references resolve without inventing placement or rank enums");
        That(room.PassiveObjects.Count == 25 && room.PassiveObjects.Count(item => item.ObjectId == 245) == 13
            && room.PassiveObjects.Count(item => item.ObjectId == 226) == 2 && room.PassiveObjects.Count(item => item.ObjectId == 109006903) == 4,
            "first room preserves its passive object list instead of treating it as monsters");
        var buffObject = room.PassiveObjects.Single(item => item.ObjectId == 15336);
        That(buffObject.RawValues.SequenceEqual([15336, 506, 250, 0])
            && buffObject.Path == "passiveobject/actionobject/map/stormofmetastasis/buff/total_buff/total_buff.obj",
            "visible buff object keeps its separate template and raw placement values");
        That(room.Buffs.SequenceEqual([0, 1, 2]), "map buff integers are separate from the passive buff object");
        That(room.Monsters is IList monsters && monsters.IsReadOnly && room.Monsters[0].RawValues is IList raw && raw.IsReadOnly
            && room.PassiveObjects is IList objects && objects.IsReadOnly && room.Buffs is IList buffs && buffs.IsReadOnly,
            "room data and raw rows are immutable projections");
        var next = catalog.Get(maze.MapSpecification!.Single(map => map.Id == 76122));
        int[][] nextExpected = [[1, 0, 3, 471, 208, 0, 1, 1], [1, 0, 3, 392, 266, 0, 1, 1],
            [1, 0, 3, 410, 222, 0, 1, 1], [1, 0, 3, 469, 295, 0, 1, 1], [2, 0, 3, 522, 244, 0, 1, 1]];
        That(next.Map.Id == 76122 && next.Monsters.Count == 5
            && next.Monsters.Select((monster, index) => monster.RawValues.SequenceEqual(nextExpected[index])).All(value => value)
            && next.Monsters.All(monster => monster.PlacementKind == "[fixed]" && monster.RankKind == "[normal]"),
            "next task room uses the same supported shape and preserves its own five monster rows");
        That(next.Monsters.Take(4).All(monster => monster.Path == "monster/goblin/goblin.mob")
            && next.Monsters[^1].MonsterId == 2 && next.Monsters[^1].Path == "monster/goblin/goblinthrower.mob"
            && next.PassiveObjects.Count == 21 && next.Buffs.Count == 0,
            "next room resolves the thrower and keeps its own objects without inventing absent buffs");
        Reject(() => catalog.Get(first with { Path = "map/../../monster/goblin/goblin.mob" }), "room rejects resource root escape");
        Reject(() => catalog.Get(first with { Path = "map/missing-fixture.map" }), "room rejects missing map resource");
        Reject(() => catalog.Get(first with { Id = -1 }), "room rejects negative map index");

        var fixture = new Fixture();
        var valid = fixture.Monster();
        var parsed = fixture.Parse(valid);
        valid[1] = new(0, 999);
        That(parsed.Monsters[0].MonsterId == 1, "parsed room does not retain mutable input tokens");
        var unusual = fixture.Parse(fixture.Monster(values: [1, int.MinValue, int.MaxValue, -3, 4, -5, 6, 7], placement: "[future placement]", rank: "[future rank]"));
        That(unusual.Monsters[0].RawValues.SequenceEqual([1, int.MinValue, int.MaxValue, -3, 4, -5, 6, 7])
            && unusual.Monsters[0].PlacementKind == "[future placement]" && unusual.Monsters[0].RankKind == "[future rank]",
            "structurally known raw values stay opaque rather than becoming a guessed business enum");
        That(fixture.Parse([]).Monsters.Count == 0 && fixture.Parse(fixture.Block("monster", [])).Monsters.Count == 0,
            "absent or explicitly empty monster sections stay empty only when the source is empty");
        Reject(() => fixture.Parse(fixture.Monster(values: [1, 2, 3, 4, 5, 6, 7])), "incomplete monster group rejected");
        Reject(() => fixture.Parse(fixture.Monster(values: [1, 2, 3, 4, 5, 6, 7, 8, 9])), "oversized monster group rejected");
        Reject(() => fixture.Parse(fixture.Monster(values: [-1, 0, 0, 0, 0, 0, 0, 0])), "negative monster ID rejected");
        Reject(() => fixture.Parse(fixture.Monster(values: [99, 0, 0, 0, 0, 0, 0, 0])), "unlisted monster ID rejected");
        Reject(() => fixture.Parse(fixture.Monster(), exists: false), "missing monster script rejected");
        var opaque = fixture.Monster();
        opaque[2] = new(7, 123);
        Reject(() => fixture.Parse(opaque), "unresolved monster numeric reference rejected");
        var badLabel = fixture.Monster();
        badLabel[9] = new(0, 123);
        Reject(() => fixture.Parse(badLabel), "numeric placement label rejected");
        Reject(() => fixture.Parse(fixture.Monster(placement: "")), "empty monster label rejected");
        Reject(() => fixture.Parse(fixture.Monster()[..^1]), "unclosed monster block rejected");
        Reject(() => fixture.Parse([.. fixture.Monster(), .. fixture.Monster()]), "duplicate monster blocks remain unsupported");
        var nested = fixture.Monster();
        nested[2] = fixture.Tag("condition");
        Reject(() => fixture.Parse(nested), "nested monster conditions remain unsupported");
        var obj = fixture.Parse(fixture.Block("passive object", [new(0, 3), new(0, -1), new(0, 2), new(0, int.MaxValue)]));
        That(obj.PassiveObjects.Single().RawValues.SequenceEqual([3, -1, 2, int.MaxValue]), "passive object raw integers are not narrowed");
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, 3), new(0, 1), new(0, 2)])), "incomplete object row rejected");
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, -1), new(0, 1), new(0, 2), new(0, 0)])), "negative object ID rejected");
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, 99), new(0, 1), new(0, 2), new(0, 0)])), "unlisted object ID rejected");
        That(fixture.Parse(fixture.Block("passive object", [new(0,99),new(0,1),new(0,2),new(0,0)]),requirePassiveDefinitions: false)
            .PassiveObjects.Single() is { ObjectId: 99, Path: null },"无关装饰保留原始行和缺失引用，不伪造任务脚本");
        var unlisted = fixture.Parse(fixture.Block("passive object", [new(0, 99), new(0, 1), new(0, 2), new(0, 0)]), requirePassiveDefinitions: false).PassiveObjects.Single();
        That(unlisted.ObjectId == 99 && unlisted.Path is null && unlisted.UnresolvedReason == "passive-object-unlisted" &&
            unlisted.RawValues.SequenceEqual([99, 1, 2, 0]), "客户端本地未知装饰保留全部原行和未解析原因，不换ID或伪造资源路径");
        foreach (var missingObject in new[] { (Dungeon: 7, Map: 58581, Object: 2109006955), (Dungeon: 8, Map: 58589, Object: 89092) })
        {
            var actual = catalog.Get(dungeons.Get(missingObject.Dungeon).Mazes[0].MapSpecification!.Single(map => map.Id == missingObject.Map), requirePassiveDefinitions: false);
            That(actual.PassiveObjects.Any(actor => actor.ObjectId == missingObject.Object && actor.Path is null && actor.RawValues.Count == 4) &&
                actual.Monsters.Count > 0 && actual.SpecialObjects.Count > 0, "真实普通图未列入目录的装饰与完整怪物、机关同时保留");
        }
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, 3), new(0, 1), new(0, 2), new(0, 0)]), exists: false), "missing object script rejected");
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, 3), new(5, 1), new(0, 2), new(0, 0)])), "object numeric reference rejected");
        That(fixture.Parse(fixture.Block("buff", [new(0, -1), new(0, int.MaxValue)])).Buffs.SequenceEqual([-1, int.MaxValue]),
            "buff list retains complete integer values without interpreting their enum");
        Reject(() => fixture.Parse(fixture.Block("buff", [new(7, 1)])), "opaque buff reference rejected");
        Reject(() => fixture.Parse([fixture.Tag("buff"), new(0, 0)]), "unclosed buff block rejected");
        Console.WriteLine($"PASS {Count - checks} dungeon room resource assertions");
    }

    private sealed class Fixture
    {
        private readonly Dictionary<int, string> strings = new();
        public PvfToken String(string value)
        {
            int id = strings.Count;
            strings.Add(id, value);
            return new(6, id);
        }

        public PvfToken Tag(string value) => new(3, String("[" + value + "]").Value);
        public PvfToken[] Block(string tag, PvfToken[] values) => [Tag(tag), .. values, Tag("/" + tag)];
        public PvfToken[] Monster(int[]? values = null, string placement = "[fixed]", string rank = "[normal]") =>
            Block("monster", [.. (values ?? [1, 0, 3, 573, 197, 0, 1, 1]).Select(value => new PvfToken(0, value)), String(placement), String(rank)]);
        public DungeonRoomDefinition Parse(PvfToken[] tokens, bool exists = true, bool requirePassiveDefinitions = true) => DungeonRoomCatalog.ParseResource(
            new("map", new(0, 0), 1, "map/fixture.map"), tokens, id => strings[id],
            new Dictionary<int, string> { [1] = "monster/fixture.mob", [62531] = "monster/merman-king-fixture.mob" },
            new Dictionary<int, string> { [3] = "passiveobject/fixture.obj" }, _ => exists,requirePassiveDefinitions);
    }
}
