using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.PcRoom;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class FatigueAccelerationTests
{
    internal static void RunCore()
    {
        int start = Count;
        That(!FatigueAccelerationRequests.Change([0]) && FatigueAccelerationRequests.Change([1]), "燃烧选择只接受原生单字节开关");
        foreach (byte[] body in new byte[][] { [], [2], [1, 0] })
            Reject<InvalidDataException>(() => FatigueAccelerationRequests.Change(body), "未知状态与多余正文拒绝");
        Bytes(FatigueAccelerationPackets.State(false), [0, 0, 0], "原生490未开启布局");
        Bytes(FatigueAccelerationPackets.State(true), [0, 0, 1], "原生490开启布局");
        Bytes(FatigueAccelerationPackets.Ack(0), [1], "562成功无额外正文");
        Bytes(FatigueAccelerationPackets.Ack(179), [0, 179, 0], "562失败沿共用u16错误码");
        var original = new ExperienceBonuses(137, [new(ExperienceBonusSource.GrowthContract, 41), new(ExperienceBonusSource.Potion, 18)]);
        var reward = FatigueAccelerationRules.Apply(original, 100, 100);
        That(reward.Total == 296 && reward.Ordinary == 137 && reward.Amount(ExperienceBonusSource.FatigueBurning) == 100 &&
            reward.Amount(ExperienceBonusSource.GrowthContract) == 41 && reward.Amount(ExperienceBonusSource.Potion) == 18,
            "燃烧只额外增加原始基础，不放大评分、契约或秘药");
        That(ReferenceEquals(FatigueAccelerationRules.Apply(original, 100, 0), original) &&
            ReferenceEquals(FatigueAccelerationRules.Apply(original, 0, 100), original), "关闭与零经验不追加分项");
        That(FatigueAccelerationRules.Apply(original, 101, 33).Amount(ExperienceBonusSource.FatigueBurning) == 33,
            "资源比例沿基础独立向下取整");
        Reject<InvalidDataException>(() => FatigueAccelerationRules.Apply(reward, 100, 100), "同一奖励不能重复冻结燃烧分项");
        Reject<InvalidDataException>(() => FatigueAccelerationRules.Apply(original, 100, -1), "负比例拒绝");
        var map = new DungeonMap("boss", new(0, 0), 1, "map/burn-fixture.map");
        var maze = new DungeonMaze(0, 1, 1, "burn-fixture", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(1, 0, 3, 0, byte.MaxValue, 0, maze, map, map);
        That(FatigueAccelerationRules.Eligible(entry), "真实普通疲劳实例允许冻结燃烧");
        var story = entry with { QuestId = 3146, RouteQuestId = 3146, ActiveQuestSlot = 0 };
        That(!story.IsOrdinary && !story.UsesFatigue && FatigueAccelerationRules.Eligible(story),
            "正常显式剧情路线沿独立任务疲劳归属，仍允许冻结网吧燃烧经验");
        foreach (var candidate in new[] { entry, story })
        foreach (var excluded in new[] { candidate with { IsTutorial = true }, candidate with { IsPractice = true },
            candidate with { DisableFatigueConsumption = true }, candidate with { DungeonId = 5000, Maze = maze with { IsTrainingRoom = true } },
            candidate with { DungeonId = 7142, Maze = maze with { IsClassExperience = true } },
            candidate with { Maze = maze with { IsTournament = true } }, candidate with { Maze = maze with { IsGriefTower = true } },
            candidate with { IsBossRush = true } })
            That(!FatigueAccelerationRules.Eligible(excluded), "普通与剧情路线均保留教学、独立场景和资源免疲劳排除");
        Store();
        Console.WriteLine($"PASS {Count-start} 网吧疲劳燃烧协议、分项与持久选择断言");
    }

    static void Store()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcroom-fatigue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("pcroom-fatigue", () => { });
            var first = repository.Create(owner, new("PcBurnA", new(1, 0, 10, 10), null));
            var second = repository.Create(owner, new("PcBurnB", new(1, 0, 10, 10), null));
            That(!repository.ReadFatigueAcceleration(owner, first.Id), "旧角色未保存时默认不强制开启");
            repository.SaveFatigueAcceleration(owner, first.Id, true);
            That(repository.ReadFatigueAcceleration(owner, first.Id) && !repository.ReadFatigueAcceleration(owner, second.Id),
                "选择按角色隔离，同账号不能串用");
            That(repository.LoadCharacter(owner, first.Slot).Revision == first.Revision, "开关不改角色资产revision");
            using var db = new SqliteConnection("Data Source=" + path);
            db.Open();
            void Execute(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            Execute("CREATE TRIGGER reject_burn BEFORE UPDATE ON character_fatigue_acceleration BEGIN SELECT RAISE(ABORT,'burn rollback'); END");
            repository.SaveFatigueAcceleration(owner, first.Id, true);
            Reject<SqliteException>(() => repository.SaveFatigueAcceleration(owner, first.Id, false), "真实SQL失败不能伪造保存成功");
            That(repository.ReadFatigueAcceleration(owner, first.Id), "SQL失败保留原选择");
            Execute("DROP TRIGGER reject_burn");
            repository.SaveFatigueAcceleration(owner, first.Id, false);
            That(!new CharacterRepository(path).ReadFatigueAcceleration(owner, first.Id), "关闭选择可冷仓储重开恢复");
            using var other = owners.Acquire("pcroom-other", () => { });
            Reject<InvalidOperationException>(() => repository.ReadFatigueAcceleration(other, first.Id), "其他账号不能读取选择");
            Reject<InvalidOperationException>(() => repository.SaveFatigueAcceleration(other, first.Id, true), "其他账号不能写入选择");
            using var replacement = owners.Acquire("pcroom-fatigue", () => { });
            Reject<InvalidOperationException>(() => repository.SaveFatigueAcceleration(owner, first.Id, true), "旧generation不能改变选择");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
