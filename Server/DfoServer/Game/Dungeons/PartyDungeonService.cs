using DfoServer.Game.Accounts;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal sealed class PartyDungeonService(PartyService parties, IPartyDungeonStore store)
{
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
        bool preserveRaidSquad = target.IsLukeRaidDungeon;
        var travel = instance.Execute(_ => target.ReturnFromFormalDungeon());
        if (travel is null) return new(19, [], []);
        // 提交完成后仍在命令门禁内移除名册，并释放剩余加载屏障。
        var result = preserveRaidSquad || preserveSolo && party.Members.Length == 1 ? new PartyResult(0, []) : parties.Leave(target);
        if (!result.Accepted) throw new InvalidOperationException("副本回城提交后退队状态不一致");
        return new(0, result.Notices, [..instance.Detach(target),
            new(PartyDungeonNoticeKind.Departure, removed, instance.Members, Travel: travel)]);
    }

    public PartyDungeonNotice[] BeginSelection(GameSession leader)
    {
        var party = parties.Current(leader);
        if (party is null || party.Members.Length < 2 ||
            !party.Members.Any(m => m.Slot == party.Leader && ReferenceEquals(m.Actor.Session, leader))) return [];
        return AccountOwners.Lease.ExecuteGroup(party.Members.Select(m => (m.Actor.Owner, m.Actor.Character.Id)).ToArray(), () =>
        {
            if (!parties.IsCurrent(party) || party.Members.Any(m => !m.Actor.Session.CanBeginPartySelection() ||
                m.Actor.Session.Selected?.Position.Town != leader.Selected?.Position.Town ||
                m.Actor.Session.Selected?.Position.Area != leader.Selected?.Position.Area)) return [];
            var notices = new List<PartyDungeonNotice>();
            foreach (var member in party.Members)
            {
                var receipt = member.Actor.Session.BeginDungeonSelection(partyAdmission: true);
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
        if (party is null || party.Members.Length < 2 && leader.PartyDungeon is null && !leader.HasLukeRaidSelection ||
            !party.Members.Any(m => m.Slot == party.Leader && ReferenceEquals(m.Actor.Session, leader)) ||
            party.Members.Any(m => m.Actor.Session.Phase != SessionPhase.MapSelect &&
                m.Actor.Session.FormalDungeonResultFocus() is null) ||
            party.Members.Select(m => m.Actor.Session.Phase).Distinct().Count() != 1) return [];
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
                if (raidRequired && raidRun is null) return [];
                var committed = store.CommitPartyDungeonEntry(reservations.Select(r => r!).ToArray());
                var notices = prepared.Select((p, index) => new PartyDungeonNotice(PartyDungeonNoticeKind.Entry, p.Member, party.Members,
                    p.Member.Actor.Session.InstallPartyDungeon(p.Preparation, committed[index], reservations[index]!, instance))).ToArray();
                raidRun?.Install();
                return notices;
            });
        }
        finally
        {
            foreach (var entry in prepared) entry.Member.Actor.Session.DiscardPartyPreparation(entry.Preparation);
        }
    }
}

internal sealed record PartyDungeonDeparture(ushort Error, PartyNotice[] PartyNotices, PartyDungeonNotice[] DungeonNotices);
