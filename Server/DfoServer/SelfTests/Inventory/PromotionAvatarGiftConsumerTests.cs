using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using DfoServer.Game.Inventory.Services;
using PvfLib;
using System.Text.Json;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class PromotionAvatarGiftConsumerTests
{
    const int Coupon = 880007001, OutputA = 880007002, OutputB = 880007003;
    const int EquipmentLeaf = 880007004, UnsupportedBonus = 880007005;
    public static void RunResources(ItemResources resources)
    {
        var shipped = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())
            ?? throw new InvalidDataException("shipped promotion missing");
        var original = shipped[0];
        int sequence = original.Rules.Tiers[0].Sequence;
        PromotionRules VoucherRules(int id) => original.Rules with
        {
            // 复用真实钱包商品隔离付费根，只校验本券的完整后继，不带入无关活动礼盒。
            Products = [new(100014, 14, 1)],
            Tiers = [new(sequence, id, 1)],
            OverflowItemId = 14
        };
        // 标准兑换券有完整可达消费者；高级永久券的四个无效装扮定义另行逐项保留拒绝。
        foreach (var (id, giftType, commodity) in new[] { (2681590, 3, 270623) })
        {
            var coupon = resources.Items.Get(id);
            var product = resources.CashShop.Product(commodity)!;
            var output = resources.Items.Get(product.ItemId);
            var avatar = resources.AvatarPackages.GetAvatar(product.ItemId);
            That(coupon.AvatarGiftType == giftType && !coupon.UseForbidden &&
                product.Section == "avatar" && product.AvatarType is >= 1 and <= 3 &&
                output.Kind == ItemKind.Avatar && output.Grade is >= 1 and <= 3 &&
                (output.Grade - 1) * 3 + product.AvatarType == giftType &&
                avatar.PendingRule is null && product.AvatarType < avatar.Types.Length &&
                !avatar.Abilities.IsEmpty,
                "真实券有生产TryAvatar相同gift档与期限的已支持兑换商品 " + id + "/" + commodity);
            var type = avatar.Types[product.AvatarType];
            That(type.Cera >= 0 && type.Gold >= 0 && type.WinPoints >= 0,
                "已证兑换商品的同一type行具有正式支持费用定义 " + commodity);
            var rules = VoucherRules(id);
            var catalog = ServerRuntime.LoadCashShopPromotions(resources,
                JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } }));
            var entry = catalog.Activities.Single();
            That(entry.Availability == PromotionAvailability.Active,
                "正式loader连接真实装扮兑换券及严格匹配的商品消费者 " + id + ": " + entry.Error);
        }
        VerifyDeluxeRemainsExplicitlyBlocked(resources, original, sequence);
        RunIsolated(resources);
    }

    static void VerifyDeluxeRemainsExplicitlyBlocked(ItemResources resources, PromotionConfiguration original, int sequence)
    {
        const int id = 2681593, giftType = 6;
        var coupon = resources.Items.Get(id);
        var matches = resources.CashShop.Products.Values
            .Where(product => product.Section == "avatar" && !product.IsMileage &&
                !resources.CashShop.IsCouponProduct(product.CommodityId))
            .Where(product => InventoryCashShopService.GiftType(resources.Items.Get(product.ItemId), product.AvatarType) == giftType)
            .ToArray();
        var expected = new[] { 2688214, 2688215, 2688216, 2688217 };
        That(coupon.AvatarGiftType == giftType && expected.All(id => matches.Any(product => product.ItemId == id)),
            "豪华装扮兑换券的四个名义匹配SKU全部保留，不能裁掉坏资源 " + id);
        var rules = original.Rules with
        {
            Products = [new(100014, 14, 1)],
            Tiers = [new(sequence, id, 1)],
            OverflowItemId = 14
        };
        var catalog = ServerRuntime.LoadCashShopPromotions(resources,
            JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } }));
        var entry = catalog.Activities.Single();
        That(entry.Availability == PromotionAvailability.Invalid &&
            expected.All(itemId => entry.Error?.Contains($"/{itemId}: avatar_gift_definition", StringComparison.Ordinal) == true),
            "豪华兑换券因四个无效装扮定义保持明确阻断，等待PVF修复后单独接入 " + id + ": " + entry.Error);
    }

    static void RunIsolated(ItemResources resources)
    {
        Fixture New() => new(resources);
        That(New().Validate() is null, "合法gift券完整连接两个匹配SKU而非首个商品");
        var noMatch = New();
        Reject(noMatch, noMatch.Shop(includeAvatars: false), "没有实际兑换商品不按gift标签放行");
        var wrongGift = New();
        wrongGift.Put(wrongGift.CouponTemplate with { AvatarGiftType = 0 });
        Reject(wrongGift, wrongGift.Shop(), "无可匹配gift档拒绝");
        var forbidden = New();
        forbidden.Put(forbidden.CouponTemplate with { UseForbidden = true });
        Reject(forbidden, forbidden.Shop(), "禁用兑换券仍不能取得消费者资格");
        var secondPending = New();
        secondPending.Avatars[OutputB] = new(() => secondPending.AvatarDefinition with { PendingRule = "fixture pending" });
        Reject(secondPending, secondPending.Shop(), "第二匹配商品typed装扮Pending不得被首个合法商品掩盖");
        var noType = New();
        noType.Avatars[OutputB] = new(() => noType.AvatarDefinition with { Types = [] });
        Reject(noType, noType.Shop(), "商城类型索引必须存在，不借礼盒type回退规则通过");
        var noAbility = New();
        noAbility.Avatars[OutputB] = new(() => noAbility.AvatarDefinition with { Abilities = [] });
        Reject(noAbility, noAbility.Shop(), "商城没有合法属性选项时不能证明可兑换");
        var invalidCost = New();
        invalidCost.Avatars[OutputB] = new(() => invalidCost.AvatarDefinition with
            { Types = invalidCost.AvatarDefinition.Types.SetItem(3,
                invalidCost.AvatarDefinition.Types[3] with { Cera = -1 }) });
        Reject(invalidCost, invalidCost.Shop(), "券支付仍要求该Types行费用定义合法，不能略过负成本");
        var bonus = New();
        Reject(bonus, bonus.Shop(badBonus: true), "兑换SKU独立bonus继续沿完整消费者图拒绝未知后继");
        var cycle = New();
        string? cycleError = cycle.Validate(cycle.Shop(returnCouponBonus: true));
        That(cycleError is not null && cycleError.Contains("free_reward_cycle", StringComparison.Ordinal),
            "兑换bonus返同一券须作为免费图环拒绝: " + cycleError);
        var hiddenSecond = New();
        That(hiddenSecond.Validate(hiddenSecond.Shop(hideSecond: true)) is null,
            "隐藏映射券商品不当作PaymentMode1后继，其他可兑换商品仍有效");
        var allHidden = New();
        Reject(allHidden, allHidden.Shop(hideAll: true), "只剩隐藏映射券商品时没有可达装扮兑换owner");
        var wrongSourceKind = New();
        wrongSourceKind.Put(wrongSourceKind.CouponTemplate with { Kind = ItemKind.Equipment, EquipmentSlot = 12 });
        Reject(wrongSourceKind, wrongSourceKind.Shop(), "非Consumable即使带gift标记也不能作为兑换券");
    }

    static void Reject(Fixture fixture, CashShopCatalog shop, string label)
    {
        string? error = fixture.Validate(shop);
        That(error is not null, label + ": " + error);
    }

    sealed class Fixture
    {
        readonly ItemResources resources;
        readonly ItemCatalog items;
        readonly BoosterCatalog boosters;
        readonly AvatarPackageCatalog avatarCatalog;
        readonly PromotionRules rules;
        public ItemTemplate CouponTemplate { get; }
        public AvatarPackageItemDefinition AvatarDefinition { get; }
        public ConcurrentDictionary<int, Lazy<AvatarPackageItemDefinition>> Avatars { get; }

        public Fixture(ItemResources resources)
        {
            this.resources = resources;
            var archive = Field<PvfArchive>(resources.Items, "pvf");
            items = new(archive, new Dictionary<int, string>(), new Dictionary<int, string>(), 100);
            CouponTemplate = resources.Items.Get(2681590) with { Id = Coupon };
            var realOutput = resources.Items.Get(106550541);
            AvatarDefinition = resources.AvatarPackages.GetAvatar(realOutput.Id);
            Put(CouponTemplate);
            Put(realOutput with { Id = OutputA });
            Put(realOutput with { Id = OutputB });
            Put(new(EquipmentLeaf, ItemKind.Equipment, 12, 1, 0, 1, 0, 0, 0,
                "[free]", ushort.MaxValue, 0, "合法装备控制叶"));
            Put(new(UnsupportedBonus, ItemKind.Consumable, 0, 1, 0, 1000, 0, 0, 0,
                "[free]", ushort.MaxValue, 0, "未知bonus", "[etc] 0"));
            boosters = new(archive, new Dictionary<int, string>());
            avatarCatalog = new(_ => throw new InvalidDataException("隔离兑换券不应读取装扮资源"),
                new Dictionary<int, string>(), new Dictionary<int, string>());
            Avatars = Field<ConcurrentDictionary<int, Lazy<AvatarPackageItemDefinition>>>(avatarCatalog, "avatars");
            Avatars[OutputA] = new(() => AvatarDefinition);
            Avatars[OutputB] = new(() => AvatarDefinition);
            rules = new(new("avatar-gift-fixture", "period", "v1", "default", "package"),
                10, 3, 31, EquipmentLeaf, 1, 5000, [new(1, EquipmentLeaf, 1)], [new(2, Coupon, 1)]);
        }

        public void Put(ItemTemplate template) => items.PrefillTemplates([KeyValuePair.Create(template.Id, template)]);
        public CashShopCatalog Shop(bool includeAvatars = true, bool badBonus = false, bool returnCouponBonus = false,
            bool hideSecond = false, bool hideAll = false) => CashShopCatalog.Parse(
            $"[item]\n1 {EquipmentLeaf} 1 0 0 100 0 0 0\n" +
            (includeAvatars ? $"[avatar]\n2 {OutputA} 3 0 0 -1\n3 {OutputB} 3 0 0 -1\n[/avatar]" : ""),
            badBonus ? $"[bonus item]\n3 {UnsupportedBonus} 1\n[/bonus item]" :
                returnCouponBonus ? $"[bonus item]\n2 {Coupon} 1\n[/bonus item]" : null,
            hideAll ? "[coupon]\n9 1 2\n9 4 3\n[/coupon]" :
                hideSecond ? "[coupon]\n9 4 3\n[/coupon]" : null);
        public string? Validate(CashShopCatalog? selectedShop = null) => new PromotionConsumerValidation(items, boosters,
            avatarCatalog, selectedShop ?? Shop(), resources.EpicPieces, resources.Contracts, resources.RandomOptions,
            resources.AttributePairs, resources.Creatures, resources.CollectBoxes, resources.LimitCubes,
            resources.Enchantments, resources.Enhancements, resources.RecoveryConsumables,
            resources.Amplifications, resources.AmplifyUpgrades).Validate(rules);
    }

    static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
