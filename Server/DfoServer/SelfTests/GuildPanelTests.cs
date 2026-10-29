using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildPanelTests
{
    internal sealed record MemberRow(uint Id, string Name, string Memo, ushort Level, byte Job, byte Grow,
        byte Region, byte Channel, byte Grade, uint OfflineSeconds, uint Reference, byte Representative, byte OfflineListable,
        uint WeeklyContribution, uint Contribution);
    internal sealed record MemberFrame(uint GuildId, string? Name, ushort? Total, MemberRow[] Rows);
    internal sealed record GradeRow(byte Region, string Name, byte Grade);
    internal sealed record GradeFrame(uint GuildId, ushort? Total, GradeRow[] Rows);

    public static void RunCore()
    {
        int before = Count;
        That(GuildRequests.TryMemberList([], false, out _) &&
            GuildRequests.TryMemberList([0x78, 0x56, 0x34, 0x12], true, out uint reference) && reference == 0x12345678,
            "成员查询区分空正文与完整四字节行引用");
        That(!GuildRequests.TryMemberList([0], false, out _) &&
            !GuildRequests.TryMemberList([], true, out _) &&
            !GuildRequests.TryMemberList([1, 2, 3], true, out _) &&
            !GuildRequests.TryMemberList([1, 2, 3, 4, 5], true, out _),
            "成员查询拒绝缺失和多余字节，不把任意引用当公会权限");
        RunPackets();
        GrowthTail();
        RunRepositoryAndOnline();
        Console.WriteLine($"PASS {Count - before} 公会成员协议、权威查询与在线投影断言");
    }

    static void GrowthTail()
    {
        var guild = new GuildMembership(7, "成长", 9, 1, "会长", "", 1234567, 1600000000)
            { Tier = 2, Crystals = 321 };
        var body = GuildPackets.Information(guild, 1);
        using var reader = new BinaryReader(new MemoryStream(body));
        reader.BaseStream.Position = body.Length - 29;
        That(reader.ReadUInt32() == 2 && reader.ReadUInt32() == 9 && reader.ReadUInt64() == 1234567,
            "现代成长尾保留档位、等级及完整八字节累计经验");
        That(reader.ReadUInt32() == 0 && reader.ReadUInt32() == 0 && reader.ReadUInt32() == 321 && reader.ReadByte() == 0,
            "NPC材料检查使用真实公会结晶，未确认字段保持独立");
    }

    static void RunPackets()
    {
        const long now = 1700000100;
        var online = new GuildPanelMember(7, "a", "会长甲", 95, 2, 0x31, 1, "", 321, now - 300)
            { Region = 2, Channel = 10, WeeklyContribution = 123 };
        var offline = new GuildPanelMember(8, "b", "会员乙", 72, 4, 0x42, 4, "中文备注中文备注中文多", 0, now - 100)
            { Region = 3 };
        var unknown = new GuildPanelMember(9, "c", "Unknown", 1, 0, 0, 2, "", 0, 0) { Region = 2 };
        var panel = new GuildPanel(new(0x12345678, "成员公会", 1, 3, online.Name, "", 0, 1600000000), 7,
            [online, offline, unknown]);
        var first = DecodeMembers(GuildPackets.Members(panel, 0, 2, true, true, now), true, true);
        That(first.GuildId == 0x12345678 && first.Name == "成员公会" && first.Total == 3 && first.Rows.Length == 2,
            "140首包使用真实公会身份、完整总人数和当前分块人数");
        That(first.Rows[0] is { Id: 7, Name: "会长甲", Level: 95, Job: 2, Grow: 0x31, Region: 2,
            Channel: 10, Grade: 1, OfflineSeconds: 0, Reference: 7, Representative: 1, OfflineListable: 0,
            WeeklyContribution: 123, Contribution: 321 },
            "在线成员保留职业、觉醒、区服、频道、会长职位及独立周贡献和累计贡献，离线时长为零");
        That(first.Rows[1] is { Id: 8, Region: 3, Channel: 255, Grade: 4, OfflineSeconds: 100,
            Reference: 8, OfflineListable: 1 } && first.Rows[1].Memo == "中文备注中文备注中文" &&
            CharacterName.Encoding.GetByteCount(first.Rows[1].Memo) == 20,
            "离线成员状态、真实经过秒数和中文备注边界完整，不截半个CP936字符");
        var next = DecodeMembers(GuildPackets.Members(panel, 2, 1, false, true, now), false, true);
        That(next.Name is null && next.Total is null && next.Rows is [{ Id: 9, OfflineSeconds: 0 }],
            "141续包仅追加行，存量未知时间保留缺省值");
        var regular = DecodeMembers(GuildPackets.Members(panel, 0, 3, true, false, now), true, false);
        var regularNext = DecodeMembers(GuildPackets.Members(panel, 2, 1, false, false, now), false, false);
        That(regular.Rows.Length == 3 && regularNext.Rows.Length == 1,
            "67及139采用对应首续包格式，不附加140系尾引用");
        var empty = DecodeMembers(GuildPackets.Members(panel with { Members = [] }, 0, 0, true, true, now), true, true);
        That(empty.Total == 0 && empty.Rows.Length == 0, "空分块仍具有可完整消费的首包头和尾引用");
        var full = panel with { Members = Enumerable.Range(1, 128).Select(id => offline with
            { Id = (ushort)id, Name = new string('N', 30), Memo = new string('中', 20) }).ToArray() };
        byte[] body = GuildPackets.Members(full, 0, 128, true, true, now);
        That(body.Length <= 65520 && DecodeMembers(body, true, true).Rows.Length == 128,
            "单块128名最长显示文本成员不超过协议帧上限");
        var grades = DecodeGrades(GuildPackets.MemberGrades(panel, 0, 2, true), true);
        var moreGrades = DecodeGrades(GuildPackets.MemberGrades(panel, 2, 1, false), false);
        That(grades.GuildId == panel.Guild.Id && grades.Total == 3 &&
            grades.Rows is [{ Region: 2, Name: "会长甲", Grade: 1 }, { Region: 3, Name: "会员乙", Grade: 4 }] &&
            moreGrades.Total is null && moreGrades.Rows is [{ Name: "Unknown", Grade: 2 }],
            "1455首包总数和1456续包逐角色保留真实大区、名称、职位，不混用140行结构");
        var emptyGrades = DecodeGrades(GuildPackets.MemberGrades(panel with { Members = [] }, 0, 0, true), true);
        That(emptyGrades.Total == 0 && emptyGrades.Rows.Length == 0, "空职位目录仍完整消费首包计数");
        var grouped = panel with { Members = [online, offline with { Account = online.Account }, unknown] };
        var groupedFirst = DecodeMembers(GuildPackets.Members(grouped, 0, 1, true, true, now), true, true);
        var groupedNext = DecodeMembers(GuildPackets.Members(grouped, 1, 2, false, true, now), false, true);
        That(groupedFirst.Rows[0] is { Reference: 7, Representative: 1 } &&
            groupedNext.Rows[0] is { Reference: 7, Representative: 0, OfflineListable: 1 },
            "同账号跨首续包共享稳定代表ID，代表标记与离线状态独立");
        grouped = grouped with { Members = grouped.Members.Reverse().Select(member => member with { Channel = 255 }).ToArray() };
        var reversed = DecodeMembers(GuildPackets.Members(grouped, 0, 3, true, false, now), true, false);
        That(reversed.Rows.Single(row => row.Id == 7) is { Reference: 7, Representative: 1 } &&
            reversed.Rows.Single(row => row.Id == 8) is { Reference: 7, Representative: 0 } &&
            reversed.Rows.GroupBy(row => row.Reference).All(group => group.Count(row => row.Representative == 1) == 1),
            "离线与名册反序不改变账号代表，每个完整分组均有唯一代表，原生检查不会落入缺代表路径");
    }

    static void RunRepositoryAndOnline()
    {
        string path = Path.Combine(Path.GetTempPath(), "guild-panel-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var masterOwner = owners.Acquire("guild-master", () => { });
            using var peerOwner = owners.Acquire("guild-peer", () => { });
            using var offlineOwner = owners.Acquire("guild-offline", () => { });
            using var foreignOwner = owners.Acquire("guild-foreign", () => { });
            var room = new Location(0, 0, 100, 100);
            var master = repository.Create(masterOwner, new("PanelMaster", room, room) { Level = 95, Job = 2, GrowType = 0x31 });
            var peer = repository.Create(peerOwner, new("PanelPeer", room, room) { Level = 72, Job = 4 });
            var offline = repository.Create(offlineOwner, new("PanelOffline", room, room));
            var foreign = repository.Create(foreignOwner, new("PanelForeign", room, room));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                VALUES(1,'成员公会','guild-panel', $master,1600000000),(2,'其他公会','guild-other',$foreign,1600000000);
                INSERT INTO guild_members(guild_id,character_id,grade,member_memo,joined_at_unix)
                VALUES(1,$master,0,'会长备注',1600000000),(1,$peer,0,'普通备注',1600000000),
                    (1,$offline,2,'副会长备注',1600000000),(2,$foreign,0,'其他公会',1600000000);
                """;
            command.Parameters.AddWithValue("$master", master.Id);
            command.Parameters.AddWithValue("$peer", peer.Id);
            command.Parameters.AddWithValue("$offline", offline.Id);
            command.Parameters.AddWithValue("$foreign", foreign.Id);
            command.ExecuteNonQuery();
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), room, room,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var masterSession = new GameSession(repository, towns, masterOwner, entry: new(new(2, 10), 22, "test"));
            var peerSession = new GameSession(repository, towns, peerOwner, entry: new(new(3, 12), 22, "test"));
            var directory = new OnlineSessionDirectory();
            directory.Register(masterOwner, masterSession);
            directory.Register(peerOwner, peerSession);
            masterSession.Select(master.Slot);
            peerSession.Select(peer.Slot);
            That(masterSession.CaptureGuildPanelQuery() is null, "未就绪的选角阶段不捕获公会页面查询");
            masterSession.MarkReady();
            peerSession.MarkReady();
            var query = masterSession.CaptureGuildPanelQuery()!;
            That(query.CharacterId == master.Id && query.IsCurrent(), "已就绪查询捕获角色与当前选角代次");
            string before = Snapshot(command);
            var service = new GuildPanelService(repository, directory);
            var panel = service.Read(masterOwner, master.Id, 2)!;
            That(panel.Guild.Id == 1 && panel.Guild.MemberCount == 3 && panel.Members.Length == 3 &&
                panel.Members.All(member => member.Id != foreign.Id), "查询只包含本人所属公会，隔离其他公会成员");
            That(panel.Members.Single(member => member.Id == master.Id).Grade == 1 &&
                panel.Members.Single(member => member.Id == peer.Id).Grade == 4 &&
                panel.Members.Single(member => member.Id == offline.Id).Grade == 2,
                "存量双方grade0只在视图映射为会长与普通会员，保留已有有效职位");
            That(masterSession.Selected?.Guild?.Grade == 1 && peerSession.Selected?.Guild?.Grade == 4 &&
                repository.GuildMembershipOf(offline.Id)?.Grade == 2,
                "本人会籍独立携带归一化职位，登录127不能依赖尚未请求的成员名单");
            byte[] actor = TownPackets.Actor(masterSession.Selected!, 0, new(2, 10));
            That(actor[^15] == 2 && TownPackets.Actor(peerSession.Selected!, 0, new(3, 12))[^15] == 3,
                "0267F091的独立尾部大区与各自真实大区一致，成员关联不再拿0与实际大区比对失败");
            That(panel.OnlineCount == 2 && panel.Members.Single(member => member.Id == peer.Id) is { Channel: 12, Region: 3 } &&
                panel.Members.Single(member => member.Id == offline.Id) is { Channel: 255, Region: 2 },
                "在线成员从各自会话取区服频道，离线成员使用请求大区及FF频道");
            That(repository.ReadGuildPanel(foreignOwner, master.Id) is null &&
                repository.ReadGuildPanel(masterOwner, ushort.MaxValue) is null,
                "其他账号不能冒用本人角色，缺失角色不构造公会视图");
            That(Snapshot(command) == before, "成员查询及在线投影不改写成员、角色资产或职位旧值");
            peerSession.End();
            That(service.Read(masterOwner, master.Id, 2)!.OnlineCount == 1,
                "结束的会话即使仍在目录中也不计为在线");
            masterSession.ReturnToRoster();
            That(!query.IsCurrent(), "返回选角使旧查询失效，不能向新角色发布旧列表");
            masterSession.Select(master.Slot);
            masterSession.MarkReady();
            var replaced = masterSession.CaptureGuildPanelQuery()!;
            using var replacement = owners.Acquire(masterOwner.Account, () => { });
            That(!replaced.IsCurrent(), "同账号接管使旧owner查询失效");
            masterSession.End();
        }
        finally
        {
            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    static string Snapshot(SqliteCommand command)
    {
        command.CommandText = """
            SELECT group_concat(value,'|') FROM (
                SELECT 'm:'||guild_id||':'||character_id||':'||grade||':'||contribution||':'||member_memo||':'||last_login_unix value
                FROM guild_members ORDER BY guild_id,character_id);
            """;
        string members = (string)command.ExecuteScalar()!;
        command.CommandText = "SELECT group_concat(id||':'||revision||':'||gold||':'||name,'|') FROM characters ORDER BY id";
        return members + "/" + command.ExecuteScalar();
    }

    // 独立按客户端字段顺序解码，TCP也复用它验证实际包体，不以生产Builder构造预期。
    internal static MemberFrame DecodeMembers(byte[] body, bool first, bool all)
    {
        using var stream = new MemoryStream(body);
        using var reader = new BinaryReader(stream);
        That(reader.ReadByte() == 1, "成员列表成功前缀");
        uint guildId = reader.ReadUInt32();
        string? name = first ? ReadText(reader) : null;
        ushort? total = first ? reader.ReadUInt16() : null;
        int count = reader.ReadUInt16();
        var rows = new MemberRow[count];
        for (int i = 0; i < count; i++)
        {
            uint id = reader.ReadUInt32();
            string memberName = ReadText(reader), memo = ReadText(reader);
            ushort level = reader.ReadUInt16();
            byte job = reader.ReadByte(), grow = reader.ReadByte(), region = reader.ReadByte();
            That(reader.ReadByte() == 0, "成员额外名牌标记保持关闭，不能误填大区号");
            byte channel = reader.ReadByte();
            That(reader.ReadByte() == 0 && reader.ReadByte() == 0, "成员未启用状态字段完整");
            byte grade = reader.ReadByte();
            uint seconds = reader.ReadUInt32(), reference = reader.ReadUInt32();
            byte representative = reader.ReadByte();
            That(representative is 0 or 1, "成员账号代表是独立布尔标记");
            That(ReadText(reader) == "", "成员分组文字完整消费");
            byte offline = reader.ReadByte();
            That(offline == (channel == 255 ? 1 : 0) && reader.ReadByte() == region &&
                reader.ReadByte() == 0 && reader.ReadByte() == 0,
                "成员分组文字、离线可列标记及区服字段顺序完整");
            uint weeklyContribution = reader.ReadUInt32();
            That(reader.ReadUInt32() == 0, "本周与累计贡献中间保留四字节字段，不能挪用贡献值");
            uint contribution = reader.ReadUInt32();
            That(reader.ReadByte() == 0 && reader.ReadByte() == 0, "95版贡献后两个标志完整消费");
            rows[i] = new(id, memberName, memo, level, job, grow, region, channel, grade, seconds, reference, representative, offline,
                weeklyContribution, contribution);
        }
        if (all) That(reader.ReadUInt32() == 0, "140及141末尾保留四字节引用");
        That(stream.Position == stream.Length, "成员列表包体逐字段完整消费，无尾部错位");
        return new(guildId, name, total, rows);
    }

    internal static GradeFrame DecodeGrades(byte[] body, bool first)
    {
        using var stream = new MemoryStream(body);
        using var reader = new BinaryReader(stream);
        That(reader.ReadByte() == 1, "职位列表外层成功前缀完整");
        uint guild = reader.ReadUInt32();
        ushort? total = first ? reader.ReadUInt16() : null;
        var rows = new GradeRow[reader.ReadUInt16()];
        for (int i = 0; i < rows.Length; i++)
        {
            byte region = reader.ReadByte();
            string name = ReadText(reader);
            byte grade = reader.ReadByte();
            That(reader.ReadByte() == 0 && reader.ReadByte() == 0,
                "原生职位行保留独立状态与转让限制字节，不将频道或账号代表标志写入");
            rows[i] = new(region, name, grade);
        }
        That(stream.Position == stream.Length, "1455/1456按原生字段完整消费，无140尾引用或多余名称");
        return new(guild, total, rows);
    }

    static string ReadText(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        That(length >= 0 && length <= reader.BaseStream.Length - reader.BaseStream.Position, "公会DSTR长度在包体内");
        return CharacterName.Encoding.GetString(reader.ReadBytes(length));
    }
}
