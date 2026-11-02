using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 首次地下城的等级、难度和柱子房调级规则跨轮次冻结；费用仍由目标图的正常入场规划决定。
internal sealed record HelperHellRotation(byte Town, byte Area, int? BasisLevel, int? MonsterBasisLevel,
    byte Difficulty, byte HellPartyMode, DungeonBattleLevelRule? BattleRule)
{
    internal static bool Supports(DungeonEntryPlan entry) => entry.IsOrdinary && entry.QuestId == 0 &&
        entry.RouteQuestId == 0 && entry.HellParty is not null && entry.HellPartyMode > 0 &&
        !entry.IsHellLegend && !entry.IsCosmo && !entry.IsInfernoParty && !entry.IsPractice &&
        !entry.IsQuestReplay && !entry.IsTagSystem && !entry.IsTournament && !entry.IsBossRush &&
        entry.RaidEntry is null && entry.PartyRoute is null && entry.LostLand is null && entry.Dimension is null &&
        entry.Hope is null && entry.Marriage is null && entry.Maze.QuestConnectionTokens.Count == 0;

    internal DungeonEntryPlan? Apply(DungeonEntryPlan target)
    {
        if (!Supports(target) || target.Difficulty != Difficulty || target.HellParty is not { } hell ||
            target.Maze.MapSpecification?.Where(map => map.Id == hell.MapId && map.Position == hell.Room).ToArray() is not [var start])
            return null;
        return target with { StartMap = start, BasisLevel = BasisLevel, MonsterBasisLevel = MonsterBasisLevel,
            HellPartyMode = HellPartyMode, HelperRotation = this };
    }

    // 按地区主选图顺序轮转，多个地下城共用同一MAP仍分别参与。
    internal static uint? Next(IReadOnlyList<uint> candidates, uint current)
    {
        int at = -1;
        for (int i = 0; i < candidates.Count; i++) if (candidates[i] == current) { at = i; break; }
        return candidates.Count > 1 && at >= 0 ? candidates[(at + 1) % candidates.Count] : null;
    }
}
