using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Infrastructure;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using DfoServer.Game.Raids;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class AccountMaterialTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(8));
    const int Coin = 10308807;

    public static void RunCore()
    {
        int week = AccountMaterialService.RaidWeek(Now);
        var reset = AccountMaterialService.WeekStart(week).AddDays(7);
        That(AccountMaterialService.RaidWeek(reset.AddTicks(-1)) == week &&
            AccountMaterialService.RaidWeek(reset) == week + 7, "账号材料周四六点边界独立于团队次数周");
        var definition = new AccountMaterialDefinition(4000, 4000, 1300);
        var inventory = InventorySnapshot.Empty;
        foreach (ushort character in new ushort[] { 1, 2, 3, 4 })
            inventory = AccountMaterialService.Grant(inventory, character, Coin, 2000, definition, Now, true).Plan!.After;
        var state = inventory.AccountWallet.Material(Coin);
        That(state.Balance == 4000 && state.RaidEarned == 4000 && state.EarnedBy(4, week) == 100 &&
            state.EarnedBy(1, week) == 1300, "多人及多角色合并账号额度且保留角色额度和部分余量");
        inventory = inventory with { AccountWallet = inventory.AccountWallet.SetMaterial(Coin, state with { Balance = 0 }) };
        var exhausted = AccountMaterialService.Grant(inventory, 1, Coin, 150, definition, Now, true).Plan!;
        That(exhausted.After.AccountWallet.Material(Coin).Balance == 0 && exhausted.After.AccountWallet.Material(Coin).RaidEarned == 4000,
            "消费余额不返还本周获得额度，达到上限不阻断其他奖励");
        var renewed = AccountMaterialService.Grant(inventory, 1, Coin, 150, definition, reset, true).Plan!;
        That(renewed.After.AccountWallet.Material(Coin) is { Balance: 150, RaidEarned: 150 } next &&
            next.EarnedBy(1, week + 7) == 150 && next.EarnedBy(2, week + 7) == 0, "跨周重置额度而非余额");
        That(!AccountMaterialService.Grant(inventory, 1, Coin, 4001, definition, Now, false).Accepted,
            "普通购买领取按持有上限足额获取或拒绝，不静默吞掉数量");
        var ordinary = AccountMaterialService.Grant(inventory, 1, Coin, 10, definition, Now, false).Plan!.After;
        That(ordinary.AccountWallet.Material(Coin) is { Balance: 10, RaidEarned: 4000 }, "非攻坚获取不消耗攻坚周额度");
        var many = AccountWallet.Empty;
        for (int id = 1; id <= 256; id++) many = many.SetMaterial(id, new(id));
        var frames = AccountMaterialPackets.Balances(many).ToArray();
        That(frames.Length == 2 && frames[0][0] == 0 && frames[0][1] == 255 && frames[0].Length == 2042 &&
            frames[1][0] == 1 && frames[1][1] == 1 && frames[1].Length == 10 &&
            BinaryPrimitives.ReadInt32LittleEndian(frames[1].AsSpan(2)) == 256, "563先全量后增量分片，不截断u8行数");
        That(AccountMaterialPackets.Balances(AccountWallet.Empty).Single().SequenceEqual(new byte[] { 0, 0 }) &&
            !AccountMaterialPackets.Balances(many, many).Any(), "空账号清旧缓存，无余额变化不伪造差量");
        var deleted = AccountMaterialPackets.Balances(AccountWallet.Empty, AccountWallet.Empty.SetMaterial(Coin, new(10))).Single();
        That(deleted.Length == 10 && deleted[0] == 1 && deleted[1] == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(deleted.AsSpan(6)) == 0, "563消费至零明确删除客户端旧项");
        RunCalendarCore();
        var limits = new Dictionary<int, AccountMaterialDefinition> { [Coin] = definition };
        var accountBody = AccountMaterialPackets.Limits(ordinary.AccountWallet, 1, limits, Now, true);
        var characterBody = AccountMaterialPackets.Limits(ordinary.AccountWallet, 1, limits, Now, false);
        That(accountBody.Length == 14 && BinaryPrimitives.ReadUInt16LittleEndian(accountBody) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(accountBody.AsSpan(2)) == Coin &&
            BinaryPrimitives.ReadInt32LittleEndian(accountBody.AsSpan(6)) == 4000 &&
            BinaryPrimitives.ReadInt32LittleEndian(accountBody.AsSpan(10)) == 4000 &&
            BinaryPrimitives.ReadInt32LittleEndian(characterBody.AsSpan(6)) == 1300 &&
            BinaryPrimitives.ReadInt32LittleEndian(characterBody.AsSpan(10)) == 1300, "1460/1461各自完整三u32行，角色与账号不混用");
        Console.WriteLine("PASS account material core: 10 checks");
    }

    public static void RunCalendarCore()
    {
        int start = Count;
        int[] ids = [3175, Coin];
        var before = AccountWallet.Empty.SetMaterial(Coin, new(5)).SetMaterial(3175, new(7));
        var current = before.SetMaterial(Coin, new(12)).SetMaterial(3175, new(0)).SetMaterial(999, new(123));
        byte[] full = ActivityCalendarItemPackets.Full(45, current, ids);
        That(full.Length == 126 && full[0] == 45 && BinaryPrimitives.ReadUInt16LittleEndian(full.AsSpan(1)) == 1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(full.AsSpan(3)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(full.AsSpan(5)) == Coin && BinaryPrimitives.ReadInt32LittleEndian(full.AsSpan(9)) == 12 &&
            full.AsSpan(13).IndexOfAnyExcept((byte)0) == -1,
            "零余额模板保留原槽序，未登记材料不进入list45，完整123B材料行不制造物理UID");
        var updates = ActivityCalendarItemPackets.Changes(45, before, current, ids).ToArray();
        That(updates.Length == 2 && !updates[0].Obtained && updates[1].Obtained &&
            updates.All(row => row.Body.Length == 126 && row.Body[0] == 45 && BinaryPrimitives.ReadUInt16LittleEndian(row.Body.AsSpan(1)) == 1),
            "同一事务多种活动材料有增有减时分别静默同步和播报获得，均使用原生完整行");
        That(BinaryPrimitives.ReadUInt16LittleEndian(updates[0].Body.AsSpan(3)) == 0 &&
            BinaryPrimitives.ReadInt32LittleEndian(updates[0].Body.AsSpan(5)) == -1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(updates[1].Body.AsSpan(3)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(updates[1].Body.AsSpan(9)) == 12,
            "归零明确清原槽，增加行携带最终12而非本次7，客户端自行计算新增数量");
        That(!ActivityCalendarItemPackets.Changes(45, current, current, ids).Any() &&
            ActivityCalendarItemPackets.Full(45, AccountWallet.Empty, ids).SequenceEqual(new byte[] {45, 0, 0}),
            "重复同步不重放获得，空账号全量清除上一个角色的显示");
        Reject<InvalidDataException>(() => ActivityCalendarItemPackets.Full(44, current, Enumerable.Range(1, 65).ToArray()),
            "账号材料显示槽遵守本版list44的64格边界");
        Reject<InvalidDataException>(() => ActivityCalendarItemPackets.Full(45, current, Enumerable.Range(1, 51).ToArray()),
            "共通材料显示槽遵守本版list45的50格边界");
        var physical = new InventorySnapshot([
            new(82, new(InventoryListType.AccountCommon, 0), new(490013716, ItemKind.Material, 150) { EnchantCardId = 7, SortLockFlag = 1 }),
            new(83, new(InventoryListType.AccountCommon, 2), new(490013717, ItemKind.Material, 30))]);
        byte[] mergedFull = ActivityCalendarItemPackets.Full(45, current, ids, physical, 0);
        That(mergedFull.Length == 3 + 3 * 123 && BinaryPrimitives.ReadUInt16LittleEndian(mergedFull.AsSpan(1)) == 3 &&
            BinaryPrimitives.ReadUInt16LittleEndian(mergedFull.AsSpan(3)) == 0 &&
            BinaryPrimitives.ReadInt32LittleEndian(mergedFull.AsSpan(5)) == 490013716 &&
            BinaryPrimitives.ReadInt32LittleEndian(mergedFull.AsSpan(9)) == 150 &&
            BinaryPrimitives.ReadInt32LittleEndian(mergedFull.AsSpan(17)) == 7 && mergedFull[3 + 95] == 1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(mergedFull.AsSpan(3 + 123)) == 2 &&
            BinaryPrimitives.ReadInt32LittleEndian(mergedFull.AsSpan(5 + 123)) == 490013717 &&
            BinaryPrimitives.ReadUInt16LittleEndian(mergedFull.AsSpan(3 + 246)) == 3 &&
            BinaryPrimitives.ReadInt32LittleEndian(mergedFull.AsSpan(5 + 246)) == Coin &&
            BinaryPrimitives.ReadInt32LittleEndian(mergedFull.AsSpan(9 + 246)) == 12,
            "list45全量保留星河实体槽与完整属性，活动共通材料跳过已占用0/2槽落入空闲槽1/3");
        var mergedUpdates = ActivityCalendarItemPackets.Changes(45, before, current, ids, physical, physical).ToArray();
        That(mergedUpdates.Length == 2 && !mergedUpdates[0].Obtained && mergedUpdates[1].Obtained &&
            BinaryPrimitives.ReadUInt16LittleEndian(mergedUpdates[0].Body.AsSpan(3)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(mergedUpdates[0].Body.AsSpan(5)) == -1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(mergedUpdates[1].Body.AsSpan(3)) == 3 &&
            BinaryPrimitives.ReadInt32LittleEndian(mergedUpdates[1].Body.AsSpan(9)) == 12,
            "实体槽存在时活动材料增减只更新让位后的空闲槽1/3，不覆盖0/2星河实体材料");
        var shifted = ActivityCalendarItemPackets.Changes(45, current, current, ids, InventorySnapshot.Empty, physical).Single();
        That(!shifted.Obtained && shifted.Body.Length == 3 + 2 * 123 &&
            BinaryPrimitives.ReadUInt16LittleEndian(shifted.Body.AsSpan(3)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(shifted.Body.AsSpan(5)) == -1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(shifted.Body.AsSpan(3 + 123)) == 3 &&
            BinaryPrimitives.ReadInt32LittleEndian(shifted.Body.AsSpan(5 + 123)) == Coin &&
            BinaryPrimitives.ReadInt32LittleEndian(shifted.Body.AsSpan(9 + 123)) == 12,
            "新增45实体材料占用原虚拟槽时，1681静默清旧虚拟槽1并将余额迁往新空闲槽3");
        Reject<InvalidDataException>(() => ActivityCalendarItemPackets.Full(45, current, Enumerable.Range(1, 49).ToArray(), physical, 0),
            "list45实体槽与虚拟活动材料总数遵守50格上限");
        Console.WriteLine($"PASS activity calendar account materials {Count - start}");
    }

    public static void Run(TownCatalog towns, ItemResources resources)
    {
        AccountMaterialDeclarationTests.Run(resources, towns);
        var template = resources.Items.Get(Coin);
        That(template.AccountMaterial is { HoldingLimit: 4000, WeeklyAccountLimit: 4000, WeeklyCharacterLimit: 1300 },
            "不渝材料容器、持有和两级额度来自匹配资源");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-account-material-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("materials", () => { });
            var character = repository.Create(owner, new("MaterialOne", towns.Room, towns.InitialReturn)
                { Inventory = InventorySnapshot.Empty with { Items = [new(0, new(InventoryListType.Main, 121), new(Coin, ItemKind.Material, 4200))] } });
            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                var historical = new PreparedRaidReward(Guid.NewGuid(), 0, 1, 0, character.Id, "fixture/account-material", [new(Coin, 150)],
                    true, 20261027) { RaidType = RaidKind.Fiendwar };
                command.CommandText = """
                    INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type)
                    VALUES($id,'materials',$instance,0,1,0,20261027,1,$plan,'[]',$now,2)
                    """;
                command.Parameters.AddWithValue("$id", character.Id);
                command.Parameters.AddWithValue("$instance", historical.Instance.ToString("N"));
                command.Parameters.AddWithValue("$plan", JsonSerializer.Serialize(historical));
                command.Parameters.AddWithValue("$now", Now.ToUnixTimeSeconds());
                command.ExecuteNonQuery();
            }
            var legacy = AccountMaterialService.Legacy(character, resources.Items);
            character = repository.ReconcileAccountMaterials(owner, character, legacy, resources.Items.RaidAccountMaterialLimits, Now);
            That(character.Inventory.Items.IsEmpty && character.Inventory.AccountWallet.Material(Coin) is { Balance: 4200, RaidEarned: 150 },
                "旧实体超上限存量不截断，当前周历史真实领奖回填额度但不重复发余额");
            var unchanged = repository.ReconcileAccountMaterials(owner, character, AccountMaterialService.Legacy(character, resources.Items),
                resources.Items.RaidAccountMaterialLimits, Now);
            That(unchanged.Revision == character.Revision && unchanged.Inventory.AccountWallet.Same(character.Inventory.AccountWallet),
                "重复选角迁移不复制余额或重复重建周额度");
            var second = repository.Create(owner, new("MaterialTwo", towns.Room, towns.InitialReturn));
            That(second.Inventory.AccountWallet.Material(Coin).Balance == 4200, "另一角色读取同一账号余额");
            var spent = InventoryMaterialConsumptionService.Plan(character.Inventory, [new(Coin, 3000)], resources.EpicPieces, Now);
            character = InventoryService.Commit(repository, owner, character, spent);
            var grant = InventoryRewardGrantService.TryGrantBatch(character, [new(Coin, 150)], resources.Items, resources.GoldLimits,
                resources.EpicPieces, Now, raidReward: true);
            character = InventoryService.Commit(repository, owner, character, grant);
            That(character.Inventory.Items.IsEmpty && character.Inventory.AccountWallet.Material(Coin) is { Balance: 1350, RaidEarned: 300 },
                "真实发奖无需背包格，扣料不返周额度，获得计入原账号账本");
            bool staleRejected = false;
            try { InventoryService.Commit(repository, owner, second, InventoryMaterialConsumptionService.Plan(second.Inventory,
                [new(Coin, 1)], resources.EpicPieces, Now)); }
            catch (InvalidOperationException) { staleRejected = true; }
            That(staleRejected, "其他角色旧钱包快照不能覆盖新余额");
            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER reject_material_audit BEFORE INSERT ON account_material_audit BEGIN SELECT RAISE(ABORT,'fixture'); END";
                command.ExecuteNonQuery();
            }
            bool rolledBack = false;
            try { InventoryService.Commit(repository, owner, character, InventoryRewardGrantService.TryGrantBatch(character,
                [new(Coin, 100)], resources.Items, resources.GoldLimits, resources.EpicPieces, Now, raidReward: true)); }
            catch (SqliteException) { rolledBack = true; }
            var reloaded = repository.LoadCharacter(owner, character.Slot);
            That(rolledBack && reloaded.Revision == character.Revision &&
                reloaded.Inventory.AccountWallet.Same(character.Inventory.AccountWallet), "审计后段失败回滚材料、额度和两个revision");
            using var other = new AccountOwners().Acquire("other-materials", () => { });
            That(repository.LoadAccountWallet(other).Materials.IsEmpty, "账号材料不会串到其他账号");
            Console.WriteLine("PASS account material resources/persistence: 8 checks");
            RunBooster(towns, resources, repository, path);
            RunShop(towns, resources, repository, path);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    static void RunShop(TownCatalog towns, ItemResources resources, CharacterRepository repository, string path)
    {
        // 匹配资源的账号材料均未直接在售；隔离目录为已有商店商品声明账号容器，不修改共享PVF或目录。
        const int itemId = 3175;
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var template = resources.Items.Get(Coin) with { Id = itemId,
            ShopPrice = ItemShopPrice.Parse(new("[price]\n10"), false, 0) };
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate> { [itemId] = template });
        var shop = new InventoryShopService(catalog, resources.Shops, resources.GoldLimits, resources.EpicPieces);
        using var owner = new AccountOwners().Acquire("material-shop", () => { });
        int week = AccountMaterialService.RaidWeek(Now);
        var character = repository.Create(owner, new("MatShop", towns.Room, towns.InitialReturn)
        {
            Inventory = InventorySnapshot.Empty with { Gold = 1000,
                Items = [.. Enumerable.Range(121, 56).Select(slot => new InventoryItem(0, new(InventoryListType.Main, (short)slot),
                    InventoryRewardGrantService.Create(resources.Items.Get(3167))))] }
        });
        var prior = new AccountMaterialBalance(3997).EarnRaid(character.Id, week, 1300);
        character = repository.CommitInventory(owner, character, new(character.Inventory, character.Inventory with
            { AccountWallet = character.Inventory.AccountWallet.SetMaterial(itemId, prior) }, "material_shop_fixture", new(character)));
        var bought = shop.Buy(character, new(itemId, 3, 2, 1), out var receipt, Now);
        That(bought.Accepted && bought.Plan!.ChangedSlots.IsEmpty && bought.Plan.After.Gold == 970 &&
            bought.Plan.After.AccountWallet.Material(itemId).Same(prior with { Balance = 4000 }) &&
            receipt is { Position.Index: -1, Count: 3, Core.ItemId: itemId, Core.Count: 3 },
            "隔离账号材料直售满材料页仍足额到账，仅扣实际价款，不使用实体槽或攻坚获得额度");
        byte[] body = ShopPackets.Bought(bought, receipt);
        That(body.Length == 141 && body[0] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1)) == 970 &&
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(17)) == ushort.MaxValue &&
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(19)) == itemId &&
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(23)) == 3 && body[140] == 0,
            "账号材料购买ACK21完整123B行使用FFFF和本次三件数量，不把最终四千余额写成购入数量");
        using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_shop_material BEFORE INSERT ON account_material_audit BEGIN SELECT RAISE(ABORT,'fixture'); END";
            command.ExecuteNonQuery();
            bool failed = false;
            try { InventoryService.Commit(repository, owner, character, bought); }
            catch (SqliteException) { failed = true; }
            var reloaded = repository.LoadCharacter(owner, character.Slot);
            That(failed && reloaded.Revision == character.Revision && reloaded.Inventory.Gold == 1000 &&
                reloaded.Inventory.AccountWallet.Same(character.Inventory.AccountWallet) && reloaded.Inventory.Items.SequenceEqual(character.Inventory.Items),
                "购买账号材料后段审计失败，费用、余额、周额度、库存及版本同事务回滚");
            command.CommandText = "DROP TRIGGER reject_shop_material"; command.ExecuteNonQuery();
        }
        var committed = InventoryService.Commit(repository, owner, character, bought);
        That(committed.Inventory.AccountWallet.Material(itemId).Balance == 4000 && committed.Inventory.Gold == 970 &&
            committed.Inventory.Items.SequenceEqual(character.Inventory.Items) &&
            !shop.Buy(committed, new(itemId, 1, 2, 1), out _, Now).Accepted,
            "材料实际提交不创建额外UID；满持有额度拒绝下一笔并保留原资产");
        Console.WriteLine("PASS isolated account material shop: 4 checks; matching resources have no direct material SKU");
    }

    static void RunBooster(TownCatalog towns, ItemResources resources, CharacterRepository repository, string path)
    {
        using var owner = new AccountOwners().Acquire("material-boxes", () => { });
        var character = repository.Create(owner, new("MatBoxes", towns.Room, towns.InitialReturn)
        {
            Inventory = InventorySnapshot.Empty with { Items = [new(0, new(InventoryListType.Main, 65),
                InventoryRewardGrantService.Create(resources.Items.Get(10306817), 4, Now))] }
        });
        var clock = new LukeRaidClockTests.Clock(Now);
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, clock: clock);
        var opened = service.Open(character, new(65, 1), out var receipt);
        That(opened.Accepted && receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(Coin, 150) }),
            "真实150礼盒走普通160开盒规则，回执展示足额实际材料");
        using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            // 前段资源回归设置的真实审计故障保持生效，验证源盒与材料一起回滚。
            bool rolledBack = false;
            try { InventoryService.Commit(repository, owner, character, opened); }
            catch (SqliteException) { rolledBack = true; }
            var reloaded = repository.LoadCharacter(owner, character.Slot);
            That(rolledBack && reloaded.Revision == character.Revision &&
                reloaded.Inventory.AccountWallet.Same(character.Inventory.AccountWallet) &&
                reloaded.Inventory.Items.SequenceEqual(character.Inventory.Items), "开盒审计失败同时回滚源盒、余额、额度和revision");
            command.CommandText = "DROP TRIGGER reject_material_audit";
            command.ExecuteNonQuery();
        }
        character = InventoryService.Commit(repository, owner, character, opened);
        int week = AccountMaterialService.RaidWeek(Now);
        That(character.Inventory.AccountWallet.Material(Coin) is { Balance: 150, RaidEarned: 150 } &&
            character.Inventory.Find(new(InventoryListType.Main, 65))?.Core.Count == 3,
            "开盒提交同时扣一盒并累计同一账号和本角色额度");
        var near = WithMaterial(new(1150, week, 1150,
            ImmutableSortedDictionary<ushort, int>.Empty.Add(character.Id, 1150)));
        var exact = service.Open(near, new(65, 1), out _);
        That(exact.Accepted && exact.Plan!.After.AccountWallet.Material(Coin) is { Balance: 1300, RaidEarned: 1300 },
            "角色周额度恰好足额时允许完整兑换");
        That(!service.Open(near with { Inventory = exact.Plan!.After }, new(65, 1), out _).Accepted &&
            !service.Open(near, new(65, 2), out _).Accepted, "角色满额和批量超过余量均拒绝整笔，保留未兑换盒");
        That(!service.Open(WithMaterial(new(0, week, 3900)), new(65, 1), out _).Accepted &&
            !service.Open(WithMaterial(new(4000)), new(65, 1), out _).Accepted,
            "账号周余量和持有空间不足时不吞礼盒，不把原150显示为虚假到账");
        var reset = AccountMaterialService.WeekStart(week).AddDays(7);
        var resetClock = new LukeRaidClockTests.Clock(reset.AddSeconds(-1));
        var resetService = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, clock: resetClock);
        var stale = resetService.Open(character, new(65, 1), out _);
        resetClock.Advance(1);
        bool expired = false;
        try { InventoryService.Commit(repository, owner, character, stale); }
        catch (InventoryQualificationExpiredException) { expired = true; }
        That(expired && repository.LoadCharacter(owner, character.Slot).Inventory.AccountWallet.Same(character.Inventory.AccountWallet),
            "周四六点前规划不能在重置后以旧额度提交或扣盒");
        character = InventoryService.Commit(repository, owner, character, resetService.Open(character, new(65, 1), out _));
        That(character.Inventory.AccountWallet.Material(Coin) is { Balance: 300, RaidEarned: 150 } next && next.RaidWeek == week + 7,
            "重置后重新开盒只重置获得额度，保留既有余额并正常扣盒");
        Console.WriteLine("PASS account material booster: 9 checks");

        Character WithMaterial(AccountMaterialBalance state) => character with { Inventory = character.Inventory with
            { AccountWallet = character.Inventory.AccountWallet.SetMaterial(Coin, state) } };
    }
}
