using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PreyTeamTcpTests
{
    static void VerifyPotionLimit(TcpTestFixture fixture, TcpPeer peer, TcpTestResources resources,
        LukeRaidClockTests.Clock clock, List<WirePacket> packets)
    {
        var catalog = new StackableDungeonLimitCatalog(resources.Archive);
        That(catalog.Potions.Count == 17 && catalog.Dungeons.Values.All(value => value == 10),
            "最终候选资源明确十七种共享额度药品及每图十次");
        Bytes(packets.Last(row => row is { Kind: 0, Opcode: 1596 }).Body, [10, 0, 0, 0],
            "加载完成后以原生四字节1596初始化十次，不再保留客户端初值零");
        That(resources.ItemResources.RecoveryConsumables.TryGet(7740, out var bread) && bread.CoolTime == 10000 &&
            resources.ItemResources.RecoveryConsumables.TryGet(8, out var remy) && remy.CoolTime == 10000,
            "实际面包与雷米有已知冷却，回归使用真实时钟和原资源");
        for (int count = 0; count < 10; count++)
        {
            if (count != 0) clock.Advance(11);
            FiendwarRaidTcpTests.VerifyConsumable(fixture, peer, true, checked((uint)(9000 + count)), count % 2 == 0 ? 7740 : 8);
            if (count == 0) FiendwarRaidTcpTests.VerifyConsumable(fixture, peer, false, 9100, 7740);
        }
        clock.Advance(11);
        FiendwarRaidTcpTests.VerifyConsumable(fixture, peer, false, 9200, 7740);
        FiendwarRaidTcpTests.VerifyConsumable(fixture, peer, false, 9201, 8);
        That(SpinWait.SpinUntil(() => fixture.Events().Any(row => row.GetProperty("Kind").GetString() == "consumable_use" &&
            row.GetProperty("Detail").GetProperty("Reason").GetString() == "dungeon-potion-limit-exhausted"), TimeSpan.FromSeconds(10)),
            "第十一次由服务端真实额度拒绝，不只是客户端按钮变红或冷却尚未结束");
    }
}
