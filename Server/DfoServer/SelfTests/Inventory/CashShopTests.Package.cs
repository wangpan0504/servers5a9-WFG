using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopTests
{
    // 两种真实购买帧分别覆盖只有属性、属性加分组选物，避免仅验证背包里的518开包。
    const string AvatarPackageFrame = "0000010000FF00D20F0000087FF428000100F528000187F528000100F628000188F62800020BF72800018AF72800010DF828000000000000000000";
    const string TigerPackageFrame = "0000010000FFFF4D1400000962F4280000E4F428000066F5280000E4F52800006AF6280001ECF62800006EF7280000F0F72800005CF828000002CEEA280000000100CEEA280001000100000000000000";

    static void PackagePurchaseCore()
    {
        var plain = Convert.FromHexString(AvatarPackageFrame);
        var tiger = Convert.FromHexString(TigerPackageFrame);
        var first = CashShopRequests.Buy(plain).Items.Single();
        var second = CashShopRequests.Buy(tiger).Items.Single();
        That(plain.Length == 59 && first is { CommodityId: 4050, FirstOptionCount: 8, SecondOptionCount: 0 } &&
            first.AvatarChoices[0] == new CeraPackageChoice(2684031, 1) && first.AvatarChoices[^1].ItemId == 2684941,
            "真实59B购买读取八件时装属性，不再按19B固定长度拒绝");
        That(tiger.Length == 80 && second is { CommodityId: 5197, FirstOptionCount: 9, SecondOptionCount: 2 } &&
            second.PackageChoices.SequenceEqual([new CashShopPackageChoice(2681550, 0, 1), new(2681550, 1, 1)]) &&
            second.RelatedItemId == 0 && second.RelatedSlot == 0,
            "真实80B购买保留源礼包、两组索引和组内选择，不把尾段误读为关联券");
        var cart = new byte[] { 2, 0, 2, 0 }.Concat(plain[4..]).Concat(tiger[4..]).ToArray();
        var parsed = CashShopRequests.Buy(cart);
        That(parsed.CurrencyMode == 2 && parsed.Items.Count == 2 && parsed.Items[0].AvatarChoices.Count == 8 &&
            parsed.Items[1].CommodityId == 5197 && parsed.Items[1].PackageChoices.Count == 2,
            "多件变长购物车逐行推进游标，第二商品不落进第一件属性段");
        foreach (var frame in new[] { plain, tiger, cart })
        {
            for (int length = 0; length < frame.Length; length++)
            {
                var truncated = frame[..length];
                Reject<InvalidDataException>(() => CashShopRequests.Buy(truncated), "选择购买的每种截断均拒绝");
            }
            Reject<InvalidDataException>(() => CashShopRequests.Buy([.. frame, 0]), "变长购买同样拒绝尾垃圾");
        }
        var duplicateAvatar = (byte[])plain.Clone();
        plain.AsSpan(12, 4).CopyTo(duplicateAvatar.AsSpan(17));
        Reject<InvalidDataException>(() => CashShopRequests.Buy(duplicateAvatar), "重复时装编号不覆盖前一属性");
        var duplicateGroup = (byte[])tiger.Clone();
        duplicateGroup[70] = 0;
        Reject<InvalidDataException>(() => CashShopRequests.Buy(duplicateGroup), "同源同组重复选择拒绝");
    }

    static void VerifyPackagePurchases(TownCatalog towns, ItemResources resources)
    {
        var now = DateTimeOffset.UtcNow;
        var character = new Character(1, 0, "PackageShop", 0, towns.Room, towns.InitialReturn)
        {
            Job = 0, Level = 95,
            Inventory = InventorySnapshot.Empty with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 100_000) }
        };
        var service = new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits,
            resources.EpicPieces, resources.Cargo, contracts: resources.Contracts, avatars: resources.AvatarPackages,
            packages: resources.Boosters) { IgnoreAbsoluteExpiration = true };
        foreach (string frame in new[] { AvatarPackageFrame, TigerPackageFrame })
        {
            var request = CashShopRequests.Buy(Convert.FromHexString(frame));
            var selected = request.Items.Single();
            var product = resources.CashShop.Product(selected.CommodityId)!;
            var definition = resources.Boosters.Get(product.ItemId);
            var result = service.Buy(character, request, out var receipt, now);
            That(result.Accepted && receipt?.Products.Single().CommodityId == product.CommodityId,
                "实际目录的多装扮礼包购买成功 " + product.CommodityId + ": " + result.RejectReason);
            var after = result.Plan!.After;
            var delivered = after.Items.Concat(result.Plan.OverflowItems).ToArray();
            That(after.AccountWallet.Get(CurrencyKind.Cera) == 100_000 - product.CeraPrice &&
                delivered.All(item => item.Core.ItemId != product.ItemId), "只扣一次整包价格，不再同时发可重复开的源礼包");
            foreach (var choice in selected.AvatarChoices)
                That(delivered.Count(item => item.Core.ItemId == choice.ItemId && item.Avatar is not null &&
                    item.Core.Durability == choice.Option) == definition.PackageRewards.Where(row => row.ItemId == choice.ItemId).Sum(row => row.Count) * product.Count,
                    "每件时装完整保留所选属性与独立详情 " + choice.ItemId);
            foreach (var choice in selected.PackageChoices)
            {
                var reward = definition.PackageSelections[choice.GroupIndex].Rewards[choice.SelectionIndex];
                That(delivered.Where(item => item.Core.ItemId == reward.ItemId).Sum(item => item.Core.Count) == reward.Count * product.Count,
                    "选物按资源组内索引足量发放 " + reward.ItemId);
            }
            foreach (var reward in definition.PackageRewards)
            {
                if (CurrencyWallet.RewardKind(reward.ItemId) is { } kind)
                    That(CurrencyWallet.Get(after, kind) == CurrencyWallet.Get(character.Inventory, kind) + reward.Count * product.Count,
                        "包内钱包奖励与扣费同计划 " + reward.ItemId);
                else if (resources.Contracts.TryAcquisitionContract(reward.ItemId, out var contract))
                    That(after.AccountWallet.ContractExpiry(contract.Type) == now.ToUnixTimeSeconds() + contract.DurationSeconds * reward.Count * product.Count,
                        "包内契约按实际类型和期限立即到账 " + reward.ItemId);
                else if (resources.Items.Get(reward.ItemId).Kind != ItemKind.Avatar)
                    That(delivered.Where(item => item.Core.ItemId == reward.ItemId).Sum(item => item.Core.Count) == reward.Count * product.Count,
                        "固定非装扮奖励不漏发 " + reward.ItemId);
            }
            var choices = selected.AvatarChoices.ToArray();
            choices[0] = choices[0] with { Option = byte.MaxValue };
            var badRows = new List<CashShopPurchaseItem>
            {
                selected with { AvatarChoices = choices },
                selected with { AvatarChoices = selected.AvatarChoices.Skip(1).ToArray(), FirstOptionCount = (byte)(selected.FirstOptionCount - 1) }
            };
            if (selected.PackageChoices.Count != 0)
            {
                var original = selected.PackageChoices.ToArray();
                foreach (var invalid in new[] { original[0] with { PackageItemId = product.ItemId + 1 },
                    original[0] with { GroupIndex = ushort.MaxValue }, original[0] with { SelectionIndex = ushort.MaxValue } })
                    badRows.Add(selected with { PackageChoices = new[] { invalid }.Concat(original.Skip(1)).ToArray() });
            }
            foreach (var invalid in badRows)
                That(service.Buy(character, request with { Items = [selected, invalid] }, out var rejected, now) is
                    { Accepted: false, Plan: null } && rejected is null && character.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 100_000 && character.Inventory.Items.IsEmpty,
                    "混车后项非法属性、缺项或选物越界整单拒绝，不扣前项费用或部分发奖");

            var template = resources.Items.Get(selected.AvatarChoices[0].ItemId);
            var full = Enumerable.Range(0, ItemSlotBoundService.AvatarCapacity).Select(slot =>
                InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Avatar, (short)slot),
                    InventoryRewardGrantService.Create(template, utcNow: now) with { Value = 1000 + slot }) with { Uid = 1000 + slot }).ToImmutableArray();
            var overflow = service.Buy(character with { Inventory = character.Inventory with { Items = full } }, request, out _, now);
            That(overflow.Accepted && overflow.Plan!.OverflowItems.Count(item => item.Avatar is not null) == selected.AvatarChoices.Count &&
                selected.AvatarChoices.All(choice => overflow.Plan.OverflowItems.Any(item => item.Core.ItemId == choice.ItemId && item.Core.Durability == choice.Option)),
                "整套时装满页时完整进入溢出邮件计划，保留各自属性");
        }
        var sourceRow = CashShopRequests.Buy(Convert.FromHexString(AvatarPackageFrame)).Items.Single();
        foreach (string section in new[] { "package", "regular package", "community package" })
        {
            var shop = CashShopCatalog.Parse($"[{section}]\n700501 2682173 2 0 13 0 0 0 0 0 0\n[/{section}]");
            var other = new InventoryCashShopService(resources.Items, shop, resources.GoldLimits, resources.EpicPieces,
                resources.Cargo, contracts: resources.Contracts, avatars: resources.AvatarPackages, packages: resources.Boosters)
                { IgnoreAbsoluteExpiration = true };
            var multiple = new CashShopPurchaseRequest(0, [sourceRow with { CommodityId = 700501 }]);
            var result = other.Buy(character, multiple, out _, now);
            That(result.Accepted && result.Plan!.After.Items.Count(item => item.Avatar is not null) == 16 &&
                result.Plan.After.AccountWallet.Get(CurrencyKind.Cera) == 99_987 && result.Plan.After.ReviveCoins == 40,
                "三类礼包共用展开路径，商品数量倍率作用于全套奖励而非费用 " + section);
        }
        var source = resources.Items.Get(2682173);
        try
        {
            service.IgnoreAbsoluteExpiration = false;
            resources.Items.PrefillTemplates([KeyValuePair.Create(source.Id, source with { Expiration = new(AbsoluteTime: now.AddSeconds(-1).ToUnixTimeSeconds()) })]);
            var request = new CashShopPurchaseRequest(0, [sourceRow]);
            That(service.Buy(character, request, out _, now) is { Accepted: false, Plan: null, RejectReason: "cash_shop_package_expired" },
                "当场展开不能绕过源礼包模板截止");
            long expires = now.AddSeconds(1).ToUnixTimeSeconds();
            resources.Items.PrefillTemplates([KeyValuePair.Create(source.Id, source with { Expiration = new(AbsoluteTime: expires) })]);
            var finite = service.Buy(character, request, out _, now);
            That(finite.Accepted && finite.Plan!.CharacterPrecondition!.Matches(character, now.ToUnixTimeSeconds()) &&
                !finite.Plan.CharacterPrecondition.Matches(character, expires), "源礼包截止仍参与整笔购买提交前复验");
        }
        finally { resources.Items.PrefillTemplates([KeyValuePair.Create(source.Id, source)]); }
    }
}
