using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal enum DungeonAltarRejection { None, Ineligible, DailyLimit, InsufficientGold }

internal static class DungeonAltarEntryPolicy
{
    // 兼容依据：Neople官方Infinity Ascension and Despair列两祭坛每日3次，等级60/65与本版资源一致。
    // 这是明确来源的服务端兼容规则；不是从缺失的[limit inout count]推导默认值。
    public const int DailyLimit = 3;
    public const string RuleId = "neople-valley-daily-three-pvf-fee-v1";

    public static DungeonSpecialEntry? Prepare(Character character, DungeonDefinition definition, int day,
        DungeonSpecialEntryUsage usage, DungeonAltarAdmissionCatalog prices, out DungeonAltarRejection rejection)
    {
        rejection = DungeonAltarRejection.Ineligible;
        if (definition.Id is not (11006 or 11007) || definition.AltarKind != (definition.Id == 11006 ? 1 : 2) ||
            definition.MinimumLevel is not { } minimum || character.Level < minimum ||
            !definition.DisableFatigueConsumption || !definition.EnterWithoutFatigue || definition.DeclaresRequiredItem) return null;
        if (usage.CharacterEntries >= DailyLimit)
        {
            rejection = DungeonAltarRejection.DailyLimit;
            return null;
        }
        int gold = definition.AltarKind == 1 ? prices.BloodFee(character.Level, usage.CharacterEntries) : 0;
        if (character.Inventory.Gold < gold)
        {
            rejection = DungeonAltarRejection.InsufficientGold;
            return null;
        }
        var entry = new DungeonSpecialEntry(day, (DungeonSpecialEntryKind)definition.AltarKind, 0, gold, usage);
        entry.Validate();
        rejection = DungeonAltarRejection.None;
        return entry;
    }

    public static InventoryPlan? Payment(Character character, DungeonSpecialEntry entry)
    {
        entry.Validate();
        if (entry.Kind is not (DungeonSpecialEntryKind.Blood or DungeonSpecialEntryKind.Ultimate))
            throw new InvalidDataException("祭坛付款不能代替绝望之塔票据");
        return CurrencyService.Spend(character.Inventory, CurrencyKind.Gold, entry.Gold).Plan;
    }
}
