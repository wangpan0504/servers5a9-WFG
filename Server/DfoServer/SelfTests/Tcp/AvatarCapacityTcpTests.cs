using static DfoServer.Testing.CashMileageTcpChecks;
using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class AvatarCapacityTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var r = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "avatar-capacity"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var product = r.CashShop.Product(104415)!;
        That(r.Items.Get(product.ItemId).AvatarExpansionStage == 16 && product.CeraPrice > 0, "当前PVF第16阶段及价格");
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repository.Create(owner, new("AvatarCap", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
            InventoryService.Commit(repository, owner, role, new CurrencyService(r.GoldLimits).Grant(role, CurrencyKind.Cera, 1_000_000));
        }
        string migrationAssets = MigrationAssets();
        // 恢复v47实际缺列和两个槽位约束，并把领奖账本的段位类型5退回v47之后才有的v127之前结构。
        fixture.Sql("DROP TABLE character_skill_switch_inventory_items; DROP TABLE character_skill_switch_inventory; DROP TABLE character_quest_stage_claims; DROP TABLE character_quest_stage_witnesses");
        fixture.Sql("ALTER TABLE characters DROP COLUMN devil_usage; ALTER TABLE characters DROP COLUMN stamina_until; ALTER TABLE characters DROP COLUMN daily_booster_usage");
        fixture.Sql("ALTER TABLE characters DROP COLUMN avatar_expansion");
        DatabaseSchemaTests.RewindRaidLifecycle(fixture.Database);
        DatabaseSchemaTests.RewindQuestTargetTypes(fixture.Database);
        fixture.Sql(DatabaseSchemaTests.GuildExtensionFixtureRewindSql + "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(replace(sql,'CHECK(list_type<>1 OR slot_index<420)','CHECK(list_type<>1 OR slot_index<210)'),'CHECK(slot_index BETWEEN 0 AND 351 OR (list_type=1 AND slot_index BETWEEN 352 AND 419))','CHECK(slot_index BETWEEN 0 AND 351)') WHERE name='character_inventory_slots'; UPDATE sqlite_schema SET sql=replace(replace(sql,'condition_type IN(0,1,2,3,4,5,6,','condition_type IN(0,1,2,3,4,6,'),'condition_type IN(0,2,4,5,6,','condition_type IN(0,2,4,6,') WHERE name='character_quest_completion_receipts'; PRAGMA writable_schema=OFF; PRAGMA schema_version=9000; ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=47");
        That(fixture.Query("PRAGMA user_version") is [[47L]] &&
            fixture.Query("SELECT instr(sql,'condition_type IN(0,1,2,3,4,5,6,')=0 AND instr(sql,'condition_type IN(0,2,4,5,6,')=0 FROM sqlite_schema WHERE name='character_quest_completion_receipts'") is [[1L]] &&
            fixture.Query("SELECT count(*) FROM sqlite_schema WHERE name IN('character_quest_stage_claims','character_quest_stage_witnesses')") is [[0L]] &&
            fixture.Query("SELECT count(*) FROM sqlite_schema WHERE name IN('character_skill_switch_inventory','character_skill_switch_inventory_items')") is [[0L]] &&
            fixture.Query("SELECT count(*) FROM pragma_table_info('characters') WHERE name IN('devil_usage','stamina_until','daily_booster_usage')") is [[0L]] &&
            fixture.Query("SELECT count(*) FROM pragma_table_info('characters') WHERE name='avatar_expansion'") is [[0L]],
            "装扮迁移输入为缺容量列的v47旧库，不残留权益与每日列、v53阶段或v54强化旁表");
        await fixture.StartAsync();
        That(fixture.Query("PRAGMA user_version") is [[long version]] && version == SqliteMigrations.CurrentVersion &&
            fixture.Query("SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_quest_stage_claims','character_quest_stage_witnesses')") is [[2L]] &&
            fixture.Query("SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_skill_switch_inventory','character_skill_switch_inventory_items')") is [[2L]],
            "v47旧库真实升级至当前版本并建立阶段与强化旁表");
        That(fixture.Query("SELECT avatar_expansion,daily_booster_usage FROM characters") is [[105L, "[]"]], "旧角色保留默认210格且每日账本为空");
        That(MigrationAssets() == migrationAssets, "旧库迁移保持角色归属、revision、金币、钱包和完整库存实体及槽位");
        fixture.Sql("CREATE TRIGGER fail_avatar_purchase BEFORE UPDATE OF avatar_expansion ON characters WHEN NEW.avatar_expansion>OLD.avatar_expansion BEGIN SELECT RAISE(ABORT,'avatar rollback'); END");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier(); string before = State(); peer.Send(64, Purchase()); peer.Closed();
            That(State() == before, "扩展写入失败时扣款和revision全部回滚");
        }
        fixture.Sql("DROP TRIGGER fail_avatar_purchase");
        using (var peer = Open(fixture))
        {
            peer.Select();
            var mileage = MileageBefore(fixture, ExpectedCashMileage(product));
            peer.Send(64, Purchase());
            var avatar = peer.Expect(0, 13).Body;
            That(avatar[0] == 1 && U16(avatar, 1) == 112, "购买确认前同步扩展112即总217格，原生重筛读取新容量");
            That(peer.Expect(1, 64).Body[0] == 1, "装扮扩展购买成功");
            That(U32(peer.Expect(0, 53).Body, 1) == 1_000_000 - product.CeraPrice, "按资源价格扣款");
            MileageAfter(peer, fixture, mileage);
            peer.Barrier(); string committed = State();
            peer.Send(64, Purchase()); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            That(State() == committed, "重复阶段不扣款也不发无效券");
        }
        await fixture.StopAsync(); await fixture.StartAsync("restart");
        using (var peer = Open(fixture)) { peer.Select(avatarExpansion: 112); peer.Barrier(); }
        await fixture.StopAsync();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repository.LoadCharacter(owner, 0);
            var stale = new CurrencyService(r.GoldLimits).Grant(role, CurrencyKind.Gold, 1);
            fixture.Sql("UPDATE characters SET avatar_expansion=119");
            Reject<InvalidOperationException>(() => InventoryService.Commit(repository, owner, role, stale), "同revision容量变化也拒绝旧快照写回");
            fixture.Sql("UPDATE characters SET avatar_expansion=112");
            var template = r.Items.Get(415600033);
            var prototype = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Avatar, 0),
                InventoryRewardGrantService.Create(template));
            var full = role.Inventory with { Items = Enumerable.Range(0, 210).Select(i => prototype with
                { Uid = i + 1000, Position = new(InventoryListType.Avatar, (short)i), Core = prototype.Core with { Value = i + 1000 } }).ToImmutableArray() };
            var rewards = Enumerable.Repeat(new InventoryReward(template.Id, 1), 7).ToArray();
            var grant = InventoryRewardGrantService.TryGrantBatch(role with { Inventory = full }, rewards, r.Items, r.GoldLimits, r.EpicPieces);
            That(grant.Accepted && grant.Plan!.After.Items.Length == 217 && grant.Plan.After.Find(new(InventoryListType.Avatar, 216)) is not null,
                "七件奖励全部使用新容量210至216");
            var filled = grant.Plan!.After;
            That(!InventoryRewardGrantService.TryGrantBatch(role with { Inventory = filled }, [new(template.Id, 1)], r.Items, r.GoldLimits, r.EpicPieces).Accepted,
                "满217格普通发奖保持原子拒绝");
            var overflow = InventoryRewardGrantService.TryGrantBatch(role with { Inventory = filled }, [new(template.Id, 1)], r.Items, r.GoldLimits, r.EpicPieces, allowOverflowMail: true);
            That(overflow.Accepted && overflow.Plan!.OverflowItems.Length == 1, "满217格允许邮件的路径只溢出新增一件");
            var one = full with { Items = [full.Items[0]] };
            InventoryMoveRequest Move(short destination) => new(new(InventoryListType.Avatar, 0), template.Id, 1,
                new(InventoryListType.Avatar, destination), 0, 0, 0, 0, 0);
            var moved = InventoryMoveService.Plan(role with { Inventory = one }, Move(216), r.Items, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            That(moved.Accepted && !InventoryMoveService.Plan(role with { Inventory = one }, Move(217), r.Items, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Accepted,
                "拖放允许末格216而拒绝217");
            var sorted = InventorySortService.Plan(moved.Plan!.After, ItemKind.Avatar, 0, r.Items, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), InventoryListType.Avatar);
            That(sorted.Accepted && sorted.Plan!.After.Find(new(InventoryListType.Avatar, 0)) is not null, "整理包括新增末格");
            var service = new InventoryCashShopService(r.Items, r.CashShop, r.GoldLimits, r.EpicPieces, r.Cargo, contracts: r.Contracts);
            That(!service.Buy(role with { Inventory = role.Inventory with { AvatarExpansion = 98 } }, new(0, [new(104415, 0)]), out _).Accepted,
                "跳过下一阶段不可购买");
            var maximum = role.Inventory with { AvatarExpansion = 315, Items = [prototype with { Position = new(InventoryListType.Avatar, 419) }] };
            role = InventoryService.Commit(repository, owner, role, new(true, 0, new(role.Inventory, maximum, "avatar_maximum_fixture")));
            var restored = repository.LoadCharacter(owner, 0);
            That(restored.Inventory.AvatarExpansion == 315 && restored.Inventory.Find(new(InventoryListType.Avatar, 419)) is not null,
                "最大420容量和第419槽真实落库重读");
            That(!ItemSlotBoundService.Allows(restored.Inventory, new(InventoryListType.Avatar, 420), ItemKind.Avatar), "420号槽越界");
            Reject<InvalidDataException>(() => (restored.Inventory with { AvatarExpansion = 316 }).Validate(), "拒绝非法阶段");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "容量迁移及保存保留完整性和外键");
        string MigrationAssets() => fixture.Snapshot("SELECT id,account,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") +
            fixture.Snapshot("SELECT * FROM character_inventory_items") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots");
        string State() => fixture.Snapshot("SELECT revision,avatar_expansion FROM characters") + fixture.Snapshot("SELECT * FROM account_currency_balances") + fixture.Snapshot("SELECT * FROM character_inventory_items");
    }

    static byte[] Purchase()
    {
        byte[] body = new byte[19]; body[2] = 1; BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7), 104415); return body;
    }
}
