using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class PvpMatchingTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var f = new TcpTestFixture(source, Path.Combine(root, "pvp-matching"), resources: resources);
        var repository = new CharacterRepository(f.Database, resources.Quests);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        using (var owner = new AccountOwners().Acquire("matching-tcp-a", () => { })) Seed(owner, "MatchingTCPA");
        using (var owner = new AccountOwners().Acquire("matching-tcp-b", () => { })) Seed(owner, "MatchingTCPB");
        await f.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(f);
        byte pvp = ChannelTcpTests.PvpChannelNumber(f);
        using var a = Open(f, "matching-tcp-a", gamePort: endpoints[47], channelNumber: 47);
        That(a.LoginEndpoint[3] == 8, "真实无双频道LOGIN安装类型8，不能以类型0或传统24代替");
        a.Send(375, PvpRecordTcpChecks.Request(35));
        a.Expect(1, 375, [0]);
        a.Send(520, Queue()); a.Expect(1, 520, [0, 0x17, 0]);
        a.Send(521, [1, 0, 0]); a.Expect(1, 521, [0, 0x17, 0]);
        a.Select(prepared: true); a.Barrier();
        a.Send(195); a.Expect(1, 195, Build(w =>
        {
            w.Write((byte)1); w.Write(0u); w.Write((byte)4);
            foreach (uint number in new uint[] { pvp, 47, 51, 55 })
            { w.Write(number); w.Write(0u); w.Write(1u); }
        }));
        using var originalB = Open(f, "matching-tcp-b", gamePort: endpoints[47], channelNumber: 47);
        var b = originalB;
        b.Select(prepared: true); PvpArenaTcpTests.TownPeer(b, a.Id); b.Barrier();
        PvpArenaTcpTests.TownPeer(a, b.Id); a.Barrier();
        string assets = f.Snapshot();
        PvpRecordTcpChecks.Score(f, a, a.Id, true);
        PvpRecordTcpChecks.Score(f, a, b.Id, false);
        foreach (byte view in new byte[] { 2, 3, 4, 5 }) PvpRecordTcpChecks.Score(f, a, a.Id, true, view: view);
        PvpRecordTcpChecks.Reject(a, [.. PvpRecordTcpChecks.Request(a.Id), 0]);
        PvpRecordTcpChecks.Reject(a, PvpRecordTcpChecks.Request(a.Id)[..7]);
        PvpRecordTcpChecks.Reject(a, PvpRecordTcpChecks.Request(b.Id));
        PvpRecordTcpChecks.Reject(a, PvpRecordTcpChecks.Request(a.Id, self: 0));
        PvpRecordTcpChecks.Reject(a, PvpRecordTcpChecks.Request(a.Id, view: 7));
        PvpRecordTcpChecks.Reject(a, PvpRecordTcpChecks.Request(a.Id, member: 3));
        PvpRecordTcpChecks.Reject(a, PvpRecordTcpChecks.Request(a.Id, member: 0));
        a.Send(520, Queue(gameType: 7)); a.Expect(1, 520, [0, 0x17, 0]);
        a.Send(520, Queue()); a.Expect(1, 520, [1, 1, 0, 0, 0]);
        a.Send(520, Queue()); a.Expect(1, 520, [1, 1, 0, 0, 0]);
        a.Send(520, Queue(option: 0)); a.Expect(1, 520, [0, 0x17, 0]);
        a.Send(521, [1, 0, 0]); a.Expect(1, 521, [1]);
        a.Send(521, [1, 0, 0]); a.Expect(1, 521, [1]);
        a.Send(520, Queue(excluded: 1)); a.Expect(1, 520, [1, 1, 0, 0, 1]);
        a.Send(521, [1, 0, 0]); a.Expect(1, 521, [1]);
        a.Send(520, Queue()); a.Expect(1, 520, [1, 1, 0, 0, 0]);
        b.Send(520, Queue(option: 0)); b.Expect(1, 520, [1, 1, 0, 0, 0]);
        a.Barrier(); b.Barrier();
        b.Send(521, [1, 0, 0]); b.Expect(1, 521, [1]);
        b.Send(520, Queue()); b.Expect(1, 520, [1, 1, 0, 0, 0]);
        var first = Paired(a); var other = Paired(b);
        That(first == other, "真实双连接使用同一房间、地图和不可复用匹配标识");
        b.Send(521, [1, 0, 0]); b.Expect(1, 521, [1]);
        foreach (var peer in new[] { a, b })
        {
            peer.Expect(0, 506, [3]);
            PvpArenaTcpTests.Left(peer, first.Room, 1, peer == a ? (byte)0 : (byte)1);
        }
        a.Barrier(); b.Barrier();
        a.Send(520, Queue()); a.Expect(1, 520, [1, 1, 0, 0, 0]);
        b.Send(520, Queue()); b.Expect(1, 520, [1, 1, 0, 0, 0]);
        var interrupted = Paired(a);
        That(interrupted == Paired(b) && interrupted.Token != first.Token, "取消后重排建立全新匹配，不复用旧预约");
        b.Dispose();
        a.Expect(0, 506, [3]);
        PvpArenaTcpTests.Left(a, interrupted.Room, 1, 0);
        a.Expect(0, 6, [(byte)b.Id, (byte)(b.Id >> 8)]);
        a.Barrier();
        using var replacementB = Open(f, "matching-tcp-b", gamePort: endpoints[47], channelNumber: 47);
        replacementB.Select(prepared: true); PvpArenaTcpTests.TownPeer(replacementB, a.Id); replacementB.Barrier();
        b = replacementB;
        PvpArenaTcpTests.TownPeer(a, b.Id); a.Barrier();
        a.Send(520, Queue()); a.Expect(1, 520, [1, 1, 0, 0, 0]);
        b.Send(520, Queue()); b.Expect(1, 520, [1, 1, 0, 0, 0]);
        var second = Paired(a);
        That(second == Paired(b) && second.Token != interrupted.Token, "断线重连后使用新预约和原角色真实评分");
        a.Send(570, Build(w => w.Write(interrupted.Token)));
        a.Send(570, Build(w => w.Write(first.Token)));
        a.Send(570, Build(w => w.Write(second.Token)));
        a.Send(570, Build(w => w.Write(second.Token)));
        a.Barrier(); b.Barrier();
        b.Send(570, Build(w => w.Write(second.Token)));
        foreach (var peer in new[] { a, b })
        {
            peer.Expect(0, 506, [2]);
            foreach (ushort id in new[] { a.Id, b.Id })
            {
                var player = peer.Expect(0, 2).Body;
                That(player.Length > 36 && player[0] == 1 && U16(player, 22) == id,
                    "双方570屏障之后才发送真实完整Player");
            }
            peer.Expect(0, 11, [0]);
            peer.Expect(0, 44, [0, 0]); peer.Expect(0, 44, [1, 0]);
            peer.Expect(0, 45, [checked((byte)second.Map), 1]);
            peer.Expect(0, 42, Build(w =>
            { w.Write(second.Room); w.Write((byte)2); w.Write((byte)0); w.Write(second.Map); w.Write((byte)1); w.Write(0u); }));
        }
        b.Send(570, Build(w => w.Write(second.Token))); b.Barrier(); a.Barrier();
        a.Send(521, [1, 0, 0]); a.Expect(1, 521, [0, 0x17, 0]);
        a.Send(298);
        a.Expect(0, 281, Build(w => w.Write(a.Id))); b.Expect(0, 281, Build(w => w.Write(a.Id)));
        a.Send(299, Connections()); b.Send(299, Connections()); a.Barrier(); b.Barrier();
        b.Send(298);
        foreach (var peer in new[] { a, b })
        {
            peer.Expect(0, 281, Build(w => w.Write(b.Id))); peer.Expect(0, 282, []);
        }
        b.Send(55, Build(w =>
        { w.Write(b.Id); w.Write(a.Id); w.Write((byte)0); w.Write((ushort)0); w.Write(0u); }));
        a.Expect(0, 46, [1, 0]); b.Expect(0, 46, [1, 0]);
        PvpArenaTcpTests.Result(f, a, 0, (a.Id, 1u), (b.Id, 0u));
        PvpArenaTcpTests.Result(f, b, 0, (a.Id, 1u), (b.Id, 0u));
        b.Barrier(); a.Barrier();
        var until = DateTimeOffset.UtcNow.AddSeconds(5);
        while (f.Query("SELECT count(*) FROM character_pvp_rating_claims WHERE receipt_json IS NOT NULL")[0][0] is not 2L &&
            DateTimeOffset.UtcNow < until) await Task.Delay(20);
        var rows = f.Query("SELECT character_id,revision,state_json FROM character_pvp_ratings");
        var result = JsonSerializer.Deserialize<PvpArenaResultRecord>((string)f.Query("SELECT result_json FROM pvp_arena_match_results").Single()[0])!;
        That(result.RatingMode == PvpRatingMode.Matching && result.Participants.Length == 2 &&
            rows.Length == 2 && rows.All(row => row[1] is 1L) &&
            JsonSerializer.Deserialize<PvpRatingState>((string)rows.Single(row => Convert.ToInt64(row[0]) == a.Id)[2]) is
                { Played: 1, Wins: 1, Losses: 0 } &&
            JsonSerializer.Deserialize<PvpRatingState>((string)rows.Single(row => Convert.ToInt64(row[0]) == b.Id)[2]) is
                { Played: 1, Wins: 0, Losses: 1 } && assets == f.Snapshot(),
            "匹配TCP战斗使用独立评分来源，双方各入账一次且不改角色资产");
        PvpRecordTcpChecks.Score(f, a, a.Id, true, wins: 1, recentWins: 1, streak: 1, highest: 1);
        PvpRecordTcpChecks.Score(f, b, b.Id, true, losses: 1, recentLosses: 1);
        PvpRecordTcpChecks.Score(f, b, a.Id, false, wins: 1, recentWins: 1, streak: 1, highest: 1);
        PvpRecordTcpChecks.Score(f, a, a.Id, true, view: 5);
        a.Send(57);
        foreach (var peer in new[] { a, b })
        {
            peer.Expect(0, 42, Build(w =>
            { w.Write(second.Room); w.Write((byte)1); w.Write((byte)0); w.Write(second.Map); w.Write((byte)1); w.Write(0u); }));
            var seats = peer.Expect(0, 43).Body;
            That(U16(seats, 6) == a.Id && U16(seats, 10) == b.Id, "单方返回仍保留原匹配双成员，不提前清房");
            peer.Expect(0, 44, [0, 0]); peer.Expect(0, 44, [1, 0]);
        }
        a.Send(57); a.Barrier(); b.Barrier();
        b.Send(57);
        foreach (var peer in new[] { a, b })
        {
            PvpArenaTcpTests.Left(peer, second.Room, 1, peer == a ? (byte)0 : (byte)1);
            peer.Send(57); peer.Barrier();
            peer.Send(520, Queue()); peer.Expect(1, 520, [1, 1, 0, 0, 0]);
            peer.Send(521, [1, 0, 0]); peer.Expect(1, 521, [1]);
        }
        That(f.Query("SELECT count(*) FROM character_pvp_rating_claims WHERE receipt_json IS NOT NULL")[0][0] is 2L &&
            assets == f.Snapshot(), "重复返回及再次排队不重复消费原局评分或改变角色资产");

        (ushort Room, ushort Map, uint Token) Paired(TcpPeer peer)
        {
            foreach (ushort id in new[] { a.Id, b.Id })
            {
                var actor = peer.Expect(0, 2).Body;
                That(actor[0] == 0 && U16(actor, 121) == id, "匹配房间先发布双方真实Actor");
            }
            byte[] room = peer.Expect(0, 41).Body;
            ushort idRoom = U16(room, 2), map = U16(room, 7);
            peer.Expect(1, 51, [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            byte[] players = peer.Expect(0, 504).Body;
            uint token = BinaryPrimitives.ReadUInt32LittleEndian(players.AsSpan(1));
            Bytes(players, Build(w =>
            {
                w.Write((byte)2); w.Write(token); w.Write((byte)2);
                foreach (var row in new[] { (a.Id, (byte)0), (b.Id, (byte)1) })
                { w.Write(row.Id); w.Write(row.Item2); w.Write(900u); w.Write((byte)0); w.Write(0u); w.Write((byte)0); }
            }), "504逐字节核对原预约、真实UID、双方位置及实际初始显示分，徽章0不绘制");
            That(U16(room, 0) == 1 && room[4] == 1 && room[5] == 1 && room[6] == 0 && room[9] == 1 &&
                U16(room, 11) == a.Id && U16(room, 14) == b.Id && token != 0,
                "匹配初始化为真实私有个人房间，不依赖手动建房回执");
            peer.Expect(0, 505, []);
            return (idRoom, map, token);
        }
        void Seed(AccountOwners.Lease owner, string name)
        {
            var seed = CharacterCreation.Prepare(name, resources.Characters.Get(0), resources.Towns.InitialReturn,
                resources.Towns.InitialReturn, resources.Learning.Get);
            repository.Create(owner, seed with { Level = 85, GrowType = 1, AcceptedStoryLevel = 85,
                InitialLearning = SkillPointLedger.Synchronize(0, 1, 85, seed.InitialLearning with
                { Experience = growth.GetLevelStartExperience(85) }, resources.Learning) });
        }
    }

    static byte[] Queue(byte option = 1, ushort gameType = 0, byte excluded = 0) => Build(w =>
    {
        w.Write((byte)1); w.Write(gameType); w.Write(option); w.Write(excluded);
        for (int at = 0; at < 8; at++) w.Write((byte)255);
        w.Write((byte)0); w.Write(0u);
    });
    static byte[] Connections() => Build(w =>
    { w.Write((byte)8); for (byte seat = 0; seat < 8; seat++) { w.Write(seat); w.Write((byte)1); } });
}
