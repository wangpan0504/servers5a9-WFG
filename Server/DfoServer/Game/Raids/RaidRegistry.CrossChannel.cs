namespace DfoServer.Game.Raids;

internal sealed record RaidCrossInvitation(RaidActor Source, RaidActor Target, uint Raid,
    uint DisplayValue, ushort DisplayArgument, DateTimeOffset Expires);

// 只保存入团授权事实；换频道后重新取得当前会话，不延用散人的旧Actor。
internal sealed record RaidCrossAcceptance(RaidActor Leader, string Account, ushort Character, uint Raid,
    DateTimeOffset Expires);
internal sealed record RaidCrossTransfer(RaidCrossAcceptance Acceptance, RaidActor Entrant, RaidParty Party);

internal sealed partial class RaidRegistry
{
    readonly Dictionary<(ushort Source, ushort Target), RaidCrossInvitation> crossInvitations = [];
    readonly Dictionary<(ushort Leader, ushort Entrant), RaidCrossAcceptance> crossAcceptances = [];

    void RemoveCrossRaidLocked(uint raid)
    {
        foreach (var row in crossInvitations.Where(row => row.Value.Raid == raid).ToArray()) crossInvitations.Remove(row.Key);
        foreach (var row in crossAcceptances.Where(row => row.Value.Raid == raid).ToArray()) crossAcceptances.Remove(row.Key);
    }

    void PruneCrossLocked()
    {
        foreach (var row in crossInvitations.Where(row => row.Value.Expires <= Now ||
            !CrossInvitationCurrent(row.Value)).ToArray())
            crossInvitations.Remove(row.Key);
        foreach (var row in crossAcceptances.Where(row => !CrossAcceptanceCurrent(row.Value)).ToArray())
            crossAcceptances.Remove(row.Key);
    }

    bool CrossInvitationCurrent(RaidCrossInvitation invitation) =>
        invitation.Source.CanManage && invitation.Target.CanManage &&
        parties.TryGetValue(invitation.Raid, out var party) && party.Started is null &&
        party.Members.Any(member => member.Actor.Character == party.Leader &&
            (Same(member.Actor, invitation.Source) || Same(member.Actor, invitation.Target)));

    int CrossCount(ushort character) => crossInvitations.Values
        .Select(row => (row.Source.Character, row.Target.Character))
        .Concat(crossAcceptances.Values.Select(row => (row.Leader.Character, row.Character)))
        .Where(pair => pair.Item1 == character || pair.Item2 == character).Distinct().Count();

    bool CrossAcceptanceCurrent(RaidCrossAcceptance acceptance) => acceptance.Expires > Now &&
        acceptance.Leader.CanManage && parties.TryGetValue(acceptance.Raid, out var party) && party.Started is null &&
        party.Leader == acceptance.Leader.Character && party.Members.Any(member => Same(member.Actor, acceptance.Leader));

    public RaidCrossInvitation? InviteCross(RaidActor source, RaidActor target, uint displayValue, ushort displayArgument)
    {
        if (source.Owner.Account == target.Owner.Account) return null;
        return Pair<RaidCrossInvitation>(source, target, () =>
        {
            lock (gate)
            {
                PruneCrossLocked();
                if (!source.CanManage || !target.CanManage) return null;
                var sourceParty = FindCore(source); var targetParty = FindCore(target);
                var party = sourceParty ?? targetParty;
                if (party is null || party.Started is not null || sourceParty is not null && targetParty is not null ||
                    party.Members.Length >= party.Capacity) return null;
                var leader = sourceParty is null ? target : source;
                var entrant = sourceParty is null ? source : target;
                if (party.Leader != leader.Character || leader.Session.ChannelEntry is not { } channel ||
                    party.Type != RaidTypes.FromChannelType(channel.Type) ||
                    RaidAdmissionPlan.Rejection(channel, entrant.Session.Selected!) is not null) return null;
                var key = (source.Character, target.Character);
                if (crossInvitations.TryGetValue(key, out var existing) && Same(existing.Source, source) && Same(existing.Target, target))
                    return existing;
                if (CrossCount(source.Character) >= 20 || CrossCount(target.Character) >= 20)
                    return null;
                var invitation = new RaidCrossInvitation(source, target, party.Key, displayValue, displayArgument, Now.AddSeconds(30));
                crossInvitations[key] = invitation;
                // 队长的邀请本身即是授权；散人确认后客户端可直接换频道，不一定先发823。
                if (sourceParty is not null)
                    crossAcceptances[(leader.Character, entrant.Character)] = new(leader, entrant.Owner.Account,
                        entrant.Character, party.Key, invitation.Expires);
                return invitation;
            }
        });
    }

    public bool CanPublishCross(RaidCrossInvitation invitation)
    {
        lock (gate) return invitation.Expires > Now && invitation.Source.CanManage && invitation.Target.CanManage &&
            crossInvitations.TryGetValue((invitation.Source.Character, invitation.Target.Character), out var current) &&
            ReferenceEquals(current, invitation) && CrossInvitationCurrent(invitation);
    }

