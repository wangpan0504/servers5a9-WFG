namespace DfoServer.Game.Dungeons;

// 只属于原Run；离开实例一起失效，不把一次战斗统计永久写入角色库。
internal sealed class DungeonQuestFacts(TimeProvider clock)
{
    long? started;
    TimeSpan? cleared;
    public int Revivals { get; private set; }
    public void Start() => started ??= clock.GetTimestamp();
    public void Revived() => Revivals = checked(Revivals + 1);
    public TimeSpan? Elapsed => cleared ?? (started is { } value ? clock.GetElapsedTime(value) : null);
    public void FreezeClear() => cleared ??= Elapsed;
    public void CancelClear() => cleared = null;
}
