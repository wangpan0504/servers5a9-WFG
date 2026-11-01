using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Town;

// Open（默认）：瞬间移动药剂不按 `[int data]` 地下城选择地图白名单限制有 `[dungeon gate]` 的区域；Pvf：恢复严格白名单。
internal enum TeleportPotionGateMode { Open, Pvf }

internal sealed record TeleportCooldowns(long PotionUntil = 0, long AuraUntil = 0, long UpgradeAuraUntil = 0, long PartyAuraUntil = 0,
    long PrivilegeUntil = 0)
{
    public long Until(AuraAbilityKind kind) => kind switch
    {
        AuraAbilityKind.PartyTeleport => PartyAuraUntil,
        AuraAbilityKind.SoloTeleport => AuraUntil,
        AuraAbilityKind.UpgradeSoloTeleport => UpgradeAuraUntil,
        _ => throw new InvalidOperationException("该能力没有传送冷却")
    };
}
internal sealed record PotionTeleportRequest(short Slot, int ItemId, byte Mode, Location Target);
internal sealed record SoloTeleportRequest(int CharacterId, int NpcId, Location Target);
internal sealed record PartyTeleportRequest(Location Target, byte Source);
internal sealed record PrivilegeTeleportRequest(Location Target);
internal sealed record TeleportTarget(ushort CharacterId, string Name, Location Position, ChannelIdentity Channel, long Serial,
    byte Level, byte Job, byte GrowType);
internal sealed record TeleportPlan(Character Expected, Location Position, Location Return, InventoryPlan Inventory,
    TeleportCooldowns Cooldowns, long Now)
{
    public AuraAbilityKind? AuraKind { get; init; }
}
internal sealed record TeleportReceipt(TownTravelReceipt Travel, InventoryResult Inventory)
{
    public AuraAbilityKind? AuraKind { get; init; }
}
internal sealed record TeleportResult(string Reason, TeleportReceipt? Receipt = null)
{
    public bool Replayed { get; init; }
    public bool Accepted => Receipt is not null || Replayed;
}
internal sealed record GuildMapTeleportCommit(string Reason, Character? Character = null, bool Replayed = false);

