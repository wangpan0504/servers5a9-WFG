using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    RaidDungeonAdmission? raidDungeonAdmission;

    // 只能由已提交开战的领域协调者调用；授权仍随原owner/选角epoch与原实例一起失效。
    public bool AuthorizeRaidDungeon(RaidDungeonAdmission admission) => owner.Execute(() =>
    {
        if (admission.Kind != RaidKind.Anton || !ReferenceEquals(admission.Actor.Session, this) || !ReferenceEquals(admission.Actor.Owner, owner) ||
            !admission.Current || ChannelEntry?.Identity != admission.Actor.Channel || RaidTypes.FromChannelType(ChannelEntry.Type) != (byte)admission.Kind ||
            !Ready || !(Phase == SessionPhase.Town || admission.Kind == RaidKind.Luke && Phase == SessionPhase.MapSelect) || formalDungeon is not null ||
            admission.Catalog.Script.WaitingRoom is not { } room || Selected?.Position is not { } position ||
            position.Town != room.Town || position.Area != room.Area) return false;
        raidDungeonAdmission = admission;
        return true;
    });

    internal bool CanBeginRaidDungeonSelection(RaidDungeonAdmission admission) => owner.Execute(() =>
        admission.Current && admission.Battle is not null && ReferenceEquals(admission.Actor.Session, this) &&
        ReferenceEquals(CurrentRaidBattle(admission.Actor), admission.Battle) && StartedRaid?.InstanceId == admission.InstanceId &&
        ReferenceEquals(admission.Actor.Owner, owner) && ChannelEntry?.Identity == admission.Actor.Channel &&
        RaidTypes.FromChannelType(ChannelEntry.Type) == (byte)admission.Kind && Ready && Phase is SessionPhase.Town or SessionPhase.MapSelect &&
        formalDungeon is null && pendingRental is null && !HasPendingTownQuestCommit &&
        admission.Catalog.Script.WaitingRoom is { } room && Selected?.Position is { } position &&
        position.Town == room.Town && position.Area == room.Area);

    internal DungeonSelectionReceipt InstallRaidDungeonSelection(RaidDungeonSelection selection) => owner.Execute(() =>
    {
        if (!CanBeginRaidDungeonSelection(selection.Admission))
            throw new InvalidOperationException("团本选图安装前身份已变化");
        raidDungeonAdmission = selection.Admission;
        CurrentDungeonSelection = new(++dungeonSelectionId, SelectionEpoch, Selected!.Id, Selected.Position, Selected.Position)
            { Raid = selection };
        SetState(SessionPhase.MapSelect);
        return new DungeonSelectionReceipt(Selected, owner.Generation, SelectionEpoch, dungeonSelectionId);
    });

    internal bool HasLukeRaidSelection => CurrentDungeonSelection?.Raid?.Admission.Kind == RaidKind.Luke;
    internal bool HasFormalRaidSelection => CurrentDungeonSelection?.Raid is not null;
    internal void CancelRaidDungeonSelection(PartyDungeonNotice[] selections)
    {
        // 仅清本次选择令牌；事务成功安装的Run和后来建立的选择均不回滚。
        foreach (var notice in selections)
            notice.Recipient.Actor.Owner.Execute(() =>
            {
                var session = notice.Recipient.Actor.Session;
                if (notice.Selection is not { } receipt || session.formalDungeon is not null ||
                    session.CurrentDungeonSelection?.Id != receipt.SelectionId || session.SelectionEpoch != receipt.SelectionEpoch) return false;
                session.CurrentDungeonSelection = session.CurrentDungeonSelection with { Raid = null };
                session.raidDungeonAdmission = null;
                return true;
            });
    }
    internal void ClearLukeRaidSelection() => owner.Execute(() =>
    {
        if (CurrentDungeonSelection?.Raid is null || formalDungeon is not null) return false;
        CurrentDungeonSelection = null;
        raidDungeonAdmission = null;
        if (Phase == SessionPhase.MapSelect) SetState(SessionPhase.Town);
        return true;
    });

    bool HasRaidDungeonAdmission => raidDungeonAdmission is { } admission && admission.Current &&
        ReferenceEquals(admission.Actor.Session, this);

    bool IsCurrentRaidDungeon(DungeonEntryPlan entry) => entry.RaidEntry is not { } raid ||
        raidDungeonAdmission is { } admission && admission.InstanceId == raid.InstanceId && admission.Phase == raid.Phase &&
        admission.Squad == raid.Squad && admission.Kind == raid.Kind && admission.Current;

    bool CanInstallRaidDungeon(DungeonEntryPlan entry) => IsCurrentRaidDungeon(entry) &&
        (entry.RaidEntry is null || raidDungeonAdmission!.Allows(checked((int)entry.DungeonId)));

    DungeonSelectionReceipt? BeginRaidDungeonSelection(bool partyAdmission)
    {
        if (!HasRaidDungeonAdmission || Selected is not { } character ||
            raidDungeonAdmission!.Catalog.Script.WaitingRoom is not { } room ||
            character.Position.Town != room.Town || character.Position.Area != room.Area ||
            !partyAdmission && HasMultiplePartyMembers?.Invoke() == true) return null;
        if (CurrentDungeonSelection is null)
            CurrentDungeonSelection = new(++dungeonSelectionId, SelectionEpoch, character.Id, character.Position, character.Position);
        ValidateDungeonSelection();
        SetState(SessionPhase.MapSelect);
        return new(character, owner.Generation, SelectionEpoch, dungeonSelectionId);
    }

    PreparedFormalDungeon? PrepareRaidDungeon(uint dungeonId, uint questId, byte difficulty, DungeonRoomCatalog rooms,
        Func<int, int>? draw, Character? requiredCharacter, bool incompatibleMode, PartyDungeonPreparation? shared, uint? firstKey, Guid? partyInstance,
        bool partyAdmission)
    {
        var captured = owner.Execute(() => (Selected: Ready && Phase == SessionPhase.MapSelect && CanEnterFormalDungeon(null, partyAdmission) &&
            (requiredCharacter is null || ReferenceEquals(requiredCharacter, Selected)) ? Selected : null,
            SelectionEpoch, CurrentDungeonSelection, Admission: raidDungeonAdmission));
        var admission = captured.Admission;
        string prefix = admission?.Kind switch { RaidKind.Luke => "luke", RaidKind.Anton => "anton", _ => "raid" };
        if (captured.Selected is not { } character || captured.CurrentDungeonSelection is null || admission is null ||
            incompatibleMode || questId != 0 || dungeonId > int.MaxValue || !admission.Allows((int)dungeonId))
            return RejectFormal(prefix + "-entry-authorization-changed");
        var definition = admission.Catalog.Find((int)dungeonId);
        if (definition is null) return RejectFormal(prefix + "-entry-resource-unregistered");
        var selected = captured.CurrentDungeonSelection.Raid;
        bool controlled = admission.Kind == RaidKind.Luke || admission.Battle is not null;
        if (controlled && (selected is null || !ReferenceEquals(selected.Admission, admission) || selected.Dungeon != dungeonId ||
            admission.Kind == RaidKind.Luke && admission.LukePolicy?.Maze(admission.Phase, selected.Dungeon) != selected.MazeOrdinal))
            return RejectFormal(prefix + "-entry-selection-changed");
        var identity = new RaidDungeonIdentity(admission.InstanceId, admission.Phase, admission.Squad, admission.Kind);
        var sharedEntry = shared?.Session.PreparedParty(shared).Dungeon.Entry;
        if (sharedEntry is not null && sharedEntry.RaidEntry != identity)
            return RejectFormal(prefix + "-entry-squad-mismatch");
        if (!DungeonEntryPlan.TryCreate(definition, character, dungeonId, 0, difficulty, out var entry, out string reason,
            draw: draw, resolveMaze: maze => admission.Catalog.ResolveMaze(maze, draw), partyRoute: sharedEntry, raid: controlled ? identity : null, raidMaze: selected?.MazeOrdinal))
            return RejectFormal(prefix + "-entry-plan:" + reason);
        entry = entry! with
        {
            RaidEntry = identity,
            CoinLimit = controlled ? 0 : definition.CoinLimit, AllowsCompanionAi = false,
            DisableMonsterAndClearExperience = controlled || entry!.DisableMonsterAndClearExperience,
            DisableClearReward = controlled || entry.DisableClearReward,
            DisableFatigueConsumption = controlled || entry.DisableFatigueConsumption,
            DisableGiveupPenalty = controlled || entry.DisableGiveupPenalty
        };
        var prepared = PrepareFormalDungeonResources(character, captured.SelectionEpoch, entry, rooms, null, null, draw, null,
            shared: shared, partyFirstKey: firstKey, partyInstance: partyInstance);
        if (prepared is null) return null;
        return owner.Execute(() => ReferenceEquals(raidDungeonAdmission, admission) && admission.Allows((int)dungeonId) &&
            ReferenceEquals(Selected, character) && ReferenceEquals(CurrentDungeonSelection, captured.CurrentDungeonSelection) &&
            SelectionEpoch == captured.SelectionEpoch ? prepared : RejectFormal(prefix + "-entry-authorization-changed"));
    }
}
