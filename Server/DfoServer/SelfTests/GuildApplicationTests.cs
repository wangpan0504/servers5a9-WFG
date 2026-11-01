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

internal static class GuildApplicationTests
{
    public static void RunCore()
    {
        int before = Count;
        DirectoryContracts();
        That(GuildApplicationRequests.Search([2, 3, 0, 0, 0, 1]) == new GuildSearchFilter(2, Tags: 3, Page: 1),
            "标签掩码与零基页码分开解析，不把掩码误用作页码");
        Reject<InvalidDataException>(() => GuildApplicationRequests.Search([2, 15, 0, 0, 0, 0]), "拒绝超过三个标签");
        Reject<InvalidDataException>(() => GuildApplicationRequests.Search([2, 128, 0, 0, 0, 0]), "拒绝未知标签位");
        Reject<InvalidDataException>(() => GuildApplicationRequests.Search([3, 0, 0, 0, 0]), "拒绝未知搜索模式");
        That(GuildApplicationRequests.Apply([.. Text("申请公会"), .. Text("申请留言")]) == ("申请公会", "申请留言") &&
            GuildApplicationRequests.Apply(Text("申请公会")) == ("申请公会", ""), "申请接受本版名称及可选留言DSTR");
        Reject<InvalidDataException>(() => GuildApplicationRequests.Apply([.. Text("申请公会"), 0]), "拒绝未完成的留言正文");
        That(GuildApplicationRequests.Decision([2, 0, 0, 0, 0], true) == (2, 0) &&
            GuildApplicationRequests.Decision([2, 0, 0, 0, 0, 0, 0, 0], false) == (2, 0),
            "批准读取5B身份，拒绝读取8B身份");
        Reject<InvalidDataException>(() => GuildApplicationRequests.Decision([2, 0, 0, 0, 0], false), "拒绝不能误收批准宽度");
        byte[] failed = GuildApplicationPackets.Failure(23, [2, 0, 0, 0, 0]);
        That(failed.SequenceEqual(new byte[] { 0, 23, 0, 2, 0, 0, 0, 0 }), "批准失败仍携完整身份，错误码沿本版u16框架");
        var sample = new GuildApplicationRow(2, "sample", "申请人", 3, 0x21, 95, "申请留言", 1_700_000_000, 0);
        var decoded = DecodeApplications(GuildApplicationPackets.Applications([sample]));
        That(decoded is [{ CharacterId: 2, Name: "申请人", Job: 3, GrowType: 0x21, Level: 95, Message: "申请留言", Region: 0 }],
            "独立读取申请列表的职业转职、留言、时间与零区服");
        var longRow = sample with { Message = new string('申', 80) };
        That(GuildApplicationPackets.Applications(Enumerable.Repeat(longRow, 300).ToArray()).Length <= 65520,
            "300条最大长度申请留言仍在单个真实帧范围内");
        Reject<InvalidDataException>(() => GuildApplicationPackets.Applications(Enumerable.Repeat(longRow, 400).ToArray()),
            "申请列表不能溢出帧或静默截断已接受的行");
        RunRepository();
        Console.WriteLine($"PASS {Count - before} 公会搜索、申请审批、完整回包与事务断言");
    }

