using System.Globalization;
using System.Text;
using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildCreateGmTests
{
    public static void RunResources(TownCatalog towns, CharacterCatalog characters, ItemResources resources, PvfArchive pvf)
    {
        int checks = Count;
        string path = Path.Combine(Path.GetTempPath(), "guild-gm-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            var catalog = new DevelopmentCatalog(pvf, resources, null);
            int cost = catalog.ReadGuildParameters().CreateCost;
            var inventory = new InventoryService(resources.Items, resources.GoldLimits, resources.EpicPieces);
            Character New(AccountOwners.Lease owner, string name, int gold)
            {
                var character = repository.Create(owner, new NewCharacter(name, towns.Room, towns.InitialReturn)
                    { Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills });
                return InventoryService.Commit(repository, owner, character, inventory.Grant(character, [new(0, gold)]));
            }
            Character rich, poor, clash;
            using (var owner = owners.Acquire("guild-gm", static () => { }))
            {
                rich = New(owner, "GmRich", cost + 1000);
                poor = New(owner, "GmPoor", Math.Max(0, cost - 1));
                clash = New(owner, "GmClash", cost + 1000);
            }
            string Answer(string command) => DevelopmentGm.Answer("account guild-gm " + command, "unused", repository,
                owners, static _ => null, catalog: catalog);
            string Create(string character, string name, int? expected = null) => Answer("guild-create " + character + " " +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(name)) + " " + (expected ?? cost).ToString(CultureInfo.InvariantCulture));
            object? Query(string sql)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open(); using var query = db.CreateCommand(); query.CommandText = sql; return query.ExecuteScalar();
            }
            using (var info = JsonDocument.Parse(Answer("guild-create-info GmRich")[3..]))
                That(info.RootElement.GetProperty("createCost").GetInt32() == cost, "GM读取当前PVF真实建会费用");
            That(Create("GmRich", "测试 公会", cost + 1).StartsWith("ERR ", StringComparison.Ordinal), "费用变化拒绝，不扣费");
            That(Answer("guild-create GmRich //// " + cost).StartsWith("ERR ", StringComparison.Ordinal), "非法UTF8公会名拒绝");
            That(Create("GmRich", "超出七个汉字的非法公会名").StartsWith("ERR ", StringComparison.Ordinal), "超长公会名拒绝");
            That(Create("GmPoor", "PoorGuild").StartsWith("ERR ", StringComparison.Ordinal), "金币不足拒绝");
            using (var held = owners.Acquire("guild-gm", static () => { }))
                That(Create("GmRich", "测试 公会").StartsWith("ERR ", StringComparison.Ordinal), "GM不接管在用账号");
            _ = Query("CREATE TRIGGER fail_guild_member BEFORE INSERT ON guild_members BEGIN SELECT RAISE(ABORT,'guild rollback'); END");
            That(Create("GmRich", "测试 公会").StartsWith("ERR ", StringComparison.Ordinal), "成员写入晚失败拒绝建会");
            That(Query("SELECT count(*) FROM guilds") is 0L && Query("SELECT gold FROM characters WHERE id=" + rich.Id) is long gold &&
                gold == cost + 1000, "晚失败完整回滚费用与公会，不残留单边成员");
            _ = Query("DROP TRIGGER fail_guild_member");
            string result = Create("GmRich", "测试 公会");
            That(result.StartsWith("OK ", StringComparison.Ordinal), "含中文和空格的公会名通过真实GM创建");
            using (var receipt = JsonDocument.Parse(result[3..]))
            {
                long guild = receipt.RootElement.GetProperty("guildId").GetInt64();
                That(guild > 0 && receipt.RootElement.GetProperty("characterId").GetInt32() == rich.Id &&
                    repository.GuildOf(rich.Id) == guild && Query("SELECT master_character_id FROM guilds") is long master && master == rich.Id,
                    "成功凭据为真实公会和会长关系");
            }
            That(Query("SELECT count(*) FROM guild_members") is 1L && Query("SELECT gold FROM characters WHERE id=" + rich.Id) is 1000L,
                "GM建会只写真实会长一人，扣费一次");
            That(Create("GmClash", "测试 公会").StartsWith("ERR ", StringComparison.Ordinal), "同名建会拒绝");
            That(Create("GmRich", "SecondGuild").StartsWith("ERR ", StringComparison.Ordinal), "已有公会拒绝重复建会");
            That(Query("SELECT count(*) FROM guilds") is 1L && Query("SELECT gold FROM characters WHERE id=" + clash.Id) is long remaining &&
                remaining == cost + 1000 && Query("SELECT gold FROM characters WHERE id=" + rich.Id) is 1000L,
                "重名和重复请求均不重复扣费或建会");
            using var reloadedOwner = owners.Acquire("guild-gm", static () => { });
            That(repository.LoadCharacter(reloadedOwner, rich.Slot).Guild?.Name == "测试 公会", "重新载入角色读到真实公会");
        }
        finally
        {
            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file)) TestFiles.DeleteFile(file);
        }
        Console.WriteLine($"PASS {Count - checks} GM guild creation assertions");
    }
}
