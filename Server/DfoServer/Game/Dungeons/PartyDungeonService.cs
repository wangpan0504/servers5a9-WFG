using DfoServer.Game.Accounts;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace DfoServer.Game.Dungeons;

internal sealed class PartyDungeonService(PartyService parties, IPartyDungeonStore store)
{
    public string? StartEvildomWar(GameSession leader, EvildomWarCatalog catalog)
    {
        var party = parties.Current(leader);
        if (party is null || !party.Members.Any(member => member.Slot == party.Leader && ReferenceEquals(member.Actor.Session, leader)))
            return "party-leader-required";
        return AccountOwners.Lease.ExecuteGroup(party.Members.Select(member => (member.Actor.Owner, member.Actor.Character.Id)).ToArray(), () =>
        {
            if (!parties.IsCurrent(party)) return "party-changed";
            foreach (var member in party.Members)
                if (member.Actor.Session.EvildomWarStartRejection(catalog, party) is { } reason) return reason;
            var deadline = leader.EvildomWarNow.AddSeconds(catalog.LimitTime);
            byte[] attributes = [0, 1, 2, 3];
            RandomNumberGenerator.Shuffle(attributes.AsSpan());
            int ordinal = 0;
            // 规格9.2规定四图随机钥匙/法阵/剑/盾，王座开局封锁；不伪造剑盾增益或战役通关奖励。
            var dungeons = catalog.Dungeons.Where(row => row.Mode == party.Settings.KindArgument).Select(row =>
                new EvildomWarDungeonState(row.Id, row.Kind, row.Kind == 0 ? attributes[ordinal++] : (byte)0,
                    row.Kind == 0 ? (byte)0 : (byte)3)).ToImmutableArray();
            foreach (var member in party.Members) member.Actor.Session.StartEvildomWar(catalog, party, deadline, dungeons);
            return null;
        });
    }

    internal static PartyDungeonNotice[] Exit(IPartyDungeonStore store, PartyMember[] members, PartyDungeonExitKind kind)
    {
        var plans = members.Select(m => m.Actor.Session.PreparePartyExit(kind)).ToArray();
        if (plans.Any(p => p is null)) return [];
        var committed = store.CommitPartyDungeonExit(plans.Select(p => p!.Reservation).ToArray());
        return members.Select((m, i) => m.Actor.Session.InstallPartyExit(plans[i]!, committed[i], m, members)).ToArray();
    }

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
        return new(0, result.Notices, [..instance.Detach(target),
            new(PartyDungeonNoticeKind.Departure, removed, instance.Members, Travel: travel)]);
    }

    public PartyDungeonNotice[] BeginSelection(GameSession leader, uint? evildomDungeon = null)
    {
        var party = parties.Current(leader);
        if (party is null || party.Members.Length < 2 && evildomDungeon is null ||
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
                var receipt = evildomDungeon is { } dungeon ? member.Actor.Session.BeginEvildomWarSelection(dungeon) :
                    member.Actor.Session.BeginDungeonSelection(partyAdmission: true);
                if (receipt is null) return [];
                if (!member.Actor.Session.ConfirmNewDungeonSelection(receipt)) return [];
                notices.Add(new(PartyDungeonNoticeKind.Selection, member, party.Members, Selection: receipt));
            }
            return notices.ToArray();
        });
    }

    public PartyDungeonNotice[] Enter(GameSession leader, uint dungeon, byte difficulty,
        DungeonCatalog catalog, DungeonRoomCatalog rooms, DungeonMonsterDropCatalog? drops, ItemCatalog? items,
        EpicPieceCatalog? pieces, Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? prepareDrops,
        uint questId = 0, bool hellParty = false, bool hellLegend = false, CrackDimensionRewardServices? crackRewards = null)
    {
        var party = parties.Current(leader);
        if (party is null || party.Members.Length < 2 && leader.PartyDungeon is null && leader.ReadEvildomWar() is null && !leader.HasFormalRaidSelection ||
            !party.Members.Any(m => m.Slot == party.Leader && ReferenceEquals(m.Actor.Session, leader)) ||
            party.Members.Any(m => m.Actor.Session.Phase != SessionPhase.MapSelect &&
                m.Actor.Session.FormalDungeonResultFocus() is null) ||
            party.Members.Select(m => m.Actor.Session.Phase).Distinct().Count() != 1) return [];
        if (MarriageDungeonAdmission.Applies(dungeon) && party.Members.Length != 2) return [];
        var instance = new PartyDungeonInstance(Guid.NewGuid(), party, store);
        uint firstKey = party.Members.Max(m => Math.Max(m.Actor.Session.Selected?.DungeonState?.NextObjectKey ?? 65536u,
            (uint)m.Actor.Character.Id + 1));
        var prepared = new List<(PartyMember Member, GameSession.PartyDungeonPreparation Preparation)>();
        if (leader.FormalDungeonResultFocus() is { } previous)
        {
            hellParty = previous.Entry.HellParty is not null || previous.Entry.IsCosmo || previous.Entry.IsInfernoParty;
            hellLegend = previous.Entry.IsHellLegend;
            questId = leader.Selected?.ActiveMainQuests.Any(q => q.QuestId == previous.Entry.QuestId) == true
                ? previous.Entry.QuestId : 0;
        }
        AntonRaidBattle.DungeonReservation? raidReservation = null;
        Exception? entryFailure = null;
        try
        {
            foreach (var member in party.Members.OrderBy(m => ReferenceEquals(m.Actor.Session, leader) ? 0 : 1))
            {
                var plan = member.Actor.Session.PreparePartyDungeon(dungeon, difficulty, catalog, rooms, drops, items, pieces,
                    prepareDrops, instance.Id, firstKey, prepared.FirstOrDefault().Preparation, questId, hellParty, hellLegend, crackRewards);
                if (plan is null) return [];
                prepared.Add((member, plan));
            }
            return AccountOwners.Lease.ExecuteGroup(party.Members.Select(m => (m.Actor.Owner, m.Actor.Character.Id)).ToArray(), () =>
            {
                if (!parties.IsCurrent(party)) return [];
                var reservations = prepared.Select(p => p.Member.Actor.Session.ReservePartyDungeon(p.Preparation)).ToArray();
                if (reservations.Any(r => r is null) || !leader.IsPartyAdmissionDayCurrent(prepared[0].Preparation)) return [];
                using var raidRun = GameSession.ReserveRaidPartyRun(prepared.Select(row => row.Preparation).ToArray(), out bool raidRequired);
                raidReservation = raidRun;
                if (raidRequired && raidRun is null) return [];
                var committed = store.CommitPartyDungeonEntry(reservations.Select(r => r!).ToArray());
                var notices = prepared.Select((p, index) => new PartyDungeonNotice(PartyDungeonNoticeKind.Entry, p.Member, party.Members,
                    p.Member.Actor.Session.InstallPartyDungeon(p.Preparation, committed[index], reservations[index]!, instance))).ToArray();
                raidRun?.Install();
                return GameSession.ObserveRaidPartyRun(party.Members, instance.Id, notices, observeMap: false);
            });
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
