using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Strongway2019;

internal enum Strongway2019CompetitionPhase { Opening, Running, Finished }
internal enum Strongway2019FinishReason { Clear, Exit, Timeout }

// Identity只冻结角色、实例、尝试；每个命令的当前Epoch仍须与原Run核对。
internal sealed record Strongway2019Participant(int Side, SinglePlayerDungeonRun Run, DungeonRunIdentity Identity);

internal sealed record Strongway2019SkillEffect(int SourceSide, int TargetSide, int Kind, uint Value, string Symbol);
internal sealed record Strongway2019SkillResult(DungeonRunOutcome Outcome, Strongway2019SkillEffect? Effect = null);

internal sealed record Strongway2019TerminalParticipant(Strongway2019Participant Participant,
    DungeonRunIdentity Identity, DungeonClearFact? Fact);

// 此记录表示正式房间推进尚待应用的替换，不能把发出HUD通知视作应用完成。
internal sealed record Strongway2019RouteChange(Guid Id, int TargetSide, int MapId,
    ImmutableArray<Strongway2019Participant> Participants);

internal sealed record Strongway2019Terminal(Strongway2019FinishReason Reason, int? WinnerSide,
    DungeonClearFact? ClearFact, ImmutableArray<Strongway2019Participant> RewardParticipants)
{
    public long CombatElapsedMilliseconds { get; init; }
    public long Created { get; init; }
    public ImmutableArray<Strongway2019TerminalParticipant> FrozenRewardParticipants { get; init; } = [];
}

internal sealed record Strongway2019CompetitionSnapshot(Strongway2019CompetitionPhase Phase,
    ImmutableArray<int> Phase3Sides, ImmutableArray<int> Phase4Sides, ImmutableArray<int> Energy,
    long RemainingMilliseconds, ImmutableArray<Strongway2019RouteChange> PendingRouteChanges,
    Strongway2019Terminal? Terminal);

// Session负责账号资格、owner及generation；这里不保存Character或Session的平行副本。
internal sealed class Strongway2019Competition
{
    // 只保护本比赛的裁决状态，不在此锁内取得Session命令门、账号锁或发送网络消息。
    readonly object gate = new();
    readonly Strongway2019Catalog catalog;
    readonly TimeProvider time;
    readonly Func<int, int> chooseMapIndex;
    readonly long budgetMilliseconds;
    readonly ImmutableDictionary<int, Strongway2019SkillRow> skills;
    readonly HashSet<int> phase3 = [], phase4 = [];
    readonly int[] energy = [0, 0];
    readonly long[] nextSkillAt = [0, 0];
    readonly int[,] skillUses = new int[2, 5];
    readonly Dictionary<Guid, RouteProgress> pendingRoutes = [];
    readonly HashSet<(Guid Change, ushort Character)> appliedRoutes = [];
    Strongway2019CompetitionPhase phase;
    Strongway2019Terminal? terminal;
    long startTimestamp, elapsedMilliseconds, chargedSeconds;

    sealed record RouteProgress(Strongway2019RouteChange Change, Dictionary<ushort, long> RequestedEpochs);

