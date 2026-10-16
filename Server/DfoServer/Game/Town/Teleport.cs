using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Town;

// Open（默认）：瞬间移动药剂不按 `[int data]` 地下城选择地图白名单限制有 `[dungeon gate]` 的区域；Pvf：恢复严格白名单。
internal enum TeleportPotionGateMode { Open, Pvf }

internal sealed record TeleportCooldowns(long PotionUntil = 0, long AuraUntil = 0);
internal sealed record PotionTeleportRequest(short Slot, int ItemId, byte Mode, Location Target);
internal sealed record SoloTeleportRequest(int CharacterId, int NpcId, Location Target);
internal sealed record TeleportTarget(ushort CharacterId, string Name, Location Position, ChannelIdentity Channel, long Serial,
    byte Level, byte Job, byte GrowType);
internal sealed record TeleportPlan(Character Expected, Location Position, Location Return, InventoryPlan Inventory,
    TeleportCooldowns Cooldowns, long Now);
internal sealed record TeleportReceipt(TownTravelReceipt Travel, InventoryResult Inventory);
internal sealed record TeleportResult(string Reason, TeleportReceipt? Receipt = null)
{
    public bool Accepted => Receipt is not null;
}

internal sealed class TeleportService(TownCatalog towns, TeleportCatalog definitions, Func<int, ItemTemplate> item, TimeProvider? clock = null,
    TeleportPotionGateMode potionGates = TeleportPotionGateMode.Open)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    public SoloTeleportDefinition? Aura(Character character, long now)
    {
        var aura = character.Inventory.Find(new(InventoryListType.Equipment, 9));
        if (aura is not { Avatar: not null, Core.Kind: ItemKind.Avatar } || aura.IsExpired(now / 1000)) return null;
        var template = item(aura.Core.ItemId);
        return template.EquipmentSlot == 9 && character.Level >= template.MinimumLevel ? definitions.Aura(aura.Core.ItemId) : null;
    }

    public (string Reason, TeleportPlan? Plan) Potion(Character character, PotionTeleportRequest request, long now)
    {
        var slot = new InventorySlot(InventoryListType.Main, request.Slot);
        var source = character.Inventory.Find(slot);
        if (request.Mode != 0 || source is not { Core.Kind: ItemKind.Consumable } ||
            source.Core.ItemId != request.ItemId || source.Core.Count < 1 || source.IsExpired(now / 1000) ||
            !ItemSlotBoundService.Allows(character.Inventory, slot, ItemKind.Consumable))
            return ("potion-unavailable", null);
        var definition = definitions.Potion(request.ItemId);
        if (definition is null || character.Level < definition.MinimumLevel) return ("potion-unavailable", null);
        if (character.TeleportCooldowns.PotionUntil > now) return ("potion-cooldown", null);
        var access = Destination(character, request.Target);
        if (access != "ready") return (access, null);
        if (potionGates == TeleportPotionGateMode.Pvf &&
            towns.Areas[(request.Target.Town, request.Target.Area)].DungeonWorldMap is { } map && !definition.DungeonMaps.Contains(map))
            return ("potion-dungeon-map-unavailable", null);
        var deleted = InventoryDeleteService.Plan(character.Inventory, slot, request.ItemId, 1);
        if (!deleted.Accepted) return ("potion-locked", null);
        return ("ready", Plan(character, request.Target,
            new(character.Inventory, deleted.Plan!.After, "teleport_potion", new(character, source.ExpireTime == 0 ? null : source.ExpireTime)),
            character.TeleportCooldowns with { PotionUntil = checked(now + definition.CoolTime) }, now));
    }

    public (string Reason, TeleportPlan? Plan) Solo(Character character, SoloTeleportRequest request, TeleportTarget? player, long now)
    {
        var definition = Aura(character, now);
        if (definition is null) return ("aura-unavailable", null);
        if (character.TeleportCooldowns.AuraUntil > now) return ("aura-cooldown", null);
        Location target;
        if (request.CharacterId == -1 && request.NpcId > 0)
        {
            target = request.Target;
            var quests = DfoServer.Game.Quests.QuestEligibility.Completed(character);
            if (!towns.Areas.TryGetValue((target.Town, target.Area), out var area) ||
                !area.Map(quests).Npcs.Any(npc => npc.Id == (uint)request.NpcId)) return ("npc-target-unavailable", null);
        }
        else if (request.CharacterId > 0 && request.NpcId == -1 && player is not null && player.CharacterId == request.CharacterId &&
            player.CharacterId != character.Id)
            // 玩家位置只取当前城镇成员；客户端预检查之后移动的旧坐标不能覆盖它。
            target = player.Position;
        else return ("player-target-unavailable", null);
        string access = Destination(character, target);
        if (access != "ready") return (access, null);
        var equipped = character.Inventory.Find(new(InventoryListType.Equipment, 9))!;
        return ("ready", Plan(character, target, new(character.Inventory, character.Inventory, "teleport_aura",
            new(character, equipped.ExpireTime == 0 ? null : equipped.ExpireTime)),
            character.TeleportCooldowns with { AuraUntil = checked(now + definition.CoolSeconds * 1000L) }, now));
    }

    string Destination(Character character, Location target)
    {
        if (target.Direction > 9) return "target-direction-unavailable";
        var completed = DfoServer.Game.Quests.QuestEligibility.Completed(character);
        string admission = towns.DestinationAdmission(target, character.Level, completed);
        if (admission != "ready") return admission;
        if (towns.IsRoom(target, completed)) return "private-room-target";
        return towns.IsPosition(target, completed) ? "ready" : "target-position-unavailable";
    }

    TeleportPlan Plan(Character character, Location target, InventoryPlan inventory, TeleportCooldowns cooldowns, long now) =>
        new(character, target, towns.ResolveRoomReturn(target, character.Return, character.Level,
            DfoServer.Game.Quests.QuestEligibility.Completed(character)), inventory, cooldowns, now);
}
