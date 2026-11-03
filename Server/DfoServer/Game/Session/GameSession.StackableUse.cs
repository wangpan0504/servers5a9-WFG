using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public bool CanUseStackablePlace(Character character, long epoch, SessionPhase phase, IReadOnlyList<string>? places) => owner.Execute(() =>
        ReferenceEquals(Selected, character) && SelectionEpoch == epoch && Phase == phase &&
        new StackableUseRules(places?.ToImmutableArray() ?? []).Check(CurrentStackableUseContext(character)) == StackableUseRejection.None);

    public StackableUseRejection CheckStackableUse(Character character, long epoch, SessionPhase phase,
        StackableUseRules rules, ItemWorlds? targetWorlds = null) => owner.Execute(() =>
            ReferenceEquals(Selected, character) && SelectionEpoch == epoch && Phase == phase
                ? rules.Check(CurrentStackableUseContext(character), targetWorlds) : StackableUseRejection.Condition);

    (StackableUseRules Rules, ItemWorlds? TargetWorlds) EffectRules(Character character, int? sourceId, InventorySlot? targetSlot)
    {
        if (sourceId is not > 0) return (StackableUseRules.Unrestricted, null);
        var rules = StackableRules(sourceId.Value);
        ItemWorlds? worlds = null;
        try
        {
            if (items is not null && targetSlot is { } slot && character.Inventory.Find(slot) is { } target)
                worlds = items.UsableWorlds(target.Core.ItemId);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException) { }
        return (rules, worlds);
    }

    StackableUseRules StackableRules(int itemId, IReadOnlyList<string>? places = null, ItemCatalog? catalog = null)
    {
        try { return (catalog ?? items)?.StackableUseRules(itemId) ?? new(places?.ToImmutableArray() ?? []); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
        { return new(places?.ToImmutableArray() ?? []); }
    }

    internal bool CanUseStackableItem(Character character, int itemId, ItemCatalog? catalog = null,
        IReadOnlyList<string>? places = null, bool automatic = false)
    {
        var rules = StackableRules(itemId, places, catalog);
        return owner.Execute(() => ReferenceEquals(Selected, character) &&
            rules.Check(CurrentStackableUseContext(character), directUse: !automatic) == StackableUseRejection.None);
    }

    // 只在原账号 owner 内读取现有状态，不新增另一套在线场景或按保存的城镇位置猜测副本状态。
    StackableUseContext CurrentStackableUseContext(Character character) => StackableUsePlacePolicy.Context(ReadState(Phase),
        Phase == SessionPhase.Town && towns.IsRoom(character.Position, QuestEligibility.Completed(character)),
        ChannelEntry?.Type == 20, formalDungeon?.Entry.IsPractice == true, formalDungeon?.Assault is not null,
        character.Guild is { Id: > 0 }, character.IsPvpPrivate, character.Guild is { Id: > 0, Grade: 1 });
}
