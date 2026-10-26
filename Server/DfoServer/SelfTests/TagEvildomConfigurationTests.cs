using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class TagEvildomConfigurationTests
{
    internal static void Run()
    {
        int before = Count;
        Requests();
        Rules();
        Packets();
        InventoryReadiness();
        Persistence();
        Console.WriteLine($"PASS {Count - before} 再战魔界编队解析、身份规则与原子配置断言（未接通客户端玩法）");
    }

    static void Requests()
    {
        // 独立按静态发送点写预期，不借生产 Builder 生成解析输入。
        var body = new byte[75];
        for (int index = 0; index < 13; index++) body[index] = (byte)(0xA0 + index);
        for (int index = 0; index < 6; index++)
        {
            body[13 + index * 9] = index < 2 ? (byte)2 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(14 + index * 9), index < 2 ? index * 3 : -1);
        }
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(67), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(71), 0xF1E2D3C4);
        var parsed = TagEvildomRequests.Setting(body);
        That(parsed.SelectedTeam == 2 && parsed.Members.Length == 6 && parsed.Members[0] == new TagEvildomMemberRequest(2, 0, 0) &&
            parsed.Members[1] == new TagEvildomMemberRequest(2, 3, 0) && parsed.Members.Skip(2).All(row => row == new TagEvildomMemberRequest(0, -1, 0)),
            "75B正文含13B不可信前缀，6项与selectedTeam按真实偏移读取，未知尾不授权身份");
        body[13] = 99;
        That(parsed.Members[0].Server == 2, "请求结果不持有可变原包内存");
        foreach (int length in new[] { 0, 13, 54, 62, 74, 76, 88 })
            Reject<InvalidDataException>(() => TagEvildomRequests.Setting(new byte[length]), "截断、误扣13B帧头及尾部均拒绝");
        TagEvildomRequests.Initialize([]);
        Reject<InvalidDataException>(() => TagEvildomRequests.Initialize([0]), "1663只接受空正文");
        foreach (uint dungeon in new uint[] { 9608, 400000095 })
        {
            var entry = new byte[6]; BinaryPrimitives.WriteUInt32LittleEndian(entry, dungeon);
            That(TagEvildomRequests.Entry(entry) == dungeon, "再战原生15精确6B，目标类型区分普通/简单");
            Reject<InvalidDataException>(() => DungeonRequests.ParseEntry(entry), "普通15仍拒绝6B尾，不全局放宽");
            entry[4] = 1;
            Reject<InvalidDataException>(() => TagEvildomRequests.Entry(entry), "再战15坏尾拒绝");
        }
        Reject<InvalidDataException>(() => TagEvildomRequests.Entry([1, 0, 0, 0, 0, 0]), "再战入口不能跨用普通地图");
    }

    static void Rules()
    {
        var roster = Enumerable.Range(1, 6).Select(id => new CharacterRosterEntry((ushort)id, (ushort)(id * 2), "Tag" + id, 0, 0, 95, [])).ToArray();
        var empty = TagEvildomConfiguration.Empty;
        var full = empty with { Teams = [new(1, 2), new(3, 4), new(5, 6)], SelectedTeam = 2 };
        That(TagEvildomConfiguration.ValidateChange(empty, full, roster, ImmutableHashSet<ushort>.Empty) == "ready", "同账号三组双角色保存稳定ID，槽位空洞不影响身份");
        foreach (var invalid in new[]
        {
            full with { Teams = [new(1, 0), default, default] },
            full with { Teams = [new(0, 1), default, default] },
            full with { Teams = [new(1, 1), default, default] },
            full with { Teams = [new(1, 2), new(1, 3), default] },
            full with { Teams = [new(1, 2)] },
            full with { SelectedTeam = -1 },
            full with { SelectedTeam = 3 }
        }) Reject<InvalidDataException>(invalid.Validate, "拒绝半队、重复角色和越界配置");
        That(TagEvildomConfiguration.ValidateChange(empty, full, roster[..5], ImmutableHashSet<ushort>.Empty) == "character-not-in-account", "另一账号或已删除角色不能进入配置");
        That(TagEvildomConfiguration.ValidateChange(empty, full, roster.Select(row => row.Id == 2 ? row with { Level = 94 } : row).ToArray(), ImmutableHashSet<ushort>.Empty) == "character-not-eligible", "94级不能凭客户端索引编队");
        That(TagEvildomConfiguration.ValidateChange(empty, full, roster.Select(row => row.Id == 2 ? row with { IsPvpPrivate = true } : row).ToArray(), ImmutableHashSet<ushort>.Empty) == "character-not-eligible", "决斗专用角色不进入正式TAG配置");
        var protectedMembers = ImmutableHashSet.Create<ushort>(3, 4);
        var reset = full.Reset(protectedMembers);
        That(reset.Teams.SequenceEqual(new TagEvildomTeam[] { default, new(3, 4), default }) && reset.SelectedTeam == full.SelectedTeam &&
            TagEvildomConfiguration.ValidateChange(full, reset, roster, protectedMembers) == "ready", "重置只清未保护队，不改真实保护名单或选择值");
        That(TagEvildomConfiguration.ValidateChange(full, empty, roster, protectedMembers) == "protected-team-changed", "不能以初始化清掉受保护队伍");
        var moved = full with { Teams = [new(3, 4), new(1, 2), new(5, 6)] };
        That(TagEvildomConfiguration.ValidateChange(full, moved, roster, protectedMembers) != "ready", "受保护角色不能换队绕过限制");
        That(full.Reset(ImmutableHashSet<ushort>.Empty).Teams.All(team => team.Empty) && full.Teams[0] == new TagEvildomTeam(1, 2), "无保护时重置三队，原不可变快照保持");
        That(TagEvildomConfiguration.ValidateChange(empty, empty with { SelectedTeam = 2 }, roster, ImmutableHashSet<ushort>.Empty) == "ready", "不因选中空队无依据收窄原生全空配置");
    }

    static void Persistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-tag-evildom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("tag-owner", () => { });
            using var foreign = owners.Acquire("tag-foreign", () => { });
            var position = new Location(38, 1, 450, 234);
            NewCharacter Role(string name, byte level = 95) => new(name, position, position) { Level = level };
            var characters = Enumerable.Range(0, 6).Select(index => repository.Create(owner, Role("TagMember" + index))).ToArray();
            var outsider = repository.Create(foreign, Role("TagOutsider"));
            var low = repository.Create(owner, Role("TagLow", 94));
            var desired = TagEvildomConfiguration.Empty with
            {
                Teams = [new(characters[0].Id, characters[1].Id), new(characters[2].Id, characters[3].Id), new(characters[4].Id, characters[5].Id)],
                SelectedTeam = 1
            };
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var sql = db.CreateCommand();
            sql.CommandText = "SELECT count(*) FROM account_tag_evildom_configuration";
            That(repository.ReadTagEvildomConfiguration(owner) == TagEvildomConfiguration.Empty && sql.ExecuteScalar() is 0L,
                "首次只读为未编队，不建默认行或申请writer");
            string before = JsonSerializer.Serialize(repository.List(owner));
            var committed = repository.CommitTagEvildomConfiguration(owner, TagEvildomConfiguration.Empty, desired);
            That(committed.Revision == 1 && committed.SameAssignments(desired) && JsonSerializer.Serialize(repository.List(owner)) == before,
                "三队一笔提交仅推进配置revision，全部角色/完整资产/疲劳不变");
            That(repository.ReadTagEvildomConfiguration(foreign) == TagEvildomConfiguration.Empty,
                "另一账号不读到本账号编队");
            var reopened = new CharacterRepository(path).ReadTagEvildomConfiguration(owner);
            That(reopened.Revision == 1 && reopened.SameAssignments(committed), "重开仓储回放真实stableId配置");
            sql.CommandText = "CREATE TRIGGER tag_reject_update BEFORE UPDATE ON account_tag_evildom_configuration BEGIN SELECT RAISE(ABORT,'tag-test'); END";
            sql.ExecuteNonQuery();
            That(repository.CommitTagEvildomConfiguration(owner, committed, committed).Revision == committed.Revision,
                "同配置重放不触发UPDATE或推进revision");
            var changed = committed with { SelectedTeam = 2 };
            Reject<SqliteException>(() => repository.CommitTagEvildomConfiguration(owner, committed, changed), "真实SQL故障不返回成功");
            That(repository.ReadTagEvildomConfiguration(owner).SameAssignments(committed) && JsonSerializer.Serialize(repository.List(owner)) == before,
                "配置失败整体回滚，角色与资产保持");
            sql.CommandText = "DROP TRIGGER tag_reject_update";
            sql.ExecuteNonQuery();
            foreach (ushort bad in new[] { outsider.Id, low.Id, ushort.MaxValue })
                Reject<InvalidOperationException>(() => repository.CommitTagEvildomConfiguration(owner, committed,
                    committed with { Teams = [new(characters[0].Id, bad), default, default] }), "提交时独立复核账号、等级和角色存在性");
            repository.SwapSlots(owner, characters[0].Slot, characters[5].Slot);
            That(repository.ReadTagEvildomConfiguration(owner).SameAssignments(committed), "名册重排不把队伍角色偷换为槽位新住户");
            var deleted = repository.LoadDeletionTarget(owner, characters[5].Slot);
            That(deleted.Id == characters[0].Id, "删除对象按当前槽位重新核对稳定身份");
            sql.CommandText = "CREATE TRIGGER tag_reject_delete BEFORE DELETE ON characters BEGIN SELECT RAISE(ABORT,'tag-delete-test'); END";
            sql.ExecuteNonQuery();
            Reject<SqliteException>(() => repository.Delete(owner, deleted), "删除后段失败回滚前段编队清理");
            That(repository.ReadTagEvildomConfiguration(owner).SameAssignments(committed) && repository.ListRoster(owner).Any(row => row.Id == deleted.Id), "删角失败不丢队友或角色");
            sql.CommandText = "DROP TRIGGER tag_reject_delete";
            sql.ExecuteNonQuery();
            repository.Delete(owner, deleted);
            var cleaned = repository.ReadTagEvildomConfiguration(owner);
            That(cleaned.Revision == 2 && cleaned.Teams[0].Empty && cleaned.Teams[1] == committed.Teams[1] && cleaned.Teams[2] == committed.Teams[2], "删角同事务清整队且保留其它两队");
            Reject<InvalidOperationException>(() => repository.CommitTagEvildomConfiguration(owner, committed, changed), "旧配置revision不能复活已删除角色");
            using var replacement = owners.Acquire("tag-owner", () => { });
            Reject<InvalidOperationException>(() => repository.ReadTagEvildomConfiguration(owner), "旧generation不可读取新会话编队");
            Reject<InvalidOperationException>(() => repository.CommitTagEvildomConfiguration(owner, cleaned, cleaned), "旧generation连零变化保存也拒绝");
            That(repository.ReadTagEvildomConfiguration(replacement).SameAssignments(cleaned), "新owner读取原真实配置而不是旧连接清理结果");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
