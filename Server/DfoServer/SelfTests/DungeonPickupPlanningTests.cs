using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonPickupPlanningTests
{
    public static void RunCore()
    {
        int before = Count;
        var limits = new GoldLimitCatalog("[gold limit from level]\n0 100 1 100\n[/gold limit from level]");
        var original = new Character(1, 0, "GroundGold", 7, new(1, 0, 10, 10), null)
        {
            Inventory = new([]) { Gold = 80, AccountWallet = AccountWallet.Empty }
        };
        var plan = DungeonPickupPlanning.Gold(original, new(null, 20), limits);
        That(plan.Accepted && plan.Plan!.After.Gold == 100 && plan.Plan.After.Items.IsEmpty &&
            plan.Plan.ChangedCurrencies.SequenceEqual(new[] { CurrencyKind.Gold }) &&
            plan.Plan.CharacterPrecondition?.Matches(original) == true && original.Inventory.Gold == 80,
            "金币拾取纯规划完整加到携带边界，保留原快照，不分配虚假物品UID");
        That(!DungeonPickupPlanning.Gold(original, new(null, 21), limits).Accepted &&
            !DungeonPickupPlanning.Gold(original with { Inventory = original.Inventory with { Gold = 100 } }, new(null, 1), limits).Accepted,
            "边界不足与已满不能部分消费冻结地面金币，拒绝保留原资产");
        var overflow = original with { Inventory = original.Inventory with { Gold = uint.MaxValue } };
        That(!DungeonPickupPlanning.Gold(overflow, new(null, int.MaxValue), limits).Accepted && overflow.Inventory.Gold == uint.MaxValue,
            "存量超携带值仍不能溢出或因拾取覆盖现有余额");
        Reject<InvalidDataException>(() => DungeonPickupPlanning.Gold(original,
            new(new ItemCore(1000, ItemKind.Consumable, 1)), limits), "普通物品不借金币分支改写资产owner");
        var pieces = new EpicPieceCatalog("[piece list]\n9000 10300000\n[/piece list]\n[equipment recipe info]\n[/equipment recipe info]");
        var fragment = new ItemTemplate(10300000, ItemKind.Material, 0, 1, 0, 1000, 0, 4, 1, "[trade]", ushort.MaxValue, 0, "碎片");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var full = original with { Inventory = new(Enumerable.Range(121, 56).Select(slot =>
            new InventoryItem(slot, new(InventoryListType.Main, (short)slot), new(2, ItemKind.Material, 1))).ToImmutableArray()) };
        var core = new ItemCore(fragment.Id, fragment.Kind, 3);
        var granted = DungeonPickupPlanning.Item(full, new(core), fragment, limits, pieces, now);
        That(granted.Accepted && granted.Plan!.After.EpicPieces.Get(0) == 3 && granted.Plan.ChangedSlots.IsEmpty &&
            granted.Plan.ChangedCurrencies.IsEmpty && granted.Plan.ChangedEpicPieces.SequenceEqual(new[] { 0 }) &&
            granted.Plan.After.Items.SequenceEqual(full.Inventory.Items) && granted.Plan.CharacterPrecondition?.Matches(full) == true,
            "满材料页的地面碎片只规划账号图鉴增量，不创建UID或占用真实槽位");
        var capped = full with { Inventory = full.Inventory with { EpicPieces = EpicPieceBook.Empty.Set(pieces, 0, int.MaxValue - 2) } };
        That(!DungeonPickupPlanning.Item(capped, new(core), fragment, limits, pieces, now).Accepted &&
            capped.Inventory.EpicPieces.Get(0) == int.MaxValue - 2,
            "碎片余额不足以接收整件时拒绝，不截断地面数量或修改原图鉴");
        foreach (var invalid in new[] { core with { ExpireTime = (int)now.ToUnixTimeSeconds() },
            core with { Value = 1001 }, core with { ItemId = 2 }, core with { Attr = 1 } })
            That(!DungeonPickupPlanning.Item(full, new(invalid), fragment, limits, pieces, now).Accepted,
                "过期、超堆叠、错模板或携带装备属性的碎片拒绝，不把特殊核心折算成数量");
        Console.WriteLine($"PASS {Count - before} 地面金币纯规划与携带上限断言");
    }

    public static void RunResources(ItemCatalog items, GoldLimitCatalog limits, EpicPieceCatalog pieces)
    {
        int before = Count;
        var now = DateTimeOffset.UtcNow;
        var character = new Character(1, 0, "GroundItems", 0, new(1, 0, 10, 10), null);
        var physical = items.Get(1004);
        var originalCore = InventoryRewardGrantService.Create(physical, 7, now);
        var granted = DungeonPickupPlanning.Item(character, new(originalCore), physical, limits, pieces, now);
        That(granted.Accepted && granted.Plan!.After.Items.Single().Core == originalCore &&
            granted.Plan.After.Items.Single() is { Uid: 0, Position.Index: 65 } && character.Inventory.Items.IsEmpty,
            "真实消耗品拾取纯规划保留冻结七件数量和完整Core，UID只在提交时产生");
        var nextCharacter = character with { Inventory = granted.Plan!.After };
        var secondTemplate = items.Get(1000);
        var secondCore = InventoryRewardGrantService.Create(secondTemplate, 2, now);
        var second = DungeonPickupPlanning.Item(nextCharacter, new(secondCore), secondTemplate, limits, pieces, now);
        var repeated = DungeonPickupPlanning.Item(character with { Inventory = second.Plan!.After },
            new(originalCore), physical, limits, pieces, now);
        That(second.Accepted && second.Plan.After.Find(new(InventoryListType.Main, 66))!.Core == secondCore &&
            repeated.Accepted && repeated.Plan!.After.Find(new(InventoryListType.Main, 65))!.Core == (originalCore with { Value = 14 }) &&
            repeated.Plan.After.Items.Length == 2 && repeated.Plan.After.Find(new(InventoryListType.Main, 66))!.Core == secondCore,
            "集合逐件拾取使用更新快照，不同消耗品依次占背包分页，后续同模板合并原堆");
        var cube = items.Get(3037);
        var cubeCore = InventoryRewardGrantService.Create(cube, 7, now);
        var wallet = DungeonPickupPlanning.Item(character, new(cubeCore), cube, limits, pieces, now);
        That(wallet.Accepted && wallet.Plan!.After.AccountWallet.Get(CurrencyKind.ClearCube) == 7 &&
            wallet.Plan.After.Items.IsEmpty && wallet.Plan.ChangedSlots.IsEmpty &&
            wallet.Plan.ChangedCurrencies.SequenceEqual(new[] { CurrencyKind.ClearCube }),
            "匹配晶体资源归账号余额，地面物品来源不造成第二份背包实体");
        var full = character with { Inventory = character.Inventory with
            { AccountWallet = character.Inventory.AccountWallet.Set(CurrencyKind.ClearCube, int.MaxValue - 6) } };
        That(!DungeonPickupPlanning.Item(full, new(cubeCore), cube, limits, pieces, now).Accepted &&
            full.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == int.MaxValue - 6,
            "账号材料余额不足以完整接收时拒绝，原冻结数量与账号余额不被部分消费");
        That(!DungeonPickupPlanning.Item(character, new(originalCore), cube, limits, pieces, now).Accepted,
            "不匹配模板不能把普通物品来源变成账号材料");
        Console.WriteLine($"PASS {Count - before} 实体与账号材料拾取规划资源断言");
    }
}
