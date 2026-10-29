using DfoServer.Game.Characters;

namespace DfoServer.Game.Dungeons;

// 本服时空裂缝资格：区域 [hell quest] 完成后，将对应 [inferno quest] / 世界地图 [is clear quest]
// 视为选图与入场已满足。NOTI356仅在外传链尚未进入时补显示位，已进入的链保持真实任务状态。
internal static class InfernoQuestUnlock
{
    public static readonly (int InfernoQuest, int HellQuest, int IntroductionQuest)[] Pairs =
    [
        (5783, 6880, 5782),
        (10104, 4391, 10103),
        (10106, 4539, 10105),
        (10108, 2002, 10107),
        (10110, 2900, 10109)
    ];

    public static readonly int[] PreparationQuestIds =
    [
        6880, 4391, 4539, 2002, 2900, 6080,
        5782, 5783, 10103, 10104, 10105, 10106, 10107, 10108, 10109, 10110
    ];

    public static bool Satisfied(int questId, IReadOnlySet<int> done)
    {
        if (done.Contains(questId)) return true;
        foreach (var (inferno, hell, _) in Pairs)
            if (inferno == questId && done.Contains(hell)) return true;
        return false;
    }

    public static void ApplyDisplayBits(byte[] body, IReadOnlySet<int> done, IReadOnlyList<ActiveMainQuest> active)
    {
        foreach (var (inferno, hell, introduction) in Pairs)
            // 21仍会列出真实可接外传；此时伪完成位会与31活动槽冲突，重登也不能重新补回。
            if (body[4 + inferno] == 0 && done.Contains(hell) && !done.Contains(introduction) &&
                !active.Any(row => row.QuestId == introduction || row.QuestId == inferno))
                body[4 + inferno] = 1;
    }
}
