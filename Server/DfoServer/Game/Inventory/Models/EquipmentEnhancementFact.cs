using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

internal enum OrdinaryEnhancementSource : byte { Material, Portable }

// 由普通升级完成抽签和完整库存规划后生成；重试保留同一事实，不从请求或reason推断进度。
internal sealed record EquipmentEnhancementFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock,
    OrdinaryEnhancementSource SourceKind, InventoryItem? Source, int Consumed, InventoryItem Target,
    EnhancementReceipt Result)
{
    public CurrencyKind? WalletMaterial { get; init; }

    public bool Matches(InventoryPlan plan, InventorySnapshot committed)
    {
        var result = Result;
        if (Id == Guid.Empty || !Enum.IsDefined(SourceKind) || Consumed <= 0 ||
            Target.Uid <= 0 || Target.Core.Kind != ItemKind.Equipment ||
            Target.Position.List is not (InventoryListType.Main or InventoryListType.Equipment) ||
            plan.Before.Find(Target.Position) != Target ||
            result.Mode is not (EnhancementMode.Reinforce or EnhancementMode.Amplify) ||
            (result.Mode == EnhancementMode.Reinforce ? Target.Core.AmplifyType != 0 : Target.Core.AmplifyType is < 1 or > 4) ||
            !result.InPlace || result.Path != 0 || result.Target != Target.Position ||
            result.OldLevel != Target.Core.Upgrade ||
            result.GoldCost < 0 || result.OutcomeCode is not (0 or 1 or 2 or 3) ||
            result.Succeeded != (result.OutcomeCode == 0) || result.NewLevel > 31)
            return false;
        bool outcome = result.OutcomeCode switch
        {
            0 => result.OldLevel < 31 && result.NewLevel == result.OldLevel + 1,
            1 => result.NewLevel == result.OldLevel,
            2 => result.NewLevel <= result.OldLevel,
            3 => result.NewLevel == 0,
            _ => false
        };
        if (!outcome) return false;
        if (WalletMaterial is { } currency)
        {
            // 钱包材料没有实体UID；只认可原虚拟槽的精确扣料，销毁返还仍沿原回执核对。
            if (Source is not null || SourceKind != OrdinaryEnhancementSource.Material ||
                currency is < CurrencyKind.BlackCube or > CurrencyKind.EpicSoul ||
                ItemSlotBoundService.CurrencySlot(currency) != result.MaterialSlot ||
                CurrencyWallet.Get(plan.Before, currency) - Consumed != result.MaterialRemaining || result.MaterialRemaining < 0)
                return false;
            long materialRefund = result.Refunds.Where(row => CurrencyWallet.RewardKind(row.ItemId) == currency)
                .Sum(row => (long)row.Count);
            if (CurrencyWallet.Get(committed, currency) != (long)result.MaterialRemaining + materialRefund) return false;
        }
        else
        {
            if (Source is not { } original || original.Uid <= 0 || original.Uid == Target.Uid ||
                original.Position.List != InventoryListType.Main || !original.Core.IsStackable || original.Core.Count < Consumed ||
                plan.Before.Find(original.Position) != original || result.MaterialSlot != original.Position.Index ||
                result.MaterialRemaining != original.Core.Count - Consumed) return false;
            var source = committed.Items.SingleOrDefault(item => item.Uid == original.Uid);
            if (result.MaterialRemaining == 0 ? source is not null :
                source != (original with { Core = original.Core with { Value = result.MaterialRemaining } })) return false;
        }
        var target = committed.Items.SingleOrDefault(item => item.Uid == Target.Uid);
        if (result.OutcomeCode == 3 ? target is not null : target != (Target with
            { Core = Target.Core with { Attr = (byte)((Target.Core.Attr & 0xE0) | result.NewLevel) } })) return false;
        // 销毁补偿可含金币，不能将合法返还误当作未扣费；补偿仍由原升级规划负责。
        long refund = result.Refunds.Where(row => CurrencyWallet.RewardKind(row.ItemId) == CurrencyKind.Gold)
            .Sum(row => (long)row.Count);
        return (long)committed.Gold == (long)plan.Before.Gold - result.GoldCost + refund;
    }
}