    static void DirectoryContracts()
    {
        // 独立按00598800/00599240消费者读取两行；特别区分徽章u32与搜索独有尾字段。
        var first = new GuildDirectoryEntry(17, "甲会", "甲长", 99, 7, 1, 3, "甲宣传")
            { Mark = 0x00340012, Tier = 2, CreatedAtUnix = 1600000000, MemberLimit = 400, AttendanceCount = 5 };
        var second = new GuildDirectoryEntry(18, "乙会", "乙长", 2, 8, 0, 4, "乙宣传") { Mark = 0x00560023 };
        Bytes(GuildApplicationPackets.PendingApplication(null), [0], "无申请308仅发送零flag，不带CMD前缀或伪造公会");
        using (var pending = new BinaryReader(new MemoryStream(GuildApplicationPackets.PendingApplication(first))))
            That(pending.ReadByte() == 1 && pending.ReadUInt32() == first.Id && ReadText(pending) == first.Name &&
                ReadText(pending) == first.MasterName && pending.ReadUInt32() == 0 && pending.ReadUInt32() == first.Members &&
                pending.ReadByte() == 0 && ReadText(pending) == first.Promotion && pending.BaseStream.Position == pending.BaseStream.Length,
                "308逐字段独立读取真实待审批公会，保留u32/u8不冒充已证业务标志");
        foreach (bool search in new[] { true, false })
        {
            byte[] body = search ? GuildApplicationPackets.Search(new(32, 1, [first, second])) :
                GuildApplicationPackets.AccountIndex(new([first, second], [new("角色甲", second, false, 3)], []));
            using var reader = new BinaryReader(new MemoryStream(body));
            That(reader.ReadByte() == 1, "目录回执含独立成功标记");
            if (search) That(reader.ReadUInt32() == 32 && reader.ReadByte() == 1, "搜索零基页与真实总数保持独立");
            That(reader.ReadByte() == 2, "两种目录都完整声明两行");
            foreach (var expected in new[] { first, second })
            {
                That(reader.ReadUInt32() == expected.Id && reader.ReadByte() == expected.JoinMode &&
                    reader.ReadUInt32() == expected.Tags && reader.ReadUInt16() == expected.Members &&
                    reader.ReadUInt32() == expected.Mark && ReadText(reader) == expected.Name &&
                    ReadText(reader) == expected.MasterName && ReadText(reader) == expected.Promotion,
                    "原生目录逐行正确读取徽章及三个文本，后一行不因前一行宽度错位");
                if (search) That(reader.ReadUInt32() == 0, "搜索行完整消费额外四字节字段");
                That(reader.ReadByte() == expected.Tier && reader.ReadByte() == expected.Level,
                    "00595B00消费行尾档位与等级，不能继续留在徽章字段或写成全零");
            }
            if (!search) That(reader.ReadByte() == 1 && reader.ReadUInt32() == 18 && reader.ReadByte() == 3 &&
                ReadText(reader) == "角色甲", "账号目录行尾准确衔接角色与公会映射，不插入搜索专有字段");
            That(reader.BaseStream.Position == reader.BaseStream.Length, "原生目录合同完整消费且无残留");
        }
        using var detail = new BinaryReader(new MemoryStream(GuildApplicationPackets.SearchResult(first)));
        That(detail.ReadByte() == 1 && detail.ReadUInt32() == 17 && ReadText(detail) == "甲会" &&
            detail.ReadUInt16() == 7 && ReadText(detail) == "甲长" && detail.ReadUInt32() == 5 &&
            detail.ReadByte() == 2 && detail.ReadByte() == 0 && detail.ReadByte() == 1 &&
            ReadText(detail) == "甲宣传" && detail.ReadByte() == 1,
            "745详情中的出勤人数不是等级，标签列表与宣传字段完整");
        That(detail.ReadUInt32() == 3 && detail.ReadUInt32() == 0 && detail.ReadUInt32() == 1600000000 &&
            detail.ReadUInt32() == 400 && detail.ReadUInt32() == 0 && detail.ReadByte() == 2 && detail.ReadByte() == 99 &&
            detail.BaseStream.Position == detail.BaseStream.Length,
            "745不截断22字节尾部，详情页使用真实创建时间、人数上限和档内等级，零排名显示未排名");
    }

