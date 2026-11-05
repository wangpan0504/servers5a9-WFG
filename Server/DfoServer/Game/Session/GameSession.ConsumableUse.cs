using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed record UsedConsumable(ConsumableUseRequest? Request, long SourceUid, long Timestamp, int DurationMilliseconds, TimeProvider Clock)
    {
        public byte Maintenance { get; init; }
        public bool EnforceCooldown { get; init; } = true;
    }
    readonly Dictionary<int, UsedConsumable> usedConsumables = new();
    InventoryPlan? pendingConsumableUse;
    sealed record PortableCargoAccess(long Generation, long SelectionEpoch,
        Guid DungeonInstance, ushort PersonalCapacity, ushort AccountCapacity,
        bool Personal = true, bool Account = true);
    PortableCargoAccess? portableCargo;

    bool CanAccessPortableCargo(InventoryListType list) =>
        list is not (InventoryListType.PersonalCargo or InventoryListType.AccountCargo) ||
        portableCargo is { } access && !AntonRejoinPresentationPending && Ready && Selected is { } character &&
        (list == InventoryListType.PersonalCargo ? access.Personal : access.Account) &&
        Phase is SessionPhase.Dungeon or SessionPhase.Settlement &&
        access.Generation == owner.Generation && access.SelectionEpoch == SelectionEpoch &&
        access.DungeonInstance == formalDungeon?.Run.Snapshot.Identity.InstanceId &&
        access.PersonalCapacity == character.Inventory.CargoCapacity &&
        access.AccountCapacity == character.Inventory.AccountCargo.Capacity;

    bool CanUseDungeonItem(int itemId, IReadOnlySet<uint>? allowedDungeons = null) => !IsImperialArenaCombat &&
        (formalDungeon is not { Entry.Maze.IsGriefTower: true } grief || grief.GriefItems?.CanUse(itemId) == true) &&
        (formalDungeon is not { Entry.RaidEntry.Kind: Raids.RaidKind.Fiendwar } fiendwar ||
            allowedDungeons?.Contains(fiendwar.Entry.DungeonId) == true);

    void CommitDungeonItemUse(int itemId)
    {
        if (formalDungeon is { Entry.Maze.IsGriefTower: true } grief)
            (grief.GriefItems ?? throw new InvalidOperationException("悲叹实例缺少物品使用规则")).CommitUse(itemId);
    }

    bool CanUseConsumable() => !AntonRejoinPresentationPending && pendingRental is null && !HasPendingTownQuestCommit && Ready && Selected is not null && Phase is SessionPhase.Town or SessionPhase.Dungeon or SessionPhase.Settlement &&
        formalDungeon?.HasPendingFatigue != true &&
        formalDungeon?.Run.HasPendingDeath != true && formalDungeon?.Run.HasPendingSettlement != true && !HasPendingFormalCard && formalDungeon?.Ground.HasPendingOperation != true;

    ConsumableUseGuard UseGuard() => new(Selected, owner.Generation, SelectionEpoch, Phase, formalDungeon?.Run.Snapshot.Identity);

    public bool CanPublishConsumable(ConsumableUseGuard guard) => owner.Execute(() => guard.Generation == owner.Generation &&
        guard.SelectionEpoch == SelectionEpoch && guard.Phase == Phase && ReferenceEquals(Selected, guard.Character) &&
        guard.DungeonIdentity == formalDungeon?.Run.Snapshot.Identity);

    public ConsumableUseResult UseConsumable(ConsumableUseRequest request, InventoryConsumableUseService service, bool activityBlocked = false)
    {
        var captured = owner.Execute(() => (Guard: UseGuard(), Character: CanUseConsumable() ? Selected : null,
            Close: ClosedUse(request)));
        if (captured.Close is { } closed) return closed;
        ConsumableUseResult Reject(string reason) => new(ConsumableUseOutcome.Rejected, reason, Guard: captured.Guard);
        if (activityBlocked || captured.Character is not { } character) return Reject("invalid-state");
        int useItemId = character.Inventory.Find(request.Position)?.Core.ItemId ?? request.ItemId;
        if (!service.TryDefinition(request.ItemId, out var definition)) return Reject("unsupported-template");
        bool requiresPcRoom = items?.Get(request.ItemId).RequiresPcRoom == true;
        if (requiresPcRoom && !owner.Execute(() => PremiumPcRoom)) return Reject("pcroom-required");
        if (!CanUseStackableItem(character, useItemId, places: definition.Places)) return Reject("usable-qualification");
        if (definition.BossSelectionBuff is not null && captured.Guard.Phase != SessionPhase.Town)
            return Reject("boss-potion-requires-town");
        if (definition.EpicDropBuff is not null && captured.Guard.Phase != SessionPhase.Town)
            return Reject("epic-potion-requires-town");
        if (definition.HellLegendEpicDropBuff is not null && captured.Guard.Phase != SessionPhase.Town)
            return Reject("hell-legend-epic-potion-requires-town");
        if (definition.VillageMoveSpeedBuff is not null && captured.Guard.Phase != SessionPhase.Town)
            return Reject("village-speed-potion-requires-town");
        if (definition.EnhancementPotion is not null && captured.Guard.Phase != SessionPhase.Town)
            return Reject("enhancement-potion-requires-town");
        if (definition.TownGiftItem != 0 && captured.Guard.Phase != SessionPhase.Town) return Reject("gift_requires_town");
        if (definition.SummonsApc && captured.Guard.Phase != SessionPhase.Dungeon) return Reject("summon_requires_dungeon");
        if (definition.RequiresDungeon && captured.Guard.Phase != SessionPhase.Dungeon) return Reject("projectile_requires_dungeon");
        if (definition.Disguise is not null && (captured.Guard.Phase != SessionPhase.Town ||
            character.Disguise?.Active(service.Clock.GetUtcNow().ToUnixTimeMilliseconds()) == true))
            return Reject("disguise_requires_untransformed_town_character");
        var preliminary = service.Plan(character, request, service.Clock.GetUtcNow().ToUnixTimeSeconds());
        return owner.Execute(() =>
        {
            if (ClosedUse(request) is { } repeated) return repeated;
            if (!CanPublishConsumable(captured.Guard) || !CanUseConsumable())
                return new ConsumableUseResult(ConsumableUseOutcome.CloseConnection, "state-changed");
            if (requiresPcRoom && !PremiumPcRoom) return Reject("pcroom-required");
            if (!CanUseStackableItem(character, useItemId, places: definition.Places)) return Reject("usable-qualification");
            if (!CanUseDungeonItem(request.ItemId, definition.AllowedDungeons))
                return Reject("grief-item-not-allowed-or-exhausted");
            if (definition.AllowedDungeons is { } allowed &&
                (Phase != SessionPhase.Dungeon || formalDungeon is not { } restricted || !allowed.Contains(restricted.Entry.DungeonId)))
                return Reject("potion_dungeon_not_allowed");
            if ((definition.SummonsApc || definition.RequiresDungeon) && (formalDungeon?.CharacterDead != false ||
                formalDungeon.Run.Phase != DfoServer.Game.Dungeons.SinglePlayerDungeonPhase.Dungeon))
                return Reject(definition.SummonsApc ? "summon_requires_living_dungeon_character" : "projectile_requires_living_dungeon_character");
            var previous = usedConsumables.GetValueOrDefault(definition.CoolGroup);
            if (formalDungeon is { } instance && definition.ExcludedDungeons.Contains(instance.Entry.DungeonId))
                return Reject("potion_dungeon_excluded");
            if (definition.RaidUseForbidden && formalDungeon?.Entry.RaidEntry is not null)
                return Reject("potion_raid_excluded");
            if (previous is { EnforceCooldown: true } && previous.Clock.GetElapsedTime(previous.Timestamp) < TimeSpan.FromMilliseconds(definition.CoolTime))
            {
                return Reject("cooldown");
            }
            if (character.Inventory.ConsumableStates.Any(row => row.Kind == ConsumableStateKind.Cooldown &&
                row.CoolGroup == definition.CoolGroup && row.Active(service.Clock.GetUtcNow().ToUnixTimeMilliseconds())))
                return Reject("cooldown");
            if (!preliminary.Accepted || preliminary.Plan is null) return Reject("source-or-qualification");
            // 规划期间可能跨过期限；提交时重新检验原源，不能只使用规划时的时钟。
            var source = character.Inventory.Find(request.Position)!;
            if (source.Core.IsExpired(service.Clock.GetUtcNow().ToUnixTimeSeconds()))
                return Reject("source-expired");
            bool preserve = PreservesDungeonConsumables;
            System.Collections.Immutable.ImmutableArray<ConsumableState>? freeEffects = null;
            if (preserve && definition.EffectMilliseconds > 0 && formalDungeon is { } freeDungeon)
            {
                var ephemeral = ConsumableStatePolicy.Activate(character.Inventory with
                    { ConsumableStates = freeDungeon.FreeConsumableEffects }, definition, service.Clock.GetUtcNow().ToUnixTimeMilliseconds());
                if (ephemeral is null) return Reject("effect-capacity");
                freeEffects = ephemeral.ConsumableStates;
            }
            var actual = preserve ? new InventoryResult(true,0,new(character.Inventory,character.Inventory,
                "free_dungeon_use_consumable",new(character, source.ExpireTime > 0 ? source.ExpireTime : null, service.Clock))) : preliminary;
            if (definition.BossSelectionBuff is { } bossBuff)
            {
                var now = service.Clock.GetUtcNow();
                var after = actual.Plan!.After with { BossSelectionBuff = new(request.ItemId, bossBuff.Entries,
                    DailyBoosterUsage.NextReset(now).ToUnixTimeMilliseconds(), bossBuff) };
                actual = actual with { Plan = actual.Plan.WithAfter(after) };
            }
            if (!preserve && (definition.EffectMilliseconds > 0 || definition.CooltimeMaintenance != 0))
            {
                var original = actual.Plan!;
                var after = ConsumableStatePolicy.Activate(original.After, definition, service.Clock.GetUtcNow().ToUnixTimeMilliseconds());
                if (after is null) return Reject("effect-capacity");
                actual = actual with { Plan = original.WithAfter(after) };
            }
            // 秘药的 2 小时冷却只记在自己的 cooltime group（7440/7377 为 99），不挡住组 0/1 的 HP/MP。
            if (!preserve && definition.ExpBonusPermille > 0 && actual.Plan is { } bonusPlan)
                bonusPlan.ExpBonus = (checked(service.Clock.GetUtcNow().ToUnixTimeSeconds() + definition.ExpBonusMilliseconds / 1000),
                    definition.ExpBonusPermille);
            if (requiresPcRoom && actual.Plan is { } pcRoomPlan)
                actual = actual with { Plan = WithPcRoomQualification(character, pcRoomPlan) };
            pendingConsumableUse = actual.Plan;
            var result = CommitInventory(character, actual, 123);
            // 异常返回保留未决标记并由连接生命周期关闭，不自动重做SQL、重发ACK或宣称药效已回滚。
            pendingConsumableUse = null;
            if (!result.Accepted) return Reject(result.RejectReason ?? "source-expired");
            // 资格只来自实际成功提交；消耗最后一只猫头鹰后仍可访问本次实例的仓库。
            if (definition.OpensPortableCargo && Phase is SessionPhase.Dungeon or SessionPhase.Settlement &&
                formalDungeon is { } cargoDungeon)
                portableCargo = new(owner.Generation, SelectionEpoch, cargoDungeon.Run.Snapshot.Identity.InstanceId,
                    Selected!.Inventory.CargoCapacity, Selected.Inventory.AccountCargo.Capacity);
            CommitDungeonItemUse(request.ItemId);
            if (freeEffects is { } effects) formalDungeon!.FreeConsumableEffects = effects;
            usedConsumables[definition.CoolGroup] = new(request, source.Uid, service.Clock.GetTimestamp(), definition.CoolTime, service.Clock)
                { Maintenance = preserve ? (byte)0 : definition.CooltimeMaintenance };
            if (definition.Disguise is { } disguise)
                pendingDisguise = new(disguise, request.ItemId, SelectionEpoch,
                    checked(service.Clock.GetUtcNow().ToUnixTimeMilliseconds() + disguise.Seconds * 1000L));
            return new ConsumableUseResult(ConsumableUseOutcome.Committed, preserve ? "dungeon-preserved" : "committed", result, UseGuard())
                { SourceUid = source.Uid, PreserveSource = preserve,
                    NativeNoConsumption = preserve && !definition.EffectsAfterAcknowledgement, TownGiftItem = definition.TownGiftItem };
        });
    }

    internal bool MustHandleConsumableReplay(ConsumableUseRequest request) => owner.Execute(() => ClosedUse(request) is not null);

    ConsumableUseResult? ClosedUse(ConsumableUseRequest request)
    {
        if (pendingConsumableUse is not null) return new(ConsumableUseOutcome.CloseConnection, "commit-unknown");
        // 成功token的歧义不因过房、活动门禁或模板变化消失，必须在任何普通拒绝ACK之前核对。
        foreach (var previous in usedConsumables.Values)
            if (previous is not null && previous.Request == request &&
                previous.Clock.GetElapsedTime(previous.Timestamp) < TimeSpan.FromMilliseconds(previous.DurationMilliseconds))
                return new(ConsumableUseOutcome.CloseConnection, "ambiguous-repeat") { SourceUid = previous.SourceUid };
        return null;
    }

    void CrossConsumableCooldownBoundary(ConsumableMaintenance boundary)
    {
        // 冷却可按PVF清除，原成功token的重放歧义仍保留到原窗口结束。
        foreach (var pair in usedConsumables.ToArray())
            if ((pair.Value.Maintenance & (byte)boundary) == 0)
                usedConsumables[pair.Key] = pair.Value with { EnforceCooldown = false };
    }

    void ClearConsumableUse()
    {
        portableCargo = null;
        usedConsumables.Clear();
        creatureFeedUses.Clear();
        levelUpCooldowns.Clear();
        pendingConsumableUse = null;
        pendingDisguise = null;
    }
}
