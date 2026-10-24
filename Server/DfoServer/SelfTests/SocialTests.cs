using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Chat;
using DfoServer.Game.Session;
using DfoServer.Game.Social;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Parsers;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class SocialTests
{
    public static void RunCore()
    {
        int before = Count;
        var friend = new SocialRow(new(new("b", 42, "RoleB", 75, 1, 2, "TeamB", 8), DateTimeOffset.UnixEpoch, "memo", 3),
            42, 2, 10, true);
        Bytes(SocialPackets.AccountFriends([friend]),
            Convert.FromHexString("012A000205000000526F6C65424B01020A00050000005465616D42040000006D656D6F00000000000300"),
            "1376角色名在前、冒险团唯一键在后，不能把角色等级或当前Actor当成冒险团身份");
        var captured = SocialRequests.Account(Convert.FromHexString("0400000031323371030000003132330103000000313233"), 1439);
        That(captured is { Name: "123q", Message: "123", Group: 1, Memo: "123", Server: 0 },
            "真实1439按冒险团、留言、分组、备注解析，不把分组当服务器或留言当角色");
        foreach (var (hex, type) in new[] { ("24010000000000030000003132330400000059616E6700", (byte)36),
            ("01010000000000030000003132330400000059616E6702", (byte)1) })
        {
            byte[] body = Convert.FromHexString(hex);
            That(ChatRequests.TryParse(body, out var request) && request.Type == type && request.TargetName == "Yang" && request.Text == "123",
                "真实右键私聊/1对1请求按名字严格寻址");
            That(!ChatRequests.TryParse(body[..^1], out _) && !ChatRequests.TryParse([..body, 0], out _), "名字和服务器尾不能截断或增加未知字段");
        }
        string root = Path.Combine(Path.GetTempPath(), "s5a9-social-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var auth = new AuthenticationService(new AuthenticationRepository(path));
            var owners = new AccountOwners();
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("social.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            auth.Create("sociala", "fixture-password"); auth.Create("socialb", "fixture-password");
            using var a = owners.Acquire("sociala", () => { });
            using var b = owners.Acquire("socialb", () => { });
            repo.Create(a, new("SocialA", point, point)); repo.Create(b, new("SocialB", point, point));
            var sa = new GameSession(repo, towns, a); sa.Select(0); sa.MarkReady();
            var sb = new GameSession(repo, towns, b); sb.Select(0); sb.MarkReady();
            var actor = sa.CaptureSocialActor()!; var other = sb.CaptureSocialActor()!;
            var noParty = sb.PublicCharacter(other.Character.Id)!;
            That(noParty.PartyId == ushort.MaxValue, "未组队目标的菜单状态为空队伍哨兵");
            var solo = sb.SetSoloParty(PartyRequests.Set(Convert.FromHexString("000304000000000500000000000007")))!;
            var withParty = sb.PublicCharacter(other.Character.Id)!;
            That(withParty.PartyId == solo.Party.Id && !noParty.IsUnchanged(),
                "菜单使用Session唯一单人队伍状态，创建队伍使旧菜单快照失效");
            sb.LeaveSoloParty();
            That(!withParty.IsUnchanged() && sb.PublicCharacter(other.Character.Id)!.PartyId == ushort.MaxValue,
                "离队后排队的旧菜单不能恢复已清除的队伍标记");
            var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
            void Execute(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            long Scalar(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
            That(repo.ChangeCharacterLink(actor, "SocialB", false, false, "", now).Accepted &&
                repo.CharacterLinks(other.Character.Id, false).Length == 0, "普通好友只修改添加者单向关系");
            That(repo.ChangeCharacterLink(actor, "SocialB", false, false, "", now).Accepted &&
                repo.CharacterLinks(actor.Character.Id, false).Length == 1, "重复添加普通好友幂等");
            That(!repo.ChangeCharacterLink(actor, "SocialA", false, false, "", now).Accepted &&
                !repo.ChangeCharacterLink(actor, "Absent", false, false, "", now).Accepted, "自己与不存在的好友拒绝");
            That(repo.ChangeCharacterLink(other, "SocialA", true, false, "备注", now).Accepted &&
                repo.IsBlocked(other.Character.Id, actor.Character.Id) && repo.BlockingCharacters(actor.Character.Id).SetEquals([other.Character.Id]),
                "黑名单真实保存并供消息过滤");
            That(repo.UpdateSocialMemo(other, "SocialA", "修改", true).Accepted &&
                repo.CharacterLinks(other.Character.Id, true).Single().Memo == "修改" &&
                !repo.UpdateSocialMemo(other, "Absent", "", true).Accepted, "备注只修改真实关系，缺失不能返回成功");
            var online = new OnlineSessionDirectory(); online.Register(a, sa); online.Register(b, sb);
            NamedChatMessage? received = null;
            sb.ChatNoticeArrived = notice => received = notice.Named;
            var named = new NamedWhisperService(online, repo);
            var send = new ChatSend(36, 1, 0, 2, "hi") { TargetName = "SocialB" };
            That(named.Send(sa, send).Rejection == "blacklisted" && received is null, "被拉黑者1对1消息不投递");
            repo.ChangeCharacterLink(other, "SocialA", true, true, "", now);
            That(named.Send(sa, send).Accepted && received is { Name: "SocialA", Recipient: "SocialB", Text: "hi" }, "解除后按名字只投目标");
            sb.ReturnToRoster();
            That(named.Send(sa, send).TargetOffline, "切角中的旧角色不继续接收");
            sb.Select(0); sb.MarkReady(); other = sb.CaptureSocialActor()!;
            var catalog = AdventureCoreTests.SyntheticCatalog();
            repo.InitializeAdventure(a, catalog, now); repo.InitializeAdventure(b, catalog, now);
            Execute("UPDATE account_adventures SET name=CASE account WHEN 'sociala' THEN 'TeamA' ELSE 'TeamB' END,name_initialized=1");
            That(!repo.RequestAccountFriend(actor, "Absent", "申请", 1, "备注", now).Accepted, "不存在的冒险团申请拒绝");
            repo.ChangeCharacterLink(other, "SocialA", true, false, "", now);
            That(!repo.RequestAccountFriend(actor, "TeamB", "申请", 1, "备注", now).Accepted, "被拉黑者不能发送冒险团好友邀请");
            repo.ChangeCharacterLink(other, "SocialA", true, true, "", now);
            foreach (bool cancel in new[] { false, true })
            {
                That(repo.RequestAccountFriend(actor, "TeamB", "待处理", 0, "", now).Accepted,
                    "拒绝/撤销前先建立真实申请");
                That(repo.ResolveAccountFriend(cancel ? actor : other, cancel ? "TeamB" : "TeamA", false, false, cancel).Accepted &&
                    repo.AccountRequests(a.Account).Length == 0 && repo.AccountRequests(b.Account).Length == 0 &&
                    repo.AccountFriends(a.Account).Length == 0 && repo.AccountFriends(b.Account).Length == 0,
                    "拒绝/撤销消费双方待处理列表，不建立好友关系");
            }
            That(repo.RequestAccountFriend(actor, "TeamB", "这是申请留言不是角色名称", 1, "发起方备注", now).Accepted &&
                repo.AccountFriends(a.Account).Length == 0 && repo.AccountRequests(b.Account) is [{ Outgoing: false }], "申请不能提前建立好友");
            var restoredRequest = new CharacterRepository(path).AccountRequests(b.Account).Single();
            That(restoredRequest.Created == now.ToUnixTimeSeconds(), "重开库保留真实申请创建时间，不以协议截止时间覆盖存档");
            byte[] pending = SocialPackets.Requests([restoredRequest with { Message = "hi" }]);
            Bytes(pending, Convert.FromHexString("0100050000005465616D410200000068697FAEFE7F"),
                "1377截止时间为time32本地转换保留余量，不发送创建时间或贴近上限的值");
            int deadline = BitConverter.ToInt32(pending, pending.Length - 4);
            That(deadline > now.ToUnixTimeSeconds() && (long)deadline + 14 * 3600 < int.MaxValue,
                "通知截止时间仍有效，UTC+14本地时间中间值也不溢出");
            if (OperatingSystem.IsWindows())
            {
                int[] local = new int[9];
                That(LocalTime32(local, ref deadline) == 0 && MakeTime32(local) == deadline,
                    "实际Windows CRT的localtime32_s→mktime32往返保留截止时间，不能只比较整数大小");
            }
            That(!repo.ResolveAccountFriend(actor, "TeamB", true, false, false).Accepted, "发起者不能代替接收者接受");
            Execute("CREATE TRIGGER fail_social BEFORE INSERT ON account_social_links WHEN NEW.owner_account='socialb' BEGIN SELECT RAISE(ABORT,'fixture'); END");
            Reject<SqliteException>(() => repo.ResolveAccountFriend(other, "TeamA", true, false, false), "双向关系第二行失败整笔回滚");
            That(repo.AccountFriends(a.Account).Length == 0 && repo.AccountRequests(b.Account).Length == 1, "失败保留原申请且双方均无好友");
            Execute("DROP TRIGGER fail_social");
            That(new CharacterRepository(path).ResolveAccountFriend(other, "TeamA", true, false, false, "伙伴", 4).Accepted && repo.AccountFriends(a.Account).Length == 1 &&
                repo.AccountFriends(b.Account).Length == 1 && repo.AccountRequests(a.Account).Length == 0, "接受双方关系和消费申请同事务");
            That(repo.AccountFriends(b.Account).Single() is { Memo: "伙伴", Group: 4 } &&
                repo.AccountFriends(a.Account).Single() is { Memo: "发起方备注", Group: 1 },
                "重开后接受仍保存双方独立备注和分组，留言不用于目标角色校验");
            That(new CharacterRepository(path).AccountFriends(a.Account).Length == 1, "重开恢复真实社交关系");
            repo.Delete(b, repo.LoadDeletionTarget(b, 0));
            That(repo.CharacterLinks(actor.Character.Id, false).Length == 0 && Scalar("SELECT count(*) FROM account_social_links") == 2,
                "删角清理两端角色关系，同时保留账号好友");
            That(repo.AccountFriends(a.Account) is [{ Person.Id: 0, Person.Adventure: "TeamB" }] &&
                repo.ResolveAccountFriend(sa.CaptureSocialActor()!, "TeamB", false, true, false).Accepted && repo.AccountFriends(a.Account).Length == 0,
                "对方删尽角色后冒险团好友仍可展示并删除");
            Execute("DROP TABLE account_social_requests");
            Execute(SocialSchema.LegacyRequestSql);
            Execute("INSERT INTO account_social_requests VALUES('sociala','socialb','旧申请',123); PRAGMA user_version=88; ALTER TABLE account_social_links ADD COLUMN unexpected INTEGER");
            Reject<InvalidDataException>(() => new CharacterRepository(path), "v89晚期结构验证失败回滚新增列与版本");
            That(Scalar("PRAGMA user_version") == 88 && Scalar("SELECT count(*) FROM pragma_table_info('account_social_requests')") == 4 &&
                Scalar("SELECT created FROM account_social_requests") == 123, "升级失败保留v88完整申请");
            Execute("ALTER TABLE account_social_links DROP COLUMN unexpected");
            _ = new CharacterRepository(path);
            That(Scalar("PRAGMA user_version") == SqliteMigrations.CurrentVersion && Scalar("SELECT group_id FROM account_social_requests") == 0 &&
                Scalar("SELECT count(*) FROM account_social_requests WHERE message='旧申请' AND memo='' AND created=123") == 1,
                "v88申请原样升级到当前版本，未知旧偏好默认无组和空备注");
            Execute("DROP TABLE character_social_links; DROP TABLE account_social_requests; DROP TABLE account_social_links; PRAGMA user_version=87; CREATE VIEW account_social_requests AS SELECT 1 AS collision");
            Reject<InvalidDataException>(() => new CharacterRepository(path), "撞名视图拒绝升级");
            That(Scalar("PRAGMA user_version") == 87 && Scalar("SELECT count(*) FROM sqlite_schema WHERE name='character_social_links'") == 0,
                "迁移失败回滚全部新表和版本");
            Execute("DROP VIEW account_social_requests");
            _ = new CharacterRepository(path);
            That(Scalar("PRAGMA user_version") == SqliteMigrations.CurrentVersion && Scalar("SELECT count(*) FROM character_social_links") == 0,
                "v87原库升级只创建空关系，不伪造好友");
            That(Scalar("SELECT count(*) FROM sqlite_schema WHERE name='player_trade_receipts'") == 0,
                "独立社交迁移不创建交易表");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - before} 社交协议、持久化、屏蔽、原子回滚及迁移断言");
    }

    [DllImport("msvcrt.dll", EntryPoint = "_localtime32_s", CallingConvention = CallingConvention.Cdecl)]
    static extern int LocalTime32([Out] int[] local, ref int timestamp);
    [DllImport("msvcrt.dll", EntryPoint = "_mktime32", CallingConvention = CallingConvention.Cdecl)]
    static extern int MakeTime32([In, Out] int[] local);
}
