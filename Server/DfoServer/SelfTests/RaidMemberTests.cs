using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;
using System.Buffers.Binary;
using System.Net;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class RaidMemberTests
{
    public static void RunCore()
    {
        int before = Count;
        var info = RaidRequests.Information([2, 1, 0, 19, 2]);
        That(info.Mode == 2 && info.Key == 0x02130001, "1361严格消费mode和key");
        var management = RaidRequests.Manager([0,0,0,0, 7,0,0,0, 5,0,0,0]);
        That(management.Character == 7 && management.Argument == 5, "669三个u32不能压缩目标角色读宽");
        Reject<InvalidDataException>(() => RaidRequests.Information([3,0,0,0,0]), "1361未知mode拒绝");
        Reject<InvalidDataException>(() => RaidRequests.Information([2,0,0,0,0,0]), "1361多余尾部拒绝");
        Reject<InvalidDataException>(() => RaidRequests.Manager(new byte[11]), "669截断拒绝");
        That(RaidRequests.Rejoin([1,0,19,2]) == 0x02130001, "668严格消费原团key");
        Reject<InvalidDataException>(() => RaidRequests.Rejoin(new byte[5]), "668多余尾部拒绝");
        Reject<InvalidDataException>(() => RaidRequests.Peer(new byte[13], false), "普通组队不借团本mode10解析");
        var emptyHp = RaidRequests.MonsterHp(new byte[5]);
        That(emptyHp.RawValue == 0 && emptyHp.RawEntries.Length == 0, "695已证全零分支保留原始字段，不解释为死亡");
        byte[] hpBody = [0x78,0x56,0x34,0x12, 2, 1,0,0,0, 0xff,0xff,0xff,0xff];
        var hp = RaidRequests.MonsterHp(hpBody);
        That(hp.RawValue == 0x12345678 && hp.RawEntries.SequenceEqual(new uint[] { 1, uint.MaxValue }),
            "695合成输入保留u32读宽和列表顺序，不赋予对象或权威HP语义");
        for (int length = 0; length < hpBody.Length; length++)
            Reject<InvalidDataException>(() => RaidRequests.MonsterHp(hpBody[..length]), "695截断或计数不符拒绝");
        Reject<InvalidDataException>(() => RaidRequests.MonsterHp([.. hpBody, 0]), "695多余尾部拒绝");
        byte[] maximumHp = new byte[5 + 4 * byte.MaxValue];
        maximumHp[4] = byte.MaxValue;
        That(RaidRequests.MonsterHp(maximumHp).RawEntries.Length == byte.MaxValue, "695计数按u8上界解析，不收窄成分队人数");
        // 两个发送循环保留完整列表；低8位回绕不代表客户端只发送截断后的N项。
        foreach (int count in new[] { 256, 257, 511 })
        {
            byte[] wrappedHp = new byte[5 + 4 * count];
            wrappedHp[4] = unchecked((byte)count);
            Reject<InvalidDataException>(() => RaidRequests.MonsterHp(wrappedHp),
                "695完整列表与低u8计数不一致时拒绝，不静默截断或把回绕零计数解释为空表");
        }

        string root = Path.Combine(Path.GetTempPath(), "s5a9-raid-members-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        List<GameSession> sessions = []; List<AccountOwners.Lease> leases = [];
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("raid-fixture.map", [new(new(0,0,20,20),[],[])], [], false, []);
            var towns = new TownCatalog(new() { [(1,0)] = new(1,0,1,[],[],[],point,map,[],[]) },
                point, point, new(0,[],[]), [], new Dictionary<uint,uint[]>());
            var owners = new AccountOwners();
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var registry = new RaidRegistry();
            RaidActor Make(int index)
            {
                var owner = owners.Acquire("raid-member-" + index, () => { }); leases.Add(owner);
                var role = repository.ListRoster(owner).FirstOrDefault();
                if (role is null) repository.Create(owner, new("RaidM" + index, point, point) { Level = 85, CompletedQuests = [12829,3779] });
                var session = new GameSession(repository, towns, owner, entry: new(new(2,19),23,"[raid]"));
                sessions.Add(session); session.Select(0); session.MarkReady();
                return session.CaptureRaidActor(registry)!;
            }
            var actors = Enumerable.Range(0, 21).Select(Make).ToArray();
            var leader = actors[0];
            var first = registry.Create(leader, 0, "a", "a"u8.ToArray(), 20)!;
            That(first.Key == 0x02130001 && first.Members.Length == 1 && registry.Count == 1, "真实频道key及单人创建");
            That(registry.Create(leader,0,"b","b"u8.ToArray(),20) is null && registry.IsCurrent(first), "重复创建不丢已有聚合");
            for (int index = 1; index < 20; index++)
            {
                That(registry.AddWaiting(actors[index],0) is null, "登记当前会话待机");
                var invitation = registry.Invite(actors[index],leader,0);
                That(invitation is not null, "无队玩家可向队长申请");
                var result = registry.Respond(leader,actors[index].Character,true);
                That(result.Accepted && result.Change?.After?.Members.Length == index + 1, "接受后整支攻坚队人数递增");
                That(registry.Waiting(leader.Channel,0).Length == 0, "加入后移除原待机项");
                if (index is 8 or 9 or 19)
                    That(registry.Find(leader)!.Members.Length == index + 1, "9/10/20成员边界不修改PVF门槛");
            }
            var party = registry.Find(leader)!;
            That(party.Capacity == 20 && registry.Invite(actors[20],leader,0) is null, "第21人不能超出资源容量");
            That(registry.Rename(actors[1],0,"bad",[]) is null && registry.Assign(actors[1],leader.Character,1) is null &&
                registry.Leave(actors[1],actors[2].Character) is null, "非队长不能改名/编队/踢人");
            for (int index = 0; index < 4; index++) That(registry.Assign(leader,actors[index].Character,1) is not null, "同分队四人");
            That(registry.Assign(leader,actors[4].Character,1) is null && registry.Assign(leader,actors[4].Character,6) is null,
                "分队不超过四人，20人团本不出现第六分队");
            var current = registry.Find(leader)!;
            var same = registry.Assign(leader,leader.Character,1)!;
            That(ReferenceEquals(same.After,current) && !registry.IsCurrent(first), "重复编队不推进版本，旧快照失效");
            That(registry.TransferLeadership(actors[1], actors[2].Character) is null &&
                registry.TransferLeadership(leader, actors[20].Character) is null &&
                registry.TransferLeadership(leader, leader.Character) is null && registry.IsCurrent(current),
                "非队长、团外目标和本人委任不改变聚合");
            var appointed = registry.TransferLeadership(leader, actors[4].Character)!;
            That(appointed.After!.Leader == actors[4].Character && appointed.After.Key == current.Key &&
                appointed.After.Members == current.Members && appointed.After.Revision > current.Revision && !registry.IsCurrent(current),
                "委任只更换实际团长及版本，保留成员、槽位、分队和团key");
            That(registry.Rename(leader,0,"bad",[]) is null && registry.Assign(leader,actors[1].Character,2) is null &&
                registry.TransferLeadership(leader, actors[1].Character) is null && registry.Leave(leader,actors[1].Character) is null,
                "委任后原团长立即失去管理权限，重复旧请求不得再次移交");
            That(registry.TransferLeadership(actors[4], leader.Character)?.After?.Leader == leader.Character,
                "新团长可将权限交回，后续自动继任仍走原合同");
            var exit = registry.Leave(leader,leader.Character)!;
            That(exit.After!.Leader == actors[1].Character && exit.After.Key == first.Key && exit.After.Members.Length == 19 &&
                exit.After.Members[0].Slot == 1, "队长离开由最小幸存槽继任，保留key和成员槽");
            That(registry.Invite(actors[1], actors[20], 42) is not null &&
                registry.TransferLeadership(actors[1], actors[4].Character) is not null &&
                !registry.Respond(actors[20], actors[1].Character, true, 42).Accepted,
                "委任撤销原团长尚未回应的邀请，不能借旧请求加入");
            That(registry.TransferLeadership(actors[4], actors[1].Character) is not null, "交回继任团长权限");
            sessions[2].ReturnToRoster();
            That(registry.Find(actors[1])!.Members.Length == 18 && registry.Find(actors[2]) is null, "重选时精确移除原选角登记");
            var stale = actors[3];
            var replacement = Make(3);
            var replacementParty = registry.Create(replacement,0,"new","new"u8.ToArray(),20)!;
            That(registry.TransferLeadership(actors[1], stale.Character) is null,
                "接管后旧成员登记尚未清理，也不能成为新团长");
            stale.Session.End();
            That(!stale.IsCurrent && registry.Find(replacement)?.Key == replacementParty.Key && registry.IsCurrent(replacementParty),
                "接管后旧End不删除新会话的攻坚队");
            That(registry.Waiting(new(2,20),0).Length == 0 && registry.Find(first.Key,new(2,20)) is null,
                "其它频道不能查询本频道聚合或待机");
            replacement.Session.ReturnToRoster();
            That(registry.Find(replacementParty.Key,replacement.Channel) is null, "最后成员重选后解散");
            That(registry.AddWaiting(actors[20], 0) is null, "无团角色登记候战");
            var takeover = Make(20);
            var oldWaiting = registry.Waiting(takeover);
            That(oldWaiting is not null && ReferenceEquals(oldWaiting.Actor.Session, actors[20].Session),
                "接管后建队仍捕获旧候战项，防止漏发删除通知");
            registry.Create(takeover, 0, "take", "take"u8.ToArray(), 20);
            That(registry.Waiting(takeover) is null, "新建团移除原角色候战项");
            actors[20].Session.End();
            That(registry.Find(takeover) is not null, "旧候战会话收尾不删除接管者的新团");
            SlowPublication(actors[1], actors[4], Make(21), registry).GetAwaiter().GetResult();
        }
        finally
        {
            foreach (var session in sessions) session.End();
            foreach (var lease in leases) lease.Dispose();
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"PASS {Count - before} 团本成员、分队、权限、版本和会话清理断言");
    }

    static async Task SlowPublication(RaidActor leader, RaidActor member, RaidActor entrant, RaidRegistry registry)
    {
        using var stop = new CancellationTokenSource();
        using var healthyStop = new CancellationTokenSource();
        using var stream = new GameSenderBudgetTests.HeldStream();
        using var healthyStream = new MemoryStream();
        await using var sender = new GameConnectionSender(member.Owner, stream, stop, 0, (_, _, _, _, _) => { });
        await using var healthy = new GameConnectionSender(leader.Owner, healthyStream, healthyStop, 0, (_, _, _, _, _) => { });
        var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), member.Owner, member.Session, sender);
        var healthyContext = new GameConnectionContext(new(2, IPAddress.Loopback, healthyStream, healthyStop), leader.Owner, leader.Session, healthy);
        var prior = member.Session.RaidNoticeArrived;
        var healthyPrior = leader.Session.RaidNoticeArrived;
        try
        {
            member.Session.RaidNoticeArrived = (change, actor) => RaidProjection.Notice(context, change, actor);
            leader.Session.RaidNoticeArrived = (change, actor) => RaidProjection.Notice(healthyContext, change, actor);
            Task held = sender.Send(0, 2, [1], "held");
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            That(registry.Invite(leader, entrant, 789) is not null, "慢接收期间邀请新角色");
            var joined = registry.Respond(entrant, leader.Character, true, 789);
            That(joined.Change is not null, "新角色加入与后续编队是不同的已提交revision");
            registry.Publish(joined.Change!);
            registry.Publish(registry.Assign(leader, member.Character, 2)!);
            await healthy.Send(1, 1278, [1], "healthy-barrier").WaitAsync(TimeSpan.FromSeconds(2));
            That(!held.IsCompleted && healthyStream.Length > 0, "慢团员写入阻塞时，发布返回且健康团员完成必要通知和后续应答");
            registry.Publish(registry.Assign(leader, member.Character, 3)!);
            Task barrier = sender.Send(1, 1278, [1], "barrier");
            That(sender.PendingBytes <= GameConnectionSender.MaxPendingBytes && !barrier.IsCompleted,
                "多人投影使用原连接有界预算，可靠等待仍受慢写入约束");
            stream.Release.TrySetResult();
            await Task.WhenAll(held, barrier).WaitAsync(TimeSpan.FromSeconds(5));
            That(stream.Frames.Count == 6 && sender.PendingBytes == 0,
                "旧成员快照丢弃，仍保留新角色Actor及最新列表/记录/成员帧，预算归零");
            byte[] actorFrame = stream.Frames[1];
            That(actorFrame.Length > 139 && actorFrame[0] == 0 &&
                BinaryPrimitives.ReadUInt16LittleEndian(actorFrame.AsSpan(1)) == 2 &&
                BinaryPrimitives.ReadUInt16LittleEndian(actorFrame.AsSpan(137)) == entrant.Character,
                "加入后立即编队不能吞掉新角色Actor，最新成员表之前仍先安装实际身份");
            byte[] frame = stream.Frames[4];
            That(BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 591 &&
                BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20)) == 3, "最新成员快照为NOTI591 op3");
            int at = 25;
            byte? squad = null;
            for (int index = 0; index < frame[24]; index++)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(at));
                int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(at + 3)));
                if (id == member.Character) squad = frame[at + 9 + length];
                at += 53 + length;
            }
            That(at == frame.Length && squad == 3, "慢接收者恢复后完整消费实际最新分队，不回退到旧分队2");
        }
        finally
        {
            member.Session.RaidNoticeArrived = prior;
            leader.Session.RaidNoticeArrived = healthyPrior;
            stream.Release.TrySetResult();
        }
    }
}
