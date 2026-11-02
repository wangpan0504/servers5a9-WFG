using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public SemaphoreSlim CommandGate { get; } = new(1, 1);
    public PartyDungeonInstance? PartyDungeon { get; private set; }
    Guid? partyDungeonRun;
    public Func<PartyDungeonNotice, Task>? PartyDungeonNoticeArrived { get; set; }
    bool CanMutatePartyDungeon => (PartyDungeon is null || PartyDungeon.IsExecuting) && formalDungeon?.Run.FiendwarObjectKeys?.RecoveryRequired != true;

    internal bool CanBeginPartySelection() => owner.Execute(() => Ready && Selected is not null &&
        Phase is SessionPhase.Town or SessionPhase.MapSelect && pendingRental is null && !HasPendingTownQuestCommit &&
        formalDungeon is null && (HasRaidDungeonAdmission || CurrentDungeonSelection is { IsClassExperience: false } ||
            towns.SelectionReturn(Selected.Position, completed) is not null));

    public bool CanBeginSpecifiedPartySelection(uint dungeon, DungeonCatalog catalog)
    {
        if (MarriageDungeonAdmission.Applies(dungeon)) return CanBeginMarriageDungeonSelection(catalog);
        if (dungeon > int.MaxValue || !catalog.ContainsDungeon((int)dungeon)) return false;
        // 15只建立选择上下文；任务、等级、费用和玩法资格仍由16按原入口统一复验。
        return owner.Execute(() => Phase == SessionPhase.Town && CanBeginPartySelection() && Selected is { } character &&
            towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) &&
            area.DungeonWorldMap is { } world && towns.DungeonMaps.TryGetValue(world, out var map) && map.Error is null &&
            map.Options.Any(option => option.DungeonId == dungeon));
    }

    public bool CanBeginMarriageDungeonSelection(DungeonCatalog catalog)
    {
        var character = owner.Execute(() => Ready && Phase is SessionPhase.Town or SessionPhase.MapSelect ? Selected : null);
        if (character is null) return false;
        var definition = catalog.Get(3541);
        return definition.TryGetDesignatedDifficulty(out byte difficulty) && character.Level >= definition.MinimumLevel &&
            PrepareMarriageDungeonAdmission(catalog, character, 0, difficulty, false, true) is not null;
    }

    MarriageDungeonAdmission? PrepareMarriageDungeonAdmission(DungeonCatalog catalog, Character expected,
        uint quest, byte difficulty, bool specialMode, bool partyAdmission)
    {
        var party = partyAdmission ? CurrentParty?.Invoke() : null;
        if (party is null || !towns.HasMarriageDungeonEntrance(expected.Position, QuestEligibility.Completed(expected))) return null;
        return MarriageDungeonAdmission.Prepare(catalog.Get(3541), MarriageStore?.FindMarriage(expected.Id),
            party.Members.Select(member => member.Actor.Character.Id).ToArray(), quest, difficulty, specialMode);
    }

    internal bool CanMoveWithParty() => owner.Execute(() => Ready && Selected is not null && Phase is SessionPhase.Dungeon or SessionPhase.Settlement &&
        formalDungeon is { } dungeon && !dungeon.Ground.HasPendingOperation && dungeon.PendingTutorialClear is null &&
        !HasPendingQuestScene && !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement && !dungeon.HasPendingFatigue && dungeon.Run.FiendwarObjectKeys?.RecoveryRequired != true);

    internal bool CanCompletePartyLoading() => owner.Execute(() => Ready && formalDungeon is { } dungeon &&
        (Phase == SessionPhase.Loading || dungeon.Run.HasScriptedReturn));

    // 此令牌只允许原Session在提交前复核私有准备，不向协调器暴露可写的Run。
    internal sealed record PartyDungeonPreparation(GameSession Session, Character Expected);
    PartyDungeonPreparation? partyPreparation;
    PreparedFormalDungeon? preparedPartyDungeon;
    PreparedFormalDungeon PreparedParty(PartyDungeonPreparation token) =>
        ReferenceEquals(partyPreparation, token) && preparedPartyDungeon is { } prepared ? prepared :
            throw new InvalidOperationException("组队入场准备已失效");

    internal void DiscardPartyPreparation(PartyDungeonPreparation token)
    {
        try
        {
            owner.Execute(() =>
            {
                if (ReferenceEquals(partyPreparation, token)) { partyPreparation = null; preparedPartyDungeon = null; }
                return true;
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { }
    }

    public PartyDungeonPreparation? PreparePartyDungeon(uint dungeonId, byte difficulty,
        DungeonCatalog catalog, DungeonRoomCatalog rooms, DungeonMonsterDropCatalog? drops, ItemCatalog? items,
        EpicPieceCatalog? pieces, Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? prepareDrops,
        Guid instance, uint firstKey, PartyDungeonPreparation? shared = null, uint questId = 0,
        bool hellParty = false, bool hellLegend = false, CrackDimensionRewardServices? crackRewards = null, bool allowDimensionHidden = true,
        byte memberCount = 1,
        DfoServer.Game.Strongway2019.Strongway2019Admission? strongwayAdmission = null)
    {
        LastFormalDungeonRejection = null;
        var retry = Phase == SessionPhase.Settlement ? FormalDungeonResultFocus() : null;
        var previousDrops = owner.Execute(() => formalDungeon?.PrepareDrops);
        if (shared is not null)
        {
            var source = shared.Session.PreparedParty(shared).Dungeon.Entry;
            difficulty = source.Difficulty;
            // 队员共用队长已冻结的物理迷宫，任务、槽位及接取身份仍由本人生成和提交。
            if (source.Hope is { } hope &&
                !BeginOperationHope(hope.Catalog,catalog,requiredMaze:hope.MazeOrdinal)) return null;
            // 助战者无需接取队长任务；只有本人仍持有原请求任务时才绑定个人请求身份。
            questId = owner.Execute(() => Selected?.ActiveMainQuests.Any(q => q.QuestId == source.QuestId && q.AcceptanceId != Guid.Empty) == true
                ? source.QuestId : 0u);
        }
        var prepared = PrepareFormalDungeon(dungeonId, questId, difficulty, catalog, rooms, drops, items, null, pieces, null, retry,
            prepareDrops: prepareDrops ?? previousDrops, shared: shared, partyFirstKey: firstKey, partyInstance: instance, partyAdmission: true,
            hellParty: hellParty, hellLegend: hellLegend, crackRewards: crackRewards, allowDimensionHidden: allowDimensionHidden,
            partyMemberCount: memberCount,
            strongwayAdmission: strongwayAdmission);
        if (prepared is null && shared is not null) shared.Session.LastFormalDungeonRejection = LastFormalDungeonRejection;
        return owner.Execute(() =>
        {
            if (prepared is null || !ReferenceEquals(Selected, prepared.Character) || SelectionEpoch != prepared.SelectionEpoch)
                return null;
            preparedPartyDungeon = prepared;
            return partyPreparation = new(this, prepared.Character);
        });
    }

    // 组队原子提交前只由队长时钟复核共同业务日；各成员不分别采样now，避免跨06:00裂成两天。
    internal bool IsPartyAdmissionDayCurrent(PartyDungeonPreparation token)
    {
        var entry = PreparedParty(token).Dungeon.Entry;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return (entry.OrdinaryLukeDay is not { } day || day == QuestCycle.Day(now)) &&
            (entry.PreyNormal is not { } prey || prey.Day == QuestCycle.Day(now)) &&
            (entry.AntonNormal is not { } anton || anton.Day == QuestCycle.Day(now));
    }

    public PartyDungeonReservation? ReservePartyDungeon(PartyDungeonPreparation token) => owner.Execute(() =>
    {
        var plan = PreparedParty(token);
        if (!Ready || SelectionEpoch != plan.SelectionEpoch || !ReferenceEquals(Selected, plan.Character) ||
            !(Phase == SessionPhase.MapSelect && formalDungeon is null ||
                Phase == SessionPhase.Settlement && FormalDungeonResultFocus() is not null || CanReserveStrongway2019Matching(plan)) || HasPendingTownQuestCommit) return null;
        var dungeon = plan.Dungeon;
        if (dungeon.Strongway2019Admission is { } strongway && !IsCurrentStrongway2019Admission(strongway)) return null;
        if (dungeon.EvildomWarEntry is { } admission && !ReferenceEquals(CurrentEvildomWar(Selected), admission)) return null;
        if (dungeon.Entry.DungeonId <= int.MaxValue && dungeon.Entry.StartFatigue > 0 &&
            CurrentEvildomWar(Selected) is { Charged: false } war && EvildomWarCostRejection(Selected, war.Catalog) is not null) return null;
        if (CurrentEvildomWar(Selected) is { Charged: false } currentWar && dungeon.ChannelCost is { } warCost &&
            (warCost.DungeonId != currentWar.Catalog.IncountDungeon || warCost.Period != IncountPeriod(warCost.DungeonId))) return null;
        if (!CanInstallRaidDungeon(dungeon.Entry)) return null;
        if (dungeon.Entry.OrdinaryLukeDay is { } day &&
            !repository.LoadOrdinaryLukeProgress(owner, Selected.Id).Allows(day, dungeon.Entry.DungeonId)) return null;
        if (dungeon.Entry.AntonNormal is { } anton &&
            !repository.LoadAntonNormalProgress(owner, Selected.Id).Allows(anton, dungeon.Entry.DungeonId)) return null;
        if (dungeon.Entry.PreyNormal is { } prey &&
            !repository.LoadPreyNormalProgress(owner, Selected.Id).Allows(prey, dungeon.Entry.DungeonId)) return null;
        var costs = new List<ItemMaterialCost>();
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        EpicPieceCatalog? costPieces = dungeon.HellPieces ?? dungeon.EntryPieces ?? dungeon.ChannelCost?.Pieces;
        InventoryPlan? items = null;
        if (dungeon.HellTicket is { } ticket)
        {
            if (dungeon.HellPieces is not { } pieces) return null;
            costs.Add(new(ticket.ItemId, ticket.Count));
            if (dungeon.LegendTicket is { } stone) costs.Add(new(stone.ItemId, stone.Count));
        }
        if (dungeon.Entry.EntryItem is { } cost)
        {
            if (dungeon.HellTicket is not null) throw new InvalidDataException("普通入场票不能叠加深渊入场费用");
            costs.Add(new(cost.ItemId, cost.Count));
        }
        if (dungeon.ChannelCost is { } channelCost) costs.AddRange(channelCost.Materials);
        if (costs.Count > 0)
        {
            if (costPieces is null) return null;
            var result = InventoryMaterialConsumptionService.Plan(Selected.Inventory, costs.ToArray(), costPieces, now);
            if (!result.Accepted) return null;
            items = result.Plan;
        }
        PartyDungeonIncount? incount = dungeon.ChannelCost is { } channel
            ? new(channel.Incount?.CountKey ?? channel.DungeonId, channel.Period, channel.Incount, channel.Account, !channel.ChargeOnClear) : null;
        if (dungeon.Entry.Dimension is { } dimension && dimension.Period != IncountPeriod(dimension.Catalog.DailyRow.CountKey)) return null;

        ushort fatigue = debugSettings?.InfiniteFatigue == true ? (ushort)0 : dungeon.Entry.StartFatigue;
        DfoServer.Game.Guild.GuildDungeonAdmission? guildAdmission = null;
        if (guildDungeons?.Find(dungeon.Entry.DungeonId) is not null)
        {
            guildAdmission = repository.PrepareGuildDungeonEntry(owner, Selected.Id, dungeon.Entry.DungeonId, guildDungeons);
            if (guildAdmission is null) { LastFormalDungeonRejection = "guild-dungeon-unavailable"; return null; }
        }
        var training = PrepareGuildTraining(Selected, items);
        // 只冻结原准备对象，正式效果在组队统一事务成功后的InstallPartyDungeon安装。
        preparedPartyDungeon = plan with { Dungeon = dungeon with { GuildTraining = training?.Snapshot } };
        return new PartyDungeonReservation(owner, Selected, plan.NextKey,
            new(items, fatigue, dungeon.Run.Snapshot.Identity, dungeon.Entry.StartMap.Id, plan.Weekly)
            { GuildTraining = training?.Inventory, OrdinaryLukeEntry = dungeon.Entry.OrdinaryLukeDay is null ? null : dungeon.Entry,
                PreyNormalEntry = dungeon.Entry.PreyNormal is null ? null : dungeon.Entry,
                AntonNormalEntry = dungeon.Entry.AntonNormal is null ? null : dungeon.Entry,
                DungeonId = checked((int)dungeon.Entry.DungeonId), Hope = dungeon.Entry.Hope, Marriage = dungeon.Entry.Marriage,
                GuildDungeon = guildAdmission, Dimension = dungeon.Entry.Dimension,
                FatigueAccelerationPercent = CaptureFatigueAcceleration(dungeon.Entry),
                RequiredEvent = Strongway2019RequiredEvent(dungeon.Strongway2019Admission) }, incount)
        {
            EvildomWar = dungeon.EvildomWarEntry is { Store: not null } activeWar
                ? new(activeWar.Battle.View, activeWar.BindingToken,
                    activeWar.Party.Members.Single(member => ReferenceEquals(member.Actor.Session, this)).Slot,
                    QuestCycle.Day(now), !activeWar.Charged) : null
        };
    });

    public FormalDungeonReceipt InstallPartyDungeon(PartyDungeonPreparation token, PartyDungeonEntryCommit result,
        PartyDungeonReservation reservation, PartyDungeonInstance instance) => owner.Execute(() =>
    {
        var committed = result.Character;
        var plan = PreparedParty(token);
        if (!ReferenceEquals(Selected, plan.Character) || SelectionEpoch != plan.SelectionEpoch ||
            committed.Id != plan.Character.Id || committed.Revision <= plan.Character.Revision)
            throw new InvalidOperationException("已提交组队入场的成员身份变化");
        // 入场名册与本人槽位属于本次Run；队友离线后结算仍沿用原槽位。
        var installing = plan.Dungeon with
        {
            PartyActors = instance.Members.OrderBy(member => member.Slot).Select(member => member.Actor.Character.Id).ToImmutableArray(),
            GuildMemberships = result.GuildMemberships,
            PartySlot = instance.Members.Single(member => ReferenceEquals(member.Actor.Session, this)).Slot
        };
        if (committed.Revision != installing.Entry.CharacterRevision)
        {
            installing.Run.RebasePreparedRevision(committed.Revision);
            installing = installing with { Entry = installing.Entry with { CharacterRevision = committed.Revision } };
        }
        if (formalDungeon is { } previous)
        {
            if (previous.Run.Return(previous.Run.Snapshot.Identity, Guid.NewGuid()).Outcome != DungeonRunOutcome.Accepted)
                throw new InvalidOperationException("已提交组队再挑战不能结束原结算实例");
            previous.Ground.ExpireRun();
        }
        installing.Run.FreezeFatigueAcceleration(PremiumPcRoom ? reservation.Charge.FatigueAccelerationPercent : 0);
        installing.Ground.PublishPassiveEntry();
        Selected = committed;
        if (installing.EvildomWarEntry is { } war)
            war.Charged = true;
        formalDungeon = installing;
        if (installing.Entry.Hope is not null) hopeSelection = null;
        PartyDungeon = instance;
        partyDungeonRun = instance.Id;
        partyPreparation = null;
        preparedPartyDungeon = null;
        CurrentDungeonSelection = null;
        pendingDungeonContinuation = null;
        pendingQuestReplay = null;
        SetState(SessionPhase.Loading);
        var payment = reservation.Charge.InventoryPayment;
        return FormalReceipt() with { EntryItemConsumedSlots = installing.HellTicket is null ? payment?.ChangedSlots ?? [] : [],
            HellConsumedSlots = installing.HellTicket is not null ? payment?.ChangedSlots ?? [] : [],
            HellTicket = installing.HellTicket, LegendTicket = installing.LegendTicket,
            EntryFatigueCharged = reservation.Charge.Fatigue, EnterCounts = result.EnterCounts,
            EntryChangedCurrencies = payment?.ChangedCurrencies ?? [],
            ChannelConsumedSlots = installing.ChannelCost?.Consume is not null ? payment?.ChangedSlots ?? [] : [] };
    });

    internal void RemovePartyDungeon(PartyDungeonInstance instance)
    {
        if (ReferenceEquals(PartyDungeon, instance))
            PartyDungeon = null;
    }

    internal sealed record PartyExitPreparation(PartyDungeonExitReservation Reservation, long Epoch, object Context, PartyDungeonExitKind Kind);

    internal PartyExitPreparation? PreparePartyExit(PartyDungeonExitKind kind) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || HasPendingTownQuestCommit || pendingRental is not null) return null;
        if (kind == PartyDungeonExitKind.SelectionReturn)
        {
            if (Phase != SessionPhase.MapSelect || CurrentDungeonSelection is not { IsTutorial: false, IsClassExperience: false } selection)
                return null;
            ValidateDungeonSelection();
            return new PartyExitPreparation(new(owner, character, selection.ReturnPosition, null), SelectionEpoch, selection, kind);
        }
        if (!(kind == PartyDungeonExitKind.FiendwarKeyRecovery ? PartyDungeon is { IsExecuting: true } : CanMutatePartyDungeon) || formalDungeon is not { } dungeon ||
            dungeon.Ground.HasPendingOperation || HasPendingFormalCard || pendingQuestCompletion is not null ||
            dungeon.HasPendingFatigue || HasPendingQuestScene || dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingSettlement ||
            dungeon.PendingHopeReward is not null && !dungeon.HopeRewardConfirmed ||
            dungeon.PendingTutorialClear is not null) return null;
        bool canExit = kind switch
        {
            PartyDungeonExitKind.HellSelection => CanBeginClearedHellSelection(0),
            PartyDungeonExitKind.EvildomWarRetreat => CanRetreatEvildomWar(),
            PartyDungeonExitKind.FiendwarKeyRecovery => CanRecoverFiendwarKeys(dungeon.Run.Scene.Identity.InstanceId),
            PartyDungeonExitKind.ResultSelection or PartyDungeonExitKind.ResultReturn =>
                FormalDungeonResultFocus() is not null || RaidDungeonResultFocus() is not null,
            _ => FormalDungeonResultFocus() is not null
        };
        if (!canExit)
            return null;
        if (kind == PartyDungeonExitKind.ResultSelection && dungeon.Entry.IsCosmo) return null;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var after = kind is PartyDungeonExitKind.HellSelection or PartyDungeonExitKind.FiendwarKeyRecovery ? character.Inventory :
            StaminaRecoveryService.ApplyDungeonRetreat(character, dungeon.Entry, dungeon.Run.Phase, now, PremiumPcRoom);
        after = ConsumableStatePolicy.CrossBoundary(after, ConsumableMaintenance.Town, now.ToUnixTimeMilliseconds());
        var inventory = after == character.Inventory ? null : new InventoryPlan(character.Inventory, after, "dungeon_retreat_stamina", new(character));
        var activeHope = dungeon.Run.HopeBinding is { } binding
            ? character.ActiveMainQuests.SingleOrDefault(q => q.AcceptanceId == binding.Acceptance.AcceptanceId) : null;
        return new PartyExitPreparation(new(owner, character, dungeon.OrdinaryReturnPosition ?? character.Position, inventory)
            { Hope = activeHope is null ? null : new(dungeon.Run.HopeCatalog!,activeHope) },
            SelectionEpoch, dungeon, kind);
    });

    internal PartyDungeonNotice InstallPartyExit(PartyExitPreparation prepared, Character committed,
        DfoServer.Game.Party.PartyMember member, DfoServer.Game.Party.PartyMember[] members) => owner.Execute(() =>
    {
        if (!Ready || SelectionEpoch != prepared.Epoch || !ReferenceEquals(Selected, prepared.Reservation.Expected) ||
            !ReferenceEquals(prepared.Context, prepared.Kind == PartyDungeonExitKind.SelectionReturn ? (object?)CurrentDungeonSelection : formalDungeon) ||
            committed.Id != Selected.Id || committed.Revision < Selected.Revision)
            throw new InvalidOperationException("组队退场提交后身份变化");
        Selected = committed;
        DfoServer.Game.Raids.AntonRaidBattleChange? raidChange = null;
        if (formalDungeon is { } dungeon)
        {
            if (dungeon.Run.Return(dungeon.Run.Snapshot.Identity, Guid.NewGuid()).Outcome != DungeonRunOutcome.Accepted)
                throw new InvalidOperationException("组队退场不能结束已复核的实例");
            dungeon.Ground.ExpireRun();
            raidChange = ReleaseCurrentRaidDungeon();
            ReleaseCurrentEvildomWarDungeon();
            formalDungeon = null;
            CrossConsumableCooldownBoundary(ConsumableMaintenance.Town);
        }
        CancelDungeonContinuation();
        pendingQuestReplay = null;
        CurrentDungeonSelection = null;
        SetState(SessionPhase.Town);
        if (prepared.Kind is PartyDungeonExitKind.ResultSelection or PartyDungeonExitKind.HellSelection)
        {
            CurrentDungeonSelection = new(++dungeonSelectionId, SelectionEpoch, committed.Id, committed.Position, committed.Position);
            SetState(SessionPhase.MapSelect);
        }
        return prepared.Kind switch
        {
            PartyDungeonExitKind.ResultReturn or PartyDungeonExitKind.EvildomWarRetreat or PartyDungeonExitKind.FiendwarKeyRecovery => new PartyDungeonNotice(PartyDungeonNoticeKind.ResultReturn, member, members,
                Travel: new(committed, owner.Generation, SelectionEpoch) { RaidBattleChange = raidChange }),
            _ => new PartyDungeonNotice(prepared.Kind == PartyDungeonExitKind.SelectionReturn ? PartyDungeonNoticeKind.SelectionReturn :
                prepared.Kind == PartyDungeonExitKind.ResultSelection ? PartyDungeonNoticeKind.ResultSelection : PartyDungeonNoticeKind.Selection,
                member, members, Selection: new(committed, owner.Generation, SelectionEpoch, dungeonSelectionId), RaidBattleChange: raidChange)
        };
    });

}
