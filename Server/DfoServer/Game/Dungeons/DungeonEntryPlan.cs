using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 这里只选择已确认任务的资源，不代表完整入场资格、实例建立或客户端加载成功。
// 调用方仍须在有效账号 owner 下绑定会话 epoch，并在实际进入时重验任务与 revision。
internal sealed record DungeonEntryPlan(
    ushort CharacterId,
    long CharacterRevision,
    uint DungeonId,
    uint QuestId,
    byte ActiveQuestSlot,
    byte Difficulty,
    DungeonMaze Maze,
    DungeonMap StartMap,
    DungeonMap BossMap)
{
    public Guid QuestAcceptanceId { get; init; }

    public static bool TryCreate(DungeonCatalog catalog, Character character, uint dungeonId, uint questId,
        byte difficulty, out DungeonEntryPlan? plan, out string reason, bool allowClearedQuest = false)
    {
        plan = null;
        reason = ValidateRequest(character, dungeonId, questId, difficulty, allowClearedQuest);
        if (reason != "resource-request-ready")
            return false;
        DungeonDefinition definition;
        try
        {
            definition = catalog.Get((int)dungeonId);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            reason = "dungeon-resource-unavailable";
            return false;
        }

        return TryCreate(definition, character, dungeonId, questId, difficulty, out plan, out reason, allowClearedQuest);
    }

    internal static bool TryCreate(DungeonDefinition definition, Character character, uint dungeonId, uint questId,
        byte difficulty, out DungeonEntryPlan? plan, out string reason, bool allowClearedQuest = false)
    {
        plan = null;
        reason = ValidateRequest(character, dungeonId, questId, difficulty, allowClearedQuest);
        if (reason != "resource-request-ready")
            return false;
        if (definition.Id != dungeonId)
        {
            reason = "dungeon-resource-mismatch";
            return false;
        }

        if (definition.MinimumLevel is null)
        {
            reason = "dungeon-level-unknown";
            return false;
        }

        if (character.Level < definition.MinimumLevel)
        {
            reason = "dungeon-level-too-low";
            return false;
        }

        // 仅识别匹配 PVF 已确认的三个整数；未知引用和其他条件形状不能变成默认迷宫。
        var candidates = definition.Mazes.Where(maze => maze.ConnectsQuest(questId)).ToArray();
        if (candidates.Length != 1)
        {
            reason = candidates.Length == 0 ? "quest-maze-unavailable" : "quest-maze-ambiguous";
            return false;
        }

        var maze = candidates[0];
        if (maze.SourceOrdinal < 0 || maze.Width <= 0 || maze.Height <= 0 || maze.Starts.Count != 1 || maze.Bosses.Count != 1
            || maze.MapSpecification is not { Count: > 0 } maps)
        {
            reason = "quest-maze-layout-unsupported";
            return false;
        }

        var start = maze.Starts[0];
        var boss = maze.Bosses[0];
        if (!InBounds(start, maze) || !InBounds(boss, maze))
        {
            reason = "quest-maze-layout-unsupported";
            return false;
        }

        var starts = maps.Where(map => map.Position == start).ToArray();
        var bosses = maps.Where(map => map.Position == boss).ToArray();
        if (starts.Length != 1 || bosses.Length != 1 || bosses[0].Kind != "boss")
        {
            reason = "quest-maze-map-unavailable";
            return false;
        }

        // SourceOrdinal 仅保留资源顺序，不能直接作为 NOTI28 的 wire 枚举。
        var active = character.ActiveMainQuests.Single(quest => quest.QuestId == questId);
        plan = new(character.Id, character.Revision, dungeonId, questId, active.Slot, difficulty, maze, starts[0], bosses[0])
        {
            QuestAcceptanceId = active.AcceptanceId
        };
        reason = "resource-plan-ready";
        return true;
    }

    private static string ValidateRequest(Character character, uint dungeonId, uint questId, byte difficulty, bool allowClearedQuest)
    {
        if (dungeonId != 3)
            return "dungeon-unsupported";
        if (questId != 3145)
            return "quest-unsupported";
        if (difficulty != 0)
            return "difficulty-unsupported";
        if (character.Position.Town != 38 || character.Position.Area != 2)
            return "dungeon-entry-mismatch";
        if (character.Level is < 1 or > 95)
            return "character-level-invalid";
        if (character.CompletedQuests.Contains((int)questId))
            return "quest-already-completed";
        var active = character.ActiveMainQuests.Where(quest => quest.QuestId == questId).ToArray();
        if (active.Length == 0)
            return "quest-not-active";
        if (active.Length != 1 || active[0].Slot > 29
            || character.ActiveMainQuests.Count(quest => quest.Slot == active[0].Slot) != 1)
            return "quest-active-conflict";
        // 已达成条件只有持久清图资格才能继续挑战；裸条件值不能伪装正式通关。
        if (active[0].ConditionState != 1 && !(allowClearedQuest && active[0].ConditionState == 0))
            return "quest-condition-not-pending";
        return "resource-request-ready";
    }

    private static bool InBounds(DungeonPoint point, DungeonMaze maze) =>
        point.X >= 0 && point.Y >= 0 && point.X < maze.Width && point.Y < maze.Height;
}
