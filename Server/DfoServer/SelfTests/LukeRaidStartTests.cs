using System.Collections.Immutable;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeRaidStartTests
{
    public static async Task RunCoreAsync()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-luke-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        List<AccountOwners.Lease> leases = []; List<GameSession> sessions = [];
        try
        {
            var origin = new Location(1, 0, 10, 10); var landing = new Location(32, 4, 12, 11);
            var map = new TownMap("synthetic/start.map", [new(new(0, 0, 30, 30), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1,0)] = new(1,0,1,[],[],[],origin,map,[],[]),
                [(32,4)] = new(32,4,1,[],[],[],null,map with { Links=[new(new(10,6,4,10),1,0,[],[])] },[],[]) }, origin, origin, new(0,[],[]), [], new Dictionary<uint,uint[]>());
            var owners = new AccountOwners(); var raids = new RaidRegistry();
            var repository = new CharacterRepository(Path.Combine(root, "game.db")); var parties = new PartyService(repository);
            var pieces = new EpicPieceCatalog("[piece list]\n9001 9002\n[/piece list]");
            var script = new RaidScript("synthetic/luke-start.etc", new(32,4), new(100,1), null,
                ImmutableDictionary<string,int>.Empty, [new("first",[],[],[]),new("second",[],[],[])], [], []);
            var definition = new RaidDefinition(1, script.Path, 8, 1, 2) { Resources = new(script,[],[],[],[],[]) };
            var service = new LukeRaidStart(raids, parties, repository, towns, pieces, new("synthetic/start-only",script));
            foreach (var (symbol,id) in new (string,int)[] {
                ("[kill all character]",102),("[kick out character]",103),("[birth sanctum object]",117),
                ("[extinction sanctum object]",118),("[spread sanctum object]",119),("[card set pattern a]",130),
                ("[card set pattern b]",131),("[card set pattern c]",132),("[energy control open]",133),
                ("[energy control effect]",138),("[energy save effect]",139),("[unknown future symbol]",999) })
            {
                var unsafeScript=script with { Symbols=script.Symbols.Add(symbol,id),
                    Phases=script.Phases.SetItem(0,script.Phases[0] with {Rules=[new([], [new("[set]",0,null,symbol,null,null,[1])])]}) };
                var unsafeDefinition=definition with {Resources=definition.Resources! with {Script=unsafeScript}};
                var unsafeService=new LukeRaidStart(raids,parties,repository,towns,pieces,new("synthetic/unresolved-native-consumer",unsafeScript));
                That(unsafeService.UnavailableReason(unsafeDefinition)=="luke-execution-symbol-consumer-unavailable",
                    "未闭合/未来symbol写入不能借583触发原生行为："+symbol);
                That(LukeRaidStart.RuleRejection(unsafeScript.Phases[0].Rules[0], unsafeScript, unsafeScript,
                    new HashSet<int>(), new HashSet<int>(), timersEnabled: false) == unsafeService.UnavailableReason(unsafeDefinition),
                    "逐条能力查询与实际开战拒绝共用同一合同：" + symbol);
            }

            foreach(var (symbol,id) in new (string,int)[]{("[temple clear]",122),("[altar clear]",124),("[altar enterance clear]",140)})
            {
                var counted=script with {Symbols=script.Symbols.Add(symbol,id),Phases=script.Phases.SetItem(0,script.Phases[0] with
                    {Rules=[new([], [new("[set]",0,null,symbol,null,null,[1])])]})};
                var countedDefinition=definition with {Resources=definition.Resources! with {Script=counted}};
                That(new LukeRaidStart(raids,parties,repository,towns,pieces,new("synthetic/known-counter",counted))
                    .UnavailableReason(countedDefinition) is null,"精确已登记计数符号不被消费者guard误伤");
            }
            var actors = Enumerable.Range(0,10).Select(index =>
            {
                var owner = owners.Acquire("luke-start-"+index, () => {}); leases.Add(owner);
                repository.Create(owner, new("LkStart"+index,origin,origin) { Level=90,CompletedQuests=[2903,4872],
                    Inventory=new([new InventoryItem(0,new(InventoryListType.Main,121),new(100,ItemKind.Material,2))]) });
                var session = new GameSession(repository,towns,owner,entry:new(new(2,20),32,"[raid]")); sessions.Add(session);
                session.Select(0); session.MarkReady(); session.CurrentParty=()=>parties.Current(session);
                return session.CaptureRaidActor(raids)!;
            }).ToArray();
            LukeRaidRunTests.RunCore(actors.Take(8).ToArray());
            LukeRaidRevivalTests.RunCore(actors.Take(8).ToArray());
            LukeRaidClockTests.RunCore(actors.Take(8).ToArray());
            LukeRaidObjectTests.RunCore(actors.Take(8).ToArray());
            LukeFacilityBridgeTests.RunCore(actors.Take(8).ToArray());
            LukeRaidCompletionTests.RunCore(actors.Take(8).ToArray());
            await LukeRewardRejectionTests.RunAsync(actors[8],root);
            var leader=actors[0]; raids.Create(leader,1,"start","start"u8.ToArray(),8,1);
            for(int index=1;index<8;index++)
            { raids.Invite(leader,actors[index],0); That(raids.Respond(actors[index],leader.Character,true).Accepted,"Luke通过原邀请加入"); }
            for(int index=0;index<8;index++) raids.Assign(leader,actors[index].Character,(byte)(index/4+1));
            var raid=raids.Find(leader)!;
            That(service.UnavailableReason(definition) is null&&!RaidProjection.CanStart(raid)&&RaidProjection.CanStart(raid,enableLuke:true),
                "执行策略已核且完整编队才可显式投影Luke能力；默认仍关闭");
            That(!RaidProjection.CanStart(raid with {Members=raid.Members.SetItem(0,raid.Members[0] with {Squad=0})},enableLuke:true),
                "有效执行策略不越过未编队门槛");
            void MakeParty(int first,int count,byte squad)
            {
                That(parties.SetInformation(sessions[first],0,RaidPreparationPlan.NativePartySettings(squad)).Accepted,"建立精确594设置");
                for(int index=first+1;index<first+count;index++)
                { That(parties.Invite(sessions[first],sessions[index],0).Accepted && parties.Respond(sessions[index],actors[first].Character,0,true).Accepted,"普通队伍真实邀请接受"); }
            }
            MakeParty(0,4,1); MakeParty(4,4,2); MakeParty(8,2,1);
            var existing=parties.Current(sessions[0])!; var other=parties.Current(sessions[4])!;
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var stream=new MemoryStream();
            await using var sender=new GameConnectionSender(leader.Owner,stream,stop,0,(_,_,_,_,_)=>{});
            var context=new GameConnectionContext(new(1,IPAddress.Loopback,stream,stop),leader.Owner,leader.Session,sender);
            var commands=new GameCommandRegistry();
            Func<Task> action=()=>Task.CompletedTask;
            commands.Register(CmdPacketType.START_RAID,async (_,_)=>{await action();return CommandResult.Handled;},nameof(LukeRaidStartTests)); commands.Seal();
            Task Dispatch()=>commands.DispatchAsync(context,new(1,666,[]));
            That(RaidStartCommandScope.TryEnter(context,raid) is null,"未取得真实Dispatch门禁不能伪造跳锁证明");
            var gates=new[]{other.Commands}.Concat(sessions.Take(8).Skip(1).Select(s=>s.CommandGate)).ToArray();
            foreach(var blocked in gates)
            {
                blocked.Wait();
                try
                {
                    action=()=>{That(RaidStartCommandScope.TryEnter(context,raid) is null,"每个扩组取得位置Busy立即拒绝");return Task.CompletedTask;};
                    await Dispatch().WaitAsync(TimeSpan.FromSeconds(2));
                    That(sessions[8].CommandGate.Wait(0),"团内Busy不占无关队伍成员门禁"); sessions[8].CommandGate.Release();
                }
                finally{blocked.Release();}
                That(gates.All(g=>g.CurrentCount==1),"Busy逆序释放此前所有已取门禁");
            }
            action=()=>
            {
                That(context.DispatchGates!.Holds(context,existing.Commands)&&!context.DispatchGates.Holds(context,other.Commands),
                    "Luke选图只能扩取当前Dispatch已持普通队伍的成员，另一队门禁不构成证明");
                var scope=RaidStartCommandScope.TryEnter(context,raid)!;
                That(scope.IsCurrent(),"真实Dispatch证明允许跳过同对象本人及本队gate");
                scope.Dispose(); scope.Dispose();
                That(!scope.IsCurrent(),"释放扩组后即使Dispatch仍活跃也不可提交，重复Dispose不双释放");
                return Task.CompletedTask;
            }; await Dispatch();
            That(context.DispatchGates is null && existing.Commands.CurrentCount==1 && context.CommandGate.CurrentCount==1,"Dispatch结束清除能力并释放原门禁");
            action=()=>{using var scope=RaidStartCommandScope.TryEnter(context,raid);throw new InvalidOperationException("injected");};
            try{await Dispatch();throw new Exception("expected exception");}catch(InvalidOperationException error)when(error.Message=="injected"){}
            That(context.DispatchGates is null && gates.All(g=>g.CurrentCount==1),"handler异常同时释放扩组与Dispatch门禁");
            using(var reverseStream=new MemoryStream())
            {
                using var reverseStop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var reverseSender=new GameConnectionSender(actors[4].Owner,reverseStream,reverseStop,0,(_,_,_,_,_)=>{});
                var reverse=new GameConnectionContext(new(2,IPAddress.Loopback,reverseStream,reverseStop),actors[4].Owner,sessions[4],reverseSender);
                existing.Commands.Wait();
                try
                {
                    action=()=>{That(RaidStartCommandScope.TryEnter(reverse,raid) is null,"另一分队反向扩组遇原队gate同样Busy，不反向等待");return Task.CompletedTask;};
                    await commands.DispatchAsync(reverse,new(1,666,[])).WaitAsync(TimeSpan.FromSeconds(2));
                }
                finally{existing.Commands.Release();}
                That(other.Commands.CurrentCount==1&&sessions[4].CommandGate.CurrentCount==1,"反向Busy释放实际caller门禁");
            }
            That(!stop.IsCancellationRequested,"另一连接Sender退出只取消自身生命周期，不误取消主连接");
            foreach(bool afterAcquisition in new[]{false,true})
            {
                using var cancelled=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var cancelStream=new MemoryStream();
                await using var cancelSender=new GameConnectionSender(actors[4].Owner,cancelStream,cancelled,0,(_,_,_,_,_)=>{});
                var cancelContext=new GameConnectionContext(new(4,IPAddress.Loopback,cancelStream,cancelled),actors[4].Owner,sessions[4],cancelSender);
                var originalParty=sessions[7].CurrentParty;int reads=0;
                // 第一次读取为取锁前快照，第二次为全部门禁已取得后的复验；只注入本连接取消，不改Party状态。
                if(afterAcquisition)sessions[7].CurrentParty=()=>
                {
                    if(++reads==2){cancelled.Cancel();cancelled.Token.ThrowIfCancellationRequested();}
                    return originalParty!();
                };
                action=()=>
                {
                    if(!afterAcquisition)cancelled.Cancel();
                    using var scope=RaidStartCommandScope.TryEnter(cancelContext,raid);
                    throw new InvalidOperationException("取消必须阻止取得可提交scope");
                };
                try
                {
                    try{await commands.DispatchAsync(cancelContext,new(1,666,[])).WaitAsync(TimeSpan.FromSeconds(2));throw new Exception("expected cancellation");}
                    catch(OperationCanceledException)when(cancelled.IsCancellationRequested){}
                }
                finally{sessions[7].CurrentParty=originalParty;}
                That(existing.Commands.CurrentCount==1&&other.Commands.CurrentCount==1&&sessions.Take(8).All(x=>x.CommandGate.CurrentCount==1)&&
                    !stop.IsCancellationRequested,"取得前或全部取得后取消，逆序归还全团门禁且不影响其它连接");
            }

            using(var beforeCommitStop=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using(var beforeCommitStream=new MemoryStream())
            {
                await using var beforeCommitSender=new GameConnectionSender(leader.Owner,beforeCommitStream,beforeCommitStop,0,(_,_,_,_,_)=>{});
                var beforeCommit=new GameConnectionContext(new(6,IPAddress.Loopback,beforeCommitStream,beforeCommitStop),leader.Owner,leader.Session,beforeCommitSender);
                var unchanged=sessions.Take(8).Select(s=>s.Selected).ToArray();
                action=()=>
                {
                    using var scope=RaidStartCommandScope.TryEnter(beforeCommit,raid)!;
                    That(scope.IsCurrent(),"取消前已持完整扩组门禁");
                    beforeCommitStop.Cancel();
                    That(!scope.IsCurrent()&&service.Start(leader,raid,definition,scope.Parties,scope.IsCurrent).Error==19,
                        "取得全部门禁后、SQL校验点前caller取消，使提交证明失效并拒19");
                    return Task.CompletedTask;
                };
                await commands.DispatchAsync(beforeCommit,new(1,666,[])).WaitAsync(TimeSpan.FromSeconds(2));
                That(sessions.Take(8).Select((s,i)=>ReferenceEquals(s.Selected,unchanged[i])&&s.StartedRaid is null&&s.RaidCombatView() is null).All(x=>x)&&
                    parties.IsCurrent(existing)&&parties.IsCurrent(other)&&existing.Commands.CurrentCount==1&&other.Commands.CurrentCount==1,
                    "提交检查点前取消无资产/位置/绑定或队伍写入，全部门禁释放");
            }

            action=()=>Task.CompletedTask;await Dispatch().WaitAsync(TimeSpan.FromSeconds(2));
            // 留一队精确复用，第二队缺失；不能通过预建全部Party掩盖SQL前预留/回滚。
            for(int index=7;index>=4;index--) parties.Leave(sessions[index]);
            var original=sessions.Take(8).Select(s=>s.Selected!).ToArray();
            void Sql(string sql){using var db=new SqliteConnection($"Data Source={Path.Combine(root,"game.db")};Pooling=False");db.Open();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
            long Scalar(string sql){using var db=new SqliteConnection($"Data Source={Path.Combine(root,"game.db")};Pooling=False");db.Open();using var command=db.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());}
            var invalidScripts = new[]
            {
                script with { Phases=script.Phases.SetItem(0,script.Phases[0] with { Dungeons=[new(999,RaidDungeonState.Open,"[fake]",1,1)] }) },
                script with { Phases=script.Phases.SetItem(0,script.Phases[0] with { Rules=[new([], [new("[unknown]",0,null,null,null,null,[])])] }) },
                script with { Phases=script.Phases.SetItem(0,script.Phases[0] with { Rules=[new([], [new("[clear phase]",0,null,null,null,null,[])])] }) }
            };
            foreach(var invalid in invalidScripts)
            {
                var denied=new LukeRaidStart(raids,parties,repository,towns,pieces,new("synthetic/invalid",invalid));
                That(denied.UnavailableReason(definition) is not null&&
                    !RaidProjection.CanStart(raid,enableLuke:denied.UnavailableReason(definition) is null),
                    "坏策略装配不可用，不因非null亮开战旗标");
                action=()=>{using var scope=RaidStartCommandScope.TryEnter(context,raid)!;
                    That(denied.Start(leader,raid,definition,scope.Parties,scope.IsCurrent).Error==19,"任意DGN或未支持执行语法在SQL前明确拒19");return Task.CompletedTask;};
                await Dispatch();
                That(parties.IsCurrent(existing)&&parties.Snapshot().Length==2&&sessions.Take(8).Select((s,i)=>
                    ReferenceEquals(s.Selected,original[i])&&s.RaidCombatView() is null&&s.StartedRaid is null).All(x=>x),"坏策略不产生费用、位置、队伍或绑定");
            }
            var storePlans=AntonRaidStart.PlanReservations(raid,script.EntryCost!,script.WaitingRoom!,towns,pieces,DateTimeOffset.UtcNow,out var planReason);
            That(planReason is null,"存储负例使用真实费用规划");
            Reject<ArgumentOutOfRangeException>(()=>repository.CommitRaidStart((RaidKind)7,definition,Guid.NewGuid(),storePlans),"未知raid kind不进入事务");
            Reject<InvalidOperationException>(()=>repository.CommitRaidStart(RaidKind.Luke,definition with {Type=0},Guid.NewGuid(),storePlans),"类型与资源身份不符拒绝");
            Reject<InvalidOperationException>(()=>repository.CommitRaidStart(RaidKind.Luke,definition,Guid.NewGuid(),storePlans.SetItem(0,storePlans[0] with {Position=origin})),"错误候战区域不得借typed存储迁移");
            Reject<InvalidOperationException>(()=>repository.CommitRaidStart(RaidKind.Luke,definition,Guid.NewGuid(),storePlans.SetItem(0,storePlans[0] with {Actor=leader with {Channel=new(2,19)}})),"旧频道身份不能借当前账号提交");
            RaidStartResult? result=null;
            action=()=>{using var scope=RaidStartCommandScope.TryEnter(context,raid)!;result=service.Start(leader,raid,definition,scope.Parties,scope.IsCurrent);return Task.CompletedTask;};
            Sql($"CREATE TRIGGER fail_luke BEFORE UPDATE ON characters WHEN OLD.id={actors[7].Character} BEGIN SELECT RAISE(ABORT,'luke-fault'); END;");
            try{await Dispatch();throw new Exception("expected SQL failure");}catch(SqliteException){}
            That(sessions.Take(8).Select((s,i)=>ReferenceEquals(s.Selected,original[i])&&s.StartedRaid is null&&s.RaidCombatView() is null).All(x=>x)&&
                parties.Snapshot().Length==2&&parties.IsCurrent(existing)&&Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name LIKE 'luke_raid_start:%'")==0,
                "末成员SQL失败整团角色/费用/位置/Battle/Started回滚，已有队伍及无关队伍不变");
            That(Scalar("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=100")==20 &&
                Scalar("SELECT count(*) FROM characters WHERE town=32 AND area=4")==0,"独立SQL读取确认没有部分费用或迁移");
            That(Scalar("SELECT count(*) FROM character_raid_runs")==0,"末成员失败也回滚全部666生命周期签发");
            Sql("DROP TRIGGER fail_luke;"); await Dispatch();
            That(result is {Error:0,Receipt.Party.Started.Participants.Length:8},"八人费用提交与唯一实例绑定成功");
            var firstReceipt=result!.Receipt!;
            That(!RaidProjection.CanStart(firstReceipt.Party,enableLuke:true),"已Started不再次投影招募开战旗标");
            var instance=firstReceipt.Party.Started!.InstanceId;
            await VerifyDisplayAsync(leader,firstReceipt.Battle!);
            That(Scalar($"SELECT count(*) FROM character_raid_runs WHERE instance_id='{instance:N}' AND raid_type=1 AND state=0 AND phase0_receipt IS NULL AND phase1_receipt IS NULL")==8,
                "666同SQL签发八人精确实例入场事实，不预填阶段资格或通关");
            That(sessions.Take(8).Select((s,i)=>s.StartedRaid?.InstanceId==instance&&s.RaidCombatView()?.Instance==instance&&
                s.Selected!.Position==landing&&s.Selected.Inventory.Items.Single().Core.Count==1&&
                s.Selected.Inventory.Items.Single().Uid==original[i].Inventory.Items.Single().Uid).All(x=>x),"真实提交角色、Started和Battle同实例，扣一次且保持UID");
            That(parties.IsCurrent(existing)&&parties.Snapshot().Length==3&&firstReceipt.Squads.Length==8,"已有精确队伍不改版本，缺队原子安装，全员完整名单供594恢复");
            long audits=Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name LIKE 'luke_raid_start:%'");
            action=()=>{var current=raids.Find(leader)!;using var scope=RaidStartCommandScope.TryEnter(context,current)!;
                result=service.Start(leader,current,definition,scope.Parties,scope.IsCurrent);return Task.CompletedTask;};
            await Dispatch(); var duplicate=result!;
            That(duplicate is {Error:0,Replay:true,Receipt.Resync:true}&&Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name LIKE 'luke_raid_start:%'")==audits,"重复启动不再收费或重建Battle");
            await DelayedPublicationAsync(firstReceipt,leader,()=>
                That(leader.Session.BeginDungeonSelection() is not null,"沿真实Session进入选图，不靠伪改阶段字段"),"首发排队后进入选图");
            await Dispatch(); That(result is {Error:19}&&leader.Session.RaidCombatView()?.Instance==instance,"选图阶段重放不能发594或迁回候战室");
            That(leader.Session.ReturnFromDungeonSelection(false) is not null,"沿原入口回到候战Town，不重建Battle");
            // 旧actor迟到回收不得触及其它成员。
            var oldView=sessions[1].RaidCombatView()!;
            var leaving=new AntonRaidStart(raids,parties,repository,towns,pieces);
            RaidChange? departure=null;
            await DelayedPublicationAsync(firstReceipt,actors[1],()=>departure=leaving.Leave(actors[1],actors[1].Character),"首发排队后离团");
            That(departure is not null&&sessions[1].RaidCombatView() is null&&!sessions[1].CanPublishRaidBattle(actors[1],oldView)&&
                sessions[2].RaidCombatView()?.Instance==instance,"精确离团只清本人实例绑定，旧发布失效但其它成员不受影响");
            That(!sessions[2].DetachRaidCombatStart(actors[2],Guid.NewGuid())&&sessions[2].RaidCombatView()?.Instance==instance,
                "错误实例迟到清理不能删除当前绑定");
            That(Scalar("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=100")==12,
                "离团不擅自退还已提交门票");
            var entrant=actors[1];raids.Create(entrant,1,"new","new"u8.ToArray(),8,1);raids.Assign(entrant,entrant.Character,1);
            using(var freshStream=new MemoryStream())
            {
                using var freshStop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var freshSender=new GameConnectionSender(entrant.Owner,freshStream,freshStop,0,(_,_,_,_,_)=>{});
                var fresh=new GameConnectionContext(new(3,IPAddress.Loopback,freshStream,freshStop),entrant.Owner,entrant.Session,freshSender);
                action=()=>{var current=raids.Find(entrant)!;using var scope=RaidStartCommandScope.TryEnter(fresh,current)!;
                    result=service.Start(entrant,current,definition,scope.Parties,scope.IsCurrent);return Task.CompletedTask;};
                await commands.DispatchAsync(fresh,new(1,666,[]));
                var nextInstance=entrant.Session.StartedRaid!.InstanceId;
                That(result is {Error:0}&&nextInstance!=instance&&entrant.Session.RaidCombatView()?.Instance==nextInstance,
                    "合成无次数策略下离团后正常一人开团建立新实例，不残留旧绑定");
                That(!entrant.Session.DetachRaidCombatStart(entrant,instance)&&entrant.Session.RaidCombatView()?.Instance==nextInstance,
                    "旧实例迟到清理不能删除新一人团绑定");
                int lateNotices=0;entrant.Session.RaidBattleNoticeArrived=(_,_)=>lateNotices++;
                entrant.Session.PublishRaidCombat(new(oldView,oldView));
                That(lateNotices==0&&entrant.Session.RaidCombatView()?.Instance==nextInstance,
                    "旧预留迟到ReleasedChange不能向新实例发布或替换其状态");
                entrant.Session.RaidBattleNoticeArrived=null;
                leaving.Leave(entrant,entrant.Character);
                That(entrant.Session.RaidCombatView() is null&&raids.Find(entrant) is null,"最后成员离团清除本团与绑定");
            }

            async Task DelayedPublicationAsync(RaidStartReceipt receipt,RaidActor recipient,Action mutate,string note)
            {
                using var publicationStop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var held=new GameSenderBudgetTests.HeldStream();
                await using var publicationSender=new GameConnectionSender(recipient.Owner,held,publicationStop,0,(_,_,_,_,_)=>{});
                var publicationContext=new GameConnectionContext(new(5,IPAddress.Loopback,held,publicationStop),recipient.Owner,recipient.Session,publicationSender);
                Task first=publicationSender.Send(1,1347,[1],"held");
                await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                try
                {
                    var town=new TownProjection(towns,new CharacterStatComputer(new CharacterCatalog(new())),null,pieces);
                    RaidProjection.Started(publicationContext,town,receipt,recipient);
                    That(publicationSender.PendingBytes>17,"实际启动通知已进入受控延迟队列，而非仅测试guard函数");
                    mutate();
                    Task barrier=publicationSender.Send(1,1347,[1],"after-mutation");
                    held.Release.TrySetResult();
                    await Task.WhenAll(first,barrier).WaitAsync(TimeSpan.FromSeconds(2));
                    That(held.Frames.Count==2&&publicationSender.PendingBytes==0&&publicationContext.RaidDisplay is null,note+"：旧名单/背包/598/594/候战位置/587均丢弃，不退回旧场景");
                }
                finally{held.Release.TrySetResult();}
            }

            var selectionView=sessions[2].RaidCombatView()!;
            action=()=>{var current=raids.Find(leader)!;using var scope=RaidStartCommandScope.TryEnter(context,current)!;
                result=service.Start(leader,current,definition,scope.Parties,scope.IsCurrent);return Task.CompletedTask;};
            await Dispatch();That(result is {Error:0,Receipt.Resync:true},"换角排队测试沿真实重放用例捕获当前回执");
            await DelayedPublicationAsync(result!.Receipt!,actors[2],sessions[2].ReturnToRoster,"完整重放排队后换角");
            That(sessions[2].RaidCombatView() is null&&!sessions[2].CanPublishRaidBattle(actors[2],selectionView)&&
                sessions[3].RaidCombatView()?.Instance==instance,"正常返回选角清除精确旧实例，不清其它成员");
            var stale=actors[3];
            using(var replacement=owners.Acquire(stale.Owner.Account,()=>{}))
            {
                var replacementSession=new GameSession(repository,towns,replacement,entry:new(new(2,20),32,"[raid]"));sessions.Add(replacementSession);
                replacementSession.Select(0);replacementSession.MarkReady();replacementSession.CurrentParty=()=>parties.Current(replacementSession);
                var current=replacementSession.CaptureRaidActor(raids)!;
                raids.Create(current,1,"replacement","replacement"u8.ToArray(),8,1);
                var currentRaid=raids.Find(current)!;
                sessions[3].End();
                That(raids.IsCurrent(currentRaid)&&raids.Find(current)==currentRaid&&sessions[4].RaidCombatView()?.Instance==instance,
                    "旧owner接管后迟到End只清旧Session/epoch，不删新记录或其它成员Battle");
                replacementSession.ReturnToRoster();
            }

        }
        finally{foreach(var session in sessions)session.Disconnect();foreach(var lease in leases)lease.Dispose();TestFiles.DeleteDirectory(root,true);}
        Console.WriteLine($"Luke raid start: {Count-before} assertions passed");
    }
    static async Task VerifyDisplayAsync(RaidActor actor, AntonRaidBattleView view)
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var held=new GameSenderBudgetTests.HeldStream();
        List<(ushort Opcode,byte[] Body)> frames=[];
        await using var sender=new GameConnectionSender(actor.Owner,held,stop,0,(_,opcode,_,body,_)=>frames.Add((opcode,body.ToArray())));
        var context=new GameConnectionContext(new(40,IPAddress.Loopback,held,stop),actor.Owner,actor.Session,sender);
        Task first=sender.Send(1,1347,[1],"display-held");
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        bool current=true;
        RaidCombatProjection.PostState(context,view,actor,()=>current,"display-stale");
        current=false;held.Release.TrySetResult();
        await Task.WhenAll(first,sender.DrainAsync()).WaitAsync(TimeSpan.FromSeconds(2));
        That(context.RaidDisplay is null&&frames.All(row=>row.Opcode!=587),"首587排队后失效不确认展示，后续可恢复");
        current=true;
        var display=RaidCombatDisplayIdentity.Capture(actor,view);
        That(display==RaidCombatDisplayIdentity.Capture(actor with {Name="display-only-name",Channel=new(9,99)},view),
            "展示名字或频道元数据不构成新阶段身份，不因展示字段变化重播动画");
        That(display!=RaidCombatDisplayIdentity.Capture(actor with {Epoch=actor.Epoch+1},view)&&
            display!=RaidCombatDisplayIdentity.Capture(actor,view with {Instance=Guid.NewGuid()}),
            "换选角epoch或攻坚实例必须构成不同展示身份");
        using(var otherOwner=new AccountOwners().Acquire("display-other-owner",()=>{}))
            That(display!=RaidCombatDisplayIdentity.Capture(actor with {Owner=otherOwner},view),"新owner代不能继承旧连接展示身份");
        RaidCombatProjection.Notice(context,view,actor);RaidCombatProjection.Notice(context,view,actor);
        await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
        That(frames.Count(row=>row.Opcode==587)==1&&frames.Count(row=>row.Opcode==583)==2&&context.RaidDisplay?.Phase==0,
            "同view重复发布仍同步symbol，但实际成功写出587一次");
        // 纯投影边界：合成下一phase视图；真实领域转换另由两阶段Runtime用例覆盖。
        var next=view with {Phase=1,LukePhaseCoinsUsed=0};
        RaidCombatProjection.PostState(context,next,actor,()=>false,"display-dropped-next-phase");
        await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
        That(frames.All(row=>row.Opcode!=778)&&context.RaidDisplay?.Phase==0,
            "新phase587被guard丢弃时不能只发778让旧阶段误读");
        RaidCombatProjection.PostState(context,next,actor,()=>current,"display-next-phase");
        await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
        That(frames.Count(row=>row.Opcode==587)==2&&frames.Last(row=>row.Opcode==587).Body.SequenceEqual(new byte[]{2,1}),
            "相同state不同phase必须发送新阶段，不能仅按state去重");
        That(frames[^1].Opcode==778&&frames[^1].Body.SequenceEqual(new byte[]{0})&&frames[^2].Opcode==587,
            "恢复的新phase先写587再写778已用0");
        var used=next with {LukePhaseCoinsUsed=1,Revision=next.Revision+1};
        RaidCombatProjection.PostState(context,used,actor,()=>true,"display-coin-update");
        RaidCombatProjection.PostState(context,next,actor,()=>false,"display-old-coin-update");
        await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
        That(frames.Count(row=>row.Opcode==587)==2&&frames.Last(row=>row.Opcode==778).Body.SequenceEqual(new byte[]{1}),
            "同phase额度更新不重播587，旧额度guard拒绝");
        using var failedStop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var failedStream=new GameSenderBudgetTests.HeldStream{FailWrite=true};
        await using var failedSender=new GameConnectionSender(actor.Owner,failedStream,failedStop,0,(_,_,_,_,_)=>{});
        var failedContext=new GameConnectionContext(new(41,IPAddress.Loopback,failedStream,failedStop),actor.Owner,actor.Session,failedSender);
        RaidCombatProjection.PostState(failedContext,view,actor,()=>true,"display-fails");
        await failedStream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task failedBarrier=failedSender.DrainAsync();failedStream.Release.TrySetResult();
        try{await failedBarrier.WaitAsync(TimeSpan.FromSeconds(2));throw new InvalidOperationException("expected display write failure");}
        catch(IOException){}
        await failedSender.DisposeAsync();
        That(failedContext.RaidDisplay is null&&failedStop.IsCancellationRequested,"587真实写入失败不确认游标且原连接作废");
        using var recoveredStop=new CancellationTokenSource(TimeSpan.FromSeconds(5));using var recoveredStream=new MemoryStream();
        await using var recoveredSender=new GameConnectionSender(actor.Owner,recoveredStream,recoveredStop,0,(_,_,_,_,_)=>{});
        var recovered=new GameConnectionContext(new(42,IPAddress.Loopback,recoveredStream,recoveredStop),actor.Owner,actor.Session,recoveredSender);
        RaidCombatProjection.PostState(recovered,view,actor,()=>true,"display-recovered");
        await recoveredSender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
        That(recovered.RaidDisplay?.Instance==view.Instance&&recoveredStream.Length>0,"新连接无旧展示游标，可恢复相同实例当前阶段");
    }

}
