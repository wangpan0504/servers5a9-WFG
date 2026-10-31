using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal readonly record struct AntonNormalProgress(int Day, int Branch, int Completed)
{
    public const uint SequenceId = 28;

    public bool Allows(AntonNormalAdmission entry, uint dungeon) => entry.Branch is >= 1 and <= 4 && entry.Day >= Day &&
        (Day != entry.Day || Branch == entry.Branch) &&
        entry.Catalog.Routes[entry.Branch - 1].IndexOf(checked((int)dungeon)) is var index && index >= 0 &&
        index <= (Day == entry.Day ? Completed : 0) &&
        (entry.Catalog.DisabledAfterClear[checked((int)dungeon)] & (Day == entry.Day ? (1u << Completed) - 1 : 0)) == 0;

    public AntonNormalProgress Clear(AntonNormalAdmission entry, uint dungeon)
    {
        if (entry.Day < Day) return this;
        if (Day == entry.Day && Branch == entry.Branch && entry.Branch is >= 1 and <= 4 &&
            entry.Catalog.Routes[entry.Branch - 1].IndexOf(checked((int)dungeon)) is var oldIndex && oldIndex >= 0 && oldIndex < Completed)
            return this;
        if (!Allows(entry, dungeon)) throw new InvalidDataException("普通安徒恩缺少当日前序进度");
        int completed = entry.Catalog.Routes[entry.Branch - 1].IndexOf(checked((int)dungeon)) + 1;
        return new(entry.Day, entry.Branch, Math.Max(Day == entry.Day ? Completed : 0, completed));
    }

    public AntonNormalSequenceInfo Information(int day)
    {
        int completed = day == Day ? Completed : 0;
        // 沿既有续图的本服兼容规则：没有历史分支时使用资源第一条路线。
        int branch = day == Day ? Branch : 1;
        if (completed is < 0 or > 6 || branch is < 1 or > 4)
            throw new InvalidDataException("普通安徒恩进度或分支越界");
        return new(checked((byte)completed), (1u << completed) - 1, checked((uint)branch));
    }

    public static bool Applies(DungeonEntryPlan entry) => entry.IsOrdinaryAnton && entry.IsOrdinary && !entry.IsPractice;
}

internal sealed record AntonNormalAdmission(int Day, int Branch, AntonNormalSequenceCatalog Catalog);
internal readonly record struct AntonNormalSequenceInfo(byte Stage, uint ClearBits, uint Branch);
