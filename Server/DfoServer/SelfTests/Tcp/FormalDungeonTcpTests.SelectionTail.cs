using System.Buffers.Binary;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    static byte[] EnterNativeSelectionTail(TcpTestFixture fixture, TcpPeer peer)
    {
        peer.Send(31, [31, 0, 0x49, 0x0C]);
        That(peer.Expect(1, 31).Body[0] == 1 &&
            fixture.Query("SELECT job FROM characters WHERE id=$p0", peer.Id) is [[9L]],
            "本版女鬼剑实际接取3145，用原角色资格验证D3普通任务入图尾字段");
        RefreshedQuests(peer);
        SelectMap(peer);
        peer.Barrier();

        byte[] native = Convert.FromHexString("030000000000000000FFFF0000000000490C000000FFFFFFFF");
        byte[] rejected = (byte[])native.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(rejected.AsSpan(5), 1);
        string before = State();
        peer.Send(16, rejected);
        peer.Expect(1, 16, [0, 9, 0, 0]);
        peer.Barrier();
        That(State() == before,
            "普通任务非零Unknown5拒绝后保留角色revision、资产、钱包、疲劳、任务、尝试号及UID序列，不发布入图或回城通知");

        // 拒绝后不重新请求选图；恢复Unknown5，沿原任务和场景继续入图。
        peer.Send(16, native);
        var information = peer.Expect(0, 28).Body;
        var map = peer.Expect(0, 29).Body;
        That(information.Length == 36 && U32(information, 0) == 3 && map.Length >= 19 &&
            U32(map, 14) == 76121 && map[18] > 0,
            "女鬼剑3145原25B请求尾FFFFFFFF保留原选图并返回完整28及实际任务起点29");
        return map;

        string State() => OrdinaryPersistentState(fixture, peer.Id) +
            fixture.Snapshot("SELECT revision FROM characters WHERE id=$p0", peer.Id) +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY rowid") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }
}
