using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019CatalogTests
{
    internal static Strongway2019Catalog CreateCatalog()
    {
        var source = Fixture();
        return new(source.Tokens, source.Resolve);
    }

    public static void RunCore()
    {
        int before = Count;
        var source = Fixture();
        AssertResource(new(source.Tokens, source.Resolve));

        var changed = Fixture(normal: [0, 15359, 9, 2, 15363, 10, 3, 15364, 11],
            rewards: [0, 490012735, 1, 2001, 2002, 2, 490012736, 1, 2003, 2004, 3, 490012737, 1, 2005, 2006]);
        var catalog = new Strongway2019Catalog(changed.Tokens, changed.Resolve);
        That(catalog.NormalDungeons.SequenceEqual(new Strongway2019DungeonRow[]
            { new(0, 15359, 9), new(2, 15363, 10), new(3, 15364, 11) }) &&
            catalog.Rewards[^1] == new Strongway2019RewardRow(3, 490012737, 1, 2005, 2006),
            "副本编号不硬编码为解析准入，隐藏键及未知尾列完整保留");

        Reject<InvalidDataException>(() => Read(Fixture(normal: [0, 5359, 4, 0, 5363, 4])),
            "普通副本重复键拒绝，不能静默覆盖路线");
        Reject<InvalidDataException>(() => Read(Fixture(matching: [0, 5360, 4, 0, 5360, 4])),
            "匹配副本重复键拒绝");
        Reject<InvalidDataException>(() => Read(Fixture(rewards: [0, 490012735, 1, 1789, 1795, 0, 490012736, 1, 1789, 1795])),
            "奖励重复键拒绝，不能覆写首档");
        Reject<InvalidDataException>(() => Read(Fixture(normal: [0, 5359])), "副本三列记录截断拒绝");
        Reject<InvalidDataException>(() => Read(Fixture(rewards: [0, 490012735, 1, 1789])), "奖励五列记录截断拒绝");
        Reject<InvalidDataException>(() => Read(Fixture(monsters: [400577, 109008147, 400577, 109008151])),
            "同地图重复怪物映射拒绝");
        Reject<InvalidDataException>(() => Read(Fixture(duplicateButton: true)), "五技能重复按钮拒绝");
        Reject<InvalidDataException>(() => Read(Fixture(nonIntegerPoint: true)), "技能点不能把浮点token当整数");
        Reject<InvalidDataException>(() => Read(Fixture(omitSkillCooltime: true)), "技能必需字段缺失拒绝");
        Reject<InvalidDataException>(() => Read(Fixture(wrongSkillClose: true)), "技能块错误闭合拒绝，不能丢弃后续技能");
        Console.WriteLine($"PASS {Count - before} 首期强者之路资源目录与声明边界");
    }

    public static void Run(PvfArchive archive)
    {
        int before = Count;
        AssertResource(new(archive));
        Console.WriteLine($"PASS {Count - before} 首期强者之路真实资源合同");
    }

    static void AssertResource(Strongway2019Catalog catalog)
    {
        That(catalog.DungeonStartDelay == 5 && catalog.StartAreaPathgateOpenDelay == 8 && catalog.ReturnVillageDelay == 6,
            "首期三个延迟保留资源值，不擅自更改单位");
        That(catalog.NormalDungeons.SequenceEqual(new Strongway2019DungeonRow[]
            { new(0, 5359, 4), new(2, 5363, 4), new(3, 5364, 4) }) &&
            catalog.MatchingDungeons.SequenceEqual(new Strongway2019DungeonRow[] { new(0, 5360, 4), new(1, 5360, 4) }),
            "普通和匹配各自解释键，保留隐藏项及原始尾列");
        That(catalog.Rewards.SequenceEqual(new Strongway2019RewardRow[]
            { new(0, 490012735, 1, 1789, 1795), new(2, 490012736, 1, 1789, 1795), new(3, 490012737, 1, 1789, 1795) }),
            "三种奖励礼盒的键及五列保持原顺序，不用副本难度键推算匹配奖励");
        That(catalog.QualifyingDungeonId == 5364 && catalog.RandomMapIds.SequenceEqual([400596, 400597, 400603]),
            "资格副本及三张随机地图候选完整保留");
        int[] maps = [400577, 400578, 400579, 400580, 400581, 400582, 400583, 400584, 400596, 400597, 400603];
        int[] monsters = [109008147, 109008151, 109008419, 109008364, 109008389, 109008501,
            109008472, 109008487, 109008475, 109008143, 109008376];
        That(catalog.MapMonsters.Count == maps.Length && maps.Select((id, index) =>
            catalog.MapMonsters.TryGetValue(id, out int monster) && monster == monsters[index]).All(value => value),
            "八张既定战斗图及三张候选图对应真实怪物编号");
        That(catalog.AddPointValues.SequenceEqual([1000, 1]) && catalog.SkillWaitingTime == 1500,
            "充能声明及技能等待时间保留原值，不推断速率单位");
        That(catalog.Skills.SequenceEqual(new Strongway2019SkillRow[]
            { new(0, "symbol change", "dplitem3", 0, 10000, 1), new(1, "symbol change", "dplitem1", 30, 10000, null),
                new(2, "symbol change", "dplitem2", 45, 10000, null), new(3, "symbol change", "dplitem0", 60, 10000, null),
                new(4, "map change", "dplitem4", 100, 10000, null) }),
            "五技能保留按钮、类型、符号、点数、冷却及首技能单次限制；未声明限次保持null");
    }

    static void Read((PvfToken[] Tokens, Func<int, string> Resolve) source) =>
        _ = new Strongway2019Catalog(source.Tokens, source.Resolve);

    static (PvfToken[] Tokens, Func<int, string> Resolve) Fixture(int[]? normal = null, int[]? matching = null,
        int[]? rewards = null, int[]? monsters = null, bool duplicateButton = false, bool nonIntegerPoint = false,
        bool omitSkillCooltime = false, bool wrongSkillClose = false)
    {
        var tokens = new List<PvfToken>();
        var strings = new List<string>();
        Field("dungeon start delay", 5);
        Field("start area pathgate open delay", 8);
        Field("return village delay", 6);
        Field("normal dungeon index", normal ?? [0, 5359, 4, 2, 5363, 4, 3, 5364, 4]);
        Tag("/normal dungeon index");
        Field("matching dungeon index", matching ?? [0, 5360, 4, 1, 5360, 4]);
        Tag("/matching dungeon index");
        Field("reward", rewards ?? [0, 490012735, 1, 1789, 1795, 2, 490012736, 1, 1789, 1795, 3, 490012737, 1, 1789, 1795]);
        Tag("/reward");
        Field("qualifying dungeon index", 5364);
        Field("random map index", 400596, 400597, 400603);
        Tag("/random map index");
        Field("map monster index", monsters ?? [400577, 109008147, 400578, 109008151, 400579, 109008419,
            400580, 109008364, 400581, 109008389, 400582, 109008501, 400583, 109008472, 400584, 109008487,
            400596, 109008475, 400597, 109008143, 400603, 109008376]);
        Tag("/map monster index");
        Tag("skill system");
        Field("add point", 1000, 1);
        Field("skill waiting time", 1500);
        string[] symbols = ["dplitem3", "dplitem1", "dplitem2", "dplitem0", "dplitem4"];
        int[] points = [0, 30, 45, 60, 100];
        for (int i = 0; i < 5; i++)
        {
            Tag("skill");
            Field("button index", duplicateButton && i == 1 ? 0 : i);
            Tag("type");
            Text(i == 4 ? "map change" : "symbol change");
            Text(symbols[i]);
            Tag("need point");
            tokens.Add(nonIntegerPoint && i == 0 ? new(2, BitConverter.SingleToInt32Bits(0f)) : new(0, points[i]));
            if (!omitSkillCooltime || i != 0) Field("global cooltime", 10000);
            if (i == 0) Field("limit count", 1);
            Tag(wrongSkillClose && i == 0 ? "/reward" : "/skill");
        }
        Tag("/skill system");
        return (tokens.ToArray(), index => strings[index]);

        void Tag(string value)
        {
            tokens.Add(new(3, strings.Count));
            strings.Add("[" + value + "]");
        }
        void Text(string value)
        {
            tokens.Add(new(6, strings.Count));
            strings.Add(value);
        }
        void Field(string name, params int[] values)
        {
            Tag(name);
            tokens.AddRange(values.Select(value => new PvfToken(0, value)));
        }
    }
}
