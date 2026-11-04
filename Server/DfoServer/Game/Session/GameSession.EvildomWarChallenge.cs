using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 准备在owner外解析房间，提交前必须仍是原一至四人名册与原选图票据。
    sealed record EvildomWarChallengeAdmission(Character Character, long Epoch,
        DungeonSelectionContext Selection, PartySnapshot Party);

    static bool HasEvildomWarChallengeRoster(PartySnapshot? party) => party is { Members.Length: >= 1 and <= EvildomWarChallengeEntryPolicy.PartyCount } &&
        party.Members.All(member => member.Slot < EvildomWarChallengeEntryPolicy.PartyCount) &&
        party.Members.Select(member => member.Slot).Distinct().Count() == party.Members.Length &&
        party.Members.Select(member => member.Actor.Character.Id).Distinct().Count() == party.Members.Length &&
        party.Members.Select(member => member.Actor.Owner.Account).Distinct(StringComparer.Ordinal).Count() == party.Members.Length &&
        party.Members.Any(member => member.Slot == party.Leader) && party.EvildomWarBattleId is null;

    bool HasCurrentEvildomWarChallengeSeat(PartySnapshot? party) => HasEvildomWarChallengeRoster(party) &&
        party!.Members.Any(member => ReferenceEquals(member.Actor.Session, this) &&
            member.Actor.Epoch == SelectionEpoch && member.IsCurrent());

    bool CanUseEvildomWarChallengeEntrance() => Selected is not { Position.Town: ChannelAreaLanding.EvildomWarChallengeTown } ||
        ChannelEntry?.Type == ChannelAreaLanding.EvildomWarChallengeType && Selected is { Level: >= 95, Position.Area: 2 } &&
        HasCurrentEvildomWarChallengeSeat(CurrentParty?.Invoke());

    EvildomWarChallengeAdmission? PrepareEvildomWarChallengeAdmission(DungeonDefinition definition, Character expected,
        long epoch, DungeonSelectionContext? selection, byte difficulty, int partyCount, bool partyAdmission, bool specialMode)
    {
        string? rejection = EvildomWarChallengeEntryPolicy.Rejection(definition, expected, ChannelEntry?.Type,
            selection, epoch, difficulty, partyCount, partyAdmission, specialMode);
        if (rejection is not null) { LastFormalDungeonRejection = rejection; return null; }
        var party = CurrentParty?.Invoke();
        if (!ReferenceEquals(Selected, expected) || SelectionEpoch != epoch || Phase != SessionPhase.MapSelect ||
            !ReferenceEquals(CurrentDungeonSelection, selection) || !HasCurrentEvildomWarChallengeSeat(party))
        { LastFormalDungeonRejection = "evildom-war-challenge-party-or-selection-changed"; return null; }
        if (!towns.Areas.TryGetValue((expected.Position.Town, expected.Position.Area), out var area) ||
            area.DungeonWorldMap != EvildomWarChallengeEntryPolicy.WorldMap)
        { LastFormalDungeonRejection = "evildom-war-challenge-world-map-required"; return null; }
        return new(expected, epoch, selection!, party!);
    }

    bool IsCurrentEvildomWarChallengeAdmission(EvildomWarChallengeAdmission admission) => Ready &&
        ReferenceEquals(Selected, admission.Character) && SelectionEpoch == admission.Epoch && Phase == SessionPhase.MapSelect &&
        ReferenceEquals(CurrentDungeonSelection, admission.Selection) && ReferenceEquals(CurrentParty?.Invoke(), admission.Party) &&
        HasCurrentEvildomWarChallengeSeat(admission.Party) &&
        ChannelEntry?.Type == ChannelAreaLanding.EvildomWarChallengeType &&
        Selected is { Level: >= 95, Position: { Town: ChannelAreaLanding.EvildomWarChallengeTown, Area: 2 } } &&
        admission.Selection.OriginPosition == Selected.Position;
}
