using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Game.Raids;

// 只引用原会话和登记身份，不保存第二份在线角色或资产快照。
internal sealed record RaidActor(GameSession Session, AccountOwners.Lease Owner, long Epoch,
    ushort Character, string Name, ChannelIdentity Channel)
{
    public bool IsCurrent => Owner.IsCurrent && Session.SelectionEpoch == Epoch && Session.Selected?.Id == Character;
    public bool CanManage => IsCurrent && Session.CanChangeTownParty &&
        RaidAdmissionPlan.Rejection(Session.ChannelEntry, Session.Selected!) is null;
}
internal sealed record RaidMember(RaidActor Actor, byte Slot, byte Squad = 0, bool Inactive = false)
{
    public byte PreySlot { get; init; }
}
internal sealed record RaidParty(uint Key, byte Type, string Title, byte[] TitleBytes, ChannelIdentity Channel,
    ushort Leader, ImmutableArray<RaidMember> Members, int Capacity, long Revision)
{
    public string LeaderName => Members.Single(m => m.Actor.Character == Leader).Actor.Name;
    public RaidStarted? Started { get; init; }
    public int StartMinimum { get; init; } = 1;
    public bool Guide { get; init; }
    public byte RecruitRoles { get; init; } = 7;
}
internal sealed record RaidChange(RaidParty Before, RaidParty? After, RaidActor? Removed = null, bool Kicked = false)
{
    internal TownTravelReceipt? TownReturn { get; init; }
    internal ushort? StateMember { get; init; }
}
internal sealed record RaidWaiting(RaidActor Actor, byte Type);
internal sealed record RaidInvitation(RaidActor Source, RaidActor Target, uint Raid, uint Peer, DateTimeOffset Expires);
internal sealed record RaidJoinResult(bool Accepted, RaidInvitation? Invitation = null, RaidChange? Change = null);
internal sealed record RaidDirectoryChange(RaidParty? Party = null, RaidWaiting? Waiting = null, bool Removed = false);
internal sealed record RaidRemovalNotices(ImmutableArray<RaidWaiting> Waiting, ImmutableArray<RaidChange> Changes);

// 进程内招募聚合；锁内不访问数据库、不取得账号锁、不等待网络。业务先持原owner，双人操作按既有锁序。
internal sealed partial class RaidRegistry
{
    readonly object gate = new();
    readonly Dictionary<uint, RaidParty> parties = [];
    readonly Dictionary<ushort, RaidWaiting> waiting = [];
    readonly Dictionary<(ushort Source, ushort Target), RaidInvitation> invitations = [];
    readonly Dictionary<ChannelIdentity, ushort> next = [];
    // 招募窗口订阅只保留原会话身份，随选角失效清理；不是在线角色或资产目录。
    readonly Dictionary<GameSession, RaidActor> observers = [];
    readonly TimeProvider timer;
    AntonRaidRejoin? rejoin;
    long revision;

    public RaidRegistry() { timer = TimeProvider.System; }
    public RaidRegistry(TimeProvider? clock) { timer = clock ?? TimeProvider.System; }
    DateTimeOffset Now => timer.GetUtcNow();

