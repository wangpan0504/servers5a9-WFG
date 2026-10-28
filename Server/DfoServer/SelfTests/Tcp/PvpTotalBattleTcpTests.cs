using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Pvp;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class PvpTotalBattleTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        // 基准固定为当日 UTC 零点：本用例后面推进两小时仍留在同一 UTC+2 记账日内，不会撞上 06:00(Asia/Shanghai) 的每日重置而多出 541。
        var clock = new Inventory.DailyBoosterTestClock(new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero));
        await using var f = new TcpTestFixture(source, Path.Combine(root, "pvp-total-battle"), resources: resources, clock: clock);
        var repository = new CharacterRepository(f.Database, resources.Quests);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var teams = new Character[2][];
        for (int side = 0; side < 2; side++)
        {
            using var owner = new AccountOwners().Acquire("total-battle-" + side, () => { });
            teams[side] = Enumerable.Range(1, 3).Select(grow =>
            {
                var seed = CharacterCreation.Prepare($"TotalB{side}{grow}", resources.Characters.Get(0),
                    resources.Towns.InitialReturn, resources.Towns.InitialReturn, resources.Learning.Get);
                return repository.Create(owner, seed with { Level = 85, GrowType = (byte)grow, AcceptedStoryLevel = 85,
                    InitialLearning = SkillPointLedger.Synchronize(0, (byte)grow, 85, seed.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(85) }, resources.Learning) });
            }).ToArray();
        }
        await f.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(f);
        using var a = Open(f, "total-battle-0", gamePort: endpoints[47], channelNumber: 47);
        a.Select(prepared: true); a.Barrier();
        using var b = Open(f, "total-battle-1", gamePort: endpoints[47], channelNumber: 47);
        b.Select(prepared: true); b.Barrier();
        a.Barrier(packet => That(packet is { Kind: 0, Opcode: 2 } && U16(packet.Body, 121) == b.Id, "总力前仍走真实城镇同屏"));
        var peers = new[] { a, b };
        for (int side = 0; side < 2; side++)
        {
            peers[side].Send(873, Build(w => { Text(w, "总力" + side); w.Write(new byte[] { 2, 0, 1 }); }));
            peers[side].Expect(1, 873, [1, 2, 0, 1]); peers[side].Barrier();
            teams[side] = [teams[side][2], teams[side][0], teams[side][1]];
        }
        string assets = f.Snapshot();
        // 相同偏好、不同模式也不能配成一局；错误模式取消不能消费总力票据。
        a.Send(520, Queue(7)); a.Expect(1, 520, [1, 1, 7, 0, 0]);
        a.Send(521, [1, 0, 0]); a.Expect(1, 521, [0, 2, 0]);
        b.Send(520, Queue(0)); b.Expect(1, 520, [1, 1, 0, 0, 0]);
        a.Barrier(); b.Barrier();
        b.Send(521, [1, 0, 0]); b.Expect(1, 521, [1]);
        b.Send(520, Queue(7)); b.Expect(1, 520, [1, 1, 7, 0, 0]);
        var paired = Paired(a); That(paired == Paired(b), "总力双连接共享同一真实预约");
        Begin();
        a.Send(876, [3]); a.Send(876, [0]); a.Send(876, [1]); a.Barrier(); b.Barrier();
        b.Send(876, [0]); Round(0, 0);
        for (byte defeated = 0; defeated < 3; defeated++)
        {
            b.Send(55, Build(w => { w.Write(b.Id); w.Write(a.Id); w.Write((byte)0); w.Write((ushort)0); w.Write(0u); }));
            foreach (var peer in peers) peer.Expect(0, 46, [1, 0]);
            if (defeated == 2) break;
            foreach (var peer in peers) peer.Expect(0, 807, []);
            b.Send(55, Build(w => { w.Write(b.Id); w.Write(a.Id); w.Write((byte)0); w.Write((ushort)0); w.Write(0u); }));
            b.Send(876, [defeated]); a.Send(876, [2]); a.Barrier(); b.Barrier();
            b.Send(876, [checked((byte)(defeated + 1))]); Round(0, checked((byte)(defeated + 1)));
        }
        foreach (var peer in peers) PvpArenaTcpTests.Result(f, peer, 0, (a.Id, 3u), (b.Id, 0u));
        a.Barrier(); b.Barrier();
        var until = DateTimeOffset.UtcNow.AddSeconds(5);
        while (f.Query("SELECT count(*) FROM account_pvp_total_rating_claims WHERE receipt_json IS NOT NULL")[0][0] is not 2L &&
            DateTimeOffset.UtcNow < until) await Task.Delay(20);
        var result = JsonSerializer.Deserialize<PvpArenaResultRecord>((string)f.Query("SELECT result_json FROM pvp_arena_match_results").Single()[0])!;
        That(result.RatingMode == PvpRatingMode.Total && result.Participants.All(row => row.TotalTeam is { Complete: true }) &&
            f.Query("SELECT count(*) FROM account_pvp_total_rating_claims WHERE receipt_json IS NOT NULL")[0][0] is 2L &&
            f.Query("SELECT count(*) FROM character_pvp_rating_claims WHERE receipt_json IS NOT NULL")[0][0] is 6L &&
            f.Query("SELECT state_json FROM character_pvp_ratings").All(row => JsonSerializer.Deserialize<PvpRatingState>((string)row[0]) is { Played: 0 }) &&
            f.Snapshot() == assets, "总力整局只入账两支队伍及六份经验，不修改个人排位战绩或角色资产");
        a.Send(57);
        foreach (var peer in peers)
        { State(peer, 1); peer.Expect(0, 43); peer.Expect(0, 44, [0, 0]); peer.Expect(0, 44, [1, 0]); }
        b.Send(57);
        foreach (var peer in peers)
        {
            var cleared = peer.Expect(0, 43).Body;
            That(Enumerable.Range(0, 8).All(at => U16(cleared, 6 + at * 4) != peer.Id), "总力双方返回后清原临时房间");
            peer.Send(520, Queue(7)); peer.Expect(1, 520, [1, 1, 7, 0, 0]);
            peer.Send(521, [1, 7, 0]); peer.Expect(1, 521, [1]); peer.Barrier();
        }
        foreach (var peer in peers) { peer.Send(520, Queue(7)); peer.Expect(1, 520, [1, 1, 7, 0, 0]); }
        paired = Paired(a); That(paired == Paired(b), "总力新局重新使用真实队伍评分"); Begin();
        clock.Advance(TimeSpan.FromSeconds(45)); Round(0, 0);
        clock.Advance(TimeSpan.FromHours(2));
        foreach (var peer in peers) peer.Expect(0, 807, []);
        clock.Advance(TimeSpan.FromSeconds(45)); Round(1, 1);
        b.Dispose();
        State(a, 1); a.Expect(0, 43); a.Expect(0, 44, [0, 0]);
        PvpArenaTcpTests.Result(f, a, 0, (a.Id, 0u)); a.Barrier();
        using (var replacement = Open(f, "total-battle-1", gamePort: endpoints[47], channelNumber: 47))
        {
            replacement.Select(prepared: true, pvpTotalTeam: Build(w => { Text(w, "总力1"); w.Write(new byte[] { 2, 0, 1 }); }));
            replacement.Barrier();
        }
        That(f.Query("SELECT count(*) FROM account_pvp_total_rating_claims WHERE receipt_json IS NOT NULL")[0][0] is 4L &&
            f.Query("SELECT count(*) FROM character_pvp_rating_claims WHERE receipt_json IS NOT NULL")[0][0] is 12L &&
            f.Query("SELECT result_json FROM pvp_arena_match_results").Select(row => JsonSerializer.Deserialize<PvpArenaResultRecord>((string)row[0])!)
                .Count(row => row.Forfeit) == 1, "自动选人、回合超时及断线弃权后，新owner完整恢复原局且不重复入账");

        void Begin()
        {
            foreach (var peer in peers) peer.Send(570, Build(w => w.Write(paired.Token)));
            foreach (var peer in peers)
            {
                peer.Expect(0, 506, [2]);
                using (var r = Reader(peer.Expect(0, 805).Body))
                {
                    That(r.ReadByte() == 2, "805包含两支队伍");
                    for (int side = 0; side < 2; side++)
                    { That(r.ReadUInt32() == peers[side].Id, "805按真实owner绑定"); Summary(r, side); }
                    That(r.BaseStream.Position == r.BaseStream.Length, "805无错位尾部");
                }
                Actors(peer.Expect(0, 803).Body);
                peer.Expect(0, 11, [0]); peer.Expect(0, 44, [0, 0]); peer.Expect(0, 44, [1, 0]);
                peer.Expect(0, 45, [checked((byte)paired.Map), 8]);
                State(peer, 2);
            }
            a.Send(298);
            foreach (var peer in peers) peer.Expect(0, 281, Build(w => w.Write(a.Id)));
            b.Send(298);
            foreach (var peer in peers)
            { peer.Expect(0, 281, Build(w => w.Write(b.Id))); peer.Expect(0, 807, []); }
            }

        (ushort Room, ushort Map, uint Token) Paired(TcpPeer peer)
        {
            foreach (var other in peers) That(U16(peer.Expect(0, 2).Body, 121) == other.Id, "总力房间先安装双方Actor");
            var room = peer.Expect(0, 41).Body;
            That(room[9] == 8, "总力房间模式8");
            peer.Expect(1, 51, [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            using var r = Reader(peer.Expect(0, 504).Body);
            That(r.ReadByte() == 8, "总力504使用模式8"); uint token = r.ReadUInt32();
            That(token != 0 && r.ReadByte() == 2, "总力504包含两位真实owner");
            for (int side = 0; side < 2; side++)
            {
                That(r.ReadUInt16() == peers[side].Id && r.ReadByte() == side && r.ReadUInt32() == Rating(side) &&
                    r.ReadByte() == 0 && r.ReadUInt32() == 0 && r.ReadByte() == 0, "504基础行保持13字节");
                Summary(r, side);
            }
            That(r.BaseStream.Position == r.BaseStream.Length, "504含三角色摘要且严格消费完毕");
            peer.Expect(0, 505, []); return (U16(room, 2), U16(room, 7), token);
        }
        void Summary(BinaryReader r, int side)
        {
            That(Text(r) == "总力" + side, "队伍名来自原持久编队");
            foreach (var actor in teams[side])
                That(Text(r) == actor.Name && r.ReadByte() == actor.Job && r.ReadByte() == actor.GrowType && r.ReadByte() == 0,
                    "摘要严格按非自然阵容顺序保留姓名职业与转职");
            That(r.ReadUInt32() == Rating(side), "摘要使用独立队伍实际分");
        }
        uint Rating(int side) => f.Query("SELECT state_json FROM account_pvp_total_ratings WHERE account=$p0", "total-battle-" + side)
            is [[string json]] ? checked((uint)JsonSerializer.Deserialize<PvpRatingState>(json)!.Rating) : 900u;
        void Actors(byte[] body)
        {
            using var r = Reader(body); That(r.ReadByte() == 2, "803完整发布双方六角色");
            for (int side = 0; side < 2; side++)
            {
                That(r.ReadUInt16() == peers[side].Id && r.ReadByte() == 3, "803 owner为u16并带三角色");
                for (byte index = 0; index < 3; index++)
                {
                    var actor = teams[side][index];
                    That(r.ReadByte() == index && Text(r) == actor.Name && r.ReadByte() == 85 &&
                        r.ReadByte() == actor.Job && r.ReadByte() == actor.GrowType, "803逐角色独立解析身份与真实等级");
                    Skip(7); That(r.ReadInt32() == 89, "803属性块与本版Player共用89字节"); Skip(89);
                    That(r.ReadByte() == actor.EquipmentSlotFlags, "803保留真实装备槽标志"); Skip(5);
                    var worn = actor.Inventory.Items.Where(item => item.Position.List == InventoryListType.Equipment && item.Position.Index != 30)
                        .OrderBy(item => item.Position.Index).ToArray();
                    That(r.ReadByte() == worn.Length, "803外观数量来自实际穿戴");
                    foreach (var item in worn)
                    {
                        That(r.ReadByte() == item.Position.Index && r.ReadInt32() == item.Core.ItemId && r.ReadInt32() == 4,
                            "803外观保留真实物品而非代表角色复制"); Skip(4 + 3 + 4 + 4 + 1);
                    }
                    Skip(8); That(r.ReadByte() == worn.Length, "803完整装备和外观共用真实穿戴");
                    foreach (var item in worn)
                    {
                        That(item.Avatar is null && item.Creature is null && r.ReadByte() == item.Position.Index &&
                            r.ReadInt32() == item.Core.ItemId, "803普通装备独立按本版顺序解析");
                        Skip(4 + 1 + 2 + 2 + 4 + 4 + 1 + 3);
                        int chronicles = r.ReadByte(); Skip(chronicles * 8 + 4);
                        int sockets = r.ReadByte(); Skip(sockets * 4 + 2);
                        That(r.ReadByte() == 0, "基础夹具没有随机属性变长支路"); Skip(10);
                        Raw(); Raw(); Skip(1); Raw(); Raw(); Raw();
                    }
                    Skip(4); int runes = r.ReadByte(); Skip(runes * 4 + 2);
                    int count = r.ReadByte(); byte active = r.ReadByte();
                    var tree = actor.Learning.Trees.Single(row => row.Context == active);
                    That(count == tree.Skills.Count, "803携带该角色真实技能树");
                    foreach (var skill in tree.Skills.OrderBy(row => row.Slot))
                        That(r.ReadByte() == skill.Slot && r.ReadUInt16() == skill.Id && r.ReadByte() == skill.Level, "803技能槽ID等级逐项匹配原角色");
                    That(r.ReadUInt32() == 0, "803辅助列表为空"); Skip(20 + 8 + 8);
                }
            }
            That(r.BaseStream.Position == body.Length, "803六角色独立解析至末字节，无技能换装错位");
            void Skip(int bytes) => That(r.ReadBytes(bytes).Length == bytes, "803字段没有越界");
            void Raw() { int length = r.ReadInt32(); That(length is >= 0 and <= 32, "803装备扩展有界"); Skip(length); }
        }
        void Round(byte first, byte second)
        {
            foreach (var peer in peers) peer.Expect(0, 806, Build(w =>
            { w.Write((uint)a.Id); w.Write(first); w.Write((uint)b.Id); w.Write(second); }));
        }
        void State(TcpPeer peer, byte state) => peer.Expect(0, 42, Build(w =>
        { w.Write(paired.Room); w.Write(state); w.Write((byte)0); w.Write(paired.Map); w.Write((byte)8); w.Write(0u); }));
    }

    static BinaryReader Reader(byte[] body) => new(new MemoryStream(body));
    static string Text(BinaryReader reader) => CharacterName.Encoding.GetString(reader.ReadBytes(checked((int)reader.ReadUInt32())));
    static void Text(BinaryWriter writer, string value)
    { var bytes = CharacterName.Encoding.GetBytes(value); writer.Write((uint)bytes.Length); writer.Write(bytes); }
    static byte[] Queue(ushort type) => Build(w =>
    { w.Write((byte)1); w.Write(type); w.Write((byte)1); w.Write((byte)0); for (int at = 0; at < 8; at++) w.Write((byte)255); w.Write((byte)0); w.Write(0u); });
}
