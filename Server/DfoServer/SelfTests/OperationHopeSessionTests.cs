using System.Text.Json;
using System.Reflection;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class OperationHopeSessionTests
{
    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int before = Count;
        var resources = new Resources(towns,pvf,characters,items);
        VerifyFatigueAdmission(resources,pvf);
        VerifyHopeRewards(pvf,items,resources);
        using (var f = new Fixture(resources))
        {
            That(f.Session.EnterFormalDungeon(8523,0,resources.Hope.Difficulty,resources.Dungeons,resources.Rooms) is null,"没有1598固定选择不能直接普通入场希望");
            int draws = 0;
            int Draw(int count) { draws++; return count == 9 ? 6 : 0; }
            That(f.Session.BeginOperationHope(resources.Hope,resources.Dungeons,Draw),"1598按真实资源冻结合法任务和迷宫");
            int frozenDraws = draws;
            That(f.Session.BeginOperationHope(resources.Hope,resources.Dungeons,Draw) && draws == frozenDraws,"同次1598重试不重新抽任务或迷宫");
            long beforeMoveRevision = f.Session.Selected!.Revision;
            That(f.Session.Move(new(41,200,0,0,0,0)) && f.Session.Selected!.Revision > beforeMoveRevision,
                "1598后真实城镇移动保存新revision");
            That(f.Session.BeginOperationHope(resources.Hope,resources.Dungeons,Draw) && draws == frozenDraws,
                "移动后1598刷新来源版本但不重抽原任务或迷宫");
            var initial = f.Session.Selected!;
            var initialCount = f.Usage();
            f.Sql("CREATE TRIGGER hope_session_entry_fail AFTER INSERT ON character_active_main_quests WHEN NEW.quest_id BETWEEN 6171 AND 6179 BEGIN SELECT RAISE(ABORT,'entry fail'); END");
            Reject<SqliteException>(() => f.Enter(),"真实Session入场末段失败回滚");
            That(f.Session.Selected == initial && f.Read().DungeonState == initial.DungeonState && f.Read().Fatigue == initial.Fatigue,
                "失败不安装Run或半份疲劳、尝试号、任务");
            That(f.Usage() == initialCount, "真实次数表随希望任务末段失败回滚，不扣日周次数");
            f.Sql("DROP TRIGGER hope_session_entry_fail");
            That(f.Session.BeginOperationHope(resources.Hope,resources.Dungeons,Draw) && draws == frozenDraws,"SQL失败重试仍使用原固定抽取");
            var entry = f.Enter();
            That(f.Usage() == new DungeonIncountUsage(initialCount.DailyUsed+1,initialCount.WeeklyUsed+1),
                "失败后重试恰扣一次真实日周次数");
            That(f.Session.EnterFormalDungeon(8523,0,resources.Hope.Difficulty,resources.Dungeons,resources.Rooms) is null &&
                f.Usage() == new DungeonIncountUsage(initialCount.DailyUsed+1,initialCount.WeeklyUsed+1),
                "已安装实例重复入场不再次扣次");
            var active = f.Session.Selected!.ActiveMainQuests.Single(row => resources.Hope.Missions.ContainsKey(row.QuestId));
            That(active.QuestId == 6177 && active.ConditionState == 1 && entry.EntryFatigueCharged == 4 &&
                initial.Fatigue!.Remaining-f.Session.Selected.Fatigue!.Remaining == 4,"入场同事务固定6177原接取并扣首区域4疲劳");
            That(f.Finish(active.QuestId) is null,"未真实目标死亡及通关不能提前领奖");
            var path = Route(resources.Hope,entry.Entry.Maze,entry.Snapshot.CurrentMap.Position,550059);
            That(path.Length > 1 && path.Select(map => resources.Hope.Maps[map.Id].AreaId).Distinct().Count() == 4,"按真实区域路由经过四个区域到终点");
            var visitedAreas = new HashSet<int>();
            bool targetObserved = false;
            for (int at = 0; at < path.Length; at++)
            {
                if (at > 0)
                {
                    var target = path[at].Position;
                    That(f.Session.MoveFormalDungeon(checked((byte)target.X),checked((byte)target.Y)) is not null,"原Session按声明邻接或warp前进");
                }
                var loaded = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("希望正式加载被拒绝");
                visitedAreas.Add(resources.Hope.Maps[loaded.Snapshot.CurrentMap.Id].AreaId);
                That(initial.Fatigue!.Remaining-f.Session.Selected!.Fatigue!.Remaining == visitedAreas.Count*4,
                    "同区过房不重复扣费，每新区域恰扣4");
                long loadedRevision = f.Session.Selected.Revision;
                That(f.Session.CompleteFormalDungeonLoading() is null && f.Session.Selected.Revision == loadedRevision,"重复加载不再次扣区域疲劳");
                var progress = f.Session.ObserveOperationHopeTrigger(active.QuestId, new(0x10, 0, 0));
                That(progress is { Outcome: Game.Quests.QuestLifecycleOutcome.Unchanged } &&
                    progress.Character.ActiveMainQuests.Single(row => row.AcceptanceId == active.AcceptanceId).ConditionState == (targetObserved ? 0u : 1u) &&
                    f.Session.Selected.Revision == loadedRevision && f.Session.CanPublishQuestLifecycle(progress),
                    "希望33只回读本实例权威进度，未击杀不归零，重复查询不写revision");
                That(f.Session.ObserveOperationHopeTrigger(active.QuestId, new(0x20, 0, 0)) is null &&
                    f.Session.ObserveOperationHopeTrigger(active.QuestId, new(0x10, 1, 0)) is null &&
                    f.Session.ObserveOperationHopeTrigger((ushort)(active.QuestId == 6171 ? 6172 : 6171), new(0x10, 0, 0)) is null,
                    "其它任务、通道和客户端自报进度不能借希望入口推进");
                foreach (var monster in loaded.Monsters.Monsters.Where(row => row.CanReportDeath && row.IsBlocking))
                {
                    if (!f.Session.DungeonSnapshot!.LivingMonsters.Contains(monster.ObjectKey)) continue;
                    var observation = new NativeDungeonDeathObservation(monster.ObjectKey,loaded.Character.Id,0x111,loaded.Character.Id,
                        600,1,0,checked((uint)monster.CandidateLevel),504,650,1,true,new string('D',64));
                    var result = f.Session.ObserveFormalDungeonDeath(observation,resources.Kills,resources.Growth);
                    That(result.Outcome == DungeonRunOutcome.Accepted,"真实Session怪物报告经原成长SQL事务确认");
                    if (monster.MonsterId == resources.Hope.Missions[active.QuestId].MonsterId)
                    {
                        targetObserved = true;
                        That(f.Session.Selected!.ActiveMainQuests.Single(row => row.AcceptanceId == active.AcceptanceId).ConditionState == 0,
                            "目标真实死亡提交后同次接取才归零");
                    }
                }
                if (at > 0 && f.Session.Phase != SessionPhase.Settlement)
                {
                    var prior = path[at-1].Position;
                    That(f.Session.MoveFormalDungeon(checked((byte)prior.X),checked((byte)prior.Y)) is null,"不能退回已通过sector或区域");
                }
            }
            That(targetObserved && f.Session.Phase == SessionPhase.Settlement &&
                initial.Fatigue!.Remaining-f.Session.Selected!.Fatigue!.Remaining == 16,"真实目标加任一声明终点完成，全程只扣16疲劳");
            var beforeReward = f.Session.Selected!;
            f.Sql("CREATE TRIGGER hope_session_reward_fail AFTER INSERT ON character_operation_hope_reward_receipts BEGIN SELECT RAISE(ABORT,'reward fail'); END");
            Reject<SqliteException>(() => f.Finish(active.QuestId),"真实Session领奖末段失败保留原计划");
            That(f.Session.Selected == beforeReward && f.Session.ReturnFromFormalDungeon() is null,"未明确完成的领奖准备阻止退出丢失恢复资格");
            f.Sql("DROP TRIGGER hope_session_reward_fail");
            var claimed = f.Finish(active.QuestId) ?? throw new InvalidOperationException("希望Session合法领奖未成功");
            That(claimed.Commit.Rewards.Sum(row => row.GrantedCount) == 60 && claimed.Commit.ExperienceReward.Amount == 0 &&
                claimed.Commit.Character.Learning.Experience == beforeReward.Learning.Experience && f.Session.CanPublishOperationHope(claimed),
                "Session发布真实60材料，原生任务禁经验不影响此前杀怪经验");
            var repeated = f.Finish(active.QuestId);
            That(repeated is { Commit.Duplicate: true } && repeated.Commit.Rewards.SequenceEqual(claimed.Commit.Rewards),"Session重复领奖精确重放不多给材料");
            That(f.Session.ReturnFromFormalDungeon() is not null && f.Session.Phase == SessionPhase.Town &&
                !f.Session.CanPublishOperationHope(claimed),"成功领奖可安全退出，旧领奖回执不再发布");
            That(f.Session.BeginOperationHope(resources.Hope,resources.Dungeons,_ => 0),"新入场允许重新抽取任务");
            f.Enter();
            That(f.Session.Selected!.ActiveMainQuests.Any(row => resources.Hope.Missions.ContainsKey(row.QuestId)),"新实例有独立原接取");
            // 新选角会话读取真实库，模拟断线遗留；不写SQL伪造完成或清理资格。
            var relog = new GameSession(f.Repository,resources.Towns,f.Owner,questCatalog:resources.Quests,operationHope:resources.Hope,incounts:resources.Incounts);
            relog.Select(f.Created.Slot); relog.MarkReady();
            That(relog.Selected!.ActiveMainQuests.All(row => !resources.Hope.Missions.ContainsKey(row.QuestId)) &&
                relog.Selected.Inventory.Items.Where(row => row.Core.ItemId == 10307438).Sum(row => row.Core.Count) == 60,
                "重选清理真实入场遗留任务，不重发旧实例奖励");
        }
        OperationHopePartyTests.Run(pvf,items);
        TimerBusy(resources,pvf).GetAwaiter().GetResult();
        HopeTimerBattlePass(resources,pvf).GetAwaiter().GetResult();
        Console.WriteLine($"PASS {Count-before} 代号希望Session入场与领奖链断言");
    }

    static void VerifyFatigueAdmission(Resources resources, PvfArchive pvf)
    {
        var start = DateTimeOffset.UtcNow;
        for (ushort remaining = 0; remaining < 4; remaining++)
        {
            // 各疲劳边界独立记次，避免前例恢复后的入场耗尽同周额度而遮蔽本例门槛。
            var clock = new HopeClock(start);
            using (var f = new Fixture(resources,clock:clock))
            {
                f.Session.ReturnToRoster();
                f.Sql($"UPDATE character_fatigue SET used=maximum-{remaining},remaining={remaining}");
                f.Session.Select(f.Created.Slot,resumeTown:true);
                f.Session.MarkReady();
                var before = f.Read();
                var counts = f.Usage();
                int draws = 0;
                int Draw(int bound) { draws++; return 0; }
                That(f.Session.BeginOperationHope(resources.Hope,resources.Dungeons,Draw),
                    "希望低疲劳1598仅冻结选择，不在静默分支截断");
                int frozenDraws = draws;
                That(f.Session.BeginDungeonSelection() is not null,"低疲劳仍经原15建立选图返回上下文");
                var selection = f.Session.CurrentDungeonSelection;
                var rejected = f.Session.EnterFormalDungeon(8523,0,resources.Hope.Difficulty,resources.Dungeons,resources.Rooms);
                That(rejected is null,$"希望{remaining}疲劳正式16不得安装实例");
                That(f.Session.LastFormalDungeonRejection == "start-fatigue-insufficient:4",
                    $"希望{remaining}疲劳正式16应报告疲劳门槛，实际：{f.Session.LastFormalDungeonRejection}");
                That(f.Session.Phase == SessionPhase.MapSelect,$"希望{remaining}疲劳拒绝后保持MapSelect，实际：{f.Session.Phase}");
                That(ReferenceEquals(f.Session.CurrentDungeonSelection,selection),$"希望{remaining}疲劳拒绝后保留原选择返回上下文");
                That(f.Session.DungeonSnapshot is null,$"希望{remaining}疲劳拒绝后不安装副本快照");
                That(JsonSerializer.Serialize(f.Read()) == JsonSerializer.Serialize(before),
                    $"希望{remaining}疲劳拒绝后完整角色、任务、尝试号与资产保持不变");
                That(f.Usage() == counts,$"希望{remaining}疲劳拒绝后日周次数保持不变，原：{counts}，实际：{f.Usage()}");
                That(f.Session.ReturnFromDungeonSelection() is not null && f.Session.Phase == SessionPhase.Town,
                    "低疲劳拒绝后可沿原取消选图返回城镇");
                That(f.Session.BeginOperationHope(resources.Hope,resources.Dungeons,Draw) && draws == frozenDraws,
                    "取消后重选复用本次任务与迷宫，不重新抽签");
                clock.Now = clock.Now.AddDays(1);
                That(f.Session.AdvanceFatigueDay(clock.Now) is not null,
                    "通过原每日恢复用例更新疲劳与角色版本");
                That(f.Session.BeginOperationHope(resources.Hope,resources.Dungeons,Draw) && draws == frozenDraws,
                    "恢复疲劳后1598只刷新角色版本，不重抽任务");
                var restored = f.Session.Selected!;
                var restoredCounts = f.Usage();
                var entered = f.Enter();
                That(entered.EntryFatigueCharged == 4 && restored.Fatigue!.Remaining-f.Read().Fatigue!.Remaining == 4 &&
                    f.Usage() == new DungeonIncountUsage(restoredCounts.DailyUsed+1,restoredCounts.WeeklyUsed+1),
                    "拒绝恢复后仅提交一次首区4疲劳与日周次数");
                That(f.Session.ReturnFromFormalDungeon() is not null,"恢复后入场实例可正常退场并清理精确任务");
            }
        }
        // 仅构造独立资源夹具；准入门槛可大于收费，不能靠当前资源的4==4掩盖门槛遗漏。
        var definition = resources.Dungeons.Get(8523);
        var source = OperationHopeFile.Parse(pvf.ReadTokens(OperationHopeCatalog.ResourcePath),pvf.ResolveString) with { NeedFatigue = 8 };
        var threshold = (OperationHopeCatalog)typeof(OperationHopeCatalog).GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single().Invoke(
            [source,OperationHopeDungeonFile.Parse(pvf.ReadTokens(definition.Path),pvf.ResolveString),definition,resources.Quests,resources.Rooms]);
        using var separate = new Fixture(resources,hope:threshold,fatigueRemaining:4);
        var original = separate.Read();
        That(original.Fatigue is { Remaining: 4 } && separate.Session.Selected!.Fatigue is { Remaining: 4 },
            $"独立门槛夹具库内与选中余额均为4，原：{original.Fatigue}，选中：{separate.Session.Selected!.Fatigue}");
        That(threshold.NeedFatigue == 8,$"独立准入门槛为8，实际：{threshold.NeedFatigue}");
        That(threshold.FatigueValue == 4,$"独立首区收费为4，实际：{threshold.FatigueValue}");
        That(separate.Session.BeginOperationHope(threshold,resources.Dungeons,_=>0),"独立门槛夹具1598冻结选择");
        That(separate.Session.BeginDungeonSelection() is not null,"独立门槛夹具15建立选图上下文");
        That(separate.Session.EnterFormalDungeon(8523,0,threshold.Difficulty,resources.Dungeons,resources.Rooms) is null,
            "可付4点费用但不足8点门槛不得安装实例");
        That(separate.Session.LastFormalDungeonRejection == "start-fatigue-insufficient:8",
            $"独立门槛应由正式入场报告8点门槛，实际：{separate.Session.LastFormalDungeonRejection}");
        var unchanged = separate.Read();
        That(unchanged.Revision == original.Revision,$"独立门槛拒绝不改角色版本，原：{original.Revision}，实际：{unchanged.Revision}");
        That(unchanged.Fatigue == original.Fatigue,$"独立门槛拒绝不改疲劳，原：{original.Fatigue}，实际：{unchanged.Fatigue}");
        // 原选角可合法接取自动每日任务；拒绝必须保留其完整接取身份，不要求活动槽为空。
        That(unchanged.ActiveMainQuests.SequenceEqual(original.ActiveMainQuests),
            $"独立门槛拒绝保留原活动任务及接取身份，原：{string.Join(',',original.ActiveMainQuests.Select(row=>row.QuestId))}，实际：{string.Join(',',unchanged.ActiveMainQuests.Select(row=>row.QuestId))}");
        That(!unchanged.ActiveMainQuests.Any(row=>threshold.Missions.ContainsKey(row.QuestId)),"独立门槛拒绝不安装希望任务");
        That(JsonSerializer.Serialize(unchanged) == JsonSerializer.Serialize(original),"独立门槛拒绝后完整角色与资产保持不变");
    }

    static async Task TimerBusy(Resources resources, PvfArchive pvf)
    {
        await TimerBusyCase(resources,pvf,invalidate:false);
        await TimerBusyCase(resources,pvf,invalidate:true);
    }

    static async Task HopeTimerBattlePass(Resources resources, PvfArchive pvf)
    {
        await HopeTimerBattlePassCase(resources,pvf,publicationBusy:false,transactionFailure:false);
        await HopeTimerBattlePassCase(resources,pvf,publicationBusy:false,transactionFailure:true);
        await HopeTimerBattlePassCase(resources,pvf,publicationBusy:true,transactionFailure:false);
    }

    static async Task HopeTimerBattlePassCase(Resources resources, PvfArchive pvf, bool publicationBusy, bool transactionFailure)
    {
        var start=new DateTimeOffset(2026,10,29,6,0,0,TimeSpan.FromHours(8));
        var policy=new BattlePassMissionPolicy("hope-timer-bp",start,BattlePassCatalog.Load(pvf));
        int day=Enumerable.Range(0,policy.Days).First(i=>policy.Active(start.AddDays(i).AddHours(1)).Any(row=>row.Key.Id==49));
        var clock=new HopeClock(start.AddDays(day).AddHours(1));
        using var f=new Fixture(resources,preserveDirectory:true,policy:policy,clock:clock);
        That(f.Session.BeginOperationHope(resources.Hope,resources.Dungeons,count=>count==9?6:0),"BP fixture freezes actual hope mission");
        var entry=f.Enter();
        var active=f.Session.Selected!.ActiveMainQuests.Single(row=>resources.Hope.Missions.ContainsKey(row.QuestId));
        var path=Route(resources.Hope,entry.Entry.Maze,entry.Snapshot.CurrentMap.Position,550059);
        for (int at=0;at<path.Length;at++)
        {
            if (at>0) That(f.Session.MoveFormalDungeon(checked((byte)path[at].Position.X),checked((byte)path[at].Position.Y)) is not null,"BP fixture advances actual route");
            var loaded=f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("BP fixture loading rejected");
            foreach (var monster in loaded.Monsters.Monsters.Where(row=>row.CanReportDeath && row.IsBlocking))
            {
                if (!f.Session.DungeonSnapshot!.LivingMonsters.Contains(monster.ObjectKey)) continue;
                var observation=new NativeDungeonDeathObservation(monster.ObjectKey,loaded.Character.Id,0x111,loaded.Character.Id,
                    600,1,0,checked((uint)monster.CandidateLevel),504,650,1,true,new string('D',64));
                That(f.Session.ObserveFormalDungeonDeath(observation,resources.Kills,resources.Growth).Outcome==DungeonRunOutcome.Accepted,"BP fixture commits actual deaths");
            }
        }
        var cards=new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf),resources.Items.DungeonDropItems,
            resources.Items.Items,resources.Items.GoldLimits,resources.Items.EpicPieces,prices:SelfTestCatalogs.CardPrices(pvf),
            hopeRewards:new(new OperationHopeRewardCatalog(pvf,resources.Items.Items),resources.Items.Items));
        var enabled=f.Session.EnableFormalDungeonSettlement() ?? throw new InvalidOperationException("BP fixture cannot enable clear");
        f.Session.ConfirmFormalDungeonSettlementEnabled(enabled.Receipt);
        var settled=f.Session.SettleFormalDungeon(f.Created.Id,99,99,DungeonSettlementProtocolTests.Report(f.Created.Id,99),
            resources.Dungeons,SelfTestCatalogs.ClearExperience(pvf),resources.Growth,cards);
        That(settled is { Outcome:DungeonRunOutcome.Accepted, Card:not null, Receipt:not null } &&
            f.Session.AdvanceFormalDungeonCards(false) is not null && f.Session.AdvanceFormalDungeonCards(true) is not null,
            "BP fixture settles actual hope and opens cards");
        var before=f.Repository.ReadBattlePassMissions(f.Owner,clock.GetUtcNow()) ?? throw new InvalidOperationException("BP fixture state missing");
        That(before.Active && before.Slots.Single(row=>row.Instance.Key.Id==49).Count==0 &&
            f.Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='operation_hope_completion'")==0,
            "BP task 49 and hope source have no seeded progress");
        if (transactionFailure)
            f.Sql("CREATE TRIGGER hope_bp_fail AFTER INSERT ON account_battle_pass_mission_progress WHEN NEW.mission_id=49 BEGIN SELECT RAISE(ABORT,'hope BP transaction fail'); END");
        if (publicationBusy)
        {
            SqliteDatabaseBootstrap.ReleaseRuntimePool();
            using var setup=new SqliteConnection("Data Source="+f.Path+";Pooling=False"); setup.Open();
            using var command=setup.CreateCommand(); command.CommandText="PRAGMA journal_mode=DELETE";
            That(string.Equals(Convert.ToString(command.ExecuteScalar()),"delete",StringComparison.OrdinalIgnoreCase),"isolated BP publication uses DELETE journal for real exclusive read lock");
        }
        var stats=new CharacterStatComputer(resources.Characters);
        var town=new TownProjection(resources.Towns,stats,null,resources.Items.EpicPieces);
        var inventory=new InventoryProjection(stats,town,resources.Items.EpicPieces);
        using var log=new ServerLog(System.IO.Path.GetDirectoryName(f.Path)!);
        using var stop=new CancellationTokenSource();
        using var stream=new MemoryStream();
        var actions=new List<string>(); byte[]? bpPayload=null; SqliteConnection? locker=null;
        long cardUid=0; short cardSlot=-1; int cardCount=0;
        int wrappedBusy=0, rawBusy=0, historyPublished=0;
        var observedPublicationBusy=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Classify(Exception error)
        {
            if (error is IOException { InnerException: SqliteException sqlite } && SqliteBusyRetry.IsBusy(sqlite)) Interlocked.Increment(ref wrappedBusy);
            bool busy=SqliteBusyRetry.IsBusy(error);
            if (busy) Interlocked.Increment(ref rawBusy);
            if (publicationBusy && Volatile.Read(ref historyPublished)==1 &&
                (busy || error is IOException { InnerException: SqliteException inner } && SqliteBusyRetry.IsBusy(inner)))
                observedPublicationBusy.TrySetResult();
            return busy;
        }
        await using var sender=new GameConnectionSender(f.Owner,stream,stop,0,(_,_,_,body,action)=>
        {
            actions.Add(action);
            if (action=="battle_pass_mission_state") bpPayload=body.ToArray();
            if (action=="operation_hope_reward_completed") Interlocked.Increment(ref historyPublished);
            if (action=="formal_free_card_auto_reveal")
            {
                var material=f.Session.Selected!.Inventory.Items.Single(row=>row.Core.ItemId==10307438);
                cardUid=material.Uid; cardSlot=material.Position.Index; cardCount=material.Core.Count;
            }
            if (!publicationBusy || action!="operation_hope_reward_completed") return;
            locker=new SqliteConnection("Data Source="+f.Path+";Pooling=False"); locker.Open();
            using var command=locker.CreateCommand(); command.CommandText="BEGIN EXCLUSIVE"; command.ExecuteNonQuery();
        });
        var context=new GameConnectionContext(new(1,System.Net.IPAddress.Loopback,stream,stop),f.Owner,f.Session,sender);
        var hope=new QuestCompletionHandler(resources.Quests,resources.Parameters,resources.Items.Items,resources.Items.GoldLimits,
            resources.Items.EpicPieces,resources.Growth,log,new CharacterLevelProjection(stats));
        await using var projection=new DungeonCardProjection(context,log,inventory,hopeCompletion:hope,isBusy:Classify);
        try
        {
            await projection.StartAsync(settled.Receipt!);
            var pending=(Task)(typeof(DungeonCardProjection).GetField("pending",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(projection)
                ?? throw new InvalidOperationException("BP timer task missing"));
            if (publicationBusy)
            {
                await observedPublicationBusy.Task.WaitAsync(TimeSpan.FromSeconds(95));
                That(locker is not null && wrappedBusy+rawBusy>=1 && bpPayload is null,
                    "post-commit BP refresh hits real SQLite busy after the original hope publication");
                using (var command=locker!.CreateCommand()) { command.CommandText="ROLLBACK"; command.ExecuteNonQuery(); }
                locker.Dispose(); locker=null;
            }
            await pending.WaitAsync(TimeSpan.FromSeconds(110));
            if (transactionFailure)
            {
                var rolledBack=f.Read();
                var unchanged=f.Repository.ReadBattlePassMissions(f.Owner,clock.GetUtcNow()) ?? throw new InvalidOperationException("rolled-back BP state missing");
                That(f.Number("SELECT count(*) FROM character_operation_hope_reward_receipts")==0 &&
                    f.Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='operation_hope_completion'")==0 &&
                    unchanged.Slots.Single(row=>row.Instance.Key.Id==49).Count==0 && unchanged.Season.Revision==before.Season.Revision &&
                    rolledBack.ActiveMainQuests.Any(row=>row.AcceptanceId==active.AcceptanceId) &&
                    rolledBack.Inventory.Items.Where(row=>row.Core.ItemId==10307438).Sum(row=>row.Core.Count)==settled.Card!.Item!.Count &&
                    actions.Count(action=>action=="operation_hope_reward_auto_committed")==0,
                    "late BP task failure rolls back hope reward, source and task while preserving the committed card");
                f.Sql("DROP TRIGGER hope_bp_fail");
                That(f.Session.SelectFormalDungeonFreeCard(0,0) is { Duplicate:true } && await hope.CompleteHopeAfterCardAsync(context),
                    "original pending hope transaction succeeds once after late BP fault is removed");
            }
            var after=f.Read();
            var pass=f.Repository.ReadBattlePassMissions(f.Owner,clock.GetUtcNow()) ?? throw new InvalidOperationException("committed BP state missing");
            var finalMaterial=after.Inventory.Items.Single(row=>row.Core.ItemId==10307438);
            That(pass.Slots.Single(row=>row.Instance.Key.Id==49).Count==1 && pass.Season.Revision==before.Season.Revision+1 &&
                pass.Season.TotalExperience==before.Season.TotalExperience &&
                f.Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='operation_hope_completion'")==1 &&
                f.Number("SELECT count(*) FROM character_operation_hope_reward_receipts")==1 &&
                after.ActiveMainQuests.All(row=>row.AcceptanceId!=active.AcceptanceId) &&
                cardUid>0 && finalMaterial.Uid==cardUid && finalMaterial.Position.Index==cardSlot && cardCount==settled.Card!.Item!.Count &&
                finalMaterial.Core.Count==cardCount+60,
                "automatic hope reward and task 49 commit once without awarding task experience");
            string[] ordered=["formal_free_card_auto_reveal","operation_hope_reward_auto_committed","operation_hope_reward_inventory",
                "operation_hope_reward_active","operation_hope_reward_completed"];
            That(ordered.Select(action=>actions.IndexOf(action)).All(index=>index>=0) &&
                ordered.Select(action=>actions.IndexOf(action)).SequenceEqual(ordered.Select(action=>actions.IndexOf(action)).Order()) &&
                ordered.All(action=>actions.Count(value=>value==action)==1),"timer sends one ordered free-card and hope reward sequence");
            That(bpPayload is not null && bpPayload.SequenceEqual(pass.ToPayload()) &&
                actions.Count(action=>action=="battle_pass_mission_state")==1 && actions.IndexOf("battle_pass_mission_state")>actions.IndexOf("operation_hope_reward_completed"),
                "timer publishes authoritative BP 1801 after hope, including after a read lock");
            var duplicate=f.Session.SelectFormalDungeonFreeCard(0,0);
            That(duplicate is { Duplicate:true } && !await hope.CompleteHopeAfterCardAsync(context) &&
                f.Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='operation_hope_completion'")==1 &&
                f.Number("SELECT count(*) FROM character_operation_hope_reward_receipts")==1 &&
                f.Repository.ReadBattlePassMissions(f.Owner,clock.GetUtcNow())!.Season.Revision==pass.Season.Revision,
                "duplicate selection does not repeat committed hope reward or BP source");
        }
        finally
        {
            if (locker is not null) { using var command=locker.CreateCommand(); command.CommandText="ROLLBACK"; command.ExecuteNonQuery(); locker.Dispose(); }
            stop.Cancel();
        }
    }

    sealed class HopeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow()=>Now;
    }

    static async Task TimerBusyCase(Resources resources, PvfArchive pvf, bool invalidate)
    {
        using var f = new Fixture(resources,preserveDirectory:true);
        That(f.Session.BeginOperationHope(resources.Hope,resources.Dungeons,count => count == 9 ? 6 : 0),"busy fixture freezes actual hope mission");
        var entry = f.Enter();
        var active = f.Session.Selected!.ActiveMainQuests.Single(row => resources.Hope.Missions.ContainsKey(row.QuestId));
        var path = Route(resources.Hope,entry.Entry.Maze,entry.Snapshot.CurrentMap.Position,550059);
        for (int at=0; at<path.Length; at++)
        {
            if (at>0) That(f.Session.MoveFormalDungeon(checked((byte)path[at].Position.X),checked((byte)path[at].Position.Y)) is not null,"busy fixture advances actual path");
            var loaded=f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("busy fixture loading rejected");
            foreach (var monster in loaded.Monsters.Monsters.Where(row => row.CanReportDeath && row.IsBlocking))
            {
                if (!f.Session.DungeonSnapshot!.LivingMonsters.Contains(monster.ObjectKey)) continue;
                var observation=new NativeDungeonDeathObservation(monster.ObjectKey,loaded.Character.Id,0x111,loaded.Character.Id,
                    600,1,0,checked((uint)monster.CandidateLevel),504,650,1,true,new string('D',64));
                That(f.Session.ObserveFormalDungeonDeath(observation,resources.Kills,resources.Growth).Outcome==DungeonRunOutcome.Accepted,"busy fixture commits actual deaths");
            }
        }
        var cards=new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf),resources.Items.DungeonDropItems,
            resources.Items.Items,resources.Items.GoldLimits,resources.Items.EpicPieces,prices:SelfTestCatalogs.CardPrices(pvf),
            hopeRewards:new(new OperationHopeRewardCatalog(pvf,resources.Items.Items),resources.Items.Items));
        var enabled=f.Session.EnableFormalDungeonSettlement() ?? throw new InvalidOperationException("busy fixture cannot enable clear");
        f.Session.ConfirmFormalDungeonSettlementEnabled(enabled.Receipt);
        var settled=f.Session.SettleFormalDungeon(f.Created.Id,99,99,DungeonSettlementProtocolTests.Report(f.Created.Id,99),
            resources.Dungeons,SelfTestCatalogs.ClearExperience(pvf),resources.Growth,cards);
        That(settled is { Outcome:DungeonRunOutcome.Accepted, Card:not null, Receipt:not null } &&
            f.Session.AdvanceFormalDungeonCards(false) is not null && f.Session.AdvanceFormalDungeonCards(true) is not null,
            "busy fixture settles actual hope and opens cards");
        var stats=new CharacterStatComputer(resources.Characters);
        var town=new TownProjection(resources.Towns,stats,null,resources.Items.EpicPieces);
        var inventory=new InventoryProjection(stats,town,resources.Items.EpicPieces);
        using var logOutput=new StringWriter();
        using var log=new ServerLog(System.IO.Path.GetDirectoryName(f.Path)!,logOutput);
        using var stop=new CancellationTokenSource();
        using var stream=new MemoryStream();
        var locked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SqliteConnection? locker=null;
        int cardAcks=0, questAcks=0, historyNotices=0;
        await using var sender=new GameConnectionSender(f.Owner,stream,stop,0,(_,_,_,_,action) =>
        {
            if (action=="operation_hope_reward_auto_committed") Interlocked.Increment(ref questAcks);
            if (action=="operation_hope_reward_completed") Interlocked.Increment(ref historyNotices);
            if (action!="formal_free_card_auto_reveal") return;
            Interlocked.Increment(ref cardAcks);
            locker=new SqliteConnection("Data Source="+f.Path+";Pooling=False"); locker.Open();
            using var command=locker.CreateCommand(); command.CommandText="BEGIN IMMEDIATE"; command.ExecuteNonQuery();
            locked.TrySetResult();
        });
        var context=new GameConnectionContext(new(1,System.Net.IPAddress.Loopback,stream,stop),f.Owner,f.Session,sender);
        var hope=new QuestCompletionHandler(resources.Quests,resources.Parameters,resources.Items.Items,resources.Items.GoldLimits,
            resources.Items.EpicPieces,resources.Growth,log,new CharacterLevelProjection(stats));
        await using var projection=new DungeonCardProjection(context,log,inventory,hopeCompletion:hope,isBusy:SqliteBusyRetry.IsBusy);
        using var gateStop=new CancellationTokenSource();
        Task? gateWait=null;
        bool gateReleased=false;
        try
        {
            await projection.StartAsync(settled.Receipt!);
            var pending=(Task)(typeof(DungeonCardProjection).GetField("pending",BindingFlags.Instance|BindingFlags.NonPublic)!
                .GetValue(projection) ?? throw new InvalidOperationException("timer task missing"));
            await locked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (invalidate) gateWait=context.CommandGate.WaitAsync(gateStop.Token);
            bool observedBusy=false;
            for (int i=0;i<190 && !observedBusy;i++)
            {
                await Task.Delay(500);
                log.Flush();
                string logPath=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Path)!,"server.jsonl");
                if (!File.Exists(logPath)) continue;
                using var reader=new StreamReader(new FileStream(logPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite));
                while (reader.ReadLine() is { } line)
                {
                    using var json=JsonDocument.Parse(line);
                    var root=json.RootElement;
                    string? kind=root.GetProperty("Kind").GetString();
                    if (kind=="formal_hope_reward_busy") { observedBusy=true; break; }
                    if (kind=="formal_free_card_failed" && root.GetProperty("Detail").TryGetProperty("Error",out var error))
                    {
                        string message=error.GetString() ?? "";
                        if (message.Contains("SQLite Error 5",StringComparison.Ordinal) || message.Contains("SQLite Error 6",StringComparison.Ordinal)) observedBusy=true;
                    }
                }
            }
            That(observedBusy,"real SQLite busy observed after the original free-card ACK within 95 seconds");
            That(f.Number("SELECT count(*) FROM character_operation_hope_reward_receipts")==0,
                "busy transaction has not committed the hope reward");
            if (invalidate)
            {
                await gateWait!.WaitAsync(TimeSpan.FromSeconds(95));
                f.Session.ReturnToRoster();
            }
            using (var command=locker!.CreateCommand()) { command.CommandText="ROLLBACK"; command.ExecuteNonQuery(); }
            locker.Dispose(); locker=null;
            if (invalidate) { context.CommandGate.Release(); gateReleased=true; }
            await pending.WaitAsync(TimeSpan.FromSeconds(95));
            var after=f.Read();
            if (invalidate)
            {
                That(cardAcks==1 && questAcks==0 && historyNotices==0 &&
                    f.Number("SELECT count(*) FROM character_operation_hope_reward_receipts")==0 &&
                    after.ActiveMainQuests.Any(row=>row.AcceptanceId==active.AcceptanceId) &&
                    after.Inventory.Items.Where(row=>row.Core.ItemId==10307438).Sum(row=>row.Core.Count)==settled.Card!.Item!.Count,
                    "timer does not deliver old hope reward after selection becomes invalid");
                return;
            }
            if (questAcks==0 && f.Number("SELECT count(*) FROM character_operation_hope_reward_receipts")==0 &&
                after.ActiveMainQuests.Any(row=>row.AcceptanceId==active.AcceptanceId))
                throw new InvalidOperationException("FAIL: timer stopped after SQLite busy with hope reward uncommitted");
            That(cardAcks==1 && questAcks==1 && historyNotices==1 && after.ActiveMainQuests.All(row=>row.AcceptanceId!=active.AcceptanceId) &&
                after.Inventory.Items.Where(row=>row.Core.ItemId==10307438).Sum(row=>row.Core.Count)==settled.Card!.Item!.Count+60,
                "timer resumes only hope delivery after busy, without replaying card or reward");
            That(f.Session.ReturnFromFormalDungeon() is not null,"completed pending hope allows dungeon exit");
        }
        finally
        {
            if (locker is not null) { using var command=locker.CreateCommand(); command.CommandText="ROLLBACK"; command.ExecuteNonQuery(); locker.Dispose(); }
            stop.Cancel();
            if (gateWait is not null && !gateReleased)
            {
                gateStop.Cancel();
                try { await gateWait; context.CommandGate.Release(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    static void VerifyHopeRewards(PvfArchive pvf,ItemResources items,Resources resources)
    {
        var prices=SelfTestCatalogs.CardPrices(pvf);
        var rows=prices.SpecificRewards[8523];
        That(rows.Select(r=>r.Count).SequenceEqual([130,140,200]) && rows.Select(r=>r.Weight).SequenceEqual([3000,4000,3000]) &&
            rows.All(r=>r.ItemId==10307438),"希望翻牌完整保留当前PVF类型76三档材料，不混入普通装备池");
        var policy=new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf),items.DungeonDropItems,items.Items,
            items.GoldLimits,items.EpicPieces,prices:prices,
            hopeRewards:new(new OperationHopeRewardCatalog(pvf,items.Items),items.Items));
        OperationHopeEquipmentTests.Run(pvf,items,resources.Hope);
        int offset=0;
        foreach(var row in rows)
        {
            foreach(int roll in new[]{offset,offset+row.Weight-1})
            {
                var reward=policy.Prepare(resources.Dungeons.Get(8523),resources.Hope.Difficulty,1,draw:bound=>bound==10000?roll:0);
                That(reward.Item is {ItemId:10307438} && reward.Item.Count==row.Count && reward.HopeEquipment is not null &&
                    reward.ResourceSource.Contains(DungeonCardPriceCatalog.ResourcePath,StringComparison.Ordinal),"希望翻牌逐档首尾边界命中原声明");
            }
            offset+=row.Weight;
        }
        var withoutTable=new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf),items.DungeonDropItems,items.Items,items.GoldLimits,items.EpicPieces);
        Reject<NotSupportedException>(()=>withoutTable.Prepare(resources.Dungeons.Get(8523),resources.Hope.Difficulty,1,draw:_=>0),
            "希望缺专表明确拒绝，不能发普通装备掩盖资源装配错误");
        var disjoint=new DfoServer.GameWorld.Items.DisjointCatalog(pvf);
        foreach(int id in new[]{100070640,101040543,101030831})
        {
            var template=items.Items.Get(id);
            That(template.IsOperationHope,"防具、普通武器及胜武器均按真实operation hope标签识别");
            foreach(var machine in new DfoServer.Game.Inventory.Services.InventoryReward[]?[]{null,[new(3037,99)]})
            {
                var rewards=DfoServer.Game.Inventory.Services.DisjointResultCalculator.Calculate(disjoint,template,new Random(1),machine);
                That(rewards.OrderBy(r=>r.ItemId).Select(r=>(r.ItemId,r.Count)).SequenceEqual(new[]{(3262,5),(10307438,5)}),
                    "希望分解按PVF给5碎片和5金色晶块，普通与玩家机器不额外生成灵魂");
            }
        }
        That(!items.Items.Get(100070603).IsOperationHope,"同为95级传说不应仅凭等级稀有度套用希望分解");
    }

    static DungeonMap[] Route(OperationHopeCatalog catalog,DungeonMaze maze,DungeonPoint start,int destination)
    {
        var maps = maze.MapSpecification!.ToDictionary(row => row.Position);
        var queue = new Queue<DungeonPoint>(); queue.Enqueue(start);
        var parents = new Dictionary<DungeonPoint,DungeonPoint?> { [start] = null };
        while (queue.TryDequeue(out var point))
        {
            if (maps[point].Id == destination)
            {
                var route = new List<DungeonMap>();
                for (DungeonPoint? at = point; at is not null; at = parents[at.Value]) route.Add(maps[at.Value]);
                route.Reverse(); return route.ToArray();
            }
            foreach (var next in maps.Keys)
                if (!parents.ContainsKey(next) && OperationHopePlan.IsDeclaredTransition(catalog,maze.SourceOrdinal,point,next))
                { parents.Add(next,point); queue.Enqueue(next); }
        }
        throw new InvalidOperationException("希望真实前向路线没有到达目标终点");
    }

    sealed class Resources(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        public ItemResources Items { get; } = items;
        public TownCatalog Towns { get; } = towns;
        public CharacterCatalog Characters { get; } = characters;
        public QuestCatalog Quests { get; } = SelfTestCatalogs.Quests(pvf);
        public QuestRewardParameterCatalog Parameters { get; } = SelfTestCatalogs.QuestRewardParameters(pvf);
        public CharacterGrowthCatalog Growth { get; } = SelfTestCatalogs.Growth(pvf,SelfTestCatalogs.SkillLearning(pvf));
        public DungeonKillExperienceCatalog Kills { get; } = SelfTestCatalogs.KillExperience(pvf);
        public DungeonCatalog Dungeons { get; } = SelfTestCatalogs.Dungeons(pvf);
        public DungeonIncountCatalog Incounts { get; } = DungeonIncountCatalog.Load(pvf);
        public DungeonRoomCatalog Rooms { get; } = SelfTestCatalogs.Rooms(pvf);
        public AdventureCatalog Adventure { get; } = new(pvf);
        public OperationHopeCatalog Hope { get; } = OperationHopeCatalog.Load(pvf,SelfTestCatalogs.Dungeons(pvf),SelfTestCatalogs.Quests(pvf),SelfTestCatalogs.Rooms(pvf));
    }
    sealed class Fixture : IDisposable
    {
        readonly Resources resources;
        readonly TimeProvider clock;
        readonly bool preserveDirectory;
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),"S5A9-hope-session-"+Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public Character Created { get; }
        public GameSession Session { get; }
        public Fixture(Resources resources, bool preserveDirectory = false, BattlePassMissionPolicy? policy = null, TimeProvider? clock = null,
            OperationHopeCatalog? hope = null, ushort? fatigueRemaining = null)
        {
            this.resources = resources; this.clock = clock ?? TimeProvider.System;
            this.preserveDirectory = preserveDirectory; Path = System.IO.Path.Combine(root,"game.db");
            Repository = new(Path,resources.Quests,resources.Adventure,resources.Growth);
            if (policy is not null) Repository.ConfigureBattlePassMissions(policy);
            new AuthenticationService(new AuthenticationRepository(Path)).Create("hope-session","fixture-password");
            Owner = new AccountOwners().Acquire("hope-session",() => { });
            Created = Repository.Create(Owner,new("HopeSession",new(54,4,40,200),null) { Level = 95, CompletedQuests = [6170],
                DungeonState = CharacterDungeonState.NewOrdinary,
                InitialLearning = new(resources.Growth.GetLevelStartExperience(95),[new(0,10,0,[]),new(1,20,0,[])]) });
            Repository.InitializeAdventure(Owner,resources.Adventure,this.clock.GetUtcNow());
            if (fatigueRemaining is { } remaining)
                Sql($"UPDATE character_fatigue SET used=maximum-{remaining},remaining={remaining}");
            Session = new(Repository,resources.Towns,Owner,questCatalog:resources.Quests,operationHope:hope ?? resources.Hope,incounts:resources.Incounts,timeProvider:clock);
            Session.Select(Created.Slot,resumeTown: true); Session.MarkReady();
            That(Session.Selected!.Position is { Town: 54, Area: 4 },"正式换频道选角保留已存档的希望入口快照");
        }
        public DungeonIncountUsage Usage()
        {
            var row = resources.Incounts.Find(8523) ?? throw new InvalidDataException("希望必须装配真实次数行");
            return Repository.LoadDungeonIncount(Owner,Created.Id,row.CountKey,
                DungeonIncountPolicy.Period(clock.GetUtcNow(),resources.Incounts.WeeklyResetDay(row.CountKey)));
        }
        public Character Read() => Repository.LoadCharacter(Owner,Created.Slot);
        public FormalDungeonReceipt Enter()
        {
            if (Session.Phase == SessionPhase.Town)
                That(Session.BeginDungeonSelection() is not null,"原15命令在真实54/4门口进入选图");
            return Session.EnterFormalDungeon(8523,0,resources.Hope.Difficulty,resources.Dungeons,resources.Rooms,
                draw:_ => 0) ??
                throw new InvalidOperationException("希望入场拒绝："+Session.LastFormalDungeonRejection);
        }
        public OperationHopeFinishReceipt? Finish(ushort quest) => Session.FinishOperationHope(quest,ushort.MaxValue,1,
            resources.Quests,resources.Parameters,resources.Items.Items,resources.Items.GoldLimits,resources.Items.EpicPieces,resources.Growth);
        public void Sql(string sql) { using var db = new SqliteConnection("Data Source="+Path+";Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public long Number(string sql) { using var db = new SqliteConnection("Data Source="+Path+";Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public void Dispose() { Owner.Dispose(); if (!preserveDirectory && Directory.Exists(root)) TestFiles.DeleteDirectory(root,true); }
    }
}
