using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Guild;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

/// <summary>
/// 创建公会窗口的两次查重：文本规则与请求解析用合成输入，查重命中用真实临时库验证
/// 「以唯一索引为唯一依据」这条合同。
/// </summary>
internal static class GuildDirectoryTests
{
    sealed class Stub : IGuildDirectoryStore
    {
        public readonly HashSet<string> Names = ["BUG公会"];
        public readonly HashSet<string> Addresses = ["bug-guild"];
        public bool NameTaken(string name) => Names.Contains(name);
        public bool AddressTaken(string address) => Addresses.Contains(address);
        public long? GuildOf(ushort characterId) => null;
        public GuildMembership? GuildMembershipOf(ushort characterId) => null;
        public GuildPanel? ReadGuildPanel(Game.Accounts.AccountOwners.Lease owner, ushort characterId) => null;
        public GuildPermitTarget? FindPermitTarget(string name) => null;
        public GuildCreateResult CreateGuild(Game.Accounts.AccountOwners.Lease owner, Game.Characters.Character expected, GuildCreatePlan plan) =>
            throw new NotSupportedException("查重测试不覆盖建会写路径");
        public Game.Characters.Character? LoadCharacterById(Game.Accounts.AccountOwners.Lease owner, ushort characterId) =>
            throw new NotSupportedException("查重测试不覆盖离线建会读取路径");
    }

    public static void RunCore()
    {
        int before = Count;
        var directory = new GuildDirectory(new Stub(), GuildParameterTests.Synthetic());

        That(directory.CheckName("新公会") == GuildTextStatus.Available, "未占用的公会名可用");
        That(directory.CheckName("") == GuildTextStatus.Length, "空文本按长度违规拒绝");
        That(directory.CheckName("BUG公会") == GuildTextStatus.Taken,
            "★ 已占用名称返回 Taken：客户端据此弹「此公会名称已被其他公会使用」");
        That(directory.CheckAddress("bug-guild") == GuildTextStatus.Taken && directory.CheckAddress("new-guild") == GuildTextStatus.Available,
            "地址查重与名称同样以存储唯一索引为准");
        That(directory.CheckName(new string('a', 15)) == GuildTextStatus.Length, "超过 14 字节的名称按长度违规拒绝");
        That(directory.CheckName("召唤师公会") == GuildTextStatus.Available && directory.CheckName("abcdefghijklmn") == GuildTextStatus.Available,
            "7 个汉字（CP936 14 字节）与 14 个 ASCII 都在上限内");
        That(directory.CheckName("BUG 公会") == GuildTextStatus.Available && directory.CheckName("\u0001公会") == GuildTextStatus.Illegal,
            "★ 非法字符只按控制字符拒绝：不擅自收窄客户端允许的字符集");

        // 实机样本：`07 00 00 00 42 55 47 B9 AB BB E1` = u32 长度 7 + CP936「BUG公会」。
        byte[] sample = [0x07, 0x00, 0x00, 0x00, 0x42, 0x55, 0x47, 0xB9, 0xAB, 0xBB, 0xE1];
        That(GuildRequests.Text(sample) == "BUG公会", "★ 实机样本按 u32 长度 + CP936 解出「BUG公会」");
        Reject<InvalidDataException>(() => GuildRequests.Text([0x07, 0x00, 0x00, 0x00, 0x41]), "★ 正文宽度与长度前缀不符必须拒绝");
        Reject<InvalidDataException>(() => GuildRequests.Text([0x01, 0x00]), "长度前缀缺失必须拒绝");

        RunCoreRepository();
        Console.WriteLine($"PASS {Count - before} 公会查重规则、请求解析与真实库查重断言");
    }

    static void RunCoreRepository()
    {
        string path = Path.Combine(Path.GetTempPath(), "guild-directory-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var repository = new CharacterRepository(path);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, ForeignKeys = true }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "INSERT INTO guilds(name,address,master_character_id,created_at_unix) VALUES('BUG公会','bug-guild',1,1700000000)";
                command.ExecuteNonQuery();
            }
            That(repository.NameTaken("BUG公会") && !repository.NameTaken("新公会"),
                "★ 仓储查重直接命中 guilds.name 唯一索引（不另建内存去重）");
            That(repository.AddressTaken("bug-guild") && !repository.AddressTaken("new-guild"),
                "地址查重同样命中 guilds.address 唯一索引");
        }
        finally
        {
            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file))
                    TestFiles.DeleteFile(file);
        }
    }
}
