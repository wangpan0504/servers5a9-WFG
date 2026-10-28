using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildGovernanceTests
{
    public static void RunCore()
    {
        int before = Count;
        RunParsers();
        RunRepository();
        Console.WriteLine($"PASS {Count - before} 公会治理字段选择、权限及真实事务断言");
    }

    static void RunParsers()
    {
        That(GuildGovernanceRequests.TryPermissions([2, 0, 0, 0, 0], out byte grade, out uint permissions) &&
            grade == 2 && permissions == 0 && !GuildGovernanceRequests.TryPermissions([2, 0], out _, out _),
            "职位权限严格读取一字节职位和完整四字节掩码，显式零值不丢失");
        That(GuildGovernanceRequests.TryRoleName([2, .. Text("副会长")], out grade, out string name) &&
            grade == 2 && name == "副会长" && !GuildGovernanceRequests.TryRoleName([2, 1, 0, 0, 0, 0x81], out _, out _),
            "职位名保持CP936完整编码，拒绝残缺双字节字符");
        byte[] settings = Settings(new(GuildGovernanceRules.Tags, "未选介绍", false, 5, 255));
        That(GuildGovernanceRequests.TrySettings(settings, out var parsed) && parsed is
            { Mask: GuildGovernanceRules.Tags, Promotion: "未选介绍", Webpage: false, Tags: 5, JoinMode: 255 } &&
            !GuildGovernanceRequests.TrySettings([.. settings, 0], out _) &&
            !GuildGovernanceRequests.TrySettings(settings[..^1], out _),
            "设置包保留所有字段与选择掩码，未选值留给领域层判断，正文不容多余字节");
        That(GuildGovernanceRequests.TryMemberGrade([2, .. Text("Member"), 3], out byte region, out name, out grade) &&
            region == 2 && name == "Member" && grade == 3 &&
            !GuildGovernanceRequests.TryMemberGrade([250, .. Text("Member"), 3], out _, out _, out _) &&
            !GuildGovernanceRequests.TryMemberGrade([2, .. Text(""), 3], out _, out _, out _),
            "成员品级请求保留区服及末尾职位，拒绝非法区服与空目标");
        byte[] callback = GuildGovernancePackets.MemberGrade(2, "Member", 3);
        Bytes(callback, [1, 0, 2, 6, 0, 0, 0, (byte)'M', (byte)'e', (byte)'m', (byte)'b', (byte)'e', (byte)'r', 3],
            "126成功回调包含额外通知字节，不能只回请求正文");
    }

    static void RunRepository()
    {
        string path = Path.Combine(Path.GetTempPath(), "guild-governance-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var masterOwner = owners.Acquire("governance-master", () => { });
            using var peerOwner = owners.Acquire("governance-peer", () => { });
            using var deputyOwner = owners.Acquire("governance-deputy", () => { });
            using var foreignOwner = owners.Acquire("governance-other", () => { });
            var room = new Location(0, 0, 100, 100);
            var master = repository.Create(masterOwner, new("GovMaster", room, room));
            var peer = repository.Create(peerOwner, new("GovMember", room, room));
            var deputy = repository.Create(deputyOwner, new("GovDeputy", room, room));
            var foreign = repository.Create(foreignOwner, new("GovForeign", room, room));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                VALUES(1,'治理公会','governance',$master,1600000000),(2,'其他公会','gov-other',$other,1600000000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix)
                VALUES(1,$master,0,1600000000),(1,$peer,4,1600000000),(1,$deputy,2,1600000000),(2,$other,0,1600000000);
                """;
            command.Parameters.AddWithValue("$master", master.Id);
            command.Parameters.AddWithValue("$peer", peer.Id);
            command.Parameters.AddWithValue("$deputy", deputy.Id);
            command.Parameters.AddWithValue("$other", foreign.Id);
            command.ExecuteNonQuery();
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), room, room,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            GuildManagementIdentity Identity(AccountOwners.Lease owner, Character character) =>
                new(new GameSession(repository, towns, owner), owner, character.Id, character.Name, 1, new(2, 10), () => owner.IsCurrent);
            var actor = Identity(masterOwner, master);
            var ordinary = Identity(peerOwner, peer);
            var delegateActor = Identity(deputyOwner, deputy);
            var target = new GuildManagementTarget(peer.Id, peerOwner.Account, peer.Name);
            GuildManagementCommit Change(GuildManagementIdentity who, GuildGovernanceChange change) =>
                repository.ChangeGuildGovernance(who, change, 1700000000);
            GuildManagementState State() => repository.ReadGuildManagement(masterOwner, master.Id)!;
            string Snapshot()
            {
                command.CommandText = """
                    SELECT COALESCE(group_concat(value,'|'),'') FROM (
                    SELECT 's:'||guild_id||':'||revision||':'||promotion||':'||tags||':'||join_mode||':'||webpage value FROM guild_settings
                    UNION ALL SELECT 'r:'||guild_id||':'||grade||':'||permissions||':'||name FROM guild_roles
                    UNION ALL SELECT 'g:'||guild_id||':'||master_character_id FROM guilds
                    UNION ALL SELECT 'm:'||guild_id||':'||character_id||':'||grade FROM guild_members
                    UNION ALL SELECT 'c:'||id||':'||account||':'||revision||':'||gold||':'||name FROM characters ORDER BY value)
                    """;
                return (string)command.ExecuteScalar()!;
            }

            var recommendation = new GuildGovernanceChange(GuildGovernanceAction.RecommendedChannel,
                RecommendedChannel: new(new(2, 10), 22, "[normal]"));
            That(Change(ordinary, recommendation).Error == 19 &&
                Change(actor, recommendation with { RecommendedChannel = new(new(2, 10), 23, "[raid]") }).Error == 7 &&
                Change(actor, recommendation with { RecommendedChannel = new(new(2, 11), 22, "[normal]") }).Error == 7,
                "推荐频道只允许真实会长、受支持类型及当前会话身份");
            var recommended = Change(actor, recommendation);
            That(recommended.Success && State().RecommendedChannel == actor.Channel &&
                Change(actor, recommendation).Revision == recommended.Revision,
                "普通22频道推荐持久保存，相同推荐重复提交不增加版本");
            command.CommandText = "CREATE TRIGGER channel_fail BEFORE UPDATE ON guild_recommended_channels BEGIN SELECT RAISE(ABORT,'channel rollback'); END";
            command.ExecuteNonQuery();
            var moved = actor with { Channel = new(2, 11) };
            Reject<SqliteException>(() => Change(moved, recommendation with
                { RecommendedChannel = new(new(2, 11), 22, "[normal]") }), "推荐频道真实SQL失败");
            That(State().RecommendedChannel == actor.Channel && State().Revision == recommended.Revision,
                "推荐保存失败同时回滚频道及公会版本");
            command.CommandText = "DROP TRIGGER channel_fail";
            command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM guild_recommended_channels; DELETE FROM guild_settings";
            command.ExecuteNonQuery();

            var zero = new GuildGovernanceChange(GuildGovernanceAction.RolePermissions, Grade: 2, Permissions: 0);
            var cleared = Change(actor, zero);
            That(cleared.Success && State().Roles[2].Permissions == 0 && cleared.Revision == 1,
                "会长可把副会长缺省权限显式清零并持久保存，不回退缺省掩码");
            string before = Snapshot();
            That(Change(actor, zero).Revision == cleared.Revision && Snapshot() == before,
                "重复设置相同权限不增加关系版本或产生额外写入");
            That(Change(actor, zero with { Grade = 1 }).Error == 48 &&
                Change(actor, zero with { Permissions = 1u << 19 }).Error == 48 &&
                Change(ordinary, zero).Error == 36 && Snapshot() == before,
                "角色1权限、越界位和非会长修改全部拒绝且不变更任何表");
            foreach (string label in new[] { new string('a', 21), new string('中', 12), "坏\0名", " " })
                That(Change(actor, new(GuildGovernanceAction.RoleName, Grade: 2, Text: label)).Error == 159,
                    "职位名同时受23编码字节、20UTF16及控制字符约束");
            That(Change(actor, new(GuildGovernanceAction.RoleName, Grade: 1, Text: "公会领队")).Success &&
                Change(actor, new(GuildGovernanceAction.RoleName, Grade: 5, Text: "指挥官")).Success &&
                State().Guild.MasterName == master.Name && State().MasterId == master.Id,
                "修改会长或特殊职位名称仅改职位标签，不改角色名和会长身份");
            byte[] roles = GuildGovernancePackets.Roles(State().Roles);
            That(roles.Length == 168 && roles[28 + 4 + CharacterName.Encoding.GetByteCount("公会领队")] == 0,
                "六条职位记录固定168字节，职位名保持24字节NUL结尾");

            var all = new GuildSettingsChange(GuildGovernanceRules.SettingsMask, "公会介绍\r\n第二行", true, 0x15, 1);
            That(Change(actor, new(GuildGovernanceAction.Settings, Settings: all)).Success,
                "会长一次提交已授权的介绍、网页、标签及加入方式");
            var tagsOnly = new GuildSettingsChange(GuildGovernanceRules.Tags, "", false, 2, 255);
            That(Change(actor, new(GuildGovernanceAction.Settings, Settings: tagsOnly)).Success &&
                State() is { Promotion: "公会介绍\r\n第二行", Tags: 2, JoinMode: 1 },
                "只选择标签时不采用客户端携带的过期介绍、网页或无效加入方式");
            before = Snapshot();
            That(Change(actor, new(GuildGovernanceAction.Settings, Settings: tagsOnly)).Success && Snapshot() == before,
                "重复选中字段相同的设置请求不产生写入");
            foreach (var invalid in new[]
                {
                    all with { Mask = 0 }, all with { Mask = 1 }, all with { JoinMode = 2 },
                    all with { Tags = 0x80 }, all with { Tags = 0x0f }, all with { Promotion = "" }
                })
                That(!Change(actor, new(GuildGovernanceAction.Settings, Settings: invalid)).Success,
                    "设置拒绝未知选择位、非法加入方式、超过三标签和清空已有介绍");
            That(Snapshot() == before, "所有设置校验失败均保留完整关系与角色资产");

            That(Change(actor, zero with { Permissions = GuildGovernanceRules.Tags }).Success,
                "会长可单独授予副会长标签权限");
            before = Snapshot();
            That(Change(delegateActor, new(GuildGovernanceAction.Settings, Settings: all)).Error == 7 && Snapshot() == before,
                "多字段选择先整体授权，不允许已授权标签先写入再拒绝未授权介绍");
            That(Change(delegateActor, new(GuildGovernanceAction.Settings, Settings: tagsOnly with { Tags = 4 })).Success,
                "已获明确权限的普通职位可编辑对应设置字段");
            That(Change(actor, zero with { Permissions = GuildRoleRules.PermissionMask }).Success &&
                Change(delegateActor, zero).Error == 36 &&
                Change(delegateActor, new(GuildGovernanceAction.RoleName, Grade: 4, Text: "越权标签")).Error == 36,
                "即使获得全部普通权限位，非会长仍不能编辑职位权限或名称");

            var rank = new GuildGovernanceChange(GuildGovernanceAction.MemberGrade, Grade: 2, Target: target, Region: 2);
            That(Change(ordinary, rank).Error == 83 &&
                Change(actor, rank with { Target = new(master.Id, masterOwner.Account, master.Name) }).Error == 85 &&
                Change(actor, rank with { Target = new(foreign.Id, foreignOwner.Account, foreign.Name) }).Error == 3 &&
                Change(actor, rank with { Grade = 5 }).Error == 88 && Change(actor, rank with { Region = 0 }).Error == 3,
                "成员品级拒绝无权限、自改、跨会、特殊职位和区服不匹配");
            That(Change(actor, rank).Success && repository.ReadGuildManagement(peerOwner, peer.Id)!.Grade == 2,
                "普通品级2实际落库，保留会长身份");
            before = Snapshot();
            That(Change(actor, rank).Success && Snapshot() == before, "重复相同品级请求不额外增加版本");
            That(Change(actor, zero with { Permissions = 1u << 11 }).Success &&
                Change(delegateActor, rank with { Grade = 3 }).Success,
                "普通品级操作按显式权限位11执行，不添加客户端合同外的品级优先限制");
            That(Change(delegateActor, rank with { Target = new(master.Id, masterOwner.Account, master.Name) }).Error == 87,
                "获授权副会长仍不能把会长当普通成员降职");

            // 在职位表写入之后故意让版本更新失败，必须回滚整个职位修改。
            before = Snapshot();
            command.CommandText = """
                CREATE TRIGGER governance_fail_revision BEFORE UPDATE OF revision ON guild_settings
                WHEN NEW.revision<>OLD.revision BEGIN SELECT RAISE(ABORT,'governance-test-revision'); END
                """;
            command.ExecuteNonQuery();
            Reject<SqliteException>(() => Change(actor, new(GuildGovernanceAction.RoleName, Grade: 3, Text: "回滚标签")),
                "SQL版本提交失败真实抛错");
            That(Snapshot() == before, "SQL提交故障完整回滚职位名称与版本，不能留下半提交");
            command.CommandText = "DROP TRIGGER governance_fail_revision";
            command.ExecuteNonQuery();
            That(Change(actor with { IsCurrent = () => false }, zero).Error == 3 && Snapshot() == before,
                "过期选角身份不能产生治理修改");
            command.CommandText = "INSERT INTO guild_roles(guild_id,grade,permissions,name) VALUES(1,1,0,'会长') ON CONFLICT(guild_id,grade) DO UPDATE SET permissions=0";
            command.ExecuteNonQuery();
            That(Change(actor, new(GuildGovernanceAction.Settings, Settings: tagsOnly with { Tags = 8 })).Success &&
                Change(actor, new(GuildGovernanceAction.Settings, Settings: all with { Mask = GuildGovernanceRules.Promotion })).Error == 7,
                "会长显式零权限仍有原生标签编辑回退，但不旁路其他已撤销权限");

            var transfer = new GuildGovernanceChange(GuildGovernanceAction.MasterTransfer, Target: target);
            before = Snapshot();
            That(Change(ordinary, transfer).Error == 36 &&
                Change(actor, transfer with { Target = new(master.Id, masterOwner.Account, master.Name) }).Error == 86 &&
                Change(actor, transfer with { Target = new(foreign.Id, foreignOwner.Account, foreign.Name) }).Error == 86 &&
                Change(actor, transfer with { Target = target with { Account = "wrong-account" } }).Error == 86 &&
                Snapshot() == before, "会长转让拒绝越权、自转让、跨会和过期目标身份，所有关系与资产不变");
            command.CommandText = """
                CREATE TRIGGER governance_fail_transfer BEFORE UPDATE OF grade ON guild_members
                WHEN NEW.grade=1 BEGIN SELECT RAISE(ABORT,'governance-test-transfer'); END
                """;
            command.ExecuteNonQuery();
            Reject<SqliteException>(() => Change(actor, transfer), "转让职位更新故障真实抛出");
            That(Snapshot() == before, "转让中途失败完整回滚会长字段、双方职位与版本");
            command.CommandText = "DROP TRIGGER governance_fail_transfer";
            command.ExecuteNonQuery();
            long transferRevision = State().Revision;
            var transferred = Change(actor, transfer);
            That(transferred.Success && transferred.Revision == transferRevision + 1 &&
                transferred.Members?.Length == 3 && State() is { MasterId: var newMaster, Grade: 4 } && newMaster == peer.Id &&
                repository.ReadGuildManagement(peerOwner, peer.Id) is { Grade: 1, Guild.MasterName: "GovMember" },
                "会长转让原子提交新会长与双方职位，旧会长为普通会员，返回原成员刷新集合");
            before = Snapshot();
            That(Change(actor, transfer).Error == 36 && Snapshot() == before,
                "重复旧会长转让请求无权限，不重写会长身份或版本");
            That(Change(ordinary, transfer with { Target = new(master.Id, masterOwner.Account, master.Name) }).Success &&
                State().MasterId == master.Id && repository.ReadGuildManagement(peerOwner, peer.Id)!.Grade == 4,
                "新会长能转回原会长，身份以后续真实公会字段为准");
        }
        finally
        {
            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    internal static byte[] Text(string text) => Build(writer =>
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(text);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    });

    internal static byte[] Settings(GuildSettingsChange change) => Build(writer =>
    {
        writer.Write(change.Mask);
        writer.Write(Text(change.Promotion));
        writer.Write((byte)(change.Webpage ? 1 : 0));
        writer.Write(change.Tags);
        writer.Write(change.JoinMode);
    });
}
