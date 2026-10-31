using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed class FiendwarDungeonService(RaidRegistry registry, PartyService parties, RaidDungeonCatalog catalog, RaidDungeonCatalog? guideCatalog = null)
{
    internal RaidParty? Find(GameSession session) => registry.FindCurrent(session, session.SelectionEpoch);
    internal RaidParty EncounterMembers(GameSession session, RaidParty raid)
    {
        var actor = raid.Members.Single(member => ReferenceEquals(member.Actor.Session, session)).Actor;
        var battle = session.CurrentRaidBattle(actor);
        return battle is null ? raid : raid with { Members = raid.Members.Where(member =>
            battle.FiendwarPosition(member.Actor.Character) == battle.FiendwarPosition(actor.Character)).ToImmutableArray() };
    }
    internal PartySnapshot? Group(GameSession initiator, int dungeon, out string? reason)
    {
        reason = "fiendwar-encounter-not-ready";
        if (Find(initiator) is not { Type: 2, Started: { } start } raid ||
            raid.Members.SingleOrDefault(member => ReferenceEquals(member.Actor.Session, initiator)) is not { } anchor ||
            initiator.CurrentRaidBattle(anchor.Actor) is not { Kind: RaidKind.Fiendwar } battle || battle.Instance != start.InstanceId ||
            battle.FiendwarAreaForDungeon(dungeon) is not { } area || battle.FiendwarPosition(anchor.Actor.Character) != area ||
            !battle.AllowsFiendwarEntry(dungeon)) return null;
        var members = raid.Members.Where(member => member.Actor.IsCurrent && battle.FiendwarPosition(member.Actor.Character) == area)
            .OrderBy(member => member == anchor ? 0 : 1).ThenBy(member => member.Slot).ToArray();
        if (members.Length is < 1 or > 4 || members.Any(member => member.Actor.Session.Phase != SessionPhase.Town)) return null;
        var grouped = AccountOwners.Lease.ExecuteGroup(members.Select(member => (member.Actor.Owner, member.Actor.Character)).ToArray(),
            () => registry.IsCurrent(raid) && members.All(member => member.Actor.IsCurrent &&
                battle.FiendwarPosition(member.Actor.Character) == area && member.Actor.Session.Selected?.Position is { Town: 57 } position && position.Area == area)
                ? parties.GroupFiendwar(battle.Instance, members.Select(member => member.Actor).ToArray()) : null);
        if (grouped is not { } result) return null;
        parties.Publish(result.Notices);
        reason = null;
        return result.Party;
    }

    internal PartyDungeonNotice[] Select(GameSession initiator, int dungeon, PartySnapshot party, out string? reason, bool replay = false)
    {
        reason = "fiendwar-selection-changed";
        if (Find(initiator) is not { Type: 2, Started: { } start } raid || !parties.IsCurrent(party) || party.FiendwarBattleId != start.InstanceId ||
            !party.Members.Any(member => member.Slot == party.Leader && ReferenceEquals(member.Actor.Session, initiator))) return [];
        var actors = party.Members.OrderBy(member => member.Slot).Select(member => raid.Members.SingleOrDefault(row =>
            row.Actor.Session == member.Actor.Session && row.Actor.Character == member.Actor.Character.Id && row.Actor.Epoch == member.Actor.Epoch)?.Actor).ToArray();
        if (actors.Any(actor => actor is null)) return [];
        var anchor = actors[0]!;
        var battle = initiator.CurrentRaidBattle(anchor);
        if (battle is not { Kind: RaidKind.Fiendwar } || battle.Instance != start.InstanceId ||
            battle.FiendwarAreaForDungeon(dungeon) is not { } area || actors.Any(actor => battle.FiendwarPosition(actor!.Character) != area)) return [];
        var activeCatalog = raid.Guide ? guideCatalog : catalog;
        if (activeCatalog is not { Ready: true } || battle.FiendwarDefinition?.Guide != raid.Guide) return [];
        string? rejection = "fiendwar-selection-changed";
        var notices = AccountOwners.Lease.ExecuteGroup(actors.Select(actor => (actor!.Owner, actor.Character)).ToArray(), () =>
        {
            if (!parties.IsCurrent(party) || !registry.IsCurrent(raid) || actors.Any(actor => !actor!.IsCurrent)) return [];
            if (replay)
            {
                if (initiator.ReplayAntonLoading(dungeon) is not { } receipt) return [];
                if (actors.Any(actor => actor!.Session.ReplayAntonLoading(dungeon) is not { } member ||
                    member.Snapshot.Identity.InstanceId != receipt.Snapshot.Identity.InstanceId ||
                    !ReferenceEquals(actor.Session.PartyDungeon, initiator.PartyDungeon))) return [];
                return new[] { new PartyDungeonNotice(PartyDungeonNoticeKind.LoadingReplay, party.Members[0], party.Members, receipt) };
            }
            if (!battle.AllowsFiendwarEntry(dungeon) || activeCatalog.Find(dungeon) is not { } definition) return [];
            var mazes = definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0).Select(maze => maze.SourceOrdinal).ToArray();
            if (battle.FiendwarMaze(dungeon, mazes) is not { } ordinal) { rejection = "fiendwar-maze-unavailable"; return []; }
            byte encounterKey = checked((byte)(raid.Members.Single(member => member.Actor == anchor).Slot + 1));
            var scale = battle.FiendwarEntryScale(dungeon, actors.Select(actor => actor!.Character));
            var fromTown = actors.ToDictionary(actor => actor!.Character, actor => actor!.Session.Phase == SessionPhase.Town);
            var selections = actors.Select(actor => new RaidDungeonSelection(new(start.InstanceId, actor!, 0, encounterKey, activeCatalog,
                () => actor!.IsCurrent && registry.Find(actor)?.Started?.InstanceId == start.InstanceId,
                battle.AllowsFiendwarEntry, RaidKind.Fiendwar) { Battle = battle, FiendwarScale = scale }, dungeon, ordinal)).ToArray();
            if (selections.Any(selection => !selection.Admission.Actor.Session.CanBeginRaidDungeonSelection(selection.Admission))) return [];
            return selections.Select(selection => new PartyDungeonNotice(PartyDungeonNoticeKind.RaidSelection,
                party.Members.Single(member => member.Actor.Session == selection.Admission.Actor.Session), party.Members,
                Selection: selection.Admission.Actor.Session.InstallRaidDungeonSelection(selection),
                SceneTransition: fromTown[selection.Admission.Actor.Character])).ToArray();
        });
        reason = notices.Length == 0 ? rejection : null;
        return notices;
    }

    internal void Travelled(GameSession session)
    {
        if (Find(session) is not { Type: 2, Started: { } start } raid) return;
        parties.Publish(parties.LeaveFiendwarArea(session, start.InstanceId));
        var actor = raid.Members.SingleOrDefault(member => member.Actor.Session == session)?.Actor;
        if (actor is not null && session.CurrentRaidBattle(actor) is { } battle) battle.Publish(battle.View);
    }
}
