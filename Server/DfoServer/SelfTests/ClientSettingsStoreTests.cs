using static DfoServer.Testing.TestCheck;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static class ClientSettingsStoreTests
{
    public static void Run()
    {
        int before = TestCheck.Count;
        SaveAndIsolation();
        CharacterGameSnapshots();
        RawAccountAndCanonicalCharacter();
        HotkeyStorage();
        ReadOnlyAndRollback();
        Migration();
        Console.WriteLine($"PASS {TestCheck.Count - before} 客户端设置规则、归属、持久化与迁移断言");
    }

    static void HotkeyStorage()
    {
        using var f = new Fixture();
        var keys = ImmutableArray.Create("I01\t77\nI02\t55\nI03\t73\nI04\t58\nK02\t31\n"u8.ToArray());
        var creatorKeys = ImmutableArray.Create("I01\t134\nI02\t134\nI03\t134\nI04\t134\nC00\t77\n"u8.ToArray());
        var creator = new CharacterRepository(f.Path).Create(f.Owner, new("KeyCreator", f.First.Position, f.First.Return) { Job = 10 });
        f.Execute($"INSERT INTO character_title_progress VALUES({f.First.Id},'settings-a',6501,1,2,3,0); INSERT INTO character_title_sections VALUES({f.First.Id},'settings-a',0,1)");
        f.Execute($"INSERT INTO character_tutorial_state VALUES({f.First.Id},'settings-a',7115,0,0)");
        string assets = f.Assets();
        Check(f.Service.Load(f.Owner, creator.Id).Hotkeys.IsDefault && f.Query("SELECT count(*) FROM account_hotkey_settings") is 0L,
            "缺省键位不建行、不生成未知默认表");
        Check(f.Service.SaveHotkeys(f.Owner, 0, keys) && !f.Service.SaveHotkeys(f.Owner, 0, keys), "键位首次持久化及相同字节零变化");
        Check(Same(f.Service.Load(f.Owner, f.First.Id).Hotkeys, keys) && Same(f.Service.Load(f.Owner, f.Second.Id).Hotkeys, keys) &&
            f.Service.Load(f.OtherOwner, f.Other.Id).Hotkeys.IsDefault && f.Service.Load(f.Owner, creator.Id).Hotkeys.IsDefault,
            "同账号普通角色共享键位，其他账号与缔造者不串配置");
        Check(f.Service.SaveHotkeys(f.Owner, 1, creatorKeys) && Same(f.Service.Load(f.Owner, creator.Id).Hotkeys, creatorKeys) &&
            Same(f.Service.Load(f.Owner, f.First.Id).Hotkeys, keys), "缔造者配置独立保留包括未分配键134");
        using (var db = Open(f.Path))
        using (var writer = db.BeginTransaction(deferred: false))
            Check(!f.Service.SaveHotkeys(f.Owner, 0, keys), "外部writer占用时重复键位保存仍不申请写事务");
        f.Execute("CREATE TRIGGER hotkey_fail BEFORE UPDATE ON account_hotkey_settings BEGIN SELECT RAISE(ABORT,'hotkey rollback'); END");
        Reject<SqliteException>(() => f.Service.SaveHotkeys(f.Owner, 0, creatorKeys), "键位SQL失败传播");
        Check(Same(f.Service.Load(f.Owner, f.First.Id).Hotkeys, keys) && f.Assets() == assets, "失败保留键位及账号凭据、角色revision与全部资产");
        f.Execute("DROP TRIGGER hotkey_fail");
        var reopened = new ClientSettingsService(new ClientSettingsRepository(f.Path));
        Check(Same(reopened.Load(f.Owner, creator.Id).Hotkeys, creatorKeys), "重开仓储恢复完整键位字节");
        Reject<InvalidDataException>(() => f.Service.SaveHotkeys(f.Owner, 2, keys), "未知配置不得保存");
        using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Service.SaveHotkeys(f.Owner, 0, keys), "旧generation不能覆盖新账号键位");
        Check(Same(reopened.Load(replacement, f.First.Id).Hotkeys, keys), "拒绝旧owner后存档仍可恢复");

        DatabaseSchemaTests.RemoveAdventureSchema(f.Path);
        f.Execute("ALTER TABLE characters DROP COLUMN emotion; DROP TABLE account_hotkey_settings; PRAGMA user_version=26");
        string existing = f.ExistingSettings();
        _ = new ClientSettingsRepository(f.Path);
        Check(f.Query("PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion && f.Query("SELECT count(*) FROM account_hotkey_settings") is 0L &&
            f.ExistingSettings() == existing && f.Assets() == assets, "v26增量只建空键位表，保留称号进度、训练状态、原设置与资产");
        string legacyAssets = f.Assets(includeAcceptance: false);
        f.Execute($"UPDATE characters SET stamina_until=1 WHERE id={f.First.Id}");
        Check(f.Assets() != assets && f.Assets(includeAcceptance: false) != legacyAssets,
            "普通与旧库快照均检查新增魔王字段，不能因迁移列序变化而忽略字段值");
        f.Execute($"UPDATE characters SET stamina_until=0 WHERE id={f.First.Id}");
        DatabaseSchemaTests.RemoveAdventureSchema(f.Path);
        f.Execute("ALTER TABLE characters DROP COLUMN emotion; DROP TABLE account_hotkey_settings; PRAGMA user_version=26; CREATE TABLE account_hotkey_settings(collision INTEGER)");
        string rollbackAssets = f.Assets();
        Reject<SqliteException>(() => new ClientSettingsRepository(f.Path), "键位迁移碰撞不吞错误");
        Check(f.Query("PRAGMA user_version") is 26L && f.Assets() == rollbackAssets, "键位DDL失败回滚版本并保留资产");
        f.Execute("DROP TABLE account_hotkey_settings");
        _ = new ClientSettingsRepository(f.Path);
        f.Integrity();
    }

    static void SaveAndIsolation()
    {
        using var f = new Fixture();
        string assets = f.Assets();
        foreach (var role in new[] { f.First, f.Second })
            Check(f.Service.Load(f.Owner, role.Id).IsDefault, "同账号各角色缺省设置不生成默认字节");
        Check(f.Service.Load(f.OtherOwner, f.Other.Id).IsDefault && f.SettingRows() == 0, "缺省查询不建设置行");
        foreach (int length in new[] { 0, 203, 205 })
            Reject<InvalidDataException>(() => f.Service.SaveGameOptions(f.Owner, Pattern(length, 0)), "拒绝不匹配主设置长度");
        foreach (int length in new[] { 0, 511, 513 })
            Reject<InvalidDataException>(() => f.Service.SaveCharacterOptions(f.Owner, f.First.Id, Pattern(length, 0)), "拒绝不匹配角色设置长度");
        Reject<InvalidDataException>(() => f.Service.SaveGameOptions(f.Owner, default), "拒绝未初始化主设置");
        Reject<InvalidDataException>(() => f.Service.SaveCharacterOptions(f.Owner, f.First.Id, default), "拒绝未初始化角色设置");
        Reject<SqliteException>(() => f.Execute("INSERT INTO account_client_settings(account,game_options) VALUES('settings-a',zeroblob(203))"),
            "数据库拒绝不匹配长度的主设置");
        Reject<SqliteException>(() => f.Execute($"INSERT INTO character_client_settings(character_id,account,character_options) VALUES({f.First.Id},'settings-b',zeroblob(512))"),
            "数据库复合外键拒绝伪造角色账号归属");
        Reject<SqliteException>(() => f.Execute($"INSERT INTO character_game_settings(character_id,account,game_options) VALUES({f.First.Id},'settings-a',zeroblob(203))"),
            "数据库拒绝不匹配长度的角色主设置快照");
        Reject<SqliteException>(() => f.Execute($"INSERT INTO character_game_settings(character_id,account,game_options) VALUES({f.First.Id},'settings-b',zeroblob(204))"),
            "角色主设置复合外键拒绝其他账号");
        Check(f.SettingRows() == 0 && f.Assets() == assets, "非法设置不写库或改变角色资产");

        var game = Pattern(ClientSettingsService.GameOptionLength, 17);
        var first = Pattern(ClientSettingsService.CharacterOptionLength, 33);
        var second = Pattern(ClientSettingsService.CharacterOptionLength, 49);
        Check(f.Service.SaveGameOptions(f.Owner, game), "首次保存账号主设置报告实际变化");
        var partial = f.Service.Load(f.Owner, f.Second.Id);
        Check(!partial.IsDefault && Same(partial.GameOptions, game) && partial.CharacterOptions.IsDefault,
            "共享主设置与角色缺省分别保留");
        Check(f.Service.SaveCharacterOptions(f.Owner, f.First.Id, first) && f.Service.SaveCharacterOptions(f.Owner, f.Second.Id, second),
            "同账号两角色设置独立保存");
        Assert(f.Service.Load(f.Owner, f.First.Id), game, first, "第一角色完整字节与 FFFF 哨兵保留");
        Assert(f.Service.Load(f.Owner, f.Second.Id), game, second, "第二角色共享主设置、独立角色设置");
        Check(!f.Service.SaveGameOptions(f.Owner, game) && !f.Service.SaveCharacterOptions(f.Owner, f.First.Id, first),
            "完全相同设置报告零变化");
        Reject<InvalidDataException>(() => f.Service.Load(f.OtherOwner, f.First.Id), "外账号不能读取角色或其主设置");
        Reject<InvalidDataException>(() => f.Service.SaveCharacterOptions(f.OtherOwner, f.First.Id, second), "外账号不能保存角色设置");
        Reject<InvalidDataException>(() => f.Service.Load(f.Owner, ushort.MaxValue), "不存在角色不能读取设置");
        Reject<InvalidDataException>(() => f.Service.SaveCharacterOptions(f.Owner, ushort.MaxValue, first), "不存在角色不能创建设置");
        using (var missing = f.Owners.Acquire("missing-settings-account", () => { }))
            Reject<InvalidDataException>(() => f.Service.SaveGameOptions(missing, game), "主设置不能替缺少的账号建行");

        var otherGame = Pattern(ClientSettingsService.GameOptionLength, 97);
        var otherCharacter = Pattern(ClientSettingsService.CharacterOptionLength, 113);
        Check(f.Service.SaveCharacterOptions(f.OtherOwner, f.Other.Id, otherCharacter), "第二账号可先保存角色设置");
        var characterOnly = f.Service.Load(f.OtherOwner, f.Other.Id);
        Check(!characterOnly.IsDefault && characterOnly.GameOptions.IsDefault && Same(characterOnly.CharacterOptions, otherCharacter),
            "仅保存角色设置时主设置仍为独立缺省");
        Check(f.Service.SaveGameOptions(f.OtherOwner, otherGame), "第二账号可再保存自己的主设置");
        var changed = Pattern(ClientSettingsService.GameOptionLength, 129);
        Check(f.Service.SaveGameOptions(f.Owner, changed), "不同主设置覆盖原账号设置");
        Assert(f.Service.Load(f.Owner, f.First.Id), changed, first, "主设置更新不覆盖第一角色设置");
        Assert(f.Service.Load(f.Owner, f.Second.Id), changed, second, "主设置更新立即由本账号另一角色读取");
        Assert(f.Service.Load(f.OtherOwner, f.Other.Id), otherGame, otherCharacter, "账号更新不泄漏到另一账号");
        var reopened = new ClientSettingsService(new ClientSettingsRepository(f.Path));
        Assert(reopened.Load(f.Owner, f.First.Id), changed, first, "重开仓储恢复实际提交的设置");
        Check(f.Assets() == assets && f.SettingRows() == 5, "设置保存不修改账号凭据、角色 revision 或完整资产");

        using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Service.Load(f.Owner, f.First.Id), "过期租约不能加载设置");
        Reject<InvalidOperationException>(() => f.Service.SaveGameOptions(f.Owner, game), "过期租约不能覆盖主设置");
        Reject<InvalidOperationException>(() => f.Service.SaveCharacterOptions(f.Owner, f.First.Id, second), "过期租约不能覆盖角色设置");
        Assert(reopened.Load(replacement, f.First.Id), changed, first, "接管后旧租约拒绝保留新 owner 可读值");
        f.Integrity();
    }

    static void CharacterGameSnapshots()
    {
        using var f = new Fixture();
        string assets = f.Assets();
        var first = Pattern(ClientSettingsService.GameOptionLength, 13);
        var second = Pattern(ClientSettingsService.GameOptionLength, 23);
        var accountOnly = Pattern(ClientSettingsService.GameOptionLength, 43);
        Check(f.Service.SaveGameOptions(f.Owner, first, f.First.Id), "首次主设置与当前角色快照一起保存");
        var original = f.Service.Load(f.Owner, f.First.Id);
        Check(Same(original.GameOptions, first) && Same(original.CharacterGameOptions, first) && original.CharacterOptions.IsDefault,
            "没有512角色选项也可独立保存204角色主设置快照");
        Check(f.Service.Load(f.Owner, f.Second.Id).CharacterGameOptions.IsDefault, "保存主设置不替同账号另一角色建立快照");
        Check(f.Service.SaveGameOptions(f.Owner, second, f.Second.Id), "第二角色保存自己的主设置快照");
        var loadedFirst = f.Service.Load(f.Owner, f.First.Id);
        var loadedSecond = f.Service.Load(f.Owner, f.Second.Id);
        Check(Same(loadedFirst.GameOptions, second) && Same(loadedFirst.CharacterGameOptions, first) &&
            Same(loadedSecond.GameOptions, second) && Same(loadedSecond.CharacterGameOptions, second),
            "共享账号原主设置与两角色原快照分别保留");
        Check(Same(original.GameOptions, first) && Same(original.CharacterGameOptions, first), "后续保存不修改已读取的不可变快照");
        Check(f.Service.SaveGameOptions(f.Owner, second, f.First.Id), "账号字节相同而角色快照不同仍须提交");
        Check(!f.Service.SaveGameOptions(f.Owner, second, f.First.Id), "账号和当前角色快照均相同才报告零变化");
        Check(f.Service.SaveGameOptions(f.Owner, first, f.First.Id), "角色快照可独立重新覆盖");
        Check(f.Service.SaveGameOptions(f.Owner, accountOnly), "未选角色保存只修改账号主设置");
        loadedFirst = f.Service.Load(f.Owner, f.First.Id);
        loadedSecond = f.Service.Load(f.Owner, f.Second.Id);
        Check(Same(loadedFirst.GameOptions, accountOnly) && Same(loadedFirst.CharacterGameOptions, first) &&
            Same(loadedSecond.GameOptions, accountOnly) && Same(loadedSecond.CharacterGameOptions, second),
            "账号更新保留两个角色原主设置快照");
        Reject<InvalidDataException>(() => f.Service.SaveGameOptions(f.OtherOwner, first, f.First.Id), "角色主设置快照拒绝外账号伪造ID");
        Reject<InvalidDataException>(() => f.Service.SaveGameOptions(f.Owner, first, ushort.MaxValue), "主设置保存拒绝不存在的角色ID");
        Check(f.Service.Load(f.OtherOwner, f.Other.Id).IsDefault && f.SettingRows() == 3 && f.Assets() == assets,
            "越权和缺省不会留下账号设置、512默认或改动资产");

        f.Service.SaveGameOptions(f.Owner, first, f.First.Id);
        using (var external = Open(f.Path))
        using (var held = external.BeginTransaction(deferred: false))
        {
            var watch = Stopwatch.StartNew();
            Check(!f.Service.SaveGameOptions(f.Owner, first, f.First.Id), "外部writer期间双份同字节不申请writer");
            Check(Same(f.Service.Load(f.Owner, f.First.Id).CharacterGameOptions, first) && watch.Elapsed < TimeSpan.FromSeconds(1),
                "外部writer期间三段加载与双份零变化直接完成");
            held.Rollback();
        }
        foreach (string table in new[] { "account_client_settings", "character_game_settings" })
        {
            f.Execute($"CREATE TRIGGER fail_game_pair AFTER UPDATE ON {table} BEGIN SELECT RAISE(ABORT,'paired settings failure'); END");
            Reject<SqliteException>(() => f.Service.SaveGameOptions(f.Owner, accountOnly, f.First.Id), "双份主设置任何更新步骤故障都拒绝提交");
            var rolledBack = f.Service.Load(f.Owner, f.First.Id);
            Check(Same(rolledBack.GameOptions, first) && Same(rolledBack.CharacterGameOptions, first) &&
                Same(f.Service.Load(f.Owner, f.Second.Id).CharacterGameOptions, second), "故障原子回滚账号和当前角色快照，其他角色保持原值");
            f.Execute("DROP TRIGGER fail_game_pair");
        }
        f.Execute("CREATE TRIGGER fail_game_pair AFTER INSERT ON character_game_settings BEGIN SELECT RAISE(ABORT,'new paired settings failure'); END");
        Reject<SqliteException>(() => f.Service.SaveGameOptions(f.OtherOwner, first, f.Other.Id), "首次角色快照插入故障回滚新账号主设置");
        Check(f.Service.Load(f.OtherOwner, f.Other.Id).IsDefault && f.SettingRows() == 3 && f.Assets() == assets,
            "双份首次保存失败不遗留任何账号或角色设置行");
        f.Execute("DROP TRIGGER fail_game_pair");
        var reopened = new ClientSettingsRepository(f.Path);
        Check(Same(reopened.Load(f.Owner, f.First.Id).CharacterGameOptions, first) && f.Assets() == assets,
            "重开仓储恢复角色主设置快照，不改变角色revision和资产");
        using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Service.SaveGameOptions(f.Owner, second, f.First.Id), "过期租约不能原子覆盖账号与角色主设置");
        Check(Same(reopened.Load(replacement, f.First.Id).GameOptions, first) &&
            Same(reopened.Load(replacement, f.First.Id).CharacterGameOptions, first), "旧租约拒绝后新租约仍读取原提交的两份主设置");
        f.Integrity();
    }

    static void RawAccountAndCanonicalCharacter()
    {
        using var f = new Fixture();
        string assets = f.Assets();
        byte[] account = Pattern(ClientSettingsService.GameOptionLength, 67).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(account.AsSpan(68 * 2), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(account.AsSpan(13 * 2), 0);
        byte[] role = Enumerable.Repeat((byte)255, ClientSettingsService.CharacterOptionLength).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(role.AsSpan(46 * 2), 37);
        BinaryPrimitives.WriteUInt16LittleEndian(role.AsSpan(49 * 2), 1);
        var rawAccount = account.ToImmutableArray();
        var rawRole = role.ToImmutableArray();
        byte[] expected = account.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(68 * 2), 37);
        BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(13 * 2), 1);
        var canonical = expected.ToImmutableArray();
        f.Service.SaveCharacterOptions(f.Owner, f.First.Id, rawRole);
        Check(f.Service.SaveGameOptions(f.Owner, rawAccount, f.First.Id), "角色512真实值纠正本角色主设置快照");
        var saved = f.Service.Load(f.Owner, f.First.Id);
        Check(Same(saved.GameOptions, rawAccount) && Same(saved.CharacterGameOptions, canonical) && Same(saved.CharacterOptions, rawRole),
            "账号204保留输入原字节，角色204只按有效角色值纠正68与13，512仍原样");
        Check(!f.Service.SaveGameOptions(f.Owner, rawAccount, f.First.Id), "原账号和独立规范角色值均相同时免重复写入");
        BinaryPrimitives.WriteUInt16LittleEndian(role.AsSpan(46 * 2), 55);
        BinaryPrimitives.WriteUInt16LittleEndian(role.AsSpan(49 * 2), 0);
        f.Service.SaveCharacterOptions(f.Owner, f.First.Id, role.ToImmutableArray());
        Check(f.Service.SaveGameOptions(f.Owner, rawAccount, f.First.Id), "197账号同值但447已改变有效角色值时仍更新角色规范快照");
        saved = f.Service.Load(f.Owner, f.First.Id);
        Check(Same(saved.GameOptions, rawAccount) && BinaryPrimitives.ReadUInt16LittleEndian(saved.CharacterGameOptions.AsSpan().Slice(68 * 2, 2)) == 55 &&
            BinaryPrimitives.ReadUInt16LittleEndian(saved.CharacterGameOptions.AsSpan().Slice(13 * 2, 2)) == 0 && f.Assets() == assets,
            "纠正角色值不改变账号原主设置或角色资产");

        var repository = new ClientSettingsRepository(f.Path);
        var directAccount = Pattern(ClientSettingsService.GameOptionLength, 83);
        var directCharacter = Pattern(ClientSettingsService.GameOptionLength, 97);
        Check(repository.SaveGameOptions(f.Owner, directAccount, f.First.Id, directCharacter), "存储合同支持账号与角色不同的完整204数据");
        var direct = repository.Load(f.Owner, f.First.Id);
        Check(Same(direct.GameOptions, directAccount) && Same(direct.CharacterGameOptions, directCharacter),
            "存储合同原样保存分别传入的账号及角色204字节");
        using (var external = Open(f.Path))
        using (var held = external.BeginTransaction(deferred: false))
        {
            var watch = Stopwatch.StartNew();
            Check(!repository.SaveGameOptions(f.Owner, directAccount, f.First.Id, directCharacter) && watch.Elapsed < TimeSpan.FromSeconds(1),
                "两份不同字节各自相同时外部writer下仍零变化");
            held.Rollback();
        }
        foreach (string table in new[] { "account_client_settings", "character_game_settings" })
        {
            f.Execute($"CREATE TRIGGER fail_separate_game AFTER UPDATE ON {table} BEGIN SELECT RAISE(ABORT,'separate settings failure'); END");
            Reject<SqliteException>(() => repository.SaveGameOptions(f.Owner, Pattern(ClientSettingsService.GameOptionLength, 109), f.First.Id,
                Pattern(ClientSettingsService.GameOptionLength, 127)), "独立第四参数的双份保存遇SQL故障整体拒绝");
            saved = repository.Load(f.Owner, f.First.Id);
            Check(Same(saved.GameOptions, directAccount) && Same(saved.CharacterGameOptions, directCharacter) && f.Assets() == assets,
                "独立角色数据保存失败回滚原账号与原角色值及全部资产");
            f.Execute("DROP TRIGGER fail_separate_game");
        }
        f.Integrity();
    }

    static void ReadOnlyAndRollback()
    {
        using var f = new Fixture();
        var game = Pattern(ClientSettingsService.GameOptionLength, 7);
        var character = Pattern(ClientSettingsService.CharacterOptionLength, 19);
        f.Service.SaveGameOptions(f.Owner, game);
        f.Service.SaveCharacterOptions(f.Owner, f.First.Id, character);
        string assets = f.Assets();
        using (var external = Open(f.Path))
        using (var held = external.BeginTransaction(deferred: false))
        {
            var watch = Stopwatch.StartNew();
            // 真实 WAL 文件库持有 writer；这些查询与零变化保存必须直接完成。
            Assert(f.Service.Load(f.Owner, f.First.Id), game, character, "外部 writer 期间只读一致快照");
            Check(f.Service.Load(f.OtherOwner, f.Other.Id).IsDefault, "外部 writer 期间缺省设置保持只读");
            Check(!f.Service.SaveGameOptions(f.Owner, game) && !f.Service.SaveCharacterOptions(f.Owner, f.First.Id, character),
                "外部 writer 期间相同字节不申请写锁");
            Check(watch.Elapsed < TimeSpan.FromSeconds(1), "只读与零变化不等待外部 writer 超时");
            held.Rollback();
        }
        foreach (string table in new[] { "account_client_settings", "character_client_settings" })
        {
            f.Execute($"CREATE TRIGGER fail_settings AFTER UPDATE ON {table} BEGIN SELECT RAISE(ABORT,'settings failure'); END");
            if (table == "account_client_settings")
                Reject<SqliteException>(() => f.Service.SaveGameOptions(f.Owner, Pattern(ClientSettingsService.GameOptionLength, 29)),
                    "主设置 SQL 更新后故障不能报告保存成功");
            else
                Reject<SqliteException>(() => f.Service.SaveCharacterOptions(f.Owner, f.First.Id, Pattern(ClientSettingsService.CharacterOptionLength, 41)),
                    "角色设置 SQL 更新后故障不能报告保存成功");
            Assert(f.Service.Load(f.Owner, f.First.Id), game, character, "故障回滚保留原主设置和角色设置");
            f.Execute("DROP TRIGGER fail_settings");
        }
        f.Execute("CREATE TRIGGER fail_settings AFTER INSERT ON character_client_settings BEGIN SELECT RAISE(ABORT,'new settings failure'); END");
        Reject<SqliteException>(() => f.Service.SaveCharacterOptions(f.Owner, f.Second.Id, character), "首次设置插入失败回滚新行");
        Check(f.Service.Load(f.Owner, f.Second.Id).CharacterOptions.IsDefault && f.SettingRows() == 2 && f.Assets() == assets,
            "失败保存不遗留默认行、不影响原值或角色资产");
        f.Execute("DROP TRIGGER fail_settings");
        f.Integrity();
    }

    static void Migration()
    {
        using var f = new Fixture();
        DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
        f.Execute("DROP TABLE IF EXISTS character_dungeon_fatigue_receipts; DROP TABLE IF EXISTS character_fatigue; DROP TABLE character_quest_completion_receipts; DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id; DROP TABLE character_tp_sources; DROP TABLE character_skill_commands; DROP TABLE character_skill_combos; DROP TABLE character_skill_point_sources; DROP TABLE account_expert_contracts; DROP TABLE character_dungeon_settlement_receipts; DROP TABLE character_dungeon_pickup_receipts; DROP TABLE character_dungeon_loot_receipts; DROP TABLE character_dungeon_state; DROP TABLE character_game_settings; DROP TABLE character_client_settings; DROP TABLE account_client_settings; PRAGMA user_version=5");
        string assets = f.Assets(includeAcceptance: false);
        var migrated = new ClientSettingsRepository(f.Path);
        Check(f.Query("PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion && f.SettingRows() == 0 && f.Assets(includeAcceptance: false) == assets,
            "v5 连续升级当前版本只创建空设置表，原账号角色资产逐行保持");
        Check(f.Query("SELECT count(*) FROM characters WHERE devil_usage<>'[]' OR stamina_until<>0") is 0L,
            "v5旧库升级的新服务账本为空、恢复时限为0，不补造历史使用记录");
        Check(migrated.Load(f.Owner, f.First.Id).IsDefault, "升级不继承旧版本默认设置");
        Check(f.Query("SELECT count(*) FROM character_active_main_quests WHERE length(acceptance_id)=32 AND acceptance_id<>'00000000000000000000000000000000'") is 3L &&
            f.Query("SELECT count(*) FROM character_quest_clear_receipts") is 0L,
            "v13迁移为原三角色任务建立稳定接取身份，不凭旧条件值生成通关事实");
        var game = Pattern(ClientSettingsService.GameOptionLength, 53);
        var character = Pattern(ClientSettingsService.CharacterOptionLength, 71);
        Check(migrated.SaveGameOptions(f.Owner, game) && migrated.SaveCharacterOptions(f.Owner, f.First.Id, character),
            "升级后新表可保存主设置和角色设置");
        Assert(new ClientSettingsRepository(f.Path).Load(f.Owner, f.First.Id), game, character, "升级后重开恢复完整设置");
        f.Integrity();

        DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
        f.Execute("DROP TABLE IF EXISTS character_dungeon_fatigue_receipts; DROP TABLE IF EXISTS character_fatigue; DROP TABLE character_quest_completion_receipts; DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id; DROP TABLE character_tp_sources; DROP TABLE character_skill_commands; DROP TABLE character_skill_combos; DROP TABLE character_skill_point_sources; DROP TABLE account_expert_contracts; DROP TABLE character_dungeon_settlement_receipts; DROP TABLE character_dungeon_pickup_receipts; DROP TABLE character_dungeon_loot_receipts; DROP TABLE character_dungeon_state; DROP TABLE character_game_settings; DROP TABLE character_client_settings; DROP TABLE account_client_settings; PRAGMA user_version=5; CREATE TABLE character_client_settings(collision INTEGER)");
        assets = f.Assets(includeAcceptance: false);
        Reject<SqliteException>(() => new ClientSettingsRepository(f.Path), "v6 第二条 DDL 冲突回滚整个升级");
        Check(f.Query("PRAGMA user_version") is 5L && f.Query("SELECT count(*) FROM sqlite_schema WHERE name='account_client_settings'") is 0L && f.Assets(includeAcceptance: false) == assets,
            "失败升级不留下第一张设置表、不推进版本或修改原表");
        Reject<SqliteException>(() => new ClientSettingsRepository(f.Path), "失败库重开再次明确拒绝而非假成功");
        f.Execute("DROP TABLE character_client_settings");
        var retried = new ClientSettingsRepository(f.Path);
        Check(f.Query("PRAGMA user_version") is long retriedVersion && retriedVersion == SqliteMigrations.CurrentVersion && retried.Load(f.Owner, f.First.Id).IsDefault && f.Assets(includeAcceptance: false) == assets,
            "移除冲突后可重新完整升级并重开");
        f.Integrity();
        MigrationFromSix(f);
    }

    static void MigrationFromSix(Fixture f)
    {
        var game = Pattern(ClientSettingsService.GameOptionLength, 59);
        var character = Pattern(ClientSettingsService.CharacterOptionLength, 79);
        f.Service.SaveGameOptions(f.Owner, game);
        f.Service.SaveCharacterOptions(f.Owner, f.First.Id, character);
        DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
        f.Execute("DROP TABLE IF EXISTS character_dungeon_fatigue_receipts; DROP TABLE IF EXISTS character_fatigue; DROP TABLE character_quest_completion_receipts; DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id; DROP TABLE character_tp_sources; DROP TABLE character_skill_commands; DROP TABLE character_skill_combos; DROP TABLE character_skill_point_sources; DROP TABLE account_expert_contracts; DROP TABLE character_dungeon_settlement_receipts; DROP TABLE character_dungeon_pickup_receipts; DROP TABLE character_dungeon_loot_receipts; DROP TABLE character_dungeon_state; DROP TABLE character_game_settings; PRAGMA user_version=6");
        string assets = f.Assets(includeAcceptance: false), existing = f.ExistingSettings();
        var upgraded = new ClientSettingsRepository(f.Path);
        Check(Convert.ToInt64(f.Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion && f.ExistingSettings() == existing && f.Assets(includeAcceptance: false) == assets &&
            upgraded.Load(f.Owner, f.First.Id).CharacterGameOptions.IsDefault,
            "v6升级v7保持原两设置表逐字节和全部资产，新角色主设置表为空");
        Check(f.Query("SELECT count(*) FROM characters WHERE devil_usage<>'[]' OR stamina_until<>0") is 0L,
            "v6旧库升级保留新服务字段的空默认值，不激活冷却或使用次数");
        Assert(upgraded.Load(f.Owner, f.First.Id), game, character, "v7不把账号设置伪装成角色历史快照");
        Check(upgraded.SaveGameOptions(f.Owner, game, f.First.Id), "升级后账号同值但缺少角色快照仍提交新行");
        Check(Same(new ClientSettingsRepository(f.Path).Load(f.Owner, f.First.Id).CharacterGameOptions, game), "v7角色快照持久化后重开保持");
        f.Integrity();

        DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
        f.Execute("DROP TABLE IF EXISTS character_dungeon_fatigue_receipts; DROP TABLE IF EXISTS character_fatigue; DROP TABLE character_quest_completion_receipts; DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id; DROP TABLE character_tp_sources; DROP TABLE character_skill_commands; DROP TABLE character_skill_combos; DROP TABLE character_skill_point_sources; DROP TABLE account_expert_contracts; DROP TABLE character_dungeon_settlement_receipts; DROP TABLE character_dungeon_pickup_receipts; DROP TABLE character_dungeon_loot_receipts; DROP TABLE character_dungeon_state; DROP TABLE character_game_settings; PRAGMA user_version=6; CREATE TABLE character_game_settings(collision INTEGER)");
        assets = f.Assets(includeAcceptance: false);
        existing = f.ExistingSettings();
        Reject<SqliteException>(() => new ClientSettingsRepository(f.Path), "v7 DDL冲突不能报告升级成功");
        Check(f.Query("PRAGMA user_version") is 6L && f.ExistingSettings() == existing && f.Assets(includeAcceptance: false) == assets &&
            f.Query("SELECT count(*) FROM pragma_table_info('character_game_settings') WHERE name='collision'") is 1L,
            "v7失败保留版本、冲突原表、两份已存设置及全部资产");
        Reject<SqliteException>(() => new ClientSettingsRepository(f.Path), "v7失败库重开仍明确拒绝");
        f.Execute("DROP TABLE character_game_settings");
        var retried = new ClientSettingsRepository(f.Path);
        Check(Convert.ToInt64(f.Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion && f.ExistingSettings() == existing && f.Assets(includeAcceptance: false) == assets &&
            retried.Load(f.Owner, f.First.Id).CharacterGameOptions.IsDefault,
            "清除v7冲突后完整重开升级且不迁入其他角色的主设置");
        f.Integrity();
    }

    static ImmutableArray<byte> Pattern(int length, int seed)
    {
        byte[] bytes = Enumerable.Range(0, length).Select(value => unchecked((byte)(value * 37 + seed))).ToArray();
        if (length >= 2)
            bytes[^1] = bytes[^2] = 0xff;
        return [.. bytes];
    }

    static bool Same(ImmutableArray<byte> actual, ImmutableArray<byte> expected) =>
        actual.IsDefault == expected.IsDefault && (actual.IsDefault || actual.AsSpan().SequenceEqual(expected.AsSpan()));
    static void Assert(ClientSettingsSnapshot actual, ImmutableArray<byte> game, ImmutableArray<byte> character, string message) =>
        Check(Same(actual.GameOptions, game) && Same(actual.CharacterOptions, character), message);
    static void Check(bool value, string message) => TestCheck.That(value, message);

    static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        db.Open();
        return db;
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "s5a9-client-settings-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public AccountOwners.Lease OtherOwner { get; }
        public ClientSettingsService Service { get; }
        public Character First { get; }
        public Character Second { get; }
        public Character Other { get; }

        public Fixture()
        {
            Path = System.IO.Path.Combine(root, "game.db");
            var repository = new CharacterRepository(Path);
            Execute("INSERT INTO accounts(account_id,salt,password_hash,iterations,revision) VALUES('settings-a',X'0123',X'4567',100000,7),('settings-b',X'89AB',X'CDEF',100000,11)");
            Owner = Owners.Acquire("settings-a", () => { });
            OtherOwner = Owners.Acquire("settings-b", () => { });
            var location = new Location(38, 1, 450, 234);
            NewCharacter Role(string name) => new(name, location, location)
            {
                Level = 23, AcceptedStoryLevel = 11, CompletedQuests = [1], Skills = [new(3, 2)],
                ActiveMainQuests = [new(0, 2, 3)],
                Inventory = new([new(0, new(InventoryListType.Main, 121), new(1001, ItemKind.Material, 3))], Gold: 200)
            };
            First = repository.Create(Owner, Role("SettingA"));
            Second = repository.Create(Owner, Role("SettingB"));
            Other = repository.Create(OtherOwner, Role("SettingC"));
            Service = new(new ClientSettingsRepository(Path));
        }

        public long SettingRows() => (long)Query("SELECT (SELECT count(*) FROM account_client_settings)+(SELECT count(*) FROM character_client_settings)+(SELECT count(*) FROM character_game_settings)")!;
        public string ExistingSettings() => string.Join('|', Query("SELECT group_concat(account||':'||hex(game_options),'|') FROM (SELECT * FROM account_client_settings ORDER BY account)"),
            Query("SELECT group_concat(character_id||':'||account||':'||hex(character_options),'|') FROM (SELECT * FROM character_client_settings ORDER BY character_id)"));
        public object? Query(string sql)
        {
            using var db = Open(Path);
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
        public void Execute(string sql)
        {
            using var db = Open(Path);
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public string Assets(bool includeAcceptance = true)
        {
            using var db = Open(Path);
            using var tables = db.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT IN ('account_client_settings','character_client_settings','character_game_settings','account_hotkey_settings') ORDER BY name";
            var names = new List<string>();
            using (var reader = tables.ExecuteReader())
                while (reader.Read()) names.Add(reader.GetString(0));
            var rows = new List<string>();
            foreach (string name in names)
            {
                using var query = db.CreateCommand();
                query.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\" ORDER BY rowid";
                if (!includeAcceptance && name == "character_active_main_quests")
                    query.CommandText = "SELECT character_id,slot,quest_id,condition_state FROM character_active_main_quests ORDER BY rowid";
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    // 增量迁移可能改变物理列序；按列名保留全部原有值，不能把换序误判成资产变化。
                    var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    for (int index = 0; index < reader.FieldCount; index++)
                    {
                        string column = reader.GetName(index);
                        if (includeAcceptance || column is not ("quest_cycle_day" or "daily_period" or "answer" or "equipment_slot_flags" or "emotion"))
                            values.Add(column, reader.GetValue(index));
                    }
                    if (!includeAcceptance && name == "characters")
                    {
                        // v5/v6夹具没有魔王字段：预期升级为空账本和零期限，已有字段的真实值仍完整参与比较。
                        values.TryAdd("devil_usage", "[]");
                        values.TryAdd("stamina_until", 0L);
                    }
                    rows.Add(name + ":" + JsonSerializer.Serialize(values));
                }
            }
            return string.Join('|', rows);
        }

        public void Integrity() => Check(Query("PRAGMA integrity_check") is "ok" && Query("SELECT count(*) FROM pragma_foreign_key_check") is 0L,
            "设置保存或升级后数据库与外键完整");
        public void Dispose()
        {
            Owner.Dispose();
            OtherOwner.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