internal sealed class TeleportService(TownCatalog towns, TeleportCatalog definitions, Func<int, ItemTemplate> item, TimeProvider? clock = null,
    TeleportPotionGateMode potionGates = TeleportPotionGateMode.Open, int guildTeleportCost = 0,
    PrivilegeServiceCatalog? privilege = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    public static bool IsGuildMap(PotionTeleportRequest request) => request is
        { Slot: 0, ItemId: 0, Mode: 0, Target.Direction: 3 };

    public (string Reason, TeleportPlan? Plan) GuildMap(Character character, PotionTeleportRequest request,
        EpicPieceCatalog pieces, long now)
    {
        if (!IsGuildMap(request) || guildTeleportCost <= 0 || character.Guild is null)
            return ("guild-teleport-unavailable", null);
        // 0284FC70在237最后写的是地图来源3，不是角色朝向；切图沿用当前实际朝向。
        var target = request.Target with { Direction = character.Position.Direction };
        string access = Destination(character, target);
        if (access != "ready") return (access, null);
        var paid = InventoryMaterialConsumptionService.Plan(character.Inventory,
            [new(GuildNpcCatalog.MemberCoinItem, guildTeleportCost)], pieces, DateTimeOffset.FromUnixTimeMilliseconds(now));
        // 余额不足仍允许提交器先查询已提交回执，最后一笔花完余额的网络重放不能再次扣费。
        return ("ready", Plan(character, target, new(character.Inventory, paid.Plan?.After ?? character.Inventory,
            "guild_map_teleport", new(character)), character.TeleportCooldowns, now));
    }
    // 返回实际穿戴光环声明的能力；`[none]` 以 Kind.None 表示“有光环但不提供传送”，与“没有光环”的null区分。
    public AuraAbilityDefinition? Aura(Character character, long now)
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
        // 个人传送入口由客户端城镇地图决定（实机 `[none]` 光环同样发送470）；能力只决定冷却与特效，不作为切图门禁。
        // 组队光环的地图入口已分流到434，不能借470绕过队伍上下文。
        if (definition is { Kind: AuraAbilityKind.PartyTeleport }) return ("personal-aura-unavailable", null);
        if (definition is { Kind: AuraAbilityKind.SoloTeleport or AuraAbilityKind.UpgradeSoloTeleport } personal &&
            character.TeleportCooldowns.Until(personal.Kind) > now) return ("aura-cooldown", null);
        bool npcTarget = request.CharacterId == -1 && request.NpcId > 0;
        Location target;
        // 实机个人传送点选城镇时两个 i32 都是 -1，目的地在随后的 town/area/坐标。不是玩家目标。
        if (request.CharacterId == -1 && request.NpcId == -1)
            target = request.Target;
        else if (npcTarget)
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
        string access = Destination(character, target, npcTarget);
        if (access != "ready") return (access, null);
        return ("ready", AuraPlan(character, target, definition, now));
    }

    public (string Reason, TeleportPlan? Plan) Party(Character character, PartyTeleportRequest request, long now)
    {
        if (request.Source != 1) return ("party-teleport-source-unavailable", null);
        var definition = Aura(character, now);
        if (definition?.Kind != AuraAbilityKind.PartyTeleport) return ("party-aura-unavailable", null);
        if (character.TeleportCooldowns.PartyAuraUntil > now) return ("aura-cooldown", null);
        string access = Destination(character, request.Target);
        return access == "ready" ? ("ready", AuraPlan(character, request.Target, definition, now)) : (access, null);
    }

    public (string Reason, TeleportPlan? Plan) Privilege(Character character, PrivilegeTeleportRequest request, ChannelEntry? channel, long now)
    {
        // 01767170及其原生分类明确拒绝这些频道；其他合法城镇频道不按普通频道22缩窄。
        if (channel is null || ChannelAreaLanding.IsPvpType(channel.Type) || channel.Type is
            55 or 40 or 27 or 39 or 23 or 32 or 25 or 45 or 50 or 62 or 63 or 8 or 13 or 24 or 20 or 31 or 41 or 59 or 60 ||
            !ChannelAreaLanding.AllowsFiendwarTown(channel, character.Position.Town) ||
            !ChannelAreaLanding.AllowsFiendwarTown(channel, request.Target.Town))
            return ("privilege-teleport-area-unavailable", null);
        var state = character.Inventory.PrivilegeService;
        if (privilege is null || !state.IsActive(now / 1000)) return ("privilege-teleport-unavailable", null);
        if (character.TeleportCooldowns.PrivilegeUntil > now) return ("privilege-teleport-cooldown", null);
        var completed = DfoServer.Game.Quests.QuestEligibility.Completed(character);
        if (towns.DestinationAdmission(character.Position, character.Level, completed) != "ready")
            return ("privilege-teleport-area-unavailable", null);
        // 669末六字节只有目的地；朝向沿用角色当前事实，不把未初始化前缀解释为权限或来源。
        var target = request.Target with { Direction = character.Position.Direction };
        string access = Destination(character, target);
        if (access != "ready") return ("privilege-teleport-area-unavailable", null);
        return ("ready", Plan(character, target,
            new(character.Inventory, character.Inventory, "teleport_privilege", new(character, state.ExpiresAt, Clock, requirePosition: true)),
            character.TeleportCooldowns with { PrivilegeUntil = checked(now + privilege.TeleportCooldownSeconds * 1000L) }, now));
    }

    TeleportPlan AuraPlan(Character character, Location target, AuraAbilityDefinition? definition, long now)
    {
        var equipped = character.Inventory.Find(new(InventoryListType.Equipment, 9));
        var cooldowns = character.TeleportCooldowns;
        AuraAbilityKind? kind = null;
        if (definition is { Kind: not AuraAbilityKind.None } declared)
        {
            long until = checked(now + declared.CoolSeconds * 1000L);
            cooldowns = declared.Kind switch
            {
                AuraAbilityKind.PartyTeleport => cooldowns with { PartyAuraUntil = until },
                AuraAbilityKind.SoloTeleport => cooldowns with { AuraUntil = until },
                AuraAbilityKind.UpgradeSoloTeleport => cooldowns with { UpgradeAuraUntil = until },
                _ => throw new InvalidOperationException("光环没有传送能力")
            };
            kind = declared.Kind;
        }
        // `[none]`或未穿戴光环只切图：不写冷却，也不把该次请求当作378的刷新项。
        return Plan(character, target, new(character.Inventory, character.Inventory, "teleport_aura",
            equipped is null ? null : new(character, equipped.ExpireTime == 0 ? null : equipped.ExpireTime)),
            cooldowns, now) with { AuraKind = kind };
    }

    string Destination(Character character, Location target, bool npcTarget = false)
    {
        if (character.IsPvpPrivate && target.Town != 10) return "pvp-private-town-only";
        if (target.Direction > 9) return "target-direction-unavailable";
        var completed = DfoServer.Game.Quests.QuestEligibility.Completed(character);
        string admission = towns.DestinationAdmission(target, character.Level, completed);
        if (admission != "ready") return admission;
        if (towns.IsRoom(target, completed)) return "private-room-target";
        // ARAD的NPC导航落点可在步行矩形外；已核对当前地图的NPC身份，不再用行走边界拒绝或改写坐标。
        return npcTarget || towns.IsPosition(target, completed) ? "ready" : "target-position-unavailable";
    }

    TeleportPlan Plan(Character character, Location target, InventoryPlan inventory, TeleportCooldowns cooldowns, long now) =>
        new(character, target, towns.ResolveRoomReturn(target, character.Return, character.Level,
            DfoServer.Game.Quests.QuestEligibility.Completed(character)), inventory, cooldowns, now);
}