    public Guid Id { get; }
    public ImmutableArray<Strongway2019Participant> Participants { get; }
    public Strongway2019CompetitionSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                AdvanceClock();
                return new(phase, phase3.Order().ToImmutableArray(), phase4.Order().ToImmutableArray(),
                    energy.ToImmutableArray(), Math.Max(0, budgetMilliseconds - elapsedMilliseconds),
                    pendingRoutes.Values.Select(row => row.Change).ToImmutableArray(), terminal);
            }
        }
    }

    // combatBudget由官方活动策略显式注入600秒，不从包含开场过程的副本限时倒推。
    // chooseMapIndex仅由服务端提供，参数为RandomMapIds长度，返回候选索引。
    public Strongway2019Competition(Guid id, IReadOnlyList<SinglePlayerDungeonRun> first,
        IReadOnlyList<SinglePlayerDungeonRun> second, Strongway2019Catalog catalog,
        TimeSpan combatBudget, TimeProvider? time = null, Func<int, int>? chooseMapIndex = null)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(catalog);
        if (id == Guid.Empty || !Strongway2019EntryPolicy.AllowsMemberCount(first.Count) ||
            !Strongway2019EntryPolicy.AllowsMemberCount(second.Count) || combatBudget <= TimeSpan.Zero)
            throw new ArgumentException("强者之路比赛必须有有效身份、两队各一至四人和正数战斗预算");
        Id = id;
        this.catalog = catalog;
        this.time = time ?? TimeProvider.System;
        this.chooseMapIndex = chooseMapIndex ?? Random.Shared.Next;
        budgetMilliseconds = combatBudget.Ticks / TimeSpan.TicksPerMillisecond;
        if (budgetMilliseconds <= 0 || first.Concat(second).Any(run => run is null))
            throw new ArgumentException("强者之路成员及毫秒战斗预算无效");
        Participants = first.Select(run => new Strongway2019Participant(0, run, run.Scene.Identity))
            .Concat(second.Select(run => new Strongway2019Participant(1, run, run.Scene.Identity))).ToImmutableArray();
        if (Participants.Select(row => row.Identity.CharacterId).Distinct().Count() != Participants.Length ||
            Participants.Any(row => row.Identity.CharacterId == 0 || row.Identity.InstanceId == Guid.Empty ||
                row.Identity.TaskAttempt <= 0 || !catalog.MatchingDungeons.Any(dungeon => dungeon.DungeonId == row.Run.Entry.DungeonId) ||
                row.Run.Phase is not (SinglePlayerDungeonPhase.Loading or SinglePlayerDungeonPhase.Dungeon)))
            throw new ArgumentException("强者之路必须冻结两支原队全部不同角色的有效匹配副本原实例");
        if (!catalog.Skills.Select(row => row.ButtonIndex).Order().SequenceEqual([0, 1, 2, 3, 4]) ||
            catalog.Skills.Any(row => row.NeedPoint < 0 || row.GlobalCooltime < 0 || row.LimitCount is <= 0))
            throw new ArgumentException("强者之路五技能目录无效", nameof(catalog));
        skills = catalog.Skills.ToImmutableDictionary(row => row.ButtonIndex);
        phase = Strongway2019CompetitionPhase.Opening;
    }

    // 同队成员共同贡献一次来源；只有两个不同队均确认同一阶段才能推进。
    public DungeonRunOutcome ConfirmOpening(SinglePlayerDungeonRun run, DungeonRunIdentity expected, int phase)
    {
        lock (gate)
        {
            AdvanceClock();
            var member = Member(run, expected);
            if (member is null || terminal is not null || phase is not (3 or 4) ||
                run.Phase != SinglePlayerDungeonPhase.Dungeon || phase == 4 && phase3.Count != 2)
                return DungeonRunOutcome.Rejected;
            var sources = phase == 3 ? phase3 : phase4;
            if (!sources.Add(member.Side)) return DungeonRunOutcome.Duplicate;
            if (phase == 4 && phase4.Count == 2)
            {
                // 只有双队开场事实齐备才开放战斗门控；原生RIGHT门及Run移动由Session随后落实。
                this.phase = Strongway2019CompetitionPhase.Running;
                startTimestamp = time.GetTimestamp();
            }
            return DungeonRunOutcome.Accepted;
        }
    }

    // 忽略C2S第三个u32：原生发送端没有初始化该栈值。Effect.Value只能由服务端生成。
    public Strongway2019SkillResult UseSkill(SinglePlayerDungeonRun run, DungeonRunIdentity expected,
        int targetSide, int kind, uint ignoredClientValue)
    {
        // 随机规划不持有比赛锁，提交时仍复验成员、预算、目标、能量及共享冷却。
        int map = kind == 4 ? catalog.RandomMapIds[chooseMapIndex(catalog.RandomMapIds.Length)] : 0;
        lock (gate)
        {
            AdvanceClock();
            var member = Member(run, expected);
            if (member is null || phase != Strongway2019CompetitionPhase.Running ||
                run.Phase != SinglePlayerDungeonPhase.Dungeon || targetSide != 1 - member.Side ||
                !skills.TryGetValue(kind, out var skill) || energy[member.Side] < skill.NeedPoint ||
                elapsedMilliseconds < nextSkillAt[member.Side] ||
                skill.LimitCount is { } limit && skillUses[member.Side, kind] >= limit)
                return new(DungeonRunOutcome.Rejected);
            energy[member.Side] -= skill.NeedPoint;
            nextSkillAt[member.Side] = elapsedMilliseconds + skill.GlobalCooltime;
            skillUses[member.Side, kind]++;
            if (kind == 4)
            {
                var targets = Participants.Where(row => row.Side == targetSide).ToImmutableArray();
                var change = new Strongway2019RouteChange(Guid.NewGuid(), targetSide, map, targets);
                // 原生只替换下一路线位置；尚未应用时后一次换图覆盖同一队的待替换位置。
                foreach (var prior in pendingRoutes.Values.Where(row => row.Change.TargetSide == targetSide).ToArray())
                    pendingRoutes.Remove(prior.Change.Id);
                pendingRoutes.Add(change.Id, new(change,
                    targets.ToDictionary(row => row.Identity.CharacterId, row => row.Run.Scene.Identity.Epoch)));
            }
            return new(DungeonRunOutcome.Accepted, new(member.Side, targetSide, kind, checked((uint)map), skill.Symbol));
        }
    }

    // 只在Session将该替换实际应用于原Run的后续正式房间推进后调用；单纯发包不得调用。
    public DungeonRunOutcome ConfirmRouteApplied(SinglePlayerDungeonRun run, DungeonRunIdentity expected, Guid changeId)
    {
        lock (gate)
        {
            AdvanceClock();
            var member = Member(run, expected);
            if (member is null || phase != Strongway2019CompetitionPhase.Running)
                return DungeonRunOutcome.Rejected;
            var receipt = (changeId, member.Identity.CharacterId);
            if (appliedRoutes.Contains(receipt)) return DungeonRunOutcome.Duplicate;
            if (!pendingRoutes.TryGetValue(changeId, out var pending) || pending.Change.TargetSide != member.Side ||
                !pending.RequestedEpochs.TryGetValue(member.Identity.CharacterId, out long requestedEpoch) ||
                expected.Epoch <= requestedEpoch || run.Scene.Map.Id != pending.Change.MapId ||
                run.Phase != SinglePlayerDungeonPhase.Dungeon)
                return DungeonRunOutcome.Rejected;
            appliedRoutes.Add(receipt);
            if (pending.Change.Participants.All(row => appliedRoutes.Contains((changeId, row.Identity.CharacterId))))
                pendingRoutes.Remove(changeId);
            return DungeonRunOutcome.Accepted;
        }
    }

    // 必须同时核对原Run引用、冻结实例/尝试、当前身份以及Run.ClearFact的原引用。
    public DungeonRunOutcome ObserveClear(SinglePlayerDungeonRun run, DungeonRunIdentity expected, DungeonClearFact fact)
    {
        lock (gate)
        {
            AdvanceClock();
            var member = Member(run, expected);
            if (member is null || !ReferenceEquals(run.ClearFact, fact) || fact.Identity != expected ||
                fact.DungeonId != run.Entry.DungeonId || run.Phase != SinglePlayerDungeonPhase.Settlement)
                return DungeonRunOutcome.Rejected;
            if (terminal is not null)
                return ReferenceEquals(terminal.ClearFact, fact) ? DungeonRunOutcome.Duplicate : DungeonRunOutcome.Rejected;
            if (phase != Strongway2019CompetitionPhase.Running) return DungeonRunOutcome.Rejected;
            Finish(new(Strongway2019FinishReason.Clear, member.Side, fact,
                Participants.Where(row => row.Side == member.Side).ToImmutableArray()));
            return DungeonRunOutcome.Accepted;
        }
    }

    public DungeonRunOutcome Exit(SinglePlayerDungeonRun run, DungeonRunIdentity expected)
    {
        lock (gate)
        {
            AdvanceClock();
            var member = Member(run, expected);
            if (member is null || terminal is not null) return DungeonRunOutcome.Rejected;
            Finish(new(Strongway2019FinishReason.Exit, 1 - member.Side, null,
                Participants.Where(row => row.Side != member.Side).ToImmutableArray()));
            return DungeonRunOutcome.Accepted;
        }
    }

    // 推进充能和截止检查；每条写命令也须先检查截止，不依赖后台Tick是否及时调度。
    public void Tick() { lock (gate) AdvanceClock(); }

    Strongway2019Participant? Member(SinglePlayerDungeonRun run, DungeonRunIdentity expected)
    {
        var member = Participants.SingleOrDefault(row => ReferenceEquals(row.Run, run));
        return member is not null && run.Scene.Identity == expected &&
            member.Identity.CharacterId == expected.CharacterId && member.Identity.InstanceId == expected.InstanceId &&
            member.Identity.TaskAttempt == expected.TaskAttempt && member.Identity.QuestId == expected.QuestId &&
            run.Phase is not (SinglePlayerDungeonPhase.Returned or SinglePlayerDungeonPhase.TimedOut) ? member : null;
    }

    void AdvanceClock()
    {
        if (phase != Strongway2019CompetitionPhase.Running) return;
        elapsedMilliseconds = Math.Max(elapsedMilliseconds,
            time.GetElapsedTime(startTimestamp, time.GetTimestamp()).Ticks / TimeSpan.TicksPerMillisecond);
        if (elapsedMilliseconds >= budgetMilliseconds)
        {
            elapsedMilliseconds = budgetMilliseconds;
            Finish(new(Strongway2019FinishReason.Timeout, null, null, []));
            return;
        }
        long seconds = elapsedMilliseconds / 1000;
        long added = seconds - chargedSeconds;
        for (int side = 0; side < 2; side++) energy[side] = (int)Math.Min(100L, energy[side] + added);
        chargedSeconds = seconds;
    }

    void Finish(Strongway2019Terminal result)
    {
        terminal = result with { CombatElapsedMilliseconds = elapsedMilliseconds,
            Created = time.GetUtcNow().ToUnixTimeSeconds(),
            FrozenRewardParticipants = result.RewardParticipants.Select(member =>
                new Strongway2019TerminalParticipant(member, member.Run.Scene.Identity, member.Run.ClearFact)).ToImmutableArray() };
        phase = Strongway2019CompetitionPhase.Finished;
        pendingRoutes.Clear();
    }
}
