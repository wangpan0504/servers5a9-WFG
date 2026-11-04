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
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "fatigue-global"), gm: true, resources: resources);
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

        SelectFull(first, 0, firstId);
        SelectFull(other, 0, otherId);
        That(Command("account 10000 infinite-fatigue off") == "OK {\"infiniteFatigue\":false}",
            "带账号前缀的开关仍修改整个Runtime，不缩为当前角色或账号");
        ExpectFatigue(first, false);
        ExpectFatigue(other, false);
        That(fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id") == stored,
            "两账号同时关闭只刷新显示，不改任一角色真实疲劳");

        first.Send(7);
        first.Expect(1, 7, [1]);
        first.Expect(0, 2);
        That(Command("account fatigue-other infinite-fatigue on") == "OK {\"infiniteFatigue\":true}",
            "从另一账号开启仍作用全局，不要求原角色在线");
        ExpectFatigue(other, true);
        SelectFull(first, 1, nextId);
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
            "同账号第二角色沿用全局开关，零余额仍可真实入场且不扣疲劳或写扣点账本");

        other.Dispose();
        using var returning = Open(fixture, "fatigue-other");
        SelectFull(returning, 0, otherId);

        That(Command("infinite-fatigue off") == "OK {\"infiniteFatigue\":false}", "再次关闭共享开关");
        ExpectFatigue(first, false);
        ExpectFatigue(returning, false);
        That(fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id") == stored,
            "换角和跨账号切换后仍保留所有角色真实余额");
        await fixture.StopAsync();
        Console.WriteLine($"PASS {Count - before} infinite fatigue global account and character TCP checks");

        string Command(string line)
        {
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, line, out var result), "GM开关命令返回结果");
            return result;
        }
        static void ExpectFatigue(TcpPeer peer, bool enabled) => peer.Expect(0, 36,
            enabled ? [0, 0, 156, 0, 0, 0, 156, 0, 0, 0] : [156, 0, 156, 0, 0, 0, 0, 0, 0, 0]);
        static void SelectFull(TcpPeer peer, ushort slot, ushort id)
        {
            byte[] ack = peer.Select(slot).Single(row => row.Kind == 1 && row.Opcode == 4).Body;
            That(peer.Id == id && U16(ack, 11) == 0 && U16(ack, 228) == 156,
                "每个账号及换选角色均按共享开关显示满疲劳");
        }
    }
}
