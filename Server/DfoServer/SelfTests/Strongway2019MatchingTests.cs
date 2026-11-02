using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Events;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using PvfLib;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019MatchingTests
{
    public static void RunTerminalRouteResources(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf);
        var kills = DungeonKillExperienceCatalog.Load(pvf);
        using var f = new Fixture(towns, activity, growth);
        var first = f.Team(); var second = f.Team();
        var sessions = first.Concat(second).ToArray();
        var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock);
        var ticket = matching.Queue(first[0], first.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray())!;
        matching.Queue(second[0], second.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray());
        var pair = matching.ReserveNext(ticket)!;
        var dungeons = DungeonCatalog.Load(pvf); var rooms = DungeonRoomCatalog.Load(pvf);
        var entered = new Strongway2019MatchingEntryService(matching, f.Parties, f.Repository, f.Repository, f.Clock)
            .Enter(pair, dungeons, rooms, DungeonMonsterDropCatalog.Load(pvf), items.Items, items.EpicPieces);
        That(entered is not null, "终点换图回归先沿原八人匹配事务正式入场");
        foreach (var team in new[] { first, second })
            foreach (var session in team) team[0].PartyDungeon!.CompleteLoading(session);
        var competition = entered!.Match.Competition;
        var firstRun = first[0].CaptureStrongway2019Run(first[0].PartyDungeon!)!;
        var instance = second[0].PartyDungeon!;
        var runs = second.Select(session => session.CaptureStrongway2019Run(instance)!).ToArray();
        foreach (int phase in new[] { 3, 4 })
            That(competition.ConfirmOpening(firstRun, firstRun.Scene.Identity, phase) == DungeonRunOutcome.Accepted &&
                competition.ConfirmOpening(runs[0], runs[0].Scene.Identity, phase) == DungeonRunOutcome.Accepted,
                "终点换图回归完成原比赛双方开场屏障：" + phase);
        var current = second[0].CurrentFormalDungeonScene()!;
        var target = current.Entry.BossMap.Position;
        var from = new Dictionary<DungeonPoint, DungeonPoint?> { [current.Entry.StartMap.Position] = null };
        var queue = new Queue<DungeonPoint>(); queue.Enqueue(current.Entry.StartMap.Position);
        while (queue.TryDequeue(out var point) && !from.ContainsKey(target))
        foreach (var link in current.Entry.Maze.StaticTopology.Links!)
        {
            DungeonPoint? next = link.First == point ? link.Second : link.Second == point ? link.First : null;
            if (next is not { } adjacent || from.ContainsKey(adjacent)) continue;
            from.Add(adjacent, point); queue.Enqueue(adjacent);
        }
        That(from.ContainsKey(target), "终点换图回归使用真实静态连接可达原Boss");
        var path = new Stack<DungeonPoint>();
        for (var point = target; from[point] is { } parent; point = parent) path.Push(point);
        That(path.Count > 1, "真实匹配路线具有原终点前房间");
        var route = new Strongway2019RouteService(activity, dungeons, rooms);
        while (true)
        {
            foreach (var monster in current.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.SpawnType == 3).ToArray())
            {
                if (!current.Snapshot.LivingMonsters.Contains(monster.ObjectKey)) continue;
                var observation = FormalDungeonSessionTests.Observation(current, monster);
                if (monster.SpawnType == 3) observation = observation with { RewriteFlag = 1, Unknown18 = 9000, Unknown26 = 30 };
                var deaths = instance.ObserveDeath(second[0], monster.ObjectKey,
                    session => session.ObserveFormalDungeonDeath(observation, kills, growth));
                That(deaths.Count(notice => notice.Kind == PartyDungeonNoticeKind.Death &&
                    notice.Death?.Outcome == DungeonRunOutcome.Accepted) == 4,
                    "终点前推进经四人原死亡事务确认真实清房：" + monster.MonsterId);
                current = second[0].CurrentFormalDungeonScene()!;
            }
            if (path.Count == 1) break;
            var next = path.Pop();
            That(route.Move(second[0], checked((byte)next.X), checked((byte)next.Y), 0).Length == 4,
                "终点换图回归沿正式RouteService推进原四人房间");
            foreach (var session in second) instance.CompleteLoading(session);
            current = second[0].CurrentFormalDungeonScene()!;
        }
        That(path.Peek() == target && runs.All(run => run.ClearFact is null && run.Phase == SinglePlayerDungeonPhase.Dungeon),
            "目标队已真实清完倒数第二房但尚未进入原终点");
        f.Clock.Advance(TimeSpan.FromSeconds(100));
        That(competition.UseSkill(firstRun, firstRun.Scene.Identity, 1, 4, 0).Outcome == DungeonRunOutcome.Accepted &&
            competition.Snapshot.PendingRouteChanges.Length == 1, "对手实际充能100并成功施放终点前待换图技能");
        var pending = competition.Snapshot.PendingRouteChanges.Single();
        var originalBosses = runs.Select(run => run.Entry.BossMap).ToArray();
        var originalIdentities = runs.Select(run => run.Scene.Identity).ToArray();
        var mailCounts = sessions.Select(session => f.Repository.LoadMailbox(session.CaptureSocialActor()!.Owner,
            session.Selected!.Id, f.Clock.GetUtcNow().ToUnixTimeSeconds()).Entries.Length).ToArray();
        var clearedRoom = current.Snapshot.CurrentMap;
        var visitedPoint = from[clearedRoom.Position]
            ?? throw new InvalidOperationException("真实终点前房间缺少已清前房连接");
        var originalEntries = runs.Select(run => run.Entry).ToArray();
        foreach (var revisit in new[] { visitedPoint, clearedRoom.Position })
        {
            var revisited = route.Move(second[0], checked((byte)revisit.X), checked((byte)revisit.Y), 0);
            That(revisited.Length == 4 && revisited.All(notice => notice.Scene is { } scene &&
                scene.Snapshot.CurrentMap.Position == revisit && scene.Snapshot.MapPayload == DungeonMapPayload.Cached) &&
                second.All(session => session.Phase == SessionPhase.Loading),
                "待换图不得封死已清房间回访及返回：四个原Run使用既有缓存进入加载");
            foreach (var session in second) instance.CompleteLoading(session);
            That(second.Select((session, index) => ReferenceEquals(session.CaptureStrongway2019Run(instance), runs[index]) &&
                ReferenceEquals(runs[index].Entry, originalEntries[index]) && runs[index].Scene.Map.Position == revisit &&
                runs[index].Scene.Identity.InstanceId == originalIdentities[index].InstanceId &&
                runs[index].Scene.Identity.TaskAttempt == originalIdentities[index].TaskAttempt &&
                runs[index].CurrentMonsterPlan.Monsters.Where(monster => monster.IsBlocking).All(monster =>
                    !runs[index].Snapshot.LivingMonsters.Contains(monster.ObjectKey)) &&
                runs[index].ClearFact is null && session.Phase == SessionPhase.Dungeon &&
                session.ObserveStrongway2019RouteLoaded() == DungeonRunOutcome.Rejected).All(value => value) &&
                competition.Snapshot.Terminal is null &&
                ReferenceEquals(competition.Snapshot.PendingRouteChanges.Single(), pending),
                "待换图回访保持原路线、Run与已清怪物，不替换演员、不确认应用或产生终局");
        }
        var moved = route.Move(second[0], checked((byte)target.X), checked((byte)target.Y), 0);
        That(moved.Length == 4 && moved.All(notice => notice.Scene is { } scene &&
            scene.Snapshot.CurrentMap.Position == target && scene.Entry.IsTerminalMap(scene.Snapshot.CurrentMap)) &&
            second.All(session => session.Phase == SessionPhase.Loading),
            "待换图不得封死合法原终点Move：四个原Run应进入原Boss加载");
        foreach (var session in second) instance.CompleteLoading(session);
        That(second.Select((session, index) => ReferenceEquals(session.CaptureStrongway2019Run(instance), runs[index]) &&
            runs[index].Entry.BossMap == originalBosses[index] && runs[index].Scene.Map == originalBosses[index] &&
            runs[index].Scene.Identity.InstanceId == originalIdentities[index].InstanceId &&
            runs[index].Scene.Identity.TaskAttempt == originalIdentities[index].TaskAttempt &&
            runs[index].Entry.Maze.MapSpecification!.Single(map => map.Position == target) == originalBosses[index] &&
            runs[index].ClearFact is null && session.Phase == SessionPhase.Dungeon).All(value => value),
            "原终点不可替换且保持原四Run、实例与尝试，加载本身不签发清图事实");
        That(second.All(session => session.ObserveStrongway2019RouteLoaded() == DungeonRunOutcome.Rejected) &&
            competition.Snapshot.Terminal is null && ReferenceEquals(competition.Snapshot.PendingRouteChanges.Single(), pending) &&
            sessions.Select((session, index) => session.PrepareStrongway2019Reward() is null &&
                f.Repository.LoadMailbox(session.CaptureSocialActor()!.Owner, session.Selected!.Id,
                    f.Clock.GetUtcNow().ToUnixTimeSeconds()).Entries.Length == mailCounts[index]).All(value => value),
            "跳过原终点替换仍保留原pending供终局清理，不误确认应用、不产生胜利或额外邮件");
        Console.WriteLine($"PASS {Count - before} 首期待换图保持原Boss合法推进与终点事实边界");
    }

    public static void RunRouteResources(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(pvf);
        using var f = new Fixture(towns, activity);
        var first = f.Team(); var second = f.Team();
        var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock);
        var ticket = matching.Queue(first[0], first.Select(session => session.PrepareStrongway2019Admission(5360, matched: true)!).ToArray())!;
        matching.Queue(second[0], second.Select(session => session.PrepareStrongway2019Admission(5360, matched: true)!).ToArray());
        var pair = matching.ReserveNext(ticket)!;
        var dungeons = DungeonCatalog.Load(pvf); var rooms = DungeonRoomCatalog.Load(pvf);
        var entered = new Strongway2019MatchingEntryService(matching, f.Parties, f.Repository, f.Repository, f.Clock)
            .Enter(pair, dungeons, rooms, DungeonMonsterDropCatalog.Load(pvf), items.Items, items.EpicPieces)!;
        That(entered is not null, "换图回归先沿八人正式入场链装配原Run");
        foreach (var team in new[] { first, second })
        {
            var instance = team[0].PartyDungeon!;
            That(team.Take(3).All(session => instance.CompleteLoading(session).Length == 0) &&
                team.All(session => session.Phase == SessionPhase.Loading), "原四人加载未齐时不释放任何Session");
            That(instance.CompleteLoading(team[3]).Length == 4 && team.All(session => session.Phase == SessionPhase.Dungeon),
                "原四人加载齐备一起进入原Run起始房间");
        }
        var competition = entered!.Match.Competition;
        var firstRun = first[0].CaptureStrongway2019Run(first[0].PartyDungeon!)!;
        var secondRun = second[0].CaptureStrongway2019Run(second[0].PartyDungeon!)!;
        That(competition.ConfirmOpening(firstRun, firstRun.Scene.Identity, 3) == DungeonRunOutcome.Accepted &&
            competition.ConfirmOpening(secondRun, secondRun.Scene.Identity, 3) == DungeonRunOutcome.Accepted &&
            competition.ConfirmOpening(firstRun, firstRun.Scene.Identity, 4) == DungeonRunOutcome.Accepted &&
            competition.ConfirmOpening(secondRun, secondRun.Scene.Identity, 4) == DungeonRunOutcome.Accepted,
            "换图回归确认两支原队开场事实并启动比赛");
        f.Clock.Advance(TimeSpan.FromSeconds(100));
        var skill = competition.UseSkill(firstRun, firstRun.Scene.Identity, 1, 4, uint.MaxValue);
        That(skill.Outcome == DungeonRunOutcome.Accepted && competition.Snapshot.PendingRouteChanges.Length == 1,
            "实际比赛技能四生成服务端候选下一房间，忽略客户端第三字段");
        var change = competition.Snapshot.PendingRouteChanges.Single();
        var previous = second.Select(session => session.CurrentFormalDungeonScene()!).ToArray();
        var route = new Strongway2019RouteService(activity, dungeons, rooms);
        var moved = route.Move(second[0], 1, 0, 0);
        That(moved.Length == 4 && moved.All(notice => notice.Scene is { } scene && scene.Snapshot.CurrentMap.Id == change.MapId &&
            scene.Entry.Maze.MapSpecification!.Single(map => map.Position == new DungeonPoint(1, 0)).Id == change.MapId &&
            scene.Monsters.Monsters.Any(monster => monster.MonsterId == activity.MapMonsters[change.MapId])) &&
            second.All(session => session.Phase == SessionPhase.Loading) && first.All(session => session.Phase == SessionPhase.Dungeon),
            "正式四人换图同步原Run与Formal.Entry及真实怪物，随后沿原Move进入加载");
        That(second.Select((session, index) => !session.CanPublishFormalDungeon(previous[index])).All(value => value) &&
            competition.Snapshot.PendingRouteChanges.Length == 1 &&
            second.All(session => session.ObserveStrongway2019RouteLoaded() == DungeonRunOutcome.Rejected),
            "换图前旧回执已失效，未完成真实加载不能提前确认技能替换");
        That(second.Take(3).All(session => second[0].PartyDungeon!.CompleteLoading(session).Length == 0) &&
            second.All(session => session.Phase == SessionPhase.Loading), "替换房间继续使用原四人加载屏障");
        That(second[0].PartyDungeon!.CompleteLoading(second[3]).Length == 4 &&
            second.All(session => session.ObserveStrongway2019RouteLoaded() == DungeonRunOutcome.Accepted) &&
            competition.Snapshot.PendingRouteChanges.Length == 0,
            "四个原Run实际进入并加载候选地图后逐员确认，最后一员才消费pending替换");
        That(competition.Participants.All(participant => first.Concat(second).Any(session =>
            ReferenceEquals(session.CaptureStrongway2019Run(session.PartyDungeon!), participant.Run))) &&
            route.Move(second[0], 1, 0, 0).Length == 0,
            "换图保持原八Run，重复同房Move不再替换或再次推进");
        f.Clock.Advance(TimeSpan.FromSeconds(100));
        That(competition.UseSkill(firstRun, firstRun.Scene.Identity, 1, 4, 0).Outcome == DungeonRunOutcome.Accepted,
            "下一次实际充能和冷却结束后可再次为敌方后续房间生成替换");
        var nextChange = competition.Snapshot.PendingRouteChanges.Single();
        var beforeRetry = second.Select(session => session.CurrentFormalDungeonScene()!.Snapshot.Identity).ToArray();
        That(route.Move(second[0], 2, 0, 0).Length == 0 && second.Select((session, index) =>
            session.CurrentFormalDungeonScene() is { } scene && scene.Snapshot.Identity.Epoch == beforeRetry[index].Epoch + 1 &&
            scene.Snapshot.CurrentMap.Position == new DungeonPoint(1, 0) &&
            scene.Entry.Maze.MapSpecification!.Single(map => map.Position == new DungeonPoint(2, 0)).Id == nextChange.MapId).All(value => value),
            "当前真实怪物未清时只替换尚未加载下一房间，Move拒绝但不伪造进图");
        var afterReplacement = second.Select(session => session.CurrentFormalDungeonScene()!.Snapshot.Identity).ToArray();
        That(route.Move(second[0], 2, 0, 0).Length == 0 && second.Select((session, index) =>
            session.CurrentFormalDungeonScene()!.Snapshot.Identity == afterReplacement[index]).All(value => value) &&
            competition.Snapshot.PendingRouteChanges.Length == 1,
            "移动暂拒后重复请求复用四个原替换准备，不分配新演员、不重复推进epoch或提前消费pending");
        Console.WriteLine($"PASS {Count - before} 首期四人原Run换图、Formal回执与加载屏障");
    }

    public static void RunResources(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(pvf);
        using var f = new Fixture(towns, activity);
        var first = f.Team(); var second = f.Team();
        var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock);
        var firstPlans = first.Select(session => session.PrepareStrongway2019Admission(5360, matched: true)!).ToArray();
        var secondPlans = second.Select(session => session.PrepareStrongway2019Admission(5360, matched: true)!).ToArray();
        That(firstPlans.Concat(secondPlans).All(row => row is not null), "真实资源准入策略为八个原Session生成四人匹配资格");
        var ticket = matching.Queue(first[0], firstPlans);
        var opponent = matching.Queue(second[0], secondPlans);
        That(ticket is not null && opponent is not null && matching.ReserveNext(ticket) is { },
            "双队正式入场沿原排队预约链冻结两支四人Party");
        var pair = ticket!.Pair!;
        That(first.All(session => session.CanPrepareStrongway2019Matching(firstPlans.Single(plan => ReferenceEquals(plan.Character, session.Selected)))) &&
            !first[0].CanPrepareStrongway2019Matching(firstPlans[0] with { }),
            "匹配城镇许可只接受原预约及票据中的准入引用，复制准入不能绕过");
        var entry = new Strongway2019MatchingEntryService(matching, f.Parties, f.Repository, f.Repository, f.Clock);
        var dungeons = DungeonCatalog.Load(pvf); var rooms = DungeonRoomCatalog.Load(pvf);
        var drops = DungeonMonsterDropCatalog.Load(pvf);
        That(dungeons.Get(5360).TryGetDesignatedDifficulty(out byte difficulty) && difficulty == 2,
            "匹配5360使用DGN指定物理难度2，活动模式键0不作为难度");
        first[0].CommandGate.Wait();
        try
        {
            That(entry.Enter(pair, dungeons, rooms, drops, items.Items, items.EpicPieces) is null &&
                ReferenceEquals(ticket.Pair, pair) && first.Concat(second).All(session => session.PartyDungeon is null),
                "未证明的忙连接门拒绝入场且不拆掉另一条可能正在使用的原预约");
        }
        finally { first[0].CommandGate.Release(); }
        var original = first.Concat(second).Select(session => session.Selected!).ToArray();
        string baseline = f.Snapshot();
        f.Sql($"CREATE TRIGGER fail_sw_match_install BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id={second[^1].Selected!.Id} BEGIN SELECT RAISE(ABORT,'sw-match-eighth'); END");
        Reject<SqliteException>(() => entry.Enter(pair, dungeons, rooms, drops, items.Items, items.EpicPieces),
            "第八名晚期入场SQL故障传播，不吞成成功比赛");
        That(f.Snapshot() == baseline && first.Concat(second).Select((session, index) =>
            ReferenceEquals(session.Selected, original[index]) && session.Phase == SessionPhase.Town && session.PartyDungeon is null &&
            session.CurrentStrongway2019Match is null && session.CurrentFormalDungeonScene() is null).All(value => value) &&
            matching.Queued(ticket.Channel).Select(row => row.Token).SequenceEqual([ticket.Token, opponent!.Token]),
            "八人事务失败后数据库与全部Session保持原状，原两队票据按顺序回队");
        f.Sql("DROP TRIGGER fail_sw_match_install");
        pair = matching.ReserveNext(ticket)!;
        var ownParty = ticket.Party;
        ownParty.Commands.Wait(); first[0].CommandGate.Wait();
        Strongway2019MatchingEntry? entered;
        try
        {
            entered = entry.Enter(pair, dungeons, rooms, drops, items.Items, items.EpicPieces,
                holdsCommandGate: command => ReferenceEquals(command, ownParty.Commands) || ReferenceEquals(command, first[0].CommandGate));
            That(ownParty.Commands.CurrentCount == 0 && first[0].CommandGate.CurrentCount == 0,
                "正式匹配入场复用Dispatcher已持原门，不额外释放调用方命令门");
        }
        finally { first[0].CommandGate.Release(); ownParty.Commands.Release(); }
        That(entered is not null && entered.Notices.Length == 8 &&
            entered.Match.First.Id != entered.Match.Second.Id &&
            first.All(session => ReferenceEquals(session.PartyDungeon, entered.Match.First)) &&
            second.All(session => ReferenceEquals(session.PartyDungeon, entered.Match.Second)) &&
            first.Concat(second).All(session => session.Phase == SessionPhase.Loading &&
                ReferenceEquals(session.CurrentStrongway2019Match, entered.Match) && session.CurrentStrongway2019Ticket is null),
            "八人同writer正式装配两个原四人Instance及八个原Run，再绑定唯一比赛");
        That(entered!.Match.Competition.Participants.All(participant =>
            first.Concat(second).Any(session => ReferenceEquals(session.CaptureStrongway2019Run(session.PartyDungeon!), participant.Run))) &&
            entered.Match.Competition.Participants.Select(participant => participant.Identity.CharacterId).Distinct().Count() == 8,
            "比赛冻结实际Session正式Run，不能另造八人Party或影子Run");
        That(entry.Enter(pair, dungeons, rooms, drops, items.Items, items.EpicPieces) is null &&
            first.Concat(second).All(session => session.Selected!.DungeonState!.LastAttempt == 1),
            "已消耗配对重放不二次入场、不重复预留尝试");
        first[1].ReturnToRoster();
        That(entered.Match.Competition.Snapshot.Terminal is { Reason: Strongway2019FinishReason.Exit, WinnerSide: 1, RewardParticipants.Length: 4 } &&
            first[1].CurrentStrongway2019Match is null && ReferenceEquals(matching.MatchFor(first[1]), entered.Match) &&
            second.All(session => ReferenceEquals(session.CurrentStrongway2019Match, entered.Match)),
            "回选角在清原Run前冻结退出判胜，保留对方四人终局和奖励引用");
        matching.Detach(first[1], first[1].SelectionEpoch);
        That(matching.MatchFor(first[1]) is null && second.All(session => ReferenceEquals(session.CurrentStrongway2019Match, entered.Match)) &&
            entered.Match.Competition.Snapshot.Terminal!.RewardParticipants.All(participant => participant.Side == 1),
            "选角epoch变更后的旧连接索引收尾不清胜队引用或改变终局");
        Console.WriteLine($"PASS {Count - before} 首期双队正式入场Session/原Run集成");
    }

    public static void RunCore()
    {
        int before = Count;
        using var f = new Fixture();
        var first = f.Team(); var second = f.Team(); var otherChannel = f.Team(50);
        var service = new Strongway2019MatchingService(f.Parties, Strongway2019CatalogTests.CreateCatalog(), f.Clock, capacity: 2);
        var firstPlans = f.Admissions(first); var secondPlans = f.Admissions(second);
        That(service.Queue(first[1], firstPlans) is null && first.All(s => s.CurrentStrongway2019Ticket is null),
            "普通队员不能替队长提交整队匹配");
        That(service.Queue(first[0], firstPlans, blocked: true) is null &&
            service.Queue(first[0], firstPlans[..3]) is null, "外部活动门禁或不足四份真实准入时零安装");
        var invalid = (Strongway2019Admission[])firstPlans.Clone(); invalid[3] = firstPlans[0];
        That(service.Queue(first[0], invalid) is null, "重复角色准入整队拒绝，不抛Single匹配异常或安装半队");
        invalid = (Strongway2019Admission[])firstPlans.Clone();
        invalid[0] = invalid[0] with { Character = invalid[0].Character with { } };
        That(service.Queue(first[0], invalid) is null, "相同角色ID的复制状态不能代替原Session当前角色");
        invalid = firstPlans.Select(row => row with { Schedule = row.Schedule with { OverrideActive = false } }).ToArray();
        That(service.Queue(first[0], invalid) is null, "活动关闭时四人准入全部拒绝");
        invalid = (Strongway2019Admission[])firstPlans.Clone();
        invalid[3] = invalid[3] with { Schedule = invalid[3].Schedule with { Revision = 2 } };
        That(service.Queue(first[0], invalid) is null, "同队四份活动版本必须一致");
        var ticket = service.Queue(first[0], firstPlans);
        That(ticket is not null && ticket.IsCurrent && first.All(s => ReferenceEquals(s.CurrentStrongway2019Ticket, ticket)) &&
            ReferenceEquals(ticket.Party, f.Parties.Current(first[0])) && ticket.Party.Members.Length == 4,
            "队长一次提交四人原Party票据，四个Session共享唯一票据引用");
        That(ReferenceEquals(service.Queue(first[0], firstPlans), ticket), "整队重复排队幂等返回原token");
        That(first.All(s => s.Strongway2019BlocksTownActions && !s.CanChangeTownParty),
            "排队占用所有队员的活动与组队门禁，不能换队或借其他成员绕过");
        That(!service.Cancel(second[0], ticket!), "局外Session不能取消另一队的精确票据");
        var opponent = service.Queue(second[0], secondPlans);
        That(opponent is not null && service.Queue(otherChannel[0], f.Admissions(otherChannel)) is null,
            "队列按队计容量，超额拒绝时不留下任何半队票据");
        var pair = service.ReserveNext(ticket!);
        That(pair is not null && ReferenceEquals(pair.First, ticket) && ReferenceEquals(pair.Second, opponent) &&
            pair.First.Party.Id != pair.Second.Party.Id && service.Queued(ticket!.Channel).Length == 0,
            "FIFO预约两支原四人队，保留两个Party身份，预约队不重复参与匹配");
        That(service.ReserveNext(ticket!) is null && !service.Release(new(Guid.NewGuid(), ticket!, opponent!)),
            "同一原预约不能重复配对，伪造预约不能释放真实两队");
        That(service.Bind(pair!, f.Instance(first), f.Instance(second)) is null &&
            first.Concat(second).All(s => s.CurrentStrongway2019Match is null) &&
            first.All(s => ReferenceEquals(s.CurrentStrongway2019Ticket, ticket)) &&
            second.All(s => ReferenceEquals(s.CurrentStrongway2019Ticket, opponent)),
            "空壳PartyDungeonInstance不能替代八个正式Run，绑定失败保留两支原票据");
        That(service.Release(pair!) && service.Queued(ticket!.Channel).Select(x => x.Token).SequenceEqual([ticket.Token, opponent!.Token]),
            "资源准备失败释放预约，两支原票据按原顺序回队");
        pair = service.ReserveNext(ticket!);
        That(service.Cancel(first[2], ticket!) && first.All(s => s.CurrentStrongway2019Ticket is null) &&
            second.All(s => ReferenceEquals(s.CurrentStrongway2019Ticket, opponent)) && service.Queued(ticket!.Channel).Length == 1 &&
            !service.Release(pair!), "一名队员取消撤销本队及原预约，对方整队回队且不被误取消");
        var renewed = service.Queue(first[0], f.Admissions(first));
        That(renewed is not null && renewed.Token != ticket!.Token && !service.Cancel(first[0], ticket),
            "重新排队使用新token，旧取消不能清新票据");
        service.Cancel(first[0], renewed!); service.Cancel(second[0], opponent!);
        var isolated = service.Queue(otherChannel[0], f.Admissions(otherChannel));
        renewed = service.Queue(first[0], f.Admissions(first));
        That(service.ReserveNext(renewed!) is null && service.Queued(isolated!.Channel).Length == 1,
            "真实频道不同的两队不能配对");
        service.Cancel(otherChannel[0], isolated!); service.Cancel(first[0], renewed!);

        firstPlans = f.Admissions(first);
        first[0].CommandGate.Wait();
        try { That(service.Queue(first[0], firstPlans) is null, "任一连接命令门忙时零等待拒绝整队，不持锁等待队友"); }
        finally { first[0].CommandGate.Release(); }
        var party = f.Parties.Current(first[0])!; party.Commands.Wait();
        try { That(service.Queue(first[0], firstPlans) is null, "队伍原命令门忙时拒绝，不建立第二套队伍门"); }
        finally { party.Commands.Release(); }
        party.Commands.Wait(); first[0].CommandGate.Wait();
        try
        {
            That(service.Queue(first[0], firstPlans, holdsCommandGate: gate => ReferenceEquals(gate, party.Commands)) is null,
                "证明只覆盖原队伍门时，未证明的忙连接门仍拒绝");
            renewed = service.Queue(first[0], firstPlans, holdsCommandGate: gate =>
                ReferenceEquals(gate, party.Commands) || ReferenceEquals(gate, first[0].CommandGate));
            That(renewed is not null && party.Commands.CurrentCount == 0 && first[0].CommandGate.CurrentCount == 0 &&
                first.All(s => ReferenceEquals(s.CurrentStrongway2019Ticket, renewed)),
                "Dispatcher已持原队伍与本人命令门的精确证明可排队，服务不释放调用方的门");
        }
        finally { first[0].CommandGate.Release(); party.Commands.Release(); }
        service.Cancel(first[0], renewed!);

        var until = f.Clock.GetUtcNow().AddSeconds(1).ToUnixTimeSeconds();
        var expiring = firstPlans.Select(row => row with { Schedule = row.Schedule with
            { StartsAt = until - 60, EndsAt = until, OverrideActive = null } }).ToArray();
        renewed = service.Queue(first[0], expiring);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        That(renewed is not null && service.Queued(renewed.Channel).Length == 0 && first.All(s => s.CurrentStrongway2019Ticket is null),
            "等候期间到活动结束点整队失效，城镇门禁同步释放");
        until = f.Clock.GetUtcNow().AddSeconds(1).ToUnixTimeSeconds();
        var reservedPlans = f.Admissions(first).Select(row => row with { Schedule = row.Schedule with
            { StartsAt = until - 60, EndsAt = until, OverrideActive = null } }).ToArray();
        renewed = service.Queue(first[0], reservedPlans);
        opponent = service.Queue(second[0], f.Admissions(second).Select(row => row with { Schedule = reservedPlans[0].Schedule }).ToArray());
        pair = service.ReserveNext(renewed!); f.Clock.Advance(TimeSpan.FromSeconds(1));
        That(pair is not null && service.Queued(renewed!.Channel).Length == 0 &&
            first.All(s => ReferenceEquals(s.CurrentStrongway2019Ticket, renewed)) &&
            second.All(s => ReferenceEquals(s.CurrentStrongway2019Ticket, opponent)),
            "预约准备期间跨活动结束点不拆已固定Pair，交由正式八人事务检查活动");
        That(service.Release(pair!) && first.Concat(second).All(s => s.CurrentStrongway2019Ticket is null),
            "活动过期且准备失败释放预约后两队失效，不重新进入等待队列");
        renewed = service.Queue(first[0], firstPlans);
        service.Detach(first[1], first[1].SelectionEpoch);
        That(renewed is not null && first.All(s => ReferenceEquals(s.CurrentStrongway2019Ticket, renewed)),
            "保留当前选角epoch的旧生命周期通知不能清掉当前整队票据");
        first[1].ReturnToRoster(); service.Detach(first[1]);
        That(first.All(s => s.CurrentStrongway2019Ticket is null) && service.Queued(renewed!.Channel).Length == 0,
            "返回选角/退出清理整个原队票据，释放其他成员等待状态");
        Console.WriteLine($"PASS {Count - before} 首期4v4原队伍排队、预约、取消与命令门");
    }

    public static void RunQueueRevisionResources(PvfArchive pvf, TownCatalog towns)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(pvf);
        foreach (bool observeWhileClosed in new[] { true, false })
        {
            using var f = new Fixture(towns, activity);
            var first = f.Team();
            var second = f.Team();
            var service = new Strongway2019MatchingService(f.Parties, activity, f.Clock, serverEvents: f.Events);
            Strongway2019Admission[] Current(GameSession[] team) => team.Select(session =>
                session.PrepareStrongway2019Admission(5360, matched: true) ??
                    throw new InvalidOperationException("真实当前312准入应可准备")).ToArray();
            var originalPlans = Current(first);
            var original = service.Queue(first[0], originalPlans);
            That(original is not null && first.All(session => session.Strongway2019BlocksTownActions),
                "真实312开启时排队，原四人票据冻结旧活动资格并占用城镇动作");

            f.SetStrongway2019Active(false);
            if (observeWhileClosed)
            {
                That(service.Snapshot().Tickets.Length == 0 && first.All(session =>
                        session.CurrentStrongway2019Ticket is null && !session.Strongway2019BlocksTownActions),
                    "真实312关闭后维护清掉未预约旧队资格，四名成员城镇动作全部释放");
            }
            f.SetStrongway2019Active(true);
            var renewedPlans = Current(first);
            That(renewedPlans.All(plan => plan.Schedule.Revision > originalPlans[0].Schedule.Revision),
                "通过真实ServerEventService关闭重开产生新revision，不伪造Schedule");
            That(service.Snapshot().Tickets.Length == 0 && first.All(session =>
                    session.CurrentStrongway2019Ticket is null && !session.Strongway2019BlocksTownActions),
                "关闭重开即使发生在两次维护之间也失效旧资格，不能将旧票据自动升级或永久挂起");

            var renewed = service.Queue(first[0], renewedPlans);
            var opponent = service.Queue(second[0], Current(second));
            That(renewed is not null && opponent is not null && !ReferenceEquals(renewed, original) &&
                    renewed.Admissions.All(plan => plan.Schedule == renewedPlans[0].Schedule),
                "旧资格已释放后两队重新取得本次有效活动资格和新票据");
            var pair = service.ReserveNext(renewed!);
            That(pair is not null && ReferenceEquals(pair.First, renewed) && ReferenceEquals(pair.Second, opponent),
                "新revision两支真实四人队可以正常预约，不再被永久旧票据或不同Schedule阻塞");
        }
        Console.WriteLine($"PASS {Count - before} 首期真实活动revision切换清理旧排队资格并允许重新匹配");
    }

    internal sealed class Fixture : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), "strongway-matching-" + Guid.NewGuid().ToString("N"));
        readonly AccountOwners owners = new();
        readonly List<AccountOwners.Lease> leases = [];
        readonly List<GameSession> sessions = [];
        readonly CharacterRepository repository;
        readonly TownCatalog towns;
        readonly Location point = new(65, 1, 300, 300);
        public Clock Clock { get; } = new();
        public PartyService Parties { get; }
        public CharacterRepository Repository => repository;
        internal string DatabasePath => Path.Combine(path, "game.db");
        readonly Strongway2019Catalog? activity;
        readonly CharacterGrowthCatalog? growth;
        readonly CharacterLearningState? initialLearning;
        readonly ServerEventService events;
        internal ServerEventService Events => events;
        internal void SetStrongway2019Active(bool active) => events.Override(ServerEventService.Strongway2019Id, active);
        public Fixture(TownCatalog? loadedTowns = null, Strongway2019Catalog? activity = null, CharacterGrowthCatalog? growth = null, CharacterLearningState? initialLearning = null)
        {
            Directory.CreateDirectory(path); repository = new(Path.Combine(path, "game.db"));
            this.activity = activity;
            this.growth = growth;
            this.initialLearning = initialLearning;
            events = new(new ServerEventRepository(Path.Combine(path, "game.db")), Clock);
            events.Override(312, true);
            var map = new TownMap("strongway-fixture.map", [new(new(0, 0, 600, 600), [], [])], [], false, []);
            towns = loadedTowns ?? new(new() { [(65, 1)] = new(65, 1, 1, [], [], [], point, map, [], []) }, point, point,
                new(0, [], []), [], new Dictionary<uint, uint[]>());
            Parties = new(repository, Clock);
        }
        public GameSession[] Team(byte channel = 49, int reviveCoins = 0)
        {
            var team = Enumerable.Range(0, 4).Select(_ =>
            {
                int id = sessions.Count; var lease = owners.Acquire("strongway-match-" + id, () => { }); leases.Add(lease);
                var created = repository.Create(lease, new("SWMatch" + id, point, point) { Level = 95,
                    InitialLearning = initialLearning ?? (growth is null ? CharacterLearningState.Uninitialized :
                        new(growth.GetLevelStartExperience(95), [new(0, 0, 0, []), new(1, 0, 0, [])])),
                    DungeonState = new(256, 0, 0, 0, 0) });
                if (reviveCoins > 0) Sql($"UPDATE characters SET revive_coins={reviveCoins} WHERE id={created.Id}");
                var session = new GameSession(repository, towns, lease, entry: new(new(2, channel), 55, "[dpl_pve]"),
                    timeProvider: Clock, strongway2019Catalog: activity, strongway2019AntiEvil: _ => 5400, serverEvents: events);
                sessions.Add(session); session.Select(0, resumeTown: activity is not null); session.MarkReady(); return session;
            }).ToArray();
            foreach (var member in team.Skip(1))
                That(Parties.Invite(team[0], member, 1).Accepted && Parties.Respond(member, team[0].Selected!.Id, 2, true).Accepted,
                    "真实四个Session通过PartyService邀请加入原队伍");
            foreach (var session in team) session.CurrentParty = () => Parties.Current(session);
            return team;
        }
        internal AccountOwners.Lease Takeover(GameSession session) => Takeover(session.CaptureSocialActor()!.Owner);
        internal AccountOwners.Lease Takeover(AccountOwners.Lease original)
        {
            var lease = owners.Acquire(original.Account, () => { });
            leases.Add(lease); return lease;
        }
        public Strongway2019Admission[] Admissions(GameSession[] team) => team.Select(session =>
            new Strongway2019Admission(session.Selected!, Strongway2019Mode.Matching, 5360, 4, 5400,
                new(ServerEventService.Strongway2019Id, 1, OverrideActive: true))).ToArray();
        public PartyDungeonInstance Instance(GameSession[] team) => new(Guid.NewGuid(), Parties.Current(team[0])!, repository);
        public string Snapshot() => JsonSerializer.Serialize(sessions.Select((session, index) => repository.LoadCharacter(leases[index], session.Selected!.Slot)));
        public void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(path, "game.db") + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            foreach (var session in sessions) { session.ResetStrongway2019Matching(); Parties.Detach(session); session.End(); }
            foreach (var lease in leases) lease.Dispose();
            TestFiles.DeleteDirectory(path, true);
        }
    }
    internal sealed class Clock : TimeProvider
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-01T12:00:00+08:00");
        long milliseconds;
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => milliseconds;
        public void Advance(TimeSpan elapsed) { now += elapsed; milliseconds += checked((long)elapsed.TotalMilliseconds); }
    }
}
