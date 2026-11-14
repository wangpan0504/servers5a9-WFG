using DfoServer.Game.Characters;
using DfoServer.GameWorld.Time;

namespace DfoServer.Game.Dungeons;

// 本服按匹配UI补齐：每角色跨图、跨难度共用3次，本机06:00分日，不复用普通incount。
internal sealed record DungeonPracticeAdmission
{
    public int Day { get; }
    public TimeProvider Clock { get; }
    public DungeonPracticeAdmission(int day, TimeProvider clock)
    {
        if (day is < 1 or > 3652058) throw new ArgumentOutOfRangeException(nameof(day));
        ArgumentNullException.ThrowIfNull(clock);
        Day = day;
        Clock = clock;
    }

    public const byte Maximum = 3;
    public static DungeonPracticeAdmission? Prepare(CharacterDungeonState state, TimeProvider clock)
    {
        state.Validate();
        int day = ServiceDay.Number(clock.GetUtcNow(), clock.LocalTimeZone);
        return Used(state, day) < Maximum ? new(day, clock) : null;
    }

    public static byte Used(CharacterDungeonState state, int day) => day > state.PracticeDay ? (byte)0 : state.PracticeUsed;

    public void RequireCurrentDay()
    {
        if (Day != ServiceDay.Number(Clock.GetUtcNow(), Clock.LocalTimeZone))
            throw new DungeonPracticeRejectedException("practice-day-changed");
    }

    public DungeonPracticeTransition Transition(CharacterDungeonState state)
    {
        state.Validate();
        byte before = Used(state, Day);
        if (before >= Maximum) throw new DungeonPracticeRejectedException("practice-entry-limit");
        return new(Math.Max(state.PracticeDay, Day), before, checked((byte)(before + 1)));
    }
}

// 仅描述同次提交的计数过渡，不是另一份角色/在线额度。
internal sealed record DungeonPracticeTransition(int Day, byte BeforeUsed, byte AfterUsed);
internal sealed class DungeonPracticeRejectedException(string reason) : InvalidOperationException(reason);
