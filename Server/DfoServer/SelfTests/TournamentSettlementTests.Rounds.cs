using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class TournamentSettlementTests
{
    static void VerifyRoundDeaths()
    {
        VerifyTournamentPlanRevalidation();
        var (authority, run) = RoundDeathFixture();
        var identity = run.Snapshot.Identity;
        NativeDungeonDeathResult Report(ushort key) => authority.ValidateAndIssue(run, run.Snapshot.Identity,
            new(key, 99, 0x0111, 99, 0, 0, 0, 70, 0, 0, 0, true, new string('A', 64)));
        bool Rejected(ushort key) => Report(key) is { Outcome: DungeonRunOutcome.Rejected, Death: null };
        That(Rejected(11) && Rejected(13) && run.Snapshot.LivingMonsters.Count == 4 && run.ClearFact is null,
            "大会未来轮次及末轮Boss不能提前签发死亡或清退前轮");
        // 明确合成同权威事件，单独验证领域入口，不能只依赖原生签发门禁。
        var future = ConfirmedDungeonMonsterDeath.Issue(authority, identity, Guid.NewGuid(), 13, 99);
        That(run.PrepareConfirmedDeath(future) is { Outcome: DungeonRunOutcome.Rejected, Pending: null },
            "绕过原生入口的未来轮次事件仍被Run拒绝");

        var first = Report(10);
        That(first is { Outcome: DungeonRunOutcome.Accepted, Death.CombatSequence: 1 },
            "未来轮次拒绝不占用签发序号或缓存");
        var pending = run.PrepareConfirmedDeath(first.Death!).Pending!;
        That(pending.Result.ClearFact is null && pending.Result.CulledMonsterKeys.Count == 0 &&
            Rejected(11) && Rejected(13) && run.Snapshot.LivingMonsters.Count == 4,
            "前轮仅准备未提交时下一轮及Boss保持关闭，前轮没有被提前清退");
        That(run.CancelUncommittedDeath(pending) && Rejected(11) && run.ClearFact is null,
            "明确回滚取消前轮准备不会开放下一轮");
        var replay = Report(10);
        var retried = run.PrepareConfirmedDeath(replay.Death!);
        That(replay.Outcome == DungeonRunOutcome.Duplicate && ReferenceEquals(replay.Death, first.Death) &&
            retried is { Outcome: DungeonRunOutcome.Accepted, Pending: not null } &&
            !ReferenceEquals(retried.Pending, pending) && ReferenceEquals(retried.Pending.Death, first.Death),
            "回滚重试保留原事件身份，只重建待提交候选");
        var committed = run.ConfirmCommittedDeath(retried.Pending!);
        That(committed.Outcome == DungeonRunOutcome.Accepted && committed.ClearFact is null &&
            committed.CulledMonsterKeys.Count == 0 && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 11, 12, 13 }),
            "首轮提交只消费本轮，保留后三轮真实对象");
        That(Report(10) is { Outcome: DungeonRunOutcome.Duplicate } duplicate &&
            ReferenceEquals(duplicate.Death, first.Death) &&
            run.PrepareConfirmedDeath(first.Death!) is { Outcome: DungeonRunOutcome.Duplicate, Pending: null },
            "轮次前进后已提交精确重复仍返回原事实");

        var events = new List<Guid> { first.Death!.EventId };
        for (ushort key = 11; key <= 13; key++)
        {
            var issued = Report(key);
            That(issued.Outcome == DungeonRunOutcome.Accepted && issued.Death!.CombatSequence == key - 9,
                "前轮提交后未来报告才首次签发，不继承提前报告缓存");
            var prepared = run.PrepareConfirmedDeath(issued.Death!).Pending!;
            That(prepared.Result.CulledMonsterKeys.Count == 0 &&
                (key == 13 ? prepared.Result.ClearFact is not null : prepared.Result.ClearFact is null) &&
                (key == 13 || Rejected((ushort)(key + 1))),
                "每轮提交前不开放下一轮，只有末轮规划通关且不清退前轮");
            events.Add(issued.Death!.EventId);
            var result = run.ConfirmCommittedDeath(prepared);
            That(result.Outcome == DungeonRunOutcome.Accepted && (key == 13 || result.ClearFact is null),
                "四轮按顺序确认，前三轮不生成通关事实");
            if (key != 13) continue;
            var clear = run.ClearFact;
            That(clear is not null && clear.ConfirmedDeathEvents.SequenceEqual(events) &&
                run.Phase == SinglePlayerDungeonPhase.Settlement && run.Snapshot.LivingMonsters.Count == 0 &&
                run.ConfirmCommittedDeath(prepared) is { Outcome: DungeonRunOutcome.Duplicate, ClearFact: null } &&
                ReferenceEquals(run.ClearFact, clear) && Report(key) is { Outcome: DungeonRunOutcome.Duplicate } finalReplay &&
                ReferenceEquals(finalReplay.Death, issued.Death),
                "四次真实死亡只生成一次通关，末轮提交与报告重放不重复结算");
        }
    }

    static (NativeDungeonCombatAuthority Authority, SinglePlayerDungeonRun Run) RoundDeathFixture()
    {
        var (entry, plan) = RoundDeathPlan();
        var authority = new NativeDungeonCombatAuthority();
        if (!SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([plan], []), authority, out var run, out var reason) ||
            !authority.BindTo(run!) || run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome != DungeonRunOutcome.Accepted)
            throw new InvalidOperationException($"FAIL 合成大会无法创建或完成加载：{reason}");
        return (authority, run!);
    }

    static (DungeonEntryPlan Entry, DungeonMonsterPlan Plan) RoundDeathPlan(bool apc = false)
    {
        var map = new DungeonMap("boss", new(0, 0), 1, "map/tournament/fixture-tournament-rounds.map");
        var maze = new DungeonMaze(0, 1, 1, "明确合成大会", [map.Position], [map.Position], [map], [])
            { IsTournament = true };
        var entry = new DungeonEntryPlan(99, 1, 120, 0, byte.MaxValue, 2, maze, map, map);
        var monsters = Enumerable.Range(0, 4).Select(index => new DungeonRoomMonster(index + 1,
            [index + 1, 0, 70, 760, 270, 0, 0, 0], "[fixed]", index == 3 ? "[boss]" : "[normal]",
            "monster/fixture-tournament-rounds.mob")).ToArray();
        var room = new DungeonRoomDefinition(map, apc ? [] : monsters, [], [])
        {
            AiCharacters = apc ? monsters.Select(monster => new DungeonRoomAiCharacter(monster.MonsterId,
                [monster.MonsterId, 760, 270, 0, 0, 0], "[monster]", monster.RankKind,
                "aicharacter/fixture-tournament-rounds.aic", 70)).ToArray() : []
        };
        var plan = DungeonMonsterPlan.FromRoom(room, 99, 10);
        var rounds = Enumerable.Range(0, 4).Select(round => (IReadOnlyList<TournamentTeam>)
            Enumerable.Range(0, 16 >> round).Select(team => new TournamentTeam(
                [new TournamentEnemy(1, team == 0 ? 0 : team == 1 ? round + 1 : 100 + team,
                    team == 0 ? 0 : 100)
                { AiCharacter = team == 1 && apc ? room.AiCharacters[round] : null }], team == 0)).ToArray()).ToArray();
        plan = plan with { Tournament = new(1, rounds, plan.Monsters.Select((monster, index) =>
            new TournamentOpponent(index + 1, monster.ObjectKey, monster.MonsterId, 70, monster.SpawnType)).ToArray()) };
        return (entry, DungeonMonsterPlan.RebuildTournamentBlocking(plan, plan));
    }

    static void VerifyTournamentPlanRevalidation()
    {
        var (entry, plan) = RoundDeathPlan(apc: true);
        That(plan.Monsters.All(monster => monster.IsBlocking) &&
            DungeonMonsterPlan.FromRoom(plan.Room, entry.CharacterId, 1).Monsters.All(monster => !monster.IsBlocking) &&
            Accepts(entry, plan), "大会APC上下文可严格复验，普通FromRoom的APC仍不阻门");
        var tournament = plan.Tournament!;
        var wrongRound = tournament.Opponents.ToArray();
        wrongRound[0] = wrongRound[0] with { Round = 2 };
        var wrongCode = tournament.Opponents.ToArray();
        wrongCode[0] = wrongCode[0] with { Code = 500 };
        var duplicated = tournament.Opponents.ToArray();
        duplicated[1] = duplicated[0];
        foreach (var opponents in new[] { wrongRound, wrongCode, duplicated })
            That(!Accepts(entry, plan with { Tournament = tournament with { Opponents = opponents } }),
                "错误轮次、模板或重复大会对手不能借上下文通过复验");
        var mismatched = plan.Monsters.ToArray();
        mismatched[0] = mismatched[0] with { SourceOrdinal = 99 };
        That(!Accepts(entry, plan with { Monsters = mismatched }) &&
            !Accepts(entry with { Maze = entry.Maze with { IsTournament = false } }, plan),
            "错绑来源或普通副本伪造大会上下文不能改变APC阻挡资格");
        var ordinaryMap = entry.StartMap with { Path = "map/ordinary/fixture-tournament-rounds.map" };
        var ordinaryEntry = entry with
        {
            StartMap = ordinaryMap, BossMap = ordinaryMap, BaseBossMap = ordinaryMap,
            Maze = entry.Maze with { MapSpecification = [ordinaryMap] }
        };
        That(!Accepts(ordinaryEntry, plan with { Room = plan.Room with { Map = ordinaryMap } }),
            "普通资源路径伪造大会迷宫标志也不能改变APC阻挡资格");
        var mismatchedRounds = tournament.Rounds.ToArray();
        var mismatchedBracket = mismatchedRounds[0].ToArray();
        var mismatchedMembers = mismatchedBracket[1].Members.ToArray();
        mismatchedMembers[0] = mismatchedMembers[0] with
        {
            AiCharacter = mismatchedMembers[0].AiCharacter! with { Path = "aicharacter/another-tournament-actor.aic" }
        };
        mismatchedBracket[1] = mismatchedBracket[1] with { Members = mismatchedMembers };
        mismatchedRounds[0] = mismatchedBracket;
        That(!Accepts(entry, plan with { Tournament = tournament with { Rounds = mismatchedRounds } }),
            "同模板编号的错误大会APC候选路径不能借上下文改变阻挡资格");

        static bool Accepts(DungeonEntryPlan entry, DungeonMonsterPlan plan) =>
            SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([plan], []), new NativeDungeonCombatAuthority(), out _, out _);
    }
}
