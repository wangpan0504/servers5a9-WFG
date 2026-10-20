namespace DfoServer.Game.Characters;

// 原序保留已证owner；三个附属值不根据等级、经验或当前剩余量推算。
internal sealed record CharacterFatigueState(ushort Used, ushort Maximum, ushort UsedMaximum,
    ushort Remaining, ushort ContextAuxiliary, ushort SelectionAuxiliary)
{
    public const string OrdinaryRoomRuleId = "local-3145-first-room-fatigue-v1";
    public const string NormalRoomRuleId = "local-normal-first-room-fatigue-v1";
    // #511 [use fatigue only start dungeon] N 的入场一次性扣点账本规则。
    public const string StartDungeonRuleId = "local-start-dungeon-fatigue-v1";
    // 与此前普通选角投影一致的本服初始状态，不宣称是原服恢复/重置公式。
    public const ushort OrdinaryMaximum = 156;
    public const ushort BlackDiamondBonus = 32;
    public const ushort BlackDiamondMaximum = OrdinaryMaximum + BlackDiamondBonus;
    public static CharacterFatigueState NewOrdinary => new(0, OrdinaryMaximum, 0, OrdinaryMaximum, 0, 0);

    public CharacterFatigueState GrantBlackDiamondCapacity()
    {
        Validate();
        return Maximum == OrdinaryMaximum
            ? this with { Maximum = BlackDiamondMaximum, Remaining = checked((ushort)(Remaining + BlackDiamondBonus)) }
            : this;
    }

    public CharacterFatigueState ForBlackDiamond(bool active)
    {
        Validate();
        if (active) return GrantBlackDiamondCapacity();
        // 保留已经消耗的黑钻容量，关闭后隐藏剩余附加池，续期不能再次补32点。
        return Maximum == BlackDiamondMaximum ? this with
        {
            Maximum = OrdinaryMaximum,
            Used = Math.Min(Used, OrdinaryMaximum),
            Remaining = (ushort)Math.Max(0, Remaining - BlackDiamondBonus)
        } : this;
    }

    public void Validate()
    {
        if (Maximum == 0 || Used > Maximum || Remaining > Maximum)
            throw new InvalidDataException("疲劳耗用值或当前剩余量超过实际上限");
    }

    // 入场一次性疲劳：余额不足时不扣成负数，由准入先拒绝。
    public CharacterFatigueState ConsumeAtEntry(ushort amount)
    {
        Validate();
        if (amount == 0 || Remaining < amount) throw new InvalidOperationException("入场疲劳余额不足");
        return this with { Used = (ushort)Math.Min(Maximum, Used + amount), Remaining = (ushort)(Remaining - amount) };
    }

    public CharacterFatigueState ConsumeOrdinaryRoom()
    {
        Validate();
        return this with { Used = (ushort)Math.Min(Maximum, Used + 1), Remaining = (ushort)Math.Max(0, Remaining - 1) };
    }
}
