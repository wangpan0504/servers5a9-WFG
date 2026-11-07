using DfoServer.Game.Caching;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed record HelperPreferences(ushort CharacterId, long Epoch, long Generation, bool HellRotation, bool DirectPickup);
    static readonly GameCacheSlot<HelperPreferences> helperPreferencesSlot = new();
    static readonly GameCacheSlot<HelperCapabilityReceipt> helperDirectCapabilitySlot = new();
    sealed record HelperRotationCapture(FormalDungeon Source, Character Character, long Epoch,
        DungeonRunIdentity Identity, DungeonPoint Target, HelperPreferences Preferences, HelperHellRotation Rotation,
        ImmutableArray<DungeonPlannedMonster> Companions);

    internal sealed record HelperSettingsReceipt(ushort CharacterId, long Epoch, bool Accepted, bool HellRotation);
    internal sealed record HelperPickupReceipt(ushort CharacterId, long Epoch, long Generation, bool Accepted, bool Enabled);
    internal sealed record HelperCapabilityReceipt(ushort CharacterId, long Epoch, long Generation);
    internal sealed record HelperCargoReceipt(Character Character, long Epoch, long Generation,
        Guid? Instance, InventoryListType List, bool Accepted);

    internal HelperCapabilityReceipt? ReadHelperCapabilities(ushort characterId, bool directPickup = false) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || character.Id != characterId) return null;
        var receipt = new HelperCapabilityReceipt(character.Id, SelectionEpoch, owner.Generation);
        if (directPickup) gameCache.Set(helperDirectCapabilitySlot, receipt);
        return receipt;
    });

    internal bool CanPublishHelperCapabilities(HelperCapabilityReceipt receipt) => owner.Execute(() =>
        Ready && Selected?.Id == receipt.CharacterId && SelectionEpoch == receipt.Epoch && owner.Generation == receipt.Generation);

    internal HelperCargoReceipt? SetHelperPortableCargo(ushort characterId, InventoryListType list) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || character.Id != characterId) return null;
        bool accepted = list is InventoryListType.PersonalCargo or InventoryListType.AccountCargo &&
            Phase is SessionPhase.Town or SessionPhase.Dungeon or SessionPhase.Settlement &&
            pendingRental is null && !HasPendingTownQuestCommit && !HasPendingFormalCard &&
            formalDungeon?.PendingTutorialClear is null && formalDungeon?.PendingInventoryDiscard is null &&
            formalDungeon?.HasPendingFatigue != true && formalDungeon?.Run.HasPendingDeath != true &&
            formalDungeon?.Run.HasPendingSettlement != true && formalDungeon?.Ground.HasPendingOperation != true &&
            (list == InventoryListType.PersonalCargo ? character.Inventory.CargoCapacity > 0 : character.Inventory.AccountCargo.Capacity > 0) &&
            (Phase == SessionPhase.Town || formalDungeon is not null);
        if (accepted && formalDungeon is { } dungeon && Phase is SessionPhase.Dungeon or SessionPhase.Settlement)
        {
            // 只授权本次选中的真实仓库；不影响随身仓库道具原有的双仓库资格。
            var previous = portableCargo;
            bool same = previous is not null && previous.Generation == owner.Generation && previous.SelectionEpoch == SelectionEpoch &&
                previous.DungeonInstance == dungeon.Run.Snapshot.Identity.InstanceId &&
                previous.PersonalCapacity == character.Inventory.CargoCapacity && previous.AccountCapacity == character.Inventory.AccountCargo.Capacity;
            portableCargo = new(owner.Generation, SelectionEpoch, dungeon.Run.Snapshot.Identity.InstanceId,
                character.Inventory.CargoCapacity, character.Inventory.AccountCargo.Capacity,
                list == InventoryListType.PersonalCargo || same && previous!.Personal,
                list == InventoryListType.AccountCargo || same && previous!.Account);
        }
        return new HelperCargoReceipt(character, SelectionEpoch, owner.Generation,
            formalDungeon?.Run.Snapshot.Identity.InstanceId, list, accepted);
    });

    internal bool CanPublishHelperCargo(HelperCargoReceipt receipt) => owner.Execute(() =>
        Ready && ReferenceEquals(Selected, receipt.Character) && SelectionEpoch == receipt.Epoch && owner.Generation == receipt.Generation &&
        formalDungeon?.Run.Snapshot.Identity.InstanceId == receipt.Instance &&
        (!receipt.Accepted || Phase == SessionPhase.Town || CanAccessPortableCargo(receipt.List)));

    internal HelperSettingsReceipt? SetHelperHellRotation(ushort characterId, bool enabled) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || character.Id != characterId) return null;
        if (enabled && (PartyDungeon is not null || HasMultiplePartyMembers?.Invoke() == true ||
            Phase is not (SessionPhase.Town or SessionPhase.MapSelect or SessionPhase.Dungeon)))
            return new HelperSettingsReceipt(character.Id, SelectionEpoch, false, false);
        var previous = CurrentHelperPreferences(character.Id);
        gameCache.Set(helperPreferencesSlot, new(character.Id, SelectionEpoch, owner.Generation, enabled, previous?.DirectPickup == true));
        return new HelperSettingsReceipt(character.Id, SelectionEpoch, true, enabled);
    });

    HelperPreferences? CurrentHelperPreferences(ushort characterId) => gameCache.Get(helperPreferencesSlot) is { } value &&
        value.CharacterId == characterId && value.Epoch == SelectionEpoch && value.Generation == owner.Generation ? value : null;

    internal HelperPickupReceipt? SetHelperDirectPickup(ushort characterId, bool enabled) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || character.Id != characterId) return null;
        var previous = CurrentHelperPreferences(character.Id);
        var capability = gameCache.Get(helperDirectCapabilitySlot);
        if (enabled && (capability is null || capability.CharacterId != character.Id ||
            capability.Epoch != SelectionEpoch || capability.Generation != owner.Generation))
            return new HelperPickupReceipt(character.Id, SelectionEpoch, owner.Generation, false, previous?.DirectPickup == true);
        gameCache.Set(helperPreferencesSlot, new(character.Id, SelectionEpoch, owner.Generation, previous?.HellRotation == true, enabled));
        return new HelperPickupReceipt(character.Id, SelectionEpoch, owner.Generation, true, enabled);
    });

    internal bool CanPublishHelperPickup(HelperPickupReceipt receipt) => owner.Execute(() =>
        Ready && Selected?.Id == receipt.CharacterId && SelectionEpoch == receipt.Epoch && owner.Generation == receipt.Generation);

    bool HelperDirectPickupEnabled => Selected is { } character && CurrentHelperPreferences(character.Id)?.DirectPickup == true;

    internal bool CanPublishHelperSettings(HelperSettingsReceipt receipt) => owner.Execute(() =>
        Ready && Selected?.Id == receipt.CharacterId && SelectionEpoch == receipt.Epoch);

    bool CanRotateHelperHell(HelperRotationCapture capture) => Ready && Phase == SessionPhase.Dungeon &&
        PartyDungeon is null && HasMultiplePartyMembers?.Invoke() != true && !HasPendingTownQuestCommit &&
        ReferenceEquals(Selected, capture.Character) && SelectionEpoch == capture.Epoch &&
        ReferenceEquals(formalDungeon, capture.Source) && ReferenceEquals(gameCache.Get(helperPreferencesSlot), capture.Preferences) &&
        capture.Preferences is { HellRotation: true } && capture.Preferences.CharacterId == capture.Character.Id &&
        capture.Preferences.Epoch == capture.Epoch && capture.Preferences.Generation == owner.Generation && !capture.Source.CharacterDead &&
        !capture.Source.Ground.HasPendingOperation && capture.Source.PendingTutorialClear is null &&
        !HasPendingQuestScene && !HasPendingFormalCard && !capture.Source.Run.HasPendingDeath &&
        HelperHellRotation.Supports(capture.Source.Entry) &&
        capture.Source.Entry.HellParty is { } hell && capture.Source.Run.Snapshot.CurrentMap.Id == hell.MapId &&
        capture.Source.Run.Snapshot.CurrentMap.Position == hell.Room &&
        capture.Character.Position.Town == capture.Rotation.Town && capture.Character.Position.Area == capture.Rotation.Area &&
        capture.Source.Run.LivingPublishedCompanions().SequenceEqual(capture.Companions) &&
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
                new(x, y), preferences, rotation, source.Run.LivingPublishedCompanions());
        });
        if (capture is null) return (false, null);
        LastFormalDungeonRejection = null;
        if (!owner.Execute(() => CanRotateHelperHell(capture))) return Rejected("helper-hell-door-not-ready");

        if (!towns.Areas.TryGetValue((capture.Rotation.Town, capture.Rotation.Area), out var area) ||
            area.DungeonWorldMap is not { } world || !towns.DungeonMaps.TryGetValue(world, out var selection) ||
            selection.Error is not null || !selection.HellDungeon) return Rejected("helper-hell-area-unavailable");
        var candidates = new List<uint>();
        var done = QuestEligibility.Completed(capture.Character);
        // 任务专用入口可能也带柱子资源，但不能作为普通深渊的后继图。
        foreach (var id in selection.Options.Where(option => !option.InProgress && option.DungeonId > 0)
            .Select(option => option.DungeonId).Distinct())
        {
            if (SelectionRejection(capture.Character, done, checked((uint)id)) is not null) continue;
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
