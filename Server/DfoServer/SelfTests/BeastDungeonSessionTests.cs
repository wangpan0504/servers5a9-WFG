using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Skills;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using DfoServer.Testing.Inventory;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 在隔离文件库中走生产Session；合成角色与时钟不代表客户端点击或实机战斗。
internal static class BeastDungeonSessionTests
{
    public static void Run(TcpTestResources resources)
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-beast-session-" + Guid.NewGuid().ToString("N"));
        var owners = new AccountOwners();
        var repository = new CharacterRepository(Path.Combine(root,"game.db"),resources.Quests);
        var catalog = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var drops = SelfTestCatalogs.MonsterDrops(resources.Archive);
        var kills = SelfTestCatalogs.KillExperience(resources.Archive);
        try
        {
            foreach (int dungeon in Enumerable.Range(310,5).Concat(Enumerable.Range(BeastDungeonRotation.FirstGatewayDungeonId,5)))
            {
                bool gateway = BeastDungeonRotation.IsGateway((uint)dungeon);
                using var owner = owners.Acquire("beast-session-"+dungeon,()=>{});
                var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                    resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeon));
                var completed = new HashSet<int>(area.Quests) { 5927 };
                if (area.AnyQuests.Length != 0) completed.Add(area.AnyQuests[0]);
                completed.UnionWith(area.Map(completed).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
                var bounds = area.Map(completed).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(completed.Contains)).Bounds;
                var position = new Location(area.Town,area.Area,(ushort)(bounds.X+bounds.Width/2),(ushort)(bounds.Y+bounds.Height/2));
                var candidate = CharacterCreation.Prepare("Beast"+dungeon,resources.Characters.Get(0),position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) },resources.Learning);
                repository.Create(owner,candidate with { Level=95,AcceptedStoryLevel=95,CompletedQuests=completed.ToArray(),InitialLearning=learning });
                var at = new DateTimeOffset(2026,10,26,12,0,0,TimeSpan.FromHours(8));
                while (!BeastDungeonRotation.Contains(BeastDungeonRotation.ServiceDay(at),dungeon)) at=at.AddDays(1);
                var clock = new DailyBoosterTestClock(at);
                var session = new GameSession(repository,resources.Towns,owner,questCatalog:resources.Quests,timeProvider:clock,
                    items:resources.ItemResources.Items,goldLimits:resources.ItemResources.GoldLimits,pieces:resources.ItemResources.EpicPieces,
                    entry:gateway ? new(new(2,49),40,"[beast]") : null);
                session.Select(0,resumeTown:true); session.MarkReady();
                if (gateway)
                {
                    var wrongChannel = new GameSession(repository,resources.Towns,owner,questCatalog:resources.Quests,timeProvider:clock,
                        items:resources.ItemResources.Items,goldLimits:resources.ItemResources.GoldLimits);
                    wrongChannel.Select(0,resumeTown:true); wrongChannel.MarkReady();
                    That(wrongChannel.EnterFormalDungeon((uint)dungeon,0,0,catalog,rooms,drops,
                        items:resources.ItemResources.Items,pieces:resources.ItemResources.EpicPieces,beastMode:true) is null &&
                        wrongChannel.LastFormalDungeonRejection == "beast-gateway-channel-and-mode-required",
                        "裸Session不能绕过专属频道门禁");
                    int closed = Enumerable.Range(1,5).First(days=>!BeastDungeonRotation.Contains(BeastDungeonRotation.ServiceDay(at.AddDays(days)),dungeon));
                    clock.Advance(TimeSpan.FromDays(closed));
                    That(session.EnterFormalDungeon((uint)dungeon,0,0,catalog,rooms,drops,
                        items:resources.ItemResources.Items,pieces:resources.ItemResources.EpicPieces,beastMode:true) is null &&
                        repository.LoadBeastDungeonUsed(owner,session.Selected!.Id,BeastDungeonRotation.ServiceDay(clock.GetUtcNow()),dungeon)==0,
                        "非开放日专属入口拒绝且不扣次");
                    clock.Advance(TimeSpan.FromDays(-closed));
                }
                // 难度取实机原值：全量日志 217,237 行里五张兼容入口图（5335–5339）的 CMD16 一律是难度 2，
                // 原生 310–314 则出现 0/1/4（4 是第六档）。以前这里一律用 0，测的不是客户端真正发的东西。
                // 兼容入口的 PVF 难度表只有 `1 1 1 0 0`，客户端发 5 会被服务端以 difficulty-unsupported 拒绝，
                // 这一点在没有第六按钮实机包之前不改行为。
                byte difficulty = gateway ? (byte)2 : (byte)4;
                var scene = session.EnterFormalDungeon((uint)dungeon,0,difficulty,catalog,rooms,drops,
                    items:resources.ItemResources.Items,pieces:resources.ItemResources.EpicPieces,beastMode:true);
                That(scene is not null,"真实魔兽Session完整路线准备及原子入场："+dungeon+" / 难度"+difficulty+" / "+session.LastFormalDungeonRejection);
                That(scene!.Entry.BeastMode && scene.Entry.Maze.MapSpecification!.Count == 15 &&
                    scene.Entry.Maze.StaticTopology.Supported && session.CompleteFormalDungeonLoading() is not null,
                    "五张魔兽图均通过正式Run及地图加载，不能只证明资源能打开");
                // 「有魔兽过场图」的判据在魔兽区块身上，不在客户端；第六难度也必须带着它进场。
                // 兼容入口固定取 310 的 BeastLayouts[0]，所以这里钉死资源路径；原图只要求非空，各自的区块可不同。
                That(gateway
                        ? scene.Entry.Maze.BeastLayout?.CutsceneImage == "Map/CutScene/Beast.img"
                        : !string.IsNullOrEmpty(scene.Entry.Maze.BeastLayout?.CutsceneImage),
                    "魔兽入场带区块声明的过场图："+dungeon+" / 难度"+difficulty+" / "+scene.Entry.Maze.BeastLayout?.CutsceneImage);
                That(repository.LoadBeastDungeonUsed(owner,session.Selected!.Id,BeastDungeonRotation.ServiceDay(at),dungeon)==1,
                    "正式魔兽Session成功入场只扣一次本图次数");
                if (dungeon != 310 && dungeon != BeastDungeonRotation.FirstGatewayDungeonId) continue;
                var cleared = new HashSet<DfoServer.GameWorld.Dungeons.DungeonPoint>();
                var targets = new[] { scene.Entry.BossMap }.Concat(scene.Entry.Maze.MapSpecification!
                    .Where(map => map.Kind == "Beast" && map != scene.Entry.BossMap)).ToArray();
                foreach (var target in targets)
                {
                    var path = QuestStoryRouteTests.Path(scene.Entry.Maze.StaticTopology,
                        session.CurrentFormalDungeonScene()!.Snapshot.CurrentMap.Position,target.Position);
                    ClearCurrent();
                    foreach (var point in path.Skip(1))
                    {
                        That(session.MoveFormalDungeon((byte)point.X,(byte)point.Y) is not null,
                            "魔兽按真实门墙过图，提前清理静态末格也不锁死后续阶段");
                        That(session.CompleteFormalDungeonLoading() is not null,"魔兽每次过图保持正式加载屏障");
                        ClearCurrent();
                    }
                    if (target == targets[0])
                        That(cleared.Count < 6 && session.Phase == SessionPhase.Dungeon,
                            "先清脚本最后一格仍未完成全部六阶段，不能提前进入结算后卡死");
                    if (session.Phase == SessionPhase.Settlement) break;
                }
                That(cleared.Count == 6 && session.Phase == SessionPhase.Settlement &&
                    session.EnableFormalDungeonSettlement() is not null,
                    "全部六间阶段房真实清完后，原Run才签发唯一通关事实");
                That(BeastDungeonRotation.StageRewardItems.All(id => session.Selected!.Inventory.Items
                    .Where(item => item.Core.ItemId == id).Sum(item => item.Core.Count) == 1),
                    "完整正式魔兽Session六阶段各到账一次袖珍罐，回访不重复发放");

                void ClearCurrent()
                {
                    var current = session.CurrentFormalDungeonScene()!;
                    foreach (var monster in current.Monsters.Monsters.Where(monster => monster.IsBlocking && monster.CanReportDeath)
                        .OrderBy(monster => monster.IsBoss))
                    {
                        var result = session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(
                            session.CurrentFormalDungeonScene()!,monster),kills,resources.Growth);
                        That(result.Outcome == DungeonRunOutcome.Accepted,"魔兽实际阶段演员通过生产死亡确认并提交成长");
                    }
                    if (current.Snapshot.CurrentMap.Kind == "Beast") cleared.Add(current.Snapshot.CurrentMap.Position);
                    That(cleared.Count == 6 || session.Phase == SessionPhase.Dungeon,
                        "未清完六阶段时保留正式战斗阶段，允许继续寻找其余阶段房");
                }
            }
        }
        finally { Directory.Delete(root,true); }
        RunParty(resources,310);
        RunParty(resources,BeastDungeonRotation.FirstGatewayDungeonId);
        Console.WriteLine($"PASS {Count-before} 魔兽正式Session资源、加载与次数断言（非实机）");
    }

    static void RunParty(TcpTestResources resources, int dungeon)
    {
        bool gateway=BeastDungeonRotation.IsGateway((uint)dungeon);
        string root = Path.Combine(Path.GetTempPath(), "S5A9-beast-party-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root,"game.db");
        var owners = new AccountOwners();
        using var alpha = owners.Acquire("beast-pa",()=>{});
        using var beta = owners.Acquire("beast-pb",()=>{});
        try
        {
            var repository = new CharacterRepository(path,resources.Quests);
            var catalog = SelfTestCatalogs.Dungeons(resources.Archive);
            var rooms = SelfTestCatalogs.Rooms(resources.Archive);
            var drops = SelfTestCatalogs.MonsterDrops(resources.Archive);
            var kills = SelfTestCatalogs.KillExperience(resources.Archive);
            var items = resources.ItemResources;
            var dropPolicy = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(resources.Archive),items.DungeonDropItems,
                drops.Get,items.Items.Get,items.DungeonSpecialDrops);
            var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeon));
            var completed = new HashSet<int>(area.Quests) {5927};
            if (area.AnyQuests.Length != 0) completed.Add(area.AnyQuests[0]);
            completed.UnionWith(area.Map(completed).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
            var bounds = area.Map(completed).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(completed.Contains)).Bounds;
            var position = new Location(area.Town,area.Area,(ushort)(bounds.X+bounds.Width/2),(ushort)(bounds.Y+bounds.Height/2));
            var at = new DateTimeOffset(2026,10,26,5,59,59,TimeSpan.FromHours(8));
            while (!BeastDungeonRotation.Contains(BeastDungeonRotation.ServiceDay(at),dungeon)) at=at.AddDays(1);
            var clocks = new[] {new DailyBoosterTestClock(at),new DailyBoosterTestClock(at.AddDays(1))};
            var leases = new[] {alpha,beta};
            var sessions = leases.Select((owner,i) =>
            {
                var candidate = CharacterCreation.Prepare("BeastParty"+i,resources.Characters.Get(0),position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with
                    {Experience=resources.Growth.GetLevelStartExperience(95)},resources.Learning);
                repository.Create(owner,candidate with {Level=95,AcceptedStoryLevel=95,CompletedQuests=completed.ToArray(),InitialLearning=learning});
                var session = new GameSession(repository,resources.Towns,owner,questCatalog:resources.Quests,timeProvider:clocks[i],
                    items:resources.ItemResources.Items,goldLimits:resources.ItemResources.GoldLimits,pieces:resources.ItemResources.EpicPieces,
                    entry:gateway ? new(new(2,49),40,"[beast]") : null);
                session.Select(0,resumeTown:true); session.MarkReady(); return session;
            }).ToArray();
            var parties = new PartyService(repository);
            foreach (var session in sessions) session.CurrentParty = () => parties.Current(session);
            That(parties.Invite(sessions[0],sessions[1],sessions[1].Selected!.Id).Accepted &&
                parties.Respond(sessions[1],sessions[0].Selected!.Id,0,true).Accepted,"魔兽使用真实邀请与接受建立两人队伍");
            var service = new PartyDungeonService(parties,repository);
            That(service.BeginSelection(sessions[0]).Length==2,"魔兽两人共同进入生产选图");
            string State() => JsonSerializer.Serialize(leases.Select(owner=>repository.LoadCharacter(owner,0)).ToArray());
            int day = BeastDungeonRotation.ServiceDay(at);
            bool Uncharged() => leases.All(owner=>repository.LoadBeastDungeonUsed(owner,repository.LoadCharacter(owner,0).Id,day,310)==0);
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText=sql; command.ExecuteNonQuery();
            }
            PartyDungeonNotice[] Enter(Action? prepared=null) => service.Enter(sessions[0],(uint)dungeon,gateway ? (byte)0 : (byte)4,catalog,rooms,drops,
                resources.ItemResources.Items,resources.ItemResources.EpicPieces,entry =>
                {
                    prepared?.Invoke();
                    return dropPolicy.PrepareEntry(entry);
                },beastMode:true);
            string initial = State();
            bool advanced=false;
            That(Enter(()=> {if(!advanced){advanced=true;clocks[0].Advance(TimeSpan.FromSeconds(1));}}).Length==0 &&
                State()==initial && Uncharged(),"准备中跨06:00时全队拒绝，次数与角色状态均不写入");
            clocks[0].Advance(TimeSpan.FromSeconds(-1));
            ushort second=sessions[1].Selected!.Id;
            Sql($"CREATE TRIGGER fail_beast_party BEFORE UPDATE OF revision ON characters WHEN NEW.id={second} BEGIN SELECT RAISE(ABORT,'beast party late rollback'); END");
            Reject<SqliteException>(()=>Enter(),"魔兽第二队员入场提交晚期失败");
            That(State()==initial && Uncharged() && sessions.All(session=>session.Phase==SessionPhase.MapSelect),
                "第二队员失败回滚全员费用、次数与attempt，且不安装运行实例");
            Sql("DROP TRIGGER fail_beast_party");
            var entered=Enter();
            That(entered.Length==2 && entered.All(notice=>notice.Scene!.Entry.BeastMode && notice.Scene.Entry.BeastDay==day) &&
                sessions.All(session=>ReferenceEquals(session.PartyDungeon,sessions[0].PartyDungeon)),
                "队员时钟不同仍透传魔兽模式、共同日和共享实例");
            That(leases.All(owner=>repository.LoadBeastDungeonUsed(owner,repository.LoadCharacter(owner,0).Id,day,310)==1),
                "全队成功入场时逐人恰好扣一次共同日次数");
            var instance=sessions[0].PartyDungeon!;
            void Loaded()
            {
                That(instance.CompleteLoading(sessions[0]).Length==0 && instance.CompleteLoading(sessions[1]).Length==2,
                    "魔兽组队全员加载完成才释放共享屏障");
            }
            Loaded();
            var entry=entered[0].Scene!.Entry;
            var targets=new[] {entry.BossMap}.Concat(entry.Maze.MapSpecification!.Where(map=>map.Kind=="Beast" && map!=entry.BossMap));
            var cleared=new HashSet<DfoServer.GameWorld.Dungeons.DungeonPoint>();
            bool checkedRetry=false, checkedDeadline=false;
            foreach (var target in targets)
            {
                var route=QuestStoryRouteTests.Path(entry.Maze.StaticTopology,sessions[0].CurrentFormalDungeonScene()!.Snapshot.CurrentMap.Position,target.Position);
                ClearCurrent();
                foreach(var point in route.Skip(1))
                {
                    if(!checkedDeadline && cleared.Count>=3 && !cleared.Contains(point) &&
                        entry.Maze.MapSpecification!.Any(map=>map.Position==point && map.Kind=="Beast"))
                    {
                        var previous=sessions.Select(session=>session.CurrentFormalDungeonScene()!.Snapshot.CurrentMap).ToArray();
                        clocks[0].Advance(TimeSpan.FromSeconds(180));
                        That(instance.Move(sessions[0],(byte)point.X,(byte)point.Y,0).Length==0 &&
                            sessions.Select(session=>session.CurrentFormalDungeonScene()!.Snapshot.CurrentMap).SequenceEqual(previous),
                            "共同180秒边界拒绝全队新阶段，任何队员均不先移动");
                        clocks[0].Advance(TimeSpan.FromSeconds(-1));
                        That(instance.Move(sessions[0],(byte)point.X,(byte)point.Y,0).Length==2,
                            "边界前一秒使用报告者共同时间，偏移队员时钟不拆散队伍");
                        clocks[0].Advance(TimeSpan.FromSeconds(-179));
                        checkedDeadline=true;
                    }
                    else
                    That(instance.Move(sessions[0],(byte)point.X,(byte)point.Y,0).Length==2,"魔兽组队共用门墙、阶段和移动时刻");
                    Loaded(); ClearCurrent();
                }
                if(sessions[0].Phase==SessionPhase.Settlement) break;
            }
            That(checkedRetry && checkedDeadline && cleared.Count==6 && sessions.All(session=>session.Phase==SessionPhase.Settlement &&
                BeastDungeonRotation.StageRewardItems.All(id=>session.Selected!.Inventory.Items.Where(item=>item.Core.ItemId==id).Sum(item=>item.Core.Count)==1)),
                "两人六阶段全部通关，各自六种奖励恰好一份");

            void ClearCurrent()
            {
                var scene=sessions[0].CurrentFormalDungeonScene()!;
                foreach(var monster in scene.Monsters.Monsters.Where(row=>row.IsBlocking && row.CanReportDeath).OrderBy(row=>row.IsBoss))
                {
                    var observation=FormalDungeonSessionTests.Observation(scene,monster);
                    PartyDungeonNotice[] Kill()=>instance.ObserveDeath(sessions[0],monster.ObjectKey,
                        member=>member.ObserveFormalDungeonDeath(observation,kills,resources.Growth));
                    if(!checkedRetry && scene.Snapshot.CurrentMap.Kind=="Beast")
                    {
                        Sql($"CREATE TRIGGER fail_beast_death AFTER INSERT ON character_dungeon_growth_receipts WHEN NEW.character_id={second} BEGIN SELECT RAISE(ABORT,'beast death late rollback'); END");
                        Reject<SqliteException>(()=>Kill(),"魔兽共享死亡第二队员提交失败");
                        Sql("DROP TRIGGER fail_beast_death");
                        checkedRetry=true;
                    }
                    That(Kill().Length==2,"魔兽共享死亡可恢复未完成成员，不重复已提交成员");
                    string committed=State();
                    That(Kill().Length==0 && State()==committed,"共享死亡重复上报不重复经验与阶段奖励");
                }
                if(scene.Snapshot.CurrentMap.Kind=="Beast") cleared.Add(scene.Snapshot.CurrentMap.Position);
                That(cleared.Count==6 || sessions.All(session=>session.Phase==SessionPhase.Dungeon),
                    "全队未清完六阶段时不提前进入结算");
            }
        }
        finally {Directory.Delete(root,true);}
    }
}
