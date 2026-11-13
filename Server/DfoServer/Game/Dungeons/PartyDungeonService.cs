using DfoServer.Game.Accounts;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace DfoServer.Game.Dungeons;

internal sealed partial class PartyDungeonService(PartyService parties, IPartyDungeonStore store, IEvildomWarStore? warStore = null,
    Func<int, int>? hiddenDraw = null, IEvildomWarGuideStore? guideStore = null)
{
    // 调用方已提交领取并仍持有队伍命令门禁；这里只冻结事件、同步入发送队列，不等待队友上报或网络。
    public void PublishSettlementCard(PartyDungeonInstance instance, GameSession source, IReadOnlyList<DungeonCardView?> snapshot)
    {
        var frozen = snapshot.ToImmutableArray();
        var notices = instance.Members.Where(member => !ReferenceEquals(member.Actor.Session, source) &&
            ReferenceEquals(member.Actor.Session.PartyDungeon, instance))
            .Select(member => new PartyNotice(PartyNoticeKind.SettlementCard, member, Dungeon: instance) { CardEvent = frozen });
        parties.Publish(notices, presenceChanged: false);
    }
    public int RefundEvildomWar(GameSession session) => warStore is null ? 0 : session.RefundEvildomWar(warStore);

    public async Task RecoverEvildomWarAsync(GameSession session, EvildomWarCatalog catalog, CancellationToken stop = default)
    {
        if (warStore is null) return;
        // 首名重连者可能尚无Party；门禁在任何owner之外等待，避免同时重建两个同场队伍。
        await parties.EvildomWarRecoveryGate.WaitAsync(stop);
        try
        {
            if (session.LoadEvildomWarRecovery(warStore) is not { } recovery) return;
            var view = recovery.Checkpoint.View;
            var existing = parties.Snapshot().SingleOrDefault(p => p.EvildomWarBattleId == view.BattleId);
            var partyGate = existing?.Commands;
            if (partyGate is not null) await partyGate.WaitAsync(stop);
            try
            {
                parties.Publish(parties.RemoveStaleEvildomWarMembers(view.BattleId));
                existing = parties.Snapshot().SingleOrDefault(p => p.EvildomWarBattleId == view.BattleId);
                EvildomWarBattle? battle = null;
                foreach (var member in existing?.Members ?? [])
                {
                    try { battle = member.Actor.Session.ReadEvildomWar()?.Battle; }
                    catch (InvalidOperationException) when (!member.Actor.Owner.IsCurrent) { }
                    if (battle is not null) break;
                }
                battle ??= EvildomWarBattle.Restore(recovery.Checkpoint);
                var notices = parties.RestoreEvildomWarMember(session, view.BattleId, recovery.Payment.Slot, view.Mode,
                    party => session.RestoreEvildomWar(warStore, catalog, party, battle, recovery));
                parties.Publish(notices);
            }
            finally { partyGate?.Release(); }
        }
        finally { parties.EvildomWarRecoveryGate.Release(); }
    }

    public string? StartEvildomWar(GameSession leader, EvildomWarCatalog catalog)
    {
        var party = parties.Current(leader);
        if (party is null || !party.Members.Any(member => member.Slot == party.Leader && ReferenceEquals(member.Actor.Session, leader)))
            return "party-leader-required";
        return AccountOwners.Lease.ExecuteGroup(party.Members.Select(member => (member.Actor.Owner, member.Actor.Character.Id)).ToArray(), () =>
        {
            if (!parties.IsCurrent(party)) return "party-changed";
            if (warStore is not null)
                foreach (var member in party.Members) member.Actor.Session.RefundEvildomWar(warStore);
            foreach (var member in party.Members)
                if (member.Actor.Session.EvildomWarStartRejection(catalog, party) is { } reason) return reason;
            if (party.Settings.KindArgument == 2 && guideStore is not null && !leader.TryBindEvildomWarGuide(guideStore))
                return "guide-account-limit";
            var startedAt = leader.EvildomWarNow;
            var deadline = startedAt.AddSeconds(catalog.LimitTime);
            byte[] attributes = [0, 1, 2, 3];
            RandomNumberGenerator.Shuffle(attributes.AsSpan());
            int ordinal = 0;
            // 规格9.2规定四图随机钥匙/法阵/剑/盾，王座开局封锁；不伪造剑盾增益或战役通关奖励。
            var dungeons = catalog.Dungeons.Where(row => row.Mode == party.Settings.KindArgument).Select(row =>
                new EvildomWarDungeonState(row.Id, row.Kind, row.Kind == 0 ? attributes[ordinal++] : (byte)0,
                    row.Kind == 0 ? (byte)0 : (byte)3)).ToImmutableArray();
            byte mode = checked((byte)party.Settings.KindArgument);
            var hidden = EvildomWarHiddenPolicy.Select(mode, dungeons, catalog.HiddenDungeons, hiddenDraw ?? RandomNumberGenerator.GetInt32);
            var battle = new EvildomWarBattle(mode, deadline, dungeons, hidden, startedAt);
            var bound = parties.BindEvildomWar(party, battle.View.BattleId);
            if (bound is null) return "party-changed";
            foreach (var member in bound.Members) member.Actor.Session.StartEvildomWar(catalog, bound, battle, warStore);
            return null;
        });
    }

    internal static PartyDungeonNotice[] Exit(IPartyDungeonStore store, PartyMember[] members, PartyDungeonExitKind kind) =>
        Exit(store, members, kind, out _);

    static PartyDungeonNotice[] Exit(IPartyDungeonStore store, PartyMember[] members, PartyDungeonExitKind kind, out string? failureReason)
    {
        failureReason = null;
        var plans = members.Select(m => m.Actor.Session.PreparePartyExit(kind)).ToArray();
        if (plans.Any(p => p is null)) return [];
        var reservations = plans.Select(p => p!.Reservation).ToArray();
        var committed = kind == PartyDungeonExitKind.FiendwarKeyRecovery
            ? store.TryCommitPartyDungeonRecoveryExit(reservations, out failureReason) : store.CommitPartyDungeonExit(reservations);
        if (committed is null) return [];
        var notices = members.Select((m, i) => m.Actor.Session.InstallPartyExit(plans[i]!, committed[i], m, members)).ToArray();
        return members[0].Actor.Session.ReadEvildomWar() is { } war
            ? [..notices, ..EvildomWarNotices(members, war.Battle.View)] : notices;
    }

    internal static PartyDungeonNotice[] EvildomWarNotices(PartyMember[] members, EvildomWarView view) => members.Select(member =>
        new PartyDungeonNotice(PartyDungeonNoticeKind.EvildomWarUpdated, member, members, WarView: view)).ToArray();

    public PartyDungeonNotice[] ReturnSelection(GameSession leader)
    {
        var party = parties.Current(leader);
        if (party is null || !party.Members.Any(m => m.Slot == party.Leader && ReferenceEquals(m.Actor.Session, leader))) return [];
        return AccountOwners.Lease.ExecuteGroup(party.Members.Select(m => (m.Actor.Owner, m.Actor.Character.Id)).ToArray(),
            () => parties.IsCurrent(party) ? Exit(store, party.Members, PartyDungeonExitKind.SelectionReturn) : []);
    }
    // 调用方先持有队伍和参与连接命令门禁；回城提交失败时不改变队伍或实例成员。
    public PartyDungeonDeparture Depart(GameSession caller, byte? kickSlot = null, bool preserveSolo = false)
    {
        var party = parties.Current(caller);
        if (party is null && kickSlot is null && caller.IsPreySoloRaid && caller.PartyDungeon is { } solo &&
            solo.Members is [var soloMember] && ReferenceEquals(soloMember.Actor.Session, caller))
        {
            var soloTravel = solo.Execute(_ => caller.ReturnFromFormalDungeon());
            if (soloTravel is null) return new(19, [], []);
            return new(0, [], [..solo.Detach(caller),
                new(PartyDungeonNoticeKind.Departure, soloMember, solo.Members, Travel: soloTravel)]);
        }
        var own = party?.Members.SingleOrDefault(m => ReferenceEquals(m.Actor.Session, caller));
        if (party is null || own is null || !parties.IsCurrent(party)) return new(19, [], []);
        if (kickSlot is not null && (party.Leader != own.Slot || kickSlot == own.Slot)) return new(4, [], []);
        var removed = kickSlot is { } slot ? party.Members.SingleOrDefault(m => m.Slot == slot) : own;
        if (removed?.Actor.Session.PartyDungeon is not { } instance ||
            !instance.Members.Contains(removed)) return new(19, [], []);
        var target = removed.Actor.Session;
        bool preserveRaidSquad = target.IsFormalRaidDungeon;
        var travel = instance.Execute(_ => target.ReturnFromFormalDungeon());
        if (travel is null) return new(19, [], []);
        // 提交完成后仍在命令门禁内移除名册，并释放剩余加载屏障。
        var result = preserveRaidSquad || preserveSolo && party.Members.Length == 1 ? new PartyResult(0, []) : parties.Leave(target);
        if (!result.Accepted) throw new InvalidOperationException("副本回城提交后退队状态不一致");
        // 权限已按原队长和旧槽校验；只标注实际被踢者，不把回城或附带拆队解释为踢人。
        if (kickSlot is not null)
            result = result with { Notices = result.Notices.Select(notice => notice.Kind == PartyNoticeKind.Removed &&
                ReferenceEquals(notice.Party, party) && ReferenceEquals(notice.Recipient, removed)
                    ? notice with { RemovalReason = PartyRemovalReason.Kicked } : notice).ToArray() };
        return new(0, result.Notices, [..instance.Detach(target),
            new(PartyDungeonNoticeKind.Departure, removed, instance.Members, Travel: travel)]);
    }

    public PartyDungeonNotice[] BeginSelection(GameSession leader, uint? evildomDungeon = null)
    {
        var party = parties.Current(leader);
        bool soloPartyContext = party?.Members.Length == 1 && leader.ChannelEntry?.Type is 55 or 64;
        if (party is null || party.Members.Length < 2 && evildomDungeon is null && !soloPartyContext ||
            !party.Members.Any(m => m.Slot == party.Leader && ReferenceEquals(m.Actor.Session, leader))) return [];
        return AccountOwners.Lease.ExecuteGroup(party.Members.Select(m => (m.Actor.Owner, m.Actor.Character.Id)).ToArray(), () =>
        {
            if (!parties.IsCurrent(party) || party.Members.Any(m => (evildomDungeon is null ? !m.Actor.Session.CanBeginPartySelection() :
                !m.Actor.Session.CanBeginEvildomWarSelection(evildomDungeon.Value)) ||
                m.Actor.Session.Selected?.Position.Town != leader.Selected?.Position.Town ||
                m.Actor.Session.Selected?.Position.Area != leader.Selected?.Position.Area)) return [];
            var notices = new List<PartyDungeonNotice>();
            foreach (var member in party.Members)
            {
                bool sceneTransition = member.Actor.Session.Phase == SessionPhase.Town;
                var receipt = evildomDungeon is { } dungeon ? member.Actor.Session.BeginEvildomWarSelection(dungeon) :
                    member.Actor.Session.BeginDungeonSelection(partyAdmission: true);
                if (receipt is null) return [];
                if (!member.Actor.Session.ConfirmNewDungeonSelection(receipt)) return [];
                notices.Add(new(PartyDungeonNoticeKind.Selection, member, party.Members, Selection: receipt, SceneTransition: sceneTransition));
            }
            return notices.ToArray();
        });
    }

    public PartyDungeonNotice[] Enter(GameSession leader, uint dungeon, byte difficulty,
        DungeonCatalog catalog, DungeonRoomCatalog rooms, DungeonMonsterDropCatalog? drops, ItemCatalog? items,
        EpicPieceCatalog? pieces, Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? prepareDrops,
        uint questId = 0, bool hellParty = false, bool hellLegend = false, CrackDimensionRewardServices? crackRewards = null, bool beastMode = false,
        Action<string, byte>? onRejected = null, bool allowDimensionHidden = true, FiendwarEntryPreparation? fiendwarPrepared = null,
        PartySnapshot? preySolo = null)
    {
        // 单人普雷只借本次场景快照复用正式Run与事务，不向普通队伍目录登记。
        var party = preySolo ?? fiendwarPrepared?.Party ?? parties.Current(leader);
        bool soloHeroEntry = party?.Members.Length == 1 && dungeon is 5363 or 400000105;
        if (party is null || preySolo is not null && (fiendwarPrepared is not null || !leader.MatchesPreySoloSelection(preySolo)) ||
            party.Members.Length < 2 && !soloHeroEntry && leader.PartyDungeon is null &&
            leader.ReadEvildomWar() is null && !leader.HasFormalRaidSelection ||
            !party.Members.Any(m => m.Slot == party.Leader && ReferenceEquals(m.Actor.Session, leader)) ||
            party.Members.Any(m => m.Actor.Session.Phase != SessionPhase.MapSelect &&
                m.Actor.Session.FormalDungeonResultFocus() is null) ||
            party.Members.Select(m => m.Actor.Session.Phase).Distinct().Count() != 1) return [];
        if (MarriageDungeonAdmission.Applies(dungeon) && party.Members.Length != 2) return [];
        var previousInstance = leader.PartyDungeon;
        var instance = fiendwarPrepared?.Instance ?? new PartyDungeonInstance(Guid.NewGuid(), party, store);
        uint firstKey = party.Members.Max(m => Math.Max(m.Actor.Session.Selected?.DungeonState?.NextObjectKey ?? 65536u,
            (uint)m.Actor.Character.Id + 1));
        var prepared = fiendwarPrepared?.Prepared ?? new List<(PartyMember Member, GameSession.PartyDungeonPreparation Preparation)>();
        if (leader.FormalDungeonResultFocus() is { } previous)
        {
            hellParty = previous.Entry.HellParty is not null || previous.Entry.IsCosmo || previous.Entry.IsInfernoParty;
            hellLegend = previous.Entry.IsHellLegend;
            questId = dungeon == previous.Entry.DungeonId &&
                leader.Selected?.ActiveMainQuests.Any(q => q.QuestId == previous.Entry.QuestId) == true
                ? previous.Entry.QuestId : 0;
        }
        AntonRaidBattle.DungeonReservation? raidReservation = null;
        Exception? entryFailure = null;
        try
        {
            // 共用物理路线时只在全员尚有隐藏周额度才抽隐藏，避免一人满额就随机拒绝整队。
            bool canUseDimensionHidden = allowDimensionHidden && party.Members.All(member => member.Actor.Session.CanUseDimensionHidden(dungeon));
            if (fiendwarPrepared is null) foreach (var member in party.Members.OrderBy(m => ReferenceEquals(m.Actor.Session, leader) ? 0 : 1))
            {
                var plan = member.Actor.Session.PreparePartyDungeon(dungeon, difficulty, catalog, rooms, drops, items, pieces,
                    prepareDrops, instance.Id, firstKey, prepared.FirstOrDefault().Preparation, questId, hellParty, hellLegend, crackRewards, canUseDimensionHidden, checked((byte)party.Members.Length));
                if (plan is null)
                {
                    if (member.Actor.Session.LastFormalDungeonRejection is { } reason)
                        onRejected?.Invoke(reason, member.Slot);
                    return [];
                }
                prepared.Add((member, plan));
            }
            return AccountOwners.Lease.ExecuteGroup(party.Members.Select(m => (m.Actor.Owner, m.Actor.Character.Id)).ToArray(), () =>
            {
                if ((preySolo is null ? !parties.IsCurrent(party) : !leader.MatchesPreySoloSelection(preySolo)) ||
                    fiendwarPrepared is { Active: false }) return [];
                if (dungeon == EvildomWarChallengeEntryPolicy.DungeonId)
                {
                    if (!EvildomWarChallengeEntryPolicy.AllowsPartyCount(prepared.Count) ||
                        prepared.Any(row => !row.Member.Actor.Session.CanBindEvildomWarChallengeKeys(row.Preparation)))
                        return [];
                    var keys = new FiendwarObjectKeyLeases(instance.Id);
                    foreach (var row in prepared) row.Member.Actor.Session.BindEvildomWarChallengeKeys(row.Preparation, keys);
                }
                var reservations = prepared.Select(p => p.Member.Actor.Session.ReservePartyDungeon(p.Preparation)).ToArray();
                if (reservations.Any(r => r is null) || !leader.IsPartyAdmissionDayCurrent(prepared[0].Preparation)) return [];
                using var raidRun = GameSession.ReserveRaidPartyRun(prepared.Select(row => row.Preparation).ToArray(), out bool raidRequired);
                raidReservation = raidRun;
                if (raidRequired && raidRun is null) return [];
                using var warRun = GameSession.ReserveEvildomWarPartyDungeon(prepared.Select(row => row.Preparation).ToArray(),
                    instance.Id, out bool warRequired);
                if (warRequired && warRun is null) return [];
                var committed = store.CommitPartyDungeonEntry(reservations.Select(r => r!).ToArray());
                var notices = prepared.Select((p, index) => new PartyDungeonNotice(PartyDungeonNoticeKind.Entry, p.Member, party.Members,
                    p.Member.Actor.Session.InstallPartyDungeon(p.Preparation, committed[index], reservations[index]!, instance))).ToArray();
                previousInstance?.ClearCards();
                raidRun?.Install();
                var war = warRun?.Install();
                var entered = GameSession.ObserveRaidPartyRun(party.Members, instance.Id, notices, observeMap: false);
                return war is null ? entered : [..entered, ..EvildomWarNotices(party.Members, war)];
            });
        }
        catch (DungeonEventAdmissionRejectedException)
        {
            onRejected?.Invoke("epic-road-event-closed", party.Members.Single(member => ReferenceEquals(member.Actor.Session, leader)).Slot);
            return [];
        }
        catch (DfoServer.Game.Guild.GuildDungeonAdmissionRejectedException)
        {
            return [];
        }
        catch (SingleDungeonAdmissionRejectedException error)
        {
            onRejected?.Invoke(error.Message, party.Members.Single(member => ReferenceEquals(member.Actor.Session, leader)).Slot);
            return [];
        }
        catch (Exception error) { entryFailure = error; throw; }
        finally
        {
            foreach (var entry in prepared) entry.Member.Actor.Session.DiscardPartyPreparation(entry.Preparation);
            // using预留已在原四人owner组内撤销，此处ExecuteGroup已退出；只发布本次最后释放形成的变化。
            if (raidReservation?.ReleasedChange is { } released)
            {
                try { leader.PublishRaidCombat(released); }
                catch (Exception publicationError) when (entryFailure is not null)
                {
                    // 不让通知失败覆盖真实SQL异常；已提交阶段事实仍可从当前view重同步。
                    entryFailure.Data["RaidPublicationFailure"] = publicationError.GetType().Name;
                }
            }
        }
    }
}

internal sealed record PartyDungeonDeparture(ushort Error, PartyNotice[] PartyNotices, PartyDungeonNotice[] DungeonNotices);
