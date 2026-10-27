using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using System.Collections.Immutable;

namespace DfoServer.Game.Session;

internal sealed record EvildomWarAdmission(EvildomWarCatalog Catalog, PartySnapshot Party, long Epoch,
    ushort CharacterId, byte Mode, DateTimeOffset Deadline, ImmutableArray<EvildomWarDungeonState> Dungeons)
{
    public bool Charged { get; set; }
    public uint? SelectionDungeon { get; set; }
}

internal sealed partial class GameSession
{
    EvildomWarAdmission? evildomWar;

    internal string? EvildomWarStartRejection(EvildomWarCatalog catalog, PartySnapshot party) => owner.Execute(() =>
    {
        if (!Ready || ChannelEntry?.Type != 54 || Selected is not { } character || Phase != SessionPhase.Town ||
            character.Position.Town != catalog.WaitingArea.Town || character.Position.Area != catalog.WaitingArea.Area ||
            formalDungeon is not null || pendingRental is not null || HasPendingTownQuestCommit)
            return "waiting-area-or-state-required";
        if (party.Settings.Kind != 5 || party.Settings.KindArgument > byte.MaxValue ||
            !catalog.MemberLimits.TryGetValue((byte)party.Settings.KindArgument, out int limit) || party.Members.Length != limit)
            return "party-mode-or-member-limit";
        byte mode = (byte)party.Settings.KindArgument;
        if (catalog.Dungeons.Where(row => row.Mode == mode).Any(row => character.Level < row.MinimumLevel)) return "level-required";
        if (CurrentEvildomWar(character) is not null) return "already-started";
        return EvildomWarCostRejection(character, catalog);
    });

    string? EvildomWarCostRejection(Character character, EvildomWarCatalog catalog)
    {
        if (incounts?.Find(catalog.IncountDungeon) is not { } row || row.Limit != catalog.DailyLimit || row.WeeklyLimit != catalog.WeeklyLimit)
            return "incount-resource-unavailable";
        if (!DungeonIncountPolicy.CanEnter(row, repository.LoadDungeonIncount(owner, character.Id, row.CountKey, IncountPeriod(row.CountKey))))
            return "incount-exhausted";
        if (debugSettings?.InfiniteFatigue != true && FatigueForClient(character).Remaining < catalog.NeedFatigue)
            return "fatigue-required";
        return null;
    }

    internal void StartEvildomWar(EvildomWarCatalog catalog, PartySnapshot party, DateTimeOffset deadline,
        ImmutableArray<EvildomWarDungeonState> dungeons) => owner.Execute(() =>
    {
        evildomWar = new(catalog, party, SelectionEpoch, Selected!.Id, checked((byte)party.Settings.KindArgument), deadline, dungeons);
        return true;
    });

    internal DateTimeOffset EvildomWarNow => (timeProvider ?? TimeProvider.System).GetUtcNow();

    EvildomWarAdmission? CurrentEvildomWar(Character character) => evildomWar is { } war &&
        war.Epoch == SelectionEpoch && war.CharacterId == character.Id && ChannelEntry?.Type == 54 &&
        character.Position.Town == war.Catalog.WaitingArea.Town && character.Position.Area == war.Catalog.WaitingArea.Area &&
        ReferenceEquals(CurrentParty?.Invoke(), war.Party) && (timeProvider ?? TimeProvider.System).GetUtcNow() < war.Deadline ? war : null;

    internal EvildomWarAdmission? ReadEvildomWar() => owner.Execute(() => Selected is { } character ? CurrentEvildomWar(character) : null);

    internal bool CanBeginEvildomWarSelection(uint dungeon) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || Phase is not (SessionPhase.Town or SessionPhase.MapSelect) ||
            formalDungeon is not null || pendingRental is not null || HasPendingTownQuestCommit || CurrentEvildomWar(character) is not { } war ||
            !war.Dungeons.Any(row => row.Id == dungeon && row.State == 0)) return false;
        return true;
    });

    internal DungeonSelectionReceipt? BeginEvildomWarSelection(uint dungeon) => owner.Execute(() =>
    {
        if (!CanBeginEvildomWarSelection(dungeon) || Selected is not { } character) return null;
        evildomWar!.SelectionDungeon = dungeon;
        CurrentDungeonSelection ??= new(++dungeonSelectionId, SelectionEpoch, character.Id, character.Position, character.Position);
        ValidateDungeonSelection();
        SetState(SessionPhase.MapSelect);
        return new DungeonSelectionReceipt(character, owner.Generation, SelectionEpoch, dungeonSelectionId);
    });
}
