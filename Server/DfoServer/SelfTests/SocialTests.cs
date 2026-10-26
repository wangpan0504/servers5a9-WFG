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
            2, 10, true);
        Bytes(SocialPackets.AccountFriends([friend]),
            Convert.FromHexString("0102000205000000526F6C65424B01020A00050000005465616D42040000006D656D6F00000000000300"),
            "1376行首大区2不能写角色42，角色名在前、冒险团唯一键在后");
        Bytes(SocialPackets.AccountFriends([friend with { Online = false, Channel = 0 }]).AsSpan(1, 3).ToArray(),
            [2, 0, 2], "离线行保留本服大区和频道服务器，不使用角色ID或离线哨兵覆盖大区");
        Bytes(ChatPackets.NamedMessage(new(36, 0, "A", 2, "hi") { AdventureName = "TeamA" }),
            Convert.FromHexString("2400010000004102020000006869050000005465616D410100000041"),
            "1对1正文后先读附加名字串，再读发送者作为会话对象，不能让接收端回复自己");
        var captured = SocialRequests.Account(Convert.FromHexString("0400000031323371030000003132330103000000313233"), 1439);
        That(captured is { Name: "123q", Message: "123", Group: 1, Memo: "123", Server: 0 },
            "真实1439按冒险团、留言、分组、备注解析，不把分组当服务器或留言当角色");
        foreach (var (hex, type) in new[] { ("24010000000000030000003132330400000059616E6700", (byte)36),
            ("01010000000000030000003132330400000059616E6702", (byte)1),
            ("29FFFF00000000040000003131313105000000636573686902", (byte)41) })
        {
            byte[] body = Convert.FromHexString(hex);
            That(ChatRequests.TryParse(body, out var request) && request.Type == type &&
                request.TargetName == (type == 41 ? "ceshi" : "Yang") && request.Text == (type == 41 ? "1111" : "123"),
                "真实右键私聊/1对1请求按名字严格寻址");
            That(!ChatRequests.TryParse(body[..^1], out _) && !ChatRequests.TryParse([..body, 0], out _), "名字和服务器尾不能截断或增加未知字段");
        }
        That(!ChatRequests.TryParse(Convert.FromHexString("24000000000000030000003131310000000002"), out _),
            "旧下行错位产生的空目标仍拒绝，不猜测或复用上一次聊天对象");
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
            That(named.Send(sa, send).Accepted && received is { Name: "SocialA", Text: "hi" }, "解除后按名字只投目标，消息保留发送者身份");
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
            var parties = new DfoServer.Game.Party.PartyService(repo);
            var publicIdentity = sa.PublicCharacter(actor.Character.Id)!;
            ushort noPartyId = parties.PartyId(actor.Character.Id);
            That(noPartyId == ushort.MaxValue, "未组队目标的菜单状态为空队伍哨兵");
            var settings = DfoServer.Game.Party.PartySettings.Default with { TitleCode = 4, Title = "", Capacity = 1 };
            That(!parties.SetInformation(sa, 1, settings).Accepted && parties.SetInformation(sa, 0, settings).Accepted &&
                parties.Current(sa) is { Members.Length: 1, Settings.Capacity: 1 } && !parties.Invite(sa, sb, 42).Accepted,
                "修改不能凭空建队，显式创建单人队并实际限制邀请人数");
            ushort createdPartyId = parties.PartyId(actor.Character.Id);
            That(createdPartyId == parties.Current(sa)!.Id && createdPartyId != noPartyId && publicIdentity.IsUnchanged(),
                "菜单从唯一队伍服务读取新ID，旧菜单队伍校验失效；纯组队变化不使公开资料快照失效");
            That(parties.SetInformation(sa, 0, settings) is { Accepted: true, Notices.Length: 0 } &&
                !parties.SetInformation(sa, 1, settings with { Capacity = 0 }).Accepted,
                "创建重发幂等，非法容量不得提交");
            That(parties.SetInformation(sa, 1, settings with { Capacity = 2 }).Accepted, "队长可以扩大已有队伍容量");
            That(parties.Invite(sa, sb, 42).Accepted && parties.SetInformation(sa, 1, settings).Accepted &&
                !parties.Respond(sb, actor.Character.Id, 0, true).Accepted && parties.Current(sa)!.Members.Length == 1,
                "邀请后队长收紧人数上限，迟到的接受不得超过最新容量");
            parties.SetInformation(sa, 1, settings with { Capacity = 2 });
            That(parties.SetInformation(sa, 1, settings with { Capacity = 2, Option = null }).Accepted &&
                parties.Current(sa)!.Settings.Option == 7, "快捷修改省略招募选项时保留原设置");
            That(!parties.Respond(sb, actor.Character.Id, 0, true).Accepted, "没有待处理邀请不能伪造组队接受");
            var invite = parties.Invite(sa, sb, 42);
            That(invite.Accepted && invite.Notices is [{ Kind: DfoServer.Game.Party.PartyNoticeKind.Invite }], "邀请绑定双方当前会话");
            That(!parties.Respond(sa, actor.Character.Id, 0, true).Accepted, "发起者不能代替目标消费邀请");
            var joined = parties.Respond(sb, actor.Character.Id, 0, true);
            That(joined.Accepted && parties.HasMultipleMembers(sa) && parties.PartyId(actor.Character.Id) == parties.PartyId(other.Character.Id),
                "接受后双方归同一真实队伍");
            That(!parties.SetInformation(sb, 1, settings with { Capacity = 4 }).Accepted &&
                !parties.SetInformation(sa, 1, settings).Accepted && parties.Current(sa)!.Settings.Capacity == 2,
                "普通队员不能改队伍设置，队长不能把容量缩到现有人数以下");
            sa.HasMultiplePartyMembers = () => parties.HasMultipleMembers(sa);
            That(sa.BeginDungeonSelection() is null && sa.BeginClassExperienceSelection() is null && sa.Phase == SessionPhase.Town,
                "多人队伍不能进入现有单人普通选图或职业体验");
            That(parties.Respond(sb, actor.Character.Id, 0, true) is { Accepted: true, Notices.Length: 0 } && !parties.Leave(sb, 0).Accepted,
                "同队接受重发幂等且普通队员不能踢队长");
            var originalParty = parties.Current(sa)!;
            var scene = parties.SceneChanged(sb);
            That(scene is [{ Kind: DfoServer.Game.Party.PartyNoticeKind.SceneChanged }] &&
                ReferenceEquals(scene[0].Recipient.Actor.Session, sb) && ReferenceEquals(scene[0].Party, originalParty),
                "切区只恢复当前接收者，保留队伍对象、成员引用与连接，不向静止队友重发入队投影");
            That(!parties.TransferLeader(sb, 0).Accepted && !parties.TransferLeader(sa, 0).Accepted &&
                !parties.TransferLeader(sa, 4).Accepted && !parties.TransferLeader(sa, 3).Accepted &&
                !parties.TransferLeader(sa, 1, true).Accepted && ReferenceEquals(parties.Current(sa), originalParty),
                "普通队员、自身槽、越界、空槽及活动占用不能改变队长或队伍快照");
            var transferred = parties.TransferLeader(sa, 1);
            var nextParty = parties.Current(sb)!;
            That(transferred.Accepted && transferred.Notices.Length == 2 &&
                transferred.Notices.All(n => n.Kind == DfoServer.Game.Party.PartyNoticeKind.LeaderChanged && n.Guard?.Invoke() == true) &&
                nextParty.Leader == 1 && nextParty.Id == originalParty.Id && nextParty.Revision > originalParty.Revision &&
                ReferenceEquals(nextParty.Members, originalParty.Members) && nextParty.Settings == originalParty.Settings &&
                scene[0].Guard?.Invoke() == false,
                "委任仅提交队长和版本；不重建ID、成员槽位或设置，旧场景恢复不能覆盖新队长");
            That(!parties.TransferLeader(sa, 1).Accepted && !parties.SetInformation(sa, 1, settings with { Capacity = 4 }).Accepted &&
                !parties.Leave(sa, 1).Accepted && parties.SetInformation(sb, 1, nextParty.Settings).Accepted,
                "委任后原队长立即失去委任、修改及踢人权限，新队长获得设置权限");
            That(parties.TransferLeader(sb, 0).Accepted && transferred.Notices.All(n => n.Guard?.Invoke() == false),
                "新队长可委任回来，迟到的旧队长投影失效");
            var left = parties.Leave(sb);
            That(left.Accepted && !parties.HasMultipleMembers(sa) && parties.PartyId(other.Character.Id) == ushort.MaxValue &&
                joined.Notices.Where(n => n.Kind == DfoServer.Game.Party.PartyNoticeKind.Updated).All(n => n.Guard?.Invoke() == false),
                "离队更新名册并使在途旧队伍快照失效");
            parties.Detach(sa);
            That(parties.PartyId(actor.Character.Id) == ushort.MaxValue && parties.PartyId(actor.Character.Id) != createdPartyId,
                "退出后菜单恢复空队伍哨兵，在途旧菜单不能恢复已清除的队伍标记");
            That(sa.BeginClassExperienceSelection() is not null && sa.ReturnFromDungeonSelection(false) is not null,
                "离队后恢复单人职业体验选图资格");
            invite = parties.Invite(sa, sb, 0);
            sb.ReturnToRoster(); sb.Select(0); sb.MarkReady();
            That(!parties.Respond(sb, actor.Character.Id, 0, true).Accepted && invite.Notices[0].Guard?.Invoke() == false,
                "换角epoch失效后不能接受旧邀请或收到迟到邀请");
            parties.Invite(sa, sb, 0);
            That(parties.Respond(sb, actor.Character.Id, 0, true).Accepted, "当前会话仍可重新组队");
            sb.ReturnToRoster(); sb.Select(0); sb.MarkReady();
            That(!parties.TransferLeader(sa, 1).Accepted && parties.Current(sa)!.Leader == 0,
                "目标换角后即使旧槽位尚未清理，也不能把队长委任给新epoch");
            parties.Detach(sa);
            parties.Detach(sb);
            other = sb.CaptureSocialActor()!;
            That(AccountOwners.Lease.ExecuteTogether(a, b, () => a.Execute(() => b.Execute(() => 7))) == 7,
                "固定双角色入口允许已持两把锁的明确重入");
            Reject<InvalidOperationException>(() => a.Execute(() => AccountOwners.Lease.ExecuteTogether(a, b, () => true)),
                "不能在单账号锁内升级为双账号锁");
            repo.Delete(b, repo.LoadDeletionTarget(b, 0));
            That(repo.CharacterLinks(actor.Character.Id, false).Length == 0 && Scalar("SELECT count(*) FROM account_social_links") == 2,
                "删角清理两端角色关系，同时保留账号好友");
            That(repo.AccountFriends(a.Account) is [{ Person.Id: 0, Person.Adventure: "TeamB" }] &&
                repo.ResolveAccountFriend(sa.CaptureSocialActor()!, "TeamB", false, true, false).Accepted && repo.AccountFriends(a.Account).Length == 0,
                "对方删尽角色后冒险团好友仍可展示并删除");
            Execute("DROP TABLE character_epic_production; DROP TABLE quest_eligibility_mail_dispatches; DROP TABLE character_pvp_training_skills; DROP TABLE account_pvp_private_characters; DROP TABLE character_operation_hope_reward_receipts");
            Execute("DROP TABLE account_social_requests");
            Execute(SocialSchema.LegacyRequestSql);
            Execute("INSERT INTO account_social_requests VALUES('sociala','socialb','旧申请',123); ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=90; ALTER TABLE account_social_links ADD COLUMN unexpected INTEGER");
            Reject<InvalidDataException>(() => new CharacterRepository(path), "v91晚期结构验证失败回滚新增列与版本");
            That(Scalar("PRAGMA user_version") == 90 && Scalar("SELECT count(*) FROM pragma_table_info('account_social_requests')") == 4 &&
                Scalar("SELECT created FROM account_social_requests") == 123, "升级失败保留v90完整申请");
            Execute("ALTER TABLE account_social_links DROP COLUMN unexpected");
            _ = new CharacterRepository(path);
            That(Scalar("PRAGMA user_version") == SqliteMigrations.CurrentVersion && Scalar("SELECT group_id FROM account_social_requests") == 0 &&
                Scalar("SELECT count(*) FROM account_social_requests WHERE message='旧申请' AND memo='' AND created=123") == 1,
                "v90申请原样升级到当前版本，未知旧偏好默认无组和空备注");
            Execute("DROP TABLE character_epic_production; DROP TABLE quest_eligibility_mail_dispatches; DROP TABLE character_pvp_training_skills; DROP TABLE account_pvp_private_characters; DROP TABLE character_operation_hope_reward_receipts; DROP TABLE character_social_links; DROP TABLE account_social_requests; DROP TABLE account_social_links; ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=87; CREATE VIEW account_social_requests AS SELECT 1 AS collision");
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
