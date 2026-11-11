using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class CharacterCopyTests
{
    static readonly Location Spawn = new(1, 1, 100, 100);

    public static void Run()
    {
        int checks = Count;
        using (var f = new Fixture())
        {
            var source = f.LoadSource();
            f.Sql($"INSERT INTO character_epic_wishes(character_id,account,item_ids) VALUES({source.Id},'copy-account','[5000,0,7000,0,0]')");
            f.Sql("INSERT INTO account_daily_attendance_days VALUES('copy-account',20261104,2);" +
                $"INSERT INTO account_daily_attendance_claims VALUES('copy-account',0,0,20261104,{source.Id},1,50001,2,1)");
            string attendanceDays = f.Snapshot("account_daily_attendance_days");
            string attendanceClaims = f.Snapshot("account_daily_attendance_claims");
            f.Sql("INSERT INTO account_returning_mission_progress VALUES('copy-account',2000000000,1,7);" +
                $"INSERT INTO account_returning_reward_claims VALUES('copy-account',2000000000,4,1,0,{source.Id},50001,2,123456)");
            string returningProgress = f.Snapshot("account_returning_mission_progress");
            string returningClaims = f.Snapshot("account_returning_reward_claims");
            source = f.LoadSource();
            string sourceJson = JsonSerializer.Serialize(source);
            var copy = f.Copy("CopyOne");
            That(copy.Id != source.Id && copy.Slot != source.Slot && copy.Name == "CopyOne", "复制分配独立角色身份与空槽");
            That(copy.Job == source.Job && copy.Level == source.Level && copy.GrowType == source.GrowType &&
                copy.CompletedQuests.SequenceEqual(source.CompletedQuests) && copy.Skills.SequenceEqual(source.Skills), "属性任务和技能保留");
            That(copy.Position == Spawn && copy.Return == Spawn && copy.Guild is null && copy.Marriage is null, "新角色在正常房间且不继承社会身份");
            That(copy.Inventory.Gold == source.Inventory.Gold && copy.Inventory.Items.Length == source.Inventory.Items.Length, "角色金币和所有库存容器保留");
            var shared = source.Inventory.Items.Single(item => item.Position.List == InventoryListType.AccountCargo);
            That(copy.Inventory.Find(shared.Position) == shared && copy.Inventory.AccountCargo == source.Inventory.AccountCargo &&
                copy.Inventory.AccountWallet.Same(source.Inventory.AccountWallet), "共享仓库和钱包不复制或递增版本");
            var common45 = source.Inventory.Items.Single(item => item.Position.List == InventoryListType.AccountCommon);
            That(copy.Inventory.Find(common45.Position) == common45 &&
                f.Scalar($"SELECT count(*) FROM character_inventory_items WHERE item_uid={common45.Uid} AND character_id IS NULL") == 1 &&
                f.Scalar("SELECT count(*) FROM character_inventory_slots WHERE list_type=45") == 1,
                "复制复用45原UID、完整核心和账号owner，不克隆或递增共享版本");
            var privateItems = source.Inventory.Items.Where(item => item.Position.List is not
                (InventoryListType.AccountCargo or InventoryListType.AccountCommon)).ToArray();
            That(privateItems.All(item => copy.Inventory.Find(item.Position) is { } next && next.Uid != item.Uid) &&
                !privateItems.Select(item => item.Uid).Intersect(copy.Inventory.Items.Select(item => item.Uid)).Any(), "所有角色物品UID重新分配且不与原件重叠");
            foreach (var item in privateItems)
            {
                var next = copy.Inventory.Find(item.Position)!;
                That(item.Avatar is null && item.Creature is null ? next.Core == item.Core : next.Core.Value == next.Uid,
                    "普通装备核心无损，时装宠物核心引用新UID");
            }
            var originalAvatar = copy.Inventory.Items.Single(item => item.Position == new InventorySlot(InventoryListType.Avatar, 0));
            var clone = copy.Inventory.Items.Single(item => item.Position == new InventorySlot(InventoryListType.Avatar, 1));
            That(clone.Avatar!.ClearAvatarSourceUid == originalAvatar.Uid, "克隆装扮借用新角色自己的原件");
            var part = copy.Inventory.AvatarPresets.Presets.Single().Parts.Single();
            That(part.ItemUid == clone.Uid && part.ClearAvatarSourceUid == originalAvatar.Uid, "装扮预设双重实例引用全部重映射");
            That(copy.ActiveMainQuests.Single().AcceptanceId != source.ActiveMainQuests.Single().AcceptanceId &&
                copy.ActiveMainQuests.Single().QuestId == source.ActiveMainQuests.Single().QuestId, "进行中任务使用新接取身份");
            That(f.ScalarText($"SELECT account FROM character_epic_wishes WHERE character_id={copy.Id}") == "copy-account" &&
                f.ScalarText($"SELECT item_ids FROM character_epic_wishes WHERE character_id={copy.Id}") == "[5000,0,7000,0,0]" &&
                f.Scalar($"SELECT count(*) FROM character_epic_wishes WHERE character_id={source.Id}") == 1 &&
                f.ScalarText($"SELECT item_ids FROM character_epic_wishes WHERE character_id={source.Id}") == "[5000,0,7000,0,0]",
                "角色复制保留同账号五槽祈愿顺序，重写新角色外键且来源行不变");
            That(sourceJson == JsonSerializer.Serialize(f.LoadSource()), "原角色完整领域快照不变");
            That(f.Snapshot("account_daily_attendance_days") == attendanceDays &&
                f.Snapshot("account_daily_attendance_claims") == attendanceClaims &&
                f.Scalar($"SELECT count(*) FROM account_daily_attendance_claims WHERE recipient_id={copy.Id}") == 0,
                "角色复制保留账号签到与原收件事实，不为新角色复制领奖资格");
            That(f.Snapshot("account_returning_mission_progress") == returningProgress &&
                f.Snapshot("account_returning_reward_claims") == returningClaims &&
                f.Scalar($"SELECT count(*) FROM account_returning_reward_claims WHERE recipient_id={copy.Id}") == 0,
                "角色复制保留账号回归进度及原收件事实，不重置或复制回归领奖资格");
            var reopened = new CharacterRepository(f.Path);
            var loaded = reopened.LoadCharacter(f.Owner, copy.Slot);
            That(JsonSerializer.Serialize(copy) == JsonSerializer.Serialize(loaded), "仓储重开后新角色完整加载一致");
            using var db = f.Open(); using var command = db.CreateCommand();
            command.CommandText = "PRAGMA foreign_key_check";
            using var reader = command.ExecuteReader(); That(!reader.Read(), "复制后全库外键无孤立记录");
        }
        using (var f = new Fixture())
        {
            f.Copy("Occupied");
            Reject(() => f.Copy("Occupied"), "同名拒绝");
            Reject(() => f.Copy("bad name"), "非法名称拒绝");
            Reject(() => f.Repository.CopyCharacter(f.Owner, f.Request("Stale") with { Revision = f.Source.Revision + 1 }), "过时修订拒绝");
            Reject(() => f.Repository.CopyCharacter(f.Owner, f.Request("WrongId") with { SourceId = (ushort)(f.Source.Id + 1) }), "原角色身份不符拒绝");
            That(f.Repository.ListRoster(f.Owner).Length == 2, "拒绝不创建半成品角色");
            f.Sql("CREATE TRIGGER copy_fail BEFORE INSERT ON character_skills WHEN NEW.character_id<>" + f.Source.Id + " BEGIN SELECT RAISE(FAIL,'test late copy failure'); END;");
            var before = f.CountRows("character_inventory_items");
            Reject(() => f.Copy("LateFail"), "晚阶段失败回滚");
            That(f.CountRows("character_inventory_items") == before && f.Repository.ListRoster(f.Owner).Length == 2, "晚失败回滚角色物品和关联行");
            f.Sql("DROP TRIGGER copy_fail; CREATE TABLE character_unknown_state(character_id INTEGER, value TEXT);");
            Reject(() => f.Copy("Unknown"), "未知角色关联不猜着复制");
        }
        using (var f = new Fixture())
        {
            ushort sourceId = f.Source.Id;
            f.Sql($"INSERT INTO character_teleport_cooldowns(character_id,account,potion_until,aura_until,upgrade_aura_until,party_aura_until,privilege_until) " +
                $"VALUES({sourceId},'copy-account',101,102,103,104,105);" +
                $"INSERT INTO character_quest_notify(character_id,account,entries) VALUES({sourceId},'copy-account',X'000102030405060708090A0B0C0D0E0F');" +
                $"INSERT INTO character_npc_bookmarks(character_id,account,entries) VALUES({sourceId},'copy-account',X'11000000');" +
                $"INSERT INTO character_fatigue_acceleration VALUES({sourceId},'copy-account',1);" +
                $"INSERT INTO character_pcroom_claims VALUES({sourceId},'copy-account',20261102);" +
                $"INSERT INTO character_privilege_service VALUES({sourceId},1000,2000,1,1,10,2,10,1);" +
                $"INSERT INTO character_fiendwar_counter_stream VALUES({sourceId},'copy-account','copy-stream',9);" +
                $"INSERT INTO character_strongway2019_statistics VALUES('copy-account',{sourceId},'11111111111111111111111111111111',1,0,5359,0,123,'{{}}');" +
                $"INSERT INTO account_strongway2019_rewards VALUES('copy-account','22222222222222222222222222222222',{sourceId},1,0,5359,0,1,1,1,'{{}}','{{}}',1);" +
                $"INSERT INTO character_strongway2019_completions VALUES('copy-account','33333333333333333333333333333333',{sourceId},1,0,5359,0,1,1,1,'{{}}','{{}}',0,NULL);" +
                $"INSERT INTO character_strongway2019_match_facts VALUES('copy-account',{sourceId},'44444444444444444444444444444444','55555555555555555555555555555555',1,1,1,1,0,'{{}}');");
            string[] originalOnlyTables =
            [
                "character_privilege_service", "character_pcroom_claims", "character_fiendwar_counter_stream", "account_strongway2019_rewards",
                "character_strongway2019_statistics", "character_strongway2019_completions", "character_strongway2019_match_facts"
            ];
            var originalRows = originalOnlyTables.ToDictionary(table => table, f.Snapshot);
            string sourceJson = JsonSerializer.Serialize(f.LoadSource());
            That(f.LoadSource().Inventory.PrivilegeService.ExpiresAt == 2000, "来源确实持有独立付费资格账本");
            var copy = f.Copy("CopySchema");
            That(copy.Inventory.PrivilegeService.ExpiresAt == 0, "复制后完整加载也不继承原角色付费资格");
            That(f.Scalar($"SELECT count(*) FROM character_npc_bookmarks WHERE account='copy-account' AND entries=X'11000000' AND character_id IN ({sourceId},{copy.Id})") == 2,
                "复制保留非空人物辞典收藏并重写角色归属，来源保持");
            That(f.Scalar($"SELECT count(*) FROM character_fatigue_acceleration WHERE character_id={copy.Id} AND account='copy-account' AND enabled=1") == 1,
                "复制保留角色疲劳燃烧选择，领取事实仍归原角色");
            That(f.Scalar($"SELECT count(*) FROM character_teleport_cooldowns WHERE character_id={copy.Id} AND account='copy-account' " +
                "AND potion_until=101 AND aura_until=102 AND upgrade_aura_until=103 AND party_aura_until=104 AND privilege_until=105") == 1,
                "五种角色传送冷却完整保留，不借复制重置礼遇冷却");
            That(f.Scalar($"SELECT count(*) FROM character_quest_notify WHERE character_id={copy.Id} AND account='copy-account' " +
                "AND entries=X'000102030405060708090A0B0C0D0E0F'") == 1, "任务提示原始十六字节归属新角色");
            foreach (string table in originalOnlyTables)
            {
                That(f.Scalar($"SELECT count(*) FROM {table} WHERE character_id={copy.Id}") == 0,
                    "不复制付费资格、去重流或强者之路原运行账本：" + table);
                That(f.Snapshot(table) == originalRows[table], "来源资格、账号奖励额度和恢复事实完全不变：" + table);
            }
            That(sourceJson == JsonSerializer.Serialize(f.LoadSource()), "当前结构复制不改变原角色快照");
        }
        using (var f = new Fixture())
        {
            f.Sql("ALTER TABLE character_teleport_cooldowns ADD COLUMN future_state INTEGER NOT NULL DEFAULT 0;");
            var before = f.CountRows("character_inventory_items");
            bool schemaRejected = false;
            try { f.Copy("UnknownCol"); }
            catch (InvalidDataException error) when (error.Message.Contains("character_teleport_cooldowns", StringComparison.Ordinal) &&
                error.Message.Contains("额外字段：future_state", StringComparison.Ordinal) && error.Message.Contains("不要手动删除字段或表", StringComparison.Ordinal))
            { schemaRejected = true; }
            That(schemaRejected, "已核对角色表增加未知字段仍由结构守卫整体拒绝");
            That(f.Repository.ListRoster(f.Owner).Length == 1 && f.CountRows("character_inventory_items") == before,
                "未知字段拒绝不产生半成品角色或物品");
        }
        foreach (string table in new[] { "characters", "character_teleport_cooldowns" })
        {
            using var f = new Fixture();
            f.Sql($"UPDATE characters SET emotion=123,clone_title_item_id=18400 WHERE id={f.Source.Id};" +
                $"INSERT INTO character_teleport_cooldowns(character_id,account,potion_until,aura_until,upgrade_aura_until,party_aura_until,privilege_until) " +
                $"VALUES({f.Source.Id},'copy-account',101,102,103,104,105);");
            var expectedCooldowns = new DfoServer.Game.Town.TeleportCooldowns(101, 102, 103, 104, 105);
            var source = f.LoadSource();
            That(source.Emotion == 123 && source.CloneTitleItemId == 18400 && source.TeleportCooldowns == expectedCooldowns,
                "列重排前真实来源具有不同非零后缀和完整五种冷却：" + table);
            string sourceBefore = JsonSerializer.Serialize(source);
            string walletBefore = f.Snapshot("account_wallets"), cargoBefore = f.Snapshot("account_cargo");
            string first = table == "characters" ? "emotion INTEGER NOT NULL DEFAULT 0 CHECK(emotion BETWEEN 0 AND 32767)," : "character_id INTEGER PRIMARY KEY,";
            string second = table == "characters" ? "clone_title_item_id INTEGER NOT NULL DEFAULT 0 CHECK(typeof(clone_title_item_id)='integer' AND clone_title_item_id BETWEEN 0 AND 4294967295)," : "account TEXT NOT NULL,";
            ReorderColumns(f, table, first, second);
            That(JsonSerializer.Serialize(f.LoadSource()) == sourceBefore, "列重排本身不改变来源加载：" + table);
            var copy = f.Copy("OrderCopy");
            That(copy.Id != source.Id && copy.Job == source.Job && copy.Level == source.Level &&
                copy.Emotion == 123 && copy.CloneTitleItemId == 18400 && copy.TeleportCooldowns == expectedCooldowns,
                "仅物理列顺序不同仍精确保留非零角色后缀和完整五种冷却：" + table);
            That(f.Scalar($"SELECT count(*) FROM characters WHERE id={copy.Id} AND emotion=123 AND clone_title_item_id=18400") == 1 &&
                f.Scalar($"SELECT count(*) FROM character_teleport_cooldowns WHERE character_id={copy.Id} AND account='copy-account' " +
                    "AND potion_until=101 AND aura_until=102 AND upgrade_aura_until=103 AND party_aura_until=104 AND privilege_until=105") == 1,
                "目标按具名SQL投影读取仍与非默认原值逐项相等：" + table);
            That(JsonSerializer.Serialize(f.LoadSource()) == sourceBefore, "列重排复制不改来源角色：" + table);
            That(f.Snapshot("account_wallets") == walletBefore && f.Snapshot("account_cargo") == cargoBefore &&
                copy.Inventory.AccountWallet.Same(source.Inventory.AccountWallet) &&
                copy.Inventory.Items.Where(item => item.Position.List is InventoryListType.AccountCargo or InventoryListType.AccountCommon)
                    .SequenceEqual(source.Inventory.Items.Where(item => item.Position.List is InventoryListType.AccountCargo or InventoryListType.AccountCommon)),
                "非默认列重排复制不改变共享钱包、仓库及其原物品身份：" + table);
            var reopened = new CharacterRepository(f.Path);
            var loaded = reopened.LoadCharacter(f.Owner, copy.Slot);
            That(JsonSerializer.Serialize(loaded) == JsonSerializer.Serialize(copy) && loaded.Emotion == 123 &&
                loaded.CloneTitleItemId == 18400 && loaded.TeleportCooldowns == expectedCooldowns,
                "非默认重排结果重开仓储后完整加载一致：" + table);
            using var db = f.Open();
            using var foreignKeys = db.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            using var reader = foreignKeys.ExecuteReader();
            That(!reader.Read(), "非默认重排复制后全库外键无孤立引用：" + table);
        }
        using (var f = new Fixture())
        {
            ReorderColumns(f, "characters", "account TEXT NOT NULL,", "slot INTEGER NOT NULL CHECK(slot BETWEEN 0 AND 61),");
            string characters = f.Snapshot("characters"), items = f.Snapshot("character_inventory_items");
            bool rejected = false;
            try { f.Copy("CoreOrder"); }
            catch (InvalidDataException error) when (error.Message.Contains("角色核心字段顺序不兼容", StringComparison.Ordinal) &&
                error.Message.Contains("当前：slot；预期：account", StringComparison.Ordinal)) { rejected = true; }
            That(rejected && f.Snapshot("characters") == characters && f.Snapshot("character_inventory_items") == items,
                "仍按位置读取的核心列顺序不符时写入前拒绝，不能误解来源字段");
        }
        using (var f = new Fixture())
        {
            f.Sql("CREATE TABLE \"character_" + new string('扩', 100) + "\"(character_id INTEGER, value TEXT);");
            string characters = f.Snapshot("characters"), items = f.Snapshot("character_inventory_items");
            bool rejected = false;
            try { f.Copy("LongSchema"); }
            catch (InvalidDataException error) when (error.Message.Contains("未适配的角色关联表", StringComparison.Ordinal) &&
                error.Message.Contains("不要手动删除字段或表", StringComparison.Ordinal) && System.Text.Encoding.UTF8.GetByteCount("ERR " + error.Message + "\n") <= 1024)
            { rejected = true; }
            That(rejected && f.Snapshot("characters") == characters && f.Snapshot("character_inventory_items") == items,
                "超长自定义结构名仍整体拒绝，传回有界且可操作的提示，资产不写");
        }
        foreach (bool missingTable in new[] { false, true })
        {
            using var f = new Fixture();
            f.Sql(missingTable ? "DROP TABLE character_epic_wishes;" : "ALTER TABLE character_teleport_cooldowns DROP COLUMN party_aura_until;");
            string characters = f.Snapshot("characters");
            string items = f.Snapshot("character_inventory_items");
            string wallet = f.Snapshot("account_wallets");
            bool rejected = false;
            try { f.Copy("MissSchema"); }
            catch (InvalidDataException error) when (error.Message.Contains(missingTable ? "缺少表：character_epic_wishes" : "缺少字段：party_aura_until", StringComparison.Ordinal) &&
                error.Message.Contains("正常启动完成迁移", StringComparison.Ordinal))
            { rejected = true; }
            That(rejected, "缺少已知字段或关联表时给出具体差异和正常迁移动作");
            That(f.Snapshot("characters") == characters && f.Snapshot("character_inventory_items") == items && f.Snapshot("account_wallets") == wallet,
                "结构预检拒绝前不写角色、个人物品或账号共享钱包");
        }
        using (var f = new Fixture())
        {
            int accepted = 0;
            Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                try { f.Copy("Concurrent"); Interlocked.Increment(ref accepted); }
                catch (InvalidDataException) { }
            }))).GetAwaiter().GetResult();
            That(accepted == 1 && f.Repository.ListRoster(f.Owner).Length == 2, "并发同名只创建一次");
        }
        using (var f = new Fixture())
        {
            f.Sql("UPDATE sqlite_sequence SET seq=2147483647 WHERE name='character_inventory_items'");
            Reject(() => f.Copy("Overflow"), "时装宠物UID超过客户端范围时整体拒绝");
            That(f.Repository.ListRoster(f.Owner).Length == 1, "UID溢出不会留下坏角色");
        }
        using (var f = new Fixture())
        {
            var source = f.LoadSource();
            for (int slot = 1; slot < source.Inventory.AccountWallet.CharacterSlotCapacity; slot++)
                f.Repository.Create(f.Owner, new("Fill" + slot, Spawn, Spawn));
            int count = f.Repository.ListRoster(f.Owner).Length;
            Reject(() => f.Copy("NoRoom"), "满角色栏拒绝复制");
            That(f.Repository.ListRoster(f.Owner).Length == count, "满槽拒绝不改动现有名册");
        }
        using (var f = new Fixture())
        {
            var source = f.LoadSource();
            var equipment = source.Inventory.Items.First(item => item.Position.List == InventoryListType.Main);
            f.Sql($"INSERT INTO character_skill_switch_inventory VALUES({source.Id},'copy-account',1);" +
                $"INSERT INTO character_skill_switch_inventory_items VALUES({source.Id},12,{equipment.Uid},{equipment.Core.ItemId});" +
                $"INSERT INTO character_dungeon_incount VALUES({source.Id},'copy-account',1,123,2,120,3);" +
                $"INSERT INTO item_purchase_counts VALUES('copy-account',{source.Id},1,4,0,5);" +
                "INSERT INTO item_purchase_counts VALUES('copy-account',0,2,4,0,7);");
            var copy = f.Copy("CopyLimits");
            That(f.Scalar($"SELECT item_uid FROM character_skill_switch_inventory_items WHERE character_id={copy.Id}") == copy.Inventory.Find(equipment.Position)!.Uid,
                "切装栏重映射为新角色自己的装备UID");
            That(f.Scalar($"SELECT daily_used+weekly_used FROM character_dungeon_incount WHERE character_id={copy.Id}") == 5 &&
                f.Scalar($"SELECT count FROM item_purchase_counts WHERE character_id={copy.Id}") == 5,
                "角色入场次数及终身限购次数保留");
            That(f.Scalar("SELECT count(*) FROM item_purchase_counts WHERE character_id=0") == 1 &&
                f.Scalar("SELECT count FROM item_purchase_counts WHERE character_id=0") == 7,
                "账号级限购仍仅一份且次数不变");
        }
        using (var f = new Fixture())
        {
            using var spouseOwner = f.Owners.Acquire("copy-spouse", () => { });
            var source = f.LoadSource();
            var spouse = f.Repository.Create(spouseOwner, new("Spouse", Spawn, Spawn) { Job = 1, Level = 30 });
            var relation = new MarriageRecord(Guid.NewGuid(), 12345, 1, source.Id, spouse.Id, 1)
            {
                Members = ImmutableDictionary<ushort, MarriageMemberState>.Empty.Add(source.Id, new()).Add(spouse.Id, new()),
                CreatedAt = 1000
            };
            f.Repository.CommitMarriageTogether(f.Owner, spouseOwner, source, spouse,
                new(source.Inventory, source.Inventory, "copy_fixture"), new(spouse.Inventory, spouse.Inventory, "copy_fixture"), null, relation);
            f.Sql($"INSERT INTO guilds(name,address,master_character_id,created_at_unix) VALUES('CopyGuild','copy-guild',{source.Id},1000);" +
                $"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(last_insert_rowid(),{source.Id},0,1000)");
            source = f.LoadSource();
            That(source.Guild is not null && source.Marriage is not null, "来源真实拥有公会和婚姻关系");
            string original = JsonSerializer.Serialize(source);
            var copy = f.Repository.CopyCharacter(f.Owner, f.Request("CopySocial") with { Revision = source.Revision });
            That(copy.Marriage is null && copy.Guild is null && f.CountRows("marriage_records") == 1 && f.CountRows("guild_members") == 1,
                "复制不挪用或新增来源的婚姻与公会身份");
            That(JsonSerializer.Serialize(f.LoadSource()) == original && f.Repository.LoadCharacter(spouseOwner, spouse.Slot).Marriage!.Id == relation.Id,
                "复制保持原角色与配偶的真实社会关系不变");
        }
        using (var f = new Fixture())
        {
            f.Sql($"INSERT INTO account_pvp_private_characters VALUES('copy-account',0,1,{f.Source.Id},0,0)");
            That(f.LoadSource().IsPvpPrivate, "来源确为专用决斗角色");
            Reject(() => f.Copy("CopyPrivate"), "专用决斗角色不突破同账号职业唯一约束");
            That(f.Repository.ListRoster(f.Owner).Length == 1, "拒绝复制专用决斗角色不产生坏档");
        }
        Console.WriteLine($"PASS {Count - checks} character copy identity, inventory, references, rollback and concurrency assertions");
    }

    static void ReorderColumns(Fixture f, string table, string first, string second)
    {
        string schema = f.ScalarText("SELECT sql FROM sqlite_schema WHERE type='table' AND name='" + table + "'");
        string columns = f.ScalarText("SELECT group_concat('\"'||name||'\"',',') FROM pragma_table_info('" + table + "')");
        string dependents = f.ScalarText("SELECT coalesce(group_concat(sql,';'),'') FROM sqlite_schema WHERE tbl_name='" + table + "' AND type IN('index','trigger') AND sql IS NOT NULL");
        string reordered = schema.Replace(first, "__copy_first_column__", StringComparison.Ordinal)
            .Replace(second, first, StringComparison.Ordinal).Replace("__copy_first_column__", second, StringComparison.Ordinal);
        That(reordered != schema && schema.Contains(first, StringComparison.Ordinal) && schema.Contains(second, StringComparison.Ordinal),
            "夹具仅交换已知字段声明，保留类型、默认值和约束：" + table);
        f.Sql("PRAGMA foreign_keys=OFF; BEGIN; CREATE TEMP TABLE copy_before AS SELECT * FROM " + table + "; DROP TABLE " + table + ";" +
            reordered + "; INSERT INTO " + table + "(" + columns + ") SELECT " + columns + " FROM copy_before; DROP TABLE copy_before;" + dependents + "; COMMIT;");
    }

    static void Reject(Action action, string reason)
    {
        bool rejected = false;
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or SqliteException or OverflowException) { rejected = true; }
        That(rejected, reason);
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "character-copy-" + Guid.NewGuid().ToString("N"));
        readonly AccountOwners owners = new();
        public AccountOwners Owners => owners;
        public string Path => System.IO.Path.Combine(root, "game.db");
        public CharacterRepository Repository { get; }
        public AccountOwners.Lease Owner { get; }
        public Character Source { get; }

        public Fixture()
        {
            Directory.CreateDirectory(root);
            Repository = new(Path);
            Owner = owners.Acquire("copy-account", () => { });
            var created = Repository.Create(Owner, new("Original", Spawn, Spawn)
            {
                Level = 95, Job = 0, GrowType = 17, CompletedQuests = [100, 101], Skills = [new(1, 2)],
                ActiveMainQuests = [new(0, 102, 3) { AcceptanceId = Guid.NewGuid() }],
                Inventory = InventorySnapshot.Empty with
                {
                    Gold = 12345,
                    Items = [
                        new(0, new(InventoryListType.Main, 9), new(18400, ItemKind.Equipment, 999)) { },
                        new(0, new(InventoryListType.Equipment, 12), new(18400, ItemKind.Equipment, 999)),
                        new(0, new(InventoryListType.PersonalCargo, 0), new(3175, ItemKind.Material, 13)),
                        new(0, new(InventoryListType.TitleGeneral, 0), new(18400, ItemKind.Equipment, 999)),
                        new(0, new(InventoryListType.Avatar, 0), new(6000001, ItemKind.Avatar, 0)) { Avatar = new() },
                        new(0, new(InventoryListType.Avatar, 1), new(6000002, ItemKind.Avatar, 0)) { Avatar = new() },
                        new(0, new(InventoryListType.Pet, 0), new(24, ItemKind.Creature, 0)) { Creature = new() { Name = "测试宠物", Level = 3 } }
                    ]
                }
            });
            var sourceAvatar = created.Inventory.Find(new(InventoryListType.Avatar, 0))!;
            var clone = created.Inventory.Find(new(InventoryListType.Avatar, 1))!;
            var sockets = Enumerable.Repeat(new AvatarSocket(0, 0), 5).ToImmutableArray();
            var after = created.Inventory with
            {
                AccountCargo = created.Inventory.AccountCargo with { Capacity = 8 },
                Items = created.Inventory.Items.Replace(clone, clone with { Avatar = clone.Avatar! with { ClearAvatarId = sourceAvatar.Core.ItemId, ClearAvatarSourceUid = sourceAvatar.Uid } })
                    .Add(new(0, new(InventoryListType.AccountCargo, 0), new(3175, ItemKind.Material, 7)))
                    .Add(new(0, new(InventoryListType.AccountCommon, 0), new(490013716, ItemKind.Material, 9) { Extension97 = 1234, Attr = 32 })),
                AvatarPresets = new(1, [new(0, "套装", [new(0, clone.Uid, (uint)clone.Core.ItemId, (uint)sourceAvatar.Core.ItemId, 0, 0, sockets, sourceAvatar.Uid)])])
            };
            Source = Repository.CommitInventory(Owner, created, new(created.Inventory, after, "test_copy_source"));
        }

        public CharacterCopyRequest Request(string name) => new(Source.Name, Source.Id, Source.Revision, name, Spawn, Spawn);
        public Character Copy(string name) => Repository.CopyCharacter(Owner, Request(name));
        public Character LoadSource() => Repository.LoadCharacter(Owner, Source.Slot);
        public SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()); db.Open(); return db;
        }
        public void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public long CountRows(string table) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM " + table; return (long)command.ExecuteScalar()!; }
        public long Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!; }
        public string ScalarText(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return (string)command.ExecuteScalar()!; }
        public string Snapshot(string table)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid";
            using var reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
            return JsonSerializer.Serialize(rows);
        }
        public void Dispose() { Owner.Dispose(); TestFiles.DeleteDirectory(root, true); }
    }
}
