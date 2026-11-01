namespace DfoServer.Game.PrivilegeServices;

// 礼遇特权属于购买角色；账号契约、网吧身份和普通物品实例均不能代替此资格。
internal sealed record PrivilegeServiceState(long StartedAt, long ExpiresAt, int PurchaseSeason, int RewardSeason,
    int LastLoginDay, int LoginDays, int DailyClaimDay, uint ClaimedMilestones)
{
    public static PrivilegeServiceState Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    public bool IsActive(long utcSeconds) => StartedAt <= utcSeconds && utcSeconds < ExpiresAt;

    public void Validate()
    {
        if (StartedAt < 0 || ExpiresAt < 0 || ExpiresAt > uint.MaxValue || PurchaseSeason < 0 || RewardSeason < 0 ||
            LastLoginDay < 0 || LastLoginDay > DateOnly.MaxValue.DayNumber || LoginDays < 0 ||
            DailyClaimDay < 0 || DailyClaimDay > LastLoginDay ||
            ExpiresAt == 0 && this != Empty || ExpiresAt > 0 &&
            (StartedAt <= 0 || StartedAt >= ExpiresAt || LastLoginDay == 0 || LoginDays == 0 || LoginDays > LastLoginDay))
            throw new InvalidDataException("角色礼遇特权账本无效");
    }

    public PrivilegeServiceState ObserveLogin(long utcSeconds, int day)
    {
        Validate();
        if (day <= 0 || day > DateOnly.MaxValue.DayNumber) throw new ArgumentOutOfRangeException(nameof(day));
        return IsActive(utcSeconds) && day > LastLoginDay
            ? this with { LastLoginDay = day, LoginDays = checked(LoginDays + 1) }
            : this;
    }
}
