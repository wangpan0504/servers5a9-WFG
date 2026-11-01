using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class RaidCrossChannelTests
{
    public static void RunCore()
    {
        int before = Count;
        byte[] request = [2, 2, 1, 0, 3, 2, 0, 7, 0, 0, 0, 8, 0, 1, 0];
        var parsed = RaidRequests.CrossRequest(request);
        That(parsed.Targets.SequenceEqual(new RaidCrossTarget[] { new(2, 1), new(3, 2) }) &&
            parsed.Coins == 7 && parsed.AntiMagic == 8 && parsed.Channel == 1,
            "822批量目标与显示尾部完整保留，批量channel1不误作目标所在频道");
        for (int length = 0; length < request.Length; length++)
            Reject<InvalidDataException>(() => RaidRequests.CrossRequest(request[..length]), "822截断拒绝");
        Reject<InvalidDataException>(() => RaidRequests.CrossRequest([.. request, 0]), "822额外尾部拒绝");
        Reject<InvalidDataException>(() => RaidRequests.CrossRequest([0, 0, 0, 0, 0, 0, 0, 1, 0]), "822零目标拒绝");
        That(RaidRequests.CrossResponse([2, 1, 0, 19]) == new RaidUserRequest(2, 1, 19) &&
            RaidRequests.CrossCancel([2, 1, 0]) == new RaidCrossTarget(2, 1), "823/826按实际读宽定位目标");
        Reject<InvalidDataException>(() => RaidRequests.CrossResponse([2, 1, 0]), "823截断拒绝");
        Reject<InvalidDataException>(() => RaidRequests.CrossCancel([2, 1, 0, 19]), "826额外频道拒绝");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-raid-cross-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        List<GameSession> sessions = []; List<AccountOwners.Lease> leases = [];
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("cross-fixture.map", [new(new(0,0,20,20),[],[])], [], false, []);
            var towns = new TownCatalog(new() { [(1,0)] = new(1,0,1,[],[],[],point,map,[],[]) },
                point, point, new(0,[],[]), [], new Dictionary<uint,uint[]>());
            var owners = new AccountOwners();
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var clock = new LukeRaidClockTests.Clock();
            var registry = new RaidRegistry(clock);
            RaidActor Make(int index, byte channel = 20)
            {
                var owner = owners.Acquire("raid-cross-" + index, () => { }); leases.Add(owner);
                if (repository.ListRoster(owner).Length == 0)
                    repository.Create(owner, new("Cross" + index, point, point) { Level = 85, CompletedQuests = [12829,3779] });
                var session = new GameSession(repository, towns, owner, entry: new(new(2,channel),23,"[raid]"));
                sessions.Add(session); session.Select(0); session.MarkReady();
                return session.CaptureRaidActor(registry)!;
            }
            var leader = Make(0, 19); var applicant = Make(1); var other = Make(2); var wrong = Make(3);
            var party = registry.Create(leader, 0, "cross", "cross"u8.ToArray(), 3)!;
            That(registry.Invite(applicant, leader, 0) is null && registry.CrossList(0).Single().Key == party.Key,
                "跨频道目录可见，但不能借普通同频道邀请绕过授权");
            var application = registry.InviteCross(applicant, leader, 7, 8)!;
            That(application is not null && registry.CanPublishCross(application) &&
                !registry.Respond(applicant, leader.Character, true).Accepted,
                "散人申请不构成队长授权，最终11不能提前加入");
            That(registry.AcceptCross(wrong, applicant) is null && registry.AcceptCross(leader, wrong) is null,
                "非队长和没有申请的目标不能生成入团凭证");
            var accepted = registry.AcceptCross(leader, applicant)!;
            That(accepted is not null && registry.CanPublishCross(accepted) &&
                !registry.Respond(applicant, leader.Character, true).Accepted,
                "队长823只授权换频道，旧频道不能提前加入");
            applicant.Session.End();
            var renewed = Make(1, 19);
            That(registry.Respond(renewed, leader.Character, true).Change?.After?.Members.Length == 2,
                "旧连接正常退出后凭证仍有效，新owner真实切入目标频道再由11加入");
            registry.RemoveSessionNotices(applicant.Session, applicant.Epoch);
            That(registry.Find(renewed)?.Key == party.Key && !registry.Respond(renewed, leader.Character, true).Accepted,
                "旧连接迟到清理不删除新成员，重复11不重复加入");

            var invitation = registry.InviteCross(leader, other, 0, 0)!;
            That(invitation is not null && registry.AcceptCross(leader, other) is not null,
                "队长主动邀请已授权，不强制散人先发823");
            var sameAccountOtherRole = other with { Character = wrong.Character };
            That(!registry.Respond(sameAccountOtherRole, leader.Character, true).Accepted &&
                !registry.Respond(wrong, leader.Character, true).Accepted, "凭证不能由别的账号或角色消费");
            That(registry.TransferLeadership(leader, renewed.Character) is not null &&
                registry.TransferLeadership(renewed, leader.Character) is not null &&
                !registry.CanPublishCross(invitation!) && registry.AcceptCross(leader, other) is null,
                "委任永久撤销旧凭证，交回原队长也不能复活旧邀请");
            That(registry.InviteCross(leader, other, 0, 0) is not null &&
                registry.CancelCross(other, 2, leader.Character) && registry.AcceptCross(leader, other) is null,
                "826切频道失败撤销调用者的真实授权");
            registry.InviteCross(leader, other, 0, 0);
            That(registry.Respond(other, leader.Character, false).Accepted && registry.AcceptCross(leader, other) is null,
                "原11拒绝撤销主动邀请和关联凭证");
            registry.InviteCross(leader, other, 0, 0);
            clock.AdvanceWithoutCallbacks(31);
            That(registry.AcceptCross(leader, other) is null && !registry.Respond(other, leader.Character, true).Accepted,
                "超时申请和授权均不接受，不因重复请求续期");

            registry.InviteCross(leader, other, 0, 0);
            registry.InviteCross(leader, wrong, 0, 0);
            other.Session.End(); wrong.Session.End();
            var first = Make(2, 19); var second = Make(3, 19);
            var results = Task.WhenAll(Task.Run(() => registry.Respond(first, leader.Character, true)),
                Task.Run(() => registry.Respond(second, leader.Character, true))).GetAwaiter().GetResult();
            That(results.Count(result => result.Accepted) == 1 && registry.Find(leader)!.Members.Length == 3,
                "两个跨频道凭证竞争最后一席，只提交一次且不超容量");

            var character = leader.Session.Selected! with { Emotion = 0x1234 };
            var actor = TownPackets.Actor(character, default, leader.Channel);
            var summary = RaidCrossChannelPackets.Request(character, leader.Channel, 99, 73, 1234, actor);
            That(summary.Length == 59 && BinaryPrimitives.ReadUInt16LittleEndian(summary) == leader.Character &&
                summary[4] == 2 && summary[5] == 19 && BinaryPrimitives.ReadUInt16LittleEndian(summary.AsSpan(10)) == 99 &&
                BinaryPrimitives.ReadUInt16LittleEndian(summary.AsSpan(12)) == 73 &&
                BinaryPrimitives.ReadUInt16LittleEndian(summary.AsSpan(14)) == 1234 &&
                BinaryPrimitives.ReadUInt16LittleEndian(summary.AsSpan(56)) == 0x1234 && summary[51] == 0,
                "758完整摘要保留真实身份、疲劳/虚弱/抗魔及原Actor心情，不触发删除标志");
            Bytes(summary.AsSpan(20, CharacterName.Encoding.GetByteCount(character.Name)),
                CharacterName.Encoding.GetBytes(character.Name), "758固定名区使用CP936而非UTF16");
            Bytes(RaidCrossChannelPackets.Response(0x1234, leader.Channel), [2, 0x34, 0x12, 0, 0, 19],
                "759使用目标队长定位，未赋义字段保持零");
        }
        finally
        {
            foreach (var session in sessions) session.End();
            foreach (var lease in leases) lease.Dispose();
            TestFiles.DeleteDirectory(root, recursive: true);
        }
        Console.WriteLine($"PASS {Count - before} 跨频道招募授权、重连身份、容量与协议断言");
    }
}
