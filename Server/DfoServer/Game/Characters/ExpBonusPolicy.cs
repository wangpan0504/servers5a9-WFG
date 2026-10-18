namespace DfoServer.Game.Characters;

internal static class ExpBonusPolicy
{
    public static uint Bonus(Character character, uint basis, long now)
    {
        if (basis == 0 || character.ExpBonusPermille <= 0 || character.ExpBonusUntil <= now) return 0;
        ulong value = (ulong)basis * (uint)character.ExpBonusPermille / 1000;
        if (value > uint.MaxValue) throw new InvalidDataException("秘药经验加成超出范围");
        return (uint)value;
    }

    public static uint Shown(uint plain, uint contract, uint potion, uint gained)
    {
        uint contractShown = gained > plain ? Math.Min(contract, gained - plain) : 0;
        uint afterContract = plain + contractShown;
        return gained > afterContract ? Math.Min(potion, gained - afterContract) : 0;
    }
}