    internal RaidPreparationResult CommitPreparation(RaidPreparationPlan plan, PartyService ordinary,
        RaidPartyBatch batch, CancellationToken cancellation)
    {
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!ReferenceEquals(batch.Raid, plan) || !plan.IsCurrent(this))
                return RaidPreparationResult.Reject("raid-preparation-stale");
        }
        // 全团current owner阻止成员、编队、委任、离队、开战与当前会话失效；旧generation收尾只匹配旧Session/epoch。
        // 复验至安装没有await、SQL或外部回调。不持目录gate等待Party gate，兼容普通队伍中的StartedRaid查询。
        return ordinary.CommitRaidPreparation(batch, cancellation);
    }

    internal void BindRejoin(AntonRaidRejoin service) => rejoin = service;

    // 在移除成员前调用。只读取原 Battle 视图和名册，不取得其它账号锁。
    internal AntonRaidDisconnectFact? CaptureAntonDisconnect(RaidActor actor, AntonRaidBattle battle, Guid? run, bool wasLoading)
    {
        if (rejoin is not { Enabled: true } || battle.Kind != RaidKind.Anton) return null;
        var view = battle.View;
        if (view.Instance != battle.Instance || view.State is not (AntonRaidBattleState.Fighting or AntonRaidBattleState.Reward) ||
            view.ClockFaulted || view.PhaseExpired) return null;
        lock (gate)
        {
            var party = FindCore(actor);
            if (party is not { Type: 0, Started: { } started } || started.InstanceId != battle.Instance) return null;
            var member = party.Members.FirstOrDefault(m => Same(m.Actor, actor));
            return member is null ? null : new AntonRaidDisconnectFact(actor.Owner.Account, actor.Character, actor.Epoch, actor.Owner,
                party.Key, battle.Instance, actor.Channel, member.Slot, member.Squad, run, wasLoading);
        }
    }

    internal bool CommitAntonDisconnect(AntonRaidDisconnectFact fact)
    {
        if (rejoin is not { Enabled: true }) return false;
        lock (gate)
        {
            if (!parties.TryGetValue(fact.Key, out var party) || party.Type != 0 || party.Channel != fact.Channel ||
                party.Started?.InstanceId != fact.Instance ||
                party.Members.Any(m => m.Actor.Character == fact.Character && m.Actor.Epoch == fact.Epoch)) return false;
        }
        return rejoin.Record(fact);
    }

    internal bool AntonBattleAlive(AntonRaidDisconnectFact fact)
    {
        RaidMember[] members;
        lock (gate)
        {
            if (!parties.TryGetValue(fact.Key, out var party) || party.Type != 0 || party.Channel != fact.Channel ||
                party.Started?.InstanceId != fact.Instance) return false;
            members = party.Members.ToArray();
        }
        foreach (var member in members)
        {
            AntonRaidBattle? battle;
            try { battle = member.Actor.Session.CurrentRaidBattle(member.Actor); }
            catch (InvalidOperationException) { continue; }
            if (battle is not { Kind: RaidKind.Anton } || battle.Instance != fact.Instance) continue;
            var view = battle.View;
            return view.Instance == fact.Instance && view.State is AntonRaidBattleState.Fighting or AntonRaidBattleState.Reward &&
                !view.ClockFaulted && !view.PhaseExpired;
        }
        return false;
    }

    internal bool AntonMemberCurrent(AntonRaidDisconnectFact fact)
    {
        lock (gate) return parties.TryGetValue(fact.Key, out var party) &&
            party.Members.Any(m => m.Actor.Character == fact.Character && m.Actor.IsCurrent);
    }

    void PruneEmptyLukeLocked()
    {
        foreach (var row in parties.Where(row => row.Value.Type is 1 or 2 && row.Value.Members.IsEmpty &&
            rejoin?.HasLukeParty(row.Key) != true).ToArray()) parties.Remove(row.Key);
    }
    public int Count { get { lock (gate) { PruneEmptyLukeLocked(); return parties.Count; } } }
    public RaidParty? Find(RaidActor actor) { lock (gate) return FindCore(actor); }
    public RaidParty? FindCurrent(GameSession session, long epoch)
    { lock (gate) return parties.Values.FirstOrDefault(p => p.Members.Any(m => ReferenceEquals(m.Actor.Session, session) && m.Actor.Epoch == epoch && m.Actor.IsCurrent)); }

    // 调用方已按固定全团账号顺序取锁；聚合gate内只复验与提交，不等待网络。
    internal RaidStartResult CommitStart(RaidActor actor, RaidParty expected, System.Collections.Immutable.ImmutableArray<RaidStartReservation> reservations, GameWorld.Raids.RaidCost cost,
        Func<(RaidStarted Start, System.Collections.Immutable.ImmutableArray<Characters.Character> Characters, Game.Party.PartyNotice[] Notices)?> commit)
    {
        lock (gate)
        {
            var current = FindCore(actor);
            if (current?.Started is not null) return new(0, "already-started", Replay: true);
            if (!ReferenceEquals(current, expected) || expected.Leader != actor.Character ||
                reservations.Any(r => !r.Actor.Session.MatchesRaidStart(r))) return new(19, "raid-members-changed");
        }
        // 全团owner阻止本团变更；数据库等待不占用其它攻坚队共用的目录gate。
        if (commit() is not { } result) return new(19, "member-party-conflict");
        lock (gate)
        {
            var started = expected with { Started = result.Start, Revision = ++revision };
            parties[expected.Key] = started;
            foreach (var key in invitations.Where(i => i.Value.Raid == expected.Key).Select(i => i.Key).ToArray()) invitations.Remove(key);
            RemoveCrossRaidLocked(expected.Key);
            return new(0, "started", new(started, reservations, result.Characters, result.Notices, cost));
        }
    }
    public RaidParty? Find(uint key, ChannelIdentity channel)
    { lock (gate) { PruneEmptyLukeLocked(); return parties.TryGetValue(key, out var party) && party.Channel == channel ? party : null; } }
    public RaidParty[] List(ChannelIdentity channel, byte type)
    { lock (gate) { PruneEmptyLukeLocked(); return parties.Values.Where(p => p.Channel == channel && p.Type == type && p.Started is null).OrderBy(p => p.Key).ToArray(); } }
    public bool IsCurrent(RaidParty party)
    { lock (gate) return parties.TryGetValue(party.Key, out var current) && ReferenceEquals(current, party); }
    public void Observe(RaidActor actor) { lock (gate) if (actor.IsCurrent) observers[actor.Session] = actor; }
    public bool CanPublishDirectory(RaidActor recipient, RaidDirectoryChange change)
    {
        lock (gate)
        {
            if (!recipient.IsCurrent || !observers.TryGetValue(recipient.Session, out var current) || !Same(current, recipient)) return false;
            if (change.Party is { } party)
                return change.Removed ? !parties.ContainsKey(party.Key) : parties.TryGetValue(party.Key, out var p) && ReferenceEquals(p, party);
            if (change.Waiting is { } row)
                return change.Removed ? !waiting.ContainsKey(row.Actor.Character) :
                    waiting.TryGetValue(row.Actor.Character, out var w) && ReferenceEquals(w, row);
            return false;
        }
    }
    public RaidWaiting[] Waiting(ChannelIdentity channel, byte type)
    { lock (gate) return waiting.Values.Where(w => w.Actor.IsCurrent && w.Actor.Channel == channel && w.Type == type).OrderBy(w => w.Actor.Character).ToArray(); }
    // 创建前捕获将被移除的候战项；它可能属于接管前尚未完成收尾的旧会话。
    public RaidWaiting? Waiting(RaidActor actor)
    { lock (gate) return actor.IsCurrent ? waiting.GetValueOrDefault(actor.Character) : null; }
    public bool IsWaitingSnapshot(ChannelIdentity channel, byte type, RaidWaiting[] rows)
    {
        lock (gate)
        {
            var current = waiting.Values.Where(w => w.Actor.IsCurrent && w.Actor.Channel == channel && w.Type == type).ToArray();
            return current.Length == rows.Length && rows.All(row => current.Contains(row));
        }
    }
    public bool IsListSnapshot(ChannelIdentity channel, byte type, RaidParty[] rows)
    {
        lock (gate) return parties.Values.Count(p => p.Channel == channel && p.Type == type && p.Started is null) == rows.Length &&
            rows.All(row => parties.TryGetValue(row.Key, out var current) && ReferenceEquals(current, row));
    }
    RaidParty? FindCore(RaidActor actor) => parties.Values.FirstOrDefault(p => p.Members.Any(m => Same(m.Actor, actor)));
    static bool Same(RaidActor a, RaidActor b) => ReferenceEquals(a.Session, b.Session) && a.Epoch == b.Epoch &&
        ReferenceEquals(a.Owner, b.Owner) && a.Character == b.Character;

    public RaidParty? Create(RaidActor actor, byte type, string title, byte[] titleBytes, int capacity, int startMinimum = 1, bool guide = false,
        byte recruitRoles = 7) => actor.Owner.Execute(() =>
    {
        lock (gate)
        {
            if (!actor.CanManage || recruitRoles is < 1 or > 7 || capacity is < 1 or > 255 || guide && (type is not (2 or 3) || capacity != 1 || startMinimum != 1) || FindCore(actor) is not null ||
                parties.Values.Any(p => p.Members.Any(m => m.Actor.Character == actor.Character && m.Actor.IsCurrent))) return null;
            uint key = Allocate(actor.Channel);
            var party = new RaidParty(key, type, title, titleBytes, actor.Channel, actor.Character,
                [new(actor, 0, guide ? (byte)1 : (byte)0) { PreySlot = type == 3 && guide ? (byte)11 : (byte)0 }], capacity, ++revision) { StartMinimum = startMinimum, Guide = guide, RecruitRoles = recruitRoles };
            parties.Add(key, party);
            waiting.Remove(actor.Character);
            return party;
        }
    });

    // 返回值对应ACK667已证的两种拒绝；其它非法身份由调用方保持未处理。
    public ushort? AddWaiting(RaidActor actor, byte type) => actor.Owner.Execute<ushort?>(() =>
    {
        lock (gate)
        {
            if (!actor.CanManage) return 0x13;
            if (FindCore(actor) is not null) return 0x13;
            if (waiting.TryGetValue(actor.Character, out var current) && Same(current.Actor, actor)) return 0x12;
            waiting[actor.Character] = new(actor, type);
            return null;
        }
    });

    public RaidChange? Rename(RaidActor actor, byte type, string title, byte[] titleBytes, byte? recruitRoles = null) => actor.Owner.Execute(() =>
    {
        lock (gate)
        {
            var old = FindCore(actor);
            if (!actor.CanManage || recruitRoles is < 1 or > 7 || old is null || old.Started is not null || old.Leader != actor.Character || old.Type != type) return null;
            var updated = old with { Title = title, TitleBytes = titleBytes, RecruitRoles = recruitRoles ?? old.RecruitRoles, Revision = ++revision };
            parties[old.Key] = updated;
            return new RaidChange(old, updated);
        }
    });

    public RaidChange? Assign(RaidActor actor, ushort target, byte squad) => actor.Owner.Execute(() =>
    {
        lock (gate)
        {
            var old = FindCore(actor);
            if (!actor.CanManage || old is null || old.Started is not null || old.Leader != actor.Character || squad > (old.Capacity + 3) / 4) return null;
            int index = -1;
            for (int at = 0; at < old.Members.Length; at++)
                if (old.Members[at].Actor.Character == target && old.Members[at].Actor.IsCurrent) { index = at; break; }
            if (index < 0 || squad != 0 && old.Members.Count(m => m.Squad == squad && m.Actor.Character != target) >= 4) return null;
            if (old.Members[index].Squad == squad) return new(old, old);
            var updated = old with { Members = old.Members.SetItem(index, old.Members[index] with { Squad = squad }), Revision = ++revision };
            parties[old.Key] = updated;
            return new RaidChange(old, updated);
        }
    });

    // 普雷669/op2使用十位分队、个位槽位；与普通op0的分队字节分别保存。
    public RaidChange? AssignPrey(RaidActor actor, ushort target, byte slot) => actor.Owner.Execute<RaidChange?>(() =>
    {
        lock (gate)
        {
            var old = FindCore(actor);
            int squad = slot / 10, seat = slot % 10;
            if (!actor.CanManage || old is not { Type: 3, Started: null } || old.Leader != actor.Character ||
                squad < 1 || squad > (old.Capacity + 3) / 4 || seat is < 1 or > 4 ||
                old.Guide && slot != 11) return null;
            int index = -1;
            for (int at = 0; at < old.Members.Length; at++)
                if (old.Members[at].Actor.Character == target && old.Members[at].Actor.IsCurrent) { index = at; break; }
            if (index < 0 || old.Members.Any(m => m.Actor.Character != target && m.PreySlot == slot) ||
                old.Members.Count(m => m.Actor.Character != target && m.Squad == squad) >= 4) return null;
            if (old.Members[index].PreySlot == slot) return new(old, old);
            var updated = old with { Members = old.Members.SetItem(index,
                old.Members[index] with { Squad = checked((byte)squad), PreySlot = slot }), Revision = ++revision };
            parties[old.Key] = updated;
            return new(old, updated);
        }
    });

    public RaidChange? TransferLeadership(RaidActor actor, ushort target)
    {
        if (target == actor.Character) return null;
        RaidActor? recipient;
        lock (gate) recipient = FindCore(actor)?.Members.FirstOrDefault(m => m.Actor.Character == target)?.Actor;
        if (recipient is null) return null;
        // 固定双方再按账号锁序复验；接管、重选或退出后的旧成员不能接任。
        return Pair<RaidChange>(actor, recipient, () =>
        {
            lock (gate)
            {
                var old = FindCore(actor);
                if (!actor.CanManage || !recipient.CanManage || old is null || old.Started is not null || old.Leader != actor.Character ||
                    !old.Members.Any(m => Same(m.Actor, recipient))) return null;
                var updated = old with { Leader = target, Revision = ++revision };
                parties[old.Key] = updated;
                foreach (var key in invitations.Where(p => p.Value.Raid == old.Key).Select(p => p.Key).ToArray())
                    invitations.Remove(key);
                RemoveCrossRaidLocked(old.Key);
                return new(old, updated);
            }
        });
    }

    public RaidChange? Leave(RaidActor actor, ushort target) => actor.Owner.Execute(() =>
    {
        lock (gate)
        {
            var party = FindCore(actor);
            if (!actor.CanManage || party is null || target != actor.Character && (party.Leader != actor.Character || party.Started is not null)) return null;
            var removed = party.Members.FirstOrDefault(m => m.Actor.Character == target);
            return removed is null ? null : RemoveCore(party, removed.Actor, target != actor.Character);
        }
    });

    RaidChange RemoveCore(RaidParty old, RaidActor actor, bool kicked = false)
    {
        var members = old.Members.Where(m => !Same(m.Actor, actor)).ToImmutableArray();
        bool keepLuke = old.Type is 1 or 2 && old.Started is not null && rejoin?.HasLukeParty(old.Key) == true;
        RaidParty? updated = members.IsEmpty && !keepLuke ? null : old with
        {
            Members = members,
            Leader = old.Leader == actor.Character && !members.IsEmpty ? members.MinBy(m => m.Slot)!.Actor.Character : old.Leader,
            Revision = ++revision
        };
        if (updated is null) { parties.Remove(old.Key); rejoin?.DropParty(old.Key); }
        else parties[old.Key] = updated;
        foreach (var key in invitations.Where(p => p.Value.Raid == old.Key ||
            Same(p.Value.Source, actor) || Same(p.Value.Target, actor)).Select(p => p.Key).ToArray()) invitations.Remove(key);
        RemoveCrossRaidLocked(old.Key);
        return new(old, updated is { Members.IsEmpty: true } ? null : updated, actor, kicked);
    }

    // 可在旧generation收尾调用，只按原Session/epoch移除，绝不按角色号删除接管者。
    public void RemoveSession(GameSession session, long epoch) => PublishRemoval(RemoveSessionNotices(session, epoch));

    // 生命周期在原owner内只提交；通知由最外层owner释放后的收尾发布。
    internal RaidRemovalNotices RemoveSessionNotices(GameSession session, long epoch)
    {
        List<RaidChange> changes = [];
        List<RaidWaiting> removedWaiting = [];
        lock (gate)
        {
            if (observers.TryGetValue(session, out var observer) && observer.Epoch == epoch) observers.Remove(session);
            foreach (var row in waiting.Where(p => ReferenceEquals(p.Value.Actor.Session, session) && p.Value.Actor.Epoch == epoch).ToArray())
            { waiting.Remove(row.Key); removedWaiting.Add(row.Value); }
            foreach (var party in parties.Values.ToArray())
                if (party.Members.FirstOrDefault(m => ReferenceEquals(m.Actor.Session, session) && m.Actor.Epoch == epoch) is { } member)
                    changes.Add(RemoveCore(party, member.Actor));
            foreach (var key in invitations.Where(p =>
                ReferenceEquals(p.Value.Source.Session, session) && p.Value.Source.Epoch == epoch ||
                ReferenceEquals(p.Value.Target.Session, session) && p.Value.Target.Epoch == epoch).Select(p => p.Key).ToArray()) invitations.Remove(key);
            foreach (var row in crossInvitations.Where(p =>
                ReferenceEquals(p.Value.Source.Session, session) && p.Value.Source.Epoch == epoch ||
                ReferenceEquals(p.Value.Target.Session, session) && p.Value.Target.Epoch == epoch).ToArray()) crossInvitations.Remove(row.Key);
            // 散人换频道只清旧会话通知，已获队长授权的短期凭证仍由新owner复验。
            PruneCrossLocked();
        }
        return new(removedWaiting.ToImmutableArray(), changes.ToImmutableArray());
    }

    internal void PublishRemoval(RaidRemovalNotices notices)
    {
        foreach (var row in notices.Waiting) PublishDirectory(new(Waiting: row, Removed: true));
        foreach (var change in notices.Changes) Publish(change);
    }

    public RaidInvitation? Invite(RaidActor source, RaidActor target, uint peer)
    {
        if (source.Owner.Account == target.Owner.Account || source.Channel != target.Channel) return null;
        return Pair<RaidInvitation>(source, target, () =>
        {
            lock (gate)
            {
                if (!source.CanManage || !target.CanManage) return null;
                var a = FindCore(source); var b = FindCore(target);
                var party = a ?? b;
                if (party is null || party.Started is not null || a is not null && b is not null ||
                    party.Leader != (a is null ? target.Character : source.Character) || party.Members.Length >= party.Capacity ||
                    source.Session.ChannelEntry is not { } channel || party.Type != RaidTypes.FromChannelType(channel.Type)) return null;
                foreach (var key in invitations.Where(p => p.Value.Expires <= Now || !p.Value.Source.IsCurrent || !p.Value.Target.IsCurrent)
                    .Select(p => p.Key).ToArray()) invitations.Remove(key);
                // 每个接收者最多保留该团容量个请求；重发同一对只续用原请求，不增加队列。
                var pair = (source.Character, target.Character);
                if (invitations.TryGetValue(pair, out var existing)) return existing;
                if (invitations.Count(p => Same(p.Value.Target, target)) >= party.Capacity) return null;
                var invitation = new RaidInvitation(source, target, party.Key, peer, Now.AddSeconds(30));
                invitations[pair] = invitation;
                return invitation;
            }
        });
    }

    public RaidJoinResult Respond(RaidActor recipient, ushort sourceCharacter, bool accept, uint peer = 0)
    {
        if (peer == 0 && (accept ? ConsumeCross(recipient, sourceCharacter) : DeclineCross(recipient, sourceCharacter)) is { } cross)
            return cross;
        RaidInvitation? captured;
        lock (gate) invitations.TryGetValue((sourceCharacter, recipient.Character), out captured);
        if (captured is null || !Same(captured.Target, recipient)) return new(false);
        return Pair<RaidJoinResult>(captured.Source, recipient, () =>
        {
            lock (gate)
            {
                var key = (sourceCharacter, recipient.Character);
                if (!invitations.TryGetValue(key, out var current) || !ReferenceEquals(current, captured) ||
                    current.Expires <= Now || current.Peer != peer || !current.Source.CanManage || !recipient.CanManage) return new(false);
                invitations.Remove(key);
                if (!accept) return new(true, current);
                if (!parties.TryGetValue(current.Raid, out var old) || old.Started is not null) return new(false);
                var leader = old.Members.FirstOrDefault(m => m.Actor.Character == old.Leader);
                if (leader is null || !(Same(leader.Actor, current.Source) || Same(leader.Actor, recipient))) return new(false);
                var entrant = Same(leader.Actor, recipient) ? current.Source : recipient;
                if (FindCore(entrant) is not null || old.Members.Length >= old.Capacity) return new(false);
                byte slot = (byte)Enumerable.Range(0, old.Capacity).First(n => old.Members.All(m => m.Slot != n));
                var updated = old with { Members = old.Members.Add(new(entrant, slot)), Revision = ++revision };
                parties[old.Key] = updated;
                if (waiting.TryGetValue(entrant.Character, out var wait) && Same(wait.Actor, entrant)) waiting.Remove(entrant.Character);
                return new(true, current, new(old, updated));
            }
        }) ?? new(false);
    }

    static T? Pair<T>(RaidActor a, RaidActor b, Func<T?> operation) where T : class
    {
        try { return AccountOwners.Lease.ExecuteTogether(a.Owner, b.Owner, operation); }
        catch (InvalidOperationException) when (!a.Owner.IsCurrent || !b.Owner.IsCurrent) { return null; }
    }

    public void Publish(RaidChange change)
    {
        if (change.StateMember is null)
            PublishDirectory(new(Party: change.After ?? change.Before, Removed: change.After is null));
        if (change.After is { } joined)
            foreach (var member in joined.Members.Where(m => !change.Before.Members.Any(old => Same(old.Actor, m.Actor))))
                PublishDirectory(new(Waiting: new(member.Actor, joined.Type), Removed: true));
        foreach (var member in change.Before.Members)
            member.Actor.Session.RaidNoticeArrived?.Invoke(change, member.Actor);
        if (change.After is { } after)
            foreach (var member in after.Members.Where(m => !change.Before.Members.Any(old => Same(old.Actor, m.Actor))))
                member.Actor.Session.RaidNoticeArrived?.Invoke(change, member.Actor);
    }

    public void PublishDirectory(RaidDirectoryChange change)
    {
        var channel = change.Party?.Channel ?? change.Waiting!.Actor.Channel;
        RaidActor[] recipients;
        lock (gate) recipients = observers.Values.Where(a => a.IsCurrent && a.Channel == channel).ToArray();
        foreach (var actor in recipients)
        {
            // 团员由成员投影发送完整状态；招募列表的订阅者只更新记录，不打开别人的窗口。
            if (change.Party is { } party && party.Members.Any(m => Same(m.Actor, actor))) continue;
            actor.Session.RaidDirectoryNoticeArrived?.Invoke(change, actor);
        }
    }

    public void PublishWaiting(RaidActor actor)
    {
        RaidWaiting? row;
        lock (gate) waiting.TryGetValue(actor.Character, out row);
        if (row is not null && Same(row.Actor, actor)) PublishDirectory(new(Waiting: row));
    }

    uint Allocate(ChannelIdentity channel)
    {
        ushort low = next.GetValueOrDefault(channel);
        uint high = ((uint)channel.ServerIndex << 24) | ((uint)channel.Number << 16);
        for (int attempt = 0; attempt < ushort.MaxValue; attempt++)
        {
            low = (ushort)(low == ushort.MaxValue ? 1 : low + 1);
            if (!parties.ContainsKey(high | low)) { next[channel] = low; return high | low; }
        }
        throw new InvalidOperationException("频道攻坚队编号已用完");
    }
}
