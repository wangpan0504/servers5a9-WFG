using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildManagementTests
{
    sealed class Clock : TimeProvider
    {
        public long Millis { get; set; } = 1_700_000_000_000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Millis);
    }

    public static void RunCore()
    {
        int before = Count;
        That(GuildRoleRules.NormalizeGrade(2, 2, 0) == 1 && GuildRoleRules.NormalizeGrade(3, 2, 0) == 4 &&
            GuildRoleRules.DefaultPermissions(1) == 524287 && GuildRoleRules.DefaultPermissions(2) == 0x4410 &&
            GuildRoleRules.DefaultName(5) == "", "存量0级仅真实会长映射1，普通成员4，5不是新会员");
        That(GuildRoleRules.CanKick(1, 4, 1u << 10) && !GuildRoleRules.CanKick(2, 2, 1u << 10) &&
            !GuildRoleRules.CanKick(2, 4, 0) && !GuildRoleRules.CanKick(1, 1, GuildRoleRules.PermissionMask),
            "踢人须权限及严格品级优先，不能移除会长或同级");
        var edge = new DateTimeOffset(2026, 10, 26, 6, 0, 0, TimeSpan.FromHours(8));
        That(GuildRoleRules.DepartureDeadline(edge.ToUnixTimeSeconds() - 1) == edge.ToUnixTimeSeconds() &&
            GuildRoleRules.DepartureDeadline(edge.ToUnixTimeSeconds()) == edge.AddDays(1).ToUnixTimeSeconds(),
            "主动退会限制准确跨越次日06点，不是固定24小时");
        That(GuildManagementRequests.TryTarget([0, 0, 0, 0, 0], true, out _, out string self) && self == "" &&
            !GuildManagementRequests.TryTarget([0, 0, 0, 0, 0], false, out _, out _) &&
            !GuildManagementRequests.TryText([1, 0, 0, 0, 0], 20, false, out _) &&
            !GuildManagementRequests.TryText([2, 0, 0, 0, 65], 20, false, out _),
            "本人空名只用于退会，非法编码及不匹配DSTR不能进入持久化");
        RunRepositoryAndInvitations();
        Console.WriteLine($"PASS {Count - before} 公会成员管理、邀请、权限与事务断言");
    }

    static void RunRepositoryAndInvitations()
    {
        string root = Path.Combine(Path.GetTempPath(), "guild-management-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var masterOwner = owners.Acquire("b-master", static () => { });
            using var memberOwner = owners.Acquire("b-member", static () => { });
            using var targetOwner = owners.Acquire("b-target", static () => { });
            var room = new Location(38, 1, 450, 234, 0);
            var master = repository.Create(masterOwner, new("BMaster", room, room));
            var member = repository.Create(memberOwner, new("BMember", room, room));
            var target = repository.Create(targetOwner, new("BTarget", room, room));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            void Sql(string text)
            {
                using var command = db.CreateCommand();
                command.CommandText = text;
                command.ExecuteNonQuery();
            }
            long Number(string text)
            {
                using var command = db.CreateCommand();
                command.CommandText = text;
                return Convert.ToInt64(command.ExecuteScalar());
            }
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                VALUES(1,'管理公会','management',{master.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix)
                VALUES(1,{master.Id},0,1000),(1,{member.Id},0,1000);
                """);
            var towns = new TownCatalog([], room, room, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var clock = new Clock();
            var masterSession = new GameSession(repository, towns, masterOwner, timeProvider: clock);
            var memberSession = new GameSession(repository, towns, memberOwner, timeProvider: clock);
            var targetSession = new GameSession(repository, towns, targetOwner, timeProvider: clock);
            masterSession.Select(master.Slot); masterSession.MarkReady();
            memberSession.Select(member.Slot); memberSession.MarkReady();
            targetSession.Select(target.Slot); targetSession.MarkReady();
            long assetsRevision = Number("SELECT sum(revision) FROM characters");
            var masterIdentity = masterSession.CaptureGuildManagement()!;
            var memberIdentity = memberSession.CaptureGuildManagement()!;
            var targetIdentity = targetSession.CaptureGuildManagement()!;
            That(repository.ReadGuildManagement(masterOwner, master.Id) is { Grade: 1, Permissions: 524287 } &&
                repository.ReadGuildManagement(memberOwner, member.Id) is { Grade: 4, Permissions: 0 } &&
                repository.ReadGuildManagement(targetOwner, member.Id) is null && Number("SELECT sum(grade) FROM guild_members") == 0,
                "权威查询限制账号，0级视图映射不回写旧成员数据");
            That(repository.ChangeGuildManagement(memberIdentity, new(GuildManagementAction.Notice, null, "越权"), 1700000000).Error == 36,
                "普通成员不能借公告入口编辑公会");
            var notice = repository.ChangeGuildManagement(masterIdentity, new(GuildManagementAction.Notice, null, "新公告"), 1700000000);
            That(notice.Success && notice.Revision == 1 &&
                repository.ChangeGuildManagement(masterIdentity, new(GuildManagementAction.Notice, null, "新公告"), 1700000000).Revision == 1,
                "公告实际变化才推进关系版本，重复保存保持幂等");
            Sql("CREATE TRIGGER fail_guild_notice BEFORE UPDATE OF notice ON guilds BEGIN SELECT RAISE(ABORT,'test'); END");
            Reject<SqliteException>(() => repository.ChangeGuildManagement(masterIdentity,
                new(GuildManagementAction.Notice, null, "失败公告"), 1700000000), "真实SQL失败由外层事务回滚");
            Sql("DROP TRIGGER fail_guild_notice");
            That(repository.ReadGuildManagement(masterOwner, master.Id) is { Revision: 1, Guild.Notice: "新公告" },
                "SQL失败后公告和关系版本均保留原提交");
            var kick = repository.ChangeGuildManagement(masterIdentity,
                new(GuildManagementAction.Leave, new(member.Id, memberOwner.Account, member.Name)), 1700000000);
            That(kick.Success && !kick.Voluntary && Number("SELECT count(*) FROM guild_membership_cooldowns") == 0 &&
                memberSession.RefreshGuildManagement(repository, 1) is { State: null } && memberSession.Selected?.Guild is null,
                "踢人立即清理会话资格且不增加主动退会限制");

            var online = new OnlineSessionDirectory();
            online.Register(masterOwner, masterSession);
            online.Register(targetOwner, targetSession);
            using var invitations = new GuildInvitations();
            var service = new GuildManagementService(repository, online, invitations, new GuildPermitRegistry(), 3, clock);
            var events = new List<GuildManagementNotice>();
            targetSession.GuildManagementNoticeArrived = events.Add;
            masterSession.GuildManagementNoticeArrived = events.Add;
            masterIdentity = masterSession.CaptureGuildManagement()!;
            That(service.Offer(masterIdentity, 0, target.Name) == 0 && events.Last().Kind == GuildManagementNoticeKind.Invite,
                "普通邀请通过原连接专用入口投递，不使用GM注入");
            var invite = events.Last().Invitation!;
            using (var reader = new BinaryReader(new MemoryStream(GuildManagementPackets.Invitation(invite))))
            {
                string Text() => CharacterName.Encoding.GetString(reader.ReadBytes(reader.ReadInt32()));
                That(Text() == "管理公会" && Text() == "BMaster" && Text() == "" && reader.ReadUInt32() == 1 &&
                    reader.ReadUInt16() == 1 && reader.ReadByte() == 0 && reader.ReadUInt32() == 1 && Text() == "BMaster" &&
                    reader.ReadUInt32() == 300 && reader.BaseStream.Position == reader.BaseStream.Length,
                    "邀请包按原生顺序完整消费，邀请人是第二个字符串");
            }
            clock.Millis += 15_000;
            That(service.Offer(masterIdentity, 0, target.Name) == 103 && service.Answer(targetIdentity, true).Error == 103 &&
                Number($"SELECT count(*) FROM guild_members WHERE character_id={target.Id}") == 0,
                "过期墓碑阻止覆盖旧窗口，迟到接受不能入会");
            That(service.Offer(masterIdentity, 0, target.Name) == 0 && service.Answer(targetIdentity, true).Success &&
                Number($"SELECT grade FROM guild_members WHERE character_id={target.Id}") == 4,
                "原窗口答复消费后新邀请可用，接受在事务内写普通成员4");
            var leave = repository.ChangeGuildManagement(targetIdentity, new(GuildManagementAction.Leave, null), clock.Millis / 1000);
            long until = Number($"SELECT until_unix FROM guild_membership_cooldowns WHERE character_id={target.Id}");
            That(leave.Success && leave.Voluntary && until == GuildRoleRules.DepartureDeadline(clock.Millis / 1000) &&
                repository.ChangeGuildManagement(targetIdentity, new(GuildManagementAction.Leave, null), clock.Millis / 1000 + 5).Error != 0 &&
                Number($"SELECT until_unix FROM guild_membership_cooldowns WHERE character_id={target.Id}") == until,
                "主动退会只记录一次06点限制，重放失败不延长期限");
            That(Number("SELECT sum(gold) FROM characters") == 0 &&
                Number("SELECT sum(revision) FROM characters") == assetsRevision,
                "成员、公告、邀请和退会均不修改角色资产或资产revision");
            masterSession.End(); memberSession.End(); targetSession.End();
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
