using DfoServer.Game.Dungeons;
using DfoServer.Game.Strongway2019;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal Func<Strongway2019Notice, Task>? Strongway2019NoticeArrived { get; set; }
    Strongway2019MatchingTicket? strongway2019Ticket;
    Strongway2019Match? strongway2019Match;
    long strongway2019SelectionEpoch;
    internal Strongway2019MatchingTicket? CurrentStrongway2019Ticket => Volatile.Read(ref strongway2019Ticket);
    internal Strongway2019Match? CurrentStrongway2019Match => Volatile.Read(ref strongway2019Match);
    internal bool Strongway2019BlocksTownActions => strongway2019SelectionEpoch == SelectionEpoch &&
        (CurrentStrongway2019Ticket is not null || CurrentStrongway2019Match is not null);

    internal bool CanQueueStrongway2019 => Ready && Selected is not null && Phase == SessionPhase.Town &&
        formalDungeon is null && PartyDungeon is null && pvpMatching is null && pvpArena is null &&
        pvpPractice is null && pvpTraining is null && pendingRental is null && !HasPendingTownQuestCommit &&
        !HasActiveDungeonContinuation && !HasPendingQuestReplay && !HasPendingFormalCard;

    internal bool CanPrepareStrongway2019Matching(Strongway2019Admission admission) =>
        CanQueueStrongway2019 && owner.IsCurrent && strongway2019SelectionEpoch == SelectionEpoch &&
        admission is { Mode: Strongway2019Mode.Matching, DungeonId: 5360 } &&
        Strongway2019EntryPolicy.AllowsMemberCount(admission.MemberCount) &&
        ReferenceEquals(admission.Character, Selected) && CurrentStrongway2019Ticket is { Pair: { Active: true } pair } ticket &&
        (ReferenceEquals(pair.First, ticket) || ReferenceEquals(pair.Second, ticket)) && ticket.IsCurrent &&
        ReferenceEquals(CurrentParty?.Invoke(), ticket.Party) && admission.MemberCount == ticket.Party.Members.Length &&
        ticket.Admissions.Any(original => ReferenceEquals(original, admission));

    bool CanReserveStrongway2019Matching(PreparedFormalDungeon plan) =>
        plan.Dungeon is { Entry.DungeonId: 5360, Strongway2019Admission: { } admission } &&
        plan.SelectionEpoch == SelectionEpoch && ReferenceEquals(plan.Character, Selected) &&
        CanPrepareStrongway2019Matching(admission) && IsCurrentStrongway2019Admission(admission);

    internal bool InstallStrongway2019Ticket(Strongway2019MatchingTicket ticket)
    {
        if (!CanQueueStrongway2019 || CurrentStrongway2019Ticket is not null ||
            !ticket.Party.Members.Any(member => ReferenceEquals(member.Actor.Session, this) && member.Actor.IsCurrent())) return false;
        strongway2019SelectionEpoch = SelectionEpoch;
        Volatile.Write(ref strongway2019Ticket, ticket);
        return true;
    }
    internal void RemoveStrongway2019Ticket(Strongway2019MatchingTicket expected)
    {
        Interlocked.CompareExchange(ref strongway2019Ticket, null, expected);
    }
    internal SinglePlayerDungeonRun? CaptureStrongway2019Run(PartyDungeonInstance instance) =>
        Ready && ReferenceEquals(PartyDungeon, instance) && formalDungeon is { Entry.DungeonId: 5360 } dungeon &&
        dungeon.Run.Scene.Identity.InstanceId == instance.Id ? dungeon.Run : null;
    internal void InstallStrongway2019Match(Strongway2019Match match)
    {
        Volatile.Write(ref strongway2019Ticket, null);
        Volatile.Write(ref strongway2019Match, match);
    }
    internal void RemoveStrongway2019Match(Strongway2019Match expected) =>
        Interlocked.CompareExchange(ref strongway2019Match, null, expected);
    // 原连接收尾只清自己的引用；服务索引随后按原票据清理，不写接管者或另一选角状态。
    internal void ResetStrongway2019Matching()
    {
        // Invalidate先在原owner内冻结退出事实，不能等清掉正式Run后才由连接收尾推断胜负。
        if (CurrentStrongway2019Match is { } match && strongway2019SelectionEpoch == SelectionEpoch)
        {
            var original = match.Competition.Participants.SingleOrDefault(participant =>
                (participant.Side == 0 ? match.Pair.First : match.Pair.Second).Party.Members.Any(member =>
                    ReferenceEquals(member.Actor.Session, this) && member.Actor.Epoch == strongway2019SelectionEpoch &&
                    member.Actor.Character.Id == participant.Identity.CharacterId));
            if (original is not null) match.Competition.Exit(original.Run, original.Run.Scene.Identity);
        }
        ResetStrongway2019Combat();
        Volatile.Write(ref strongway2019Ticket, null);
        Volatile.Write(ref strongway2019Match, null);
    }
}
