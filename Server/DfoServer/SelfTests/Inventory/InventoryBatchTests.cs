using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using static DfoServer.Game.Inventory.Services.InventoryRewardGrantService;

namespace DfoServer.Testing.Inventory;

internal static class InventoryBatchTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static void Compare(InventoryResult actual, InventoryResult expected, string reason)
    {
        TestCheck.That(actual.Accepted == expected.Accepted && actual.Error == expected.Error, reason + " 成功/错误码");
        if (actual.Accepted)
            TestCheck.That(actual.Plan!.After.Items.SequenceEqual(expected.Plan!.After.Items) &&
                actual.Plan.After.Gold == expected.Plan.After.Gold && actual.Plan.After.ReviveCoins == expected.Plan.After.ReviveCoins &&
                actual.Plan.After.WinPoints == expected.Plan.After.WinPoints &&
                actual.Plan.After.AccountWallet.Same(expected.Plan.After.AccountWallet) &&
                actual.Plan.After.EpicPieces.Same(expected.Plan.After.EpicPieces), reason + " 全量资产与顺序");
    }

    public static void RunCore()
    {
        int start = TestCheck.Count;
        var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
        var random = new Random(902);
        for (int sample = 0; sample < 400; sample++)
        {
            var items = new List<InventoryItem>();
            for (short slot = 3; slot < 9; slot++)
                items.Add(new(slot, new(InventoryListType.Main, slot), Core(slot)));
            for (short slot = 121; slot < 150; slot++)
                items.Add(new(slot, new(InventoryListType.Main, slot), Core(slot)));
            for (short slot = 9; slot < 15; slot++)
                items.Add(new(slot, new(InventoryListType.Main, slot), new(60000, ItemKind.Equipment, random.Next(2))
                    { EquipmentLockId = (byte)random.Next(2), SortLockFlag = 1 }));
            var before = new InventorySnapshot(items.OrderBy(_ => random.Next()).ToImmutableArray(), Gold: 100)
            {
                AccountWallet = AccountWallet.Empty.Set(CurrencyKind.WhiteCube, 90),
                EpicPieces = EpicPieceBook.Empty.Set(pieces, 0, 50)
            };
            before.Validate();
            string original = JsonSerializer.Serialize(before);
            int[] ids = [50000, 50001, 50002, 50003, 60000, 70001, 3034];
            var costs = Enumerable.Range(0, random.Next(0, 12)).Select(_ => new ItemMaterialCost(ids[random.Next(ids.Length)], random.Next(1, 20))).ToArray();
            int multiplier = random.Next(1, 4);
            Compare(InventoryMaterialConsumptionService.Plan(before, costs, pieces, Now, multiplier),
                ReferenceMaterial(before, costs, pieces, Now, multiplier), "随机材料 " + sample);
            TestCheck.That(JsonSerializer.Serialize(before) == original, "材料规划不修改输入快照与实例");

            ItemCore Core(short slot) => new(50000 + random.Next(4), ItemKind.Material, random.Next(1, 20))
            {
                EquipmentLockId = (byte)(random.Next(4) == 0 ? 1 : 0), SortLockFlag = (byte)random.Next(2),
                ExpireTime = random.Next(4) == 0 ? 1 : 0, EnchantCardId = slot
            };
        }
        var one = new InventorySnapshot([new(1, new(InventoryListType.Main, 121), new(50000, ItemKind.Material, 20))]);
        foreach (var costs in new ItemMaterialCost[][] { [new(0, 1)], [new(50000, 0)], [new(50000, -1)], [new(50000, int.MaxValue), new(50000, 1)], [new(50000, 5), new(50000, 5)] })
            foreach (int multiplier in new[] { -1, 0, 1, 2, int.MaxValue })
                Compare(InventoryMaterialConsumptionService.Plan(one, costs, pieces, Now, multiplier), ReferenceMaterial(one, costs, pieces, Now, multiplier), "材料输入与乘法边界");
        Console.WriteLine($"PASS {TestCheck.Count - start} 材料批量独立算法对照断言");
    }

    public static void Run(ItemResources resources)
    {
        int start = TestCheck.Count;
        var random = new Random(901);
        var catalog = resources.Items;
        var consumable = Create(catalog.Get(1106), utcNow: Now);
        var material = Create(catalog.Get(3167), utcNow: Now);
        for (int sample = 0; sample < 240; sample++)
        {
            var items = new List<InventoryItem>();
            for (short slot = 3; slot < 9; slot++) Add(slot, consumable);
            for (short slot = 65; slot < 115; slot++) Add(slot, consumable);
            for (short slot = 121; slot < 165; slot++) Add(slot, material);
            var before = new InventorySnapshot(items.OrderBy(_ => random.Next()).ToImmutableArray());
            before.Validate();
            var character = new Character(1, 0, "Batch", 0, new(1, 0, 10, 10), null) { Inventory = before };
            int[] ids = [1106, 3167, 10400, 3034];
            var rewards = Enumerable.Range(0, random.Next(1, 25)).Select(_ => new InventoryReward(ids[random.Next(ids.Length)], random.Next(1, 60))).ToArray();
            Compare(TryGrantBatch(character, rewards, catalog, resources.GoldLimits, resources.EpicPieces, Now),
                ReferenceGrant(character, rewards, catalog, resources.GoldLimits, resources.EpicPieces, Now), "随机奖励 " + sample);
            void Add(short slot, ItemCore core)
            {
                if (random.Next(4) == 0) return;
                items.Add(new(slot, new(InventoryListType.Main, slot), core with
                {
                    Value = random.Next(1, 30), ItemId = random.Next(4) == 0 ? 900000 + slot : core.ItemId,
                    EquipmentLockId = (byte)(random.Next(8) == 0 ? 1 : 0), SortLockFlag = (byte)random.Next(2),
                    ExpireTime = random.Next(4) == 0 ? 1 : core.ExpireTime, TradeRestriction = (byte)(random.Next(3) == 0 ? 1 : 0)
                }));
            }
        }
        var empty = new Character(1, 0, "Empty", 0, new(1, 0, 10, 10), null);
        foreach (var rewards in new InventoryReward[][] { [new(1106, 1), new(3167, 1), new(1106, 1)],
            [new(10400, 56)], [new(10400, 57)], [new(1106, int.MaxValue)], [new(1106, -1)],
            [new(1106, 1, 1)], [new(10400, 1, 32)], [new(3167, 1), new(10400, 57)] })
            Compare(TryGrantBatch(empty, rewards, catalog, resources.GoldLimits, resources.EpicPieces, Now),
                ReferenceGrant(empty, rewards, catalog, resources.GoldLimits, resources.EpicPieces, Now), "奖励空页、重复项与边界");
        Console.WriteLine($"PASS {TestCheck.Count - start} 奖励批量独立算法对照断言");
    }

    // 固定的逐项扫描参考算法；与优化实现分开，检查完整堆叠、空槽和原子拒绝语义。
    static InventoryResult ReferenceGrant(Character character, IReadOnlyList<InventoryReward> rewards, ItemCatalog catalog, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, DateTimeOffset? utcNow = null)
    {
        var inventory = character.Inventory;
        var wallet = inventory;
        var currencies = new CurrencyService(goldLimits);
        var planned = inventory.Items.ToDictionary(item => item.Position);
        var now = utcNow ?? DateTimeOffset.UtcNow;
        foreach (var reward in rewards)
        {
            if (reward.Count <= 0 || reward.Upgrade > 31 || reward.Refinement > 127)
                return InventoryResult.Reject();
            if (pieces.ByPiece.TryGetValue(reward.ItemId, out var piece))
            {
                if (reward.Upgrade != 0 || reward.Refinement != 0)
                    return InventoryResult.Reject();
                wallet.EpicPieces.Validate(pieces);
                long balance = (long)wallet.EpicPieces.Get(piece.Index) + reward.Count;
                if (balance > int.MaxValue)
                    return InventoryResult.Reject(17);
                wallet = wallet with { EpicPieces = wallet.EpicPieces.Set(pieces, piece.Index, (int)balance) };
                continue;
            }
            if (CurrencyWallet.RewardKind(reward.ItemId) is { } currency)
            {
                if (reward.Upgrade != 0 || reward.Refinement != 0)
                    return InventoryResult.Reject();
                wallet = currencies.Grant(character with { Inventory = wallet }, currency, reward.Count).Plan!.After;
                continue;
            }
            var template = catalog.Get(reward.ItemId);
            if (template.Kind != ItemKind.Equipment && (reward.Upgrade != 0 || reward.Refinement != 0))
                return InventoryResult.Reject();
            int remaining = reward.Count;
            var prototype = Create(template, utcNow: now, upgrade: reward.Upgrade, refinement: reward.Refinement);
            if (prototype.IsExpired(now.ToUnixTimeSeconds()))
                return InventoryResult.Reject(19);
            var range = ItemSlotBoundService.Range(template.Kind, inventory.Expansion);
            var list = ItemSlotBoundService.List(template.Kind);
            if (prototype.IsStackable)
            {
                // 自动发放只向首个兼容堆叠完整入库，不能靠拆成新格绕过该次入库上限。
                if (remaining > template.StackLimit)
                    return InventoryResult.Reject(17);
                var item = planned.Values.Where(item => item.Position.List == list &&
                    InventoryStackRuleService.CanShareStack(prototype, item.Core)).MinBy(item => item.Position.Index);
                if (item is not null)
                {
                    if (!InventoryStackRuleService.CanMerge(prototype, item.Core, remaining, template.StackLimit))
                        return InventoryResult.Reject(17);
                    planned[item.Position] = item with { Core = item.Core with { Value = checked(item.Core.Value + remaining) } };
                    remaining = 0;
                }
            }

            for (short slot = range.Start; remaining > 0 && slot <= range.End; slot++)
            {
                var position = new InventorySlot(list, slot);
                if (planned.ContainsKey(position))
                    continue;
                int count = Math.Min(remaining, template.StackLimit);
                planned.Add(position, CreateItem(template, position, prototype.IsStackable ? prototype with { Value = count } : prototype));
                remaining -= count;
            }

            if (remaining != 0)
                return InventoryResult.Reject(17);
        }

        var after = wallet with { Items = planned.Values.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray() };
        after.Validate(allocating: true);
        return new(true, 0, new(inventory, after, "reward_grant"));
    }

    static InventoryResult ReferenceMaterial(InventorySnapshot before, IReadOnlyList<ItemMaterialCost> costs, EpicPieceCatalog pieces,
        DateTimeOffset utcNow, int multiplier = 1)
    {
        if (multiplier <= 0)
            return InventoryResult.Reject();
        var totals = new Dictionary<int, int>();
        foreach (var cost in costs)
        {
            long count = (long)cost.Count * multiplier + totals.GetValueOrDefault(cost.ItemId);
            if (cost.ItemId <= 0 || cost.Count <= 0 || count > int.MaxValue)
                return InventoryResult.Reject();
            totals[cost.ItemId] = (int)count;
        }

        var after = before;
        foreach (var (itemId, count) in totals)
        {
            if (pieces.ByPiece.TryGetValue(itemId, out var piece))
            {
                after.EpicPieces.Validate(pieces);
                int balance = after.EpicPieces.Get(piece.Index);
                if (balance < count)
                    return InventoryResult.Reject();
                after = after with { EpicPieces = after.EpicPieces.Set(pieces, piece.Index, balance - count) };
            }
            else if (CurrencyWallet.RewardKind(itemId) is { } currency)
            {
                var spent = CurrencyService.Spend(after, currency, count);
                if (!spent.Accepted)
                    return InventoryResult.Reject();
                after = spent.Plan!.After;
            }
            else
            {
                int remaining = count;
                foreach (var item in after.Items.Where(item => item.Position.List == InventoryListType.Main &&
                    item.Core.ItemId == itemId && item.Core.EquipmentLockId == 0 && !item.Core.IsExpired(utcNow.ToUnixTimeSeconds()))
                    .OrderBy(item => item.Position.Index))
                {
                    int consumed = Math.Min(remaining, item.Core.Count);
                    if (consumed == 0)
                        break;
                    var deletion = ReferenceDelete(after, item.Position, item.Core.Value, consumed);
                    if (!deletion.Accepted)
                        return InventoryResult.Reject();
                    after = deletion.Plan!.After;
                    remaining -= consumed;
                }
                if (remaining != 0)
                    return InventoryResult.Reject();
            }
        }
        return new(true, 0, new(before, after, "material_consume"));
    }
    static InventoryResult ReferenceDelete(InventorySnapshot before, InventorySlot position, int value, int count)
    {
        var item = before.Find(position);
        // 时装的客户端 value 是递减的剩余秒数，不是服务端详情 UID，不能用它校验实例身份。
        if (!Allows(position.List) || item is null || value < 0 || item.Avatar is null && item.Core.Value != value ||
            item.Core.EquipmentLockId != 0)
            return InventoryResult.Reject();
        count = InventoryStackRuleService.NormalizeMoveCount(item.Core, count);
        if (count <= 0)
            return InventoryResult.Reject();
        var after = before with
        {
            Items = count == item.Core.Count ? before.Items.Remove(item) :
                before.Items.Replace(item, item with { Core = item.Core with { Value = item.Core.Value - count } })
        };
        return new(true, 0, new(before, after, "delete_item"));
    }
    static bool Allows(InventoryListType list) => list is InventoryListType.Main or InventoryListType.Avatar or
        InventoryListType.Pet or InventoryListType.GuildMedal or InventoryListType.Equipment or InventoryListType.PersonalCargo;
}
