using DfoServer.Game.Dungeons;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal sealed record Strongway2019RoomPreparation(PreparedStrongway2019RoomReplacement Replacement);
    Strongway2019RoomPreparation? strongway2019RoomPreparation;

    internal uint Strongway2019NextObjectKey => formalDungeon?.NextObjectKey ?? ushort.MaxValue + 1u;

    internal Strongway2019RoomPreparation? PrepareStrongway2019NextRoom(Strongway2019RouteChange change,
        DungeonPoint target, Strongway2019Catalog activity, DungeonRoomDefinition resource, ushort firstKey) => owner.Execute(() =>
    {
        if (!CanUseStrongway2019Combat() || formalDungeon is not { } dungeon ||
            !change.Participants.Any(member => ReferenceEquals(member.Run, dungeon.Run)) || resource.Map.Id != change.MapId ||
            CurrentStrongway2019Match!.Competition.Snapshot.PendingRouteChanges.All(row => !ReferenceEquals(row, change))) return null;
        // 原房间尚未清完时Move可以暂拒；重试复用同一实际准备，不再分配演员或推进epoch。
        if (strongway2019RoomPreparation is { } existing && existing.Replacement.ChangeId == change.Id &&
            existing.Replacement.Target == target && dungeon.Run.CanApplyStrongway2019NextRoom(existing.Replacement)) return existing;
        if (!dungeon.Run.TryPrepareStrongway2019NextRoom(dungeon.Run.Scene.Identity, change.Id, target, activity,
            resource, firstKey, out var replacement, out _)) return null;
        return strongway2019RoomPreparation = new(replacement!);
    });

    internal bool CanApplyStrongway2019NextRoom(Strongway2019RoomPreparation prepared) =>
        CanUseStrongway2019Combat() && ReferenceEquals(strongway2019RoomPreparation, prepared) &&
        formalDungeon!.Run.CanApplyStrongway2019NextRoom(prepared.Replacement);

    internal FormalDungeonReceipt? ApplyStrongway2019NextRoom(Strongway2019RoomPreparation prepared)
    {
        if (!CanMutatePartyDungeon || !CanApplyStrongway2019NextRoom(prepared) || formalDungeon is not { } dungeon) return null;
        var result = dungeon.Run.ApplyStrongway2019NextRoom(prepared.Replacement);
        if (result.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate)) return null;
        if (result.Outcome == DungeonRunOutcome.Accepted)
        {
            var replacement = prepared.Replacement.Plan;
            var route = dungeon.Route with { Rooms = Array.AsReadOnly(dungeon.Route.Rooms.Select(plan =>
                plan.Room.Map.Position == prepared.Replacement.Target && plan.Room.Map.Kind == prepared.Replacement.OriginalMap.Kind
                    ? replacement : plan).ToArray()) };
            // 保留原Run、Authority、Ground和已提交事实；正式回执与后续准备统一读取真实替换后的Entry/Route。
            formalDungeon = dungeon with { Entry = dungeon.Run.Entry, Route = route,
                NextObjectKey = Math.Max(dungeon.NextObjectKey, prepared.Replacement.NextObjectKey) };
        }
        return FormalReceipt();
    }

    internal DungeonRunOutcome ObserveStrongway2019RouteLoaded() => owner.Execute(() =>
    {
        if (!CanUseStrongway2019Combat() || formalDungeon is not { } dungeon ||
            strongway2019RoomPreparation is not { } prepared) return DungeonRunOutcome.Rejected;
        var result = CurrentStrongway2019Match!.Competition.ConfirmRouteApplied(dungeon.Run, dungeon.Run.Scene.Identity,
            prepared.Replacement.ChangeId);
        if (result is DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate) strongway2019RoomPreparation = null;
        return result;
    });

    bool CanUseStrongway2019Combat() => Ready && Selected is not null && Phase == SessionPhase.Dungeon &&
        strongway2019SelectionEpoch == SelectionEpoch && CurrentStrongway2019Match is { } match &&
        PartyDungeon is { } instance && (ReferenceEquals(instance, match.First) || ReferenceEquals(instance, match.Second)) &&
        formalDungeon is { Entry.DungeonId: 5360, Strongway2019Admission.Mode: Strongway2019Mode.Matching } dungeon &&
        !ScriptedNavigationBlocked(dungeon) &&
        match.Competition.Participants.Any(member => ReferenceEquals(member.Run, dungeon.Run) &&
            member.Identity.CharacterId == Selected.Id && member.Identity.InstanceId == instance.Id);

    void ResetStrongway2019Combat() => strongway2019RoomPreparation = null;
}
