using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.ExpertJob;

internal sealed class ExpertJobDisassembly(InventoryDisjointService disjoint, ExpertJobScriptCatalog scripts,
    ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, ExpertJobCompatibility compatibility,
    TimeProvider? clock = null, Func<int, int>? random = null)
{
    readonly ExpertJobCompatibility rules = compatibility.Validate();
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly Func<int, int> next = random ?? Random.Shared.Next;

    public ExpertJobActionPlan? Plan(Character character, InventorySlot slot, bool ownMachine = true)
    {
        if (character.ExpertJobType != 3 || character.ExpertMachineGrade is < 1 or > 11 || character.ExpertMachineEndurance < rules.DisjointEnduranceCost ||
            character.Inventory.Gold < rules.DisjointGoldCost) return null;
        var source = character.Inventory.Find(slot);
        if (source is null || source.Uid <= 0 || !ItemSlotBoundService.Allows(character.Inventory, slot, source.Core.Kind)) return null;
        var now = time.GetUtcNow();
        var request = new DisjointRequest(slot, -1, 0);
        var validation = disjoint.Disjoint(character, request, out var baseReceipt, now);
        if (!validation.Accepted || baseReceipt is null) return null;
        try
        {
            var rewards = baseReceipt.Rewards.Select(row => new InventoryReward(row.ItemId,
                checked((int)Math.Max(1, (long)row.Count * rules.DisjointRewardPermille / 1000)))).ToList();
            if (ownMachine && next(100) < rules.SelfServiceRewardPercent)
            {
                if (!InventoryRewardGrantService.Create(items.Get(rules.SelfServiceRewardItem)).IsStackable) return null;
                rewards.Add(new(rules.SelfServiceRewardItem, rules.SelfServiceRewardCount));
            }
            var merged = rewards.GroupBy(row => row.ItemId).Select(group => new InventoryReward(group.Key,
                checked(group.Sum(row => row.Count)))).ToArray();
            // 235的已闭合条目只表示Main物品，图鉴碎片须有独立协议，不能静默提交不通知。
            if (merged.Any(row => pieces.ByPiece.ContainsKey(row.ItemId))) return null;
            var paid = InventoryDeleteService.Plan(character.Inventory, slot, source.Core.ItemId, 1).Plan!.After;
            paid = paid with { Gold = checked(paid.Gold - (uint)rules.DisjointGoldCost) };
            foreach (var reward in merged)
                if (CurrencyWallet.RewardKind(reward.ItemId) is { } kind && (long)CurrencyWallet.Get(paid, kind) + reward.Count > int.MaxValue)
                    return null;
            var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid }, merged, items, goldLimits, pieces, now);
            if (!grant.Accepted || grant.Plan is null || !grant.Plan.OverflowItems.IsEmpty) return null;
            var products = new List<DisjointReward>();
            foreach (var reward in merged)
            {
                if (CurrencyWallet.RewardKind(reward.ItemId) is { } kind)
                    products.Add(new(ItemSlotBoundService.CurrencySlot(kind) ?? throw new InvalidDataException("机器产物不是Main货币"), reward.ItemId, reward.Count));
                else foreach (var item in grant.Plan.After.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == reward.ItemId))
                {
                    int count = item.Core.Count - (paid.Find(item.Position)?.Core.Count ?? 0);
                    if (count > 0) products.Add(new(item.Position.Index, reward.ItemId, count));
                }
            }
            if (products.Count > byte.MaxValue || products.Any(row => row.Slot < 0)) return null;
            var gain = scripts.DisjointGain(3);
            int experience = checked(character.ExpertJobExperience + gain.Min + (gain.Max > gain.Min ? next(gain.Max - gain.Min + 1) : 0));
            var inventory = new InventoryPlan(character.Inventory, grant.Plan.After, "expert_job_machine_disjoint",
                new(character, source.ExpireTime > 0 ? source.ExpireTime : null, time));
            return new(character, inventory, experience, character.ExpertMachineEndurance - rules.DisjointEnduranceCost,
                Disjoint: new(request, products.ToArray()));
        }
        catch (OverflowException) { return null; }
        catch (InvalidDataException) { return null; }
    }
}
