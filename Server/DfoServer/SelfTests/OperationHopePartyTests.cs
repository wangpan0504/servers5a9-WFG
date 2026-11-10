using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class OperationHopePartyTests
{
    public static void Run(PvfArchive pvf,ItemResources items)
    {
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var drops = SelfTestCatalogs.MonsterDrops(pvf);
        var quests = SelfTestCatalogs.Quests(pvf);
        var towns = DfoServer.GameWorld.Towns.TownCatalog.Load(pvf);
        var hope = OperationHopeCatalog.Load(pvf,dungeons,quests,rooms);
        var growth = SelfTestCatalogs.Growth(pvf,SelfTestCatalogs.SkillLearning(pvf));
        var kills = SelfTestCatalogs.KillExperience(pvf);
        var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
        var counts = DungeonIncountCatalog.Load(pvf);
        var adventure = new AdventureCatalog(pvf);
        var rewardCatalog=new OperationHopeRewardCatalog(pvf,items.Items);
        var rewards=new OperationHopeRewardPolicy(rewardCatalog,items.Items);
        foreach (int size in new[] {2,4})
            foreach(bool hidden in new[]{false,true}) Verify(size,hidden);

        void Verify(int size,bool hidden)
        {
            string root = Path.Combine(Path.GetTempPath(),"S5A9-hope-party-"+Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root,"game.db");
            var owners = new AccountOwners();
            var leases = Enumerable.Range(0,size).Select(i=>owners.Acquire("hope-party-"+i,()=>{})).ToArray();
            try
            {
                var clock = new HopeClock(DateTimeOffset.UtcNow);
                var repository = new CharacterRepository(path,quests,adventure,growth);
                var sessions = leases.Select((owner,i)=>
                {
                    new AuthenticationService(new AuthenticationRepository(path)).Create(owner.Account,"fixture-password");
                    var inventory=InventorySnapshot.Empty;
                    if(size==2 && !hidden && i==1)
                    {
                        var range=ItemSlotBoundService.Range(ItemKind.Equipment,inventory.Expansion);
                        inventory=inventory with {Items=[..Enumerable.Range(range.Start,range.End-range.Start+1).Select(slot=>
                            new InventoryItem(0,new(InventoryListType.Main,(short)slot),InventoryRewardGrantService.Create(items.Items.Get(10400))))]};
                    }
                    repository.Create(owner,new("HopeParty"+i,new(54,4,40,200),null)
                    {
                        Level=95,GrowType=(byte)(i==0?1:2),CompletedQuests=[6170],DungeonState=CharacterDungeonState.NewOrdinary,
                        Inventory=inventory,
                        InitialLearning=new(growth.GetLevelStartExperience(95),[new(0,10,0,[]),new(1,20,0,[])])
                    });
                    repository.InitializeAdventure(owner,adventure,clock.Now);
                    // 复用本轮2/4人实例，分别覆盖队长和非零槽队员疲劳不足，不另启动服务。
                    if (i == (size == 2 ? 0 : 1))
                    {
                        using var db = new SqliteConnection("Data Source="+path+";Pooling=False");
                        db.Open();
                        using var command = db.CreateCommand();
                        command.CommandText = "UPDATE character_fatigue SET used=maximum-3,remaining=3 WHERE account=$account";
                        command.Parameters.AddWithValue("$account",owner.Account);
                        command.ExecuteNonQuery();
                    }
                    var session = new GameSession(repository,towns,owner,questCatalog:quests,operationHope:hope,incounts:counts,timeProvider:clock);
                    session.Select(0,resumeTown:true);session.MarkReady();return session;
                }).ToArray();
                var parties = new PartyService(repository);
                foreach (var session in sessions)
                {
                    session.CurrentParty=()=>parties.Current(session);
                    session.HasMultiplePartyMembers=()=>parties.Current(session)?.Members.Length>1;
                }
                foreach (var session in sessions.Skip(1))
                    That(parties.Invite(sessions[0],session,session.Selected!.Id).Accepted &&
                        parties.Respond(session,sessions[0].Selected!.Id,0,true).Accepted,"希望真实邀请建立多人队伍");
                var service = new PartyDungeonService(parties,repository);
                That(!sessions[1].BeginPartyOperationHope(hope,dungeons,_=>0),"队员不能代替队长冻结全队路线");
                int failedDraws=0;
                Reject<InvalidOperationException>(()=>sessions[0].BeginPartyOperationHope(hope,dungeons,bound=>
                    bound==9 && ++failedDraws==size ? throw new InvalidOperationException("hope selection failure") : 0),
                    "末位成员抽签失败不留下半队选择");
                int missionDraws=0;
                int Draw(int bound)=>bound==100?(hidden?99:0):bound==9?(++missionDraws==1?0:8):bound==4 && hidden && size==4?3:0;
                That(sessions[0].BeginPartyOperationHope(hope,dungeons,Draw) && missionDraws==size,
                    "失败后整队重新准备，共享迷宫但各人任务独立");
                That(sessions[0].BeginPartyOperationHope(hope,dungeons,_=>throw new InvalidOperationException("unexpected reroll")),
                    "1598重复请求保留全员冻结选择，不重新抽签");
                That(service.BeginSelection(sessions[0]).Length==size,"希望队长15开启全员选图");
                var preparedProfessions=new HashSet<(byte,byte)>();
                (string Reason,byte Slot)? rejection = null;
                PartyDungeonNotice[] Enter()=>service.Enter(sessions[0],8523,hope.Difficulty,dungeons,rooms,drops,items.Items,items.EpicPieces,
                    entry=>
                    {
                        preparedProfessions.Add(entry.HopeRewardProfession!.Value);
                        return monster=>rewards.Extend(new("hope-equipment-fixture",[]),entry,monster);
                    },onRejected:(reason,slot)=>rejection=(reason,slot));
                string State()=>JsonSerializer.Serialize(leases.Select(owner=>repository.LoadCharacter(owner,0)));
                long Scalar(string sql)
                {
                    using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();
                    using var command=db.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());
                }
                void Sql(string sql)
                {
                    using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();
                    using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();
                }
                string insufficient = State();
                int failedIndex = size == 2 ? 0 : 1;
                byte failedSlot = parties.Current(sessions[0])!.Members.Single(member=>ReferenceEquals(member.Actor.Session,sessions[failedIndex])).Slot;
                That(Enter().Length == 0 && rejection == ("start-fatigue-insufficient:4",failedSlot) &&
                    State() == insufficient && Scalar("SELECT count(*) FROM character_dungeon_incount") == 0 &&
                    sessions.All(session=>session.PartyDungeon is null && session.Phase == SessionPhase.MapSelect),
                    "希望疲劳不足精确报告实际队长或非零成员槽，全队不扣费、不接任务、不安装实例");
                clock.Now = clock.Now.AddDays(1);
                That(sessions[failedIndex].AdvanceFatigueDay(clock.Now) is not null &&
                    sessions[0].BeginPartyOperationHope(hope,dungeons,_=>throw new InvalidOperationException("unexpected recovery reroll")) &&
                    missionDraws == size,"疲劳恢复后刷新全队原选择的角色版本，不重新抽取任务或迷宫");
                string original=State();
                Sql($"CREATE TRIGGER fail_hope_member AFTER INSERT ON character_active_main_quests WHEN NEW.character_id={sessions[^1].Selected!.Id} BEGIN SELECT RAISE(ABORT,'hope party late rollback'); END");
                Reject<SqliteException>(()=>Enter(),"末位希望成员接取SQL失败");
                That(State()==original && Scalar("SELECT count(*) FROM character_dungeon_incount")==0 &&
                    sessions.All(s=>s.PartyDungeon is null),"全队次数/疲劳/attempt/任务原子回滚，不安装半份实例");
                Sql("DROP TRIGGER fail_hope_member");
                var entry=Enter();
                That(entry.Length==size,"希望组队原子入场："+sessions[0].LastFormalDungeonRejection);
                That(preparedProfessions.SetEquals(new[]{((byte)0,(byte)1),((byte)0,(byte)2)}) && entry.All(n=>
                    n.Scene!.Entry.HopeRewardProfession==(n.Scene.Character.Job,n.Scene.Character.GrowType)),
                    "不同转职队员实际重新准备本人掉落策略，不沿用队长职业池");
                That(entry.Select(n=>n.Scene!.Snapshot.Identity.InstanceId).Distinct().Count()==1 &&
                    entry.Select(n=>n.Scene!.Entry.Maze.SourceOrdinal).Distinct().Count()==1 &&
                    entry.Select(n=>n.Scene!.Entry.Hope!.Acceptance.AcceptanceId).Distinct().Count()==size,
                    "同一物理迷宫和实例，逐人保留独立任务接取身份");
                That(entry.All(n=>n.Scene!.EntryFatigueCharged==4 && n.Scene.EnterCounts.Length>0) &&
                    Scalar("SELECT count(*) FROM character_active_main_quests WHERE quest_id BETWEEN 6171 AND 6179")==size,
                    "每人首区域扣4疲劳并发布实际次数与原任务："+JsonSerializer.Serialize(entry.Select(n=>new{
                        n.Scene!.EntryFatigueCharged,Counts=n.Scene.EnterCounts.Length,Tasks=n.Scene.Character.ActiveMainQuests.Length})));
                var instance=sessions[0].PartyDungeon!;
                var maze=hope.Mazes[entry[0].Scene!.Entry.Maze.SourceOrdinal];
                That(maze.HiddenBoss==hidden,"共同迷宫准确保留本次普通或隐藏分支");
                var target=hope.Missions[sessions[0].Selected!.ActiveMainQuests.Single(q=>hope.Missions.ContainsKey(q.QuestId)).QuestId];
                var route=Route(maze.Layout.Starts[0],false);
                var initialFatigue=entry.ToDictionary(n=>n.Recipient.Actor.Character.Id,n=>n.Scene!.Character.Fatigue!.Remaining+4);
                bool sawHidden=false;
                foreach(var point in route)
                {
                    foreach(var session in sessions.Take(size-1))
                        That(instance.CompleteLoading(session).Length==0,"等待全部成员加载，不提前发布房间就绪");
                    That(instance.CompleteLoading(sessions[^1]).Length==size,"最后成员加载后共同释放屏障");
                    var scene=sessions[0].CurrentFormalDungeonScene()!;
                    foreach(var monster in scene.Monsters.Monsters.Where(m=>m.CanReportDeath && m.IsBlocking).OrderBy(m=>m.IsBoss))
                    {
                        var observation=FormalDungeonSessionTests.Observation(scene,monster);
                        var deaths=instance.ObserveDeath(sessions[0],monster.ObjectKey,s=>s.ObserveFormalDungeonDeath(observation,kills,growth));
                        That(deaths.Length==size,
                            "一名成员真实死亡报告推进每人的原任务与成长账本");
                        if(monster.MonsterId==107001276)
                        {
                            sawHidden=true;
                            That(deaths.All(n=>n.Death!.Drops.Count(g=>g.Core.Kind==DfoServer.GameWorld.Items.ItemKind.Equipment)==2 &&
                                n.Death.Drops.Where(g=>g.Core.ItemId==10307438).Select(g=>g.Core.Count).SequenceEqual(new[]{9,9,9}) &&
                                n.Death.Drops.All(g=>g.Core.Kind==DfoServer.GameWorld.Items.ItemKind.Equipment || g.Core.ItemId is 10307438 or 490011160)),
                                "真实隐藏领主死亡逐人冻结两件装备和三堆材料");
                            That(instance.ObserveDeath(sessions[0],monster.ObjectKey,s=>s.ObserveFormalDungeonDeath(observation,kills,growth)).Length==0,
                                "重复隐藏死亡不能再次生成装备");
                        }
                    }
                    if(point==route[^1]) break;
                    var next=route[Array.IndexOf(route,point)+1];
                    That(instance.Move(sessions[0],(byte)next.X,(byte)next.Y,0).Length==size,"全队按原Hope单向区域路线前进");
                }
                That(sessions.All(s=>s.Phase==SessionPhase.Settlement &&
                    initialFatigue[s.Selected!.Id]-s.Selected.Fatigue!.Remaining==16),"多人全程每人仅扣16疲劳且真实终点结算");
                That(sawHidden==hidden,"隐藏Boss可以位于中途区域或终点，真实路线均经过并击杀");
                foreach(var session in sessions)
                {
                    var active=session.Selected!.ActiveMainQuests.Single(q=>hope.Missions.ContainsKey(q.QuestId));
                    if(active.ConditionState!=0) continue;
                    var receipt=instance.Execute(_=>session.FinishOperationHope(active.QuestId,ushort.MaxValue,1,quests,parameters,
                        items.Items,items.GoldLimits,items.EpicPieces,growth));
                    That(receipt is {Commit.Duplicate:false} && receipt.Commit.Rewards.Sum(r=>r.GrantedCount)==60,"完成本人目标后独立领取60材料");
                    That(instance.Execute(_=>session.FinishOperationHope(active.QuestId,ushort.MaxValue,1,quests,parameters,
                        items.Items,items.GoldLimits,items.EpicPieces,growth)) is {Commit.Duplicate:true},"多人重传领奖不再次发奖");
                }
                That(Scalar("SELECT count(*) FROM character_operation_hope_reward_receipts")>=1,"至少覆盖队长真实目标及领奖事务");
                var cards=new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf),items.DungeonDropItems,items.Items,
                    items.GoldLimits,items.EpicPieces,random:_=>0,prices:SelfTestCatalogs.CardPrices(pvf),hopeRewards:rewards);
                foreach(var session in sessions)
                {
                    var settled=instance.Execute(_=>session.SettleFormalDungeon(session.Selected!.Id,99,99,
                        DungeonSettlementProtocolTests.Report(sessions.Select(s=>(s.Selected!.Id,(byte)99)).ToArray()),
                        dungeons,SelfTestCatalogs.ClearExperience(pvf),growth,cards,reportMembers:sessions.Select(s=>s.Selected!.Id).ToArray()));
                    That(settled.Outcome==DungeonRunOutcome.Accepted,"每名成员正式结算独立冻结材料和装备免费牌");
                    session.AdvanceFormalDungeonCards(false);session.AdvanceFormalDungeonCards(true);
                    var beforeCard=session.Selected!.Inventory;
                    int Total(InventorySnapshot snapshot,int id)=>snapshot.Items.Where(i=>i.Core.ItemId==id).Sum(i=>i.Core.Count);
                    byte seat=(byte)Array.IndexOf(sessions,session);
                    long mails=Scalar("SELECT count(*) FROM mailbox_attachments");
                    var claimed=session.SelectFormalDungeonFreeCard(0,seat);
                    That(claimed is not null && settled.Card is {HopeEquipment:not null,HopeSealingItem:not null},"每人原槽领取材料、完整装备与黄金碎片");
                    var afterCard=session.Selected!.Inventory;
                    bool full=size==2 && !hidden && seat==1;
                    That(Total(afterCard,10307438)-Total(beforeCard,10307438)==settled.Card!.Item!.Count &&
                        Total(afterCard,490011160)-Total(beforeCard,490011160)==1 &&
                        afterCard.Items.Count(i=>i.Core.Kind==ItemKind.Equipment)-beforeCard.Items.Count(i=>i.Core.Kind==ItemKind.Equipment)==(full?0:1) &&
                        Scalar("SELECT count(*) FROM mailbox_attachments")==mails+(full?1:0),
                        "材料与黄金碎片实际入包，满装备页只把完整装备转永久邮件");
                    string afterClaim=State();
                    That(session.SelectFormalDungeonFreeCard(0,seat) is {Duplicate:true} && State()==afterClaim &&
                        Scalar("SELECT count(*) FROM mailbox_attachments")==mails+(full?1:0),"重传不再发材料、黄金、装备或邮件");
                }
                original=State();
                That(sessions[^1].Selected!.ActiveMainQuests.Any(q=>hope.Missions.ContainsKey(q.QuestId)),"实际路线保留未达成本人目标，覆盖退出清理");
                Sql($"CREATE TRIGGER fail_hope_exit AFTER DELETE ON character_active_main_quests WHEN OLD.character_id={sessions[^1].Selected!.Id} BEGIN SELECT RAISE(ABORT,'hope exit rollback'); END");
                Reject<SqliteException>(()=>instance.ResultSelection(sessions[0]),"末位成员清理失败整队退出回滚");
                That(State()==original && sessions.All(s=>s.Phase==SessionPhase.Settlement && ReferenceEquals(s.PartyDungeon,instance)),
                    "退出失败保留位置、资产、个人任务与原实例");
                Sql("DROP TRIGGER fail_hope_exit");
                That(instance.ResultSelection(sessions[0]).Length==size && sessions.All(s=>s.PartyDungeon is null &&
                    s.Selected!.ActiveMainQuests.All(q=>!hope.Missions.ContainsKey(q.QuestId))),"成功全队选图只清理各自本次任务，允许下一次入场");
                if(size==2 && !hidden)
                {
                    That(sessions[0].BeginPartyOperationHope(hope,dungeons,_=>0) && Enter().Length==size,"退出后可开始下一次真实希望实例");
                    var loading=sessions[0].PartyDungeon!;
                    That(loading.CompleteLoading(sessions[0]).Length==0,"退队边界保留未齐加载屏障");
                    That(service.Depart(sessions[1]).Error==0 && sessions[1].Selected!.ActiveMainQuests.All(q=>!hope.Missions.ContainsKey(q.QuestId)) &&
                        sessions[0].Selected!.ActiveMainQuests.Any(q=>hope.Missions.ContainsKey(q.QuestId)) &&
                        ReferenceEquals(sessions[0].PartyDungeon,loading) && sessions[0].Phase==SessionPhase.Dungeon,
                        "加载中退队只清自己的本次任务，幸存者任务及实例保留并释放加载屏障");
                    That(service.Depart(sessions[0]).Error==0 && sessions[0].Selected!.ActiveMainQuests.All(q=>!hope.Missions.ContainsKey(q.QuestId)),
                        "最后一人退队仍清理自己的本次接取");
                }

                DungeonPoint[] Route(DungeonPoint start,bool met)
                {
                    var maps=maze.Layout.MapSpecification!;
                    var queue=new Queue<(DungeonPoint Point,bool Met,bool Hidden,DungeonPoint[] Path)>();
                    var visited=new HashSet<(DungeonPoint,bool,bool)>();queue.Enqueue((start,met,false,[]));
                    while(queue.TryDequeue(out var node))
                    {
                        var map=maps.Single(m=>m.Position==node.Point);
                        bool hit=node.Met || maze.MissionMaps[target.Quest.Id].Contains(map.Id);
                        bool hiddenMap=rooms.Get(map,requirePassiveDefinitions:false).Monsters.Any(m=>m.MonsterId==107001276);
                        bool metHidden=node.Hidden || hiddenMap;
                        if(!visited.Add((node.Point,hit,metHidden))) continue;
                        DungeonPoint[] travel=[..node.Path,node.Point];
                        if(hit && maze.ClearMaps.Contains(map.Id) && (!hidden || metHidden)) return travel;
                        foreach(var next in maps)
                            if(OperationHopePlan.IsDeclaredTransition(hope,maze.Layout.SourceOrdinal,node.Point,next.Position))
                                queue.Enqueue((next.Position,hit,metHidden,travel));
                    }
                    throw new InvalidDataException("希望组队没有经过任务目标的终点路线");
                }
            }
            finally
            {
                foreach(var lease in leases) lease.Dispose();
                SqliteConnection.ClearAllPools();
                if(Directory.Exists(root)) TestFiles.DeleteDirectory(root,true);
            }
        }
    }

    sealed class HopeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
}
