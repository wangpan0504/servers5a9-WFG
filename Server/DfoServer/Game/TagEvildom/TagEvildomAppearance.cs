using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.TagEvildom;

internal readonly record struct TagEvildomAppearance(byte Stamina, bool NameTagActive, InventoryItem? NameTag, long Deadline, long WalletRevision)
{
    public static TagEvildomAppearance Capture(Character character, DateTimeOffset now)
    {
        var inventory = character.Inventory;
        var nameTag = inventory.Find(new(InventoryListType.Equipment, 30));
        long seconds = now.ToUnixTimeSeconds();
        // 绑定实际投影装备/Runes及NameTag的真正到期；相对秒倒计时不要求每秒重建。
        long deadline = inventory.Items.Where(item => TagEvildomInventory.Projected(item) ||
                item.Position.List == InventoryListType.Equipment && item.Position.Index == 30)
            .Select(item => (long)item.ExpireTime).Append(character.Learning.ExpertContractExpiresAt)
            .Where(value => value > seconds).DefaultIfEmpty(long.MaxValue).Min();
        return new(StaminaRecoveryService.Percent(inventory, now), nameTag is not null && !nameTag.IsExpired(seconds), nameTag,
            deadline, inventory.AccountWallet.Revision);
    }

    public bool Matches(Character character, DateTimeOffset now)
    {
        var inventory = character.Inventory;
        return now.ToUnixTimeSeconds() < Deadline && inventory.AccountWallet.Revision == WalletRevision &&
            Stamina == StaminaRecoveryService.Percent(inventory, now) &&
            NameTagActive == (NameTag is not null && !NameTag.IsExpired(now.ToUnixTimeSeconds()));
    }
}
