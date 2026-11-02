using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassAltarNonTargetTests
{
    public static void Run(TcpTestResources resources)
    {
        int before = Count;
        var archive = resources.Archive;
        var growth = SelfTestCatalogs.Growth(archive);
        var dungeons = SelfTestCatalogs.Dungeons(archive);
        var rooms = SelfTestCatalogs.Rooms(archive);
        var items = resources.ItemResources;
        var policy = new BattlePassMissionPolicy("altar-nontarget-source-fixture",
            new DateTimeOffset(2026,10,22,6,0,0,TimeSpan.FromHours(8)), BattlePassCatalog.Load(archive));
        var at = Enumerable.Range(0,policy.Days).Select(day=>policy.Start.AddDays(day).AddHours(1))
            .First(value=>policy.Active(value).Any(row=>row.Key.Category==0 && row.Key.Id==1));
        var area = resources.Towns.Areas.Values.First(row=>row.DungeonWorldMap is {} world &&
            resources.Towns.DungeonMaps[world].Options.Any(option=>option.DungeonId==11006));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(area.Default.Bounds.SelectMany(row=>row.Quests))
            .Concat(area.Default.DungeonGates.SelectMany(row=>row.Quests)).Append(63).Distinct().ToArray();
        var bounds = area.Map(done.ToHashSet()).Bounds.First(row=>row.Conditions.Length==0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        foreach (string scenario in new[] { "39","255","rollback","projection-gm","projection-reselect","projection-takeover","projection-busy","projection-busy-reselect" })
        {
            bool use255 = scenario!="39";
            var clock = new Clock(at);
            using var linked = new QuestSceneTests.Fixture(resources.Towns,resources.Characters,resources.Learning,growth,
                resources.Quests,false,70,done,position:position,timeProvider:clock);
            linked.Repository.ConfigureBattlePassMissions(policy);
            That(linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!.Slots
                .Single(row=>row.Instance.Key.Category==0 && row.Instance.Key.Id==1).Count==0,"非目标祭坛隔离任务起点为零");
            That(linked.Session.MutateInventory(c=>CurrencyService.SetGold(c,500000)).Accepted,"准备非目标祭坛费用");
            That(linked.Session.ChangeQuest(5650,DfoServer.Game.Quests.QuestLifecycleAction.Accept,
                resources.Quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,"保留真实祭坛5650接取");
            That(linked.Session.BeginDungeonSelection() is not null,"非目标死亡沿原选图上下文");
            var entered = linked.Session.EnterFormalDungeon(11006,5650,0,dungeons,rooms,pieces:items.EpicPieces);
            That(entered?.Monsters.Altar is not null,"进入带真实接取的无尽祭坛");
            var loaded = linked.Session.CompleteFormalDungeonLoading()!;
            That(linked.Session.PrepareDungeonAltarUi(loaded),"沿真实加载准备祭坛波次");
            var current = linked.Session.StartDungeonAltar(loaded)!;
            clock.AdvanceTo(current.Due!.Value);
            current = linked.Session.TickDungeonAltar(current)!;
            That(current.Batch is not null,"原计时器发布第一批真实祭坛对象");
            int target = resources.Quests.Get(5650).IntData[2];
            var actor = current.Batch!.Actors.First(row=>row.Phase.MonsterId!=target);
            var original = linked.Read();
            var selected = linked.Session.Selected;
            var beforeDeath = linked.Session.DungeonSnapshot!;
            long growthReceipts = Convert.ToInt64(linked.Query("SELECT count(*) FROM character_dungeon_growth_receipts"));
            That(linked.Session.ObserveDungeonAltarDeaths([ushort.MaxValue],new string('D',64),growth) is null,
                "未发布对象不能伪造祭坛独立来源");
            if (scenario=="rollback")
            {
                var missionBefore = linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!;
                int fault = 0;
                foreach (string trigger in new[]
                {
                    "BEFORE INSERT ON account_battle_pass_source_events",
                    "BEFORE INSERT ON account_battle_pass_mission_progress",
                    "BEFORE UPDATE OF revision ON account_battle_pass_seasons"
                })
                {
                    var expected = linked.Session.Selected!;
                    linked.Sql("CREATE TRIGGER fail_altar_nontarget " + trigger + " BEGIN SELECT RAISE(ABORT,'altar independent source rollback'); END");
                    Reject<Microsoft.Data.Sqlite.SqliteException>(()=>linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey),growth),
                        "非目标来源、进度或晚季版本失败保留原pending：" + trigger);
                    var rolled = linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!;
                    That(ReferenceEquals(linked.Session.Selected,expected) && linked.Read().Revision==expected.Revision &&
                        linked.Read().ActiveMainQuests.SequenceEqual(original.ActiveMainQuests) &&
                        linked.Session.DungeonSnapshot!.Identity==beforeDeath.Identity &&
                        linked.Session.DungeonSnapshot.LivingMonsters.SequenceEqual(beforeDeath.LivingMonsters) &&
                        rolled.Season.Revision==missionBefore.Season.Revision && rolled.Season.TotalExperience==missionBefore.Season.TotalExperience &&
                        rolled.Season.PassType==missionBefore.Season.PassType && rolled.Season.ToClaimBytes().SequenceEqual(missionBefore.Season.ToClaimBytes()) &&
                        rolled.Slots.Select(row=>(row.Instance.Key,row.Count,row.Completed)).SequenceEqual(
                            missionBefore.Slots.Select(row=>(row.Instance.Key,row.Count,row.Completed))) &&
                        Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_non_target_monster_death'"))==0 &&
                        Convert.ToInt64(linked.Query("SELECT count(*) FROM character_dungeon_growth_receipts"))==growthReceipts,
                        "独立事件事务全回滚且不确认原对象，不改任务或角色资产：" + trigger);
                    linked.Sql("DROP TRIGGER fail_altar_nontarget");
                    if (fault++==0)
                    {
                        long epoch = linked.Session.SelectionEpoch;
                        var updated = linked.Session.SetExpertJob(expected.Name,2,17);
                        That(updated.Id==expected.Id && updated.Revision==expected.Revision+1 && !ReferenceEquals(updated,expected) &&
                            ReferenceEquals(updated,linked.Session.Selected) && linked.Session.SelectionEpoch==epoch &&
                            updated.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) &&
                            linked.Session.DungeonSnapshot!.Identity==beforeDeath.Identity &&
                            linked.Session.DungeonSnapshot.LivingMonsters.SequenceEqual(beforeDeath.LivingMonsters) &&
                            linked.Read().ExpertJobType==2 && linked.Read().ExpertJobExperience==17,
                            "真实GM副职业替换同Id快照，原非目标死亡pending与完整任务保持");
                    }
                }
                original=linked.Read();
                selected=linked.Session.Selected;
            }
            current = use255
                ? linked.Session.ObserveDungeonAltarDeaths([actor.ObjectKey],new string('B',64),growth)!
                : linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey),growth)!;
            That(current is not null,"已发布非目标死亡沿真实39或255确认");
            if (current is null) throw new InvalidDataException("已发布非目标死亡缺少原回执");
            var mission = linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!;
            long kills = mission.Slots.Single(row=>row.Instance.Key.Category==0 && row.Instance.Key.Id==1).Count;
            long sources = Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_non_target_monster_death'"));
            Console.WriteLine($"ALTAR_NONTARGET_SOURCE opcode={(use255?255:39)} monster={actor.Phase.MonsterId} count={kills} sourceRows={sources} expected=1");
            That(kills==1 && sources==1,"已发布非目标祭坛真实死亡独立计一次基础击杀且不要求任务变化");
            var after = linked.Read();
            That(after.Revision==original.Revision && ReferenceEquals(linked.Session.Selected,selected) &&
                after.ActiveMainQuests.SequenceEqual(original.ActiveMainQuests) && after.Learning.Experience==original.Learning.Experience &&
                after.Level==original.Level && after.DungeonState==original.DungeonState && after.Inventory.Gold==original.Inventory.Gold &&
                after.Inventory.Items.SequenceEqual(original.Inventory.Items) && mission.Season.TotalExperience==0 &&
                Convert.ToInt64(linked.Query("SELECT count(*) FROM character_dungeon_growth_receipts"))==growthReceipts &&
                Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_quest_target_monster_death'"))==0 &&
                linked.Session.DungeonSnapshot!.Identity==beforeDeath.Identity && !linked.Session.DungeonSnapshot.LivingMonsters.Contains(actor.ObjectKey),
                "非目标只提交独立战令来源，不伪造任务、角色revision、普通经验掉落或growth回执");
            That(current.QuestCharacter is null && current.BattlePassCharacter is not null && current.BattlePassMissionClock is not null &&
                linked.Session.CanPublishDungeonAltar(current),"非目标只提供独立已提交战令投影回执，不伪装任务条件变化");
            VerifyPublicationAsync(linked,current,clock,scenario,actor.ObjectKey,growth).GetAwaiter().GetResult();
            if (scenario is "projection-reselect" or "projection-takeover" or "projection-busy-reselect") continue;
            long seasonRevision = mission.Season.Revision;
            _ = linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey),growth);
            _ = linked.Session.ObserveDungeonAltarDeaths([actor.ObjectKey],new string('C',64),growth);
            That(linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!.Season.Revision==seasonRevision &&
                Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_non_target_monster_death'"))==1,
                "同对象跨39与255重报只保留原独立来源，不重复计数或季版本");
            That(linked.Session.ReturnFromFormalDungeon() is not null,"非目标提交后正常返回城镇");
            var reopened = new DfoServer.Persistence.CharacterRepository(
                (string)linked.Query("SELECT file FROM pragma_database_list WHERE name='main'")!,resources.Quests);
            reopened.ConfigureBattlePassMissions(policy);
            That(reopened.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!.Slots
                .Single(row=>row.Instance.Key.Category==0 && row.Instance.Key.Id==1).Count==1 &&
                reopened.LoadCharacter(linked.Owner,0).ActiveMainQuests.SequenceEqual(original.ActiveMainQuests),
                "重建Repository恢复非目标战令击杀，原任务条件仍完全不变");
        }
        var bossAt = Enumerable.Range(0,policy.Days).Select(day=>policy.Start.AddDays(day).AddHours(1))
            .First(value=>policy.Active(value).Any(row=>row.Key.Category==0 && row.Key.Id==1) &&
                policy.Active(value).Any(row=>row.Key.Category==0 && row.Key.Id==2));
        foreach (uint dungeonId in new[] { 11006u,11007u })
        {
            var clock=new Clock(bossAt);
            using var linked=new QuestSceneTests.Fixture(resources.Towns,resources.Characters,resources.Learning,growth,
                resources.Quests,false,70,done.Append(7339).Distinct().ToArray(),position:position,timeProvider:clock);
            linked.Repository.ConfigureBattlePassMissions(policy);
            That(linked.Session.MutateInventory(c=>CurrencyService.SetGold(c,500000)).Accepted,"准备无接取祭坛领主路线费用");
            That(linked.Session.BeginDungeonSelection() is not null,"无接取祭坛沿原选图上下文");
            var entered=linked.Session.EnterFormalDungeon(dungeonId,0,0,dungeons,rooms,pieces:items.EpicPieces);
            That(entered?.Monsters.Altar is not null,"无接取祭坛沿真实波次寻找原Rank3领主："+dungeonId);
            var original=linked.Read();
            long deaths=0,bosses=0;
            while (bosses==0)
            {
                var loaded=linked.Session.CompleteFormalDungeonLoading()!;
                That(linked.Session.PrepareDungeonAltarUi(loaded),"无接取领主路线准备原祭坛UI");
                var current=linked.Session.StartDungeonAltar(loaded)!;
                while (!current.State.Completed && bosses==0)
                {
                    if (current.State.WaitingForDifficulty)
                        current=linked.Session.SelectDungeonAltarDifficulty(current.State.Choices.Last())!;
                    clock.AdvanceTo(current.Due!.Value);
                    current=linked.Session.TickDungeonAltar(current)!;
                    if (current.Batch is not {} batch) continue;
                    foreach (var actor in batch.Actors)
                    {
                        // 加载回执只含当时已发布对象；计时器发布后重新取原Run的真实存活怪表。
                        var planned=linked.Session.CurrentFormalDungeonScene()!.Monsters.Monsters
                            .Single(row=>row.ObjectKey==actor.ObjectKey);
                        That(planned.IsBoss==(actor.Rank==3),"领主资格只取原已规划祭坛Rank3，不从模板名猜测");
                        current=deaths%3==0
                            ? linked.Session.ObserveDungeonAltarScriptedDeath(new(actor.ObjectKey,0,true,new string('E',64)),growth)!
                            : deaths%3==1 ? linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey),growth)!
                            : linked.Session.ObserveDungeonAltarDeaths([actor.ObjectKey],new string('F',64),growth)!;
                        That(current is not null,"普通39、零参与者39与255共用已发布对象死亡来源");
                        if (current is null) throw new InvalidDataException("原祭坛已发布死亡缺少回执");
                        deaths++;
                        if (planned.IsBoss) bosses++;
                        var mission=linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!;
                        That(mission.Slots.Single(row=>row.Instance.Key.Category==0 && row.Instance.Key.Id==1).Count==Math.Min(10,deaths) &&
                            mission.Slots.Single(row=>row.Instance.Key.Category==0 && row.Instance.Key.Id==2).Count==Math.Min(2,bosses) &&
                            Convert.ToInt64(linked.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_non_target_monster_death'"))==deaths &&
                            mission.Season.TotalExperience==0 && linked.Read().Revision==original.Revision &&
                            linked.Read().ActiveMainQuests.SequenceEqual(original.ActiveMainQuests) &&
                            linked.Read().Learning.Experience==original.Learning.Experience && linked.Read().Inventory.Gold==original.Inventory.Gold &&
                            linked.Read().Inventory.Items.SequenceEqual(original.Inventory.Items),
                            "两祭坛全部已发布真实死亡计raw1，只有原领主计raw2，封顶仍保每事件来源且无资产奖励");
                        if (bosses!=0) break;
                    }
                }
                if (bosses==0)
                {
                    var next=entered!.Entry.Maze.StaticTopology.Links!.SelectMany(link=>
                        new[] { (From:link.First,To:link.Second),(From:link.Second,To:link.First) })
                        .Single(edge=>edge.From==loaded.Snapshot.CurrentMap.Position && edge.To.X>edge.From.X).To;
                    That(linked.Session.MoveFormalDungeon(checked((byte)next.X),checked((byte)next.Y)) is not null,
                        "祭坛领主未出现前只沿原房间连接前进");
                }
            }
            That(bosses>0,"两祭坛均实际遇到并确认原Rank3领主");
        }
        Console.WriteLine($"PASS {Count-before} BattlePass altar non-target source checks");
    }

    static async Task VerifyPublicationAsync(QuestSceneTests.Fixture linked,DungeonAltarReceipt receipt,TimeProvider clock,string scenario,
        ushort objectKey,DfoServer.GameWorld.Characters.CharacterGrowthCatalog growth)
    {
        using var stop=new CancellationTokenSource();
        using var output=new MemoryStream();
        var sent=new List<(ushort Opcode,byte[] Body)>();
        await using var sender=new DfoServer.Network.GameConnectionSender(linked.Owner,output,stop,0,
            (_,opcode,_,body,_)=>sent.Add((opcode,body.ToArray())));
        var context=new DfoServer.Network.Protocol.GameConnectionContext(new(1,System.Net.IPAddress.Loopback,output,stop),
            linked.Owner,linked.Session,sender);
        byte[] expected=linked.Repository.ReadBattlePassMissions(linked.Owner,clock.GetUtcNow())!.ToPayload();
        DfoServer.Game.Accounts.AccountOwners.Lease? replacement=null;
        if (scenario=="projection-gm")
        {
            var original=linked.Session.Selected!;
            long epoch=linked.Session.SelectionEpoch;
            var updated=linked.Session.SetExpertJob(original.Name,3,23);
            That(updated.Id==original.Id && !ReferenceEquals(original,updated) && updated.Revision==original.Revision+1 &&
                linked.Session.SelectionEpoch==epoch && linked.Session.CanPublishDungeonAltarBattlePass(receipt),
                "死亡已提交后真实GM副职业替换同角色快照，原owner/epoch/实例BP资格保持");
            var replay=linked.Session.ObserveDungeonAltarDeaths([objectKey],new string('9',64),growth);
            That(replay is not null && replay.QuestCharacter is null && linked.Session.CanPublishDungeonAltarBattlePass(replay),
                "同Id新快照后原255重报仍恢复待发BP回执，不借任务通知或重计死亡");
            receipt=replay ?? throw new InvalidDataException("原非目标待发BP回执丢失");
        }
        else if (scenario=="projection-reselect")
        {
            long epoch=linked.Session.SelectionEpoch;
            linked.Session.ReturnToRoster();linked.Session.Select(0);linked.Session.MarkReady();
            That(linked.Session.SelectionEpoch!=epoch && !linked.Session.CanPublishDungeonAltarBattlePass(receipt),
                "同角色重新选择仍以原选角纪元拒绝旧祭坛BP投影");
        }
        else if (scenario=="projection-takeover")
        {
            // 复用本夹具实际账号目录完成接管，不创建第二套假owner。
            var owners=(DfoServer.Game.Accounts.AccountOwners)typeof(QuestSceneTests.Fixture)
                .GetField("owners",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(linked)!;
            replacement=owners.Acquire(linked.Owner.Account,()=>{});
            That(!linked.Owner.IsCurrent && replacement.IsCurrent,"实际接管使原祭坛owner失效");
        }
        try
        {
            if (scenario is "projection-busy" or "projection-busy-reselect")
                await VerifyBusyPublicationAsync(linked,context,receipt,sent,scenario=="projection-busy-reselect");
            else await DfoServer.Network.Handlers.DungeonHandler.PublishAltarProgressAsync(context,receipt);
        }
        catch (Exception error) when (scenario=="projection-takeover" && error is InvalidOperationException or IOException or OperationCanceledException) { }
        finally { replacement?.Dispose(); }
        if (scenario is "projection-reselect" or "projection-takeover" or "projection-busy-reselect")
        {
            That(sent.Count==0,"换角纪元或接管后不发布旧祭坛1801或任务通知");
            return;
        }
        That(sent.Count==1 && sent[0].Opcode==(ushort)DfoServer.Network.NotiPacketType.BATTLE_PASS_INFO_MISSION &&
            sent[0].Body.SequenceEqual(expected),"原祭坛Handler前缀实际仅发一次1801，不额外发ACCEPTED_QUEST");
        That(!linked.Session.MarkDungeonAltarBattlePassPublished(receipt),"成功发送后原战令投影已正确标记");
    }

    static async Task VerifyBusyPublicationAsync(QuestSceneTests.Fixture linked,
        DfoServer.Network.Protocol.GameConnectionContext context,DungeonAltarReceipt receipt,List<(ushort Opcode,byte[] Body)> sent,bool reselect)
    {
        string path=(string)linked.Query("SELECT file FROM pragma_database_list WHERE name='main'")!;
        DfoServer.Persistence.SqliteDatabaseBootstrap.ReleaseRuntimePool();
        using var locker=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource=path,Pooling=false }.ToString());
        locker.Open();
        using (var setup=locker.CreateCommand())
        {
            setup.CommandText="PRAGMA journal_mode=DELETE";
            That(string.Equals(Convert.ToString(setup.ExecuteScalar()),"delete",StringComparison.OrdinalIgnoreCase),
                "隔离祭坛库切换DELETE日志以实际阻塞读取");
            setup.CommandText="BEGIN EXCLUSIVE";
            setup.ExecuteNonQuery();
        }
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquired=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer=System.Diagnostics.Stopwatch.StartNew();
        long firstBusyTicks=0,acquiredTicks=0;
        int busyCount=0,firstBusyCode=0;
        bool ClassifyBusy(Exception error)
        {
            bool busy=DfoServer.Persistence.SqliteBusyRetry.IsBusy(error);
            if (busy)
            {
                Interlocked.CompareExchange(ref firstBusyTicks,timer.ElapsedTicks,0);
                Interlocked.CompareExchange(ref firstBusyCode,((Microsoft.Data.Sqlite.SqliteException)error).SqliteErrorCode,0);
                Interlocked.Increment(ref busyCount);
            }
            return busy;
        }
        using var releaseGate=new ManualResetEventSlim();
        Task publishing=Task.Run(async ()=>
        {
            started.SetResult();
            await DfoServer.Network.Handlers.DungeonHandler.PublishAltarProgressAsync(context,receipt,
                ClassifyBusy);
        });
        Task? probe=null;
        bool probeRunning=false,blocked=false,gateAvailable=false,pending=false;
        int busyBeforeRelease=0;
        long firstBusyBeforeRelease=0,acquiredBeforeRelease=0;
        TaskStatus publishingBeforeRelease=default;
        try
        {
            await started.Task;
            await Task.Delay(250);
            probe=Task.Factory.StartNew(()=>
            {
                probeStarted.TrySetResult();
                return linked.Owner.Execute(()=>
                {
                    Interlocked.Exchange(ref acquiredTicks,timer.ElapsedTicks);
                    acquired.TrySetResult();
                    releaseGate.Wait();
                    if (reselect)
                    {
                        linked.Session.ReturnToRoster();
                        linked.Session.Select(0);
                        linked.Session.MarkReady();
                    }
                    return true;
                });
            },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            probeRunning=await Task.WhenAny(probeStarted.Task,Task.Delay(5000))==probeStarted.Task;
            blocked=probeRunning && await Task.WhenAny(acquired.Task,Task.Delay(1000))!=acquired.Task;
            // 原生busy_timeout与托管重试可能叠加，保留首次真实BUSY与锁释放断言。
            gateAvailable=await Task.WhenAny(acquired.Task,Task.Delay(TimeSpan.FromSeconds(95)))==acquired.Task;
            pending=gateAvailable && !publishing.IsCompleted && sent.Count==0;
            busyBeforeRelease=Volatile.Read(ref busyCount);
            firstBusyBeforeRelease=Interlocked.Read(ref firstBusyTicks);
            acquiredBeforeRelease=Interlocked.Read(ref acquiredTicks);
            publishingBeforeRelease=publishing.Status;
        }
        finally
        {
            using var release=locker.CreateCommand();
            release.CommandText="ROLLBACK";
            release.ExecuteNonQuery();
            releaseGate.Set();
            if (probe is not null) await probe.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Exception? failure=null;
        try { await publishing.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) { failure=error; }
        if (!probeRunning || !blocked || !gateAvailable || !pending || failure is not null)
            Console.WriteLine($"ALTAR_BUSY_DIAGNOSTIC reselect={reselect} probeStarted={probeRunning} blocked={blocked} " +
                $"gateAvailable={gateAvailable} publishingBeforeRelease={publishingBeforeRelease} " +
                $"busyCallsBeforeRelease={busyBeforeRelease} firstBusyCode={Volatile.Read(ref firstBusyCode)} " +
                $"firstBusyElapsedMs={1000.0*firstBusyBeforeRelease/System.Diagnostics.Stopwatch.Frequency:F1} " +
                $"acquiredElapsedMs={1000.0*acquiredBeforeRelease/System.Diagnostics.Stopwatch.Frequency:F1} " +
                $"publishingFailure={failure}");
        That(probeRunning,"账号锁探针专用线程已开始执行");
        That(blocked,"原投影读取确实持有账号锁并被真实SQLite独占锁阻塞");
        That(gateAvailable,"读取忙锁超时后重试等待释放账号锁");
        That(busyBeforeRelease>0,"独占锁释放之前已实际分类SQLite BUSY或LOCKED");
        That(pending && failure is null,
            "忙锁超时后保留原投影并等待恢复，解锁后原发布成功："+failure);
        if (reselect) That(!linked.Session.CanPublishDungeonAltarBattlePass(receipt),
            "忙锁重试前重新选择角色使原祭坛receipt失效");
    }

    static NativeDungeonDeathObservation Observation(ushort key) => new(key,1,0x111,1,600,1,0,70,0,650,1,true,new string('A',64));
    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        long elapsed;
        public override long TimestampFrequency=>1000;
        public override long GetTimestamp()=>elapsed;
        public override DateTimeOffset GetUtcNow()=>now.AddMilliseconds(elapsed);
        public void AdvanceTo(long value)=>elapsed=Math.Max(elapsed,value);
    }
}
