using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class EnchantmentTests
{
    static void Spring2020KnownCardRarityQualificationPlans()
    {
        // 真实381卡完整文本由root取证；仅宝珠包装和称号宿主为合成夹具。
        // 原始字节SHA256： 1844C83934DFE0D97476894FC5E4908C9E1131B69A8042C1BDC18817D5C2B20B.
        // 本用例不涉及含normal的卡，也不推测其稀有度映射。
        const string card =
            "\n" +
            "[name]\n" +
            "`name_490702716` \n" +
            "[grade]\n" +
            "1 \n" +
            "[rarity]\n" +
            "3 \n" +
            "[usable job]\n" +
            "`[all]` \n" +
            "[/usable job]\n" +
            "\n" +
            "[attach type]\n" +
            "`[account]` \n" +
            "[minimum level]\n" +
            "1 \n" +
            "[weight]\n" +
            "1 \n" +
            "[icon]\n" +
            "`Item/stackable/cash_japan.img` 435 \n" +
            "[field image]\n" +
            "`Item/field_material.img` 30 \n" +
            "[stackable type]\n" +
            "`[material expert job]` 1 \n" +
            "[move wav]\n" +
            "`PAPER_TOUCH` \n" +
            "[string data]\n" +
            "`Interface/MonsterCard/jp/Monstercard_image_jp.img` `[title name]` \n" +
            "[/string data]\n" +
            "\n" +
            "[sub type]\n" +
            "1 \n" +
            "[int data]\n" +
            "15 56049 \n" +
            "[/int data]\n" +
            "\n" +
            "[item category]\n" +
            "`monster card` \n" +
            "[/item category]\n" +
            "\n" +
            "[enchant table]\n" +
            "\n" +
            "[enchant index]\n" +
            "0 \n" +
            "[all elemental attack]\n" +
            "15 15 \n" +
            "[equipment physical attack]\n" +
            "40 40 \n" +
            "[equipment magical attack]\n" +
            "40 40 \n" +
            "[separate attack]\n" +
            "40 40 \n" +
            "[all skill item container]\n" +
            "\n" +
            "[all skill item]\n" +
            "\n" +
            "[item growtype]\n" +
            "`[all]` 0 \n" +
            "[skill apply condition]\n" +
            "\n" +
            "[skill group]\n" +
            "`[active]` \n" +
            "[lower bound level]\n" +
            "1 \n" +
            "[upper bound level]\n" +
            "50 \n" +
            "[value]\n" +
            "1 \n" +
            "[extra condition]\n" +
            "`[except for ex skill]` \n" +
            "[/extra condition]\n" +
            "\n" +
            "[/skill apply condition]\n" +
            "\n" +
            "[skill phase]\n" +
            "`[all]` \n" +
            "[/all skill item]\n" +
            "\n" +
            "[/all skill item container]\n" +
            "\n" +
            "[/enchant index]\n" +
            "\n" +
            "[/enchant table]\n" +
            "\n" +
            "[unable use rarity]\n" +
            "`common` `uncommon` \n" +
            "[/unable use rarity]\n";
        const int beadId = 490705300, cardId = 490705381, gearId = 990103;
        const long now = 2_000_000_000;
        // 最小普通宝珠结构；300→381映射另由保存证据绑定。
        string bead = $"[stackable type]\n`[enchant waste]` 0\n[monster card id]\n{cardId}\n";
        EnchantmentDefinition Definition(string cardText) => new EnchantmentCatalog(
            path => path == "stackable/bead.stk" ? bead : cardText,
            new Dictionary<int, string> { [beadId] = "bead.stk", [cardId] = "cash/chn_490705381.stk" }).Get(beadId)!;
        var definition = Definition(card);
        That(definition is not null && definition.CardItemId == cardId && definition.Upgrade == 0 &&
            definition.Targets.SetEquals([(ItemKind.Equipment, (byte)13)]),
            "381完整真实卡定义保留称号13和索引0，不以拒绝整张卡规避目标资格测试");
        // 限制是已取证卡的最后一个根块；仅移除该完整块作为无字段旧卡对照。
        int restrictionAt = card.IndexOf("[unable use rarity]", StringComparison.Ordinal);
        var unrestricted = Definition(card[..restrictionAt]);
        var beadTemplate = new ItemTemplate(beadId, ItemKind.Consumable, 0, 1, 0, 1000, 1, 2, 1, "[free]", ushort.MaxValue, 0, "Bead");
        var titleTemplate = new ItemTemplate(gearId, ItemKind.Equipment, 13, 1, 45, 1, 20, 0, 1, "[free]", ushort.MaxValue, 0, "Title");
        var source = new InventoryItem(81, new(InventoryListType.Main, 65), new(beadId, ItemKind.Consumable, 2) { SortLockFlag = 1 });
        var target = new InventoryItem(82, new(InventoryListType.Main, 9), new(gearId, ItemKind.Equipment, 1234567)
        {
            Attr = 7, GenuineUpgrade = 4, AmplifyType = 2, AmplifyValue = 21, Rune = 8,
            Durability = 0, SealFlag = 1, EnchantCardId = 3175, EnchantUpgrade = 2, SortLockFlag = 1, Random0 = new(1, 2, 3)
        });
        Character role = new(1, 0, "KnownCardRarity", 0, new(0, 0, 0, 0), null)
        { Level = 15, Inventory = new([source, target], Gold: 3456) { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 123) } };
        var request = new EnchantmentRequest(source.Position, target.Position, uint.MaxValue);
        foreach (int rarity in new[] { 0, 1, 2, 3, 4, 5, 6 })
        {
            var title = titleTemplate with { Rarity = rarity };
            var result = InventoryEnchantmentService.Plan(role, request, now, definition!, beadTemplate, title);
            if (rarity is 0 or 1)
                That(!result.Accepted && result.Plan is null && role.Inventory.Find(source.Position) == source &&
                    role.Inventory.Find(target.Position) == target,
                    $"真实381 unable use rarity拒绝稀有度{rarity}称号，不生成扣源或改目标的计划");
            else
                Accepted(result, rarity, "381未禁稀有度");
            Accepted(InventoryEnchantmentService.Plan(role, request, now, unrestricted, beadTemplate, title), rarity,
                "无unable use rarity字段的旧卡");
        }

        void Accepted(InventoryResult result, int rarity, string label)
        {
            var after = result.Plan?.After;
            That(result.Accepted && after is not null &&
                after.Find(source.Position) == source with { Core = source.Core with { Value = 1 } } &&
                after.Find(target.Position) == target with { Core = target.Core with { EnchantCardId = cardId, EnchantUpgrade = 0 } } &&
                after.Gold == role.Inventory.Gold && after.AccountWallet.Same(role.Inventory.AccountWallet) &&
                role.Inventory.Find(source.Position) == source && role.Inventory.Find(target.Position) == target,
                $"{label} {rarity}继续合法，只扣一件并改Card/Upgrade，保留UID/完整Core和原快照");
        }
    }
}
