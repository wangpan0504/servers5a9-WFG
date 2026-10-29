using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using System.Collections.Immutable;

namespace DfoServer.Game.Session;

internal sealed record EvildomWarAdmission(EvildomWarCatalog Catalog, PartySnapshot Party, long Epoch,
    ushort CharacterId, EvildomWarBattle Battle)
{
    public byte Mode => Battle.View.Mode;
    public DateTimeOffset Deadline => Battle.View.Deadline;
    public ImmutableArray<EvildomWarDungeonState> Dungeons => Battle.View.Dungeons;
    public bool Charged { get; set; }
    public uint? SelectionDungeon { get; set; }
    public EvildomWarRewardSelection? RewardSelection { get; set; }
    public EvildomWarRewardPlan? RewardPlan { get; set; }
    public EvildomWarRewardCommit? RewardCommit { get; set; }
    public bool RewardPublished { get; set; }
    public EvildomWarHiddenRewardCommit? HiddenRewardCommit { get; set; }
    public Guid BindingToken { get; init; } = Guid.NewGuid();
    public IEvildomWarStore? Store { get; init; }
}

internal sealed record EvildomWarRewardReceipt(EvildomWarAdmission Admission, long Generation, EvildomWarRewardCommit Commit);
internal sealed record EvildomWarHiddenRewardReceipt(EvildomWarAdmission Admission, long Generation, EvildomWarHiddenRewardCommit Commit);

internal sealed partial class GameSession
{
    EvildomWarAdmission? evildomWar;
    internal Action<Exception>? EvildomWarCleanupFailed { get; set; }

    internal bool TryBindEvildomWarGuide(IEvildomWarGuideStore store) => owner.Execute(() =>
        Ready && ChannelEntry?.Type == 54 && Selected is { Level: >= 95, Position.Town: 67 } character &&
        Phase == SessionPhase.Town && formalDungeon is null && pendingRental is null && !HasPendingTownQuestCommit &&
        store.TryBindEvildomWarGuide(owner, character, DungeonIncountPolicy.Period(EvildomWarNow).Week));

