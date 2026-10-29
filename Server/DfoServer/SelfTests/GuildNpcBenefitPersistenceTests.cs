using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildNpcBenefitPersistenceTests
{
    sealed class Clock(long unix) : TimeProvider
    {
        public long Unix { get; set; } = unix;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Unix);
    }

    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "guild-npc-benefits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Schema(Path.Combine(root, "schema.db"));
            Repository(Path.Combine(root, "benefits.db"));
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - before} guild NPC benefit persistence assertions");
    }

    static void Schema(string path)
    {
        _ = new CharacterRepository(path);
        using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
        void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        long Number(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        That(Number("SELECT count(*) FROM guild_npc_common_benefits") == 0 &&
            Number("SELECT count(*) FROM character_guild_npc_benefits") == 0 &&
            Number("SELECT count(*) FROM guild_npc_training_receipts") == 0,
            "新库创建空福利与训练回执，不补造历史购买");
        Sql("DROP TABLE character_guild_npc_item_crafts; DROP TABLE guild_npc_training_receipts; DROP TABLE character_guild_npc_benefits; DROP TABLE guild_npc_common_benefits; PRAGMA user_version=145");
        Sql("CREATE TABLE guild_npc_training_receipts(request_id TEXT PRIMARY KEY, selection TEXT NOT NULL)");
        Reject<InvalidDataException>(() => new CharacterRepository(path), "福利升级拒绝覆盖同名损坏表");
        That(Number("PRAGMA user_version") == 145 && Number("SELECT count(*) FROM sqlite_schema WHERE name IN('guild_npc_common_benefits','character_guild_npc_benefits')") == 0,
            "第三张福利表碰撞时前两张表和版本一起回滚");
        Sql("DROP TABLE guild_npc_training_receipts");
        _ = new CharacterRepository(path);
        That(Number("PRAGMA user_version") == SqliteMigrations.CurrentVersion &&
            Number("SELECT count(*) FROM guild_npc_common_benefits") == 0,
            "145库真实升级到福利持久化结构");
        Sql("CREATE TRIGGER benefit_reopen_probe BEFORE INSERT ON guild_npc_common_benefits BEGIN SELECT RAISE(ABORT,'reopen probe'); END");
        _ = new CharacterRepository(path);
        That(Number("SELECT count(*) FROM sqlite_schema WHERE name='benefit_reopen_probe'") == 1,
            "重开福利库不重建或覆盖已有业务表");
        Sql($"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
        Reject<InvalidDataException>(() => new CharacterRepository(path), "福利库拒绝向下打开未来版本");
        Sql($"PRAGMA user_version={SqliteMigrations.CurrentVersion}");
        Sql("DROP TABLE guild_npc_training_receipts; CREATE TABLE guild_npc_training_receipts(request_id TEXT PRIMARY KEY, selection TEXT NOT NULL)");
        Reject<InvalidDataException>(() => new CharacterRepository(path), "当前版本也拒绝缺少外键及JSON约束的训练回执");
    }

    static void Repository(string path)
    {
        const long now = 1700000000;
        var repository = new CharacterRepository(path); var owners = new AccountOwners();
        using var owner = owners.Acquire("benefit-master", static () => { });
        var location = new Location(38, 1, 450, 234, 0);
        var character = repository.Create(owner, new("BenefitLead", location, location));
        using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
        void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        long Number(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        string Snapshot()
        {
            var rows = new List<string>();
            foreach (string table in new[] { "characters", "character_inventory_items", "inventory_audit_log", "currency_audit_log", "guilds", "guild_settings",
                "guild_npc_common_benefits", "character_guild_npc_benefits", "guild_npc_receipts",
                "guild_request_claims", "guild_npc_training_receipts" })
            {
                using var query = db.CreateCommand(); query.CommandText = "SELECT * FROM " + table + " ORDER BY rowid";
                using var reader = query.ExecuteReader();
                while (reader.Read()) rows.Add(table + ":" + string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
            }
            return string.Join("\n", rows);
        }
        Sql($"""
            INSERT INTO guilds(guild_id,name,address,master_character_id,public_funds,created_at_unix)
            VALUES(1,'福利公会','benefit-guild',{character.Id},1000,1000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{character.Id},1,1000);
            UPDATE characters SET gold=1000 WHERE id={character.Id};
            INSERT INTO guild_npc_progress(guild_id,npc_id,level,contract_until) VALUES(1,400001064,2,{now + 600});
            INSERT INTO guild_npc_progress(guild_id,npc_id,level,contract_until) VALUES(1,400001065,1,{now + 600});
            """);
        var towns = new TownCatalog([], location, location, new(0, [], []), [], new Dictionary<uint, uint[]>());
        var session = new GameSession(repository, towns, owner); session.Select(character.Slot); session.MarkReady();
        var actor = session.CaptureGuildManagement()!; var npcs = GuildNpcTests.Synthetic();
        var common = new GuildNpcBenefitDefinition(6001, 400001065, 1, true, "emblem compound buff", "public funds", 100, "none", 0, 60, [1, 3]);
        var personal = common with { Id = 6002, Common = false, FirstCurrency = "gold", FirstAmount = 20, DurationSeconds = 120, Parameters = [2, 3] };
        var training = new GuildNpcBenefitDefinition(5001, 400001064, 1, false, "guild trainer attack raid buff", "gold", 40, "none", 0, 0, [1281, 1, 25]);
        var next = training with { Id = 5002, Level = 2, Parameters = [1281, 2, 30] };
        var critical = training with { Id = 5005, Effect = "guild trainer critical raid buff" };
        var catalog = Catalog(common, personal, training, next, critical);
        using var invitations = new GuildInvitations();
        var management = new GuildManagementService(repository, new(), invitations, new());
        var clock = new Clock(now);
        var service = new GuildNpcBenefitService(repository, npcs, catalog,
            new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"), management, clock);
        Character Current() => repository.LoadCharacter(owner, character.Slot);
        string Key(int value) => value.ToString("x64");
        GuildNpcBenefitCommit Buy(GuildNpcBenefitDefinition definition, int key, long time = now, Character? expected = null) =>
            repository.PurchaseGuildNpcBenefit(actor, expected ?? Current(), npcs, definition, Key(key), time, current =>
            {
                var paid = CurrencyService.Spend(current.Inventory, CurrencyKind.Gold, definition.FirstAmount);
                return paid.Accepted ? new(current.Inventory, paid.Plan!.After, "guild_npc_benefit_purchase", new(current)) : null;
            });
        GuildNpcBenefitCommit Select(int key, params uint[] ids) => repository.SelectGuildNpcTraining(actor, catalog, ids, Key(key), now);

        string unchanged = Snapshot();
        That(!Buy(next, 1).Success && unchanged == Snapshot(), "训练不能跳过前置档位，拒绝不扣费");
        That(!Select(2, 5001).Success && unchanged == Snapshot(), "尚未解锁的训练不能选用");
        Sql($"UPDATE guild_npc_progress SET contract_until={now} WHERE npc_id=400001065"); unchanged = Snapshot();
        That(!Buy(common, 3).Success && !Buy(personal, 4).Success && unchanged == Snapshot(),
            "契约恰好到期时公有和个人购买均拒绝且资产不变");
        Sql($"UPDATE guild_npc_progress SET contract_until={now + 600} WHERE npc_id=400001065; UPDATE guilds SET public_funds=99");
        unchanged = Snapshot();
        That(Buy(common, 3).Error == 17 && unchanged == Snapshot(), "公款不足不能保存福利或占用请求");
        Sql("UPDATE guilds SET public_funds=1000"); unchanged = Snapshot();
        Sql("CREATE TRIGGER benefit_receipt_fail BEFORE INSERT ON guild_npc_receipts BEGIN SELECT RAISE(ABORT,'benefit receipt rollback'); END");
        Reject<SqliteException>(() => Buy(common, 5), "公有购买末段回执故障");
        That(unchanged == Snapshot(), "公有福利失败时扣款、状态、revision及回执全部回滚");
        Reject<SqliteException>(() => Buy(personal, 6), "个人购买末段回执故障");
        That(unchanged == Snapshot(), "个人福利失败时金币、背包、角色revision及统一请求归属全部回滚");
        Sql("DROP TRIGGER benefit_receipt_fail");
        var bought = Buy(common, 5);
        That(bought.Success && bought.State!.Common is [{ BenefitId: 6001, Remaining: 3, Deadline: now + 60 }] &&
            Number("SELECT public_funds FROM guilds") == 900, "公有购买保存使用次数和绝对期限，只扣一次公款");
        That(service.SuccessPercent(actor, "emblem compound buff", 3) == 1 &&
            service.SuccessPercent(actor, "emblem compound buff", 2) == 0 &&
            service.SuccessPercent(actor, "equip upgrade buff", 3) == 0 &&
            service.SuccessPercent(null, "emblem compound buff", 3) == 0,
            "成功率只使用当前公会的精确效果和目标档位，没有身份不加成");
        unchanged = Snapshot();
        That(Buy(common, 5).Replayed && !Buy(personal, 5).Success && unchanged == Snapshot(),
            "同请求精确重放不扣费，替换福利参数不能复用");
        var stale = Current(); bought = Buy(personal, 6);
        That(bought.Success && bought.Character!.Inventory.Gold == 980 && bought.Inventory is not null &&
            bought.State!.Individual is [{ BenefitId: 6002, Remaining: 3, Deadline: now + 120 }],
            "个人购买同事务扣金币并返回已提交角色和福利");
        That(service.SuccessPercent(actor, "emblem compound buff", 3) == 2,
            "公有和个人成功率取较强一项，不把两份增益相加");
        Sql($"UPDATE guild_npc_progress SET contract_until={now} WHERE npc_id=400001065");
        That(service.SuccessPercent(actor, "emblem compound buff", 3) == 0,
            "购买仍在期限内但NPC契约到期时，公有个人成功率同时停用");
        Sql($"UPDATE guild_npc_progress SET contract_until={now + 600} WHERE npc_id=400001065");
        unchanged = Snapshot();
        That(!Buy(personal, 7, expected: stale).Success && Buy(personal, 6).Replayed && unchanged == Snapshot(),
            "旧角色revision不能新扣款，精确重放不再次改变资产");
        That(Buy(personal, 7, now + 10).Success && Number("SELECT count(*) FROM character_guild_npc_benefits") == 1 &&
            repository.ReadGuildNpcBenefits(actor, now + 10)!.Individual is [{ Deadline: now + 130 }],
            "同类型个人限时福利重新购买覆盖期限，不叠加重复记录");
        var expensive = personal with { FirstAmount = 1001 }; unchanged = Snapshot();
        That(Buy(expensive, 8).Error == 17 && unchanged == Snapshot(), "个人资产不足时福利、回执和revision不变");
        Sql($"INSERT INTO guild_request_claims VALUES('{Key(9)}',2,'{owner.Account}',{character.Id})"); unchanged = Snapshot();
        That(!Buy(common, 9).Success && unchanged == Snapshot(), "福利不能复用其他资产业务的请求ID");

        That(Buy(training, 10).Success && Buy(next, 11).Success && Buy(critical, 12).Success,
            "训练按前置档位永久解锁，并分别收取费用");
        unchanged = Snapshot();
        That(!Buy(training, 13).Success && Buy(training, 10).Replayed && unchanged == Snapshot(),
            "训练永久解锁不能重复付费，旧购买仍可精确重放");
        That(!Select(14, 5001, 5002).Success && !Select(14, 5001, 5001).Success && unchanged == Snapshot(),
            "训练选择拒绝同类型多档及重复福利");
        That(Select(15, 5001, 5005).Success, "允许同时选择已解锁的不同训练类型");
        unchanged = Snapshot();
        That(Select(15, 5005, 5001).Replayed && !Select(15, 5002, 5005).Success && unchanged == Snapshot(),
            "训练选择重放忽略数组顺序，但不能改变原请求的选择集合");
        Sql("CREATE TRIGGER training_receipt_fail BEFORE INSERT ON guild_npc_training_receipts BEGIN SELECT RAISE(ABORT,'training receipt rollback'); END");
        Reject<SqliteException>(() => Select(16, 5002), "训练选择末段明细回执故障");
        That(unchanged == Snapshot(), "训练选择失败时原选择、公会revision、主回执和claim全部回滚");
        Sql("DROP TRIGGER training_receipt_fail");
        That(Select(16, 5002).Success, "训练选择失败后可用原请求重试成功");
        var reopened = new CharacterRepository(path);
        var restored = reopened.ReadGuildNpcBenefits(actor, now)!;
        That(restored.Common is [{ BenefitId: 6001, Deadline: now + 60 }] &&
            restored.Individual.Single(row => row.BenefitId == 5002) is { Deadline: 0, Selected: true } &&
            restored.Individual.Where(row => row.BenefitId is 5001 or 5005).All(row => !row.Selected) &&
            reopened.LoadCharacter(owner, character.Slot).Inventory.Gold == 840,
            "重开仓储恢复公有个人期限、训练解锁和精确选择，资产不重复扣款");
        restored = reopened.ReadGuildNpcBenefits(actor, now + 130)!;
        That(restored.Common.Length == 0 && restored.Individual.Length == 3 && restored.Individual.All(row => row.Deadline == 0),
            "期限恰好到达时限时福利消失，永久训练保持解锁");
        clock.Unix = now + 130;
        That(service.SuccessPercent(actor, "emblem compound buff", 3) == 0,
            "购买期限恰好到达时不再向成功率消费者提供加成");
        clock.Unix = now;
        That(Select(17).Success && reopened.ReadGuildNpcBenefits(actor, now)!.Individual.All(row => !row.Selected),
            "空训练选择清除当前选择但保留所有解锁");
        Sql($"UPDATE guild_members SET joined_at_unix=1001 WHERE character_id={character.Id}"); unchanged = Snapshot();
        That(reopened.ReadGuildNpcBenefits(actor, now)!.Individual.Length == 0 && !Buy(training, 10).Success &&
            !Select(16, 5002).Success && unchanged == Snapshot(), "退出重入不继承旧个人福利、训练或请求回执");
    }

    static GuildNpcBenefitCatalog Catalog(params GuildNpcBenefitDefinition[] definitions)
    {
        var strings = new List<string>(); var tokens = new List<PvfToken>();
        void Text(byte kind, string value) { strings.Add(value); tokens.Add(new(kind, strings.Count - 1)); }
        void Number(int value) => tokens.Add(new(0, value));
        Text(3, "[guild trainer buff interval time]"); Number(180); Text(3, "[npc benefit info]");
        foreach (var row in definitions)
        {
            Number(checked((int)row.Id)); Number(checked((int)row.NpcId)); Number(row.Level);
            Text(7, row.Common ? "common" : "individual"); Text(7, row.Effect);
            Text(7, row.FirstCurrency); Number(row.FirstAmount); Text(7, row.SecondCurrency); Number(row.SecondAmount);
            Number(row.DurationSeconds / 60); Number(row.Parameters.Length);
            foreach (int parameter in row.Parameters) Number(parameter);
        }
        Text(3, "[/npc benefit info]");
        return new(tokens, index => strings[index], new HashSet<int>());
    }
}
