using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Social;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AccountFriendGroupTests
{
    public static void RunCore()
    {
        int before = Count;
        AccountFriendGroup[] groups = [new(7, "一二三四五"), new(0, "Team B"), new(4, "!"), new(2, "好友")];
        byte[] Body(AccountFriendGroup[] rows)
        {
            byte[] bytes = new byte[61];
            bytes[0] = 0x71; bytes[12] = 0xff; // opaque前缀可以非零。
            for (int i = 0; i < rows.Length; i++)
            {
                bytes[13 + i] = rows[i].Icon;
                CharacterName.Encoding.GetBytes(rows[i].Name).CopyTo(bytes, 17 + 11 * i);
            }
            return bytes;
        }
        byte[] body = Body(groups);
        That(SocialRequests.AccountGroups(body).SequenceEqual(groups), "1683完整61B按13B opaque、四图标与四定长槽解析，10B中文名可用");
        body[60] = 0x81;
        That(SocialRequests.AccountGroups(body).SequenceEqual(groups), "名称NUL之后的未解释尾字节不参与编码或业务校验");
        foreach (int size in new[] { 0, 48, 60, 62 })
            Reject<InvalidDataException>(() => SocialRequests.AccountGroups(new byte[size]), "1683长度错误不能截断或增加正文");
        foreach (var (index, value) in new[] { (13, (byte)8), (17, (byte)0x81) })
        {
            byte[] invalid = Body(groups); invalid[index] = value;
            if (index == 17) invalid[index + 1] = 0;
            Reject<InvalidDataException>(() => SocialRequests.AccountGroups(invalid), "非法图标或不完整CP936拒绝");
        }
        byte[] noNul = Body(groups); Array.Fill(noNul, (byte)'A', 17, 11);
        Reject<InvalidDataException>(() => SocialRequests.AccountGroups(noNul), "11B名称槽必须有NUL");
        foreach (string invalid in new[] { "", " lead", "trail ", "a'b", "a%b", "a\tb", "Team B" })
        {
            var rows = groups.ToArray(); rows[0] = new(1, invalid);
            Reject<InvalidDataException>(() => SocialRequests.AccountGroups(Body(rows)), "空名、已证非法字符和重复组名拒绝");
        }
        That(!AccountFriendGroup.Valid([.. groups[..3]]) &&
            !AccountFriendGroup.Valid([groups[0] with { Name = "12345678901" }, .. groups[1..]]) &&
            !AccountFriendGroup.Valid([groups[0] with { Name = "😀" }, .. groups[1..]]),
            "领域存储边界拒绝缺组、超10B和不可编码名称");
        Bytes(SocialPackets.AccountGroups(groups), Convert.FromHexString(
            "070A000000D2BBB6FEC8FDCBC4CEE500060000005465616D20420401000000210204000000BAC3D3D1"),
            "1702独立字节预期保留四组槽序、真实图标与CP936名称，无数量头");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-account-groups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var auth = new AuthenticationService(new AuthenticationRepository(path));
            auth.Create("groupsa", "fixture-password"); auth.Create("groupsb", "fixture-password");
            var owners = new AccountOwners();
            using var a = owners.Acquire("groupsa", () => { });
            using var b = owners.Acquire("groupsb", () => { });
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("groups.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            repo.Create(a, new("GroupsA", point, point)); repo.Create(a, new("GroupsAlt", point, point));
            repo.Create(b, new("GroupsB", point, point));
            var sa = new GameSession(repo, towns, a); sa.Select(0); sa.MarkReady();
            var sb = new GameSession(repo, towns, b); sb.Select(0); sb.MarkReady();
            var actor = sa.CaptureSocialActor()!;
            void Execute(string sql) { using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            long Scalar(string sql) { using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
            That(repo.AccountGroups(actor).SequenceEqual(AccountFriendGroup.Defaults()) && Scalar("SELECT count(*) FROM account_social_groups") == 0,
                "旧账号只读缺配置返回原四个默认组，不建默认行");
            repo.SaveAccountGroups(actor, groups);
            That(repo.ChangeCharacterLink(actor, "GroupsB", false, false, "", DateTimeOffset.UnixEpoch).Accepted,
                "迁移基线保留真实角色好友关系");
            That(new CharacterRepository(path).AccountGroups(actor).SequenceEqual(groups) &&
                repo.AccountGroups(sb.CaptureSocialActor()!).SequenceEqual(AccountFriendGroup.Defaults()), "新仓储重开恢复四组，账号隔离");
            Execute("CREATE TRIGGER fail_groups BEFORE INSERT ON account_social_groups WHEN NEW.slot=3 BEGIN SELECT RAISE(ABORT,'fixture'); END");
            var changed = groups.Select(row => row with { Icon = 1, Name = row.Name + "x" }).ToArray();
            changed[0] = new(1, "Changed");
            Reject<SqliteException>(() => repo.SaveAccountGroups(actor, changed), "最后一组SQL失败拒绝成功");
            That(repo.AccountGroups(actor).SequenceEqual(groups), "删除旧组及前三行插入均与最后一行失败整体回滚");
            Execute("DROP TRIGGER fail_groups");
            sa.ReturnToRoster(); sa.Select(1); sa.MarkReady();
            That(!actor.IsCurrent(), "换角后旧四组结果发送守卫失效");
            Reject<InvalidOperationException>(() => repo.SaveAccountGroups(actor, changed), "旧选角epoch不得写新角色的账号配置");
            actor = sa.CaptureSocialActor()!;
            That(repo.AccountGroups(actor).SequenceEqual(groups), "同账号换角仍恢复账号四组");
            using var replacement = owners.Acquire("groupsa", () => { });
            Reject<InvalidOperationException>(() => repo.SaveAccountGroups(actor, changed), "旧owner不得提交四组");
            Reject<InvalidOperationException>(() => repo.AccountGroups(actor), "旧owner不得取得新会话的四组回放");
            var reconnected = new GameSession(new CharacterRepository(path), towns, replacement); reconnected.Select(0); reconnected.MarkReady();
            That(repo.AccountGroups(reconnected.CaptureSocialActor()!).SequenceEqual(groups), "重连新owner回放真实四组");

            // 仅回退本次新增表，代表真实前一版本；历史社交数据必须原样保留。
            Execute("INSERT INTO account_social_links VALUES('groupsa','groupsb',100,'历史好友',3),('groupsb','groupsa',101,'对方备注',4); CREATE TRIGGER history_social AFTER INSERT ON account_social_links BEGIN SELECT 1; END; INSERT INTO account_social_requests VALUES('groupsa','groupsb','历史申请',123,'历史备注',2); DROP TABLE account_social_groups; PRAGMA user_version=206; CREATE VIEW account_social_groups AS SELECT 1 AS collision");
            Reject<InvalidDataException>(() => new CharacterRepository(path), "v206未知同名视图拒绝升级");
            That(Scalar("PRAGMA user_version") == 206, "同名视图拒绝不推进版本");
            Execute("DROP VIEW account_social_groups; CREATE TABLE account_social_groups(account TEXT)");
            Reject<InvalidDataException>(() => new CharacterRepository(path), "v206未知同名表拒绝升级");
            Execute("DROP TABLE account_social_groups; ALTER TABLE account_social_links ADD COLUMN unexpected INTEGER");
            Reject<InvalidDataException>(() => new CharacterRepository(path), "迁移后晚期原社交结构核验失败");
            That(Scalar("PRAGMA user_version") == 206 && Scalar("SELECT count(*) FROM sqlite_schema WHERE name='account_social_groups'") == 0,
                "晚期失败整体回滚新表及版本");
            Execute("ALTER TABLE account_social_links DROP COLUMN unexpected");
            var upgraded = new CharacterRepository(path);
            That(Scalar("PRAGMA user_version") == SqliteMigrations.CurrentVersion &&
                Scalar("SELECT count(*) FROM account_social_requests WHERE created=123 AND message='历史申请' AND memo='历史备注' AND group_id=2") == 1 &&
                Scalar("SELECT count(*) FROM character_social_links") == 1 && Scalar("SELECT count(*) FROM account_social_links") == 2 &&
                Scalar("SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND name='history_social'") == 1 &&
                upgraded.AccountGroups(reconnected.CaptureSocialActor()!).SequenceEqual(AccountFriendGroup.Defaults()),
                "前一版本升级保留原申请与偏好，不伪造名称或配置");
            upgraded.SaveAccountGroups(reconnected.CaptureSocialActor()!, groups);
            That(new CharacterRepository(path).AccountGroups(reconnected.CaptureSocialActor()!).SequenceEqual(groups), "升级后保存与再次打开幂等");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} 账号四组协议、原子保存、owner与迁移断言");
    }
}
