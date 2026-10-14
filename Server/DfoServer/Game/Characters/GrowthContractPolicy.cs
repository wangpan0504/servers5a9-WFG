using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Characters;

internal static class GrowthContractPolicy
{
    public static uint ExperienceBonus(Character character, uint basis, GrowthContractBenefits? benefits, long now) =>
        Active(character, benefits, now) ? checked((uint)((ulong)basis * (uint)benefits!.ExperiencePercent / 100)) : 0;

    public static int QuestDropRate(Character character, int basis, GrowthContractBenefits? benefits, long now)
    {
        if (basis is < 0 or > 100) throw new InvalidDataException("任务掉落概率越界");
        return Active(character, benefits, now) ? (int)Math.Min(100, basis + (long)basis * benefits!.QuestItemDropPercent / 100) : basis;
    }

    public static bool Active(Character character, GrowthContractBenefits? benefits, long now) =>
        benefits is not null && character.Inventory.AccountWallet.ContractExpiry(84) > now;

    // 等级上限截断时只展示实际发放的加成，不倒灌成基础或组队经验。
    public static uint AppliedBonus(uint basis, uint bonus, uint gained) => gained > basis ? Math.Min(bonus, gained - basis) : 0;
}
