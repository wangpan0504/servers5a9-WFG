using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Events;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Strongway2019;

internal sealed class Strongway2019MatchingTicket(Guid token, PartySnapshot party,
    ImmutableArray<Strongway2019Admission> admissions, ChannelIdentity channel, long order)
{
    public Guid Token { get; } = token;
    public PartySnapshot Party { get; } = party;
    public ImmutableArray<Strongway2019Admission> Admissions { get; } = admissions;
    public ChannelIdentity Channel { get; } = channel;
    public long Order { get; } = order;
    Strongway2019MatchingPair? pair;
    internal Strongway2019MatchingPair? Pair { get => Volatile.Read(ref pair); set => Volatile.Write(ref pair, value); }
    public bool IsCurrent => Strongway2019EntryPolicy.AllowsMemberCount(Party.Members.Length) && Party.Members.All(member => member.IsCurrent() &&
        (Pair is not null || member.Actor.Session.Phase == SessionPhase.Town) &&
        ReferenceEquals(member.Actor.Session.CurrentStrongway2019Ticket, this));
}

internal sealed class Strongway2019MatchingPair(Guid id, Strongway2019MatchingTicket first, Strongway2019MatchingTicket second)
{
    public Guid Id { get; } = id;
    public Strongway2019MatchingTicket First { get; } = first;
    public Strongway2019MatchingTicket Second { get; } = second;
    internal bool Active { get; set; } = true;
}

internal sealed record Strongway2019Match(Strongway2019MatchingPair Pair, PartyDungeonInstance First,
    PartyDungeonInstance Second, Strongway2019Competition Competition)
{
    internal bool CompletionCheckpointCommitted { get; set; }
    internal bool ReadyCompletionRecoveryNotified { get; set; }
}

