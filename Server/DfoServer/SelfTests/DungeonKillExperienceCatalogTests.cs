using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class DungeonKillExperienceCatalogTests
{
    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var table = DungeonKillExperienceCatalog.Load(pvf);
        That(table.GetExperience(1) == 30 && table.GetExperience(2) == 60 &&
            table.GetExperience(3) == 72 && table.GetExperience(300) == 64906,
            "本服倍率1规则直接使用匹配表的等级与经验整数对");
        var maze = DungeonCatalog.Load(pvf).Get(3).Mazes.Single(candidate => candidate.QuestConnectionTokens.Count == 3 &&
            candidate.QuestConnectionTokens[1] == new PvfToken(0, 3145));
        var rooms = DungeonRoomCatalog.Load(pvf);
        var room25 = rooms.Get(maze.MapSpecification!.Single(map => map.Id == 76125));
        var room26 = rooms.Get(maze.MapSpecification!.Single(map => map.Id == 76126));
        That(room25.Monsters[4].RawValues.Take(3).SequenceEqual([1, 1, 0]) &&
            room26.Monsters.Count == 2 && room26.Monsters.All(monster => monster.RawValues[2] == 3 &&
                table.GetExperience(monster.RawValues[2]) == 72),
            "76125特殊行原值保留，76126资源候选Lv3对应72，不假定特殊行实际生成等级");
        var special = DfoServer.Game.Dungeons.DungeonMonsterPlan.FromRoom(room25, 99, 100);
        That(special.Monsters.Count == 6 && special.Monsters[4].Source.RawValues[2] == 0 &&
            special.Monsters[4].CandidateLevel == 1 && table.GetExperience(special.Monsters[4].CandidateLevel) == 30,
            "本服限定76125特殊行保留原值，使用实际规划Lv1查击杀经验");
        var boss = DfoServer.Game.Dungeons.DungeonMonsterPlan.FromRoom(room26, 99, 200);
        That(boss.Monsters.Count == 2 && boss.Monsters.Select(monster => monster.SpawnType).SequenceEqual(new byte[] { 3, 0 }) &&
            boss.Monsters.All(monster => monster.CandidateLevel == 3 && table.GetExperience(monster.CandidateLevel) == 72),
            "完整Boss房保留两行生成类型，按已支持计划等级查经验");
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
