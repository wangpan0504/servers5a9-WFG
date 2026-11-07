using PvfLib;

namespace DfoServer.GameWorld.Items;

internal readonly record struct EquipmentUpgradeBonuses(float ReinforcementRate, float AmplificationRate,
    float ProbabilityIncrease, float ReinforcementDiscount, float AmplificationDiscount)
{
    public static EquipmentUpgradeBonuses Read(PvfDocument document)
    {
        var raw = EquipmentUpgradeFile.Read(document);
        return new(raw.ReinforcementRate, raw.AmplificationRate, raw.ProbabilityIncrease,
            raw.DiscountReinforcement == 1 ? raw.CostDiscount : 0,
            raw.DiscountAmplification == 1 ? raw.CostDiscount : 0);
    }
}
