using System.Collections.Immutable;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class ClientSettingsStoreTests
{
    static void BatteryAndHotkey208Upgrade()
    {
        foreach (bool localBattery in new[] { true, false })
        {
            using var f = new Fixture();
            ImmutableArray<byte> legacy = [.. "I01\t77\n"u8.ToArray()];
            ImmutableArray<byte> saved = [.. "I01\t55\n"u8.ToArray()];
            f.Execute($"INSERT INTO account_hotkey_settings VALUES('settings-a',0,X'{Convert.ToHexString(legacy.AsSpan())}')");
            f.Service.SaveHotkeys(f.Owner, f.First.Id, 0, saved);
            if (localBattery)
                f.Execute($"DROP TABLE character_hotkey_settings; INSERT INTO character_fatigue_battery VALUES({f.First.Id},17,31)");
            else
                f.Execute("DROP TABLE character_fatigue_battery");
            f.Execute("PRAGMA user_version=208");
            string assets = f.Assets();
            var migrated = new ClientSettingsRepository(f.Path);
            Check(f.Query("PRAGMA user_version") is 209L && f.Assets() == assets,
                "两种208来源均原子升至209，完整角色资产与既有电池值保持");
            if (localBattery)
                Check(Same(migrated.Load(f.Owner, f.First.Id).Hotkeys, legacy) &&
                    Same(migrated.Load(f.Owner, f.Second.Id).Hotkeys, legacy) &&
                    f.Query($"SELECT charging*1000+buff FROM character_fatigue_battery WHERE character_id={f.First.Id}") is 17031L,
                    "本地电池208补齐缺失键位并保留17充能和31祝福");
            else
                Check(Same(migrated.Load(f.Owner, f.First.Id).Hotkeys, saved) &&
                    migrated.Load(f.Owner, f.Second.Id).Hotkeys.IsDefault &&
                    f.Query("SELECT count(*) FROM character_fatigue_battery") is 0L,
                    "上游键位208追加空电池，不覆盖已存键位或给后建角色回填旧账号配置");
            f.Integrity();
        }
        using var broken = new Fixture();
        broken.Execute("DROP TABLE character_hotkey_settings; DROP TABLE character_fatigue_battery; " +
            "CREATE TABLE character_fatigue_battery(collision INTEGER); PRAGMA user_version=208");
        Reject<InvalidDataException>(() => new ClientSettingsRepository(broken.Path), "209拒绝未知电池结构");
        Check(broken.Query("PRAGMA user_version") is 208L &&
            broken.Query("SELECT count(*) FROM sqlite_schema WHERE name='character_hotkey_settings'") is 0L &&
            broken.Query("SELECT count(*) FROM pragma_table_info('character_fatigue_battery') WHERE name='collision'") is 1L,
            "209后段失败回滚前段补表和版本，保留未知原表");
    }

    static void CharacterHotkeyDeletionRollback()
    {
        using var f = new Fixture();
        var repository = new CharacterRepository(f.Path);
        ImmutableArray<byte> keys = [.. "I01\t77\nI02\t55\n"u8.ToArray()];
        f.Service.SaveHotkeys(f.Owner, f.First.Id, 0, keys);
        f.Service.SaveHotkeys(f.Owner, f.Second.Id, 0, keys);
        string assets = f.Assets();
        var target = repository.LoadDeletionTarget(f.Owner, f.First.Slot);
        f.Execute("CREATE TRIGGER fail_hotkey_delete AFTER DELETE ON characters BEGIN SELECT RAISE(ABORT,'late character deletion'); END");
        Reject<SqliteException>(() => repository.Delete(f.Owner, target), "角色删除末段失败回滚前面清理的键位");
        Check(Same(f.Service.Load(f.Owner, f.First.Id).Hotkeys, keys) && Same(f.Service.Load(f.Owner, f.Second.Id).Hotkeys, keys) &&
            f.Query("SELECT count(*) FROM character_hotkey_settings") is 2L && f.Assets() == assets,
            "删角失败保留角色身份、键位和其他角色资产");
        f.Execute("DROP TRIGGER fail_hotkey_delete");
        f.Integrity();
    }

    static void CharacterHotkeyMigration()
    {
        using var f = new Fixture();
        var creator = new CharacterRepository(f.Path).Create(f.Owner, new("MigrateKeys", f.First.Position, f.First.Return) { Job = 10 });
        ImmutableArray<byte> common = [.. "I01\t77\nI02\t55\n"u8.ToArray()];
        ImmutableArray<byte> creatorKeys = [.. "I01\t134\nC00\t77\n"u8.ToArray()];
        f.Execute($"INSERT INTO account_hotkey_settings VALUES('settings-a',0,X'{Convert.ToHexString(common.AsSpan())}'),('settings-a',1,X'{Convert.ToHexString(creatorKeys.AsSpan())}')");
        string original = Legacy(), assets = f.Assets();
        string freshSchema = (string)f.Query("SELECT sql FROM sqlite_schema WHERE name='character_hotkey_settings'")!;
        f.Execute("DROP TABLE character_hotkey_settings; PRAGMA user_version=207");
        var migrated = new ClientSettingsRepository(f.Path);
        Check(f.Query("PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
            f.Query("SELECT sql FROM sqlite_schema WHERE name='character_hotkey_settings'") is string upgradedSchema &&
            freshSchema.ReplaceLineEndings("\n") == upgradedSchema.ReplaceLineEndings("\n") &&
            f.Assets() == assets && Legacy() == original,
            "v207升级角色键位与新建库结构同构，旧账号原文及其他资产保持");
        Check(Same(migrated.Load(f.Owner, f.First.Id).Hotkeys, common) && Same(migrated.Load(f.Owner, f.Second.Id).Hotkeys, common) &&
            Same(migrated.Load(f.Owner, creator.Id).Hotkeys, creatorKeys) && migrated.Load(f.OtherOwner, f.Other.Id).Hotkeys.IsDefault &&
            f.Query("SELECT count(*) FROM character_hotkey_settings") is 3L,
            "迁移仅按已有角色的真实职业复制匹配profile，不伪造其他账号或另一profile历史");
        Check(migrated.SaveHotkeys(f.Owner, f.First.Id, 0, creatorKeys) && Same(migrated.Load(f.Owner, f.Second.Id).Hotkeys, common) && Legacy() == original,
            "迁移后普通角色独立修改，其他角色及历史账号配置不被覆盖");
        f.Execute("PRAGMA user_version=207");
        migrated = new ClientSettingsRepository(f.Path);
        Check(Same(migrated.Load(f.Owner, f.First.Id).Hotkeys, creatorKeys) && Same(migrated.Load(f.Owner, f.Second.Id).Hotkeys, common),
            "精确已存在角色表重入升级保留独立修改，不用旧账号配置覆盖已有行");
        var created = new CharacterRepository(f.Path).Create(f.Owner, new("AfterKeys", f.First.Position, f.First.Return));
        Check(new ClientSettingsRepository(f.Path).Load(f.Owner, created.Id).Hotkeys.IsDefault &&
            f.Query($"SELECT count(*) FROM character_hotkey_settings WHERE character_id={created.Id}") is 0L,
            "迁移后新角色重开仍缺省，不从保全的旧账号表回退");

        f.Execute("DELETE FROM character_hotkey_settings; PRAGMA user_version=207");
        // 至少一行已插入后再故障，验证回填和版本更新共同回滚。
        f.Execute("CREATE TRIGGER fail_hotkey_migration BEFORE INSERT ON character_hotkey_settings WHEN (SELECT count(*) FROM character_hotkey_settings)>0 BEGIN SELECT RAISE(ABORT,'late character hotkey migration'); END");
        assets = f.Assets();
        Reject<SqliteException>(() => new ClientSettingsRepository(f.Path), "角色键位回填末段失败拒绝升级");
        Check(f.Query("PRAGMA user_version") is 207L && f.Query("SELECT count(*) FROM character_hotkey_settings") is 0L &&
            Legacy() == original && f.Assets() == assets, "回填失败不遗留前段行、不推进版本、不改历史和资产");
        f.Execute("DROP TRIGGER fail_hotkey_migration");
        _ = new ClientSettingsRepository(f.Path);
        Check(f.Query("SELECT count(*) FROM character_hotkey_settings") is 4L && Legacy() == original,
            "去除明确故障后可重试升级，按本次升级时已有角色恢复原始字节");
        f.Integrity();

        f.Execute("DROP TABLE character_hotkey_settings; CREATE TABLE character_hotkey_settings(collision INTEGER); PRAGMA user_version=207");
        Reject<InvalidDataException>(() => new ClientSettingsRepository(f.Path), "v208拒绝未知同名角色键位表");
        Check(f.Query("PRAGMA user_version") is 207L && f.Query("SELECT count(*) FROM pragma_table_info('character_hotkey_settings') WHERE name='collision'") is 1L &&
            Legacy() == original && f.Assets() == assets, "结构碰撞不破坏未知原表、旧账号配置或其他资产");
        f.Execute($"PRAGMA user_version={SqliteMigrations.CurrentVersion}");
        Reject<SqliteException>(() => new ClientSettingsRepository(f.Path), "当前版本同样拒绝损坏角色键位结构，不只检查迁移版本号");

        string Legacy() => (string)f.Query("SELECT group_concat(account||':'||profile||':'||hex(hotkeys),'|') FROM (SELECT * FROM account_hotkey_settings ORDER BY account,profile)")!;
    }
}
