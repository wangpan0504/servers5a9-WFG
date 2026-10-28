using DfoServer.Game.Accounts;
using System.Buffers.Binary;
using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildAppearanceTests
{
    internal static GuildAppearanceCatalog Catalog() => new("[guild mark]\n5 2000\n[/guild mark]",
        "<ROOT>" + string.Concat(Enumerable.Range(1, 15).Select(id => $"<IControl ID=\"STR:{id}\"/>")) + "</ROOT>",
        "[insignia mark]\n`mark0.ani` `mark1.ani`\n[/insignia mark]\n[insignia frame]\n`frame0.ani`\n[/insignia frame]",
        "[guild mark info]\n" + string.Concat(Enumerable.Range(0, 20).Select(index =>
            index < 4 ? "`low class` 1 " : index < 16 ? "`mid class` 3 " : "`conqueror` 10 ")) +
        "\n[/guild mark info]");

    public static void RunCore()
    {
        int before = Count;
        var catalog = Catalog();
        That(catalog.Marks == 15 && catalog.FreeMarks == 5 && catalog.MarkPrice == 2000 &&
            catalog.AuraMarks == 2 && catalog.AuraFrames == 1, "会标费用、可选索引及装饰范围分别取资源");
        That(catalog.ValidAura(1, 0) && catalog.ValidAura(uint.MaxValue, uint.MaxValue) &&
            !catalog.ValidAura(2, 0) && !catalog.ValidAura(uint.MaxValue, 0) && !catalog.ValidAura(0, uint.MaxValue),
            "装饰范围按声明验证，清除必须双FFFFFFFF");
        That(catalog.ModernMarks == 20 && catalog.ValidModernMark(3u | 3u << 16, 0, 1) &&
            !catalog.ValidModernMark(4, 0, 10) && !catalog.ValidModernMark(4, 1, 2) &&
            catalog.ValidModernMark(4, 1, 3) && !catalog.ValidModernMark(19, 4, 9) &&
            catalog.ValidModernMark(19, 4, 10) && !catalog.ValidPackedMark(20) &&
            !catalog.ValidPackedMark(4u << 16) && !catalog.ValidPackedMark(0x100),
            "现代20图案按真实档位及等级解锁，四种背景全开放且保留位拒绝");
        That(GuildAppearanceRequests.Mark([7, 0, 0, 0]) == new GuildAppearanceChange(false, 7),
            "787按本版u32解析而非其他版本单字节");
        That(GuildAppearanceRequests.Mark([7]) == new GuildAppearanceChange(false, 7, LegacyMark: true),
            "同版旧选择器确有单字节发送，必须显式兼容");
        Reject<InvalidDataException>(() => GuildAppearanceRequests.Mark([7, 1, 0, 0]), "拒绝packed组件间非零保留位");
        Reject<InvalidDataException>(() => GuildAppearanceRequests.Mark([7, 0, 0, 0, 0]), "拒绝会标多余尾部");
        That(GuildAppearanceRequests.Aura([1, 0, 0, 0, 0, 0, 0, 0]) ==
            new GuildAppearanceChange(true, AuraMark: 1, AuraFrame: 0), "834读取两个独立u32");
        Reject<InvalidDataException>(() => GuildAppearanceRequests.Aura([0, 0, 0, 0]), "拒绝半组装饰正文");
        That(GuildAppearancePackets.MarkResult(0).SequenceEqual(new byte[] { 1 }) &&
            GuildAppearancePackets.MarkResult(22).SequenceEqual(new byte[] { 0, 22, 0 }),
            "会标成功不附虚构字段，失败沿本版u16框架");
        byte[] information = GuildPackets.Information(new(1, "外观公会", 10, 1, "会长", "", 0, 1000)
            { Tier = 4 }, 0);
        That(BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(information.Length - 29)) == 4 &&
            BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(information.Length - 25)) == 10,
            "NOTI70现代模型尾部携真实tier与level，图案解锁不再全部因零等级锁定");
        RunRepository(catalog);
        Console.WriteLine($"PASS {Count - before} 公会会标、装饰权限与原子费用断言");
    }

    static void RunRepository(GuildAppearanceCatalog catalog)
    {
        string root = Path.Combine(Path.GetTempPath(), "guild-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var masterOwner = owners.Acquire("g-master", static () => { });
            using var memberOwner = owners.Acquire("g-member", static () => { });
            var room = new Location(38, 1, 450, 234, 0);
            var master = repository.Create(masterOwner, new("LookMaster", room, room));
            var member = repository.Create(memberOwner, new("LookMember", room, room));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long Number(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix,public_funds)
                VALUES(1,'外观测试','appearance',{master.Id},1000,4000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix)
                VALUES(1,{master.Id},0,1000),(1,{member.Id},1,1000);
                """);
            var towns = new TownCatalog([], room, room, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var masterSession = new GameSession(repository, towns, masterOwner);
            var memberSession = new GameSession(repository, towns, memberOwner);
            masterSession.Select(0); masterSession.MarkReady();
            memberSession.Select(0); memberSession.MarkReady();
            var actor = masterSession.CaptureGuildManagement()!;
            var ordinary = memberSession.CaptureGuildManagement()!;
            GuildAppearanceCommit Change(GuildAppearanceChange change) => repository.ChangeGuildAppearance(actor, change, catalog);
            That(repository.ReadGuildAppearance(masterOwner, master.Id) == new GuildAppearance(),
                "存量无外观行使用空装饰，而非零号装饰");
            That(repository.ChangeGuildAppearance(ordinary, new(false, 7, LegacyMark: true), catalog).Error == 36 &&
                Number("SELECT public_funds FROM guilds") == 4000, "伪grade1普通成员不能改会标或扣公共积分");
            That(Change(new(false, 7, LegacyMark: true)).Success && Number("SELECT public_funds FROM guilds") == 2000,
                "付费会标与公共积分扣款同事务提交");
            That(Change(new(false, 7, LegacyMark: true)).Success && Number("SELECT public_funds FROM guilds") == 2000,
                "重复同值请求不重复收费");
            That(Change(new(true, AuraMark: 1, AuraFrame: 0)).Success && Number("SELECT public_funds FROM guilds") == 2000,
                "装饰提交不消耗公共积分并保留会标");
            That(Change(new(true, AuraMark: uint.MaxValue, AuraFrame: 0)).Error == 34 &&
                repository.ReadGuildAppearance(masterOwner, master.Id) == new GuildAppearance(7, 1, 0),
                "半组清除不改持久外观");
            Sql("CREATE TRIGGER g_appearance_abort AFTER UPDATE ON guild_appearance BEGIN SELECT RAISE(ABORT,'appearance rollback'); END");
            Reject<SqliteException>(() => Change(new(false, 8, LegacyMark: true)), "外观写失败向事务调用方暴露异常");
            That(Number("SELECT public_funds FROM guilds") == 2000 && Number("SELECT mark FROM guild_appearance") == 7,
                "外观写失败将此前扣款一并回滚");
            Sql("DROP TRIGGER g_appearance_abort");
            That(Change(new(false, 8, LegacyMark: true)).Success && Change(new(false, 9, LegacyMark: true)).Error == 22 &&
                Number("SELECT public_funds FROM guilds") == 0 && Number("SELECT mark FROM guild_appearance") == 8,
                "余额不足保留上次外观及零余额");
            That(Change(new(false, 1)).Success && Change(new(true)).Success &&
                repository.ReadGuildAppearance(masterOwner, master.Id) == new GuildAppearance(1),
                "免费会标在零余额仍可更换且清除装饰持久化");
            That(Change(new(false, 4)).Error == 34 && Change(new(false, 3u | 3u << 16)).Success &&
                Number("SELECT public_funds FROM guilds") == 0, "现代图案成长门槛拒绝越级，合法组合在零余额可提交");
            Sql("UPDATE guilds SET tier=4,level=10 WHERE guild_id=1");
            That(Change(new(false, 19u | 3u << 16)).Success &&
                repository.ReadGuildAppearance(masterOwner, master.Id) == new GuildAppearance(19u | 3u << 16),
                "现代图案读取真实持久档位，不把旧level当跨档总等级");
            That(repository.ChangeGuildAppearance(actor with { IsCurrent = () => false }, new(false, 2), catalog).Error == 34 &&
                Number("SELECT mark FROM guild_appearance") == (19u | 3u << 16), "旧会话不能修改外观");
            var reopened = new CharacterRepository(path);
            That(reopened.ReadGuildAppearance(masterOwner, master.Id) == new GuildAppearance(19u | 3u << 16), "重开数据库保留外观");
            Sql("DELETE FROM guilds WHERE guild_id=1");
            That(Number("SELECT count(*) FROM guild_appearance") == 0 && Number("SELECT count(*) FROM pragma_foreign_key_check") == 0,
                "解散外键清除外观，不留下孤立记录");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
