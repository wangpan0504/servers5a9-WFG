using System.Collections.Immutable;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Inventory.Services;

// 580..587仅为服务端账本键；原生概览为58，八项实际期限通过CMD905单独投影。
internal static class DevilContractPolicy
{
    public static bool IsService(int type) => type is >= 580 and <= 587;
    public static InventoryResult ReportDungeonEffect(DfoServer.Game.Characters.Character character, byte slot,
        Guid instanceId, DateTimeOffset now)
    {
        var before = character.Inventory;
        long expiry = slot is 3 or 4 ? Expiry(before.AccountWallet, slot) : 0;
        if (instanceId == Guid.Empty || expiry <= now.ToUnixTimeSeconds())
            return InventoryResult.Reject(19);
        // 同一副本重传、过图及跨06:00均不重复消费；新副本才允许再次触发。
        var prior = before.DevilUses.FirstOrDefault(row => row.Slot == slot);
        if (prior?.InstanceId == instanceId)
            return new(true, 0, new(before, before, "devil_effect_duplicate", new(character, expiry)));
        byte used = Used(before, slot, now);
        if (slot == 3 && used >= 10) return InventoryResult.Reject(19);
        var after = before with { DevilUses = before.DevilUses.Where(row => row.Slot != slot)
            .Append(new DevilServiceUsage(slot, ShopPurchaseCount.Today(now), slot == 3 ? (byte)(used + 1) : (byte)0, instanceId))
            .OrderBy(row => row.Slot).ToImmutableArray() };
        return new(true, 0, new(before, after, "devil_dungeon_effect", new(character, expiry)));
    }
    public static byte Used(InventorySnapshot inventory, byte slot, DateTimeOffset now) =>
        inventory.DevilUses.FirstOrDefault(row => row.Slot == slot && row.Day == ShopPurchaseCount.Today(now))?.Used ?? 0;
    public static byte LotteryAllowance(InventorySnapshot inventory, DateTimeOffset now) =>
        (byte)Math.Max(8, (int)(inventory.DevilUses.FirstOrDefault(row => row.Slot == 1 && row.Day == ShopPurchaseCount.Today(now))?.Allowance ?? 0));
    public static InventorySnapshot RenewLottery(InventorySnapshot inventory, DateTimeOffset now, bool activeBefore)
    {
        int day = ShopPurchaseCount.Today(now);
        var prior = inventory.DevilUses.FirstOrDefault(row => row.Slot == 1 && row.Day == day);
        byte allowance = (byte)Math.Min(30, activeBefore ? LotteryAllowance(inventory, now) + 8 : 8);
        return inventory with { DevilUses = inventory.DevilUses.Where(row => row.Slot != 1)
            .Append(new DevilServiceUsage(1, day, prior?.Used ?? 0, Allowance: allowance)).OrderBy(row => row.Slot).ToImmutableArray() };
    }
    // 原生客户端阈值固定8；续购得到的剩余额度按本窗口投影，真实累计仍在服务端保存。
    public static byte ClientUsed(DevilServiceUsage? row) => row?.Slot == 1
        ? (byte)Math.Clamp(8 - (Math.Max(8, (int)row.Allowance) - row.Used), 0, 8) : row?.Used ?? 0;
    public static bool TryConsume(InventorySnapshot inventory, byte slot, byte limit, DateTimeOffset now, out InventorySnapshot after)
    {
        after = inventory;
        byte used = Used(inventory, slot, now);
        if (Expiry(inventory.AccountWallet, slot) <= now.ToUnixTimeSeconds() || used >= limit) return false;
        after = inventory with { DevilUses = inventory.DevilUses.Where(row => row.Slot != slot)
            .Append(new DevilServiceUsage(slot, ShopPurchaseCount.Today(now), checked((byte)(used + 1)),
                Allowance: slot == 1 ? LotteryAllowance(inventory, now) : (byte)0)).OrderBy(row => row.Slot).ToImmutableArray() };
        return true;
    }
    public static long Expiry(AccountWallet wallet, int slot)
    {
        if (slot is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(slot));
        // 旧GM会员58只提供免费黄金牌，不能扩大成八项全开。
        return slot == 0 ? Math.Max(wallet.ContractExpiry(58), wallet.ContractExpiry(580)) : wallet.ContractExpiry(580 + slot);
    }
    public static ImmutableSortedDictionary<int, long> ClientContracts(AccountWallet wallet)
    {
        var result = wallet.PremiumContracts.Where(pair => !IsService(pair.Key)).ToImmutableSortedDictionary();
        long overview = Enumerable.Range(0, 8).Max(slot => Expiry(wallet, slot));
        return overview > 0 ? result.SetItem(58, overview) : result;
    }
}
