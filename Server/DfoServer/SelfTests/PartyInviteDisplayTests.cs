using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PartyInviteDisplayTests
{
    public static void RunCore()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-party-invite-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new InviteClock { Now = DateTimeOffset.UtcNow };
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("invite.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var sourceOwner = owners.Acquire("invite-source", () => { });
            using var targetOwner = owners.Acquire("invite-target", () => { });
            repository.Create(sourceOwner, new("InviteSource", point, point)
            {
                DungeonState = CharacterDungeonState.NewOrdinary
            });
            repository.Create(targetOwner, new("InviteTarget", point, point) { DungeonState = CharacterDungeonState.NewOrdinary });
            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open();
                using var sql = db.CreateCommand();
                sql.CommandText = "UPDATE character_fatigue SET remaining=CASE account WHEN 'invite-source' THEN 77 ELSE 33 END";
                That(sql.ExecuteNonQuery() == 2, "隔离夹具确有双方疲劳账本并写入不同余额，不能退回缺记录默认值");
            }
            var source = new GameSession(repository, towns, sourceOwner, timeProvider: clock);
            var target = new GameSession(repository, towns, targetOwner, timeProvider: clock);
            source.Select(0); source.MarkReady(); target.Select(0); target.MarkReady();
            var weakened = source.PlanAndCommitInventory(character => new(true, 0,
                new(character.Inventory, character.Inventory with { StaminaUntil = clock.Now.ToUnixTimeSeconds() + 600 },
                    "invite_test_weaken", new(character, null))));
            That(weakened.Accepted, "邀请显示夹具通过真实库存事务持久化虚弱期限，换角后仍可读取");
            using var memory = new DfoServer.Game.Caching.GameCache();
            var missing = new PartyService(repository, clock, memory);
            Reject<InvalidOperationException>(() => missing.Invite(source, target, 1), "缺抗魔provider明确失败，不能发送伪零显示");
            var failed = new PartyService(repository, clock, memory, inviteAntiEvil: _ => throw new InvalidDataException("fixture"));
            Reject<InvalidDataException>(() => failed.Invite(source, target, 1), "抗魔计算失败不创建邀请");
            foreach (int invalid in new[] { -1, 65536 })
            {
                var overflow = new PartyService(repository, clock, memory, inviteAntiEvil: _ => invalid);
                Reject<OverflowException>(() => overflow.Invite(source, target, 1), "超出u16的显示抗魔明确失败，不回绕或伪零");
            }
            That(memory.Count == 0 && !failed.Respond(target, source.Selected!.Id, 1, true).Accepted,
                "配置或计算失败无邀请、队伍及可接受的半状态");
            var stale = new PartyService(repository, clock, memory, inviteAntiEvil: _ =>
            {
                That(source.Move(new(11, 10, 0, 0, 0, 0)), "准备显示期间源角色发生真实位置提交");
                return 1234;
            });
            That(!stale.Invite(source, target, 1).Accepted && memory.Count == 0,
                "Pair内复验精确源快照，拒绝在准备期间变化的Character");
            var reselected = new PartyService(repository, clock, memory, inviteAntiEvil: _ =>
            {
                source.ReturnToRoster(); source.Select(0); source.MarkReady(); return 1234;
            });
            That(!reselected.Invite(source, target, 1).Accepted && memory.Count == 0,
                "准备后同角色新选角代次也不能创建旧邀请");
            var parties = new PartyService(repository, clock, memory,
                inviteAntiEvil: character => character.Id == source.Selected!.Id ? 1234 : 5678);
            const uint peer = 0x01020304;
            var result = parties.Invite(source, target, peer);
            var notice = result.Notices.Single();
            That(result.Accepted && ReferenceEquals(notice.Peer!.Actor.Session, source) &&
                ReferenceEquals(notice.Recipient.Actor.Session, target) && notice.InviteDisplay == new PartyInviteDisplay(77, 10, 1234),
                $"邀请资料取邀请者疲劳77/虚弱10/抗魔1234，不取被邀请者33/100/5678；实际{notice.InviteDisplay}，期限{source.Selected!.Inventory.StaminaUntil}，当前{clock.Now.ToUnixTimeSeconds()}");
            byte[] expected = Convert.FromHexString("000000040302014D000A00D20400000000");
            expected[0] = (byte)source.Selected!.Id; expected[1] = (byte)(source.Selected.Id >> 8);
            Bytes(PartyPackets.Peer(source.Selected.Id, 0, peer, notice.InviteDisplay), expected,
                "NOTI7正文严格17B，三个u16依次为邀请者真实值，末u32仍为0");
            That(source.Move(new(12, 10, 0, 0, 0, 0)) && notice.Guard!() &&
                notice.InviteDisplay == new PartyInviteDisplay(77, 10, 1234),
                "邀请创建后移动不吞掉已创建邀请，显示保留创建时不可变快照");
            clock.Now += TimeSpan.FromSeconds(31);
            That(!notice.Guard!() && !parties.Respond(target, source.Selected.Id, peer, true).Accepted,
                "创建时显示快照不绕过30秒到期门禁");
            var next = parties.Invite(source, target, peer);
            var joined = parties.Respond(target, source.Selected.Id, peer, true);
            That(next.Accepted && joined.Accepted && !next.Notices.Single().Guard!(), "接受消费邀请后迟到显示通知失效");
            var accepted = joined.Notices.Single(n => n.Kind == PartyNoticeKind.Accepted);
            ushort targetId = target.Selected!.Id;
            Bytes(PartyPackets.Peer(targetId, 0, peer), [(byte)targetId, (byte)(targetId >> 8), 0, 4, 3, 2, 1],
                "NOTI8接受仍严格7B，不追加邀请显示尾部");
            That(accepted.InviteDisplay is null, "接受通知不携带邀请显示资料");
            ushort sourceId = source.Selected.Id;
            Bytes(TownPackets.PeerMenu(sourceId, 0x1234), [(byte)sourceId, (byte)(sourceId >> 8), 15, 0, 0, 0, 0, 0x34, 0x12],
                "NOTI7 mode15原菜单9B合同不受mode0邀请尾部影响");
            parties.Detach(source); parties.Detach(target);
            var reverse = parties.Invite(target, source, peer);
            That(reverse.Accepted && reverse.Notices.Single().InviteDisplay == new PartyInviteDisplay(33, 100, 5678),
                "交换邀请方向后显示也随真实邀请者交换，不复用接收者或上次资料");
            parties.Respond(source, targetId, peer, false);
            var replaced = new PartyService(repository, clock, memory, inviteAntiEvil: _ =>
            {
                using var replacement = owners.Acquire(sourceOwner.Account, () => { }); return 1234;
            });
            That(!replaced.Invite(source, target, 1).Accepted && memory.Count == 0,
                "准备后源owner失效时Pair拒绝，原邀请索引和队伍无新增");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }

    sealed class InviteClock : TimeProvider
    {
        public DateTimeOffset Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
