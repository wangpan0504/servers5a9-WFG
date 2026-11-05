using DfoServer.Development.Dungeons;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class DebugDungeonKillTcpTests
{
    internal static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
#if DEBUG
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "debug-dungeon-kill"), resources: resources);
        await fixture.StartAsync();
        using var solo = Open(fixture, "debugsolo");
        using var leader = Open(fixture, "debugleader");
        using var member = Open(fixture, "debugmember");
        var peers = new[] { solo, leader, member };
        for (int slot = 0; slot < peers.Length; slot++)
        {
            var peer = peers[slot];
            peer.Create("DebugKill" + slot);
            fixture.Sql("INSERT INTO character_completed_quests(character_id,quest_id) VALUES($p0,3145)", peer.Id);
            peer.Select(prepared: true);
            peer.Send(1426); Drain(peer);
            peer.Send(36, Area(38, 2, 1085, 249, 38, 0, kind: 5)); Drain(peer);
            peer.Send(2, Build(w => { w.Write((byte)5); w.Write(0x0100007fu); w.Write(0x0100007fu);
                w.Write(checked((ushort)(30000 + slot))); w.Write(512u); }));
            Drain(peer);
        }
        foreach (var peer in peers) Drain(peer);

        string town = fixture.Snapshot();
        Send(solo, 3, "#kill");
        That(Drain(solo).All(row => row is not { Kind: 0, Opcode: 38 }) && fixture.Snapshot() == town,
            "Debug城镇秒杀不创建正式死亡或修改数据库");

        var soloMap = Enter([solo]);
        solo.Send(37, new byte[8]);
        That(Drain(solo).Any(row => row is { Kind: 0, Opcode: 30 }), "单人真实37完成加载");
        KillAndCheck([solo], solo, soloMap, "#kill");

        byte[] Peer(ushort id, bool invitation) => Build(w =>
        { w.Write(id); w.Write((byte)0); w.Write(0u); if (invitation) w.Write(new byte[6]); });
        leader.Send(10, Peer(member.Id, true));
        That(Drain(leader).Any(row => row is { Kind: 1, Opcode: 10, Body: [1] }) &&
            Drain(member).Any(row => row is { Kind: 0, Opcode: 7 }), "双人沿真实邀请入队");
        member.Send(11, Peer(leader.Id, false));
        That(Drain(member).Any(row => row is { Kind: 0, Opcode: 9 }) &&
            Drain(leader).Any(row => row is { Kind: 0, Opcode: 9 }), "双人共享队伍已安装");
        var partyMap = Enter([leader, member]);
        Send(member, 2, "#kill");
        That(Drain(member).All(row => row is not { Kind: 0, Opcode: 38 }), "尚未全员37的共享实例不执行秒杀");
        Drain(leader);
        leader.Send(37, new byte[8]); Drain(leader);
        member.Send(37, new byte[8]);
        That(new[] { Drain(member), Drain(leader) }.All(rows => rows.Any(row => row is { Kind: 0, Opcode: 30 })),
            "双人真实加载屏障完成后才可清怪");
        Send(member, 46, "#kill", member.Id);
        That(Drain(member).Any(row => row is { Kind: 0, Opcode: 12 }) &&
            Drain(leader).All(row => row is not { Kind: 0, Opcode: 38 }), "私聊精确命令保持原聊天，不清共享房间");
        KillAndCheck([leader, member], member, partyMap, "#raidkill");

        byte[] Enter(TcpPeer[] participants)
        {
            var actor = participants[0];
            actor.Send(15, BitConverter.GetBytes(0u));
            foreach (var peer in participants) That(Drain(peer).Any(row => row is { Kind: 0, Opcode: 27 }), "原选图层覆盖全部成员");
            actor.Send(15, BitConverter.GetBytes(3u)); Drain(actor);
            foreach (var peer in participants.Skip(1)) Drain(peer);
            actor.Send(16, Convert.FromHexString("030000000000000000FFFF0000000000000000000000000000"));
            byte[][] maps = participants.Select(peer => Drain(peer).Single(row => row is { Kind: 0, Opcode: 29 }).Body).ToArray();
            That(maps.All(map => map.SequenceEqual(maps[0])), "全部成员共享同一真实房间和对象key");
            return maps[0];
        }

        void KillAndCheck(TcpPeer[] participants, TcpPeer actor, byte[] map, string command)
        {
            ushort[] keys = Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                .Where(at => map[at + 13] != 9 && map[at + 14] == 0).Select(at => U16(map, at + 6)).ToArray();
            That(keys.Length > 1, "秒杀回归使用本房多个真实敌对对象");
            // 先等请求者的命令完成；其它连接先发屏障可能抢在本次命令之前通过。
            var recipients = participants.OrderBy(peer => ReferenceEquals(peer, actor) ? 0 : 1).ToArray();
            Send(actor, participants.Length == 1 ? (byte)3 : (byte)2, command);
            foreach (var peer in recipients)
            {
                var rows = Drain(peer);
                That(rows.Where(row => row is { Kind: 0, Opcode: 38 }).Select(row => U16(row.Body)).ToHashSet().SetEquals(keys) &&
                    rows.All(row => row is not { Kind: 0, Opcode: 12 }), "一次命令完成本房全部正式死亡并同步各成员，不广播命令文本");
                That(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", peer.Id)
                    is [[long count]] && count == keys.Length, "每名成员均经过原击杀事务，逐只获得唯一成长回执");
            }
            string committed = fixture.Snapshot();
            Send(actor, participants.Length == 1 ? (byte)3 : (byte)2, command);
            foreach (var peer in recipients) That(Drain(peer).All(row => row is not { Kind: 0, Opcode: 38 }), "重复秒杀不重发已退休目标");
            That(fixture.Snapshot() == committed, "重复命令不重奖、不重写回执或资产");
        }
#else
        await Task.CompletedTask;
#endif
    }

    static void Send(TcpPeer peer, byte type, string text, uint target = 0) => peer.Send(17, Build(w =>
    { w.Write(type); w.Write(peer.Id); w.Write(target); w.Write((uint)text.Length); w.Write(System.Text.Encoding.ASCII.GetBytes(text)); }));

    static WirePacket[] Drain(TcpPeer peer) => PartyDungeonTcpTests.Drain(peer);
}