    static void RunRepository()
    {
        string root = Path.Combine(Path.GetTempPath(), "guild-applications-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var masterOwner = owners.Acquire("c-master", static () => { });
            using var memberOwner = owners.Acquire("c-member", static () => { });
            using var applicantOwner = owners.Acquire("c-applicant", static () => { });
            using var otherOwner = owners.Acquire("c-other", static () => { });
            var room = new Location(38, 1, 450, 234, 0);
            var master = repository.Create(masterOwner, new("AppMaster", room, room));
            var member = repository.Create(memberOwner, new("AppMember", room, room));
            var applicant = repository.Create(applicantOwner, new("AppApplicant", room, room));
            var other = repository.Create(otherOwner, new("OtherMaster", room, room));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            void Sql(string text)
            {
                using var command = db.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery();
            }
            long Number(string text)
            {
                using var command = db.CreateCommand(); command.CommandText = text; return Convert.ToInt64(command.ExecuteScalar());
            }
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                VALUES(1,'申请公会','application',{master.Id},1000),(2,'其他公会','other',{other.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix)
                VALUES(1,{master.Id},0,1000),(1,{member.Id},0,1000),(2,{other.Id},0,1000);
                INSERT INTO guild_appearance(guild_id,mark) VALUES(1,3407890);
                """);
            for (int i = 0; i < 32; i++)
                Sql($"""
                    INSERT INTO guilds(guild_id,name,address,master_character_id,level,created_at_unix)
                    VALUES({i + 3},'Find{i:D2}','find-{i}',{other.Id},2,1000);
                    INSERT INTO guild_settings(guild_id,tags) VALUES({i + 3},3);
                    """);
            var page = repository.SearchGuilds(new(2, Tags: 3, Page: 1));
            That(page.Total == 32 && page.Guilds.Length == 2 && page.Guilds[0].Name == "Find30" &&
                repository.SearchGuilds(new(2, Tags: 3, Page: 255)) is { Total: 32, Guilds.Length: 0 },
                "先按全部标签筛选统计，再按30条分页；越界页保留真实总数");
            That(repository.SearchGuilds(new(0, "申请")).Guilds is [{ Name: "申请公会", Mark: 3407890 }] &&
                repository.SearchGuilds(new(1, "AppMaster")).Guilds is [{ Name: "申请公会" }],
                "文字搜索区分公会名与会长名，查询不依赖客户端传公会ID");
            var towns = new TownCatalog([], room, room, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var masterSession = new GameSession(repository, towns, masterOwner);
            var memberSession = new GameSession(repository, towns, memberOwner);
            var applicantSession = new GameSession(repository, towns, applicantOwner);
            var otherSession = new GameSession(repository, towns, otherOwner);
            foreach (var session in new[] { masterSession, memberSession, applicantSession, otherSession })
            { session.Select(0); session.MarkReady(); }
            var actor = masterSession.CaptureGuildManagement()!;
            var ordinary = memberSession.CaptureGuildManagement()!;
            var pending = applicantSession.CaptureGuildManagement()!;
            var outsider = otherSession.CaptureGuildManagement()!;
            long now = 1_800_000_000;
            var first = repository.ApplyToGuild(pending, "申请公会", "请批准", 0, () => now, 300);
            That(first.Success && !first.Joined && repository.ApplyToGuild(pending, "申请公会", "请批准", 0, () => now + 1, 300).Success &&
                Number("SELECT count(*) FROM guild_applications WHERE guild_id=1") == 1 &&
                Number("SELECT created_at_unix FROM guild_applications WHERE guild_id=1") == now,
                "完全相同申请重试不重复插入、不重置时间；仍待审批且不提前加入");
            That(repository.CancelGuildApplication(outsider, 1).Error != 0 && repository.ReadGuildApplications(ordinary).Error == 36 &&
                repository.ReadGuildApplications(actor).Rows is [{ Name: "AppApplicant", Message: "请批准", Region: 0 }],
                "取消只作用于当前角色，普通成员不读审批表，会长读权威角色资料");
            That(repository.ApplyToGuild(pending, "其他公会", "另一个申请", 0, () => now, 300).Success,
                "未加入前可保留另一公会待审申请");
            That(repository.DecideGuildApplication(actor, applicant.Id, 1, true, () => now, 300).Error != 0 &&
                repository.DecideGuildApplication(actor, applicant.Id, 0, true, () => now, 2).Error == 38 &&
                Number("SELECT count(*) FROM guild_applications") == 2,
                "错误区服和容量失败都保留待审申请，不能丢掉客户端乐观移除的行");
            Sql($"CREATE TRIGGER application_abort AFTER INSERT ON guild_members WHEN NEW.character_id={applicant.Id} BEGIN SELECT RAISE(ABORT,'application rollback'); END");
            Reject<SqliteException>(() => repository.DecideGuildApplication(actor, applicant.Id, 0, true, () => now, 300),
                "批准的真实SQL末段失败");
            That(Number("SELECT count(*) FROM guild_applications") == 2 &&
                Number($"SELECT count(*) FROM guild_members WHERE character_id={applicant.Id}") == 0,
                "失败同事务回滚成员及所有申请，不提前发布加入");
            Sql("DROP TRIGGER application_abort");
            var approved = repository.DecideGuildApplication(actor, applicant.Id, 0, true, () => now, 300);
            That(approved.Success && approved.Joined && approved.Members!.Length == 3 &&
                Number("SELECT count(*) FROM guild_applications") == 0 &&
                Number($"SELECT grade FROM guild_members WHERE character_id={applicant.Id}") == 4,
                "批准原子加入普通职位并清除该角色全部公会申请，返回真实成员投影");
            That(repository.DecideGuildApplication(actor, applicant.Id, 0, true, () => now, 300).Error != 0,
                "重复批准不增加成员或产生新的成功提交");
            Sql($"DELETE FROM guild_members WHERE character_id={applicant.Id}; INSERT INTO guild_membership_cooldowns VALUES({applicant.Id},{now + 60})");
            That(repository.ApplyToGuild(pending, "申请公会", "", 0, () => now, 300).Error == 103,
                "申请入口遵守主动退会限制，不能从搜索绕过");
            Sql("INSERT INTO guild_settings(guild_id,join_mode) VALUES(1,1) ON CONFLICT(guild_id) DO UPDATE SET join_mode=1");
            var direct = repository.ApplyToGuild(pending, "申请公会", "", 0, () => now + 60, 300);
            That(direct.Success && direct.Joined && Number($"SELECT guild_id FROM guild_members WHERE character_id={applicant.Id}") == 1,
                "限制到期时开放直接加入策略，状态来自事务内设置");
            applicantSession.ReturnToRoster();
            That(repository.ApplyToGuild(pending, "其他公会", "", 0, () => now + 61, 300).Error != 0,
                "选角代次失效后旧申请请求不能写新角色关系");
            foreach (var session in new[] { masterSession, memberSession, applicantSession, otherSession }) session.End();
            That(Number("SELECT count(*) FROM pragma_foreign_key_check") == 0, "申请和成员事务保留隔离库外键完整性");
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
    }

    internal static GuildApplicationRow[] DecodeApplications(byte[] body)
    {
        using var reader = new BinaryReader(new MemoryStream(body));
        That(reader.ReadByte() == 1, "申请列表使用成功正文");
        uint count = reader.ReadUInt32();
        var rows = new List<GuildApplicationRow>();
        for (uint i = 0; i < count; i++)
        {
            ushort id = checked((ushort)reader.ReadUInt32());
            string name = ReadText(reader);
            byte job = reader.ReadByte(), grow = reader.ReadByte(), level = reader.ReadByte();
            That(reader.ReadByte() == 0, "申请行的保留标志为0");
            string message = ReadText(reader);
            uint created = reader.ReadUInt32();
            That(reader.ReadByte() == 0, "申请行分组标志为0");
            byte region = reader.ReadByte();
            rows.Add(new(id, "", name, job, grow, level, message, created, region));
        }
        That(reader.BaseStream.Position == body.Length, "独立申请解码器完整消费正文，无字段错位或尾随垃圾");
        return rows.ToArray();
    }

    internal static string ReadText(BinaryReader reader) => CharacterName.Encoding.GetString(reader.ReadBytes(reader.ReadInt32()));
    internal static byte[] Text(string text)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(text);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(bytes.Length); writer.Write(bytes); return stream.ToArray();
    }
}
