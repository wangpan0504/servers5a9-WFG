using System.Collections.Immutable;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Inventory.Models;

internal enum EquipmentEnchantMode { BeadCard, BeadTitle, ExpertCard }
internal sealed record EnchantItemDebit(InventoryItem Item, int Count);
internal sealed record EnchantCurrencyDebit(CurrencyKind Kind, long Count);

// 原附魔成功事实；消耗和已抽定的完整目标核心在原事务中冻结。
internal sealed record EquipmentEnchantFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock,
    EquipmentEnchantMode Mode, string Reason, InventoryItem Source, InventoryItem Target, ItemCore TargetAfter,
    ImmutableArray<EnchantItemDebit> Items, ImmutableArray<EnchantCurrencyDebit> Currencies,
    InventorySnapshot Before, InventorySnapshot Paid, InventorySnapshot After)
{
    public static EquipmentEnchantFact Capture(InventorySnapshot before, InventorySnapshot paid, InventorySnapshot after,
        InventoryItem source, InventoryItem target, EquipmentEnchantMode mode, string reason, DateTimeOffset at, TimeProvider clock)
    {
        var paidIndex = new InventoryIndex(paid);
        var items = before.Items.Where(item => paidIndex.Find(item.Position) != item)
            .Select(item => new EnchantItemDebit(item, checked(item.Core.Count - (paidIndex.Find(item.Position)?.Core.Count ?? 0))))
            .ToImmutableArray();
        var currencies = Enum.GetValues<CurrencyKind>()
            .Where(kind => CurrencyWallet.Get(before, kind) != CurrencyWallet.Get(paid, kind))
            .Select(kind => new EnchantCurrencyDebit(kind, checked(CurrencyWallet.Get(before, kind) - CurrencyWallet.Get(paid, kind))))
            .ToImmutableArray();
        return new(Guid.NewGuid(), at, clock, mode, reason, source, target,
            after.Find(target.Position)?.Core ?? throw new InvalidDataException("附魔目标未保留"), items, currencies, before, paid, after);
    }

    public EquipmentEnchantFact ForTrade(InventoryPlan original, InventorySnapshot before, InventorySnapshot after, uint fee)
    {
        if (Mode != EquipmentEnchantMode.ExpertCard || Reason != "expert_job_card_enchant" ||
            !ReferenceEquals(original.EquipmentEnchant, this) || !ReferenceEquals(original.Before, Before) ||
            !ReferenceEquals(original.After, After) || !ReferenceEquals(before, Before) || fee > Paid.Gold ||
            fee > After.Gold || after != (After with { Gold = After.Gold - fee }))
            throw new InvalidDataException("附魔交易来源与顾客原计划或摊位费不一致");
        var currencies = Currencies;
        if (fee != 0)
        {
            var gold = currencies.SingleOrDefault(row => row.Kind == CurrencyKind.Gold);
            currencies = gold is null ? currencies.Add(new(CurrencyKind.Gold, fee)) :
                currencies.Replace(gold, gold with { Count = checked(gold.Count + fee) });
        }
        return this with { Reason = "expert_job_trade_customer", Currencies = currencies,
            Paid = Paid with { Gold = Paid.Gold - fee }, After = after };
    }

    public bool Matches(InventoryPlan plan, InventorySnapshot inventory)
    {
        bool reason = Reason switch
        {
            "enchant_by_bead" => Mode is EquipmentEnchantMode.BeadCard or EquipmentEnchantMode.BeadTitle,
            "expert_job_card_enchant" or "expert_job_trade_customer" => Mode == EquipmentEnchantMode.ExpertCard,
            _ => false
        };
        if (!reason || Id == Guid.Empty || plan.Reason != Reason || !ReferenceEquals(plan.Before, Before) ||
            !ReferenceEquals(plan.After, After) || Source.Uid <= 0 || Target.Uid <= 0 || Source.Uid == Target.Uid ||
            Source.Position == Target.Position || Source.Position.List != InventoryListType.Main ||
            Target.Position.List is not (InventoryListType.Main or InventoryListType.Equipment or InventoryListType.Pet) ||
            !Source.Core.IsStackable || Target.Core.IsStackable || plan.BeforeIndex.Find(Source.Position) != Source ||
            plan.BeforeIndex.Find(Target.Position) != Target || Items.IsDefaultOrEmpty || Currencies.IsDefault ||
            Items.Select(row => row.Item.Position).Distinct().Count() != Items.Length ||
            Currencies.Select(row => row.Kind).Distinct().Count() != Currencies.Length ||
            Items.SingleOrDefault(row => row.Item == Source)?.Count != 1 ||
            Mode == EquipmentEnchantMode.ExpertCard && TargetAfter.EnchantCardId != Source.Core.ItemId) return false;
        var expected = Before;
        foreach (var debit in Items)
        {
            if (debit.Count <= 0 || debit.Item.Uid <= 0 || !debit.Item.Core.IsStackable ||
                debit.Item.Position.List != InventoryListType.Main || plan.BeforeIndex.Find(debit.Item.Position) != debit.Item ||
                debit.Count > debit.Item.Core.Count ||
                !InventoryDeleteService.TryDelete(debit.Item, debit.Item.Core.ItemId, debit.Count, out var remaining)) return false;
            expected = expected with { Items = remaining is null ? expected.Items.Remove(debit.Item) : expected.Items.Replace(debit.Item, remaining) };
        }
        foreach (var debit in Currencies)
        {
            if (!Enum.IsDefined(debit.Kind) || debit.Count <= 0 || CurrencyWallet.Get(expected, debit.Kind) < debit.Count) return false;
            expected = CurrencyWallet.Set(expected, debit.Kind, CurrencyWallet.Get(expected, debit.Kind) - debit.Count);
        }
        if (!Paid.Items.SequenceEqual(expected.Items) || !Paid.AccountWallet.Same(expected.AccountWallet) ||
            Paid.Gold != expected.Gold || Paid.ReviveCoins != expected.ReviveCoins || Paid.WinPoints != expected.WinPoints ||
            (Paid with { Items = Before.Items, AccountWallet = Before.AccountWallet, Gold = Before.Gold,
                ReviveCoins = Before.ReviveCoins, WinPoints = Before.WinPoints }) != Before || Paid.Find(Target.Position) != Target)
            return false;
        var targetCore = Mode == EquipmentEnchantMode.BeadTitle ? Target.Core with { Extension97 = TargetAfter.Extension97 } :
            Target.Core with { EnchantCardId = TargetAfter.EnchantCardId, EnchantUpgrade = TargetAfter.EnchantUpgrade };
        if (TargetAfter != targetCore || Mode != EquipmentEnchantMode.BeadTitle && TargetAfter.EnchantCardId <= 0 ||
            !After.Items.SequenceEqual(Paid.Items.Replace(Target, Target with { Core = TargetAfter })) ||
            (After with { Items = Paid.Items }) != Paid || !inventory.Items.SequenceEqual(After.Items)) return false;
        var wallet = After.AccountWallet with { Revision = checked(After.AccountWallet.Revision +
            (Currencies.Any(row => CurrencyWallet.IsAccount(row.Kind)) ? 1 : 0)) };
        return inventory.AccountWallet.Same(wallet) &&
            (inventory with { Items = After.Items, AccountWallet = After.AccountWallet }) == After;
    }
}
