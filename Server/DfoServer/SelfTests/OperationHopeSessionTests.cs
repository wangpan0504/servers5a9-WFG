using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class OperationHopeSessionTests
{
    public static void Run(PvfArchive pvf,ItemResources items)
    {
        int before = Count;
        var resources = new Resources(pvf,items);
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
        Console.WriteLine($"PASS {Count-before} 代号希望Session入场与领奖链断言");
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

    sealed class Resources(PvfArchive pvf,ItemResources items)
    {
        public ItemResources Items { get; } = items;
        public TownCatalog Towns { get; } = TownCatalog.Load(pvf);
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
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),"S5A9-hope-session-"+Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public Character Created { get; }
        public GameSession Session { get; }
        public Fixture(Resources resources)
        {
            this.resources = resources; Path = System.IO.Path.Combine(root,"game.db");
            Repository = new(Path,resources.Quests,resources.Adventure,resources.Growth);
            new AuthenticationService(new AuthenticationRepository(Path)).Create("hope-session","fixture-password");
            Owner = new AccountOwners().Acquire("hope-session",() => { });
            Created = Repository.Create(Owner,new("HopeSession",new(54,4,40,200),null) { Level = 95, CompletedQuests = [6170],
                DungeonState = CharacterDungeonState.NewOrdinary,
                InitialLearning = new(resources.Growth.GetLevelStartExperience(95),[new(0,10,0,[]),new(1,20,0,[])]) });
            Repository.InitializeAdventure(Owner,resources.Adventure,DateTimeOffset.UtcNow);
            Session = new(Repository,resources.Towns,Owner,questCatalog:resources.Quests,operationHope:resources.Hope,incounts:resources.Incounts);
            Session.Select(Created.Slot,resumeTown: true); Session.MarkReady();
            That(Session.Selected!.Position is { Town: 54, Area: 4 },"正式换频道选角保留已存档的希望入口快照");
        }
        public DungeonIncountUsage Usage()
        {
            var row = resources.Incounts.Find(8523) ?? throw new InvalidDataException("希望必须装配真实次数行");
            return Repository.LoadDungeonIncount(Owner,Created.Id,row.CountKey,
                DungeonIncountPolicy.Period(DateTimeOffset.UtcNow,resources.Incounts.WeeklyResetDay(row.CountKey)));
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
        public void Dispose() { Owner.Dispose(); if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
