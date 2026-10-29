namespace DfoServer.Game.Dungeons;

// 本服时空裂缝资格：区域 [hell quest] 完成后，将对应 [inferno quest] / 世界地图 [is clear quest]
// 视为选图与入场已满足，并在 NOTI356 补显示位。外传仍可按原任务接取交付，不另写存档。
internal static class InfernoQuestUnlock
{
    public static readonly (int InfernoQuest, int HellQuest)[] Pairs =
    [
        (5783, 6880),
        (10104, 4391),
        (10106, 4539),
        (10108, 2002),
        (10110, 2900)
    ];

    public static readonly int[] PreparationQuestIds =
    [
        6880, 4391, 4539, 2002, 2900, 6080,
        5782, 5783, 10103, 10104, 10105, 10106, 10107, 10108, 10109, 10110
    ];

    public static bool Satisfied(int questId, IReadOnlySet<int> done)
    {
        if (done.Contains(questId)) return true;
        foreach (var (inferno, hell) in Pairs)
            if (inferno == questId && done.Contains(hell)) return true;
        return false;
    }

    public static void ApplyDisplayBits(byte[] body, IReadOnlySet<int> done)
    {
        foreach (var (inferno, hell) in Pairs)
            if (body[4 + inferno] == 0 && done.Contains(hell))
                body[4 + inferno] = 1;
    }
}
