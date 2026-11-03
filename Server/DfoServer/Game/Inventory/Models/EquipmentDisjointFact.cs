using System.Collections.Immutable;
using DfoServer.Game.Currency;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

// 一件实际装备被原业务消费的冻结事实；产物数量不扩增分解件数。
internal sealed record EquipmentDisjointFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock,
    DisjointMode Mode, string Reason, DisjointRequest Request, DisjointReceipt Receipt,
    ImmutableArray<DisjointReward> Rewards, InventoryItem Source, InventoryItem? Tool,
    uint GoldCost, InventorySnapshot Before, InventorySnapshot Paid, InventorySnapshot After)
{
    public static EquipmentDisjointFact Capture(InventorySnapshot before, InventorySnapshot paid, InventorySnapshot after,
        DisjointRequest request, DisjointReceipt receipt, DisjointMode mode, string reason, InventoryItem source,
        InventoryItem? tool, DateTimeOffset at, TimeProvider clock) =>
        new(Guid.NewGuid(), at, clock, mode, reason, request, receipt, receipt.Rewards.ToImmutableArray(), source, tool,
            checked(before.Gold - paid.Gold), before, paid, after);

    // 顾客原库存与产物不变，仅绑定原双账号交易新增的摊位费；不把店主记作分解者。
    public EquipmentDisjointFact ForTrade(InventoryPlan original, InventorySnapshot before, InventorySnapshot after, uint fee)
    {
        if (Mode != DisjointMode.OtherMachine || Reason != "expert_job_machine_disjoint" ||
            !ReferenceEquals(original.EquipmentDisjoint, this) || !ReferenceEquals(original.Before, Before) ||
            !ReferenceEquals(original.After, After) || !ReferenceEquals(before, Before) ||
            fee > After.Gold || fee > Paid.Gold || after != (After with { Gold = After.Gold - fee }))
            throw new InvalidDataException("分解交易来源与顾客原计划或摊位费不一致");
        return this with { Reason = "expert_job_trade_customer", GoldCost = checked(GoldCost + fee),
            Paid = Paid with { Gold = Paid.Gold - fee }, After = after };
    }

    public bool Matches(InventoryPlan plan, InventorySnapshot inventory)
    {
        bool reason = Reason switch
        {
            "equipment_disjoint" => Enum.IsDefined(Mode),
            "expert_job_machine_disjoint" => Mode is DisjointMode.OwnMachine or DisjointMode.OtherMachine,
            "expert_job_trade_customer" => Mode == DisjointMode.OtherMachine,
            _ => false
        };
        if (!reason || Id == Guid.Empty || plan.Reason != Reason ||
            !ReferenceEquals(plan.Before, Before) || !ReferenceEquals(plan.After, After) ||
            Receipt.Request != Request || Rewards.IsDefaultOrEmpty || !Receipt.Rewards.SequenceEqual(Rewards) ||
            Rewards.Any(row => row.ItemId <= 0 || row.Count <= 0) || Source.Uid <= 0 ||
            Source.Core.Kind != ItemKind.Equipment || Source.Core.Count != 1 ||
            Source.Position != Request.Position || Source.Position.List != InventoryListType.Main ||
            plan.BeforeIndex.Find(Source.Position) != Source || Before.Gold < GoldCost ||
            Paid.Gold != Before.Gold - GoldCost || After.Gold != Paid.Gold) return false;
        var removed = Before.Items.ToBuilder();
        if (!removed.Remove(Source)) return false;
        if (Tool is { } tool)
        {
            if (Mode != DisjointMode.Ordinary || Request.ToolSlot < 0 || tool.Uid <= 0 || tool.Uid == Source.Uid ||
                tool.Position != new InventorySlot(InventoryListType.Main, Request.ToolSlot) || !tool.Core.IsStackable ||
                tool.Core.Count < 1 || plan.BeforeIndex.Find(tool.Position) != tool) return false;
            int index = removed.IndexOf(tool); if (index < 0) return false;
            if (tool.Core.Count == 1) removed.RemoveAt(index);
            else removed[index] = tool with { Core = tool.Core with { Value = tool.Core.Value - 1 } };
        }
        else if (Request.ToolSlot != -1) return false;
        // 图鉴writer在同事务中恰递增一版；余额与目录顺序仍按原冻结结果逐项校验。
        var committedPieces = Before.EpicPieces.Same(After.EpicPieces) ? After.EpicPieces :
            After.EpicPieces with { Revision = checked(Before.EpicPieces.Revision + 1) };
        if (!Paid.Items.SequenceEqual(removed) || (Paid with { Items = Before.Items, Gold = Before.Gold }) != Before ||
            After.Items.Any(item => item.Uid == Source.Uid) || inventory.Items.Length != After.Items.Length ||
            !inventory.EpicPieces.Same(committedPieces) ||
            Enum.GetValues<CurrencyKind>().Any(kind => CurrencyWallet.Get(inventory, kind) != CurrencyWallet.Get(After, kind))) return false;
        var actual = new InventoryIndex(inventory);
        foreach (var expected in After.Items)
        {
            var current = actual.Find(expected.Position);
            if (current is null || current.Uid <= 0 || expected.Uid > 0 && current.Uid != expected.Uid ||
                expected.Uid == 0 && plan.BeforeIndex.FindUid(current.Uid) is not null ||
                (current with { Uid = expected.Uid }) != expected) return false;
        }
        return true;
    }
}
