using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Projection;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonEpicDropBuffTests
{
    internal static ImmutableDictionary<int, DungeonEpicDropBuffDefinition> Definitions(bool validate = false)
    {
        string[] text = ["Dice_Attendance_5", "dungeon_one_match", "epic_rarity_item_drop_rate",
            "inferno_dungeon_epic_drop_rate_up", "particular_dungeon_epic_drop_rate_up",
            "creak_daybreak_dungeon_epic_drop_rate_up", "hell_legend_dungeon_epic_drop_rate_up", "boss_drop_rate"];
        PvfTokenSection Text(string name, int id) => new(name, [new(6, id)], []);
        PvfTokenSection Numbers(string name, params int[] values) => new(name, [.. values.Select(v => new PvfToken(0, v))], []);
        PvfTokenSection Effect(int name, int value) => new("effect", [], [Text("name", name), Numbers("param", value)], true);
        PvfTokenSection Definition(int id) => new("index", [new(0, id)],
            [Text("system group", 0), new("apply type", [], [Text("type", 1), Numbers("param", 1, 0)], true),
                Effect(2, id == 280 ? 2500 : 500), Effect(3, id == 280 ? 2500 : 500), Effect(4, id == 280 ? 2500 : 500),
                .. id == 280 ? new[] { Effect(5, 1250), Effect(6, 1250) } : new[] { Effect(7, 1) }], true);
        var rows = ImmutableArray.Create(Definition(280), Definition(284));
        var result = DungeonEpicDropBuffDefinition.ReadDefinitions(rows, id => text[id]);
        if (validate)
        {
            That(result.Count == 2 && result[280].Rates.Count == 5 && result[284].Rates.Count == 3 &&
                result[284].BossDropPercent == 1, "280五类/284三类史诗参数完整解析，领主项独立保留百分比参数");
            foreach (var row in new[] { Definition(280) with { Children = Definition(280).Children.Add(Numbers("unknown lifetime field", 1)) },
                Definition(280) with { Children = Definition(280).Children.SetItem(2, Effect(2, -1)) },
                Definition(280) with { Children = Definition(280).Children.SetItem(2,
                    new("effect", [], [Text("name", 2)], true)) },
                Definition(280) with { Children = Definition(280).Children.Add(Effect(2, 2500)) } })
                That(DungeonEpicDropBuffDefinition.ReadDefinitions([row], id => text[id]).IsEmpty,
                    "未知生命周期字段、负参数、畸形或重复效果不借编号兼容放行");
            That(DungeonEpicDropBuffDefinition.ReadDefinitions([rows[0], rows[0]], id => text[id]).IsEmpty,
                "重复Buff声明不变成两份可用定义");
        }
        return result;
    }

    public static void RunCore()
    {
        int before = Count;
        var defs = Definitions(validate: true);
        foreach (var (dungeon, inferno, legend, scope, rate280, rate284) in new[]
        {
            (103u,false,false,DungeonEpicDropScope.Ordinary,2500,500),
            (300u,true,false,DungeonEpicDropScope.Inferno,2500,500),
            (316u,false,false,DungeonEpicDropScope.Particular,2500,500),
            (530u,false,false,DungeonEpicDropScope.Daybreak,1250,0),
            (291100268u,false,true,DungeonEpicDropScope.HellLegend,1250,0)
        })
            That(DungeonEpicDropBuffPolicy.Scope(dungeon,inferno,legend)==scope && defs[280].Rates.GetValueOrDefault(scope)==rate280 &&
                defs[284].Rates.GetValueOrDefault(scope)==rate284,"模式互斥选自己的史诗参数，不叠加五项或替284补无声明的黎明/星空");
        PvfTokenSection Mapping(int item,int buff) => new("index",[new(0,item)],[new("buff list",[new(0,buff)],[])],true);
        var mapping=DungeonEpicDropBuffDefinition.ReadMappings([Mapping(10304461,280),Mapping(10305166,284)],defs);
        That(mapping.Count==2 && mapping[10304461].BuffId==280 && mapping[10305166].BuffId==284 &&
            DungeonEpicDropBuffDefinition.ReadMappings([Mapping(10304461,280),Mapping(10304461,284)],defs).IsEmpty,
            "物品从单一完整资源映射绑定，不借冲突映射或编号兜底");
        That(DungeonEpicBuffProjection.AddedBody(280).SequenceEqual(new byte[] {1,24,1,0,0,1,0,0,0,0,0,0,0,0,0,0,0}) &&
            DungeonEpicBuffProjection.AddedBody(284)[1]==28,"原生487投影为一次副本计数1，不是截止Unix秒");
        BossEligibility(defs);
        GenericEffects();
        Console.WriteLine($"PASS {Count-before} dungeon epic buff definition, scope and wire assertions");
    }

    static void GenericEffects()
    {
        string[] strings = ["自由组合组", "dungeon_one_match", "boss_drop_rate", "physical_attack", "unknown_server_effect",
            "expire_time", "epic_rarity_item_drop_rate", "level"];
        PvfTokenSection Text(string field, int value) => new(field, [new(6, value)], []);
        PvfTokenSection Numbers(string field, params int[] values) => new(field, [.. values.Select(value => new PvfToken(0, value))], []);
        PvfTokenSection Effect(int name, int value) => new("effect", [], [Text("name", name), Numbers("param", value)], true);
        var mixed = new PvfTokenSection("index", [new(0, 90001)], [Text("system group", 0), Effect(2, 100), Effect(3, 5),
            new("apply type", [], [Text("type", 1), Numbers("param", 1, 0)], true)], true);
        var moved = mixed with { Values = [new(0, 90002)] };
        var parsed = DungeonEpicDropBuffDefinition.ReadDefinitions([mixed, moved], id => strings[id]);
        That(parsed.Count == 2 && parsed.Values.All(buff => buff.BossDropPercent == 100 && buff.Rates.IsEmpty &&
            buff.Resource!.Effects.Any(effect => effect.Executor == VariousBuffExecutor.NativeClient && effect.Name == "physical_attack")),
            "任意新编号按name组合领主掉率和客户端攻击属性，替换编号不改变参数或执行职责");
        var timed = mixed with { Values = [new(0, 90003)], Children = mixed.Children.SetItem(3, Effect(6, 150))
            .Add(new("apply type", [], [Text("type", 5), Numbers("param", 600, 0)], true)) };
        var timeDef = DungeonEpicDropBuffDefinition.ReadDefinitions([timed], id => strings[id])[90003];
        That(timeDef.Timed && timeDef.Rates[DungeonEpicDropScope.Ordinary] == 150 && timeDef.BossDropPercent == 100,
            "计时Buff同样组合多个服务端效果，不把效果名当互斥的药剂编号分支");
        var unknown = mixed with { Values = [new(0, 90004)], Children = mixed.Children.Add(Effect(4, 9)) };
        var registry = VariousBuffCatalog.ReadDefinitions([unknown], id => strings[id]);
        That(!registry[90004].Supported && registry[90004].UnsupportedReasons.Contains("unverified-effect:unknown_server_effect") &&
            DungeonEpicDropBuffDefinition.ReadDefinitions([unknown], id => strings[id]).IsEmpty,
            "未知effect保留明确状态，不一律当客户端效果或只执行已认识的一半");
        var level = mixed with { Values = [new(0, 90005)], Children = mixed.Children.Add(new("apply dungeon type",
            [new(6, 7), new(0, 25), new(0, 34)], [], true)) };
        var scoped = VariousBuffCatalog.ReadDefinitions([level], id => strings[id])[90005];
        That(scoped.Supported && scoped.DungeonScope == VariousBuffDungeonScope.Level &&
            scoped.MinimumDungeonLevel == 25 && scoped.MaximumDungeonLevel == 34,
            "适用副本等级从通用元数据读取，与物品职业或使用门禁分开");
        That(DungeonEpicBuffProjection.AddedBody(90001)[1] == 145 &&
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(DungeonEpicBuffProjection.AddedBody(90003, 1900000600).AsSpan(5)) == 1900000600,
            "任意资源Buff编号使用统一487，动态参数区分剩余次数和计时截止");
    }

    static void BossEligibility(ImmutableDictionary<int, DungeonEpicDropBuffDefinition> definitions)
    {
        var map = new DungeonMap("boss", new(0, 0), 1, "map/buff-boss.map");
        var maze = new DungeonMaze(0, 1, 1, "fixture", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(1, 0, 1, 0, 0, 0, maze, map, map);
        var boss = new DungeonPlannedMonster(0, 1, 42, 95,
            new(42, [], "[fixed]", "[boss]", "monster/buff-boss.mob")) { SpawnType = 3 };
        That(DungeonEpicDropBuffPolicy.BossBonus(definitions[284], entry, boss) == 1 &&
            DungeonEpicDropBuffPolicy.BossBonus(definitions[280], entry, boss) == 0 &&
            DungeonEpicDropBuffPolicy.BossBonus(null, entry, boss) == 0,
            "只有含领主项的284采用独立的1%增量，280和无资格不补造领主加成");
        foreach (var excluded in new[] { boss with { Source = null }, boss with { IsFriendly = true },
            boss with { IsDummyMapBoss = true }, boss with { Source = boss.Source! with { Modifiers = ["[dummy]"] } } })
            That(DungeonEpicDropBuffPolicy.BossBonus(definitions[284], entry, excluded) == 0,
                "普通怪、友军、dummy和剧情控制Boss不获得真实领主击杀加成");
        foreach (var free in new[] { entry with { IsPractice = true }, entry with { IsTutorial = true },
            entry with { DungeonId = 5000, Maze = maze with { IsTrainingRoom = true } },
            entry with { DungeonId = 7142, Maze = maze with { IsClassExperience = true } } })
            That(DungeonEpicDropBuffPolicy.BossBonus(definitions[284], free, boss) == 0,
                "练习、教程、修炼和职业体验不借资格产生领主奖励");
    }

    public static void RunResources(ItemResources resources)
    {
        foreach (var (item,buff,cool) in new[] { (10304461,280,4100),(10305166,284,60) })
            That(resources.RecoveryConsumables.TryGet(item,out var definition) && definition.DungeonEpicDropBuff?.BuffId==buff &&
                definition.EffectMilliseconds==0 && definition.CooltimeMaintenance==0 && definition.CoolTime==cool &&
                definition.BossSelectionBuff is null && definition.EpicDropBuff is null && definition.HellLegendEpicDropBuff is null,
                "当前真实STK按一次副本效果接入，不假造计时/持久化或套用其他药剂："+item);
    }
}
