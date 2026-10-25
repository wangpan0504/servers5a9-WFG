namespace DfoServer.Game.Dungeons;

internal sealed partial class SinglePlayerDungeonRun
{
    private Room? ResolveCinematicBossTerminal()
    {
        if (entry.IsTutorial || entry.IsClassExperience || entry.IsPractice || entry.HasExplicitClearMap ||
            entry.QuestClearMap is not null || entry.TerminalLayerMap is not null || entry.BossMap != entry.BaseBossMap ||
            storyRooms.Length != 0 || conditionalRooms.Count != 0 || entry.Maze.DummyStages.Count != 0 ||
            entry.Maze.ClearConditions.Count != 0 || entry.RouteQuestId is 0 or >= 30000 ||
            !entry.Maze.ConnectsQuest(entry.RouteQuestId,entry.Difficulty)) return null;
        if (entry.IsQuestReplay)
        {
            if (entry.QuestId != 0 || entry.ActiveQuestSlot != byte.MaxValue || entry.QuestAcceptanceId != Guid.Empty) return null;
        }
        else
        {
            var accepted = entry.QuestAcceptances.Where(row => row.QuestId == entry.RouteQuestId).ToArray();
            if (accepted is not [{ Slot: <= 29, ConditionState: <= 1 } original] || original.AcceptanceId == Guid.Empty ||
                entry.QuestAcceptances.Count(row => row.Slot == original.Slot) != 1 ||
                entry.QuestAcceptances.Count(row => row.AcceptanceId == original.AcceptanceId) != 1 ||
                entry.QuestId != 0 && (entry.QuestId != original.QuestId || entry.ActiveQuestSlot != original.Slot ||
                    entry.QuestAcceptanceId != original.AcceptanceId)) return null;
        }
        var basis = rooms[entry.BaseBossMap.Position];
        if (basis.Plan.Room.BossDeathLayerChange is not { } change || change.MapId != basis.Plan.Room.Map.Id ||
            change.MapPath != basis.Plan.Room.Map.Path || change.CinematicId <= 0 || change.CinematicPath.Length == 0 ||
            basis.Plan.Monsters.Where(row => row.IsBlocking).ToArray() is not [{ IsBoss:true, IsScriptedBoss:false, AiCharacter:null } source] ||
            source.SourceOrdinal != change.MonsterOrdinal || source.MonsterId != change.MonsterId || source.Source?.Path != change.MonsterPath ||
            entry.Maze.LayeredMaps.Where(map => map.Position == entry.BaseBossMap.Position).ToArray() is not [var layer] ||
            !layeredRooms.TryGetValue(layer,out var terminal) || terminal.Plan.Room.DeclaredType != "[boss]" ||
            terminal.Plan.Room.StartCinematicBossDestroy is not { } destroy || destroy.MapId != layer.Id || destroy.MapPath != layer.Path ||
            destroy.CinematicId <= 0 || destroy.CinematicPath.Length == 0 ||
            destroy.MonsterId != change.MonsterId || destroy.MonsterPath != change.MonsterPath ||
            terminal.Plan.Monsters.Where(row => row.IsBlocking).ToArray() is not
                [{ IsBoss:true, IsScriptedBoss:false, IsFriendly:false, CanReportDeath:true, AiCharacter:null } target] ||
            target.SourceOrdinal != destroy.MonsterOrdinal || target.MonsterId != destroy.MonsterId || target.Source?.Path != destroy.MonsterPath)
            return null;
        // 基础死亡演出换层、末层演出销毁同模板Boss；仅改终点，不补基础39，也不执行资源中的DESTROY。
        // 末层仍须本实例真实对象39及既有持久死亡事务，才能产生ClearFact和结算许可。
        return terminal;
    }
}
