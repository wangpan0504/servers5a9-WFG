namespace DfoServer.Game.EpicProduction;

// 角色资产状态；周次数不随切角、换武器或领取成品重置。
internal sealed record EpicProductionState(int WeaponId, int Charge, int Week, int WeeklyUses,
    int SinceBigChance, int SpecialUses, int AdditionalWeeklyUses)
{
    public int IntermediateMaterialRemainder { get; init; }
    public int OrdinaryMaterialRemainder { get; init; }
    public static EpicProductionState Empty { get; } = new(0,0,-1,0,0,0,0);
    public void Validate()
    {
        if (WeaponId < 0 || Charge < 0 || Week < -1 || WeeklyUses is < 0 or > byte.MaxValue ||
            IntermediateMaterialRemainder < 0 || OrdinaryMaterialRemainder < 0 || SinceBigChance < 0 || SpecialUses < 0 || AdditionalWeeklyUses < 0 || WeaponId == 0 && Charge != 0)
            throw new InvalidDataException("史诗制作角色状态无效");
    }
    public EpicProductionState InWeek(int week)
    {
        Validate();
        if (week < Week) throw new InvalidOperationException("史诗制作周期不能倒退");
        return week == Week ? this : this with { Week = week, WeeklyUses = 0, AdditionalWeeklyUses = 0 };
    }
}
