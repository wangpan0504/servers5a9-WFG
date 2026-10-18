using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class StaminaRecoveryService(DevilContractCatalog catalog)
{
    // 本版PVF声明恢复价格与免费次数，未声明自然恢复周期；采用参考逻辑的10分钟兼容策略。
    public static byte Percent(InventorySnapshot inventory, DateTimeOffset now) =>
        inventory.AccountWallet.ContractExpiry(CardMembership.BlackDiamondType) > now.ToUnixTimeSeconds() ? (byte)100 :
            (byte)Math.Clamp(100 - ((Math.Max(0, inventory.StaminaUntil - now.ToUnixTimeSeconds()) + 59) / 60) * 9, 10, 100);

    public static InventorySnapshot ApplyDungeonRetreat(Character character, DungeonEntryPlan entry,
        SinglePlayerDungeonPhase phase, DateTimeOffset now, bool premiumPcRoom = false)
    {
        var before = character.Inventory;
        if (phase != SinglePlayerDungeonPhase.Dungeon || entry.DungeonId == 5000 || entry.DisableGiveupPenalty ||
            entry.IsTutorial || entry.HasFreeCombatConsumption || entry.IsTournament)
            return before;
        long seconds = now.ToUnixTimeSeconds();
        if (character.BlackDiamondExpiresAt > seconds)
            return before.StaminaUntil == 0 ? before : before with { StaminaUntil = 0 };
        // 网吧身份只豁免本次退图，不凭全局调试开关改写角色已有资产。
        if (premiumPcRoom)
            return before;
        // 匹配ARAD的20级门槛；非豁免退图仍保留十分钟及重复撤退重计时规则。
        if (character.Level < 20)
            return before;
        return before with { StaminaUntil = seconds + 600 };
    }

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
