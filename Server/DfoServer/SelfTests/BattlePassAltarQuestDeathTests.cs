using System.Collections.Immutable;
using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassAltarQuestDeathTests
{
    public static void Run(TcpTestResources resources) => RunScenario(resources, replaceSelectedOnRetry: false);
    public static void RunPendingRetry(TcpTestResources resources) => RunScenario(resources, replaceSelectedOnRetry: true);
    public static void RunMixedBatch(TcpTestResources resources)
    {
        RunScenario(resources,replaceSelectedOnRetry:false,mixedOrder:1);
        RunScenario(resources,replaceSelectedOnRetry:false,mixedOrder:2);
    }

    public static void RunMixedBatchDiagnostic(TcpTestResources resources)
    {
        var failures=new List<Exception>();
        foreach (int order in new[] { 1,2 })
            try { RunScenario(resources,replaceSelectedOnRetry:false,mixedOrder:order,diagnostics:true); }
            catch (Exception error)
            {
                Console.WriteLine($"ALTAR_MIXED_DIAG_FAILURE order={order} {error}");
                failures.Add(error);
            }
        if (failures.Count!=0) throw new AggregateException("真实混合255诊断保留原失败",failures);
    }

    static void RunScenario(TcpTestResources resources, bool replaceSelectedOnRetry, int mixedOrder=0, bool diagnostics=false)
    {
        int before = Count;
        var archive = resources.Archive;
        var dungeons = SelfTestCatalogs.Dungeons(archive);
        var rooms = SelfTestCatalogs.Rooms(archive);
        var growth = SelfTestCatalogs.Growth(archive);
        var items = resources.ItemResources;
        var missionPolicy = new BattlePassMissionPolicy("altar-target-source-fixture",
            new DateTimeOffset(2026,10,22,6,0,0,TimeSpan.FromHours(8)), BattlePassCatalog.Load(archive));
        var at = Enumerable.Range(0,missionPolicy.Days).Select(day=>missionPolicy.Start.AddDays(day).AddHours(1))
            .First(value=>missionPolicy.Active(value).Any(row=>row.Key.Category==0 && row.Key.Id==1));
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 11006));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(area.Default.Bounds.SelectMany(row => row.Quests))
            .Concat(area.Default.DungeonGates.SelectMany(row => row.Quests)).Distinct().ToArray();
        var bounds = area.Map(done.ToHashSet()).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        // 原生选图会自动携带关联支线任务；不能只测手工清零任务号的请求。
        // 5650目标是独占末轮；7344原Round5/A有两目标及61488非目标，适合真实混合255。
        var scenarios=mixedOrder==0
            ? new[] { (11006u,(ushort)5650,63),(11007u,(ushort)7340,7339) }
            : new[] { (11007u,(ushort)7344,7343) };
        foreach (var (dungeonId, questId, prerequisite) in scenarios)
        {
            if (replaceSelectedOnRetry && dungeonId != 11006) continue;
            var clock = new Clock(at);
            using var linked = New(clock, done.Append(prerequisite).Distinct().ToArray());
            var accepted = linked.Session.ChangeQuest(questId, DfoServer.Game.Quests.QuestLifecycleAction.Accept,
                resources.Quests, items.Items, items.GoldLimits, items.EpicPieces);
            That(accepted?.Commit is not null, "合法接取祭坛关联支线：" + questId);
            That(linked.Session.BeginDungeonSelection() is not null, "关联任务祭坛建立原选图上下文");
            That(linked.Session.EnterFormalDungeon(dungeonId, (uint)(questId + 1), 0, dungeons, rooms, pieces: items.EpicPieces) is null &&
                linked.Session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!.Counts.All(row => row.Daily == 3) &&
                linked.Session.Selected!.Inventory.Gold == 500000 && linked.Session.DungeonSnapshot is null,
                "未接取任务仍拒绝，不扣费扣次或安装实例");
            if (!replaceSelectedOnRetry && mixedOrder == 0)
            {
                using var cross = New(new Clock(at), done.Append(prerequisite).Distinct().ToArray());
                That(cross.Session.ChangeQuest(questId, QuestLifecycleAction.Accept,
                    resources.Quests, items.Items, items.GoldLimits, items.EpicPieces)?.Commit is not null &&
                    cross.Session.BeginDungeonSelection() is not null, "独立祭坛fixture保留真实任务接取与选图");
                var active = cross.Session.Selected!.ActiveMainQuests.Single(row => row.QuestId == questId);
                uint otherDungeon = dungeonId == 11006 ? 11007u : 11006u;
                var carried = cross.Session.EnterFormalDungeon(otherDungeon, questId, 0, dungeons, rooms, pieces: items.EpicPieces);
                That(carried is not null && carried.Monsters.Altar is not null && carried.Entry.IsOrdinaryHunt &&
                    carried.Entry.DungeonId == otherDungeon && carried.Entry.QuestId == questId && carried.Entry.RouteQuestId == 0 &&
                    carried.Entry.QuestAcceptanceId == active.AcceptanceId && carried.Entry.QuestAcceptances.Contains(active) &&
                    cross.Read().ActiveMainQuests.Single(row => row.QuestId == questId) == active,
                    "已接取猎杀任务可由另一普通祭坛承载，保持专用波次与原接取身份");
                int fee = otherDungeon == 11006 ? dungeons.AltarAdmission.BloodFee(cross.Session.Selected!.Level, 0) : 0;
                That(cross.Session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!.Counts
                        .All(row => row.Daily == (row.Dungeon == otherDungeon ? 2 : 3)) &&
                    cross.Read().Inventory.Gold == 500000 - fee,
                    "跨祭坛普通任务入场只扣实际所选祭坛的一次额度与资源规定费用");
                if (questId == 7340)
                {
                    var crossTarget = resources.Quests.Get(questId).IntData[2];
                    var crossMonster = new DungeonPlannedMonster(0, 100, crossTarget, 70, null);
                    That(QuestHuntProgress.MonsterDeath([active], carried!.Entry, resources.Quests, crossMonster)
                            .SequenceEqual([active]),
                        "7340原接取在11006死亡即使模板相同也不推进11007目标");
                }
            }
            var entered = linked.Session.EnterFormalDungeon(dungeonId, questId, 0, dungeons, rooms, pieces: items.EpicPieces);
            That(entered is not null, $"关联任务不阻断祭坛准入：{questId}/{linked.Session.LastFormalDungeonRejection}");
            That(entered!.Monsters.Altar is not null && entered.Entry.QuestId == questId && entered.Entry.RouteQuestId == 0 &&
                entered.Entry.QuestAcceptanceId == linked.Session.Selected!.ActiveMainQuests.Single(row => row.QuestId == questId).AcceptanceId,
                "祭坛复用普通任务路线资格并冻结原接取，仍使用专用波次与次数事务");
            if (dungeonId == 11007)
            {
                var hunts = resources.Quests.All.Where(quest => quest.Condition == "[hunt monster]" &&
                    (quest.Id == 5650 || quest.DungeonIndex is 11006 or 11007 || quest.IntData.Count >= 4 && quest.IntData[0] is 11006 or 11007)).ToArray();
                That(hunts.Select(quest => quest.Id).Order().SequenceEqual(new[] { 5650, 7340, 7341, 7343, 7344, 7345 }),
                    "正式任务索引可加载的六条祭坛猎杀声明完整覆盖");
                foreach (var quest in hunts)
                {
                    bool supported = QuestHuntProgress.TryTargets(quest, out var targets);
                    That(supported == (quest.Grade != "[training]"), "祭坛同类普通猎杀声明可消费，training不扩大准入：" + quest.Id);
                    if (!supported) continue;
                    var active = new ActiveMainQuest(0, checked((ushort)quest.Id), QuestHuntProgress.Initial(quest)) { AcceptanceId = Guid.NewGuid() };
                    var entry = entered.Entry with { DungeonId = quest.IntData[0] == 11007 ? 11007u : 11006u, QuestAcceptances = [active] };
                    var remaining = ImmutableArray.Create(active);
                    foreach (var hunt in targets)
                    {
                        var monster = new DungeonPlannedMonster(0, 100, hunt.MonsterId, 70, null);
                        That(QuestHuntProgress.MonsterDeath(remaining, entry with { QuestAcceptances = [] }, resources.Quests, monster)
                                .SequenceEqual(remaining), "祭坛同类目标仍只消费原接取身份");
                        remaining = QuestHuntProgress.MonsterDeath(remaining, entry, resources.Quests, monster);
                    }
                    That(remaining.Single().ConditionState == 0, "祭坛各真实目标完整推进含木乃伊双通道：" + quest.Id);
                }
            }
            var huntQuest=resources.Quests.Get(questId);
            var target = huntQuest.IntData[2];
            var targetTemplates=Enumerable.Range(0,huntQuest.IntData.Count/4)
                .Select(index=>huntQuest.IntData[index*4+2]).ToHashSet();
            uint initialCondition=mixedOrder==0 ? 1u : 513u;
            if (mixedOrder!=0)
                That(huntQuest.Id==7344 && huntQuest.Grade=="[side]" && huntQuest.MinimumLevel==65 &&
                    huntQuest.MaximumLevel==99 && huntQuest.Prerequisites.SequenceEqual([7343]) &&
                    huntQuest.Definition is { Job:"[all]",GrowType:-1,JobChange:0,Diagnostics.Length:0 } &&
                    huntQuest.IntData.SequenceEqual([11007,-1,56445,1,11007,-1,56446,1]) &&
                    linked.Read().ActiveMainQuests.Single(row=>row.QuestId==questId).ConditionState==initialCondition,
                    "7344沿真实前置、全职业等级资格与双目标513初始编码合法接取");
            long confirmedDeaths = 0;
            bool targetKilled = false;
            while (!targetKilled)
            {
                var loaded = linked.Session.CompleteFormalDungeonLoading()!;
                That(linked.Session.PrepareDungeonAltarUi(loaded), "任务祭坛沿真实加载准备波次");
                var current = linked.Session.StartDungeonAltar(loaded)!;
                ushort? mixedCompanion = null;
                if (diagnostics && current.State.Plan.Actors.Any(row=>row.Phase.MonsterId==target))
                    Console.WriteLine("ALTAR_MIXED_DIAG_PLAN "+System.Text.Json.JsonSerializer.Serialize(new
                    {
                        dungeonId, mixedOrder, target, map=loaded.Snapshot.CurrentMap.Id,
                        rounds=current.State.Plan.Rounds.Select(row=>new { row.Round,row.Difficulty,
                            batches=row.Batches.Select(part=>new { part.DelayMilliseconds,
                                actors=part.Actors.Select(value=>new { value.ObjectKey,monster=value.Phase.MonsterId,value.Rank }) }) })
                    }));
                void TraceMixed(string stage,DungeonAltarBatch observed)
                {
                    if (!diagnostics) return;
                    var state=current.State;
                    var living=linked.Session.DungeonSnapshot!.LivingMonsters;
                    var active=state.Plan.Rounds.Single(row=>row.Round==state.Round && row.Difficulty==state.Difficulty);
                    Console.WriteLine("ALTAR_MIXED_DIAG_STATE "+System.Text.Json.JsonSerializer.Serialize(new
                    {
                        stage,dungeonId,mixedOrder,target,state.Round,state.Difficulty,current.Due,
                        retainedKey=mixedCompanion,batchIndex=active.Batches.IndexOf(observed),observed.DelayMilliseconds,
                        batchActors=observed.Actors.Select(row=>new { row.ObjectKey,monster=row.Phase.MonsterId }),
                        livingKeys=living,
                        roundActors=active.Batches.SelectMany(row=>row.Actors).Select(row=>new
                        { row.ObjectKey,monster=row.Phase.MonsterId,published=state.WasPublished(row.ObjectKey),alive=living.Contains(row.ObjectKey) })
                    }));
                }
                while (!current.State.Completed && !targetKilled)
                {
                    if (current.State.WaitingForDifficulty)
                        current = linked.Session.SelectDungeonAltarDifficulty(mixedOrder==0 ? current.State.Choices.Last() : (byte)1)!;
                    clock.AdvanceTo(current.Due!.Value);
                    current = linked.Session.TickDungeonAltar(current)!;
                    if (current.Batch is not { } batch) continue;
                    var actors=mixedOrder==0 ? batch.Actors : batch.Actors.OrderByDescending(row=>row.Phase.MonsterId==target).ToImmutableArray();
                    foreach (var actor in actors)
                    {
                        bool matches = actor.Phase.MonsterId == target;
                        // 7344的56446是另一真实任务目标，不能作为首目标的非目标同伴或提前清掉。
                        if (mixedOrder!=0 && !matches && targetTemplates.Contains(actor.Phase.MonsterId)) continue;
                        if (mixedOrder!=0 && !targetTemplates.Contains(actor.Phase.MonsterId) && current.State.Plan.Rounds
                            .Single(row=>row.Round==current.State.Round && row.Difficulty==current.State.Difficulty)
                            .Batches.SelectMany(row=>row.Actors).Any(row=>row.Phase.MonsterId==target))
                        {
                            // 目标可能是本轮最后一批；从真实已选时间线预留一只先发布的非目标，保持其存活。
                            mixedCompanion ??= actor.ObjectKey;
                            if (actor.ObjectKey==mixedCompanion)
                            {
                                That(current.State.WasPublished(actor.ObjectKey) &&
                                    linked.Session.DungeonSnapshot!.LivingMonsters.Contains(actor.ObjectKey),
                                    "混合255同伴来自原目标轮真实已发布且未确认死亡的非目标");
                                TraceMixed("retain",batch);
                                continue;
                            }
                        }
                        var beforeDeath = linked.Read();
                        if (matches && mixedOrder!=0)
                        {
                            TraceMixed("target-before-wait",batch);
                            // 保留原目标存活，沿原计时器发布同轮后续批次；不构造额外怪物或跳轮。
                            while (!current.State.Plan.Actors.Any(row=>!targetTemplates.Contains(row.Phase.MonsterId) &&
                                current.State.WasPublished(row.ObjectKey) && linked.Session.DungeonSnapshot!.LivingMonsters.Contains(row.ObjectKey)) &&
                                current.Due is {} due)
                            {
                                clock.AdvanceTo(due);
                                current=linked.Session.TickDungeonAltar(current) ?? throw new InvalidDataException("混合255原后续批次未发布");
                            }
                        }
                        if (matches && mixedOrder!=0) TraceMixed("target-after-wait",batch);
                        // 回滚/GM重试由原两入口完整覆盖；混合255必须从无pending的两只存活对象开始。
                        if (matches && mixedOrder==0)
                        {
                            var selected = linked.Session.Selected;
                            var snapshot = linked.Session.DungeonSnapshot;
                            long receiptsBefore = Convert.ToInt64(linked.Query("SELECT count(*) FROM character_dungeon_growth_receipts"));
                            var missionBefore = linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!;
                            linked.Sql(questId==7340
                                ? "CREATE TRIGGER fail_altar_hunt BEFORE INSERT ON account_battle_pass_source_events WHEN NEW.source_kind='altar_quest_target_monster_death' BEGIN SELECT RAISE(ABORT,'altar battle pass rollback'); END"
                                : "CREATE TRIGGER fail_altar_hunt BEFORE UPDATE ON character_active_main_quests BEGIN SELECT RAISE(ABORT,'altar hunt rollback'); END");
                            Reject<Microsoft.Data.Sqlite.SqliteException>(() =>
                                linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey), growth),
                                "祭坛目标任务写入失败保留原死亡候选");
                            That(linked.Read().Revision == beforeDeath.Revision &&
                                ReferenceEquals(linked.Session.Selected, selected) && linked.Session.DungeonSnapshot!.Identity == snapshot!.Identity &&
                                linked.Session.DungeonSnapshot.LivingMonsters.SequenceEqual(snapshot.LivingMonsters) &&
                                linked.Read().ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState == 1,
                                "祭坛目标SQL失败不提交revision、条件或内存死亡");
                            var missionAfterFailure = linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!;
                            That(missionAfterFailure.Season.Revision==missionBefore.Season.Revision &&
                                missionAfterFailure.Season.TotalExperience==missionBefore.Season.TotalExperience &&
                                missionAfterFailure.Season.PassType==missionBefore.Season.PassType &&
                                missionAfterFailure.Season.ToClaimBytes().SequenceEqual(missionBefore.Season.ToClaimBytes()) &&
                                missionAfterFailure.Slots.Select(row=>(row.Instance.Key,row.Count,row.Completed)).SequenceEqual(
                                    missionBefore.Slots.Select(row=>(row.Instance.Key,row.Count,row.Completed))) &&
                                Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_quest_target_monster_death'"))==0 &&
                                Convert.ToInt64(linked.Query("SELECT count(*) FROM character_dungeon_growth_receipts"))==receiptsBefore,
                                "原任务早期或战令晚季版本失败均回滚原死亡回执、来源、任务进度与季版本");
                            linked.Sql("DROP TRIGGER fail_altar_hunt");
                            if (replaceSelectedOnRetry)
                            {
                                var original = linked.Session.Selected!;
                                long epoch = linked.Session.SelectionEpoch;
                                var updated = linked.Session.SetExpertJob(original.Name,2,17);
                                That(!ReferenceEquals(updated,original) && ReferenceEquals(linked.Session.Selected,updated) &&
                                    updated.Id==original.Id && updated.Revision==original.Revision+1 &&
                                    linked.Session.SelectionEpoch==epoch && updated.ActiveMainQuests.SequenceEqual(original.ActiveMainQuests) &&
                                    linked.Session.DungeonSnapshot!.Identity==snapshot!.Identity &&
                                    linked.Session.DungeonSnapshot.LivingMonsters.SequenceEqual(snapshot.LivingMonsters) &&
                                    linked.Read().ExpertJobType==2 && linked.Read().ExpertJobExperience==17 &&
                                    linked.Read().Revision==updated.Revision,
                                    "真实GM副职业提交替换同角色快照，原选角、接取和未确认目标死亡保持");
                            }
                        }
                        long retryRevision = linked.Session.Selected!.Revision;
                        long countBefore = linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!.Slots
                            .Single(row=>row.Instance.Key.Category==0 && row.Instance.Key.Id==1).Count;
                        int batchDeaths=1;
                        try
                        {
                            if (matches && mixedOrder!=0)
                            {
                                var other=current.State.Plan.Actors.FirstOrDefault(row=>!targetTemplates.Contains(row.Phase.MonsterId) &&
                                    current.State.WasPublished(row.ObjectKey) && linked.Session.DungeonSnapshot!.LivingMonsters.Contains(row.ObjectKey));
                                That(other is not null && actor.Phase.Round==other.Phase.Round &&
                                    actor.Phase.Difficulty==other.Phase.Difficulty &&
                                    current.State.WasPublished(actor.ObjectKey) &&
                                    linked.Session.DungeonSnapshot!.LivingMonsters.Contains(actor.ObjectKey),
                                    "原真实同轮已发布目标与非目标均存活，才能验证255混合顺序");
                                if (other is null) throw new InvalidDataException("目标批缺少真实非目标对象");
                                long sourcesBefore=Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events"));
                                current=linked.Session.ObserveDungeonAltarDeaths(mixedOrder==1
                                    ? [actor.ObjectKey,other.ObjectKey] : [other.ObjectKey,actor.ObjectKey],new string('8',64),growth)!;
                                That(Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events"))==sourcesBefore+2 &&
                                    !linked.Session.DungeonSnapshot!.LivingMonsters.Contains(actor.ObjectKey) &&
                                    !linked.Session.DungeonSnapshot.LivingMonsters.Contains(other.ObjectKey),
                                    "单255两种真实目标/非目标顺序各新增两个来源并确认两个原对象");
                                batchDeaths=2;
                            }
                            else current=linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey),growth)!;
                        }
                        catch (InvalidDataException error) when (replaceSelectedOnRetry && matches &&
                            error.Message=="祭坛战令来源不是原角色、接取目录与目标死亡的冻结事实")
                        {
                            Console.WriteLine("ALTAR_PENDING_RETRY " + error);
                            That(false,"祭坛失败pending在同角色原接取保留且GM副职业替换快照后可重试");
                            throw;
                        }
                        if (replaceSelectedOnRetry && matches && current is null)
                            throw new InvalidDataException("祭坛失败pending在同角色原接取保留且GM副职业替换快照后可重试");
                        if (current is null) throw new InvalidDataException("原祭坛死亡没有提交回执");
                        confirmedDeaths+=batchDeaths;
                        var afterDeath = linked.Read();
                        if (replaceSelectedOnRetry && matches)
                            That(afterDeath.ExpertJobType==2 && afterDeath.ExpertJobExperience==17 &&
                                linked.Session.Selected!.ExpertJobType==2 && linked.Session.Selected.ExpertJobExperience==17 &&
                                afterDeath.Revision==retryRevision+1,
                                "死亡重试基于GM后的当前revision提交且保留新副职业，不回写旧角色快照");
                        That(afterDeath.ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState ==
                            (matches ? mixedOrder==0 ? 0u : 512u : initialCondition),
                            "祭坛只按真实目标击杀持久推进猎杀条件，不把通关或任意小怪当目标：" + questId);
                        That(afterDeath.Learning.Experience == beforeDeath.Learning.Experience &&
                            afterDeath.Level == beforeDeath.Level && afterDeath.Skills.SequenceEqual(beforeDeath.Skills) &&
                            afterDeath.Learning.Trees.Zip(beforeDeath.Learning.Trees).All(pair =>
                                pair.First.Context == pair.Second.Context && pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp &&
                                pair.First.Skills.SequenceEqual(pair.Second.Skills) && pair.First.SameCombos(pair.Second)) &&
                            afterDeath.DungeonState == beforeDeath.DungeonState && afterDeath.Inventory.Gold == beforeDeath.Inventory.Gold &&
                            afterDeath.Inventory.Items.SequenceEqual(beforeDeath.Inventory.Items),
                            "祭坛任务死亡不授普通经验、技能点、熟练度、金币或掉落");
                        var mission = linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!;
                        long kills = mission.Slots.Single(row=>row.Instance.Key.Category==0 && row.Instance.Key.Id==1).Count;
                        long sources = Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_quest_target_monster_death'"));
                        if (!matches)
                        {
                            That(kills==Math.Min(10,confirmedDeaths) && sources==0 &&
                                Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_non_target_monster_death'"))==confirmedDeaths,
                                "每个已发布非任务目标祭坛死亡独立计基础击杀，仍不推进原任务或伪造目标来源");
                            continue;
                        }
                        Console.WriteLine($"ALTAR_TARGET_SOURCE quest={questId} monster={target} count={kills} sourceRows={sources} totalDeaths={confirmedDeaths} expected={Math.Min(10,confirmedDeaths)}");
                        That(kills==Math.Min(10,confirmedDeaths) && kills==Math.Min(10,countBefore+batchDeaths) && sources==1 && mission.Season.TotalExperience==0 &&
                            Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_non_target_monster_death'"))==confirmedDeaths-1,
                            "祭坛实际目标零经验死亡与原任务同事务计一次战令击杀，不自动发任务XP");
                        targetKilled = true;
                        if (mixedOrder!=0)
                        {
                            PublishProgressAsync(linked,current,clock).GetAwaiter().GetResult();
                            That(linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey),growth) is not null &&
                                linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!.Season.Revision==mission.Season.Revision,
                                "目标先或非目标先的单255混合批次原来源不重复，任务条件只推进一次");
                            break;
                        }
                        That(current.QuestCharacter is not null && linked.Session.CanPublishDungeonAltar(current),
                            "同一原对象SQL重试成功后提供已提交任务投影并可确认发送");
                        // 先沿真实Handler发送任务及累计BP页并双标记，再验证255没有待补投影。
                        PublishProgressAsync(linked,current,clock).GetAwaiter().GetResult();
                        long revision = afterDeath.Revision;
                        long missionRevision = mission.Season.Revision;
                        That(linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey), growth) is not null &&
                            linked.Session.ObserveDungeonAltarDeaths([actor.ObjectKey], new string('B',64), growth) is null &&
                            linked.Read().Revision == revision &&
                            linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!.Season.Revision==missionRevision &&
                            Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_quest_target_monster_death'"))==1,
                            "任务目标39与255混合重报不重复计数或递增revision");
                        break;
                    }
                }
                if (!targetKilled)
                {
                    var next = entered.Entry.Maze.StaticTopology.Links!.SelectMany(link =>
                        new[] { (From: link.First, To: link.Second), (From: link.Second, To: link.First) })
                        .Single(edge => edge.From == loaded.Snapshot.CurrentMap.Position && edge.To.X > edge.From.X).To;
                    That(linked.Session.MoveFormalDungeon(checked((byte)next.X), checked((byte)next.Y)) is not null,
                        "未出现目标前按真实祭坛连接推进下一房间");
                }
            }
            if (mixedOrder!=0)
            {
                // 首混合批次只完成56445通道；同轮另一真实目标56446仍须独立确认后才可领奖。
                var scene=linked.Session.CurrentFormalDungeonScene()!;
                var remaining=scene.Monsters.Monsters.Single(row=>row.MonsterId==56446 &&
                    row.AltarActor is { Phase.Round:5,Phase.Difficulty:1 });
                That(scene.Monsters.Altar is not null && linked.Session.DungeonSnapshot!.LivingMonsters.Contains(remaining.ObjectKey) &&
                    linked.Read().ActiveMainQuests.Single(row=>row.QuestId==questId).ConditionState==512,
                    "混合255后另一双通道目标真实已发布且存活，不把半完成任务当成完成");
                long sourcesBefore=Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events"));
                var completed=linked.Session.ObserveDungeonAltarDeath(Observation(remaining.ObjectKey),growth)
                    ?? throw new InvalidDataException("7344第二个真实已发布任务目标未提交");
                confirmedDeaths++;
                That(linked.Read().ActiveMainQuests.Single(row=>row.QuestId==questId).ConditionState==0 &&
                    Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events"))==sourcesBefore+1 &&
                    Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_quest_target_monster_death'"))==2 &&
                    Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_non_target_monster_death'"))==confirmedDeaths-2,
                    "7344两真实任务目标分别计原目标来源，混合非目标保独立来源，双通道最终归零");
                PublishProgressAsync(linked,completed,clock).GetAwaiter().GetResult();
            }
            That(linked.Session.ReturnFromFormalDungeon() is not null &&
                linked.Repository.LoadCharacter(linked.Owner,0).ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState == 0,
                "已提交的祭坛目标进度退出并重读后保留，不依赖最终奖励结算");
            That(linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!.Slots
                .Single(row=>row.Instance.Key.Category==0 && row.Instance.Key.Id==1).Count==Math.Min(10,confirmedDeaths),
                "回城重读仍保留战令任务目标来源进度");
            var reopened = new DfoServer.Persistence.CharacterRepository(
                (string)linked.Query("SELECT file FROM pragma_database_list WHERE name='main'")!,resources.Quests);
            reopened.ConfigureBattlePassMissions(missionPolicy);
            var restoredMission = reopened.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!;
            That(restoredMission.Slots.Single(row=>row.Instance.Key.Category==0 && row.Instance.Key.Id==1).Count==Math.Min(10,confirmedDeaths) &&
                restoredMission.Season.TotalExperience==0 &&
                reopened.LoadCharacter(linked.Owner,0).ActiveMainQuests.Single(row=>row.QuestId==questId).ConditionState==0,
                "新Repository按原owner同时恢复战令进度与原目标完成，未发任务经验");
            var finished = linked.Session.FinishQuest(questId, ushort.MaxValue, resources.Quests,
                SelfTestCatalogs.QuestRewardParameters(archive), items.Items, items.GoldLimits, items.EpicPieces, growth);
            That(finished.Receipt is not null && linked.Read().CompletedQuests.Contains(questId) &&
                linked.Read().ActiveMainQuests.All(row => row.QuestId != questId),
                "祭坛任务沿原领奖事务完成并移出活动列表：" + questId);
        }
        Console.WriteLine($"PASS {Count-before} BattlePass altar original quest target source checks");

        QuestSceneTests.Fixture New(Clock clock, int[]? completed = null)
        {
            var fixture = new QuestSceneTests.Fixture(resources.Towns,resources.Characters,resources.Learning,growth,
                resources.Quests,false,70,completed ?? done,position:position,timeProvider:clock);
            fixture.Repository.ConfigureBattlePassMissions(missionPolicy);
            That(fixture.Repository.ReadBattlePassMissions(fixture.Owner,clock.GetUtcNow())!.Slots
                .Single(row=>row.Instance.Key.Category==0 && row.Instance.Key.Id==1).Count==0,"新隔离账号任务起点为零");
            That(fixture.Session.MutateInventory(c => CurrencyService.SetGold(c,500000)).Accepted,"准备隔离角色祭坛费用");
            return fixture;
        }
        NativeDungeonDeathObservation Observation(ushort key) => new(key, 1, 0x111, 1, 600, 1, 0, 70, 0, 650, 1,
            true, new string('A',64));
    }
    static async Task PublishProgressAsync(QuestSceneTests.Fixture linked,DungeonAltarReceipt receipt,TimeProvider clock)
    {
        using var stop=new CancellationTokenSource();
        using var output=new MemoryStream();
        var sent=new List<(ushort Opcode,byte[] Body)>();
        await using var sender=new DfoServer.Network.GameConnectionSender(linked.Owner,output,stop,0,
            (_,opcode,_,body,_)=>sent.Add((opcode,body.ToArray())));
        var context=new DfoServer.Network.Protocol.GameConnectionContext(new(1,System.Net.IPAddress.Loopback,output,stop),
            linked.Owner,linked.Session,sender);
        byte[] expected=linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!.ToPayload();
        await DfoServer.Network.Handlers.DungeonHandler.PublishAltarProgressAsync(context,receipt);
        That(sent.Count==2 && sent[0].Opcode==(ushort)DfoServer.Network.NotiPacketType.ACCEPTED_QUEST &&
            sent[1].Opcode==(ushort)DfoServer.Network.NotiPacketType.BATTLE_PASS_INFO_MISSION && sent[1].Body.SequenceEqual(expected),
            "目标死亡及单255两种混合顺序都沿原任务通知后发布一次累计1801");
        That(!linked.Session.MarkDungeonAltarQuestsPublished(receipt) && !linked.Session.MarkDungeonAltarBattlePassPublished(receipt),
            "原任务与独立BP投影均实际发送后标记，重复不补旧页");
    }
    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        long elapsed;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => elapsed;
        public override DateTimeOffset GetUtcNow() => now.AddMilliseconds(elapsed);
        public void AdvanceTo(long value) => elapsed = Math.Max(elapsed,value);
    }
}
