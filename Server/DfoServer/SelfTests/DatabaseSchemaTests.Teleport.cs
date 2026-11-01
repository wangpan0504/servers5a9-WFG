using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Town;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void AuraCooldownUpgrade(string root)
    {
        PrivilegeTeleportCooldownUpgrade(root);
        root = Path.Combine(root, "aura-cooldown");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        var repository = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("aura-test", () => { });
        var role = repository.Create(owner, new NewCharacter("AuraTest", new(38, 1, 450, 234), null)
            { Inventory = InventorySnapshot.Empty with { Gold = 9876 } });
        using (var db = Open(path))
        {
            RemoveSkillTreeExpansionSchema(db);
            Execute(db, """
                ALTER TABLE character_teleport_cooldowns DROP COLUMN upgrade_aura_until;
                ALTER TABLE character_teleport_cooldowns DROP COLUMN party_aura_until;
                ALTER TABLE character_teleport_cooldowns DROP COLUMN privilege_until;
                INSERT INTO character_teleport_cooldowns VALUES(1,'aura-test',1000,2000);
                CREATE INDEX aura_history_account ON character_teleport_cooldowns(account);
                PRAGMA user_version=80;
                """);
        }
        foreach (string scenario in new[] { "wrong-type", "missing-check", "late-failure" })
        {
            string damaged = Path.Combine(root, scenario + ".db");
            using (var source = Open(path))
            using (var target = Open(damaged)) source.BackupDatabase(target);
            using (var db = Open(damaged))
                Execute(db, scenario switch
                {
                    "wrong-type" => "ALTER TABLE character_teleport_cooldowns ADD COLUMN upgrade_aura_until TEXT NOT NULL DEFAULT '0'",
                    "missing-check" => "ALTER TABLE character_teleport_cooldowns ADD COLUMN upgrade_aura_until INTEGER NOT NULL DEFAULT 0",
                    _ => "ALTER TABLE accounts DROP COLUMN disabled"
                });
            string before = Snapshot(damaged);
            RejectDatabase(() => _ = new CharacterRepository(damaged));
            Check(Snapshot(damaged) == before, "升级冷却迁移遇到同名未知结构或末段验证失败，原DDL、旧冷却、角色与版本一起保留");
        }
        repository = new(path);
        var loaded = repository.LoadCharacter(owner, role.Slot);
        Check(JsonSerializer.Serialize(loaded) == JsonSerializer.Serialize(role with { TeleportCooldowns = new(1000, 2000) }) &&
            QueryOn(path, "SELECT upgrade_aura_until FROM character_teleport_cooldowns") is 0L &&
            Convert.ToInt32(QueryOn(path, "PRAGMA user_version")) == SqliteMigrations.CurrentVersion,
            "真实v80升级只补零值高级冷却，不重置原药剂与普通光环冷却、资产或revision");
        using (var db = Open(path))
        {
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='aura_history_account'") is 1L,
                "增量加列保留原冷却表索引");
            Reject<SqliteException>(() => Execute(db, "UPDATE character_teleport_cooldowns SET upgrade_aura_until=-1"), "高级冷却拒绝负截止时刻");
            Execute(db, "UPDATE character_teleport_cooldowns SET upgrade_aura_until=3000");
        }
        string stable = Snapshot(path);
        repository = new(path);
        Check(Snapshot(path) == stable && repository.LoadCharacter(owner, role.Slot).TeleportCooldowns == new TeleportCooldowns(1000, 2000, 3000),
            "迁移重开保留两类光环及药剂冷却，不重新延长或清零");
        repository.Delete(owner, repository.LoadDeletionTarget(owner, role.Slot));
        Check(QueryOn(path, "SELECT count(*) FROM character_teleport_cooldowns") is 0L, "删除角色同时清理两类个人传送冷却");

        string partyPath = Path.Combine(root, "partial-aura.db");
        repository = new(partyPath);
        using var partyOwner = new AccountOwners().Acquire("party-test", () => { });
        var partyRole = repository.Create(partyOwner, new NewCharacter("PartyCD", new(38, 1, 450, 234), null));
        using (var db = Open(partyPath))
        {
            RemoveSkillTreeExpansionSchema(db);
            Execute(db, """
                ALTER TABLE character_teleport_cooldowns DROP COLUMN party_aura_until;
                ALTER TABLE character_teleport_cooldowns DROP COLUMN privilege_until;
                INSERT INTO character_teleport_cooldowns VALUES(1,'party-test',1000,2000,3000);
                PRAGMA user_version=80;
                """);
        }
        foreach (string scenario in new[] { "wrong-type", "missing-check", "late-failure" })
        {
            string damaged = Path.Combine(root, "party-" + scenario + ".db");
            using (var source = Open(partyPath))
            using (var target = Open(damaged)) source.BackupDatabase(target);
            using (var db = Open(damaged))
                Execute(db, scenario switch
                {
                    "wrong-type" => "ALTER TABLE character_teleport_cooldowns ADD COLUMN party_aura_until TEXT NOT NULL DEFAULT '0'",
                    "missing-check" => "ALTER TABLE character_teleport_cooldowns ADD COLUMN party_aura_until INTEGER NOT NULL DEFAULT 0",
                    _ => "ALTER TABLE accounts DROP COLUMN disabled"
                });
            string before = Snapshot(damaged);
            RejectDatabase(() => _ = new CharacterRepository(damaged));
            Check(Snapshot(damaged) == before, "光环冷却迁移遇未知同名结构或末段失败时回滚DDL和版本");
        }
        repository = new(partyPath);
        Check(JsonSerializer.Serialize(repository.LoadCharacter(partyOwner, partyRole.Slot)) ==
            JsonSerializer.Serialize(partyRole with { TeleportCooldowns = new(1000, 2000, 3000) }),
            "增量升级只为旧行补零值缺失冷却，不修改原冷却、角色资产和revision");
        using (var db = Open(partyPath))
        {
            Execute(db, "UPDATE character_teleport_cooldowns SET party_aura_until=4000");
            Reject<SqliteException>(() => Execute(db, "UPDATE character_teleport_cooldowns SET party_aura_until=-1"), "组队截止时刻拒绝负值");
        }
        string partyStable = Snapshot(partyPath);
        repository = new(partyPath);
        Check(Snapshot(partyPath) == partyStable && repository.LoadCharacter(partyOwner, partyRole.Slot).TeleportCooldowns == new TeleportCooldowns(1000, 2000, 3000, 4000),
            "增量升级及重开恢复四种冷却，type0不覆盖type2/3");
        AuraPreviewUpgrade(root);
    }

    static void AuraPreviewUpgrade(string root)
    {
        foreach (int version in new[] { 78, 79 })
        {
            string path = Path.Combine(root, "preview-" + version + ".db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("preview-aura", () => { });
            var role = repository.Create(owner, new NewCharacter("PreviewAura", new(38, 1, 450, 234), null)
                { Inventory = InventorySnapshot.Empty with { Gold = 23456 } });
            using (var db = Open(path))
            {
                if (version == 78) RemoveEmptyPromotionFixtureSchema(db);
                RewindZeroExperience(db);
                Execute(db, "DROP TABLE character_quest_daily_clear;ALTER TABLE character_expert_job DROP COLUMN learned_recipes");
                Execute(db, "ALTER TABLE character_teleport_cooldowns DROP COLUMN privilege_until");
                if (version == 78) Execute(db, "ALTER TABLE character_teleport_cooldowns DROP COLUMN party_aura_until");
                RewindRaidLifecycle(db);
                Execute(db, $"INSERT INTO character_teleport_cooldowns VALUES(1,'preview-aura',1000,2000,3000{(version == 79 ? ",4000" : "")});PRAGMA user_version={version}");
            }
            string damaged = Path.Combine(root, "preview-unknown-" + version + ".db");
            using (var source = Open(path))
            using (var target = Open(damaged)) source.BackupDatabase(target);
            using (var db = Open(damaged)) Execute(db, "ALTER TABLE character_teleport_cooldowns ADD COLUMN unexpected INTEGER");
            string before = Snapshot(damaged);
            RejectDatabase(() => _ = new CharacterRepository(damaged));
            Check(Snapshot(damaged) == before, "未知测试结构不触发编号兼容，升级失败保持原结构、数据与版本");
            repository = new(path);
            Check(JsonSerializer.Serialize(repository.LoadCharacter(owner, role.Slot)) == JsonSerializer.Serialize(role with
                { TeleportCooldowns = new(1000, 2000, 3000, version == 79 ? 4000 : 0) }) &&
                QueryOn(path, "SELECT count(*) FROM character_quest_daily_clear") is 0L &&
                QueryOn(path, "SELECT count(*) FROM pragma_table_info('character_expert_job') WHERE name='learned_recipes'") is 1L,
                "已知光环v78/v79补齐正式迁移，不清除冷却、补发奖励或改变资产与revision");
            string stable = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == stable, "已知光环测试库升级后重开不再重放或延长冷却");
        }
    }
}
