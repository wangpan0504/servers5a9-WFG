using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeRewardRecoveryTests
{
    public static void RunCore()
    {
        LukeRewardDiscoveryTests.RunCore();
        LukeAutomaticRewardTests.RunCore();
        int before=Count;
        string id=Guid.NewGuid().ToString("N");
        That(DevelopmentRequest.Parse($"luke-reward-inspect Hero 1 {id} 0 0 0","account") is {Writes:false,Action:DevelopmentAction.LukeRewardInspect},"检查历史资格是只读命令");
        That(DevelopmentRequest.Parse($"luke-reward-claim Hero 1 {id} 0 0 0 {id} 1","account") is {Writes:true,Action:DevelopmentAction.LukeRewardClaim},"显式领取标记写操作，不伪装只读");
        foreach(string command in new[]{"luke-reward-inspect Hero",$"luke-reward-claim Hero 1 {id} 0 0 0",$"luke-reward-inspect Hero 1 {id} 0 0 0 extra"})
        {
            bool refused=false;try{DevelopmentRequest.Parse(command,"account");}catch(InvalidDataException){refused=true;}
            That(refused,"恢复命令缺少完整身份或多余参数拒绝");
        }
        Console.WriteLine($"LukeRewardRecoveryTests core: {Count-before} passed");
    }

    internal static void RunResources(TcpTestResources resources,CharacterRepository repository,AccountOwners owners,
        IReadOnlyList<GameSession> sessions,IReadOnlyList<RaidActor> actors,RaidPhaseEligibility[] eligibilities,string path)
    {
        int before=Count;var catalog=new DevelopmentCatalog(resources.Archive,resources.ItemResources,resources.Quests);
        var target=eligibilities[0];var role=sessions[0].Selected!;var first=target.Offers.First();
        string Args(RaidPhaseEligibility row,byte kind,byte card)=>$"{row.Character} {row.Instance:N} {row.Phase} {kind} {card}";
        string inspect=$"luke-reward-inspect {role.Name} {Args(target,first.Kind,first.Card)}";
        string claim=$"luke-reward-claim {role.Name} {Args(target,first.Kind,first.Card)} {target.Event:N} {target.Anchor}";
        string Answer(string command,DevelopmentCatalog? supplied=null)=>DevelopmentGm.Answer(command,actors[0].Owner.Account,repository,owners,_=>null,catalog:supplied);
        That(DevelopmentGm.Answer(claim,actors[0].Owner.Account,repository,owners,_=>sessions[0],catalog:catalog).StartsWith("ERR "),"已选角色的管理入口拒绝领取，不强制退场或顶号");
        That(Answer(claim,catalog).StartsWith("ERR "),"离线路由遇已占账号拒绝，不取得第二租约");
        bool cross=false;try{repository.ReadLukeRewardRecovery(actors[1].Owner,sessions[1].Selected!,new(target.Character,target.Instance,0,first.Kind,first.Card));}catch(InvalidDataException){cross=true;}
        That(cross,"其它账号/角色不能读取目标资格");
        actors[0].Owner.Dispose();
        string stable=Facts();
        string read=Answer(inspect);That(read.StartsWith("OK "),"真实GM读取旧state1资格，无Battle恢复");
        using(var json=JsonDocument.Parse(read[3..]))That(!json.RootElement.GetProperty("Claimed").GetBoolean()&&json.RootElement.GetProperty("Prepared").GetProperty("Week").GetInt32()==target.Week,"未领资格使用原周期，不按系统日期重算");
        That(Facts()==stable,"检查不改持久资格/完成次数/奖励回执");
        That(Answer(claim.Replace(target.Event.ToString("N"),Guid.NewGuid().ToString("N")),catalog).StartsWith("ERR "),"错误event不能授权领取");
        That(Answer(claim.Replace($" {target.Anchor}",$" {target.Anchor+1}"),catalog).StartsWith("ERR "),"错误anchor不能授权领取");
        That(Answer($"luke-reward-claim {role.Name} {Args(target,first.Kind,255)} {target.Event:N} {target.Anchor}",catalog).StartsWith("ERR "),"未知card不能生成新奖励");
        That(Answer($"luke-reward-inspect {role.Name} {role.Id+1} {target.Instance:N} 0 {first.Kind} {first.Card}").StartsWith("ERR "),"名字相同但明确角色ID不符拒绝");
        That(Answer($"luke-reward-inspect {role.Name} {role.Id} {Guid.NewGuid():N} 0 {first.Kind} {first.Card}").StartsWith("ERR "),"未知历史instance不生成资格");
        That(Answer($"luke-reward-inspect {role.Name} {role.Id} {target.Instance:N} 1 {first.Kind} {first.Card}").StartsWith("ERR "),"未提交P2不能借用P1资格");
        That(Facts()==stable,"全部无效目标与凭证零资产/资格写入");
        string delivered=Answer(claim,catalog);That(delivered.StartsWith("OK "),"无旧Battle时真实GM原事务领取已赢得资格");
        using(var json=JsonDocument.Parse(delivered[3..]))That(!json.RootElement.GetProperty("duplicate").GetBoolean(),"首次领取不是伪回执");
        string after=Facts();
        string replay=Answer(claim);That(replay.StartsWith("OK "),"已提交回执即使资源目录缺失也可重放，不再规划模板");
        using(var json=JsonDocument.Parse(replay[3..]))That(json.RootElement.GetProperty("duplicate").GetBoolean(),"SQL成功未ACK重试标记原回执");
        That(Facts()==after,"重复领取不再次提交资产/资格/奖励回执");
        var alternate=target.Offers.First(x=>x.Kind==first.Kind&&x.Card!=first.Card);
        That(Answer($"luke-reward-claim {role.Name} {Args(target,alternate.Kind,alternate.Card)} {target.Event:N} {target.Anchor}",catalog).StartsWith("ERR "),"同kind另选card冲突，不能当新权益");
        var remaining=target.Offers.First(x=>x.Kind!=first.Kind);
        That(Answer($"luke-reward-inspect {role.Name} {Args(target,remaining.Kind,remaining.Card)}").Contains("\"Claimed\":false"),"半领时其它kind仍有独立原权益");
        string second=$"luke-reward-claim {role.Name} {Args(target,remaining.Kind,remaining.Card)} {target.Event:N} {target.Anchor}";
        string[] results=new string[2];Parallel.Invoke(()=>results[0]=Answer(second,catalog),()=>results[1]=Answer(second,catalog));
        That(results.Any(x=>x.StartsWith("OK ")),"两个显式请求竞争至少一次成功，另一请求仅拒绝租约或重放");
        That(Answer(second).Contains("\"duplicate\":true"),"竞争后稳定重放唯一已提交权益");
        using(var db=new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
        {
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$id AND instance_id=$instance AND phase=0";
            cmd.Parameters.AddWithValue("$id",role.Id);cmd.Parameters.AddWithValue("$instance",target.Instance.ToString("N"));
            That(Convert.ToInt32(cmd.ExecuteScalar())==2,"两个kind只产生两条原事务receipt，不因备选card或并发增加");
        }
        // 在线选角账号仍借用原owner；实际领取与并发选角按同一账号锁串行化。
        var live=sessions[1];var liveActor=actors[1];var liveEligibility=eligibilities[1];var liveRole=live.Selected!;
        var liveOffer=liveEligibility.Offers.First();live.ReturnToRoster();
        string liveCommand=$"luke-reward-claim {liveRole.Name} {Args(liveEligibility,liveOffer.Kind,liveOffer.Card)} {liveEligibility.Event:N} {liveEligibility.Anchor}";
        That(DevelopmentGm.Answer(liveCommand,liveActor.Owner.Account,repository,owners,_=>live,catalog:catalog).StartsWith("OK "),"在线无Selected账号经原GM路由成功领取");
        var otherKind=liveEligibility.Offers.First(x=>x.Kind!=liveOffer.Kind);
        using(var captured=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())
        using(var selectStarted=new ManualResetEventSlim())using(var loginStarted=new ManualResetEventSlim())
        {
            var claimTask=Task.Run(()=>live.WithUnselectedDevelopmentOwner(liveRole.Name,(owner,current)=>LukeRewardRecovery.Claim(owner,current,
                new(current.Id,liveEligibility.Instance,0,otherKind.Kind,otherKind.Card),liveEligibility.Event,liveEligibility.Anchor,repository,()=>
                {captured.Set();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException("恢复领取并发夹具超时");return(resources.ItemResources.Items,resources.ItemResources.GoldLimits,resources.ItemResources.EpicPieces);},DateTimeOffset.UtcNow)));
            Task? selectTask=null;Task<bool>? loginTask=null;
            try
            {
                That(captured.Wait(TimeSpan.FromSeconds(10)),"真实未领规划持有原账号owner，未提前提交");
                selectTask=Task.Run(()=>{selectStarted.Set();live.Select(0);});
                loginTask=Task.Run(()=>{loginStarted.Set();bool accepted=owners.TryAcquire(liveActor.Owner.Account,()=>{},DuplicateLoginPolicy.Reject,out var denied,out _);denied?.Dispose();return accepted;});
                That(selectStarted.Wait(TimeSpan.FromSeconds(2))&&!selectTask.Wait(50),"并发选角不能越过资产提交owner屏障");
                That(loginStarted.Wait(TimeSpan.FromSeconds(2))&&!loginTask.Wait(50),"并发登录也等待同一账号提交屏障，不在测试主线程同步取锁");
            }
            finally
            {
                release.Set();
                Task.WhenAll(new Task?[]{claimTask,selectTask,loginTask}.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            var committed=claimTask.GetAwaiter().GetResult();
            That(!loginTask!.GetAwaiter().GetResult(),"提交后并发登录仍按Reject策略拒绝，不顶号");
            var reloaded=repository.LoadCharacter(liveActor.Owner,liveRole.Slot);
            That(live.Selected!.Revision==reloaded.Revision&&live.Selected.Revision>=committed.Character.Revision,
                "选角可另存落点，但在线版本必须等于最新持久版本且不早于领奖提交");
            var grantedIds=otherKind.Grants.Select(x=>x.ItemId).ToHashSet();
            That(live.Selected.Inventory.Items.Where(x=>grantedIds.Contains(x.Core.ItemId)).SequenceEqual(
                committed.Character.Inventory.Items.Where(x=>grantedIds.Contains(x.Core.ItemId)))&&
                reloaded.Inventory.Items.Where(x=>grantedIds.Contains(x.Core.ItemId)).SequenceEqual(
                committed.Character.Inventory.Items.Where(x=>grantedIds.Contains(x.Core.ItemId))),
                "并发选角保留实际已提交奖励完整物品UID/数量/位置，不能只放宽revision断言");
        }
        // 未领缺模板拒绝且无receipt；不把此失败替换成其它物品或邮件。
        var missingActor=actors[2];var missingEligibility=eligibilities[2];var missingRole=sessions[2].Selected!;
        var missingOffer=missingEligibility.Offers.First();string absentBefore=Facts();
        var emptyItems=new ItemCatalog(null!,new Dictionary<int,string>(),new Dictionary<int,string>(),0);
        bool templateRefused=false;
        try{LukeRewardRecovery.Claim(missingActor.Owner,missingRole,new(missingRole.Id,missingEligibility.Instance,0,missingOffer.Kind,missingOffer.Card),
            missingEligibility.Event,missingEligibility.Anchor,repository,()=> (emptyItems,resources.ItemResources.GoldLimits,resources.ItemResources.EpicPieces),DateTimeOffset.UtcNow);}
        catch(InvalidDataException){templateRefused=true;}
        That(templateRefused&&Facts()==absentBefore,"尚未领的原物品模板缺失，资产及receipt保持不变");
        // 满包沿既有溢出邮件；邮件SQL故障必须回滚整笔原奖励。
        var fullSession=sessions[3];var fullActor=actors[3];var fullEligibility=eligibilities[3];var fullRole=fullSession.Selected!;fullSession.ReturnToRoster();
        var fullOffer=fullEligibility.Offers.First();var template=resources.ItemResources.Items.Get(fullOffer.Grants[0].ItemId)!;
        var range=ItemSlotBoundService.Range(template.Kind,fullRole.Inventory);var list=ItemSlotBoundService.List(template.Kind);
        var packed=fullRole.Inventory.Items.Where(x=>x.Position.List!=list||x.Position.Index<range.Start||x.Position.Index>range.End).ToImmutableArray();
        for(short slot=range.Start;slot<=range.End;slot++)packed=packed.Add(new(0,new(list,slot),InventoryRewardGrantService.Create(template,template.StackLimit)));
        fullRole=InventoryService.Commit(repository,fullActor.Owner,fullRole,new(true,0,new InventoryPlan(fullRole.Inventory,fullRole.Inventory with{Items=packed},"fixture_full",new(fullRole))));
        string fullCommand=$"luke-reward-claim {fullRole.Name} {Args(fullEligibility,fullOffer.Kind,fullOffer.Card)} {fullEligibility.Event:N} {fullEligibility.Anchor}";
        string fullBefore=Facts();Sql("CREATE TRIGGER recovery_mail_failure BEFORE INSERT ON mailbox_messages BEGIN SELECT RAISE(ABORT,'recovery mail fixture failure'); END");
        try{That(DevelopmentGm.Answer(fullCommand,fullActor.Owner.Account,repository,owners,_=>fullSession,catalog:catalog).StartsWith("ERR ")&&Facts()==fullBefore,"满包邮件提交失败不留下资产或奖励回执");}
        finally{Sql("DROP TRIGGER recovery_mail_failure");}
        string fullClaim=DevelopmentGm.Answer(fullCommand,fullActor.Owner.Account,repository,owners,_=>fullSession,catalog:catalog);
        That(fullClaim.StartsWith("OK "),"邮件故障解除后同一原权益可重试");
        using(var json=JsonDocument.Parse(fullClaim[3..]))That(json.RootElement.GetProperty("MailIds").GetArrayLength()>0,"满包发放实际原事务邮件，不假称已入包");
        string mailReplay=DevelopmentGm.Answer(fullCommand,fullActor.Owner.Account,repository,owners,_=>fullSession);
        using(var firstJson=JsonDocument.Parse(fullClaim[3..]))using(var nextJson=JsonDocument.Parse(mailReplay[3..]))
            That(nextJson.RootElement.GetProperty("duplicate").GetBoolean()&&firstJson.RootElement.GetProperty("MailIds").GetRawText()==nextJson.RootElement.GetProperty("MailIds").GetRawText(),"满包重试只重放同一mailIDs");
        // 仅故障注入修改已由真实阶段事务生成的合成记录，结束恢复原文，不创建资格。
        var damaged=eligibilities[4];var damageActor=actors[4];var damageRole=sessions[4].Selected!;
        foreach(var broken in new[]{damaged with{Offers=[null!]},damaged with{Offers=[damaged.Offers[0] with{Grants=[null!]}]}})
        {
            SetEligibility(JsonSerializer.Serialize(broken));bool rejected=false;
            try{repository.ReadLukeRewardRecovery(damageActor.Owner,damageRole,new(damageRole.Id,damaged.Instance,0,damaged.Offers[0].Kind,damaged.Offers[0].Card));}
            catch(InvalidDataException){rejected=true;}
            finally{SetEligibility(JsonSerializer.Serialize(damaged));}
            That(rejected,"合法JSON中的null牌面/嵌套奖励显式损坏拒绝，不NRE或默认发奖");
        }
        void SetEligibility(string json)
        {
            using var db=new SqliteConnection($"Data Source={path};Pooling=False");db.Open();using var cmd=db.CreateCommand();
            cmd.CommandText="UPDATE character_raid_runs SET phase0_receipt=$json WHERE character_id=$id AND instance_id=$instance";
            cmd.Parameters.AddWithValue("$json",json);cmd.Parameters.AddWithValue("$id",damageRole.Id);cmd.Parameters.AddWithValue("$instance",damaged.Instance.ToString("N"));cmd.ExecuteNonQuery();
        }
        void Sql(string sql){using var db=new SqliteConnection($"Data Source={path};Pooling=False");db.Open();using var cmd=db.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}
        Console.WriteLine($"LukeRewardRecoveryTests GM/SQLite: {Count-before} passed");
        string Facts()
        {
            using var db=new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");db.Open();using var cmd=db.CreateCommand();
            cmd.CommandText="SELECT json_group_array(json_object('i',instance_id,'c',character_id,'s',state,'w',clear_week,'p0',phase0_receipt,'p1',phase1_receipt)) FROM character_raid_runs";
            string runs=Convert.ToString(cmd.ExecuteScalar())!;cmd.CommandText="SELECT count(*) FROM character_raid_reward_receipts";
            string receipts=Convert.ToString(cmd.ExecuteScalar())!;
            cmd.CommandText="SELECT json_group_array(json_object('id',id,'r',revision)) FROM characters";
            return runs+":"+receipts+":"+cmd.ExecuteScalar();
        }
    }
}