    public RaidCrossTransfer? AcceptCross(RaidActor leader, RaidActor applicant)
    {
        return Pair<RaidCrossTransfer>(leader, applicant, () =>
        {
            lock (gate)
            {
                PruneCrossLocked();
                if (!leader.CanManage || !applicant.CanManage || FindCore(applicant) is not null ||
                    FindCore(leader) is not { Started: null } party || party.Leader != leader.Character ||
                    party.Members.Length >= party.Capacity) return null;
                var key = (leader.Character, applicant.Character);
                if (crossAcceptances.TryGetValue(key, out var accepted) && accepted.Account == applicant.Owner.Account &&
                    Same(accepted.Leader, leader) && CrossAcceptanceCurrent(accepted)) return new(accepted, applicant, party);
                if (!crossInvitations.TryGetValue((applicant.Character, leader.Character), out var invitation) ||
                    !Same(invitation.Source, applicant) || !Same(invitation.Target, leader) || invitation.Raid != party.Key)
                    return null;
                var acceptance = new RaidCrossAcceptance(leader, applicant.Owner.Account, applicant.Character,
                    party.Key, invitation.Expires);
                crossAcceptances[key] = acceptance;
                crossInvitations.Remove((applicant.Character, leader.Character));
                return new(acceptance, applicant, party);
            }
        });
    }

    public bool CanPublishCross(RaidCrossTransfer transfer)
    {
        lock (gate) return transfer.Entrant.CanManage && transfer.Entrant.Owner.Account == transfer.Acceptance.Account &&
            crossAcceptances.TryGetValue((transfer.Acceptance.Leader.Character, transfer.Entrant.Character), out var current) &&
            ReferenceEquals(current, transfer.Acceptance) && CrossAcceptanceCurrent(current) &&
            parties.TryGetValue(current.Raid, out var party) && ReferenceEquals(party, transfer.Party);
    }

    RaidJoinResult? ConsumeCross(RaidActor entrant, ushort leaderCharacter)
    {
        RaidCrossAcceptance? captured;
        lock (gate)
        {
            PruneCrossLocked();
            crossAcceptances.TryGetValue((leaderCharacter, entrant.Character), out captured);
        }
        if (captured is null || captured.Account != entrant.Owner.Account) return null;
        return Pair<RaidJoinResult>(captured.Leader, entrant, () =>
        {
            lock (gate)
            {
                var key = (leaderCharacter, entrant.Character);
                if (!crossAcceptances.TryGetValue(key, out var current) || !ReferenceEquals(current, captured) ||
                    !CrossAcceptanceCurrent(current) || !entrant.CanManage || entrant.Channel != current.Leader.Channel ||
                    FindCore(entrant) is not null || !parties.TryGetValue(current.Raid, out var party) ||
                    party.Members.Length >= party.Capacity || party.Type != RaidTypes.FromChannelType(entrant.Session.ChannelEntry!.Type) ||
                    RaidAdmissionPlan.Rejection(current.Leader.Session.ChannelEntry, entrant.Session.Selected!) is not null)
                    return new(false);
                byte slot = (byte)Enumerable.Range(0, party.Capacity).First(index => party.Members.All(member => member.Slot != index));
                var updated = party with { Members = party.Members.Add(new(entrant, slot)), Revision = ++revision };
                parties[party.Key] = updated;
                foreach (var row in crossAcceptances.Where(row => row.Value.Character == entrant.Character).ToArray())
                    crossAcceptances.Remove(row.Key);
                foreach (var row in crossInvitations.Where(row => row.Value.Source.Character == entrant.Character ||
                    row.Value.Target.Character == entrant.Character).ToArray()) crossInvitations.Remove(row.Key);
                if (waiting.TryGetValue(entrant.Character, out var wait) && Same(wait.Actor, entrant)) waiting.Remove(entrant.Character);
                return new(true, new(current.Leader, entrant, party.Key, 0, current.Expires), new(party, updated));
            }
        }) ?? new(false);
    }

    RaidJoinResult? DeclineCross(RaidActor recipient, ushort sourceCharacter)
    {
        RaidCrossInvitation? captured;
        lock (gate) crossInvitations.TryGetValue((sourceCharacter, recipient.Character), out captured);
        if (captured is null || !Same(captured.Target, recipient)) return null;
        return Pair<RaidJoinResult>(captured.Source, recipient, () =>
        {
            lock (gate)
            {
                if (!recipient.CanManage || !captured.Source.CanManage || captured.Expires <= Now ||
                    !crossInvitations.TryGetValue((sourceCharacter, recipient.Character), out var current) ||
                    !ReferenceEquals(current, captured)) return new(false);
                crossInvitations.Remove((sourceCharacter, recipient.Character));
                crossAcceptances.Remove((sourceCharacter, recipient.Character));
                return new(true, new(captured.Source, captured.Target, captured.Raid, 0, captured.Expires));
            }
        }) ?? new(false);
    }

    public bool CancelCross(RaidActor entrant, byte server, ushort leaderCharacter)
    {
        return entrant.Owner.Execute(() =>
        {
            lock (gate)
            {
                var key = (leaderCharacter, entrant.Character);
                if (!entrant.IsCurrent || !crossAcceptances.TryGetValue(key, out var current) ||
                    current.Account != entrant.Owner.Account || current.Leader.Channel.ServerIndex != server) return false;
                crossAcceptances.Remove(key);
                crossInvitations.Remove((leaderCharacter, entrant.Character));
                return true;
            }
        });
    }

    public RaidParty[] CrossList(byte type)
    {
        lock (gate)
        {
            PruneEmptyLukeLocked();
            PruneCrossLocked();
            return parties.Values.Where(party => party.Type == type && party.Started is null).OrderBy(party => party.Key).ToArray();
        }
    }
}
