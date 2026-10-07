using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class DungeonKillExperienceCatalogTests
{
    public static void Run(PvfArchive pvf)
    {
        int checks = 0;
        var table = DungeonKillExperienceCatalog.Load(pvf);
        Check(table.GetExperience(1) == 30 && table.GetExperience(2) == 60 &&
            table.GetExperience(3) == 72 && table.GetExperience(300) == 64906,
            "本服倍率1规则直接使用匹配表的等级与经验整数对");
        var maze = DungeonCatalog.Load(pvf).Get(3).Mazes.Single(candidate => candidate.QuestConnectionTokens.Count == 3 &&
            candidate.QuestConnectionTokens[1] == new PvfToken(0, 3145));
        var rooms = DungeonRoomCatalog.Load(pvf);
        var room25 = rooms.Get(maze.MapSpecification!.Single(map => map.Id == 76125));
        var room26 = rooms.Get(maze.MapSpecification!.Single(map => map.Id == 76126));
        Check(room25.Monsters[4].RawValues.Take(3).SequenceEqual([1, 1, 0]) &&
            room26.Monsters.Count == 2 && room26.Monsters.All(monster => monster.RawValues[2] == 3 &&
                table.GetExperience(monster.RawValues[2]) == 72),
            "76125特殊行原值保留，76126资源候选Lv3对应72，不假定特殊行实际生成等级");
        Reject<NotSupportedException>(() => DfoServer.Game.Dungeons.DungeonMonsterPlan.FromRoom(room25, 99, 100),
            "未支持的特殊生成行不能用于已确认死亡奖励");
        Reject<NotSupportedException>(() => DfoServer.Game.Dungeons.DungeonMonsterPlan.FromRoom(room26, 99, 200),
            "尚未支持Boss生成时拒绝残缺计划");
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
        Console.WriteLine($"PASS {checks} 本服击杀经验规则与匹配资源断言");

        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL 击杀经验规则: " + name);
        }
        void Reject<T>(Action action, string name) where T : Exception
        {
            try { action(); }
            catch (T) { Check(true, name); return; }
            throw new InvalidOperationException("FAIL expected " + typeof(T).Name + ": " + name);
        }
    }
}