// 队列协调两支原一至四人队，不创建跨队普通Party，不复制Session角色状态。
internal sealed class Strongway2019MatchingService(PartyService parties, Strongway2019Catalog catalog,
    TimeProvider? clock = null, int capacity = 256, ServerEventService? serverEvents = null, bool completionRecovery = false)
{
    readonly ServerEventService? events = serverEvents;
    readonly object gate = new();
    readonly Dictionary<Guid, Strongway2019MatchingTicket> waiting = [];
    readonly Dictionary<GameSession, Strongway2019Match> matches = [];
    readonly TimeProvider time = clock ?? TimeProvider.System;
    long order;

    // 维护任务读取原索引快照，不建立另一份匹配或Session名册。
    public (Strongway2019MatchingTicket[] Tickets, Strongway2019Match[] Matches) Snapshot()
    {
        var schedule = CurrentSchedule();
        lock (gate)
        {
            Prune(schedule);
            foreach (var row in matches.Where(row => row.Value.CompletionCheckpointCommitted && row.Value.ReadyCompletionRecoveryNotified &&
                !ReferenceEquals(row.Key.CurrentStrongway2019Match, row.Value)).ToArray()) matches.Remove(row.Key);
            return (waiting.Values.OrderBy(ticket => ticket.Order).ToArray(), matches.Values.Distinct().ToArray());
        }
    }

    internal Strongway2019Match? MatchFor(GameSession session)
    {
        lock (gate) return matches.GetValueOrDefault(session);
    }

    public Strongway2019MatchingTicket? Queue(GameSession leader, IReadOnlyList<Strongway2019Admission> admissions,
        bool blocked = false, Func<SemaphoreSlim, bool>? holdsCommandGate = null)
    {
        var schedule = CurrentSchedule();
        var party = parties.Current(leader);
        if (blocked || party is null || !Strongway2019EntryPolicy.AllowsMemberCount(party.Members.Length) || admissions.Count != party.Members.Length ||
            admissions.Select(row => row.Character.Id).Distinct().Count() != party.Members.Length ||
            !party.Members.Any(member => member.Slot == party.Leader && ReferenceEquals(member.Actor.Session, leader))) return null;
        return WithCommands<Strongway2019MatchingTicket?>([party], () =>
        {
            if (!parties.IsCurrent(party) || party.Members.Any(member => !member.Actor.Session.CanQueueStrongway2019)) return null;
            var channel = leader.ChannelEntry?.Identity;
            var plans = party.Members.Select(member => admissions.SingleOrDefault(row =>
                ReferenceEquals(row.Character, member.Actor.Session.Selected))).ToArray();
            if (channel is null || plans.Any(row => row is null) ||
                plans.Select(row => row!.Character.Id).Distinct().Count() != party.Members.Length ||
                plans.Any(row => row!.Mode != Strongway2019Mode.Matching || row.DungeonId != 5360 || row.MemberCount != party.Members.Length ||
                    !row.Schedule.IsActive(time.GetUtcNow()) || !CurrentSchedule(row.Schedule, schedule) ||
                    row.Schedule != plans[0]!.Schedule) ||
                party.Members.Any(member => member.Actor.Session.ChannelEntry?.Identity != channel ||
                    member.Actor.Session.CurrentStrongway2019Match is not null)) return null;
            // 入场资格由原策略复核；队列不接受客户端或第二份角色状态替代服务端穿戴结果。
            if (party.Members.Select((member, index) => Strongway2019EntryPolicy.Prepare(catalog, plans[index]!.Character,
                member.Actor.Session.ChannelEntry, 5360, party.Members.Length, plans[index]!.AntiEvil, plans[index]!.Schedule,
                time.GetUtcNow(), matched: true)).Any(row => row is null)) return null;
            lock (gate)
            {
                Prune(schedule);
                var existing = waiting.Values.SingleOrDefault(row => ReferenceEquals(row.Party, party));
                if (existing is not null) return existing.IsCurrent && existing.Admissions.SequenceEqual(plans.Select(row => row!)) ? existing : null;
                if (waiting.Count >= capacity || party.Members.Any(member => member.Actor.Session.CurrentStrongway2019Ticket is not null)) return null;
                var ticket = new Strongway2019MatchingTicket(Guid.NewGuid(), party, [..plans.Select(row => row!)], channel.Value, ++order);
                foreach (var member in party.Members)
                    if (!member.Actor.Session.InstallStrongway2019Ticket(ticket))
                        throw new InvalidOperationException("全员预验后的强者之路队列安装发生变化");
                waiting.Add(ticket.Token, ticket);
                return ticket;
            }
        }, null, holdsCommandGate);
    }

    public bool Cancel(GameSession member, Strongway2019MatchingTicket expected, Func<SemaphoreSlim, bool>? holdsCommandGate = null)
    {
        if (!expected.Party.Members.Any(row => ReferenceEquals(row.Actor.Session, member) && row.Actor.IsCurrent())) return false;
        return WithCommands([expected.Party], () =>
        {
            lock (gate)
            {
                if (!waiting.TryGetValue(expected.Token, out var stored) || !ReferenceEquals(stored, expected) ||
                    !ReferenceEquals(member.CurrentStrongway2019Ticket, expected)) return false;
                Remove(expected);
                return true;
            }
        }, false, holdsCommandGate);
    }

    public Strongway2019MatchingTicket[] Queued(ChannelIdentity channel)
    {
        var schedule = CurrentSchedule();
        lock (gate)
        {
            Prune(schedule);
            return waiting.Values.Where(row => row.Channel == channel && row.Pair is null).OrderBy(row => row.Order).ToArray();
        }
    }

    public Strongway2019MatchingPair? ReserveNext(Strongway2019MatchingTicket ticket,
        Func<SemaphoreSlim, bool>? holdsCommandGate = null)
    {
        var schedule = CurrentSchedule();
        foreach (var other in Queued(ticket.Channel).Where(row => !ReferenceEquals(row, ticket)))
        {
            var result = WithCommands<Strongway2019MatchingPair?>([ticket.Party, other.Party], () =>
            {
                lock (gate)
                {
                    Prune(schedule);
                    if (!Waiting(ticket, schedule) || !Waiting(other, schedule) || ticket.Channel != other.Channel ||
                        ticket.Admissions[0].Schedule != other.Admissions[0].Schedule) return null;
                    var first = ticket.Order < other.Order ? ticket : other;
                    var second = ReferenceEquals(first, ticket) ? other : ticket;
                    var pair = new Strongway2019MatchingPair(Guid.NewGuid(), first, second);
                    first.Pair = second.Pair = pair;
                    return pair;
                }
            }, null, holdsCommandGate);
            if (result is not null) return result;
        }
        return null;
    }

    public bool Release(Strongway2019MatchingPair pair)
    {
        var schedule = CurrentSchedule();
        lock (gate)
        {
            if (!PairCurrent(pair)) return false;
            Unpair(pair);
            Prune(schedule);
            return true;
        }
    }

    // 调用方安装两支原队正式副本后仍持全员原owner及门禁；证明谓词只跳过当前确实已持的门。
    // 不在双队owner内调用PartyDungeonInstance.Execute，后者只接受原单队组合。
    public Strongway2019Match? Bind(Strongway2019MatchingPair pair, PartyDungeonInstance first,
        PartyDungeonInstance second, Func<SemaphoreSlim, bool>? holdsCommandGate = null) =>
        WithCommands<Strongway2019Match?>([pair.First.Party, pair.Second.Party], () =>
        {
            if (ReferenceEquals(first, second) || first.Id == second.Id ||
                !MatchesInstance(pair.First, first) || !MatchesInstance(pair.Second, second)) return null;
            var firstRuns = pair.First.Party.Members.Select(member => member.Actor.Session.CaptureStrongway2019Run(first)).ToArray();
            var secondRuns = pair.Second.Party.Members.Select(member => member.Actor.Session.CaptureStrongway2019Run(second)).ToArray();
            if (firstRuns.Any(run => run is null) || secondRuns.Any(run => run is null)) return null;
            lock (gate)
            {
                if (!PairCurrent(pair)) return null;
                var competition = new Strongway2019Competition(pair.Id, firstRuns.Select(run => run!).ToArray(),
                    secondRuns.Select(run => run!).ToArray(), catalog, TimeSpan.FromSeconds(600), time);
                var match = new Strongway2019Match(pair, first, second, competition);
                foreach (var member in pair.First.Party.Members.Concat(pair.Second.Party.Members))
                {
                    member.Actor.Session.InstallStrongway2019Match(match);
                    matches[member.Actor.Session] = match;
                }
                waiting.Remove(pair.First.Token); waiting.Remove(pair.Second.Token);
                Unpair(pair);
                return match;
            }
        }, null, holdsCommandGate);

    public void Detach(GameSession session, long? retainedEpoch = null)
    {
        lock (gate)
        {
            foreach (var ticket in waiting.Values.Where(row => row.Party.Members.Any(member =>
                ReferenceEquals(member.Actor.Session, session) && member.Actor.Epoch != retainedEpoch)).ToArray())
                Remove(ticket);
            if (!matches.TryGetValue(session, out var match)) return;
            var original = match.Competition.Participants.SingleOrDefault(row =>
                (row.Side == 0 ? match.Pair.First : match.Pair.Second).Party.Members.Any(member =>
                    ReferenceEquals(member.Actor.Session, session) && member.Actor.Epoch != retainedEpoch &&
                    member.Actor.Character.Id == row.Identity.CharacterId));
            if (original is null) return;
            match.Competition.Exit(original.Run, original.Run.Scene.Identity);
            // 未落盘终局保留在原匹配索引，当前在线owner登记后由同一秒维护重试。
            if (!completionRecovery || match.CompletionCheckpointCommitted && match.ReadyCompletionRecoveryNotified ||
                match.Competition.Snapshot.Terminal?.WinnerSide is null)
                matches.Remove(session);
            session.RemoveStrongway2019Match(match);
        }
    }

    // 全部原命令门保护资源准备；此时尚未固定全员owner，不将PVF解析放进组合锁。
    internal T WithReservedCommands<T>(Strongway2019MatchingPair pair, Func<T> action, T rejected,
        Func<SemaphoreSlim, bool>? holds) => WithCommands([pair.First.Party, pair.Second.Party], () =>
    {
        lock (gate)
            if (!PairCurrent(pair) || !Valid(pair.First) || !Valid(pair.Second)) return rejected;
        return action();
    }, rejected, holds, fixOwners: false);

    // 调用方已固定原八个owner；同步提交、安装及绑定期间不能释放或清理该原Pair。
    internal T CommitReserved<T>(Strongway2019MatchingPair pair, Func<T> action, T rejected)
    {
        lock (gate) return PairCurrent(pair) && Valid(pair.First) && Valid(pair.Second) ? action() : rejected;
    }

    bool MatchesInstance(Strongway2019MatchingTicket ticket, PartyDungeonInstance instance) => parties.IsCurrent(ticket.Party) &&
        instance.Members.Length == ticket.Party.Members.Length && instance.Members.All(member => ticket.Party.Members.Any(expected =>
            ReferenceEquals(expected.Actor.Session, member.Actor.Session) && ReferenceEquals(expected.Actor.Owner, member.Actor.Owner) &&
            expected.Actor.Epoch == member.Actor.Epoch)) && ticket.Party.Members.All(member => member.Actor.IsCurrent() &&
            ReferenceEquals(member.Actor.Session.CurrentStrongway2019Ticket, ticket));
    ServerEventSchedule? CurrentSchedule() => events?.Read(ServerEventService.Strongway2019Id).Schedule;
    // 门外读取可能落后于刚入队的新资格；旧观察不能撤销更高版本的票据。
    static bool CurrentSchedule(ServerEventSchedule admission, ServerEventSchedule? observed) =>
        observed is null || admission.Revision > observed.Revision || admission == observed;
    bool Waiting(Strongway2019MatchingTicket ticket, ServerEventSchedule? schedule) => waiting.TryGetValue(ticket.Token, out var stored) &&
        ReferenceEquals(stored, ticket) && ticket.Pair is null && Valid(ticket, schedule);
    // 预约后的最终活动门归双队同writer事务；不能在提交与安装之间因时钟跨界清掉原Pair。
    bool Valid(Strongway2019MatchingTicket ticket, ServerEventSchedule? schedule = null) => ticket.IsCurrent && parties.IsCurrent(ticket.Party) &&
        (ticket.Pair is not null || ticket.Admissions.All(row => CurrentSchedule(row.Schedule, schedule) && row.Schedule.IsActive(time.GetUtcNow()) &&
            ticket.Party.Members.Any(member => ReferenceEquals(member.Actor.Session.Selected, row.Character))));
    bool PairCurrent(Strongway2019MatchingPair pair) => pair.Active && ReferenceEquals(pair.First.Pair, pair) &&
        ReferenceEquals(pair.Second.Pair, pair) && waiting.TryGetValue(pair.First.Token, out var first) && ReferenceEquals(first, pair.First) &&
        waiting.TryGetValue(pair.Second.Token, out var second) && ReferenceEquals(second, pair.Second);
    static void Unpair(Strongway2019MatchingPair pair)
    {
        if (ReferenceEquals(pair.First.Pair, pair)) pair.First.Pair = null;
        if (ReferenceEquals(pair.Second.Pair, pair)) pair.Second.Pair = null;
        pair.Active = false;
    }
    void Remove(Strongway2019MatchingTicket ticket)
    {
        if (ticket.Pair is { } pair) Unpair(pair);
        waiting.Remove(ticket.Token);
        foreach (var member in ticket.Party.Members) member.Actor.Session.RemoveStrongway2019Ticket(ticket);
    }
    void Prune(ServerEventSchedule? schedule)
    {
        foreach (var ticket in waiting.Values.Where(row => !Valid(row, schedule)).ToArray()) Remove(ticket);
    }

    // 先原队伍门、再账号排序的连接门，最后固定实际全员owner；忙时零等待，不扩锁阻塞队友。
    T WithCommands<T>(PartySnapshot[] snapshots, Func<T> action, T rejected, Func<SemaphoreSlim, bool>? holds,
        bool fixOwners = true)
    {
        var members = snapshots.SelectMany(party => party.Members).OrderBy(member => member.Actor.Owner.Account, StringComparer.Ordinal).ToArray();
        if (snapshots.Length is < 1 or > 2 || snapshots.Any(party => !Strongway2019EntryPolicy.AllowsMemberCount(party.Members.Length)) ||
            members.Select(member => member.Actor.Character.Id).Distinct().Count() != members.Length ||
            members.Select(member => member.Actor.Owner.Account).Distinct(StringComparer.Ordinal).Count() != members.Length) return rejected;
        var gates = snapshots.OrderBy(party => party.Id).Select(party => party.Commands)
            .Concat(members.Select(member => member.Actor.Session.CommandGate)).Distinct().ToArray();
        var acquired = new List<SemaphoreSlim>();
        try
        {
            foreach (var command in gates)
            {
                if (holds?.Invoke(command) == true) continue;
                if (!command.Wait(0)) return rejected;
                acquired.Add(command);
            }
            return fixOwners ? AccountOwners.Lease.ExecuteRaidGroup(
                members.Select(member => (member.Actor.Owner, member.Actor.Character.Id)).ToArray(), members.Length, action) : action();
        }
        catch (InvalidOperationException) when (members.Any(member => !member.Actor.IsCurrent())) { return rejected; }
        finally { for (int i = acquired.Count - 1; i >= 0; i--) acquired[i].Release(); }
    }
}
