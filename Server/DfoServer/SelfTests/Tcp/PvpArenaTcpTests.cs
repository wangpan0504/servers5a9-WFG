using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.Game.Pvp;
using DfoServer.Persistence;
using System.Text.Json;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class PvpArenaTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await RunOrdinaryAsync(source, root, resources);
        await PvpTotalTeamTcpTests.RunAsync(source, root, resources);
        await PvpMatchingTcpTests.RunAsync(source, root, resources);
        await PvpTotalBattleTcpTests.RunAsync(source, root, resources);
    }

    static async Task RunOrdinaryAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        PvpRatingTests.RunResources(resources.Archive);
        var clock = new Inventory.DailyBoosterTestClock(DateTimeOffset.UtcNow);
        await using var f = new TcpTestFixture(source, Path.Combine(root, "pvp-arena"), resources: resources, clock: clock);
        var repository = new CharacterRepository(f.Database, resources.Quests);
        Console.WriteLine("PVP fixture: repository initialized");
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        using (var owners = new AccountOwners().Acquire("arena-tcp-host", () => { })) Seed(owners, "ArenaTCPHost");
        using (var owners = new AccountOwners().Acquire("arena-tcp-guest", () => { })) Seed(owners, "ArenaTCPGst");
        Console.WriteLine("PVP fixture: characters prepared");
        await f.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(f);
        byte pvp = ChannelTcpTests.PvpChannelNumber(f);
        using var host = Open(f, "arena-tcp-host", gamePort: endpoints[pvp], channelNumber: pvp);
        host.Select(prepared: true);
        host.Barrier();
        using var guest = Open(f, "arena-tcp-guest", gamePort: endpoints[pvp], channelNumber: pvp);
        guest.Select(prepared: true);
        guest.Barrier();
        host.Barrier(packet => That(packet is { Kind: 0, Opcode: 2 } && packet.Body[0] == 0 &&
            U16(packet.Body, 121) == guest.Id,
            "另一角色选角只发布真实城镇角色"));
        string before = f.Snapshot();

        // 预设标题3、地图0、密码pw、普通kind0/mode0。
        host.Send(50, [3, 0, 0, 1, 2, 0, 0, 0, (byte)'p', (byte)'w', 0, 0]);
        host.Expect(1, 50, [1]);
        Actor(host, host.Id);
        byte[] created = host.Expect(0, 41).Body;
        ushort room = U16(created, 2);
        Room(created, room, 1, 0, 0, (0, host.Id, 0));
        Room(guest.Expect(0, 41).Body, room, 1, 0, 0, (0, host.Id, 0));
        guest.Send(51, Build(w => { w.Write(room); w.Write((byte)0); }));
        guest.Expect(1, 51, [0, 4, 0]);
        guest.Send(51, Build(w => { w.Write(room); w.Write((byte)1); w.Write(1u); w.Write((byte)'x'); }));
        guest.Expect(1, 51, [0, 19, 0]);
        guest.Barrier();
        host.Barrier();
        guest.Send(51, Build(w => { w.Write(room); w.Write((byte)1); w.Write(2u); w.Write(new byte[] { (byte)'p', (byte)'w' }); }));
        guest.Expect(1, 51, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        Actor(guest, host.Id);
        Actor(guest, guest.Id);
        Room(guest.Expect(0, 41).Body, room, 1, 0, 0, (0, host.Id, 0), (1, guest.Id, 0));
        Actor(host, guest.Id);
        Seats(host, room, 1, (0, host.Id, 0), (1, guest.Id, 0));
        Ready(host, 0, false); Ready(host, 1, false);

        guest.Send(53, [1]);
        Ready(guest, 1, true); Ready(host, 1, true);
        guest.Send(59, [1, 0]);
        guest.Barrier(); host.Barrier();
        host.Send(59, [1, 0]);
        foreach (var peer in new[] { host, guest })
        {
            State(peer, room, 1, 0, 1, 1);
            Seats(peer, room, 1, (0, host.Id, 0), (1, guest.Id, 0));
            Ready(peer, 0, false); Ready(peer, 1, false);
        }
        host.Send(54, [2]);
        foreach (var peer in new[] { host, guest })
        {
            State(peer, room, 1, 0, 1, 2);
            Seats(peer, room, 2, (0, host.Id, 1), (1, guest.Id, 2));
            Ready(peer, 0, false); Ready(peer, 1, false);
        }
        guest.Send(53, [1]); Ready(guest, 1, true); Ready(host, 1, true);
        guest.Send(52, [3, 2]);
        foreach (var peer in new[] { host, guest })
        {
            Seats(peer, room, 2, (0, host.Id, 1), (3, guest.Id, 2));
            Ready(peer, 0, false); Ready(peer, 3, false);
        }
        host.Send(53, [1]); host.Barrier(); guest.Barrier();
        guest.Send(53, [1]); Ready(guest, 3, true); Ready(host, 3, true);
        host.Send(53, [1]);
        foreach (var peer in new[] { host, guest })
        {
            foreach (ushort id in new[] { host.Id, guest.Id })
            {
                byte[] player = peer.Expect(0, 2).Body;
                That(player.Length > 36 && player[0] == 1 && U16(player, 1) == 1 && U16(player, 22) == id,
                    "开战前恢复真实参战者完整Player，保留现有Actor引用");
            }
            peer.Expect(0, 11, [0]);
            Ready(peer, 0, false); Ready(peer, 3, false);
            peer.Expect(0, 45, [1, 2]);
            State(peer, room, 2, 0, 1, 2);
        }
        guest.Send(55, Death(guest.Id, host.Id));
        guest.Send(52, [4, 2]); host.Send(59, [0, 0]); host.Send(53, [1]);
        guest.Barrier(); host.Barrier();
        host.Send(298);
        host.Expect(0, 281, Build(w => w.Write(host.Id))); guest.Expect(0, 281, Build(w => w.Write(host.Id)));
        host.Send(298); host.Send(299, Connections(false)); host.Barrier(); guest.Barrier();
        host.Send(299, Connections(true));
        guest.Send(299, Connections(true));
        host.Barrier(); guest.Barrier();
        guest.Send(298);
        foreach (var peer in new[] { host, guest })
        {
            peer.Expect(0, 281, Build(w => w.Write(guest.Id)));
            peer.Expect(0, 282, []);
        }
        host.Send(55, Death(guest.Id, host.Id));
        guest.Send(55, Death(guest.Id, 60000));
        host.Send(57); guest.Send(57);
        host.Barrier(); guest.Barrier();
        guest.Send(55, Death(guest.Id, host.Id));
        host.Expect(0, 46, [3, 0]); guest.Expect(0, 46, [3, 0]);
        Result(f, host, 0, (host.Id, 1u), (guest.Id, 0u));
        Result(f, guest, 0, (host.Id, 1u), (guest.Id, 0u));
        guest.Send(55, Death(guest.Id, host.Id)); guest.Barrier(); host.Barrier();
        var resultRows = f.Query("SELECT result_json FROM pvp_arena_match_results");
        That(resultRows.Length == 1, "真实TCP死亡及重复报告只保存一条整局回执");
        var savedResult = JsonSerializer.Deserialize<PvpArenaResultRecord>((string)resultRows[0][0])!;
        That(!savedResult.Forfeit && savedResult.Mode == PvpArenaMode.Team && savedResult.Participants.Length == 2 &&
            savedResult.Participants.Single(row => row.CharacterId == host.Id) is
                { Seat: 0, Outcome: PvpArenaOutcome.Win, Account: "arena-tcp-host" } &&
            savedResult.Participants.Single(row => row.CharacterId == guest.Id) is
                { Seat: 3, Outcome: PvpArenaOutcome.Lose, Account: "arena-tcp-guest" },
            "独立读库核对冻结账号、真实角色、挪席后的双方结果，发送失败不能替换本局事实");
        host.Send(57);
        Returned();
        host.Send(57); host.Send(53, [1]); host.Barrier(); guest.Barrier();
        guest.Send(57);
        Returned();
        guest.Send(57); guest.Barrier(); host.Barrier();
        StartAnother();
        clock.Advance(TimeSpan.FromSeconds(29));
        host.Send(57); guest.Send(57); host.Barrier(); guest.Barrier();
        That(f.Query("SELECT count(*) FROM pvp_arena_match_results")[0][0] is 1L, "加载截止前不取消，旧57不能替新局确认返回");
        clock.Advance(TimeSpan.FromSeconds(1));
        Result(f, host, 255, (host.Id, 0u), (guest.Id, 0u));
        Result(f, guest, 255, (host.Id, 0u), (guest.Id, 0u));
        That(f.Query("SELECT result_json FROM pvp_arena_match_results").Select(row =>
                JsonSerializer.Deserialize<PvpArenaResultRecord>((string)row[0])!).Count(row =>
                row.Participants.All(player => player.Outcome == PvpArenaOutcome.Cancelled)) == 1,
            "无人加载时真实维护在30秒保存无奖取消并发布结果，不留卡死局");
        host.Send(57); Returned(); guest.Send(57); Returned();
        StartAnother(); LoadAll();
        uint[] scores = new uint[8]; scores[0] = 20; scores[3] = 100;
        byte[] timeout = Build(w => { foreach (uint score in scores) w.Write(score); });
        guest.Send(56, timeout); guest.Barrier(); host.Barrier();
        That(f.Query("SELECT count(*) FROM pvp_arena_match_results")[0][0] is 2L, "提前56不能结束240秒兼容大会时长");
        clock.Advance(TimeSpan.FromSeconds(240));
        host.Send(56, timeout); host.Barrier(); guest.Barrier();
        That(f.Query("SELECT count(*) FROM pvp_arena_match_results")[0][0] is 2L, "最小值胜方不能冒充最大值报告者");
        guest.Send(56, timeout);
        Result(f, host, 0, (host.Id, 0u), (guest.Id, 0u));
        Result(f, guest, 0, (host.Id, 0u), (guest.Id, 0u));
        guest.Send(56, timeout); guest.Barrier(); host.Barrier();
        host.Send(57); Returned(); guest.Send(57); Returned();
        StartAnother(); LoadAll();
        clock.Advance(TimeSpan.FromSeconds(254)); host.Barrier(); guest.Barrier();
        That(f.Query("SELECT count(*) FROM pvp_arena_match_results")[0][0] is 3L, "无报告宽限结束前保留原局");
        clock.Advance(TimeSpan.FromSeconds(1));
        Result(f, host, 255, (host.Id, 0u), (guest.Id, 0u));
        Result(f, guest, 255, (host.Id, 0u), (guest.Id, 0u));
        clock.Advance(TimeSpan.FromSeconds(29)); host.Barrier(); guest.Barrier();
        clock.Advance(TimeSpan.FromSeconds(1)); Returned();
        host.Send(57); guest.Send(57); host.Barrier(); guest.Barrier();
        host.Send(13);
        Seats(host, room, 2, (3, guest.Id, 2));
        State(guest, room, 1, 3, 1, 2);
        Seats(guest, room, 2, (3, guest.Id, 2));
        Ready(guest, 3, false);
        Room(host.Expect(0, 41).Body, room, 2, 3, 1, (3, guest.Id, 2));
        guest.Send(52, [3, 254]);
        Seats(guest, room, 2);
        State(host, room, 0, 255, 1, 2);
        // 客人进房后目录不再刷新；删除通知沿用他在大厅最后收到的目录字段。
        State(guest, room, 0, 255, 0, 1);
        host.Barrier(); guest.Barrier();
        That(before == f.Snapshot(), "正式房间、开战双屏障及死亡报告均不改角色资产和位置");
        That(f.Query("SELECT count(*) FROM pvp_arena_match_results")[0][0] is 4L,
            "结果后的离房和房主移交不重复记胜负");
        var ratingRows = f.Query("SELECT character_id,revision,state_json FROM character_pvp_ratings");
        That(ratingRows.Length == 2 && ratingRows.All(row => row[1] is 4L) &&
            JsonSerializer.Deserialize<PvpRatingState>((string)ratingRows.Single(row => Convert.ToInt64(row[0]) == host.Id)[2]) is
                { Experience: 900, Rating: 900, Played: 0 } &&
            JsonSerializer.Deserialize<PvpRatingState>((string)ratingRows.Single(row => Convert.ToInt64(row[0]) == guest.Id)[2]) is
                { Experience: 0, Rating: 900, Played: 0 } &&
            f.Query("SELECT count(*) FROM character_pvp_rating_claims WHERE receipt_json IS NOT NULL")[0][0] is 8L,
            "四局各消费双方来源一次，仅死亡胜局与有效56胜局各加450，取消和平局不加经验，不改匹配分");

        void StartAnother()
        {
            guest.Send(53, [1]); Ready(guest, 3, true); Ready(host, 3, true);
            host.Send(53, [1]);
            foreach (var peer in new[] { host, guest })
            {
                foreach (ushort id in new[] { host.Id, guest.Id })
                {
                    byte[] player = peer.Expect(0, 2).Body;
                    That(player[0] == 1 && U16(player, 22) == id, "返回后新局重新发布真实完整Player");
                }
                peer.Expect(0, 11, [0]); Ready(peer, 0, false); Ready(peer, 3, false);
                peer.Expect(0, 45, [1, 2]); State(peer, room, 2, 0, 1, 2);
            }
        }

        void LoadAll()
        {
            foreach (var loading in new[] { host, guest })
            {
                loading.Send(298);
                foreach (var peer in new[] { host, guest }) peer.Expect(0, 281, Build(w => w.Write(loading.Id)));
            }
            host.Send(299, Connections(true)); guest.Send(299, Connections(true));
            host.Expect(0, 282, []); guest.Expect(0, 282, []);
        }

        void Returned()
        {
            foreach (var peer in new[] { host, guest })
            {
                State(peer, room, 1, 0, 1, 2);
                Seats(peer, room, 2, (0, host.Id, 1), (3, guest.Id, 2));
                Ready(peer, 0, false); Ready(peer, 3, false);
            }
        }

        void Seed(AccountOwners.Lease owner, string name)
        {
            var seed = CharacterCreation.Prepare(name, resources.Characters.Get(0), resources.Towns.InitialReturn,
                resources.Towns.InitialReturn, resources.Learning.Get);
            repository.Create(owner, seed with { Level = 85, AcceptedStoryLevel = 85,
                InitialLearning = SkillPointLedger.Synchronize(0, 0, 85, seed.InitialLearning with
                { Experience = growth.GetLevelStartExperience(85) }, resources.Learning) });
        }
    }

    static void Actor(TcpPeer peer, ushort id)
    {
        byte[] body = peer.Expect(0, 2).Body;
        That(body.Length > 123 && body[0] == 0 && U16(body, 1) == 1 && U16(body, 121) == id,
            "房间引用前装载真实成员UID");
    }

    internal static void Result(TcpTestFixture fixture, TcpPeer peer, byte winner,
        params (ushort Id, uint Kills)[] fighters)
    {
        byte[] body = peer.Expect(0, 47).Body;
        var receipts = fixture.Query("SELECT receipt_json FROM character_pvp_rating_claims WHERE receipt_json IS NOT NULL")
            .Select(row => JsonSerializer.Deserialize<PvpRatingReceipt>((string)row[0])!).ToArray();
        var own = receipts.Where(row => row.CharacterId == peer.Id).MaxBy(row => row.Revision)!;
        That(body.Length == 26 + 23 * fighters.Length && body[0] == winner && U32(body, 1) == 0 &&
            body[5] == own.Change.Grade && U32(body, 6) == unchecked((uint)own.Change.ExperienceDelta) && body[10] == fighters.Length,
            "真实47完整长度、胜席、胜点绝对余额及本人的持久评分回执一致");
        for (int at = 0; at < fighters.Length; at++)
        {
            int start = 11 + at * 23;
            var fighter = fighters[at];
            That(U16(body, start) == fighter.Id && U32(body, start + 2) == fighter.Kills &&
                body[start + 6] == receipts.Where(row => row.CharacterId == fighter.Id).MaxBy(row => row.Revision)!.Change.Grade &&
                U32(body, start + 7) == 0 && U32(body, start + 11) == 0 && U32(body, start + 15) == 0 &&
                U32(body, start + 19) == fighter.Kills,
                "结果逐行引用真实角色与有效击杀，段位来自已落库回执，未消费字段保留兼容值");
        }
        Bytes(body.AsSpan(11 + 23 * fighters.Length).ToArray(),
            [0, 0, 255, 255, 0, 0, 0, 0, winner == 255 ? (byte)1 : (byte)0, 255, 0, 0, 0, 0, 0],
            "结果尾部按兼容合同保留原值，不伪造提示或追加奖励");
    }

    static void Room(byte[] body, ushort room, byte mode, byte leader, ushort map,
        params (byte Seat, ushort Id, byte State)[] members)
    {
        byte[] expected = Build(w =>
        {
            w.Write((ushort)1); w.Write(room); w.Write((byte)3); w.Write((byte)1);
            w.Write(leader); w.Write(map); w.Write(mode);
            for (byte seat = 0; seat < 8; seat++)
            {
                var match = members.Where(row => row.Seat == seat).ToArray();
                w.Write(match.Length == 0 ? (byte)255 : match[0].State);
                w.Write(match.Length == 0 ? ushort.MaxValue : match[0].Id);
            }
            w.Write((byte)0); w.Write(0u);
        });
        Bytes(body, expected, "正式普通房间按独立八席合同完整核对");
    }

    static void Seats(TcpPeer peer, ushort room, byte mode, params (byte Seat, ushort Id, byte State)[] members) =>
        peer.Expect(0, 43, Build(w =>
        {
            w.Write(room); w.Write(mode); w.Write((byte)8);
            for (byte seat = 0; seat < 8; seat++)
            {
                var match = members.Where(row => row.Seat == seat).ToArray();
                w.Write(seat); w.Write(match.Length == 0 ? (byte)255 : match[0].State);
                w.Write(match.Length == 0 ? ushort.MaxValue : match[0].Id);
            }
        }));

    static void Ready(TcpPeer peer, byte seat, bool value) => peer.Expect(0, 44, [seat, value ? (byte)1 : (byte)0]);
    static byte[] Death(ushort victim, ushort killer) => Build(w =>
    { w.Write(victim); w.Write(killer); w.Write((byte)0); w.Write((ushort)0); w.Write(0u); });
    static byte[] Connections(bool success) => Build(w =>
    {
        w.Write((byte)8);
        for (byte seat = 0; seat < 8; seat++) { w.Write(seat); w.Write(success ? (byte)1 : (byte)0); }
    });
    static void State(TcpPeer peer, ushort room, byte state, byte leader, ushort map, byte mode) =>
        peer.Expect(0, 42, Build(w => { w.Write(room); w.Write(state); w.Write(leader); w.Write(map); w.Write(mode); w.Write(0u); }));
}
