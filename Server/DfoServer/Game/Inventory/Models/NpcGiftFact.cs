using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

// 原NPC赠礼的一次实际消费与已抽定好感变化，不包含染色操作。
internal sealed record NpcGiftFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock,
    NpcFavorRequest Request, NpcFavorGift Gift, int OwnerId, int Maximum, byte DailyLimit,
    InventoryItem? Source, CurrencyKind? Currency, NpcFavorPoint? Previous, NpcFavorPoint Updated,
    InventorySnapshot Before, InventorySnapshot Paid, InventorySnapshot After)
{
    public bool Matches(InventoryPlan plan, InventorySnapshot inventory)
    {
        if (Id == Guid.Empty || plan.Reason != "npc_favor_gift" || !ReferenceEquals(plan.Before, Before) ||
            !ReferenceEquals(plan.After, After) || OwnerId <= 0 || Maximum <= 0 || DailyLimit == 0 ||
            Gift.NpcId != -1 && Gift.NpcId != OwnerId || Gift.ItemId <= 0 || Gift.Count <= 0 ||
            Gift.Minimum <= 0 || Gift.Maximum < Gift.Minimum || Request.NpcId <= 0 || Request.Operation is not (0 or 2) ||
            Before.NpcFavor.SingleOrDefault(row => row.NpcId == OwnerId) != Previous ||
            Previous is { } previous && (previous.Points >= Maximum || previous.GiftDay > ShopPurchaseCount.Today(PlannedAt) ||
                previous.EffectiveGifts(ShopPurchaseCount.Today(PlannedAt)) >= DailyLimit)) return false;
        int oldPoints = Previous?.Points ?? 0;
        int minimum = Math.Min(Maximum - oldPoints, Gift.Minimum), maximum = Math.Min(Maximum - oldPoints, Gift.Maximum);
        int gain = Updated.Points - oldPoints, day = ShopPurchaseCount.Today(PlannedAt);
        if (minimum <= 0 || gain < minimum || gain > maximum || Updated.NpcId != OwnerId ||
            Updated.GiftDay != day || Updated.GiftCount != (Previous?.EffectiveGifts(day) ?? 0) + 1 ||
            Updated.GiftCount > DailyLimit || Updated.Colors != (Previous?.Colors ?? 0)) return false;
        if (Request.Operation == 0)
        {
            int[] cubes = [3033, 3034, 3035, 3036, 3037, 3262];
            if (Request.SourceType != 36 || Request.SourceIndex is < 354 or > 359 || Source is not null ||
                Currency is not { } kind || CurrencyWallet.RewardKind(Gift.ItemId) != kind ||
                Gift.ItemId != cubes[Request.SourceIndex - 354] || !CurrencyWallet.IsAccount(kind) ||
                CurrencyWallet.Get(Before, kind) < Gift.Count ||
                !Paid.AccountWallet.Same(Before.AccountWallet.Set(kind, checked((int)CurrencyWallet.Get(Before, kind) - Gift.Count))) ||
                (Paid with { AccountWallet = Before.AccountWallet }) != Before) return false;
        }
        else
        {
            if (Request.SourceType != 0 || Request.SourceIndex > short.MaxValue || Currency is not null ||
                Source is not { Uid: > 0, Core.Kind: ItemKind.Consumable or ItemKind.Material or ItemKind.Quest or ItemKind.Profession } ||
                Source.Position != new InventorySlot(InventoryListType.Main, (short)Request.SourceIndex) ||
                Source.Core.ItemId != Gift.ItemId || Source.Core.Count < Gift.Count || Source.Core.EquipmentLockId != 0 ||
                Source.IsExpired(PlannedAt.ToUnixTimeSeconds()) || plan.BeforeIndex.Find(Source.Position) != Source ||
                !InventoryDeleteService.TryDelete(Source, Gift.ItemId, Gift.Count, out var remaining)) return false;
            var expectedItems = remaining is null ? Before.Items.Remove(Source) : Before.Items.Replace(Source, remaining);
            if (!Paid.Items.SequenceEqual(expectedItems) || (Paid with { Items = Before.Items }) != Before) return false;
        }
        var rows = Previous is null ? Paid.NpcFavor.Add(Updated) : Paid.NpcFavor.Replace(Previous, Updated);
        if (!After.NpcFavor.SequenceEqual(rows.OrderBy(row => row.NpcId)) ||
            (After with { NpcFavor = Paid.NpcFavor }) != Paid || !inventory.Items.SequenceEqual(After.Items)) return false;
        var wallet = After.AccountWallet with { Revision = checked(After.AccountWallet.Revision + (Currency is null ? 0 : 1)) };
        return inventory.AccountWallet.Same(wallet) &&
            (inventory with { Items = After.Items, AccountWallet = After.AccountWallet }) == After;
    }
}
