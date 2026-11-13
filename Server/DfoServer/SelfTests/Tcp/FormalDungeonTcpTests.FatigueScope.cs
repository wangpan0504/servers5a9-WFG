using System.Buffers.Binary;
using DfoServer.Game.Auth;
using DfoServer.Infrastructure;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunFatigueScopeAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "fatigue-account"), gm: true, resources: resources);
        await fixture.StartAsync();
        var authentication = new AuthenticationService(new AuthenticationRepository(fixture.Database));
        authentication.Create("fatigue-other", NativeLoginWire.Digest);
        using var first = Open(fixture);
        first.Create("FatigueFirst");
        ushort firstId = first.Id;
        first.Create("FatigueNext");
        ushort nextId = first.Id;
        using var other = Open(fixture, "fatigue-other");
        other.Create("FatigueOther");
        ushort otherId = other.Id;
        fixture.Sql("UPDATE character_fatigue SET used=maximum,remaining=0");
        string stored = fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id");

        SelectFull(first, 0, firstId, false);
        SelectFull(other, 0, otherId, false);
        That(Command("account 10000 infinite-fatigue on") == "OK {\"infiniteFatigue\":true}",
            "当前账号开关只开启本账号");
        ExpectFatigue(first, true);
        other.Barrier();
        That(Command("account fatigue-other infinite-fatigue") == "OK {\"infiniteFatigue\":false}", "另一账号仍默认关闭且没有收到跨账号刷新");
        That(fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id") == stored,
            "开关只刷新本账号显示，不改任一角色真实疲劳");

        first.Send(7);
        first.Expect(1, 7, [1]);
        first.Expect(0, 2);
        That(Command("account fatigue-other infinite-fatigue on") == "OK {\"infiniteFatigue\":true}",
            "另一账号可独立开启");
        ExpectFatigue(other, true);
        SelectFull(first, 1, nextId, true);
        EntryTown(first);
        SelectMap(first);
        byte[] selection = Selection();
        BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16), 0);
        first.Send(16, selection);
        first.Expect(0, 28);
        first.Expect(0, 29);
        Load(first, fatigue: false);
        ExpectFatigue(first, true);
        first.Barrier();
        That(fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id") == stored &&
            fixture.Query("SELECT count(*) FROM character_dungeon_fatigue_receipts") is [[0L]],
            "同账号第二角色沿用本账号开关，零余额仍可真实入场且不扣疲劳或写扣点账本");

        other.Dispose();
        using var returning = Open(fixture, "fatigue-other");
        SelectFull(returning, 0, otherId, true);

        That(Command("infinite-fatigue off") == "OK {\"infiniteFatigue\":false}", "只关闭当前账号开关");
        ExpectFatigue(first, false);
        returning.Barrier();
        That(Command("account fatigue-other infinite-fatigue") == "OK {\"infiniteFatigue\":true}", "关闭当前账号不改变另一个账号开关");
        That(fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id") == stored,
            "换角和跨账号切换后仍保留所有角色真实余额");
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        That(Command("infinite-fatigue") == "OK {\"infiniteFatigue\":false}" &&
            Command("account fatigue-other infinite-fatigue") == "OK {\"infiniteFatigue\":true}", "重启保留两个账号不同的开关状态");
        await fixture.StopAsync();
        Console.WriteLine($"PASS {Count - before} infinite fatigue per-account and character TCP checks");

        string Command(string line)
        {
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, line, out var result), "GM开关命令返回结果");
            return result;
        }
        static void ExpectFatigue(TcpPeer peer, bool enabled) => peer.Expect(0, 36,
            enabled ? [0, 0, 156, 0, 0, 0, 0, 0, 0, 0] : [156, 0, 156, 0, 0, 0, 0, 0, 0, 0]);
        static void SelectFull(TcpPeer peer, ushort slot, ushort id, bool enabled)
        {
            byte[] ack = peer.Select(slot).Single(row => row.Kind == 1 && row.Opcode == 4).Body;
            That(peer.Id == id && U16(ack, 11) == (enabled ? 0 : 156) && U16(ack, 13) == 156 &&
                U16(ack, 228) == 0,
                "选角按本账号开关显示实际或满疲劳，不凭无限疲劳复制电量");
        }
    }
}
