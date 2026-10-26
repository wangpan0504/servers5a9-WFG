using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunDazzlementAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new DazzlementDays();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "dungeon-dazzlement"), resources: resources, clock: clock);
        await fixture.StartAsync();
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 7601));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(area.Default.Bounds.SelectMany(row => row.Quests))
            .Concat(area.Default.DungeonGates.SelectMany(row => row.Quests)).Distinct().ToArray();
        var bounds = area.Map(done.ToHashSet()).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new DfoServer.GameWorld.Towns.Location(area.Town, area.Area,
            checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("DazzleTcp", resources.Characters.Get(0), position, position, resources.Learning.Get);
            created = new CharacterRepository(fixture.Database).Create(owner, candidate with
            {
                Level = 95, AcceptedStoryLevel = 95, CompletedQuests = done,
                InitialLearning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning)
            });
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot, prepared: true, resumeTown: true, room: false);
            SelectTowerMap(peer);
            That(peer.DazzlementBindings is [0], "首次选图1608明确清空旧角色遗留名单");
            byte[] map = Enter(peer);
            That(Attempts() == 1 && Bindings() == 1, "首次入场与绑定/日资格一次提交");
            for (int room = 0; room < 2; room++)
            {
                Load(peer);
                That(U32(map, 14) == (room == 0 ? 450001u : 450002u), "眩惑仍使用7601真实两房");
                // 末房Boss位于中间行；先清同房普通演员，避免清图后的残留39被当作新死亡。
                foreach (int actor in Enumerable.Range(0, map[18]).OrderBy(index => map[19 + index * 22 + 13] == 3))
                {
                    int at = 19 + actor * 22;
                    if (map[at + 21] != 0) continue;
                    peer.Send(39, Death(U16(map, at + 6), peer.Id, map[at + 12]));
                    var packets = Drain(peer);
                    That(packets.Any(p => p.Opcode == 38), "眩惑真实房间演员的合成击杀得到确认");
                }
                if (room == 0)
                {
                    byte[] move = new byte[100]; move[0] = 1;
                    peer.Send(45, move); peer.Expect(1, 45, [1]);
                    map = peer.Expect(0, 29).Body; Drain(peer);
                }
            }
            peer.Send(46, DungeonSettlementProtocolTests.Report(peer.Id, 90));
            peer.Expect(0, 34); peer.ReadCardMembershipState(); peer.Expect(0, 35); Drain(peer);
            That(fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0 AND dungeon_id=7601", created.Id) is [[1L]],
                "真实TCP清图及46结算仍完成，不以提前拒绝代替通关");
            ReturnTown(peer);
            SelectTowerMap(peer);
            That(peer.DazzlementBindings[0] == 1, "通关回城后名单包含当前角色，供原生绑定确认判定使用");
            Reject(peer);
            var alternate = Request(); BinaryPrimitives.WriteUInt32LittleEndian(alternate, 7602);
            peer.Send(16, alternate); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
            var practice = Request(); practice[8] = 1;
            peer.Send(16, practice); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
            That(Attempts() == 1, "换报眩惑其它DGN或练习不能绕过当前角色当日资格");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot, prepared: true, resumeTown: true, room: false);
            SelectTowerMap(peer); Reject(peer);
            That(Attempts() == 1 && Bindings() == 1, "重连仍拒绝当天重入，未另开实例或重复绑定");
            clock.Now = DateTimeOffset.Parse("2026-10-27T06:00:00+08:00");
            peer.ExpectUnusedDailyBoosterReset();
            SelectTowerMap(peer); Enter(peer); Load(peer); ReturnTown(peer);
            That(Attempts() == 2 && Bindings() == 1, "次日06:00允许原绑定角色进入，未消耗新绑定名额");
            SelectTowerMap(peer); Reject(peer);
            clock.Now = DateTimeOffset.Parse("2026-10-29T06:00:00+08:00");
            peer.ExpectUnusedDailyBoosterReset();
            SelectTowerMap(peer);
            That(peer.DazzlementBindings is [0], "周四边界的选图不回放旧周名单");
            Enter(peer); Load(peer); ReturnTown(peer);
            That(Attempts() == 3 && Bindings() == 1, "新周首次入场原子替换旧绑定周期");
        }

        long Attempts() => (long)fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", created.Id).Single()[0];
        long Bindings() => (long)fixture.Query("SELECT count(*) FROM account_dazzlement_tower_bindings").Single()[0];
        byte[] Request() { byte[] body = new byte[25]; BinaryPrimitives.WriteUInt32LittleEndian(body, 7601); body[9] = 255; body[10] = 255; return body; }
        byte[] Enter(TcpPeer peer)
        {
            peer.Send(16, Request()); peer.ExpectDazzlementBindings();
            That(U32(peer.Expect(0, 28).Body, 0) == 7601, "绑定提交后1608→28→29仍按原路线入场");
            var map = peer.Expect(0, 29).Body; Drain(peer); return map;
        }
        void Reject(TcpPeer peer) { peer.Send(16, Request()); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier(); }
        void ReturnTown(TcpPeer peer)
        {
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
            peer.Expect(0, 24); RefreshedQuests(peer); peer.Barrier();
        }
        List<WirePacket> Drain(TcpPeer peer)
        {
            peer.SendBarrier(); var packets = new List<WirePacket>();
            for (int i = 0; i < 100; i++)
            {
                var packet = peer.Read();
                if (packet is { Kind: 1, Opcode: 1347 }) { Bytes(packet.Body, [1], "眩惑命令屏障"); return packets; }
                That(packet.Kind == 0, "眩惑请求后的通知保持完整包序"); packets.Add(packet);
            }
            throw new InvalidOperationException("眩惑响应超过预算");
        }
    }

    sealed class DazzlementDays : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T12:00:00+08:00");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
