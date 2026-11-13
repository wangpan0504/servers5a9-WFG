using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PreyTeamProtocolTests
{
    internal static void RunCore()
    {
        var request = RaidRequests.Manager([2, 0, 0, 0, 0x34, 0x12, 0, 0, 11, 0, 0, 0]);
        That(request == new RaidManagerRequest(2, 0x1234, 11), "普雷669保持三个u32，11表示第1队第1格");
        var marker = RaidRequests.PreyMarker([1, 0, 0, 0, 0, 0, 0, 0, 0x31, 1, 0, 0]);
        That(marker == new PreyMarkerRequest(1, 0, 305), "普雷1642读取操作、分组和区域symbol三个u32");
        Bytes(RaidCombatPackets.PreyMarker(0, 305), [4,0,0,0, 1,0,0,0, 0,0,0,0, 0x31,1,0,0], "普雷1595 op4保留selector与三列u32");
        Bytes(RaidCombatPackets.PreyMarker(2, 0), [4,0,0,0, 2,0,0,0, 2,0,0,0, 0,0,0,0], "普雷1595 op4清除为action2及目标0");
        Reject<InvalidDataException>(() => RaidRequests.PreyMarker(new byte[11]), "普雷1642截断拒绝");
        Reject<InvalidDataException>(() => RaidRequests.PreyMarker([1,0,0,0, 3,0,0,0, 0x31,1,0,0]), "普雷1642第四组拒绝");
        Reject<InvalidDataException>(() => RaidRequests.PreyMarker([2,0,0,0, 0,0,0,0, 0x31,1,0,0]), "普雷1642清除只接受target0");
        That(RaidRequests.PreyMonsterHp([37, 0, 0, 0]) == 37 && RaidRequests.PreyBossHpAlarm([25, 0, 0, 0]) == 25,
            "普雷1613和1614各严格消费一个4B原值");
        That(RaidRequests.PreyMonsterHp([0xff, 0xff, 0xff, 0xff]) == -1,
            "普雷HP解析保留原位型，范围由当前战斗业务校验");
        byte[] preciseHp = [0xd4, 0, 0, 0, 0xe1, 0x10, 0, 0, 0];
        That(RaidRequests.PreyBossHpUnits(preciseHp) == 4321 &&
            RaidRequests.PreyBossHpUnits([0xd4, 0, 0, 0, 0x10, 0x27, 0, 0, 0]) == 10000,
            "662精确HP保留symbol212和万分比值，不降为百分比");
        foreach (var invalid in new byte[][]
        {
            preciseHp[..8], [.. preciseHp, 0], [0xd5, 0, 0, 0, 0xe1, 0x10, 0, 0, 0],
            [0xd4, 0, 0, 0, 0xe1, 0x10, 0, 0, 1], [0xd4, 0, 0, 0, 0xe1, 0x10, 0, 0, 2]
        }) That(RaidRequests.PreyBossHpUnits(invalid) is null, "普雷HP只接已证9B symbol212赋值，不借其他符号或加减操作");
        foreach (int length in new[] { 0, 1, 3, 5, 8 })
        {
            Reject<InvalidDataException>(() => RaidRequests.PreyMonsterHp(new byte[length]), "1613截断和附加字段拒绝");
            Reject<InvalidDataException>(() => RaidRequests.PreyBossHpAlarm(new byte[length]), "1614截断和附加字段拒绝");
        }
        Bytes(RaidCombatPackets.PreyStatus(1), [1, 0, 0, 0], "1595选择器按03C85290读取u32");
        Bytes(RaidCombatPackets.PreyStatus(5, 4988), [5, 0, 0, 0, 0x7c, 0x13, 0, 0], "1595清图为两个u32");
        Reject<ArgumentOutOfRangeException>(() => RaidCombatPackets.PreyStatus(5), "1595清图不能省略副本号");
        var fixedOffer = new RaidRewardOffer(1, 0, [new(1234, 2)], [])
            { PreyDisplay = [new(0, 1234, 2, 0, 0, 0)] };
        var squadOffer = new RaidRewardOffer(0, 0, [new(1234, 4), new(4321, 3)], [])
            { PreyDisplay = [new(0, 1234, 4, 0, 0, 0), new(0, 4321, 3, 0, 0, 0)] };
        Bytes(PreyRewardPackets.Personal(0x1234, [fixedOffer, squadOffer]),
            [1,1,0x34,0x12,0,2, 0,0xd2,4,0,0,6,0,0,0,0,0,0,0,0,0,0,
                0,0xe1,0x10,0,0,3,0,0,0,0,0,0,0,0,0,0],
            "普雷个人面板同itemId的两份已提交奖励2+4显示6，不以连续0/1覆盖数量");
        That(fixedOffer.Grants[0].Count == 2 && squadOffer.Grants[0].Count == 4,
            "个人显示合并不改冻结资产来源和原数量");
        using (var stream = new MemoryStream())
        {
            using var writer = new BinaryWriter(stream);
            RaidPackets.WriteMember(writer, 0x1234, "X"u8.ToArray(), 1, 7, 11);
            byte[] expected = [0x34, 0x12, 0, 1, 0, 0, 0, (byte)'X', 0, 0, 1, 0,
                0, 0, 0, 0, 0, 0, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 11,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
            Bytes(stream.ToArray(), expected, "成员块保留+2D分队与+64独立普雷位置，其余字段不移动");
            var located = expected.ToArray();
            located[35] = 0x31; located[36] = 1;
            var information = RaidPackets.Information(1, 0x02130001, [], 3, 0x1234, "X"u8.ToArray(),
                [(0x1234, "X"u8.ToArray(), 1)], clearCounts: new Dictionary<ushort, byte> { [0x1234] = 7 },
                preyPositions: new Dictionary<ushort, byte> { [0x1234] = 11 },
                preyLocations: new Dictionary<ushort, ushort> { [0x1234] = 305 });
            Bytes(information, [1, 1, 1, 0, 0x13, 2, 1, .. located],
                "1361成员地点保留u16值305且后续+68字段及完整尾部不移位");
            var retries = new Dictionary<(ushort Character, int Dungeon), uint>
                { [(0x1234, 5003)] = 0x12345678, [(0x1235, 5003)] = 0x87654321 };
            stream.SetLength(0); stream.Position = 0;
            RaidPackets.WriteMember(writer, 0x1234, "X"u8.ToArray(), 1, 7, 11, preyRetryDeadlines: retries);
            byte[] retryExpected = [.. expected[..^13], 1, 0, 0, 0, 0x8b, 0x13, 0, 0, 0x78, 0x56, 0x34, 0x12, .. expected[^9..]];
            Bytes(stream.ToArray(), retryExpected,
                "成员重试表仅写本人5003和完整u32绝对期限，+68与原9B尾保持");
            retryExpected[35] = 0x31; retryExpected[36] = 1;
            Bytes(RaidPackets.Information(1, 0x02130001, [], 3, 0x1234, "X"u8.ToArray(),
                [(0x1234, "X"u8.ToArray(), 1)], clearCounts: new Dictionary<ushort, byte> { [0x1234] = 7 },
                preyPositions: new Dictionary<ushort, byte> { [0x1234] = 11 },
                preyLocations: new Dictionary<ushort, ushort> { [0x1234] = 305 }, preyRetryDeadlines: retries),
                [1, 1, 1, 0, 0x13, 2, 1, .. retryExpected], "1361完整成员信息保留地点及本体重试期限");
        }

        string root = Path.Combine(Path.GetTempPath(), "s5a9-prey-position-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        List<GameSession> sessions = [];
        List<AccountOwners.Lease> leases = [];
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("prey-position.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var owners = new AccountOwners();
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var registry = new RaidRegistry();
            RaidActor Make(int index)
            {
                var owner = owners.Acquire("prey-position-" + index, () => { });
                leases.Add(owner);
                repository.Create(owner, new("PreyP" + index, point, point) { Level = 95 });
                var session = new GameSession(repository, towns, owner, entry: new(new(2, 42), 50, "[prey]"));
                sessions.Add(session);
                session.Select(0);
                session.MarkReady();
                return session.CaptureRaidActor(registry)!;
            }
            var actors = Enumerable.Range(0, 3).Select(Make).ToArray();
            PreyTeamBattleTests.RunMembers(actors);
            var leader = actors[0];
            var created = registry.Create(leader, 3, "prey", "prey"u8.ToArray(), 12)!;
            That(created.Members is [{ Squad: 1, PreyPosition: 11, Slot: 0 }],
                "普通普雷创建者默认第1队第1格，仍保留原加入顺序Slot0");
            foreach (var actor in actors.Skip(1))
            {
                That(registry.Invite(actor, leader, 0) is not null, "普雷普通团接受当前角色申请");
                That(registry.Respond(leader, actor.Character, true).Accepted, "普雷普通团提交真实成员");
            }
            var original = registry.Find(leader)!;
            That(original.Members.Skip(1).All(member => member is { Squad: 0, PreyPosition: 0 }),
                "后来加入的普通成员保持未标记，不继承创建者默认位置");
            foreach (byte invalid in new byte[] { 1, 10, 15, 20, 25, 30, 35, 41, byte.MaxValue })
                That(registry.AssignPrey(leader, leader.Character, invalid) is null && registry.IsCurrent(original),
                    "非法十进制位置不修改成员或版本");
            That(registry.AssignPrey(actors[1], leader.Character, 11) is null && registry.IsCurrent(original),
                "非团长不能指定普雷位置");
            That(registry.AssignPrey(leader, leader.Character, 11) is not null &&
                registry.AssignPrey(leader, actors[1].Character, 14) is not null &&
                registry.AssignPrey(leader, actors[2].Character, 31) is not null, "有效位置可保留同队空格及第三队");
            var assigned = registry.Find(leader)!;
            That(assigned.Members[0].PreyPosition == 11 && assigned.Members[0].Squad == 1 &&
                assigned.Members[1].PreyPosition == 14 && assigned.Members[1].Squad == 1 &&
                assigned.Members[2].PreyPosition == 31 && assigned.Members[2].Squad == 3 &&
                assigned.Members.Select(member => member.Slot).SequenceEqual(original.Members.Select(member => member.Slot)),
                "普雷位置派生分队但不覆盖加入顺序Slot");
            That(registry.AssignPrey(leader, actors[1].Character, 11) is null && registry.IsCurrent(assigned),
                "目标位置已占用时保持双方原位，不推定交换");
            That(ReferenceEquals(registry.AssignPrey(leader, leader.Character, 11)!.After, assigned),
                "重复同位置不递增版本");
            var open = RaidPackets.OpenWithSquads(assigned.Key, RaidProjection.Members(assigned),
                preyPositions: RaidProjection.PreyPositions(assigned));
            int at = 9;
            foreach (var member in assigned.Members.OrderBy(member => member.Slot))
            {
                int nameLength = CharacterName.Encoding.GetByteCount(member.Actor.Name);
                That(open[at + 9 + nameLength] == member.Squad && open[at + 33 + nameLength] == member.PreyPosition,
                    "完整591成员表分别投影真实分队和普雷位置");
                at += 53 + nameLength;
            }
            That(at == open.Length, "591成员表完整消费且没有额外尾部");
            That(registry.AssignPrey(leader, leader.Character, 0)!.After!.Members[0] is { Squad: 0, PreyPosition: 0 },
                "原生op2 argument0同时清空普通普雷位置及分队");
            actors[2].Session.ReturnToRoster();
            var current = registry.Find(leader)!;
            That(registry.AssignPrey(leader, actors[2].Character, 21) is null && registry.IsCurrent(current),
                "离团及旧选角角色不能再次编队");
            foreach (var actor in actors.Take(2)) registry.Leave(actor, actor.Character);
            var guide = registry.Create(leader, 3, "guide", [], 1, guide: true)!;
            That(guide.Members[0] is { Squad: 1, PreyPosition: 11 } &&
                ReferenceEquals(registry.AssignPrey(leader, leader.Character, 11)?.After, guide) &&
                registry.AssignPrey(leader, leader.Character, 0) is null &&
                registry.AssignPrey(leader, leader.Character, 12) is null &&
                registry.AssignPrey(leader, leader.Character, 21) is null && registry.IsCurrent(guide),
                "引导模式保持单队首槽11，重复编队幂等且不能清空或换槽");
        }
        finally
        {
            foreach (var session in sessions) session.End();
            foreach (var lease in leases) lease.Dispose();
        }
    }
}
