namespace DfoServer.Game.Characters;

// 原序保留已证owner；三个附属值不根据等级、经验或当前剩余量推算。
internal sealed record CharacterFatigueState(ushort Used, ushort Maximum, ushort UsedMaximum,
    ushort Remaining, ushort ContextAuxiliary, ushort SelectionAuxiliary)
{
    public const string OrdinaryRoomRuleId = "local-3145-first-room-fatigue-v1";
    public const string NormalRoomRuleId = "local-normal-first-room-fatigue-v1";
    // 与此前普通选角投影一致的本服初始状态，不宣称是原服恢复/重置公式。
    public static CharacterFatigueState NewOrdinary => new(0, 156, 0, 156, 0, 0);

    public void Validate()
    {
        if (Maximum == 0 || Used > Maximum || Remaining > Maximum)
            throw new InvalidDataException("疲劳耗用值或当前剩余量超过实际上限");
    }

    public CharacterFatigueState ConsumeOrdinaryRoom()
    {
        Validate();
        return this with { Used = (ushort)Math.Min(Maximum, Used + 1), Remaining = (ushort)Math.Max(0, Remaining - 1) };
    }
}
