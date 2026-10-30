namespace DfoServer.Game.Dungeons;

// 六图顺序采用2017国服规则；组队共同入场日、个人进度与首击资格采用批准的本服兼容规则。
internal readonly record struct OrdinaryLukeProgress(int Day, int Completed)
{
    public const uint SequenceId = 42;

    public OrdinaryLukeSequenceInfo SequenceInfo(int day)
    {
        int completed = day == Day ? Completed : 0;
        if (completed is < 0 or > 6) throw new InvalidDataException("普通卢克进度越界");
        return new(checked((byte)completed), (1u << completed) - 1);
    }

    public bool Allows(int day, uint dungeonId) => day >= Day && dungeonId is >= 5101 and <= 5106 &&
        dungeonId - 5101 <= (day == Day ? Completed : 0);

    public OrdinaryLukeProgress Clear(int day, uint dungeonId)
    {
        if (day < Day) return this; // 旧Run晚提交不能覆盖新日，也不能推进新日。
        if (!Allows(day, dungeonId)) throw new InvalidDataException("普通卢克通关缺少当日前序进度");
        return new(day, Math.Max(day == Day ? Completed : 0, checked((int)dungeonId - 5100)));
    }

    public static bool Applies(DungeonEntryPlan entry) => entry.IsOrdinaryLuke && entry.IsOrdinary &&
        !entry.IsPractice && entry.DungeonId is >= 5101 and <= 5106;
}

internal readonly record struct OrdinaryLukeSequenceInfo(byte Stage, uint ClearBits);