    internal string? EvildomWarStartRejection(EvildomWarCatalog catalog, PartySnapshot party) => owner.Execute(() =>
    {
        if (!Ready || ChannelEntry?.Type != 54 || Selected is not { } character || Phase != SessionPhase.Town ||
            character.Position.Town != catalog.WaitingArea.Town || character.Position.Area != catalog.WaitingArea.Area ||
            formalDungeon is not null || pendingRental is not null || HasPendingTownQuestCommit)
            return "waiting-area-or-state-required";
        // 本服普通/困难支持不足四人的队伍；资源人数作为上限，引导仍限一人。
        if (party.Settings.Kind != 5 || party.Settings.KindArgument > byte.MaxValue ||
            !catalog.MemberLimits.TryGetValue((byte)party.Settings.KindArgument, out int limit) ||
            party.Members.Length < 1 || party.Members.Length > limit)
            return "party-mode-or-member-limit";
        byte mode = (byte)party.Settings.KindArgument;
        if (catalog.Dungeons.Where(row => row.Mode == mode).Any(row => character.Level < row.MinimumLevel)) return "level-required";
        if (CurrentEvildomWar(character) is { } current && (current.Battle.Completion is null || current.RewardCommit is null))
            return "already-started";
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

    internal void StartEvildomWar(EvildomWarCatalog catalog, PartySnapshot party, EvildomWarBattle battle,
        IEvildomWarStore? store = null) => owner.Execute(() =>
    {
        evildomWar = new(catalog, party, SelectionEpoch, Selected!.Id, battle) { Store = store };
        return true;
    });

    internal DateTimeOffset EvildomWarNow => (timeProvider ?? TimeProvider.System).GetUtcNow();

    EvildomWarAdmission? CurrentEvildomWar(Character character) => evildomWar is { } war &&
        war.Epoch == SelectionEpoch && war.CharacterId == character.Id && ChannelEntry?.Type == 54 &&
        character.Position.Town == war.Catalog.WaitingArea.Town && character.Position.Area == war.Catalog.WaitingArea.Area &&
        CurrentParty?.Invoke() is { } party && (ReferenceEquals(party, war.Party) || party.EvildomWarBattleId == war.Battle.View.BattleId) &&
        party.Members.Any(member => ReferenceEquals(member.Actor.Session, this) && member.Actor.Epoch == SelectionEpoch) &&
        (war.Battle.Completion is { } completed ? completed.Facts.Any(fact => fact.Identity.CharacterId == character.Id) :
            (timeProvider ?? TimeProvider.System).GetUtcNow() < war.Deadline) ? war : null;

    internal EvildomWarAdmission? ReadEvildomWar() => owner.Execute(() => Selected is { } character ? CurrentEvildomWar(character) : null);

    internal bool CanRetreatEvildomWar() => owner.Execute(() => Ready && Phase == SessionPhase.Dungeon &&
        Selected is { } character && CurrentEvildomWar(character) is { } war &&
        formalDungeon is { Run.Phase: SinglePlayerDungeonPhase.Dungeon, Run.ClearFact: null } dungeon &&
        ReferenceEquals(dungeon.EvildomWarEntry, war));

    internal void PersistEvildomWarCheckpoint(EvildomWarView before, EvildomWarCheckpoint after)
    {
        if (evildomWar is { Charged: true, Store: { } store } war)
            store.CommitEvildomWarCheckpoint(owner, war.CharacterId, war.BindingToken, before, after);
    }

    internal EvildomWarRecovery? LoadEvildomWarRecovery(IEvildomWarStore store) => owner.Execute(() =>
        Ready && Selected is { } character && ChannelEntry?.Type == 54 && evildomWar is null
            ? store.LoadEvildomWarRecovery(owner, character.Id, EvildomWarNow) : null);

    internal bool RestoreEvildomWar(IEvildomWarStore store, EvildomWarCatalog catalog, PartySnapshot party,
        EvildomWarBattle battle, EvildomWarRecovery recovery) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || Phase != SessionPhase.Town || evildomWar is not null ||
            character.Position.Town != catalog.WaitingArea.Town || character.Position.Area != catalog.WaitingArea.Area) return false;
        Guid token = Guid.NewGuid();
        if (!store.ClaimEvildomWarSeat(owner, character.Id, recovery, token, EvildomWarNow)) return false;
        evildomWar = new(catalog, party, SelectionEpoch, character.Id, battle)
            { Store = store, Charged = true, BindingToken = token };
        battle.RecoverCheckpoint(PersistEvildomWarCheckpoint);
        return true;
    });

    internal void DisconnectEvildomWar() => owner.Execute(() =>
    {
        if (evildomWar is { Charged: true, Store: { } store } war && war.Battle.Completion is null)
        {
            var until = EvildomWarNow.AddSeconds(war.Catalog.DisconnectWaitTime);
            store.DisconnectEvildomWarSeat(owner, war.CharacterId, war.Battle.View.BattleId, war.BindingToken,
                until < war.Deadline ? until : war.Deadline);
        }
        return true;
    });

    internal int RefundEvildomWar(IEvildomWarStore store) => owner.Execute(() =>
    {
        if (Selected is not { } character || Phase != SessionPhase.Town || formalDungeon is not null) return 0;
        var refund = store.RefundExpiredEvildomWarSeats(owner, character, EvildomWarNow);
        if (refund.Seats > 0) Selected = refund.Character;
        return refund.Seats;
    });

    internal ImmutableArray<uint> RecoverEvildomWarRewards(IEvildomWarRewardStore store, EvildomWarRewardPolicy? policy = null)
    {
        var capture = owner.Execute(() => Ready && Selected is { } character
            ? (Character: character, Epoch: SelectionEpoch) : default);
        if (capture.Character is not { } selected) return [];
        var hiddenMails = ImmutableArray.CreateBuilder<uint>();
        if (policy is not null)
        {
            foreach (var laboratory in store.LoadUnrewardedEvildomWarHiddenCompletions(owner, selected.Id))
            {
                var reward = policy.PrepareHidden(laboratory.Mode);
                var snapshot = new EvildomWarRewardSnapshot(laboratory, reward, policy.Materialize(reward, laboratory.Created))
                    { Rule = EvildomWarRewardPolicy.HiddenRule };
                owner.Execute(() =>
                {
                    if (!Ready || !ReferenceEquals(Selected, selected) || SelectionEpoch != capture.Epoch)
                        throw new InvalidOperationException("魔界实验室领奖恢复的角色上下文已变化");
                    var delivered = store.CommitEvildomWarHiddenReward(owner, selected.Id, snapshot);
                    if (!delivered.Duplicate) hiddenMails.AddRange(delivered.MailIds);
                    return true;
                });
            }
            foreach (var completed in store.LoadUnrewardedEvildomWarCompletions(owner, selected.Id))
            {
                var choice = policy.Prepare(completed.Mode, selected, System.Security.Cryptography.RandomNumberGenerator.GetInt32);
                var snapshot = new EvildomWarRewardSnapshot(completed, choice, policy.Materialize(choice, completed.Created));
                owner.Execute(() =>
                {
                    if (!Ready || !ReferenceEquals(Selected, selected) || SelectionEpoch != capture.Epoch)
                        throw new InvalidOperationException("魔界领奖恢复的角色上下文已变化");
                    store.FreezeRecoveredEvildomWarReward(owner, selected.Id, snapshot);
                    return true;
                });
            }
        }
        return owner.Execute(() => Ready && ReferenceEquals(Selected, selected) && SelectionEpoch == capture.Epoch
            ? hiddenMails.ToImmutable().AddRange(store.RecoverEvildomWarRewards(owner, selected.Id)) : []);
    }

    internal EvildomWarHiddenRewardReceipt? CommitEvildomWarHiddenReward(EvildomWarRewardPolicy policy, IEvildomWarRewardStore store)
    {
        var capture = owner.Execute(() => Ready && Selected is { } character && CurrentEvildomWar(character) is { Charged: true } war &&
            war.Battle.HiddenCompletion is { } completed && formalDungeon is { Run.ClearFact: { } fact } dungeon &&
            ReferenceEquals(dungeon.EvildomWarEntry, war) && !dungeon.Run.HasPendingDeath &&
            completed.Facts.Any(own => own.Identity == fact.Identity && own.DungeonId == fact.DungeonId &&
                own.ConfirmedDeathEvents.SequenceEqual(fact.ConfirmedDeathEvents)) &&
            dungeon.Entry.IsTerminalMap(dungeon.Run.Snapshot.CurrentMap)
            ? (War: war, Completion: completed, Character: character, Generation: owner.Generation) : default);
        if (capture.War is not { } current) return null;
        var reward = policy.PrepareHidden(current.Mode);
        var snapshot = new EvildomWarRewardSnapshot(capture.Completion, reward, policy.Materialize(reward, capture.Completion.Created))
            { Rule = EvildomWarRewardPolicy.HiddenRule };
        return owner.Execute(() =>
        {
            if (!Ready || owner.Generation != capture.Generation || SelectionEpoch != current.Epoch ||
                !ReferenceEquals(Selected, capture.Character) || !ReferenceEquals(evildomWar, current)) return null;
            current.HiddenRewardCommit ??= store.CommitEvildomWarHiddenReward(owner, current.CharacterId, snapshot);
            return new EvildomWarHiddenRewardReceipt(current, owner.Generation, current.HiddenRewardCommit);
        });
    }

    internal bool CanPublishEvildomWarHiddenReward(EvildomWarHiddenRewardReceipt receipt)
    {
        try
        {
            return owner.Execute(() => Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.Admission.Epoch &&
                Selected?.Id == receipt.Admission.CharacterId && ReferenceEquals(evildomWar, receipt.Admission) &&
                ReferenceEquals(receipt.Admission.HiddenRewardCommit, receipt.Commit));
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return false; }
    }

    internal bool CanPublishEvildomWar(EvildomWarView view) => owner.Execute(() => Ready && Selected is { } character &&
        CurrentEvildomWar(character) is { } war && war.Battle.View is { } current &&
        current.BattleId == view.BattleId && current.Revision == view.Revision);

    internal EvildomWarRewardReceipt? CommitEvildomWarReward(EvildomWarRewardPolicy policy, IEvildomWarRewardStore store)
    {
        var capture = owner.Execute(() => Ready && Selected is { } character && CurrentEvildomWar(character) is { Charged: true } war &&
            war.Battle.Completion is { } completed && formalDungeon is { } dungeon && ReferenceEquals(dungeon.EvildomWarEntry, war)
            ? (War: war, Completion: completed, Dungeon: dungeon, Character: character, Generation: owner.Generation) : default);
        if (capture.War is not { } current) return null;
        if (current.RewardCommit is null)
        {
            var candidate = current.RewardSelection ?? policy.Prepare(current.Mode, capture.Character, capture.Dungeon.Draw);
            var selection = owner.Execute(() => Current() ? current.RewardSelection ??= candidate : null);
            if (selection is null) return null;
            var planned = current.RewardPlan ?? new(capture.Completion, capture.Dungeon.Run, selection,
                policy.Materialize(selection, capture.Completion.Created));
            return owner.Execute(() =>
            {
                if (!Current()) return null;
                current.RewardPlan ??= planned;
                current.RewardCommit ??= store.CommitEvildomWarReward(owner, current.RewardPlan);
                return new EvildomWarRewardReceipt(current, owner.Generation, current.RewardCommit);
            });
        }
        return owner.Execute(() => Current() ? new EvildomWarRewardReceipt(current, owner.Generation, current.RewardCommit!) : null);

        bool Current() => Ready && owner.Generation == capture.Generation && SelectionEpoch == current.Epoch &&
            ReferenceEquals(Selected, capture.Character) && ReferenceEquals(evildomWar, current) &&
            ReferenceEquals(formalDungeon, capture.Dungeon) && ReferenceEquals(current.Battle.Completion, capture.Completion);
    }

    internal bool CanPublishEvildomWarReward(EvildomWarRewardReceipt receipt)
    {
        try
        {
            return owner.Execute(() => Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.Admission.Epoch &&
                Selected?.Id == receipt.Admission.CharacterId && ReferenceEquals(evildomWar, receipt.Admission) &&
                ReferenceEquals(receipt.Admission.RewardCommit, receipt.Commit));
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return false; }
    }

    internal bool ShouldPublishEvildomWarReward(EvildomWarRewardReceipt receipt) =>
        CanPublishEvildomWarReward(receipt) && !receipt.Admission.RewardPublished;

    internal void ConfirmEvildomWarRewardPublished(EvildomWarRewardReceipt receipt) => owner.Execute(() =>
    {
        if (CanPublishEvildomWarReward(receipt)) receipt.Admission.RewardPublished = true;
        return true;
    });

    // 调用方已持原队伍全员owner；必须先完成全员SQL及Run安装，不能逐成员提前推进。
    internal static EvildomWarBattle.Reservation? ReserveEvildomWarPartyDungeon(PartyDungeonPreparation[] prepared, Guid instance,
        out bool required)
    {
        required = prepared.Any(token => token.Session.PreparedParty(token).Dungeon.EvildomWarEntry is not null);
        if (!required || prepared.Length is < 1 or > 4 || prepared[0].Session.ReadEvildomWar() is not { } first) return null;
        var runs = new List<DungeonRunIdentity>();
        uint? dungeonId = null;
        foreach (var token in prepared)
        {
            var session = token.Session;
            var dungeon = session.PreparedParty(token).Dungeon;
            if (session.ReadEvildomWar() is not { } own || !ReferenceEquals(own.Battle, first.Battle) ||
                !ReferenceEquals(dungeon.EvildomWarEntry, own) ||
                dungeon.Run.Snapshot.Identity.InstanceId != instance || dungeonId is not null && dungeonId != dungeon.Entry.DungeonId) return null;
            dungeonId = dungeon.Entry.DungeonId;
            runs.Add(dungeon.Run.Snapshot.Identity);
        }
        return first.Battle.Reserve(dungeonId!.Value, runs.ToArray(), prepared[0].Session.EvildomWarNow);
    }

    internal static EvildomWarView? CompleteEvildomWarPartyDungeon(PartyMember[] members, Guid instance)
    {
        if (members.Length is < 1 or > 4 || members[0].Actor.Session.ReadEvildomWar() is not { } first) return null;
        var facts = new List<DungeonClearFact>();
        foreach (var member in members)
        {
            var session = member.Actor.Session;
            if (!member.IsCurrent() || session.ReadEvildomWar() is not { } own || !ReferenceEquals(own.Battle, first.Battle) ||
                session.formalDungeon is not { Run.ClearFact: { } fact } dungeon || !ReferenceEquals(dungeon.EvildomWarEntry, own) ||
                dungeon.Run.HasPendingDeath || fact.Identity.InstanceId != instance || fact.Identity != dungeon.Run.Snapshot.Identity ||
                !dungeon.Entry.IsTerminalMap(dungeon.Run.Snapshot.CurrentMap)) return null;
            facts.Add(fact);
        }
        return first.Battle.Complete(facts.ToArray(), members[0].Actor.Session.EvildomWarNow,
            members[0].Actor.Session.PersistEvildomWarCheckpoint) ??
            (first.Battle.Completion is not null || first.Battle.HiddenCompletion is { } hidden &&
                hidden.Facts.Length == facts.Count && hidden.Facts.All(saved => facts.Any(actual => actual.Identity == saved.Identity &&
                    actual.DungeonId == saved.DungeonId && actual.ConfirmedDeathEvents.SequenceEqual(saved.ConfirmedDeathEvents)))
                ? first.Battle.View : null);
    }

    void ReleaseCurrentEvildomWarDungeon(bool cleanup = false, bool defer = false)
    {
        if (formalDungeon is { EvildomWarEntry: { } war } dungeon)
        {
            try
            {
                war.Battle.Release(dungeon.Run.Snapshot.Identity, PersistEvildomWarCheckpoint,
                    defer: defer || war.Store is not null && !owner.IsCurrent);
            }
            catch (Exception error) when (cleanup)
            {
                war.Battle.Release(dungeon.Run.Snapshot.Identity, defer: true);
                EvildomWarCleanupFailed?.Invoke(error);
            }
        }
    }

    internal bool CanBeginEvildomWarSelection(uint dungeon) => owner.Execute(() =>
    {
        if (Selected is { } selected && CurrentEvildomWar(selected) is { Store: not null } current)
            current.Battle.RecoverCheckpoint(PersistEvildomWarCheckpoint);
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
