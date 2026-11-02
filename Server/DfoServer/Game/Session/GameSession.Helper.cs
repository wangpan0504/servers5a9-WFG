using DfoServer.Game.Caching;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed record HelperPreferences(ushort CharacterId, long Epoch, bool HellRotation);
    static readonly GameCacheSlot<HelperPreferences> helperPreferencesSlot = new();
    sealed record HelperRotationCapture(FormalDungeon Source, Character Character, long Epoch,
        DungeonRunIdentity Identity, DungeonPoint Target, HelperPreferences Preferences, HelperHellRotation Rotation);

    internal sealed record HelperSettingsReceipt(ushort CharacterId, long Epoch, bool Accepted, bool HellRotation);

    internal HelperSettingsReceipt? SetHelperHellRotation(ushort characterId, bool enabled) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || character.Id != characterId) return null;
        if (enabled && (PartyDungeon is not null || HasMultiplePartyMembers?.Invoke() == true ||
            Phase is not (SessionPhase.Town or SessionPhase.MapSelect or SessionPhase.Dungeon)))
            return new HelperSettingsReceipt(character.Id, SelectionEpoch, false, false);
        gameCache.Set(helperPreferencesSlot, new(character.Id, SelectionEpoch, enabled));
        return new HelperSettingsReceipt(character.Id, SelectionEpoch, true, enabled);
    });

    internal bool CanPublishHelperSettings(HelperSettingsReceipt receipt) => owner.Execute(() =>
        Ready && Selected?.Id == receipt.CharacterId && SelectionEpoch == receipt.Epoch);

    bool CanRotateHelperHell(HelperRotationCapture capture) => Ready && Phase == SessionPhase.Dungeon &&
        PartyDungeon is null && HasMultiplePartyMembers?.Invoke() != true && !HasPendingTownQuestCommit &&
        ReferenceEquals(Selected, capture.Character) && SelectionEpoch == capture.Epoch &&
        ReferenceEquals(formalDungeon, capture.Source) && ReferenceEquals(gameCache.Get(helperPreferencesSlot), capture.Preferences) &&
        capture.Preferences is { HellRotation: true } && capture.Preferences.CharacterId == capture.Character.Id &&
        capture.Preferences.Epoch == capture.Epoch && !capture.Source.CharacterDead &&
        !capture.Source.Ground.HasPendingOperation && capture.Source.PendingTutorialClear is null &&
        !HasPendingQuestScene && !HasPendingFormalCard && !capture.Source.Run.HasPendingDeath &&
        HelperHellRotation.Supports(capture.Source.Entry) &&
        capture.Source.Entry.HellParty is { } hell && capture.Source.Run.Snapshot.CurrentMap.Id == hell.MapId &&
        capture.Source.Run.Snapshot.CurrentMap.Position == hell.Room &&
        capture.Character.Position.Town == capture.Rotation.Town && capture.Character.Position.Area == capture.Rotation.Area &&
        capture.Source.Run.CanMove(capture.Identity, capture.Target);

    // null/false 表示普通过门；true/null 表示轮换已适用但被拒绝，必须留在旧房，不能降级成普通过门。
    internal (bool Attempted, FormalDungeonReceipt? Receipt) RotateHelperHell(byte x, byte y, byte moveKind,
        DungeonCatalog catalog, DungeonRoomCatalog rooms, DungeonMonsterDropCatalog? drops = null, ItemCatalog? items = null,
        EpicPieceCatalog? pieces = null,
        Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? prepareDrops = null)
    {
        var capture = owner.Execute(() =>
        {
            if (moveKind != 0 || !Ready || Selected is not { } character || Phase != SessionPhase.Dungeon ||
                formalDungeon is not { } source || !HelperHellRotation.Supports(source.Entry) ||
                gameCache.Get(helperPreferencesSlot) is not { HellRotation: true } preferences ||
                preferences.CharacterId != character.Id || preferences.Epoch != SelectionEpoch ||
                source.Entry.HellParty is not { } hell || source.Run.Snapshot.CurrentMap.Id != hell.MapId ||
                source.Run.Snapshot.CurrentMap.Position != hell.Room) return null;
            var rotation = source.Entry.HelperRotation ?? new(character.Position.Town, character.Position.Area,
                source.Entry.BasisLevel, source.Entry.MonsterBasisLevel, source.Entry.Difficulty, source.Entry.HellPartyMode,
                source.Run.BattleLevels.Freeze(source.Entry.DungeonId, source.Run.Snapshot.CurrentMap));
            return new HelperRotationCapture(source, character, SelectionEpoch, source.Run.Snapshot.Identity,
                new(x, y), preferences, rotation);
        });
        if (capture is null) return (false, null);
        LastFormalDungeonRejection = null;
        if (!owner.Execute(() => CanRotateHelperHell(capture))) return Rejected("helper-hell-door-not-ready");

        if (!towns.Areas.TryGetValue((capture.Rotation.Town, capture.Rotation.Area), out var area) ||
            area.DungeonWorldMap is not { } world || !towns.DungeonMaps.TryGetValue(world, out var selection) ||
            selection.Error is not null || !selection.HellDungeon) return Rejected("helper-hell-area-unavailable");
        var candidates = new List<uint>();
        foreach (var id in selection.Options.Select(option => option.DungeonId).Where(id => id > 0).Distinct())
        {
            try
            {
                var definition = catalog.Get(id);
                if (!definition.IsRaid && definition.Mazes.Any(maze => maze.QuestConnectionTokens.Count == 0 &&
                    maze.HellParty is { MapId: > 0 } && maze.HellPartyPath is not null)) candidates.Add(checked((uint)id));
            }
            catch (Exception error) when (error is InvalidDataException or FileNotFoundException or NotSupportedException)
            {
                // 无合法柱子资源的地下城不进入轮换；不修改地区原选图目录。
            }
        }
        if (HelperHellRotation.Next(candidates, capture.Source.Entry.DungeonId) is not { } next)
            return Rejected("helper-hell-no-other-dungeon");
        var prepared = PrepareFormalDungeon(next, 0, capture.Rotation.Difficulty, catalog, rooms, drops, items, null,
            pieces, capture.Character, null, prepareDrops, hellParty: true, helperRotation: capture);
        if (prepared is null) return (true, null);
        var receipt = owner.Execute(() => CanRotateHelperHell(capture) ? InstallFormalDungeon(prepared) : null);
        if (receipt is null) LastFormalDungeonRejection ??= "helper-hell-state-changed-before-install";
        return (true, receipt);

        (bool, FormalDungeonReceipt?) Rejected(string reason)
        {
            LastFormalDungeonRejection = reason;
            return (true, null);
        }
    }
}
