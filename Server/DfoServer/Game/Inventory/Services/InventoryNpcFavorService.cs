using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryNpcFavorService(Func<int, NpcFavorItemDefinition?> definitions, NpcFavorCatalog npcs, TownCatalog? towns = null, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    public TimeProvider Clock => clock;

    public InventoryResult Plan(Character character, InventorySlot slot, int npcId, bool inTown)
    {
        var item = character.Inventory.Find(slot);
        if (item is null || definitions(item.Core.ItemId) is not { } definition || !definition.Targets.Contains(npcId) ||
            npcs.Get(npcId) is not { } target) return InventoryResult.Reject(19);
        var rows = character.Inventory.NpcFavor;
        var previous = rows.SingleOrDefault(row => row.NpcId == target.OwnerId);
        int points = previous?.Points ?? 0;
        // 满好感不扣信；接近上限时按NPC资源封顶，不能写出客户端不可表示的溢出值。
        if (points >= target.Maximum || previous is null && rows.Length == byte.MaxValue) return InventoryResult.Reject(19);
        int afterPoints = (int)Math.Min(target.Maximum, (long)points + definition.Source.Parameters[0]);
        string place = !inTown ? "[dungeon]" : towns?.IsRoom(character.Position, QuestEligibility.Completed(character)) == true
            ? "[seria room]" : "[village]";
        var paid = InventoryActionConsumableService.Plan(character, slot, definition.Source, place, clock);
        if (!paid.Accepted) return paid;
        var updated = previous is null ? rows.Add(new(target.OwnerId, afterPoints)) : rows.Replace(previous, previous with { Points = afterPoints });
        var after = paid.Plan!.After with { NpcFavor = updated.OrderBy(row => row.NpcId).ToImmutableArray() };
        return new(true, 0, new(character.Inventory, after, "npc_favor_item", paid.Plan.CharacterPrecondition));
    }
}
