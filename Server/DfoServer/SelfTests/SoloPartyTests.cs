using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class SoloPartyTests
{
    public static void RunCore()
    {
        var (operation, settings) = PartyRequests.Information(Convert.FromHexString("000304000000000500000000000007"));
        That(operation == 0 && settings == new PartySettings(3, "", 4, 0, 5, 0, 0, 0, 0, 7),
            "实收15B创建请求完整保留设置");
        var evildom = PartyRequests.Information(Convert.FromHexString("0000040000003132333104000000000500000005010007"));
        That(evildom.Operation == 0 && evildom.Settings == new PartySettings(0, "1231", 4, 0, 5, 0, 0, 5, 1, 7),
            "魔界大战实收CMD12保留kind5与kindArgument1，不改写为普通招募或CREATE_RAID");
        foreach (int width in new[] { 0, 13, 14, 16 })
            Reject<InvalidDataException>(() => PartyRequests.Information(new byte[width]), "畸形创建请求不接受缺失或未知尾部");
        var custom = DfoServer.Testing.TcpPeer.Build(w =>
        {
            w.Write((byte)0); w.Write((byte)0); PacketEncoding.WriteString(w, "测试队伍");
            w.Write(Convert.FromHexString("04000000000500000000000007"));
        });
        That(PartyRequests.Information(custom).Settings.Title == "测试队伍", "自定义标题按DSTR/CP936解码");
        byte[] expected = new byte[56];
        expected[0] = 1; expected[2] = 15; expected[3] = 39; expected[4] = 232; expected[5] = 253;
        expected[6] = 2; expected[7] = 10; expected[12] = 3; expected[14] = 4; expected[19] = 5;
        expected[36] = 7; expected[37] = 1; expected[39] = 232; expected[40] = 253;
        byte[] Info(PartySettings value) => PartyPackets.Information(65000, 2, 10, [(0, 65000)], 0, false, value);
        Bytes(Info(settings), expected, "单人队伍使用统一完整名册，不截断成员字段");
        var selection = Info(settings with { Dungeon = 0x12345678 });
        That(selection.AsSpan(15, 4).SequenceEqual(new byte[4]) && selection.AsSpan(30, 4).SequenceEqual(new byte[] { 120, 86, 52, 18 }),
            "目标selectionId回放在本版模型+116对应位置");
        byte[] clear = expected[..11]; clear[9] = 3;
        Bytes(PartyPackets.Information(65000, 2, 10, [], 0, true), clear, "清理kind3保留完整记录前缀");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-solo-party-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("party.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("solo-party", () => { });
            repository.Create(owner, new("SoloParty", point, point));
            var session = new GameSession(repository, towns, owner);
            var parties = new PartyService(repository);
            That(!parties.SetInformation(session, 0, settings).Accepted, "未选角不能建队");
            session.Select(0);
            That(!parties.SetInformation(session, 0, settings).Accepted, "入城未就绪不能提前建队");
            session.MarkReady();
            string stored = JsonSerializer.Serialize(repository.LoadCharacter(owner, 0));
            That(!parties.SetInformation(session, 1, settings).Accepted, "无队伍不能修改");
            That(!parties.SetInformation(session, 0, settings, blocked: true).Accepted,
                "原交互占用门禁拒绝建队");
            var created = parties.SetInformation(session, 0, settings);
            var originalParty = parties.Current(session)!;
            That(created.Accepted && originalParty.Members.Length == 1 && originalParty.Leader == 0 &&
                session.GetState() == UserState.Town, "唯一队长由共享队伍服务持有，建队不改变城镇阶段");
            That(parties.SetInformation(session, 0, settings).Notices.Length == 0 &&
                ReferenceEquals(originalParty, parties.Current(session)), "相同请求不重复分配队伍");
            var changed = parties.SetInformation(session, 1, settings with { TitleCode = 5 });
            var frameIdentity = session.PublicCharacter(session.Selected!.Id)!;
            var frameNotices = parties.FrameChanged(session, frameIdentity);
            That(frameNotices is [{ Kind: PartyNoticeKind.FrameChanged }] && frameNotices[0].Guard!(),
                "边框通知复用当前队伍，保留提交后的源公开快照");
            That(created.Notices.All(n => n.Guard?.Invoke() == false) && changed.Notices.All(n => n.Guard?.Invoke() == true),
                "设置更新使旧队伍发布授权失效");
            That(JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == stored, "建队和修改不写资产或revision");
            session.Move(new(11, 10, 0, 0, 0, 0));
            That(frameNotices[0].Guard!(), "普通移动不吞掉已入队的边框刷新，出队时投影最新角色");
            var phaseFrames = parties.FrameChanged(session, session.PublicCharacter(session.Selected!.Id)!);
            That(changed.Notices.All(n => n.Guard?.Invoke() == true), "城镇移动保留队伍身份");
            session.BeginDungeonSelection();
            That(!phaseFrames[0].Guard!(), "接收者切换阶段后不发送旧边框通知");
            That(changed.Notices.All(n => n.Guard?.Invoke() == false) && !parties.SetInformation(session, 0, settings).Accepted,
                "选图阶段不发布排队的旧城镇名册或修改设置");
            session.ReturnFromDungeonSelection(false);
            var left = parties.Leave(session);
            That(left.Accepted && parties.Current(session) is null, "单人退队删除统一队伍状态");
            parties.SetInformation(session, 0, settings); parties.Leave(session);
            That(left.Notices.All(n => n.Guard?.Invoke() == false), "重建再退队不能使旧清理授权重新生效");
            var beforeReturn = parties.SetInformation(session, 0, settings);
            session.ReturnToRoster(); session.Select(0); session.MarkReady();
            That(parties.Current(session) is null && beforeReturn.Notices.All(n => n.Guard?.Invoke() == false),
                "选角epoch阻止继承旧队伍及旧通知");
            parties.Detach(session);
            string finalStored = JsonSerializer.Serialize(repository.LoadCharacter(owner, 0));
            var final = parties.SetInformation(session, 0, settings);
            using var nextOwner = owners.Acquire(owner.Account, () => { });
            That(final.Notices.All(n => n.Guard?.Invoke() == false), "旧generation不能发布队伍");
            var next = new GameSession(repository, towns, nextOwner);
            next.Select(0); next.MarkReady(); parties.SetInformation(next, 0, settings);
            session.End(); parties.Detach(session);
            That(parties.Current(next) is not null, "旧会话清理不删除接管后新队伍");
            next.End(); parties.Detach(next);
            That(JsonSerializer.Serialize(repository.LoadCharacter(nextOwner, 0)) == finalStored,
                "创建、接管与队伍收尾不额外写角色资产");
        }
        finally { Directory.Delete(root, true); }
        VerifyMembership();
    }

    static void VerifyMembership()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-party-members-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("party.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var a = owners.Acquire("party-a", () => { });
            using var b = owners.Acquire("party-b", () => { });
            using var c = owners.Acquire("party-c", () => { });
            using var d = owners.Acquire("party-d", () => { });
            using var e = owners.Acquire("party-e", () => { });
            GameSession Ready(AccountOwners.Lease owner, string name)
            {
                repository.Create(owner, new(name, point, point));
                var session = new GameSession(repository, towns, owner);
                session.Select(0); session.MarkReady(); return session;
            }
            var sessions = new[] { Ready(a, "PartyA"), Ready(b, "PartyB"), Ready(c, "PartyC"), Ready(d, "PartyD"), Ready(e, "PartyE") };
            var clock = new PartyClock();
            using var memory = new DfoServer.Game.Caching.GameCache();
            var parties = new PartyService(repository, clock, memory);
            var leader = sessions[0];
            ushort id = leader.Selected!.Id;
            That(!parties.Invite(leader, sessions[1], 1, blocked: true).Accepted, "交互占用不能邀请");
            var refused = parties.Invite(leader, sessions[1], 1);
            That(refused.Accepted && parties.Respond(sessions[1], id, 2, false).Notices.Single().Kind == PartyNoticeKind.Refused &&
                !parties.HasMultipleMembers(leader), "拒绝邀请不创建队伍");
            var expired = parties.Invite(leader, sessions[1], 1);
            clock.Now += TimeSpan.FromSeconds(31);
            That(!parties.Respond(sessions[1], id, 2, true).Accepted && expired.Notices.Single().Guard?.Invoke() == false,
                "30秒过期后接受及在途邀请失效");
            foreach (var member in sessions.Skip(1).Take(3))
                That(parties.Invite(leader, member, 1).Accepted && parties.Respond(member, id, 2, true).Accepted,
                    "实际成员依次加入同一队伍");
            var full = parties.Current(leader)!;
            That(full.Members.Select(m => m.Slot).SequenceEqual(new byte[] { 0, 1, 2, 3 }) &&
                !parties.Invite(leader, sessions[4], 1).Accepted, "四人实际满员，不接受第五人");
            That(!parties.Leave(sessions[1], 2).Accepted, "普通队员不能踢另一个队员");
            That(parties.Leave(leader).Accepted && parties.Current(sessions[1]) is { Leader: 1, Members.Length: 3 } inherited &&
                inherited.Id == full.Id && inherited.Members.Select(m => m.Slot).SequenceEqual(new byte[] { 1, 2, 3 }),
                "队长退出按最小幸存槽继任，保留队伍ID及原槽");
            That(parties.Invite(sessions[1], sessions[4], 3).Accepted &&
                parties.Respond(sessions[4], sessions[1].Selected!.Id, 4, true).Accepted &&
                parties.Current(sessions[4])!.Members.Single(m => ReferenceEquals(m.Actor.Session, sessions[4])).Slot == 0,
                "空槽复用不移动幸存队员或改变已继任队长");
            var kicked = parties.Leave(sessions[1], 0);
            That(kicked.Accepted && parties.Current(sessions[4]) is null, "继任队长能踢新成员");
            parties.Detach(sessions[1]);
            That(parties.Current(sessions[2]) is { Leader: 2, Members.Length: 2 }, "精确断线清理后再按槽继任");
            foreach (var session in sessions) { parties.Detach(session); session.End(); }
            That(parties.Snapshot().Length == 0 && memory.Count == 0, "成员全部离开释放共享队伍与邀请索引");
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class PartyClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
