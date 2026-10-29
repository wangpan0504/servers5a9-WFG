using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopTests
{
    internal static void VerifyMappedCoupons(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var now = DateTimeOffset.UtcNow;
        const int couponId = 490703365;
        var template = resources.Items.Get(couponId);
        var source = new InventoryItem(77, new(InventoryListType.Main, 65),
            InventoryRewardGrantService.Create(template, 2, now) with { ExpireTime = checked((int)now.AddDays(1).ToUnixTimeSeconds()) });
        var role = new Character(1, 0, "MappedCoupon", 0, towns.Room, towns.InitialReturn)
        {
            Job = 0, Level = 95,
            Inventory = new([source]) { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 100_000) }
        };
        var service = new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits,
            resources.EpicPieces, resources.Cargo, contracts: resources.Contracts);
        // 首个失败必须是合法券购买被拒绝，不能用新增API导致的编译失败冒充RED。
        var first = service.Buy(role, CouponRequest(108628), out var receipt, now);
        That(first.Accepted && receipt is { Products: [var product] } && product.CommodityId == 108628 && product.CeraPrice == 19900 &&
            first.Plan!.After.AccountWallet.Get(CurrencyKind.Cera) == 80100 && first.Plan.After.Find(source.Position) is { Uid: 77, Core.Count: 1 } &&
            first.Plan.After.Items.Single(item => item.Core.ItemId == 490701918).Core.Count == 1,
            "合法神选券按映射商品19900原价购买并在同一计划只扣一张券");
        That(first.Plan!.CharacterPrecondition!.QualificationExpiresAt == source.ExpireTime &&
            !first.Plan.CharacterPrecondition.IsExpired(source.ExpireTime - 1L) && first.Plan.CharacterPrecondition.IsExpired(source.ExpireTime),
            "券本身的真实期限进入提交资格，边界相等即失效");
        for (int index = 0; index < 14; index++)
        {
            var result = service.Buy(role, CouponRequest(108628 + index), out var row, now);
            That(result.Accepted && row!.Products.Single() is { CeraPrice: 19900, Count: 1 } &&
                result.Plan!.After.Items.Single(item => item.Core.ItemId == 490701918 - index).Core.Count == 1 &&
                result.Plan.After.AccountWallet.Get(CurrencyKind.Cera) == 80100,
                "table54全部14项保留配置价与准确商品映射 " + index);
            Refused(role, new(0, [new(108628 + index, 0)]), "隐藏mapped商品不能无券直购");
            Refused(role, CouponRequest(105276 + index), "原商品不能附券绕过实际mapped商品校验");
        }
        var twice = service.Buy(role, new(0, [CouponLine(108628), CouponLine(108629)]), out _, now);
        That(twice.Accepted && twice.Plan!.After.Items.All(item => item.Core.ItemId != source.Core.ItemId) && twice.Plan.After.AccountWallet.Get(CurrencyKind.Cera) == 60200 &&
            twice.Plan.After.Items.Count(item => item.Core.ItemId is 490701918 or 490701917) == 2,
            "同槽两张实体券逐行扣二，不把堆叠券误作一张复用");
        var one = role with { Inventory = role.Inventory with { Items = [source with { Core = source.Core with { Value = 1 } }] } };
        Refused(one, new(0, [CouponLine(108628), CouponLine(108629)]), "同槽只有一张券的双行重用整车拒绝");
        var noMoney = role with { Inventory = CurrencyWallet.Set(role.Inventory, CurrencyKind.Cera, 19899) };
        Refused(noMoney, CouponRequest(108628), "余额少一也不消耗券");
        foreach (var line in new[]
        {
            CouponLine(108628) with { RelatedSlot = 66 }, CouponLine(108628) with { RelatedSlot = ushort.MaxValue },
            CouponLine(108628) with { RelatedItemId = 490703366 }, CouponLine(108628) with { RelatedItemId = 0 },
            CouponLine(108628) with { RelatedItemId = uint.MaxValue },
            CouponLine(108628) with { FirstOptionCount = 1 }, CouponLine(108628) with { SecondOptionCount = 1 },
            CouponLine(108628) with { PaymentMode = 1 }, CouponLine(105276), CouponLine(105531), CouponLine(1)
        }) Refused(role, new(line.PaymentMode, [line]), "错slot/key/扩展/支付模式或非mapped商品不能套券");
        var nonCoupon = source with { Core = InventoryRewardGrantService.Create(resources.Items.Get(1108), 2, now) };
        Refused(role with { Inventory = role.Inventory with { Items = [nonCoupon] } },
            new(0, [CouponLine(108628) with { RelatedItemId = 1108 }]), "实际槽物品ID匹配但不是coupon声明也不能套券");
        foreach (var bad in new[]
        {
            source with { Position = new(InventoryListType.PersonalCargo, 65) },
            source with { Core = source.Core with { EquipmentLockId = 1 } },
            source with { Core = source.Core with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) } },
            source with { Core = source.Core with { ExpireTime = checked((int)now.AddSeconds(-1).ToUnixTimeSeconds()) } }
        }) Refused(role with { Inventory = role.Inventory with { Items = [bad] } }, CouponRequest(108628),
            "商城忽略历史绝对日期也不能绕过券实例的容器/锁定/到期");
        var expired = role with { Inventory = role.Inventory with { Items = [source with { Core = source.Core with { ExpireTime = 1 } }] } };
        foreach (bool ignore in new[] { false, true })
        {
            service.IgnoreAbsoluteExpiration = ignore;
            Refused(expired, CouponRequest(108628), "两种商城期限开关状态都拒绝过期券");
        }
        service.IgnoreAbsoluteExpiration = true;
        // 保留商城既有满页邮件溢出合同，不能为券购买新增少发或满页拒绝。
        var targetTemplate = resources.Items.Get(490701918);
        var range = ItemSlotBoundService.Range(targetTemplate.Kind, role.Inventory);
        var fillers = Enumerable.Range(range.Start, range.End - range.Start + 1)
            .Select(index => new InventoryItem(1000 + index, new(InventoryListType.Main, (short)index),
                InventoryRewardGrantService.Create(targetTemplate, targetTemplate.StackLimit, now, shopExpiration: true))).ToImmutableArray();
        var fullItems = fillers.Where(item => item.Position != source.Position).Append(source).ToImmutableArray();
        var full = service.Buy(role with { Inventory = role.Inventory with { Items = fullItems } }, CouponRequest(108628), out _, now);
        That(full.Accepted && full.Plan!.After.Find(source.Position) is { Core.Count: 1 } &&
            full.Plan.OverflowItems.Sum(item => item.Core.Count) == 1 && full.Plan.After.AccountWallet.Get(CurrencyKind.Cera) == 80100,
            "满目标页按既有商城合同完整邮件溢出且只扣一张券和19900");
        CouponPersistence(towns, resources, source with { Uid = 0, Core = source.Core with { Value = 1 } }, service, now);
        Console.WriteLine($"PASS {Count - checks} mapped coupon purchase assertions");

        void Refused(Character character, CashShopPurchaseRequest request, string message)
        {
            string before = JsonSerializer.Serialize(character);
            That(service.Buy(character, request, out var rejected, now) is { Accepted: false, Plan: null } && rejected is null &&
                JsonSerializer.Serialize(character) == before, message);
        }
    }

    static CashShopPurchaseItem CouponLine(int commodity) => new(commodity, 0, RelatedItemId: 490703365, RelatedSlot: 65);
    static CashShopPurchaseRequest CouponRequest(int commodity) => new(0, [CouponLine(commodity)]);

    static void CouponPersistence(TownCatalog towns, ItemResources resources, InventoryItem source,
        InventoryCashShopService service, DateTimeOffset now)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-mapped-coupon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool complete = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("mapped-coupon", () => { });
            var saved = repo.Create(owner, new("CouponSql", towns.Room, towns.InitialReturn)
                { Job = 0, Level = 95, Inventory = new([source]) });
            saved = InventoryService.Commit(repo, owner, saved,
                new CurrencyService(resources.GoldLimits).Grant(saved, CurrencyKind.Cera, 100_000));
            string before = Snapshot();
            var purchase = service.Buy(saved, CouponRequest(108628), out _, now);
            That(purchase.Accepted, "持久源券可规划购买");
            var plan = purchase.Plan!;
            var reward = plan.After.Items.Single(item => item.Core.ItemId == 490701918);
            Sql("CREATE TRIGGER fail_coupon_reward BEFORE INSERT ON character_inventory_slots WHEN NEW.character_id=" + saved.Id +
                " AND NEW.list_type=" + (int)reward.Position.List + " AND NEW.slot_index=" + reward.Position.Index +
                " BEGIN SELECT RAISE(ABORT,'mapped coupon rollback'); END");
            bool failed = false;
            try { repo.CommitInventory(owner, saved, plan); } catch (SqliteException) { failed = true; }
            That(failed && Snapshot() == before, "奖励槽晚SQL失败回滚源删除、物品、点券、审计、revision和UID序列");
            Sql("DROP TRIGGER fail_coupon_reward");
            var committed = repo.CommitInventory(owner, saved, plan);
            var recovered = new CharacterRepository(path).List(owner).Single();
            That(recovered.Revision == saved.Revision + 1 && recovered.Inventory.Find(source.Position)?.Core.ItemId != source.Core.ItemId &&
                recovered.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 80100 &&
                recovered.Inventory.Items.Single(item => item.Core.ItemId == 490701918) is { Uid: > 0, Core.Count: 1 } &&
                JsonSerializer.Serialize(committed) == JsonSerializer.Serialize(recovered), "移除故障后同一冻结计划成功且重开保存完整结果");
            before = Snapshot();
            That(service.Buy(recovered, CouponRequest(108628), out var replay, now) is { Accepted: false, Plan: null } && replay is null &&
                Snapshot() == before, "耗尽券重放不扣款或再发奖");
            bool stale = false;
            try { repo.CommitInventory(owner, saved, plan); } catch (InvalidOperationException) { stale = true; }
            That(stale && Snapshot() == before, "旧revision冻结计划不能再次提交");
            complete = true;

            string Snapshot() => JsonSerializer.Serialize(new CharacterRepository(path).List(owner)) + ":" +
                Rows("SELECT * FROM character_inventory_items ORDER BY item_uid") + ":" +
                Rows("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index") + ":" +
                Rows("SELECT * FROM account_wallets ORDER BY account") + ":" +
                Rows("SELECT * FROM inventory_audit_log ORDER BY audit_id") + ":" +
                Rows("SELECT * FROM currency_audit_log ORDER BY audit_id") + ":" +
                Rows("SELECT * FROM sqlite_sequence ORDER BY name");
            string Rows(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql;
                using var reader = command.ExecuteReader(); var rows = new List<object[]>();
                while (reader.Read()) { var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values); }
                return JsonSerializer.Serialize(rows);
            }
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
        }
        finally
        {
            if (complete) Directory.Delete(root, true);
            else Console.Error.WriteLine("mapped coupon SQL failure fixture retained: " + root);
        }
    }
}
