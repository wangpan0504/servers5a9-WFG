using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Session;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.Game.Dungeons;
using PvfLib;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019AdmissionSessionTests
{
    public static void RunTimeout(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        int before = Count;
        string directory = Path.Combine(Path.GetTempPath(), "s5a9-sw-deadline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var repository = new CharacterRepository(Path.Combine(directory, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("sw-deadline-session", () => { });
            var clock = new Strongway2019MatchingTests.Clock();
            var events = new ServerEventService(new ServerEventRepository(Path.Combine(directory, "game.db")), clock);
            events.Override(312, true);
            var role = repository.Create(owner, new("SWDeadline", new(65, 1, 450, 234), towns.InitialReturn)
                { Level = 95, DungeonState = new(256, 0, 0, 0, 0) });
            var session = new GameSession(repository, towns, owner, entry: new(new(2, 49), 55, "[dpl_pve]"),
                timeProvider: clock, strongway2019Catalog: new(pvf), strongway2019AntiEvil: _ => 4000, serverEvents: events);
            session.Select(role.Slot, resumeTown: true); session.MarkReady();
            var entered = session.EnterFormalDungeon(5359, 0, 2, DungeonCatalog.Load(pvf), DungeonRoomCatalog.Load(pvf),
                DungeonMonsterDropCatalog.Load(pvf), items: items.Items, pieces: items.EpicPieces);
            That(entered is not null && session.CompleteFormalDungeonLoading() is not null, "限时回归使用首期真实原Run并完成加载");
            clock.Advance(TimeSpan.FromSeconds(3599));
            That(session.CurrentFormalDungeonScene()!.Snapshot.Phase == SinglePlayerDungeonPhase.Dungeon,
                "首期普通资源3600秒期限前原Run仍可战斗");
            clock.Advance(TimeSpan.FromSeconds(1));
            That(session.CurrentFormalDungeonScene()!.Snapshot.Phase == SinglePlayerDungeonPhase.TimedOut &&
                session.PrepareStrongway2019Reward() is null,
                "首期普通资源限时在服务端原Run生效，到期不能接受击杀或领奖");
            That(session.ReturnFromFormalDungeon() is not null && session.Phase == SessionPhase.Town,
                "首期到期仍沿原回城事务退出并保留角色");
        }
        finally { TestFiles.DeleteDirectory(directory, true); }
        Console.WriteLine($"PASS {Count - before} 首期普通原Run限时与到期返回");
    }

    public static void RunHero(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        int before = Count;
        var growth = CharacterGrowthCatalog.Load(pvf);
        using var f = new Strongway2019MatchingTests.Fixture(towns, new(pvf), growth);
        var team = f.Team();
        var dungeons = DungeonCatalog.Load(pvf);
        var service = new PartyDungeonService(f.Parties, f.Repository);
        var selected = service.BeginSelection(team[0]);
        That(selected.Length == 4 && team.All(session => session.Phase == SessionPhase.MapSelect),
            "首期英雄从65/1原四人队伍进入正式选图");
        var entered = service.Enter(team[0], 5363, 2, dungeons, DungeonRoomCatalog.Load(pvf),
            DungeonMonsterDropCatalog.Load(pvf), items.Items, items.EpicPieces, RejectDropPreparation);
        That(entered.Length == 4 && team.All(session => session.Phase == SessionPhase.Loading) &&
            entered.All(notice => notice.Scene is { Entry.DungeonId: 5363, Entry.DisableMonsterAndClearExperience: true }),
            "首期英雄原四人队经正式资源准入安装四个5363原Run");
        foreach (var notice in entered) AssertNoItemDrops(notice.Scene!, dungeons);
        CompletePartyRoute(team, growth, DungeonKillExperienceCatalog.Load(pvf), dungeons);
        foreach (var session in team)
        {
            var pending = session.PrepareStrongway2019Reward();
            That(pending is { Fact: not null, Admission.Mode: Strongway2019Mode.Hero } &&
                session.CommitStrongway2019Reward(pending, items.Items, f.Repository) is { Commit.Granted: true },
                "首期英雄四个原Run各自真终点提交736邮件");
        }
        Console.WriteLine($"PASS {Count - before} 首期英雄原四人选图、全部真实房间死亡与邮件");
    }

    public static void RunMatchingClear(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf);
        using var f = new Strongway2019MatchingTests.Fixture(towns, activity, growth);
        var first = f.Team(); var second = f.Team();
        var dungeons = DungeonCatalog.Load(pvf);
        var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock);
        var ticket = matching.Queue(first[0], first.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray())!;
        matching.Queue(second[0], second.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray());
        var pair = matching.ReserveNext(ticket)!;
        var entered = new Strongway2019MatchingEntryService(matching, f.Parties, f.Repository, f.Repository, f.Clock)
            .Enter(pair, dungeons, DungeonRoomCatalog.Load(pvf), DungeonMonsterDropCatalog.Load(pvf),
                items.Items, items.EpicPieces, RejectDropPreparation)!;
        That(entered is not null, "匹配通关回归先沿真实八人入场安装");
        foreach (var notice in entered!.Notices) AssertNoItemDrops(notice.Scene!, dungeons);
        foreach (var session in second) second[0].PartyDungeon!.CompleteLoading(session);
        // 通关路径仍由原四人加载/击杀事务推进；此处的开场事实使用同一比赛与原Run。
        foreach (var session in first) first[0].PartyDungeon!.CompleteLoading(session);
        var competition = entered!.Match.Competition;
        var firstRun = first[0].CaptureStrongway2019Run(first[0].PartyDungeon!)!;
        var secondRun = second[0].CaptureStrongway2019Run(second[0].PartyDungeon!)!;
        foreach (int phase in new[] { 3, 4 })
        {
            competition.ConfirmOpening(firstRun, firstRun.Scene.Identity, phase);
            competition.ConfirmOpening(secondRun, secondRun.Scene.Identity, phase);
        }
        CompletePartyRoute(first, growth, DungeonKillExperienceCatalog.Load(pvf), dungeons);
        That(competition.ObserveClear(firstRun, firstRun.Scene.Identity, firstRun.ClearFact!) == DungeonRunOutcome.Accepted &&
            competition.Snapshot.Terminal is { Reason: Strongway2019FinishReason.Clear, WinnerSide: 0 },
            "首期匹配只由获胜原Run的真实终点裁决整队胜利");
        foreach (var session in first)
        {
            var pending = session.PrepareStrongway2019Reward();
            That(pending is { Terminal.WinnerSide: 0 } &&
                session.CommitStrongway2019Reward(pending, items.Items, f.Repository) is { Commit.Granted: true },
                "首期匹配胜队四个原成员提交737邮件");
        }
        That(second.All(session => session.PrepareStrongway2019Reward() is null), "匹配未胜对方不获得胜利邮件");
        Console.WriteLine($"PASS {Count - before} 首期匹配真终点、整队裁决与737邮件");
    }

    static void CompletePartyRoute(GameSession[] team, CharacterGrowthCatalog growth, DungeonKillExperienceCatalog kills,
        DungeonCatalog dungeons)
    {
        var instance = team[0].PartyDungeon!;
        if (team[0].Phase == SessionPhase.Loading)
            foreach (var session in team) instance.CompleteLoading(session);
        var current = team[0].CurrentFormalDungeonScene()!;
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
        That(from.ContainsKey(target), "首期原四人资源路线可达真实终点");
        var path = new Stack<DungeonPoint>();
        for (var point = target; from[point] is { } parent; point = parent) path.Push(point);
        uint actorBoundary = 0;
        int killed = 0;
        while (true)
        {
            actorBoundary = Math.Max(actorBoundary, AssertNoItemDrops(current, dungeons));
            foreach (var monster in current.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.SpawnType == 3).ToArray())
            {
                if (!current.Snapshot.LivingMonsters.Contains(monster.ObjectKey)) continue;
                var observation = FormalDungeonSessionTests.Observation(current, monster);
                if (monster.SpawnType == 3) observation = observation with { RewriteFlag = 1, Unknown18 = 9000, Unknown26 = 30 };
                var notices = instance.ObserveDeath(team[0], monster.ObjectKey,
                    session => session.ObserveFormalDungeonDeath(observation, kills, growth));
                That(notices.Count(notice => notice.Kind == PartyDungeonNoticeKind.Death &&
                    notice.Death?.Outcome == DungeonRunOutcome.Accepted) == 4,
                    "首期四人同一真实死亡报告经原权威和各角色事务提交：" + monster.MonsterId);
                That(notices.Where(notice => notice.Kind == PartyDungeonNoticeKind.Death).All(notice =>
                    notice.Death is { Drop: null, Drops.IsEmpty: true }), "首期每个原成员真实死亡回执不生成任何普通或显式掉落");
                killed++;
                current = team[0].CurrentFormalDungeonScene()!;
            }
            if (!path.TryPop(out var next)) break;
            That(instance.Move(team[0], checked((byte)next.X), checked((byte)next.Y), 0).Length == 4,
                "首期四人原Run在真实清房后一起过房");
            foreach (var session in team) instance.CompleteLoading(session);
            current = team[0].CurrentFormalDungeonScene()!;
        }
        That(team.All(session => session.Phase == SessionPhase.Settlement), "首期四个原Run均由真终点进入结算");
        That(killed > 0 && team.All(session => session.Selected!.DungeonState!.NextObjectKey == actorBoundary),
            "首期保留完整战斗演员并真实击杀，对象key尾界止于最后演员，不额外预留地面对象");
    }

    // 用调用边界证明禁掉落未准备普通计划，不读取Session私有DropPlans或Pools。
    static Func<DungeonPlannedMonster, DungeonDropGenerationPlan> RejectDropPreparation(DungeonEntryPlan entry) =>
        throw new InvalidOperationException("首期禁掉落资源不应调用普通掉落生成器：" + entry.DungeonId);

    static uint AssertNoItemDrops(FormalDungeonReceipt scene, DungeonCatalog dungeons)
    {
        var definition = dungeons.Get(checked((int)scene.Entry.DungeonId));
        That(scene.Entry.DisableItemDrops && !scene.Entry.SpringTreasurePotential && !scene.Entry.SpringTreasureRecommended &&
            !SpringTreasureDropPolicy.IsPotential(definition, scene.Entry) &&
            !SpringTreasureDropPolicy.IsRecommended(definition, scene.Entry, scene.Character.Level) && scene.PassiveItems.IsEmpty,
            "首期原Entry冻结禁掉落且春节策略独立排除潜在及推荐资格：" + scene.Entry.DungeonId);
        return scene.Monsters.Monsters.Select(monster => (uint)monster.ObjectKey)
            .Concat(scene.Monsters.SpecialObjects.Select(actor => (uint)actor.ObjectKey))
            .Concat(scene.Monsters.HellWaves.Select(actor => (uint)actor.ObjectKey)).DefaultIfEmpty(0u).Max() + 1;
    }

    public static void RunFormal(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        int before = Count;
        string directory = Path.Combine(Path.GetTempPath(), "s5a9-sw-formal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("sw-formal-session", () => { });
            var events = new ServerEventService(new ServerEventRepository(path));
            var growth = CharacterGrowthCatalog.Load(pvf);
            var role = repository.Create(owner, new("SWFormal", new(65, 1, 450, 234), towns.InitialReturn)
                { Level = 95, InitialLearning = new(growth.GetLevelStartExperience(95),
                    [new(0, 0, 0, []), new(1, 0, 0, [])]), DungeonState = new(256, 0, 0, 0, 0) });
            var session = new GameSession(repository, towns, owner, entry: new ChannelEntry(new(2, 49), 55, "[dpl_pve]"),
                strongway2019Catalog: new(pvf), strongway2019AntiEvil: _ => 4000, serverEvents: events);
            session.Select(role.Slot, resumeTown: true); session.MarkReady();
            var dungeons = DungeonCatalog.Load(pvf); var rooms = DungeonRoomCatalog.Load(pvf);
            var drops = DungeonMonsterDropCatalog.Load(pvf);
            That(session.EnterFormalDungeon(5359, 0, 2, dungeons, rooms, drops, items: items.Items, pieces: items.EpicPieces) is null &&
                session.Selected!.DungeonState!.LastAttempt == 0, "关闭首期活动时原正式入口拒绝且不预留尝试");
            events.Override(312, true);
            That(session.EnterFormalDungeon(5359, 0, 0, dungeons, rooms, drops, items: items.Items, pieces: items.EpicPieces) is null,
                "模式键0不能冒充资源指定的物理难度2");
            var entered = session.EnterFormalDungeon(5359, 0, 2, dungeons, rooms, drops, items: items.Items, pieces: items.EpicPieces,
                prepareDrops: RejectDropPreparation);
            That(entered is not null && session.Phase == SessionPhase.Loading && entered.Entry.DungeonId == 5359 &&
                entered.Snapshot.Identity.TaskAttempt == 1 && entered.Entry.DisableFatigueConsumption,
                "首期简单实际DGN/完整路线经原正式入场与同writer活动门安装：" + session.LastFormalDungeonRejection);
            That(entered!.Entry.DisableMonsterAndClearExperience, "首期真实disable exp标记禁止怪物及结算经验");
            That(session.CompleteFormalDungeonLoading() is not null && session.Phase == SessionPhase.Dungeon,
                "首期实际Run沿正式加载屏障开放，未用影子实例替代");
            That(session.PrepareStrongway2019Reward() is null, "未达真实终点不因入场或加载授奖");
            var current = session.CurrentFormalDungeonScene()!;
            var initialExperience = session.Selected!.Learning.Experience;
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
            That(from.ContainsKey(target), "首期完整资源路线有原生终点通路");
            var pathToBoss = new Stack<DungeonPoint>();
            for (var point = target; from[point] is { } parent; point = parent) pathToBoss.Push(point);
            var kills = DungeonKillExperienceCatalog.Load(pvf);
            int killed = 0, visited = 0;
            uint actorBoundary = 0;
            while (true)
            {
                visited++;
                actorBoundary = Math.Max(actorBoundary, AssertNoItemDrops(current, dungeons));
                foreach (var monster in current.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.SpawnType == 3).ToArray())
                {
                    if (!current.Snapshot.LivingMonsters.Contains(monster.ObjectKey)) continue;
                    var observation = FormalDungeonSessionTests.Observation(current, monster);
                    if (monster.SpawnType == 3) observation = observation with { RewriteFlag = 1, Unknown18 = 9000, Unknown26 = 30 };
                    var death = session.ObserveFormalDungeonDeath(observation, kills, growth);
                    That(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null,
                        "首期实际怪物沿原死亡权威与提交完成：" + monster.MonsterId);
                    That(death.Drop is null && death.Drops.IsEmpty, "首期简单每个真实死亡回执均无普通或显式掉落");
                    current = death.Receipt!; killed++;
                    That(session.Selected!.Learning.Experience == initialExperience, "首期真实击杀不产生普通经验");
                }
                if (!pathToBoss.TryPop(out var next)) break;
                That(session.PrepareStrongway2019Reward() is null, "非终点清房不提前奖励");
                current = session.MoveFormalDungeon(checked((byte)next.X), checked((byte)next.Y))
                    ?? throw new InvalidOperationException("首期真实路线过房失败：" + next);
                current = session.CompleteFormalDungeonLoading()
                    ?? throw new InvalidOperationException("首期真实路线加载失败：" + next);
            }
            That(visited > 1 && killed > 0 && session.Phase == SessionPhase.Settlement &&
                session.PrepareStrongway2019Reward() is { Fact: not null }, "首期完整原路线产生可领奖真终点");
            That(session.Selected!.DungeonState!.NextObjectKey == actorBoundary,
                "首期简单保留全部真怪演员，对象key尾界不包含额外地面预留");
            var pending = session.PrepareStrongway2019Reward()!;
            That(session.CommitStrongway2019Statistics(pending, repository) is { Recorded: true, Duplicate: false } &&
                session.ReadStrongway2019Statistics(repository) is { NormalBestMode: 0 },
                "实际简单真终点在领奖前独立提交本角色历史");
            That(session.CommitStrongway2019Statistics(pending with { }, repository) is null,
                "复制准备不能冒Session原终点去写成绩");
            var receipt = session.CommitStrongway2019Reward(pending, items.Items, repository);
            That(receipt is { Commit.Granted: true, Commit.MailId: > 0 } &&
                ReferenceEquals(session.CommitStrongway2019Reward(pending, items.Items, repository), receipt),
                "首期实际简单通关提交完整735邮件且同实例重放不重复发奖");
            That(session.CommitStrongway2019Statistics(pending, repository) is { Recorded: true, Duplicate: true },
                "邮件后重投原成绩保持幂等，与奖励是否新授予无关");
            using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"); connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM mailbox_attachments a JOIN character_inventory_items i ON i.item_uid=a.item_uid WHERE json_extract(i.item_core,'$.ItemId')=490012735";
            That(Convert.ToInt32(command.ExecuteScalar()) == 1, "首期实际简单通关只保存一个735附件");
            session.ReturnToRoster();
            That(session.CommitStrongway2019Statistics(pending, repository) is null &&
                session.ReadStrongway2019Statistics(repository) is null, "旧选角epoch不能读写首期成绩");
        }
        finally { TestFiles.DeleteDirectory(directory, true); }
        Console.WriteLine($"PASS {Count - before} 首期实际简单路线准入、原Run与加载");
    }

    public static void RunResources(TownCatalog towns)
    {
        int before = Count;
        string directory = Path.Combine(Path.GetTempPath(), "s5a9-sw-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("sw-admission-session", () => { });
            var events = new ServerEventService(new ServerEventRepository(path));
            var catalog = Strongway2019CatalogTests.CreateCatalog();
            var role = repository.Create(owner, new("SWAdmission", new(65, 1, 450, 234), null)
                { Level = 95, DungeonState = new(256, 0, 0, 0, 0) });
            int antiEvil = 4000, computed = 0;
            Character? sessionActor = null;
            var session = new GameSession(repository, towns, owner, entry: new ChannelEntry(new(2, 49), 55, "[dpl_pve]"),
                strongway2019Catalog: catalog, strongway2019AntiEvil: actor =>
                {
                    That(ReferenceEquals(actor, sessionActor!), "准入只计算当前正式角色的服务端穿戴快照");
                    computed++;
                    return antiEvil;
                }, serverEvents: events);
            session.Select(role.Slot, resumeTown: true); session.MarkReady();
            sessionActor = session.Selected;
            That(session.PrepareStrongway2019Admission(5359) is null, "312默认关闭时正式Session不授予准入");
            var enabled = events.Override(312, true).Schedule;
            var admission = session.PrepareStrongway2019Admission(5359);
            That(admission is { Mode: Strongway2019Mode.Simple, MemberCount: 1, AntiEvil: 4000 } &&
                ReferenceEquals(admission.Character, session.Selected) && admission.Schedule == enabled && computed > 0,
                "正式Session用当前角色、真实活动版本及服务端计算结果生成首期简单准入");
            antiEvil = 3999;
            That(session.PrepareStrongway2019Admission(5359) is null, "穿戴抗魔不足不能沿用此前准入");
            antiEvil = 9999;
            That(session.PrepareStrongway2019Admission(5360) is null &&
                session.PrepareStrongway2019Admission(5360, matched: true) is null &&
                session.PrepareStrongway2019Admission(5363) is null &&
                session.PrepareStrongway2019Admission(5364) is null,
                "单人无法借直接调用获得匹配、英雄或隐藏图资格");
            events.Override(312, false);
            That(session.PrepareStrongway2019Admission(5359) is null, "关活动后不再生成新准入");
            session.ReturnToRoster();
            That(session.PrepareStrongway2019Admission(5359) is null, "旧选角结束后不得授予新准入");
        }
        finally { TestFiles.DeleteDirectory(directory, true); }
        Console.WriteLine($"PASS {Count - before} 首期Session准入、服务端抗魔与活动开关");
    }
}
