using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    AntonRaidDungeonAdmission? antonDungeonAdmission;

    // 只能由已提交开战的领域协调者调用；授权仍随原owner/选角epoch与原实例一起失效。
    public bool AuthorizeAntonRaidDungeon(AntonRaidDungeonAdmission admission) => owner.Execute(() =>
    {
        if (!ReferenceEquals(admission.Actor.Session, this) || !ReferenceEquals(admission.Actor.Owner, owner) ||
            !admission.Current || ChannelEntry?.Identity != admission.Actor.Channel || RaidTypes.FromChannelType(ChannelEntry.Type) != 0 ||
            !Ready || Phase != SessionPhase.Town || formalDungeon is not null ||
            admission.Catalog.Script.WaitingRoom is not { } room || Selected?.Position is not { } position ||
            position.Town != room.Town || position.Area != room.Area) return false;
        antonDungeonAdmission = admission;
        return true;
    });

    bool HasAntonDungeonAdmission => antonDungeonAdmission is { } admission && admission.Current &&
        ReferenceEquals(admission.Actor.Session, this);

    bool IsCurrentAntonDungeon(DungeonEntryPlan entry) => entry.AntonRaid is not { } raid ||
        antonDungeonAdmission is { } admission && admission.InstanceId == raid.InstanceId && admission.Phase == raid.Phase &&
        admission.Squad == raid.Squad && admission.Current;

    bool CanInstallAntonDungeon(DungeonEntryPlan entry) => IsCurrentAntonDungeon(entry) &&
        (entry.AntonRaid is null || antonDungeonAdmission!.Allows(checked((int)entry.DungeonId)));

    DungeonSelectionReceipt? BeginAntonDungeonSelection(bool partyAdmission)
    {
        if (!HasAntonDungeonAdmission || Selected is not { } character ||
            antonDungeonAdmission!.Catalog.Script.WaitingRoom is not { } room ||
            character.Position.Town != room.Town || character.Position.Area != room.Area ||
            !partyAdmission && HasMultiplePartyMembers?.Invoke() == true) return null;
        if (CurrentDungeonSelection is null)
            CurrentDungeonSelection = new(++dungeonSelectionId, SelectionEpoch, character.Id, character.Position, character.Position);
        ValidateDungeonSelection();
        SetState(SessionPhase.MapSelect);
        return new(character, owner.Generation, SelectionEpoch, dungeonSelectionId);
    }

    PreparedFormalDungeon? PrepareAntonDungeon(uint dungeonId, uint questId, byte difficulty, DungeonRoomCatalog rooms,
        Func<int, int>? draw, Character? requiredCharacter, bool incompatibleMode, PartyDungeonPreparation? shared, uint? firstKey, Guid? partyInstance,
        bool partyAdmission)
    {
        var captured = owner.Execute(() => (Selected: Ready && Phase == SessionPhase.MapSelect && CanEnterFormalDungeon(null, partyAdmission) &&
            (requiredCharacter is null || ReferenceEquals(requiredCharacter, Selected)) ? Selected : null,
            SelectionEpoch, CurrentDungeonSelection, Admission: antonDungeonAdmission));
        var admission = captured.Admission;
        if (captured.Selected is not { } character || captured.CurrentDungeonSelection is null || admission is null ||
            incompatibleMode || questId != 0 || dungeonId > int.MaxValue || !admission.Allows((int)dungeonId))
            return RejectFormal("anton-entry-authorization-changed");
        var definition = admission.Catalog.Find((int)dungeonId);
        if (definition is null) return RejectFormal("anton-entry-resource-unregistered");
        var identity = new AntonRaidDungeonIdentity(admission.InstanceId, admission.Phase, admission.Squad);
        var sharedEntry = shared?.Session.PreparedParty(shared).Dungeon.Entry;
        if (sharedEntry is not null && sharedEntry.AntonRaid != identity)
            return RejectFormal("anton-entry-squad-mismatch");
        if (!DungeonEntryPlan.TryCreate(definition, character, dungeonId, 0, difficulty, out var entry, out string reason,
            draw: draw, resolveMaze: maze => admission.Catalog.ResolveMaze(maze, draw), partyRoute: sharedEntry))
            return RejectFormal("anton-entry-plan:" + reason);
        entry = entry! with
        {
            AntonRaid = identity,
            CoinLimit = definition.CoinLimit, AllowsCompanionAi = false
        };
        var prepared = PrepareFormalDungeonResources(character, captured.SelectionEpoch, entry, rooms, null, null, draw, null,
            shared: shared, partyFirstKey: firstKey, partyInstance: partyInstance);
        if (prepared is null) return null;
        return owner.Execute(() => ReferenceEquals(antonDungeonAdmission, admission) && admission.Allows((int)dungeonId) &&
            ReferenceEquals(Selected, character) && ReferenceEquals(CurrentDungeonSelection, captured.CurrentDungeonSelection) &&
            SelectionEpoch == captured.SelectionEpoch ? prepared : RejectFormal("anton-entry-authorization-changed"));
    }
}
