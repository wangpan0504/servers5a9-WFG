using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal readonly record struct PreyNormalProgress(int Day, int Branch, int Completed)
{
    public bool Allows(PreyNormalAdmission entry, uint dungeon) => entry.Day >= Day &&
        entry.Branch == PreyNormalCatalog.BranchForDay(entry.Day) &&
        (Day != entry.Day || Branch == entry.Branch) &&
        entry.Catalog.Route(entry.Branch).IndexOf(checked((int)dungeon)) is var index && index >= 0 &&
        index <= (Day == entry.Day ? Completed : 0);

    public PreyNormalProgress Clear(PreyNormalAdmission entry, uint dungeon)
    {
        if (entry.Day < Day) return this;
        if (!Allows(entry, dungeon)) throw new InvalidDataException("普通普雷缺少当日路线前序");
        int completed = entry.Catalog.Route(entry.Branch).IndexOf(checked((int)dungeon)) + 1;
        return new(entry.Day, entry.Branch, Math.Max(Day == entry.Day ? Completed : 0, completed));
    }

    public PreyNormalSequenceInfo Information(int day)
    {
        int branch = PreyNormalCatalog.BranchForDay(day), completed = day == Day ? Completed : 0;
        if (completed is < 0 or > 5 || day == Day && Branch != branch)
            throw new InvalidDataException("普通普雷进度与日历不匹配");
        return new(checked((byte)completed), (1u << completed) - 1, checked((uint)branch));
    }

    public static bool Applies(DungeonEntryPlan entry) => entry.IsOrdinaryPrey && entry.IsOrdinary && !entry.IsPractice;
}

internal sealed record PreyNormalAdmission(int Day, int Branch, PreyNormalCatalog Catalog);
internal readonly record struct PreyNormalSequenceInfo(byte Stage, uint ClearBits, uint Branch);
