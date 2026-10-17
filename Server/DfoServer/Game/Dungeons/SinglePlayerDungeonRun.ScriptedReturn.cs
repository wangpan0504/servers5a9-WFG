using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed partial class SinglePlayerDungeonRun
{
    // 仅描述客户端呈现；实际房间、死亡候选、epoch和结算仍归原末层。
    private sealed class ScriptedReturn(DungeonPoint target)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public DungeonPoint Target { get; } = target;
        public bool Presented { get; set; }
        public bool Loaded { get; set; }
    }

    private ScriptedReturn? scriptedReturn;
    internal bool HasScriptedReturn => scriptedReturn is not null;
    internal Guid ScriptedReturnId => scriptedReturn?.Id ?? Guid.Empty;
    internal bool MatchesScriptedReturn(Guid id) => id != Guid.Empty && scriptedReturn?.Id == id &&
        Phase is SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement;

    internal DungeonRunResult? BeginScriptedReturn(DungeonRunIdentity expected, DungeonPoint target, byte kind)
    {
        if (!Matches(expected) || Phase is not (SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement) ||
            HasPendingDeath || pendingSettlement is not null || kind > 1) return null;
        if (scriptedReturn is not null)
        {
            if (target != scriptedReturn.Target) return Result(DungeonRunOutcome.Rejected);
            return kind == 0 && !scriptedReturn.Presented ? Presentation() : Result(DungeonRunOutcome.Duplicate);
        }
        if (kind != 0 || !CanPresentScriptedBase(target)) return null;
        scriptedReturn = new(target);
        return Presentation();
    }

    DungeonRunResult Presentation() => new(DungeonRunOutcome.Accepted,Snapshot with
        {
            MapOperation = DungeonMapOperation.RestoreBase,
            MapPayload = DungeonMapPayload.Cached
        });

    internal void ConfirmScriptedReturnPresentation(DungeonRunIdentity expected, Guid id)
    {
        if (Matches(expected) && MatchesScriptedReturn(id)) scriptedReturn!.Presented = true;
    }

    internal DungeonRunOutcome CompleteScriptedReturn(DungeonRunIdentity expected)
    {
        if (!Matches(expected) || scriptedReturn is not { Presented: true } || !MatchesScriptedReturn(scriptedReturn.Id) ||
            HasPendingDeath || pendingSettlement is not null) return DungeonRunOutcome.Rejected;
        if (scriptedReturn.Loaded) return DungeonRunOutcome.Duplicate;
        return DungeonRunOutcome.Accepted;
    }

    internal void ConfirmScriptedReturnLoading(DungeonRunIdentity expected, Guid id)
    {
        if (Matches(expected) && MatchesScriptedReturn(id) && scriptedReturn is { Presented: true }) scriptedReturn.Loaded = true;
    }

    internal bool ConfirmScriptedReturnBase(DungeonRunIdentity expected, DungeonPoint target)
    {
        if (!Matches(expected) || scriptedReturn is not { Presented: true } || !MatchesScriptedReturn(scriptedReturn.Id) ||
            scriptedReturn.Target != target || HasPendingDeath || pendingSettlement is not null) return false;
        return true;
    }

    internal bool CanPresentScriptedBase(DungeonPoint target)
    {
        var map = current.Plan.Room.Map;
        if (entry.IsTutorial || entry.IsClassExperience || entry.IsQuestReplay || !entry.HasExplicitClearMap ||
            entry.RouteQuestId is 0 or >= 30000 || entry.TerminalLayerMap != map || map.Kind != "layered" ||
            map.Position != target || !current.Loaded || !enteredLayers.Contains(map) || HasPendingLayer() ||
            current.Elevator is { Completed: false } || !layerSources.TryGetValue(target,out var source) || !source.Loaded ||
            current.Plan.Room.ScriptedLayerReturn is not { } action || action.MapId != map.Id || action.MapPath != map.Path ||
            action.X != target.X || action.Y != target.Y || action.SceneFlag != 1 || action.ActionIndex < 0 ||
            action.ActionPath.Length == 0 || action.ObjectOrdinal < 0 || action.ObjectOrdinal >= current.Plan.Room.PassiveObjects.Count)
            return false;
        var actor = current.Plan.Room.PassiveObjects[action.ObjectOrdinal];
        if (actor.ObjectId != action.ObjectId || actor.Path != action.ObjectPath) return false;
        var acceptances = entry.QuestAcceptances.Where(row => row.QuestId == entry.RouteQuestId).ToArray();
        return acceptances is [{ Slot: <= 29 } accepted] && accepted.AcceptanceId != Guid.Empty &&
            entry.TerminalLayerQuestId == accepted.QuestId && entry.TerminalLayerQuestSlot == accepted.Slot &&
            entry.TerminalLayerAcceptanceId == accepted.AcceptanceId &&
            entry.QuestAcceptances.Count(row => row.Slot == accepted.Slot) == 1 &&
            entry.QuestAcceptances.Count(row => row.AcceptanceId == accepted.AcceptanceId) == 1 &&
            (entry.QuestId == 0 || entry.QuestId == accepted.QuestId && entry.ActiveQuestSlot == accepted.Slot &&
                entry.QuestAcceptanceId == accepted.AcceptanceId);
    }
}
