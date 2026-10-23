using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 真实目录逐任务验证入场，并补尚缺的清图交付；合成死亡不替代自然演出或客户端验收。
internal static class MetroQuestEntryCoverageTests
{
    sealed record EntryCase(StoryQuest Quest, uint Dungeon, DungeonMaze Maze);

    internal static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var kills = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf);
        var answeredFactions = new HashSet<int>();
        // 六条已有完整TCP末图交付，保留原用例，不在本矩阵重复其战斗与奖励。
        int[] terminalCovered = [3973, 3988, 3989, 3990, 3998, 4872];
        int[] remainingClear = [3946,3947,3949,3950,3951,3960,3961,3962,3963,3964,3965,3966,3967,3969,3970,
            3974,3975,3976,3978,3979,3980,3981,3985,3997,3999,4830,4831,4836,4837,4838,4839,4840,4842,4847,
            7947,7948,7949,7956,7958,7960];
        var terminalVerified = new HashSet<int>();
        var connections = Enumerable.Range(310, 5).SelectMany(id => dungeons.Get(id).Mazes
            .Where(maze => maze.QuestConnectionTokens.Count != 0).Select(maze => (Dungeon: (uint)id, Maze: maze))).ToArray();
        That(connections.Length == 63 && connections.All(row => row.Maze.ConnectionQuestId is not null &&
                row.Maze.QuestConnectionTokens[0].Value == 0),
            "五图保留全部63条非空活动连接，不丢弃异常引用");
        var missing = connections.Where(row => !quests.TryGet(row.Maze.ConnectionQuestId!.Value, out _)).ToArray();
        That(missing.Select(row => (row.Dungeon, row.Maze.SourceOrdinal, row.Maze.ConnectionQuestId!.Value))
                .SequenceEqual(new[] { (311u, 11, 2982), (311u, 12, 3912), (311u, 13, 4885), (312u, 11, 4885) }),
            "三条缺QST的四行连接独立保留，不伪造任务定义或借邻接路线");
        var cases = connections.Where(row => quests.TryGet(row.Maze.ConnectionQuestId!.Value, out _))
            .Select(row => new EntryCase(quests.Get(row.Maze.ConnectionQuestId!.Value), row.Dungeon, row.Maze)).ToArray();
        That(cases.Length == 59 && cases.Select(row => row.Quest.Id).Distinct().Count() == 59 &&
                cases.Count(row => row.Quest.MinimumLevel < 90) == 53 &&
                cases.Count(row => row.Quest.MinimumLevel == 90) == 3 && cases.Count(row => row.Quest.MinimumLevel == 95) == 3,
            "全部59个有效连接均进入业务矩阵，保留Lv90与Lv95任务");
        That(cases.Where(row => row.Quest.MainQuest is not null).Select(row => row.Quest.Id).SequenceEqual(new[] { 3973 }),
            "3973是矩阵唯一顺序子任务，不能用普通前置快照直接接取");
        That(cases.Where(row => row.Quest.Condition == "[clear map]").Select(row => row.Quest.Id).ToHashSet()
                .SetEquals(remainingClear.Concat(terminalCovered)) && remainingClear.Length == 40,
            "46条清图按原任务编号单列，与九条寻物、三条演出及一条物件计数分开");

        int additionalArms = 0;
        foreach (var row in cases.Where(row => row.Quest.MainQuest is null))
        {
            var groups = row.Quest.Definition!.PrerequisiteGroups;
            That(groups.Length > 0, "真实普通任务保留明确前置组：" + row.Quest.Id);
            for (int arm = 0; arm < groups.Length; arm++)
            {
                using var f = Make(row, groups[arm].ToArray());
                var before = f.Read();
                var access = QuestEligibility.Evaluate(before, row.Quest, quests);
                That(QuestEligibility.InspectResource(row.Quest).Entry == QuestResourceEntry.Ordinary &&
                        QuestCoverage.HasOrdinaryDeclarations(row.Quest) && access is { Eligible: true, Visible: true },
                    $"真实目录资格及单个OR臂可普通接取：{row.Quest.Id}/{arm} {access.Reason}");
                That(!QuestEligibility.Evaluate(before with { Level = checked((byte)(row.Quest.MinimumLevel - 1)) },
                    row.Quest, quests).Eligible, "不能以高等级夹具掩盖最低等级：" + row.Quest.Id);
                if (arm == 0)
                {
                    Accept(f, row.Quest.Id);
                    Exercise(f, row, row.Quest.Id, [checked((uint)row.Quest.Id), 0]);
                    if (remainingClear.Contains(row.Quest.Id))
                        CompleteClear(f, row);
                }
                else
                {
                    // 额外OR臂仅规划，不重复完整MAP准备；真实阵营回答已由Make持久提交。
                    var accepted = QuestLifecyclePlan.Accept(before, row.Quest, quests, items.Items,
                        items.GoldLimits, items.EpicPieces, DateTimeOffset.UtcNow);
                    var candidate = before with { Inventory = accepted.Inventory.After, ActiveMainQuests = accepted.ActiveAfter.ToArray() };
                    foreach (uint request in new uint[] { checked((uint)row.Quest.Id), 0 })
                    {
                        That(DungeonEntryPlan.TryCreate(dungeons, candidate, row.Dungeon, request, 0, out var planned, out var reason,
                            quest: request == 0 ? null : row.Quest, quests: quests),
                            $"额外OR臂沿原接取规划显式/0路线：{row.Quest.Id}/{arm}/{request} {reason}");
                        Binding(planned!, candidate, row, row.Quest.Id, request, $"OR[{arm}]", reason);
                    }
                    That(QuestCompletionRepositoryTests.Same(before, f.Read()), "只读OR规划不写角色：" + row.Quest.Id);
                    additionalArms++;
                }
            }
        }
        That(additionalArms == 13, "八条OR任务的21个臂分层覆盖，额外13臂不重复完整副本准备");
        That(answeredFactions.SetEquals(Enumerable.Range(3870, 5)), "五个合法职业/回答阵营分支均由真实问题与会面事务建立");
        Sequential(cases.Single(row => row.Quest.Id == 3973));
        That(terminalVerified.SetEquals(remainingClear),
            "其余40条真实清图任务均走原接取、逐房死亡、末图凭据及交付，不以入场覆盖代替");

        var daily = quests.Get(13056);
        That(!cases.Any(row => row.Quest.Id == daily.Id) && daily.Grade == "[auto daily mission]" &&
                daily.Definition!.Dungeons.Count(row => row.DungeonId is >= 310 and <= 314) == 5 &&
                !QuestHuntProgress.TryTargets(daily, out _) && QuestEligibility.InspectResource(daily).Entry == QuestResourceEntry.Dedicated,
            "13056无连接专用每日合同单列，不能强塞普通31或计入59条完成");
        Console.WriteLine($"PASS {Count - checks} 地轨59条入场、21个OR臂及40条原任务清图交付（死亡为合成输入；自然剧情、物件和13056专用每日仍未核销）");

        QuestSceneTests.Fixture Make(EntryCase row, int[] group)
        {
            var done = group.ToHashSet();
            int faction = FindFaction(group, []);
            byte job = faction is 3872 or 3874 ? (byte)11 : (byte)0;
            StoryQuest? question = faction == 0 ? null : quests.Get(faction is 3872 or 3874 ? 3869 : 3868);
            if (faction != 0)
            {
                done.Remove(faction);
                done.UnionWith(question!.Definition!.PrerequisiteGroups[0]);
            }
            var entrance = Entrance(row, done);
            Location? branchPosition = faction == 0 ? null : Npc(quests.Get(faction).IntData.Single(), done, checked((byte)row.Quest.MinimumLevel));
            var start = question is null ? entrance : Npc(question.IntData.Single(), done, checked((byte)row.Quest.MinimumLevel));
            That(!done.Contains(row.Quest.Id), "区域资格准备不预填目标任务完成位：" + row.Quest.Id);
            var f = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false,
                checked((byte)row.Quest.MinimumLevel), done.Order().ToArray(), job: job, position: start);
            try
            {
                if (question is not null)
                {
                    var branch = quests.Get(faction);
                    var answer = branch.Definition!.RequiredAnswers.Single(required => required.QuestId == question.Id);
                    That(!f.Read().CompletedQuests.Contains(question.Id) && !f.Read().QuestAnswers.ContainsKey(question.Id),
                        "阵营问题不能从准备阶段注入答案：" + faction);
                    Accept(f, question.Id);
                    That(f.Session.ChangeQuest(checked((ushort)question.Id), QuestLifecycleAction.Condition, quests,
                        items.Items, items.GoldLimits, items.EpicPieces, new(null, checked((byte)answer.Answer), 0))?.Commit is not null,
                        "原NPC场景回答真实问题：" + question.Id);
                    Finish(f, question.Id);
                    That(f.Read().QuestAnswers.TryGetValue(question.Id, out byte saved) && saved == answer.Answer,
                        "真实问题交付保存原回答：" + faction);
                    f.TravelTo(towns, branchPosition!);
                    Accept(f, faction);
                    That(f.Session.ChangeQuest(checked((ushort)faction), QuestLifecycleAction.Condition, quests,
                        items.Items, items.GoldLimits, items.EpicPieces, new(null, 0, 0))?.Commit is not null,
                        "原NPC场景完成选定阵营会面：" + faction);
                    Finish(f, faction);
                    That(Enumerable.Range(3870, 5).Count(id => f.Read().CompletedQuests.Contains(id)) == 1,
                        "每个夹具只有真实选择的一个阵营完成位");
                    answeredFactions.Add(faction);
                }
                if (f.Session.Selected!.Position != entrance) f.TravelTo(towns, entrance);
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        int FindFaction(IEnumerable<int> ids, HashSet<int> visited)
        {
            int direct = ids.FirstOrDefault(id => id is >= 3870 and <= 3874);
            if (direct != 0) return direct;
            foreach (int id in ids)
            {
                if (!visited.Add(id) || !quests.TryGet(id, out var dependency) || dependency.Definition!.PrerequisiteGroups.Length == 0) continue;
                int found = FindFaction(dependency.Definition.PrerequisiteGroups[0], visited);
                if (found != 0) return found;
            }
            return 0;
        }

        Location Entrance(EntryCase row, HashSet<int> done)
        {
            var area = towns.Areas.Values.First(candidate => candidate.Level <= row.Quest.MinimumLevel &&
                candidate.DungeonWorldMap is { } world && towns.DungeonMaps[world].Options.Any(option => option.DungeonId == row.Dungeon));
            OpenArea(area, done);
            var gate = area.Map(done).DungeonGates.First(candidate => candidate.Conditions.Length == 0);
            done.UnionWith(gate.Quests);
            return Interior(area, done);
        }

        Location Npc(int id, HashSet<int> done, byte level)
        {
            foreach (var area in towns.Areas.Values.Where(candidate => candidate.Level <= level))
            {
                var candidate = done.ToHashSet();
                OpenArea(area, candidate);
                if (area.ExceptQuests.Any(candidate.Contains) || !area.Map(candidate).Npcs.Any(npc => npc.Id == id)) continue;
                done.UnionWith(candidate);
                return Interior(area, done);
            }
            throw new InvalidOperationException("真实城镇目录没有当前可用NPC场景：" + id);
        }

        static void OpenArea(TownArea area, HashSet<int> done)
        {
            done.UnionWith(area.Quests);
            if (area.AnyQuests.Length > 0 && !area.AnyQuests.Any(done.Contains)) done.Add(area.AnyQuests[0]);
        }

        static Location Interior(TownArea area, HashSet<int> done)
        {
            var bounds = area.Map(done).Bounds.First(region => region.Conditions.Length == 0 && region.Quests.All(done.Contains)).Bounds;
            return new(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        }

        void Accept(QuestSceneTests.Fixture f, int id)
        {
            var result = f.Session.ChangeQuest(checked((ushort)id), QuestLifecycleAction.Accept, quests,
                items.Items, items.GoldLimits, items.EpicPieces);
            That(result?.Commit is not null, $"真实31接取{id}：{result?.Reason}");
        }

        void Finish(QuestSceneTests.Fixture f, int id)
        {
            var result = f.Session.FinishQuest(checked((ushort)id), ushort.MaxValue, quests, parameters,
                items.Items, items.GoldLimits, items.EpicPieces, growth);
            That(result.Outcome == QuestFinishOutcome.Committed, $"真实34交付{id}：{result.Outcome}/{result.Failure}");
        }

        void Sequential(EntryCase row)
        {
            var parent = quests.Get(3971);
            That(QuestRelationProgress.SequentialParent(parent, quests) && parent.IntData.SequenceEqual(new[] { 3972, 3973 }),
                "3971使用原顺序父任务及两个真实子任务");
            var done = parent.Definition!.PrerequisiteGroups[0].ToHashSet();
            var entrance = Entrance(row, done);
            var meeting = Npc(quests.Get(3972).IntData.Single(), done, 88);
            using var f = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 88,
                done.Order().ToArray(), position: meeting);
            Accept(f, 3971);
            That(f.Read().ActiveMainQuests.Single(active => active.QuestId == 3971).ConditionState == 2 &&
                    !QuestEligibility.Evaluate(f.Read(), row.Quest, quests).Eligible,
                "3972未完成时父scalar2，不能提前接3973");
            var unchanged = f.Read();
            That(f.Session.ChangeQuest(3973, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits, items.EpicPieces)?.Commit is null &&
                    QuestCompletionRepositoryTests.Same(unchanged, f.Read()), "提前31不改变原父接取");
            foreach (uint request in new uint[] { 3971, 0 })
            {
                bool allowed = DungeonEntryPlan.TryCreate(dungeons, f.Read(), row.Dungeon, request, 0, out var plan, out _,
                    quest: request == 0 ? null : parent, quests: quests);
                That(!allowed || !plan!.Maze.ConnectsQuest(3973), "当前会面子任务不能借后继3973迷宫：" + request);
            }
            Accept(f, 3972);
            That(f.Session.ChangeQuest(3972, QuestLifecycleAction.Condition, quests, items.Items,
                items.GoldLimits, items.EpicPieces, new(null, 0, 0))?.Commit is not null, "真实NPC455场景达成3972会面");
            Finish(f, 3972);
            That(f.Read().CompletedQuests.Contains(3972) &&
                f.Read().ActiveMainQuests.Single(active => active.QuestId == 3971).ConditionState == 1,
                "实际子交付将原父scalar2推进为1");
            f.TravelTo(towns, entrance);
            Exercise(f, row, 3971, [3971, 0]);
            Accept(f, 3973);
            Exercise(f, row, 3973, [3973, 0]);
        }

        void Exercise(QuestSceneTests.Fixture f, EntryCase row, int routeQuest, uint[] requests)
        {
            DungeonEntryPlan? first = null;
            foreach (uint request in requests)
            {
                var before = f.Read();
                That(DungeonEntryPlan.TryCreate(dungeons, before, row.Dungeon, request, 0, out var plan, out var reason,
                    quest: request == 0 ? null : quests.Get(checked((int)request)), quests: quests),
                    $"59条真实任务入场规划：{row.Quest.Id}/{request} {reason}");
                Binding(plan!, before, row, routeQuest, request, "planned", reason);
                if (first is not null) That(first.Maze.SameLayout(plan!.Maze) && first.StartMap == plan.StartMap && first.BossMap == plan.BossMap,
                    "显式与普通0复用同一原接取路线：" + row.Quest.Id);
                first ??= plan;
                var expected = CompletePlans(plan!, before.DungeonState!.NextObjectKey);
                var opened = f.Session.EnterFormalDungeon(row.Dungeon, request, 0, dungeons, rooms);
                That(opened is not null, $"生产Session完整资源准备并入场：{row.Quest.Id}/{request}");
                Binding(opened!.Entry, before, row, routeQuest, request, "installed", reason);
                That(opened.Entry.CharacterRevision == before.Revision + 1 && opened.Generation == f.Owner.Generation &&
                    opened.Snapshot.Identity.TaskAttempt == before.DungeonState!.LastAttempt + 1 && f.Session.CanPublishFormalDungeon(opened),
                    "入场提交绑定本人owner、revision及独立尝试：" + row.Quest.Id);
                var loaded = f.Session.CompleteFormalDungeonLoading()
                    ?? throw new InvalidOperationException("真实起点未完成加载：" + row.Quest.Id);
                var openingPlan = expected.Single(plan => plan.Room.Map == loaded.Snapshot.CurrentMap);
                That(f.Session.Phase == SessionPhase.Dungeon &&
                    loaded.Monsters.Monsters.Select(Shape).SequenceEqual(openingPlan.Monsters.Select(Shape)) &&
                    loaded.Monsters.SpecialObjects.Select(actor => (actor.ObjectKey, actor.Source.SourceOrdinal, actor.Source.ObjectId))
                        .SequenceEqual(openingPlan.SpecialObjects.Select(actor => (actor.ObjectKey, actor.Source.SourceOrdinal, actor.Source.ObjectId))) &&
                    f.Session.EnableFormalDungeonSettlement() is null, "真实起点加载完整演员且不能提前结算：" + row.Quest.Id);
                long retreatStarted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                That(f.Session.ReturnFromFormalDungeon() is not null && !f.Session.CanPublishFormalDungeon(opened),
                    "未通关退出释放原实例及发布资格：" + row.Quest.Id);
                long retreatFinished = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var after = f.Read();
                That(after.Inventory.StaminaUntil >= retreatStarted+600 && after.Inventory.StaminaUntil <= retreatFinished+600,
                    "非训练房未清图回城沿正式规则写入十分钟虚弱期限：" + row.Quest.Id);
                That(after.ActiveMainQuests.SequenceEqual(before.ActiveMainQuests) && after.CompletedQuests.SequenceEqual(before.CompletedQuests) &&
                    after.AccountCompletedQuests.SequenceEqual(before.AccountCompletedQuests) &&
                    after.QuestAnswers.OrderBy(pair => pair.Key).SequenceEqual(before.QuestAnswers.OrderBy(pair => pair.Key)) &&
                    after.AccountQuestAnswers.OrderBy(pair => pair.Key).SequenceEqual(before.AccountQuestAnswers.OrderBy(pair => pair.Key)) &&
                    JsonSerializer.Serialize(after.Inventory) == JsonSerializer.Serialize(
                        before.Inventory with { StaminaUntil = after.Inventory.StaminaUntil }) &&
                    JsonSerializer.Serialize(after.Learning) == JsonSerializer.Serialize(before.Learning) &&
                    f.Repository.LoadQuestClear(f.Owner, after, checked((ushort)row.Quest.Id)) is null,
                    "入场/加载/退出不制造任务进度、清图、答案或奖励：" + row.Quest.Id);
            }
        }

        void CompleteClear(QuestSceneTests.Fixture f, EntryCase row)
        {
            ushort id = checked((ushort)row.Quest.Id);
            var accepted = f.Read().ActiveMainQuests.Single(active => active.QuestId == id);
            That(accepted.ConditionState == 1 && f.Repository.LoadQuestClear(f.Owner, f.Read(), id) is null,
                "入场矩阵没有预填本次清图条件或凭据：" + id);
            ushort selector = QuestFixedRewardDefinition.Parse(row.Quest with { Rewards = row.Quest.Definition!.SelectRewards })
                .ForJob(f.Read().Job, f.Read().GrowType).Length == 0 ? ushort.MaxValue : (ushort)0;
            QuestFinishResult FinishClear() => f.Session.FinishQuest(id, selector, quests, parameters,
                items.Items, items.GoldLimits, items.EpicPieces, growth);
            var before = f.Read();
            That(FinishClear().Outcome == QuestFinishOutcome.Rejected && QuestCompletionRepositoryTests.Same(before, f.Read()),
                "尚无末图事实时34拒绝且不改变资产：" + id);
            var opening = f.Session.EnterFormalDungeon(row.Dungeon, 0, 0, dungeons, rooms)
                ?? throw new InvalidOperationException("清图任务普通0入场失败：" + id);
            var target = opening.Entry.QuestClearMap ?? opening.Entry.TerminalLayerMap ?? opening.Entry.BossMap;
            That(target.Id == row.Quest.IntData.Single() && opening.Entry.RouteQuestId == id &&
                opening.Entry.QuestAcceptances.Any(active => active.AcceptanceId == accepted.AcceptanceId),
                "清图路线独立匹配QST目标和原AcceptanceId：" + id);
            var path = QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology, opening.Entry.StartMap.Position, target.Position);
            var receipt = f.Session.CompleteFormalDungeonLoading()!;
            NativeDungeonDeathObservation? previousRoomDeath = null;
            ushort? clearCause = null;
            for (int at = 0; at < path.Length; at++)
            {
                if (at == path.Length - 1 && receipt.Snapshot.CurrentMap != target)
                {
                    That(target.Kind == "layered" && f.Session.MoveFormalDungeon(checked((byte)target.Position.X),
                        checked((byte)target.Position.Y), 1) is not null, "真实目标层沿原kind1进入，不击杀基础层替代：" + id);
                    receipt = f.Session.CompleteFormalDungeonLoading()!;
                }
                That(receipt.Snapshot.CurrentMap.Position == path[at] && receipt.Generation == f.Owner.Generation &&
                    receipt.Snapshot.Identity.InstanceId == opening.Snapshot.Identity.InstanceId,
                    "逐房沿原门墙抵达目标坐标并保留原owner及实例：" + id);
                before = f.Read();
                var active = before.ActiveMainQuests.Single(active => active.QuestId == id);
                That(active.AcceptanceId == accepted.AcceptanceId && active.Slot == accepted.Slot && active.ConditionState == 1 &&
                    f.Repository.LoadQuestClear(f.Owner, before, id) is null && FinishClear().Outcome == QuestFinishOutcome.Rejected &&
                    QuestCompletionRepositoryTests.Same(before, f.Read()), "加载与换房不能预填末图凭据或提前34：" + id);
                if (previousRoomDeath is { } old)
                {
                    before = f.Read();
                    var stale = f.Session.ObserveFormalDungeonDeath(old, kills, growth);
                    That(stale.Reward is null && stale.Receipt is null && stale.ClearFact is null &&
                        f.Repository.LoadQuestClear(f.Owner, f.Read(), id) is null && QuestCompletionRepositoryTests.Same(before, f.Read()),
                        "旧房实际key重报不能给当前房签发清图或资产：" + id);
                }
                foreach (var monster in receipt.Monsters.Monsters.Where(actor => actor.IsBlocking).OrderBy(actor => actor.IsBoss))
                {
                    That(monster.CanReportDeath && !monster.IsFriendly, "清图只报告本图真实敌人，不杀友方或装饰物：" + id);
                    var observation = FormalDungeonSessionTests.Observation(receipt, monster);
                    var result = f.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    That(result.Outcome == DungeonRunOutcome.Accepted, "本图已下发对象沿原死亡事务提交：" + id);
                    var deathClear = f.Repository.LoadQuestClear(f.Owner, f.Read(), id);
                    if (deathClear is not null && clearCause is null)
                    {
                        That(at == path.Length - 1 && result.ClearFact is { } fact &&
                            fact.ConfirmedDeathEvents.Contains(deathClear.EventId) && result.MonsterKey == monster.ObjectKey,
                            "清图凭据首次产生于实际末图本次已确认死亡，不由加载或旧房补写：" + id);
                        clearCause = monster.ObjectKey;
                    }
                    before = f.Read();
                    That(f.Session.ObserveFormalDungeonDeath(observation, kills, growth).Outcome == DungeonRunOutcome.Duplicate &&
                        f.Repository.LoadQuestClear(f.Owner, f.Read(), id) == deathClear && QuestCompletionRepositoryTests.Same(before, f.Read()),
                        "同一死亡重复输入不重复清图凭据、进度或奖励：" + id);
                    previousRoomDeath = observation;
                }
                if (at == path.Length - 1) break;
                That(f.Read().ActiveMainQuests.Single(active => active.QuestId == id).ConditionState == 1 &&
                    f.Repository.LoadQuestClear(f.Owner, f.Read(), id) is null, "前房死亡不能代替QST末图：" + id);
                var next = path[at + 1];
                That(f.Session.MoveFormalDungeon(checked((byte)next.X), checked((byte)next.Y)) is not null,
                    "真实清图路线按原门墙及warp条件换房：" + id);
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
            var clear = f.Repository.LoadQuestClear(f.Owner, f.Read(), id);
            That(clearCause is not null && clear is not null && clear.AcceptanceId == accepted.AcceptanceId && clear.QuestSlot == accepted.Slot &&
                clear.MapId == row.Quest.IntData.Single() && clear.DungeonId == row.Dungeon &&
                clear.InstanceId == opening.Snapshot.Identity.InstanceId && clear.TaskAttempt == opening.Snapshot.Identity.TaskAttempt &&
                f.Read().ActiveMainQuests.Single(active => active.QuestId == id).ConditionState == 0,
                "实际末图清图凭据绑定本次实例、原槽、Acceptance及尝试：" + id);
            before = f.Read();
            var finished = FinishClear();
            That(finished.Outcome == QuestFinishOutcome.Committed && finished.Receipt?.Commit is { Duplicate: false } commit &&
                commit.AcceptanceId == accepted.AcceptanceId && commit.Clear == clear && commit.ConditionType == 3 &&
                (ulong)f.Read().Inventory.Gold == (ulong)before.Inventory.Gold + commit.DirectGoldAmount &&
                f.Read().Learning.Experience == before.Learning.Experience + commit.GainedExperience &&
                f.Read().CompletedQuests.Contains(id) && f.Read().ActiveMainQuests.All(active => active.QuestId != id),
                "原34按原凭据交付，金币和实际经验与唯一提交一致：" + id);
            before = f.Read();
            That(FinishClear().Outcome == QuestFinishOutcome.AlreadyCompleted && QuestCompletionRepositoryTests.Same(before, f.Read()) &&
                f.Query($"SELECT count(*) FROM character_quest_completion_receipts WHERE character_id={before.Id} AND quest_id={id}") is 1L,
                "重复34不重复发奖且仅一条完成回执：" + id);
            That(f.Session.ReturnFromFormalDungeon() is not null, "清图交付后沿原返回流程退出：" + id);
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Read().CompletedQuests.Contains(id) && f.Read().ActiveMainQuests.All(active => active.QuestId != id) &&
                f.Query($"SELECT count(*) FROM character_quest_completion_receipts WHERE character_id={before.Id} AND quest_id={id}") is 1L,
                "重新选角从持久化恢复唯一交付，不注入完成快照：" + id);
            That(terminalVerified.Add(id), "每条未覆盖清图任务仅验证一次原末图交付：" + id);
        }

        static void Binding(DungeonEntryPlan entry, Character character, EntryCase row, int routeQuest, uint request,
            string stage, string reason)
        {
            string context = $"QID={row.Quest.Id} request={request} route={routeQuest} stage={stage} reason={reason}";
            Check(entry.CharacterId == character.Id, $"CharacterId expected={character.Id} actual={entry.CharacterId}");
            Check(entry.DungeonId == row.Dungeon, $"DungeonId expected={row.Dungeon} actual={entry.DungeonId}");
            Check(entry.QuestId == request, $"QuestId expected={request} actual={entry.QuestId}");
            Check(entry.RouteQuestId == routeQuest, $"RouteQuestId expected={routeQuest} actual={entry.RouteQuestId}");
            Check(entry.Maze.SourceOrdinal == row.Maze.SourceOrdinal,
                $"Maze.SourceOrdinal expected={row.Maze.SourceOrdinal} actual={entry.Maze.SourceOrdinal}");
            Check(entry.Maze.QuestConnectionTokens.SequenceEqual(row.Maze.QuestConnectionTokens), "Maze.QuestConnectionTokens");
            var expectedMaze = EntryMaze(row);
            bool sameLayout = entry.Maze.SameLayout(expectedMaze);
            Check(sameLayout, "Maze.SameLayout" + (sameLayout ? "" :
                $" expected={JsonSerializer.Serialize(expectedMaze)} actual={JsonSerializer.Serialize(entry.Maze)}"));
            Check(entry.QuestAcceptances.SequenceEqual(character.ActiveMainQuests), "QuestAcceptances原活动快照");
            Check(character.ActiveMainQuests.Any(active => active.QuestId == routeQuest && QuestHuntProgress.Bound(active, entry)),
                "原路线任务AcceptanceId绑定");
            var active = request == 0 ? null : character.ActiveMainQuests.Single(value => value.QuestId == request);
            Check(entry.ActiveQuestSlot == (active?.Slot ?? byte.MaxValue),
                $"ActiveQuestSlot expected={active?.Slot ?? byte.MaxValue} actual={entry.ActiveQuestSlot}");
            Check(entry.QuestAcceptanceId == (active?.AcceptanceId ?? Guid.Empty), "QuestAcceptanceId原接取/普通0哨兵");
            if (QuestClearEligibility.IsSingleMap(row.Quest))
            {
                Check(entry.HasExplicitClearMap, "HasExplicitClearMap");
                Check((entry.QuestClearMap ?? entry.TerminalLayerMap ?? entry.BossMap).Id == row.Quest.IntData[0],
                    $"原清图目标 expected={row.Quest.IntData[0]} actual={(entry.QuestClearMap ?? entry.TerminalLayerMap ?? entry.BossMap).Id}");
            }

            void Check(bool condition, string field) => That(condition, $"入场绑定字段 {field}：{context}");
        }

        static DungeonMaze EntryMaze(EntryCase row)
        {
            var maze = row.Maze;
            var layerIndexes = new Dictionary<DungeonPoint, int>();
            maze = maze with
            {
                LayeredMaps = maze.LayeredMaps.Select(map =>
                {
                    // 当前59矩阵三条叠层均为单候选；只独立计算生产按坐标分配的层序，不改地图资源身份。
                    That(map.CandidateIds.Count <= 1 && (map.CandidateIds.Count == 0 || map.CandidateIds[0] == map.Id),
                        $"原目录单候选叠层完整身份：QID={row.Quest.Id} map={map.Id}");
                    int index = layerIndexes.GetValueOrDefault(map.Position);
                    layerIndexes[map.Position] = index + 1;
                    return map with { LayerIndex = index };
                }).ToArray()
            };
            if (maze.StoryStages.Count != 0 || maze.Bosses.Count != 1 || maze.MapSpecification is null ||
                !QuestClearEligibility.IsSingleMap(row.Quest)) return maze;
            var bosses = maze.MapSpecification.Where(map => map.Position == maze.Bosses[0]).ToArray();
            if (bosses is not [{ Kind: "map" } boss] || boss.Id != row.Quest.IntData[0] &&
                !maze.LayeredMaps.Any(map => map.Position == boss.Position && map.Id == row.Quest.IntData[0])) return maze;
            // 对齐生产清图计划：仅当前Boss基础格及其同坐标条件格可投影Kind，其余资源字段完整比较。
            return maze with
            {
                MapSpecification = maze.MapSpecification.Select(map => map == boss ? map with { Kind = "boss" } : map).ToArray(),
                ConditionalMaps = maze.ConditionalMaps.Select(map => map.Position == boss.Position && map.Kind == boss.Kind
                    ? map with { Kind = "boss" } : map).ToArray()
            };
        }

        DungeonMonsterPlan[] CompletePlans(DungeonEntryPlan entry, uint next)
        {
            That(!entry.IsTutorial, "地轨矩阵使用普通完整资源策略，不借训练场景简化演员");
            var plans = new List<DungeonMonsterPlan>();
            var keys = new HashSet<ushort>();
            // 与Session准备顺序一致；同ID的基础、叠层和条件格均保留各自地图身份与key区间。
            foreach (var map in entry.Maze.MapSpecification!.Concat(entry.Maze.LayeredMaps).Concat(entry.Maze.ConditionalMaps))
            {
                That(next <= ushort.MaxValue, "完整路线预留对象key不能溢出：" + map.Id);
                var room = rooms.Get(map, requirePassiveDefinitions: false, allowUnresolvedSpecialMetadata: entry.IsTutorial);
                var plan = DungeonMonsterPlan.FromRoom(room, entry.CharacterId, checked((ushort)next), entry.BasisLevel,
                    includeSpecial: !entry.IsTutorial, playerTeam: entry.PlayerTeam);
                That(plan.Monsters.Count(actor => actor.Source is { SpecialChild: null }) == room.Monsters.Count &&
                    plan.Monsters.Count(actor => actor.AiCharacter is not null) == room.AiCharacters.Count &&
                    plan.SpecialObjects.Count == room.SpecialObjects.Count &&
                    plan.Monsters.Select(actor => actor.ObjectKey).Concat(plan.SpecialObjects.Select(actor => actor.ObjectKey))
                        .All(key => key != entry.CharacterId && keys.Add(key)), "完整MAP保留全部演员及全实例唯一key：" + map.Id);
                plans.Add(plan);
                if (plan.Monsters.Count > 0 || plan.SpecialObjects.Count > 0) next = plan.NextObjectKey;
            }
            return plans.ToArray();
        }

        static (ushort Key, int Ordinal, int Id, int Level, byte Type, int Team, bool Friendly, bool Blocking, int? Ai) Shape(DungeonPlannedMonster actor)
            => (actor.ObjectKey, actor.SourceOrdinal, actor.MonsterId, actor.CandidateLevel, actor.SpawnType,
                actor.Team, actor.IsFriendly, actor.IsBlocking, actor.AiCharacter?.Id);
    }
}
