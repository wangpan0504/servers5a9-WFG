namespace DfoServer.Game.Titles;

// Total由真实玩法收据的原事务累计，Applied只随称号进度和奖励同事务推进。
internal readonly record struct FiendwarTitleCounter(int Quest, int Total, int Applied)
{
    internal int Pending => Total - Applied;
    internal bool Valid => FiendwarTitleFacts.IsQuest(Quest) && Applied >= 0 && Total > Applied && Total <= ushort.MaxValue;
}

internal static class FiendwarTitleFacts
{
    internal static bool IsQuest(int quest) => quest is >= 7003 and <= 7008;
    internal static bool IsTeamDungeon(uint dungeon) => dungeon is >= 291100339 and <= 291100364;
    internal static bool IsWorld(uint dungeon) => dungeon is 291100339 or 291100364;
}
