using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassGuildContributionTests
{
    public static void RunCore()
    {
        int before = Count;
        var definition = new BattlePassMission(9, 150, 100, 9, "获得公会贡献度", -1);
        That(BattlePassGuildContributionSource.Matches(definition), "公会贡献只匹配原daily9定义");
        That(BattlePassGuildContributionSource.NextCount(definition, 0, 100) == 100 &&
            BattlePassGuildContributionSource.NextCount(definition, 100, 50) == 150,
            "登录100与聊天50按真实积分金额累加，不按两次请求冒两点");
        That(BattlePassGuildContributionSource.NextCount(definition, 149, 150) == 150 &&
            BattlePassGuildContributionSource.NextCount(definition, 0, int.MaxValue) == 150 &&
            BattlePassGuildContributionSource.NextCount(definition, 150, 100) == 150,
            "大厅批量或高额正式贡献只封顶原150，不溢出或溢发");
        foreach (var other in new[] { definition with { Id = 10 }, definition with { RawTypeCode = 10 },
            definition with { RawParameter = 8 }, definition with { RequiredCount = 151 }, definition with { Experience = 101 } })
        {
            That(!BattlePassGuildContributionSource.Matches(other), "变异定义不能借公会贡献来源");
            Reject<InvalidDataException>(() => BattlePassGuildContributionSource.NextCount(other, 0, 100), "变异定义不得累加");
        }
        foreach (int amount in new[] { 0, -1 })
            Reject<InvalidDataException>(() => BattlePassGuildContributionSource.NextCount(definition, 0, amount), "非正积分不是获得贡献");
        foreach (long current in new[] { -1L, 151L })
            Reject<InvalidDataException>(() => BattlePassGuildContributionSource.NextCount(definition, current, 100), "损坏进度不能被贡献静默修正");
        Console.WriteLine($"PASS {Count - before} BattlePassGuildContributionTests core");
    }
}
