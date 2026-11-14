using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using DfoServer.Testing.Inventory;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class OrdinaryLukePartyTests
{
    internal static (Location Position, int[] Completed) Entrance(TcpTestResources resources)
    {
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 5101));
        var done = new HashSet<int>(area.Quests); done.UnionWith([3778,4872,8682]);
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        var options = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.Where(option => option.DungeonId == 5101).ToArray();
        if (!options.Any(option => option.QuestId == -1)) done.Add(options.First(option => !option.InProgress).QuestId);
        done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        return (new(area.Town,area.Area,(ushort)(bounds.X+ bounds.Width/2),(ushort)(bounds.Y+bounds.Height/2)),done.ToArray());
    }

    internal static void Run(TcpTestResources resources)
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(),"S5A9-luke-party-"+Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root,"game.db");
        var owners = new AccountOwners();
        using var alpha = owners.Acquire("luke-pa",()=>{});
        using var beta = owners.Acquire("luke-pb",()=>{});
        try
        {
            var repository = new CharacterRepository(path,resources.Quests);
            var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
            var rooms = SelfTestCatalogs.Rooms(resources.Archive);
            var monsters = SelfTestCatalogs.MonsterDrops(resources.Archive);
            var kills = SelfTestCatalogs.KillExperience(resources.Archive);
            var growth = resources.Growth;
            var items = resources.ItemResources;
            var drops = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(resources.Archive),items.DungeonDropItems,
                monsters.Get,items.Items.Get,items.DungeonSpecialDrops);
            var (position,done) = Entrance(resources);
            var clocks = new[] {new DailyBoosterTestClock(new DateTimeOffset(2026,10,26,5,59,59,TimeSpan.FromHours(8))),
                new DailyBoosterTestClock(new DateTimeOffset(2026,10,27,6,0,0,TimeSpan.FromHours(8)))};
            var leases = new[] {alpha,beta};
            var sessions = leases.Select((owner,i) =>
            {
                var candidate = CharacterCreation.Prepare("LukeParty"+i,resources.Characters.Get(0),position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with {Experience=growth.GetLevelStartExperience(95)},resources.Learning);
                repository.Create(owner,candidate with {Level=95,AcceptedStoryLevel=95,CompletedQuests=done,InitialLearning=learning,
                    Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.Add(new(0, new(InventoryListType.Main, 3),
                        InventoryRewardGrantService.Create(items.Items.Get(400360000)) with { Durability = (ushort)(25 - i * 10) })) } });
                var session = new GameSession(repository,resources.Towns,owner,questCatalog:resources.Quests,timeProvider:clocks[i],items:items.Items);
                session.Select(0,resumeTown:true); session.MarkReady(); return session;
            }).ToArray();
            var energyStart = sessions.Select(session => session.Selected!).ToArray();
            var parties = new PartyService(repository, inviteAntiEvil: _ => 0);
            foreach (var session in sessions) session.CurrentParty = () => parties.Current(session);
            That(parties.Invite(sessions[0],sessions[1],sessions[1].Selected!.Id).Accepted &&
                parties.Respond(sessions[1],sessions[0].Selected!.Id,0,true).Accepted,"卢克领域夹具经原邀请/接受建立两名真实owner队伍");
            var service = new PartyDungeonService(parties,repository);
            That(service.BeginSelection(sessions[0]).Length == 2,"两人进入共同选图");
            string State() => JsonSerializer.Serialize(leases.Select(owner=>repository.LoadCharacter(owner,0)).ToArray());
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source="+path); db.Open();
                using var command = db.CreateCommand(); command.CommandText=sql; command.ExecuteNonQuery();
            }
            PartyDungeonNotice[] Enter(uint id=5101,Action? prepared=null) => service.Enter(sessions[0],id,2,dungeons,rooms,monsters,
                items.Items,items.EpicPieces,entry => { prepared?.Invoke(); return drops.PrepareEntry(entry); });
            string initial = State();
            That(Enter(prepared:()=>clocks[0].Advance(TimeSpan.FromSeconds(1))).Length==0 && State()==initial &&
                leases.All((owner)=>repository.LoadOrdinaryLukeProgress(owner,repository.LoadCharacter(owner,0).Id)==default),
                "准备期间跨06:00整队拒绝，费用/疲劳/attempt/日进度全不写");
            clocks[0].Advance(TimeSpan.FromSeconds(-1));
            int day = QuestCycle.Day(clocks[0].GetUtcNow());
            ushort a = sessions[0].Selected!.Id, b = sessions[1].Selected!.Id;
            Sql($"INSERT INTO character_luke_normal_progress VALUES({a},'luke-pa',{day},1)");
            That(Enter(5102).Length==0 && State()==initial,"队长有前序、队员没有时整队拒绝，不由队长代授权");
            Sql($"DELETE FROM character_luke_normal_progress WHERE character_id={a}");
            Sql($"CREATE TRIGGER fail_luke_party BEFORE UPDATE OF revision ON characters WHEN NEW.id={b} BEGIN SELECT RAISE(ABORT,'party entry late rollback'); END");
            Reject<SqliteException>(()=>Enter(),"第二成员晚SQL失败");
            That(State()==initial && leases.All(owner=>repository.LoadOrdinaryLukeProgress(owner,repository.LoadCharacter(owner,0).Id)==default),
                "全员原SQL事务回滚，第一成员不得留下日资格和扣费");
            Sql("DROP TRIGGER fail_luke_party");
            var entry = Enter();
            That(entry.Length==2 && entry.All(n=>n.Scene!.Entry.OrdinaryLukeDay==day && n.Scene.Entry.CoinLimit is null),
                "不同成员时钟仍冻结队长同一入场日；不新增多人12币策略");
            for (int pass=0; pass<2; pass++)
            {
                var instance = sessions[0].PartyDungeon!;
                var route = QuestStoryRouteTests.Path(entry[0].Scene!.Entry.Maze.StaticTopology,entry[0].Scene!.Entry.StartMap.Position,entry[0].Scene!.Entry.BossMap.Position);
                bool checkedFailure=false, checkedPersonal=false;
                foreach (var point in route)
                {
                    instance.CompleteLoading(sessions[0]);
                    var loaded = instance.CompleteLoading(sessions[1]);
                    That(loaded.Length==2,"全员原加载屏障");
                    foreach (var notice in loaded)
                    {
                        var current = notice.Scene!.Character;
                        var beforeEnergy = energyStart.Single(role => role.Id == current.Id);
                        var source = beforeEnergy.Inventory.Find(new(InventoryListType.Main, 3))!;
                        int fatigueLost = beforeEnergy.Fatigue!.Remaining - current.Fatigue!.Remaining;
                        That(fatigueLost > 0 && current.Inventory.Find(source.Position) == source with
                            { Core = source.Core with { Durability = (ushort)Math.Max(0, source.Core.Durability - fatigueLost) } },
                            "组队加载回执按成员本人真实疲劳同步独立能量，不串用队长或队友的库存");
                    }
                    var scene = sessions[0].CurrentFormalDungeonScene()!;
                    foreach (var monster in scene.Monsters.Monsters.Where(m=>m.IsBlocking && m.CanReportDeath && !m.IsFriendly).OrderBy(m=>m.IsBoss))
                    {
                        bool daily=items.DungeonSpecialDrops.OrdinaryLukeCards.ContainsKey(monster.MonsterId);
                        bool personal=pass==0 && daily && checkedFailure && !checkedPersonal;
                        Guid seeded=Guid.NewGuid();
                        if (personal) Sql($"INSERT INTO character_luke_normal_drop_claims VALUES({a},'luke-pa',{day},{monster.MonsterId},'{seeded:N}','{Guid.NewGuid():N}')");
                        var observation=FormalDungeonSessionTests.Observation(scene,monster);
                        PartyDungeonNotice[] Kill()=>instance.ObserveDeath(sessions[0],monster.ObjectKey,
                            member=>member.ObserveFormalDungeonDeath(observation,kills,growth));
                        if (pass==0 && daily && !checkedFailure)
                        {
                            Sql($"CREATE TRIGGER fail_luke_death AFTER INSERT ON character_dungeon_growth_receipts WHEN NEW.character_id={b} BEGIN SELECT RAISE(ABORT,'party death late rollback'); END");
                            Reject<SqliteException>(()=>Kill(),"死亡第二成员资格写入后晚失败");
                            var first=repository.LoadOrdinaryLukeDropEvent(alpha,a,day,monster.MonsterId);
                            That(first is not null && repository.LoadOrdinaryLukeDropEvent(beta,b,day,monster.MonsterId) is null,
                                "已提交成员保留资格，失败成员SQL全回滚，不伪造全队原子死亡");
                            Sql("DROP TRIGGER fail_luke_death");
                            var resumed=Kill();
                            That(resumed.Length==2 && repository.LoadOrdinaryLukeDropEvent(alpha,a,day,monster.MonsterId)==first &&
                                repository.LoadOrdinaryLukeDropEvent(beta,b,day,monster.MonsterId) is not null,"重试完成原pending，不重写首位成员event");
                            checkedFailure=true;
                        }
                        else
                        {
                            var notices=Kill();
                            That(notices.Length==2,"各成员独立提交正式死亡");
                            if (pass==1 && daily) That(notices.All(n=>n.Death!.Drops.IsEmpty),"同日跨Run各人的同源怪都已消费，不重新抽正常地面");
                            if (personal)
                            {
                                That(notices.Single(n=>n.Recipient.Actor.Character.Id==a).Death!.Drops.Length==0 &&
                                    repository.LoadOrdinaryLukeDropEvent(alpha,a,day,monster.MonsterId)==seeded &&
                                    repository.LoadOrdinaryLukeDropEvent(beta,b,day,monster.MonsterId) is not null,
                                    "已领取者只有本人无正常地面物，不阻断队友首击资格");
                                checkedPersonal=true;
                            }
                        }
                        string committed=State();
                        That(Kill().Length==0 && State()==committed,"组队重传不再抽、不再成长或占资格");
                    }
                    if (point==route[^1]) break;
                    var next=route[Array.IndexOf(route,point)+1];
                    That(instance.Move(sessions[0],(byte)next.X,(byte)next.Y,0).Length==2,"共同清房后正常过图");
                }
                if(pass==0) That(checkedFailure && checkedPersonal,"实际5101路线覆盖不同资格与部分SQL失败");
                var clears=SelfTestCatalogs.ClearExperience(resources.Archive);
                var cards=new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(resources.Archive),items.DungeonDropItems,
                    items.Items,items.GoldLimits,items.EpicPieces,prices:SelfTestCatalogs.CardPrices(resources.Archive));
                foreach (var session in sessions)
                {
                    var result=instance.Execute(_=>session.SettleFormalDungeon(session.Selected!.Id,99,99,DungeonSettlementProtocolTests.Report(sessions.Select(m=>(m.Selected!.Id,(byte)99)).ToArray()),dungeons,clears,growth,cards,
                        reportMembers:sessions.Select(m=>m.Selected!.Id).ToArray()));
                    That(result.Outcome==DungeonRunOutcome.Accepted,"各成员真实结算与翻牌独立");
                    session.AdvanceFormalDungeonCards(false); session.AdvanceFormalDungeonCards(true);
                    That(session.SelectFormalDungeonFreeCard(0,(byte)Array.IndexOf(sessions,session)) is not null,"各人按原队伍槽独立领取本次免费牌");
                }
                That(repository.LoadOrdinaryLukeProgress(alpha,a)==new OrdinaryLukeProgress(day,1) &&
                    repository.LoadOrdinaryLukeProgress(beta,b)==new OrdinaryLukeProgress(day,1),"实际通关分别推进两名角色入场日，不采用偏移队员时钟");
                if(pass==0) { entry=Enter(); That(entry.Length==2,"同日整队重进已解锁图"); }
            }
            clocks[0].Advance(TimeSpan.FromDays(1));
            entry=Enter();
            int nextDay=QuestCycle.Day(clocks[0].GetUtcNow());
            That(entry.Length==2 && entry.All(n=>n.Scene!.Entry.OrdinaryLukeDay==nextDay),"次日仍由队长冻结共同新日");
            var nextInstance=sessions[0].PartyDungeon!;
            var nextRoute=QuestStoryRouteTests.Path(entry[0].Scene!.Entry.Maze.StaticTopology,entry[0].Scene!.Entry.StartMap.Position,entry[0].Scene!.Entry.BossMap.Position);
            bool detached=false;
            foreach(var point in nextRoute)
            {
                nextInstance.CompleteLoading(sessions[0]); nextInstance.CompleteLoading(sessions[1]);
                var scene=sessions[0].CurrentFormalDungeonScene()!;
                foreach(var monster in scene.Monsters.Monsters.Where(m=>m.IsBlocking && m.CanReportDeath && !m.IsFriendly).OrderBy(m=>m.IsBoss))
                {
                    bool daily=items.DungeonSpecialDrops.OrdinaryLukeCards.ContainsKey(monster.MonsterId);
                    if(daily) { nextInstance.Detach(sessions[1]); detached=true; }
                    var observation=FormalDungeonSessionTests.Observation(scene,monster);
                    var notices=nextInstance.ObserveDeath(sessions[0],monster.ObjectKey,m=>m.ObserveFormalDungeonDeath(observation,kills,growth));
                    if(daily)
                    {
                        That(notices.Length==1 && repository.LoadOrdinaryLukeDropEvent(alpha,a,nextDay,monster.MonsterId) is not null &&
                            repository.LoadOrdinaryLukeDropEvent(beta,b,nextDay,monster.MonsterId) is null &&
                            repository.LoadOrdinaryLukeDropEvent(beta,b,day,monster.MonsterId) is not null,
                            "退离成员不由队长代占新日资格；在场成员新旧日独立，旧资格不被覆盖");
                        break;
                    }
                }
                if(detached) break;
                var next=nextRoute[Array.IndexOf(nextRoute,point)+1]; nextInstance.Move(sessions[0],(byte)next.X,(byte)next.Y,0);
            }
            That(detached,"真实奖励源怪验证退离边界");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if(Directory.Exists(root)) TestFiles.DeleteDirectory(root,true);
        }
        Console.WriteLine($"PASS {Count-before} 普通卢克组队日进度与个人首击合同断言");
    }
}
