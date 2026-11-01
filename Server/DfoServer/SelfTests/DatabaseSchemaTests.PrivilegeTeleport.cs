using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Town;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void PrivilegeTeleportCooldownUpgrade(string root)
    {
        root = Path.Combine(root, "privilege-teleport"); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        var repository = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("privilege-teleport", () => { });
        var role = repository.Create(owner, new NewCharacter("PrivilegeTP", new(38, 1, 450, 234), null));
        using (var db = Open(path))
            Execute(db, """
                ALTER TABLE character_teleport_cooldowns DROP COLUMN privilege_until;
                INSERT INTO character_teleport_cooldowns(character_id,account,potion_until,aura_until,upgrade_aura_until,party_aura_until)
                  VALUES(1,'privilege-teleport',1000,2000,3000,4000);
                CREATE INDEX privilege_teleport_history ON character_teleport_cooldowns(account);
                PRAGMA user_version=178;
                """);
        foreach (string scenario in new[] { "wrong-type", "missing-check", "late-failure" })
        {
            string damaged = Path.Combine(root, scenario + ".db");
            using (var source = Open(path))
            using (var target = Open(damaged)) source.BackupDatabase(target);
            using (var db = Open(damaged))
                Execute(db, scenario switch
                {
                    "wrong-type" => "ALTER TABLE character_teleport_cooldowns ADD COLUMN privilege_until TEXT NOT NULL DEFAULT '0'",
                    "missing-check" => "ALTER TABLE character_teleport_cooldowns ADD COLUMN privilege_until INTEGER NOT NULL DEFAULT 0",
                    _ => "ALTER TABLE accounts DROP COLUMN disabled"
                });
            string before = Snapshot(damaged);
            RejectDatabase(() => _ = new CharacterRepository(damaged));
            Check(Snapshot(damaged) == before, "179畸形同名列或末段验证失败回滚DDL、四类旧冷却、角色和版本");
        }
        repository = new(path);
        Check(JsonSerializer.Serialize(repository.LoadCharacter(owner, role.Slot)) ==
            JsonSerializer.Serialize(role with { TeleportCooldowns = new(1000, 2000, 3000, 4000) }) &&
            QueryOn(path, "SELECT privilege_until FROM character_teleport_cooldowns") is 0L &&
            Convert.ToInt32(QueryOn(path, "PRAGMA user_version")) == SqliteMigrations.CurrentVersion,
            "178升级179只补零礼遇冷却，不重置四类旧冷却、角色资产或revision");
        using (var db = Open(path))
        {
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='privilege_teleport_history'") is 1L,
                "179原表增量加列保留旧索引");
            Reject<SqliteException>(() => Execute(db, "UPDATE character_teleport_cooldowns SET privilege_until=-1"), "礼遇截止时刻禁止负值");
            Execute(db, "UPDATE character_teleport_cooldowns SET privilege_until=5000");
        }
        string stable = Snapshot(path);
        repository = new(path);
        Check(Snapshot(path) == stable && repository.LoadCharacter(owner, role.Slot).TeleportCooldowns == new TeleportCooldowns(1000, 2000, 3000, 4000, 5000),
            "重开恢复独立第五冷却，不重复延长或清零");
        repository.Delete(owner, repository.LoadDeletionTarget(owner, role.Slot));
        Check(QueryOn(path, "SELECT count(*) FROM character_teleport_cooldowns") is 0L, "删角清理礼遇冷却并保留既有外键生命周期");
    }
}
