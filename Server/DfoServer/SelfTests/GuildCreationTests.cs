using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using PvfLib;

namespace DfoServer;

/// <summary>
/// 创建公会提交路径（阶段 2 的核心）：建会花费与建会**同事务**、失败整笔回滚不留半成品。
/// 请求包体与客户端应答尚未取证，因此本组只验证领域与存储层的行为。
/// </summary>
internal static class GuildCreationTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources resources, PvfArchive pvf)
    {
        GuildCreateGmTests.RunResources(towns, characters, resources, pvf);
        int before = Count;
        string path = Path.Combine(Path.GetTempPath(), "guild-create-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            var directory = new GuildDirectory(repository, new GuildParameterCatalog(pvf));
            var inventory = new InventoryService(resources.Items, resources.GoldLimits, resources.EpicPieces);
            using var owner = owners.Acquire("guild-create", () => { });

            var rich = repository.Create(owner, new NewCharacter("GuildRich", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills
            });
            rich = InventoryService.Commit(repository, owner, rich, inventory.Grant(rich, [new(0, 400_000)]));
            var poor = repository.Create(owner, new NewCharacter("GuildPoor", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills
            });
            poor = InventoryService.Commit(repository, owner, poor, inventory.Grant(poor, [new(0, 100_000)]));

            That(directory.CreateCost == 300000, "建会花费来自 PVF `[guild create cost]`：300000");
            That(repository.GuildOf(rich.Id) is null, "未入会的角色查不到公会");

            // ① 规划阶段的边界：金币不足、名称超长都必须在写库前拒绝。
            That(directory.PlanCreate(poor, "PoorGuild", "poor-guild", 1700000000).Error == GuildCreateError.NotEnoughGold,
                "★ 金币不足在规划阶段拒绝（客户端失败码 31 的依据）");
            That(directory.PlanCreate(rich, "这个公会名字实在太长了", "bug-guild", 1700000000).Error == GuildCreateError.InvalidText,
                "名称超过 14 字节按非法文本拒绝");

            // ② 正常建会：扣费与建会同事务，带回提交后的角色快照。
            var plan = directory.PlanCreate(rich, "BUG公会", "bug-guild", 1700000000).Plan!;
            That(plan.Cost == 300000 && plan.Inventory.After.Gold == rich.Inventory.Gold - 300_000,
                "规划里已经体现建会花费的扣减");
            var created = directory.Create(owner, rich, plan);
            That(created.Error == GuildCreateError.None && created.GuildId > 0 && created.Character is not null,
                "★ 建会提交成功并带回提交后的角色快照");
            That(created.Character!.Inventory.Gold == rich.Inventory.Gold - 300_000,
                "★ 提交后的快照就是扣费后的余额（与建会同事务）");
            That(repository.GuildOf(rich.Id) == created.GuildId, "建会后角色可查到自己所属的公会");
            using (var db = Open(path))
            {
                string expectedGuild = $"BUG公会:bug-guild:{rich.Id}:0:1:0:0";
                string expectedMember = $"{created.GuildId}:{rich.Id}:0:0";
                That(Query(db, "SELECT name||':'||address||':'||master_character_id||':'||tier||':'||level||':'||experience||':'||public_funds FROM guilds") is string guild && guild == expectedGuild,
                    "公会行按计划写入：名称、地址、会长、初始档位等级与基金");
                That(Query(db, "SELECT guild_id||':'||character_id||':'||grade||':'||contribution FROM guild_members") is string member && member == expectedMember,
                    "会长成员行同事务写入（职位编号待成员玩法取证后校正）");
                That(Query(db, "SELECT gold FROM characters WHERE id=" + rich.Id) is 100_000L,
                    "独立读库确认金币确实被扣且只扣一次");
            }

            // ③ 同名建会：事务内复核发现占用 ⇒ 返回错误且**金币不变**（回滚证据）。
            uint goldBefore = repository.List(owner).Single(role => role.Id == rich.Id).Inventory.Gold;
            var clash = repository.Create(owner, new NewCharacter("GuildClash", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills
            });
            clash = InventoryService.Commit(repository, owner, clash, inventory.Grant(clash, [new(0, 400_000)]));
            var clashPlan = directory.PlanCreate(clash, "BUG公会", "another-guild", 1700000001).Plan!;
            var rejected = directory.Create(owner, clash, clashPlan);
            That(rejected.Error == GuildCreateError.NameTaken && rejected.Character is null && rejected.GuildId == 0,
                "★ 同名公会在提交阶段被拒绝（并发建会的唯一索引兜底）");
            That(repository.List(owner).Single(role => role.Id == clash.Id).Inventory.Gold == 400_000,
                "★ 被拒的建会不扣钱：扣费与建会同事务、失败整笔回滚");
            That(repository.List(owner).Single(role => role.Id == rich.Id).Inventory.Gold == goldBefore,
                "被拒的建会也不影响已有角色余额");

            // ④ 同一角色重复建会：成员唯一约束生效。
            var again = directory.PlanCreate(rich, "SecondGuild", "second-guild", 1700000002);
            That(again.Error == GuildCreateError.AlreadyInGuild && again.Plan is null,
                "★ 已在公会的角色不能再次建会（客户端失败码 32 的依据）");
        }
        finally
        {
            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file))
                    TestFiles.DeleteFile(file);
        }
        Console.WriteLine($"PASS {Count - before} 创建公会事务、花费扣减与失败回滚断言");
    }

    static Microsoft.Data.Sqlite.SqliteConnection Open(string path)
    {
        var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        db.Open();
        return db;
    }

    static object? Query(Microsoft.Data.Sqlite.SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
