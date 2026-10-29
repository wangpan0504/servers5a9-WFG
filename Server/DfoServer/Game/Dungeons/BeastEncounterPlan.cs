using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record BeastEncounterRoom(int Stage, DungeonPoint Sentinel, int BossId, DungeonMonsterPlan Plan);

// 每种遭遇的六个真实阶段预先规划到各哨兵，入场一次预留所有对象key。
// 首访只安装当前阶段的候选，未选候选不发布、不参与清房或终局。
internal static class BeastEncounterPlan
{
    static readonly int[][][] Maps =
    [
        [[400363,400373,400374,400375,400376,400377], [400364,400378,400379,400380,400381,400382]],
        [[400365,400383,400384,400385,400386,400387], [400366,400389,400390,400391,400392,400393]],
        [[400371,400414,400415,400416,400417,400418], [400372,400419,400420,400421,400422,400423]],
        [[400369,400404,400405,400406,400407,400408], [400370,400409,400410,400411,400412,400413]],
        [[400367,400394,400395,400396,400397,400398], [400368,400399,400400,400401,400402,400403]]
    ];
    static readonly int[][] Bosses = [[107005374,107005375,107005376,107005377,107005378,107005379], [66159,66160,66161,66162,66164,66165]];

    public static bool IsNative(DungeonEntryPlan entry) => entry.BeastMode && entry.DungeonId is >= 310 and <= 314;

    public static (int Map, int Boss) Stage(DungeonEntryPlan entry, int stage)
    {
        if (!IsNative(entry) || entry.BeastDay is not { } day || stage is < 0 or > 5 ||
            !BeastDungeonRotation.Contains(day, checked((int)entry.DungeonId)))
            throw new InvalidDataException("原图魔兽阶段缺少冻结日及当日图身份");
        int encounter = BeastDungeonRotation.Pair(day).First == entry.DungeonId ? 0 : 1;
        return (Maps[checked((int)entry.DungeonId) - 310][encounter][stage], Bosses[encounter][stage]);
    }

    public static bool Validate(DungeonEntryPlan entry, IReadOnlyList<BeastEncounterRoom> candidates)
    {
        if (!IsNative(entry)) return candidates.Count == 0;
        if (entry.BeastDay is null || !BeastDungeonRotation.Contains(entry.BeastDay.Value, checked((int)entry.DungeonId)) ||
            entry.Maze.MapSpecification is not { } maps) return false;
        var sentinels = maps.Where(map => map.Kind == "Beast").Select(map => map.Position).ToHashSet();
        if (sentinels.Count != 6 || candidates.Count != 36 ||
            candidates.Select(row => (row.Sentinel, row.Stage)).Distinct().Count() != 36) return false;
        foreach (var row in candidates)
        {
            if (!sentinels.Contains(row.Sentinel) || row.Stage is < 0 or > 5) return false;
            var expected = Stage(entry, row.Stage);
            if (row.Plan.Room.Map.Kind != "Beast" || row.Plan.Room.Map.Position != row.Sentinel ||
                row.Plan.Room.Map.Id != expected.Map || row.BossId != expected.Boss ||
                row.Plan.Monsters.Count(monster => monster.MonsterId == row.BossId && monster.IsBoss && monster.IsBlocking) != 1)
                return false;
        }
        return true;
    }
}
