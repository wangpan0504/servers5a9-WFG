using System.Collections;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class DungeonRoomCatalogTests
{
    public static void RunCore()
    {
        int checks = 0;
        var fixture = new Fixture();
        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL room team metadata: " + name);
        }
        void Reject(PvfToken[] tokens, string name)
        {
            bool rejected = false;
            try { fixture.Parse(tokens); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException) { rejected = true; }
            Check(rejected, name);
        }
        var row = fixture.Monster()[1..^1];
        var monsters = fixture.Block("monster", [.. row, .. row]);
        var teamTokens = fixture.Block("monster team", [new(0, 100), new(0, 0)]);
        var parsed = fixture.Parse([.. monsters, .. teamTokens]);
        Check(parsed.MonsterTeams!.SequenceEqual([100, 0]) && parsed.Monsters.Count == 2,
            "team values retain positional source order beside the complete actor roster");
        teamTokens[1] = new(0, 0);
        Check(parsed.MonsterTeams!.SequenceEqual([100, 0]) && parsed.MonsterTeams is IList teams && teams.IsReadOnly,
            "parsed teams are read only and do not retain mutable input tokens");
        Check(fixture.Parse(monsters).MonsterTeams is { Count: 0 }, "absent team section preserves default blocking metadata");
        Check(fixture.Parse([.. monsters, .. fixture.Block("monster team", [])]).MonsterTeams is { Count: 0 },
            "explicitly empty team section is valid");
        Check(fixture.Parse([.. monsters, .. fixture.Block("monster team", [new(0, 0)])]).MonsterTeams!.SequenceEqual([0]),
            "missing trailing team entries remain absent instead of inventing friendly values");
        Check(fixture.Parse([.. monsters, .. fixture.Block("monster team", [new(0, int.MinValue), new(0, int.MaxValue)])])
            .MonsterTeams!.SequenceEqual([int.MinValue, int.MaxValue]), "nonzero team integers stay opaque and complete");
        Reject([.. monsters, .. fixture.Block("monster team", [new(6, fixture.String("0").Value)])], "nonnumeric team rejected");
        Reject([.. monsters, .. fixture.Block("monster team", [new(0, 100), new(0, 0), new(0, 0)])], "excess team index rejected");
        Reject(fixture.Block("monster team", [new(0, 0)]), "team without corresponding monster rejected");
        Reject([.. monsters, fixture.Tag("monster team"), new(0, 100)], "unclosed team section rejected");
        Reject([.. monsters, .. fixture.Block("monster team", []), .. fixture.Block("monster team", [])], "duplicate team sections rejected");
        Check(fixture.Parse([.. monsters, .. fixture.Block("future room data", [new(6, fixture.String("opaque").Value)])]).Monsters.Count == 2,
            "unrelated unknown metadata keeps existing parsing behavior");
        Console.WriteLine($"PASS {checks} dungeon room team metadata assertions");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            checks++;
            if (!condition)
                throw new Exception("FAIL: " + name);
        }

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

            Check(rejected, name);
        }

        var dungeons = DungeonCatalog.Load(pvf);
        var maze = dungeons.Get(3).Mazes.Single(candidate => candidate.QuestConnectionTokens.Count == 3
            && candidate.QuestConnectionTokens[1] == new PvfToken(0, 3145));
        var first = maze.MapSpecification!.Single(map => map.Position == maze.Starts.Single());
        var catalog = DungeonRoomCatalog.Load(pvf);
        var room = catalog.Get(first);
        Check(room.Map.Id == 76121 && room.Map.Path == "map/cataclysm/granflores/01_mirkwood/3145_76121.map",
            "room resolves the matching task first-map path");
        int[][] expected = [[1, 0, 3, 573, 197, 0, 1, 1], [1, 0, 3, 557, 280, 0, 1, 1],
            [1, 0, 3, 643, 208, 0, 1, 1], [1, 0, 3, 647, 263, 0, 1, 1]];
        Check(room.Monsters.Count == 4 && room.Monsters.Select((monster, index) => monster.RawValues.SequenceEqual(expected[index])).All(value => value),
            "first room retains all four full monster rows in source order");
        Check(room.Monsters.All(monster => monster.MonsterId == 1 && monster.PlacementKind == "[fixed]" && monster.RankKind == "[normal]"
            && monster.Path == "monster/goblin/goblin.mob"), "monster references resolve without inventing placement or rank enums");
        Check(room.PassiveObjects.Count == 25 && room.PassiveObjects.Count(item => item.ObjectId == 245) == 13
            && room.PassiveObjects.Count(item => item.ObjectId == 226) == 2 && room.PassiveObjects.Count(item => item.ObjectId == 109006903) == 4,
            "first room preserves its passive object list instead of treating it as monsters");
        var buffObject = room.PassiveObjects.Single(item => item.ObjectId == 15336);
        Check(buffObject.RawValues.SequenceEqual([15336, 506, 250, 0])
            && buffObject.Path == "passiveobject/actionobject/map/stormofmetastasis/buff/total_buff/total_buff.obj",
            "visible buff object keeps its separate template and raw placement values");
        Check(room.Buffs.SequenceEqual([0, 1, 2]), "map buff integers are separate from the passive buff object");
        Check(room.Monsters is IList monsters && monsters.IsReadOnly && room.Monsters[0].RawValues is IList raw && raw.IsReadOnly
            && room.PassiveObjects is IList objects && objects.IsReadOnly && room.Buffs is IList buffs && buffs.IsReadOnly,
            "room data and raw rows are immutable projections");
        var next = catalog.Get(maze.MapSpecification!.Single(map => map.Id == 76122));
        int[][] nextExpected = [[1, 0, 3, 471, 208, 0, 1, 1], [1, 0, 3, 392, 266, 0, 1, 1],
            [1, 0, 3, 410, 222, 0, 1, 1], [1, 0, 3, 469, 295, 0, 1, 1], [2, 0, 3, 522, 244, 0, 1, 1]];
        Check(next.Map.Id == 76122 && next.Monsters.Count == 5
            && next.Monsters.Select((monster, index) => monster.RawValues.SequenceEqual(nextExpected[index])).All(value => value)
            && next.Monsters.All(monster => monster.PlacementKind == "[fixed]" && monster.RankKind == "[normal]"),
            "next task room uses the same supported shape and preserves its own five monster rows");
        Check(next.Monsters.Take(4).All(monster => monster.Path == "monster/goblin/goblin.mob")
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
        Check(parsed.Monsters[0].MonsterId == 1, "parsed room does not retain mutable input tokens");
        var unusual = fixture.Parse(fixture.Monster(values: [1, int.MinValue, int.MaxValue, -3, 4, -5, 6, 7], placement: "[future placement]", rank: "[future rank]"));
        Check(unusual.Monsters[0].RawValues.SequenceEqual([1, int.MinValue, int.MaxValue, -3, 4, -5, 6, 7])
            && unusual.Monsters[0].PlacementKind == "[future placement]" && unusual.Monsters[0].RankKind == "[future rank]",
            "structurally known raw values stay opaque rather than becoming a guessed business enum");
        Check(fixture.Parse([]).Monsters.Count == 0 && fixture.Parse(fixture.Block("monster", [])).Monsters.Count == 0,
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
        Check(obj.PassiveObjects.Single().RawValues.SequenceEqual([3, -1, 2, int.MaxValue]), "passive object raw integers are not narrowed");
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, 3), new(0, 1), new(0, 2)])), "incomplete object row rejected");
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, -1), new(0, 1), new(0, 2), new(0, 0)])), "negative object ID rejected");
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, 99), new(0, 1), new(0, 2), new(0, 0)])), "unlisted object ID rejected");
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, 3), new(0, 1), new(0, 2), new(0, 0)]), exists: false), "missing object script rejected");
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, 3), new(5, 1), new(0, 2), new(0, 0)])), "object numeric reference rejected");
        Check(fixture.Parse(fixture.Block("buff", [new(0, -1), new(0, int.MaxValue)])).Buffs.SequenceEqual([-1, int.MaxValue]),
            "buff list retains complete integer values without interpreting their enum");
        Reject(() => fixture.Parse(fixture.Block("buff", [new(7, 1)])), "opaque buff reference rejected");
        Reject(() => fixture.Parse([fixture.Tag("buff"), new(0, 0)]), "unclosed buff block rejected");
        Console.WriteLine($"PASS {checks} dungeon room resource assertions");
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
        public DungeonRoomDefinition Parse(PvfToken[] tokens, bool exists = true) => DungeonRoomCatalog.ParseResource(
            new("map", new(0, 0), 1, "map/fixture.map"), tokens, id => strings[id],
            new Dictionary<int, string> { [1] = "monster/fixture.mob" },
            new Dictionary<int, string> { [3] = "passiveobject/fixture.obj" }, _ => exists);
    }
}
