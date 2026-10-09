using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class DungeonKillExperienceCatalogTests
{
    public static void RunCore()
    {
        int checks = Count;
        uint[] expected = [5, 5, 5, 20, 50, 75, 100, 100, 100, 100, 112, 112, 112, 100, 100, 75, 70, 60, 50, 5, 5];
        for (int difference = -9; difference <= 11; difference++)
            That(DungeonKillExperiencePolicy.Calculate(100, 1, 1, 1, 20, 20 + difference) == expected[difference + 9],
                "external等级差每档整数经验 " + difference);
        That(DungeonKillExperiencePolicy.Calculate(100, 1.25f, 1.3f, 1, 19, 20) == 181,
            "float32在权重、倍率、等级差三阶段分别取整");
        That(DungeonKillExperiencePolicy.Calculate(72, 1, 1.3f, 1, 1, 3) == 104 &&
            DungeonKillExperiencePolicy.Calculate(72, 1, 1.3f, 1, 3, 3) == 93 &&
            DungeonKillExperiencePolicy.Calculate(95, 1, 1.3f, 1, 3, 5) == 137,
            "真实Lv3和相对Lv5怪物的104/93/137经验");
        string Resolve(int id) => id == 1 ? "[dungeon difficulty exp bonusrate]" : "[monster exp bonusrate]";
        PvfToken[] table = [new(0, 1), new(0, 30)];
        PvfToken[] server = [new(3, 1), new(2, BitConverter.SingleToInt32Bits(1.3f)), new(0, 2), new(2, BitConverter.SingleToInt32Bits(2.5f)),
            new(0, 3), new(0, 4), new(3, 2), new(0, 1)];
        var catalog = DungeonKillExperienceCatalog.Parse(table, server, Resolve);
        That(catalog.GetMonsterRate(0, true) == 3 && catalog.GetMonsterRate(3, true) == 1 &&
            catalog.GetMonsterRate(9, false) == 1 && catalog.GetDifficultyRate(5) == 1,
            "单项怪物倍率覆盖Boss及越界类型，精英三倍不加在Boss上");
        That(DungeonKillExperiencePolicy.Calculate(100, 1, 1.3f, catalog.GetMonsterRate(0, true), 20, 20) == 390,
            "非Boss精英按资源品级倍率再乘三");
        Reject<InvalidDataException>(() => DungeonKillExperienceCatalog.Parse(table, [], Resolve), "缺倍率拒绝");
        Reject<InvalidDataException>(() => DungeonKillExperienceCatalog.Parse(table, server[..^1], Resolve), "空怪物倍率拒绝");
        Reject<InvalidDataException>(() => DungeonKillExperiencePolicy.Calculate(100, float.NaN, 1, 1, 1, 1), "非法权重拒绝");
        Reject<InvalidDataException>(() => DungeonKillExperiencePolicy.Calculate(100, 1, -1, 1, 1, 1), "负倍率拒绝");
        Reject<InvalidDataException>(() => DungeonKillExperiencePolicy.Calculate(int.MaxValue, 1, 1, 1, 1, 1),
            "float32把最大i32舍入到越界值时拒绝转换");
        Console.WriteLine($"PASS {Count - checks} 击杀经验倍率与等级差数值断言");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var table = DungeonKillExperienceCatalog.Load(pvf);
        That(table.GetExperience(1) == 30 && table.GetExperience(2) == 60 &&
            table.GetExperience(3) == 72 && table.GetExperience(300) == 64906,
            "基础经验直接使用匹配表的等级与经验整数对");
        var dungeon = DungeonCatalog.Load(pvf).Get(3);
        var maze = dungeon.Mazes.Single(candidate => candidate.QuestConnectionTokens.Count == 3 &&
            candidate.QuestConnectionTokens[1] == new PvfToken(0, 3145));
        var rooms = DungeonRoomCatalog.Load(pvf);
        var room25 = rooms.Get(maze.MapSpecification!.Single(map => map.Id == 76125));
        var room26 = rooms.Get(maze.MapSpecification!.Single(map => map.Id == 76126));
        That(room25.Monsters[4].RawValues.Take(3).SequenceEqual([1, 1, 0]) &&
            room26.Monsters.Count == 2 && room26.Monsters.All(monster => monster.RawValues[2] == 3 &&
                table.GetExperience(monster.RawValues[2]) == 72),
            "76125特殊行原值保留，76126资源候选Lv3对应72，不假定特殊行实际生成等级");
        var special = DungeonMonsterPlan.FromRoom(room25, 99, 100, dungeon.BasisLevel);
        That(special.Monsters.Count == 6 && special.Monsters[4].Source!.RawValues[2] == 0 &&
            special.Monsters[4].CandidateLevel == 5 && table.GetExperience(special.Monsters[4].CandidateLevel) == 95,
            "76125相对等级用副本基准5加偏移0，查Lv5基础95");
        var boss = DfoServer.Game.Dungeons.DungeonMonsterPlan.FromRoom(room26, 99, 200);
        That(boss.Monsters.Count == 2 && boss.Monsters.Select(monster => monster.SpawnType).SequenceEqual(new byte[] { 3, 0 }) &&
            boss.Monsters.All(monster => monster.CandidateLevel == 3 && table.GetExperience(monster.CandidateLevel) == 72),
            "完整Boss房保留两行生成类型，按已支持计划等级查经验");
        That(DungeonKillExperienceCatalog.GetDungeonWeight(dungeon) == 1 && DungeonKillExperienceCatalog.GetNamedMonsters(dungeon).Count == 0,
            "当前副本缺权重和精英声明时采用中性值");
        float[] difficulty = [1.3f, 2, 2.5f, 3, 4];
        uint[] sameLevel = [93, 144, 180, 216, 288];
        for (byte index = 0; index < difficulty.Length; index++)
            That(table.GetDifficultyRate(index) == difficulty[index] &&
                DungeonKillExperiencePolicy.Calculate(72, 1, table.GetDifficultyRate(index), table.GetMonsterRate(3, false), 3, 3) == sameLevel[index],
                "匹配PVF五项难度及Boss基础倍率 " + index);
        That(DungeonKillExperienceCatalog.GetDungeonWeight(dungeon with { RawSections = [new("experience increasing point", [new(0, -1)])] }) == 1 &&
            DungeonKillExperienceCatalog.GetDungeonWeight(dungeon with { RawSections = [new("experience increasing point", [new(2, BitConverter.SingleToInt32Bits(1.25f))])] }) == 1.25f &&
            DungeonKillExperienceCatalog.GetNamedMonsters(dungeon with { RawSections = [new("named monster", [new(0, 1), new(0, 2)])] }).SequenceEqual([1, 2]),
            "副本负权重采用1，显式权重和精英ID原样解析");
        Reject<NotSupportedException>(() => table.GetExperience(0), "零等级不补经验");
        Reject<NotSupportedException>(() => table.GetExperience(301), "表外等级拒绝而不夹取");
        Reject<InvalidDataException>(() => DungeonKillExperienceCatalog.Parse([]), "空表拒绝");
        Reject<InvalidDataException>(() => DungeonKillExperienceCatalog.Parse([new(0, 1)]), "残缺整数对拒绝");
        Reject<InvalidDataException>(() => DungeonKillExperienceCatalog.Parse([new(0, 1), new(2, 30)]), "非整数奖励拒绝");
        Reject<InvalidDataException>(() => DungeonKillExperienceCatalog.Parse([new(0, 0), new(0, 30)]), "非法等级拒绝");
        Reject<InvalidDataException>(() => DungeonKillExperienceCatalog.Parse([new(0, 1), new(0, 0)]), "零奖励拒绝");
        Reject<InvalidDataException>(() => DungeonKillExperienceCatalog.Parse([new(0, 1), new(0, -1)]), "负奖励拒绝");
        Reject<InvalidDataException>(() => DungeonKillExperienceCatalog.Parse([new(0, 1), new(0, 30), new(0, 1), new(0, 60)]), "重复等级拒绝");
        Reject<InvalidDataException>(() => DungeonKillExperienceCatalog.Parse([new(0, 2), new(0, 60), new(0, 1), new(0, 30)]), "乱序等级拒绝");
        Console.WriteLine($"PASS {Count - checks} 本服击杀经验规则与匹配资源断言");

    }
}
