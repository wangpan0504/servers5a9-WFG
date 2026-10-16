using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class StaminaRecoveryService(DevilContractCatalog catalog)
{
    // 本版PVF声明恢复价格与免费次数，未声明自然恢复周期；采用参考逻辑的10分钟兼容策略。
    public static byte Percent(InventorySnapshot inventory, DateTimeOffset now) =>
        (byte)Math.Clamp(100 - ((Math.Max(0, inventory.StaminaUntil - now.ToUnixTimeSeconds()) + 59) / 60) * 9, 10, 100);

    public InventoryResult Recover(Character character, ushort mode, DateTimeOffset now)
    {
        var before = character.Inventory;
        if (mode > 1) return InventoryResult.Reject(19);
        byte stamina = Percent(before, now);
        if (stamina == 100) return InventoryResult.Reject(18);
        var after = before;
        if (mode == 1)
        {
            if (!DevilContractPolicy.TryConsume(before, 5, 10, now, out after))
                return InventoryResult.Reject(19);
        }
        else
        {
            int level = character.Level;
            if (level <= 0 || level > catalog.RecoveryPrices.Length) return InventoryResult.Reject(19);
            uint price = checked((uint)((long)catalog.RecoveryPrices[level - 1] * (100 - stamina) / 90));
            if (before.Gold < price) return InventoryResult.Reject(22);
            after = before with { Gold = before.Gold - price };
        }
        return new(true, 0, new(before, after with { StaminaUntil = 0 }, "stamina_recovery",
            new(character, mode == 1 ? DevilContractPolicy.Expiry(before.AccountWallet, 5) : null)));
    }
}
