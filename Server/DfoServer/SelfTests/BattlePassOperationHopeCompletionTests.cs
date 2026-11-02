using DfoServer.Game.BattlePass;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.BattlePass;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassOperationHopeCompletionTests
{
    public static void RunCore()
    {
        int before = Count;
        var definition = new BattlePassMission(49, 3, 1000, 8, "完成希望整次通关", 81);
        That(BattlePassOperationHopeCompletion.MatchesMission(1, definition), "只接原周49整次希望完成");
        foreach (byte category in new byte[] { 0, 2, byte.MaxValue })
        {
            That(!BattlePassOperationHopeCompletion.MatchesMission(category, definition), "其他分类不能借原周完成");
            Reject<InvalidDataException>(() => BattlePassOperationHopeCompletion.NextCount(category, definition, 0), "错误分类拒绝累计");
        }
        foreach (var other in new[] { definition with { Id = 48 }, definition with { RawTypeCode = 13 },
            definition with { RawParameter = 8523 }, definition with { RequiredCount = 4 }, definition with { Experience = 100 } })
        {
            That(!BattlePassOperationHopeCompletion.MatchesMission(1, other), "变异定义不借原希望完成");
            Reject<InvalidDataException>(() => BattlePassOperationHopeCompletion.NextCount(1, other, 0), "变异定义拒绝累计");
        }
        That(BattlePassOperationHopeCompletion.NextCount(1, definition, 0) == 1 &&
            BattlePassOperationHopeCompletion.NextCount(1, definition, 1) == 2 &&
            BattlePassOperationHopeCompletion.NextCount(1, definition, 2) == 3 &&
            BattlePassOperationHopeCompletion.NextCount(1, definition, 3) == 3, "一次原领奖仅一整次，封顶原3");
        foreach (long current in new[] { -1L, 4L })
            Reject<InvalidDataException>(() => BattlePassOperationHopeCompletion.NextCount(1, definition, current), "损坏旧进度不能静默修正");
        var identity = new DungeonRunIdentity(1, 0, 1, Guid.Parse("00000001-0002-0003-0004-000000000005"), 1);
        Guid acceptance = Guid.Parse("00000006-0007-0008-0009-00000000000a");
        string key = BattlePassOperationHopeCompletion.Identity(identity, 0, acceptance);
        That(key.Length == 32 && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            key == BattlePassOperationHopeCompletion.Identity(identity, 0, acceptance), "完整来源确定性32hex符合原schema");
        foreach (var other in new[] { identity with { InstanceId = Guid.NewGuid() }, identity with { CharacterId = 2 },
            identity with { TaskAttempt = 2 }, identity with { Epoch = 2 } })
            That(key != BattlePassOperationHopeCompletion.Identity(other, 0, acceptance), "原实例角色attempt与epoch身份不混");
        That(key != BattlePassOperationHopeCompletion.Identity(identity, 1, acceptance) &&
            key != BattlePassOperationHopeCompletion.Identity(identity, 0, Guid.NewGuid()), "原maze与acceptance不混");
        foreach (var other in new[] { identity with { InstanceId = Guid.Empty }, identity with { CharacterId = 0 },
            identity with { TaskAttempt = 0 }, identity with { Epoch = 0 }, identity with { QuestId = 1 } })
            Reject<InvalidDataException>(() => BattlePassOperationHopeCompletion.Identity(other, 0, acceptance), "缺失或其他路线身份不借希望");
        Reject<InvalidDataException>(() => BattlePassOperationHopeCompletion.Identity(identity, -1, acceptance), "负迷宫不构成原身份");
        Reject<InvalidDataException>(() => BattlePassOperationHopeCompletion.Identity(identity, 0, Guid.Empty), "空接取不构成原身份");
        Console.WriteLine($"PASS {Count - before} BattlePassOperationHopeCompletionTests core");
    }
}
