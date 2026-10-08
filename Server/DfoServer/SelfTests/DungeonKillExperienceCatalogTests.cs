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
        var plan25 = DfoServer.Game.Dungeons.DungeonMonsterPlan.FromRoom(room25, 99, 100);
        That(plan25.Monsters.Count == 6 && plan25.Monsters[4].Source.RawValues[2] == 0 &&
            plan25.Monsters[4].CandidateLevel == 1 && table.GetExperience(plan25.Monsters[4].CandidateLevel) == 30,
            "批准的特殊行明确实际Lv1取30，不将raw第三列0当生成等级或第二列1套通用公式");
        var plan26 = DfoServer.Game.Dungeons.DungeonMonsterPlan.FromRoom(room26, 99, 200);
        That(plan26.Monsters.Count == 2 && plan26.Monsters[0].MonsterId == 107000903 && plan26.Monsters[0].SpawnType == 3 &&
            plan26.Monsters[0].CandidateLevel == 3 && table.GetExperience(3) == 72 &&
            plan26.Monsters[1].MonsterId == 107000906 && plan26.Monsters[1].Team == 0 &&
            plan26.Monsters.Select(monster => monster.SpawnType).SequenceEqual(new byte[] { 3, 0 }) &&
            plan26.Monsters.All(monster => monster.CandidateLevel == 3 && table.GetExperience(monster.CandidateLevel) == 72),
            "正式Boss生成保留Lv3经验规则，Tobi同team资格由实际实例拒绝签发成长");
        var changedSpecial = room25.Monsters[4] with { RawValues = [1, 1, 0, 504, 233, 0, 1, 1] };
        Reject<NotSupportedException>(() => DfoServer.Game.Dungeons.DungeonMonsterPlan.FromRoom(room25 with
            { Monsters = room25.Monsters.Take(4).Append(changedSpecial).ToArray() }, 99, 100),
            "特殊行批准不扩展到不同资源位置或未闭合相对等级声明");
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
