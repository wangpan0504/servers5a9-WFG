using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Pvp;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Pvp;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class SteelLearningTcpTests
{
    public static async Task RunAsync(ServerOptions options,string root,TcpTestResources r)
    {
        await using var f=new TcpTestFixture(options,Path.Combine(root,"steel-learning"),resources:r);
        var repository=new CharacterRepository(f.Database,r.Quests);
        var owners=new AccountOwners();
        var growth=SelfTestCatalogs.Growth(r.Archive,r.Learning);
        ushort id,opponent;
        using(var owner=owners.Acquire(f.Options.DevelopmentAccount,()=>{}))
        {
            var seed=CharacterCreation.Prepare("SteelTCP",r.Characters.Get(0),r.Towns.Room,r.Towns.InitialReturn,r.Learning.Get);
            var created=repository.Create(owner,seed with {Level=60,AcceptedStoryLevel=60,
                InitialLearning=SkillPointLedger.Synchronize(0,0,60,
                    seed.InitialLearning with {Experience=growth.GetLevelStartExperience(60)},r.Learning)});
            id=created.Id;
            var wallet=AccountWallet.Empty.Set(CurrencyKind.BlackCube,200).Set(CurrencyKind.WhiteCube,200)
                .Set(CurrencyKind.RedCube,200).Set(CurrencyKind.BlueCube,200).Set(CurrencyKind.ClearCube,999);
            repository.CommitInventory(owner,created,new(created.Inventory,created.Inventory with {AccountWallet=wallet},"fixture_steel_materials"));
        }
        using(var owner=owners.Acquire("steel-opponent",()=>{}))
            opponent=repository.Create(owner,new("SteelOther",r.Towns.Room,r.Towns.InitialReturn)).Id;
        await f.StartAsync();
        using(var peer=Open(f))
        {
            peer.Select(prepared:true);peer.Barrier();
            peer.Send(29,[0,1,31,0,0,1,0]);peer.Expect(1,29,[0,0xD9,0]);peer.Barrier();
            peer.Send(31,[31,0,93,1]);peer.Expect(1,31,[1,93,1,1,0,0,0,0]);QuestLifecycleTcpTests.DrainNotifications(peer);
            peer.Send(33,[33,0,93,1,0,0]);peer.Expect(1,33,[1,93,1,1,0,0,0]);peer.Barrier();
            Finish(peer,349,false);
            That(f.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=349",id) is [[1L]],
                "无段位时客户端归零事件和领奖均不能授权偷学");
            // 隔离fixture沿正式整局事实和结算来源累积段位；不裸写评分表。
            var rules=new PvpRatingCatalog(r.Archive);
            var ratings=new PvpRatingSettlement(repository,new(rules));
            using(var owner=owners.Acquire(f.Options.DevelopmentAccount,()=>{}))
            {
                for(int round=0;ratings.Grade(ratings.Capture(owner,id))<2;round++)
                {
                    That(round<1000,"正式普通胜利可在有界局数达到真实段位目标");
                    var result=new PvpArenaResultRecord(Guid.NewGuid(),Guid.NewGuid(),296,PvpArenaMode.Individual,
                        [new(owner.Account,id,0,0,PvpArenaOutcome.Win),new("steel-opponent",opponent,1,1,PvpArenaOutcome.Lose)],false,0);
                    repository.CommitPvpArenaResult(owner,result);ratings.Settle(owner,id,result.MatchId);
                }
            }
            peer.Send(33,[33,0,93,1,0,0]);peer.Expect(1,33,[1,93,1,0,0,0,0]);peer.Barrier();
            Finish(peer,349,true,5);
            peer.Send(31,[31,0,99,1]);var accepted=peer.Expect(1,31).Body;
            That(accepted[0]==1 && U16(accepted,1)==355,"349完成开放真实355材料任务");QuestLifecycleTcpTests.DrainNotifications(peer);
            string before=f.Snapshot();Finish(peer,355,false);
            That(before==f.Snapshot(),"末种材料少一个时整笔拒绝，不先消费其它四种");
            peer.Send(3,[0]);peer.Expect(1,3,[1]);peer.Closed();
        }
        using(var owner=owners.Acquire(f.Options.DevelopmentAccount,()=>{}))
        {
            var character=repository.LoadCharacter(owner,0);
            var before=character.Inventory;
            var after=before with {AccountWallet=before.AccountWallet.Set(CurrencyKind.ClearCube,1000)};
            repository.CommitInventory(owner,character,new(before,after,"fixture_steel_missing_material"));
        }
        using(var peer=Open(f))
        {
            peer.Select(prepared:true);peer.Barrier();
            string before=f.Snapshot("SELECT currency_kind,balance FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind",f.Options.DevelopmentAccount);
            f.Sql("CREATE TRIGGER steel_finish_abort AFTER INSERT ON character_quest_completion_receipts WHEN NEW.quest_id=355 BEGIN SELECT RAISE(ABORT,'steel finish rollback'); END");
            peer.Send(34,[34,0,99,1,255,255,1,0,255,255]);peer.Closed();
            That(before==f.Snapshot("SELECT currency_kind,balance FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind",f.Options.DevelopmentAccount) &&
                f.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=355",id) is [[0L]] &&
                f.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=355",id) is [[1L]],
                "355末段故障回滚五种钱包消耗、完成身份和任务槽，重连可重试");
            f.Sql("DROP TRIGGER steel_finish_abort");
        }
        using(var peer=Open(f))
        {
            peer.Select(prepared:true);peer.Barrier();
            Finish(peer,355,true,0);peer.Barrier();
            using(var owner=owners.Acquire(f.Options.DevelopmentAccount,()=>{}))
            That(new[]{CurrencyKind.BlackCube,CurrencyKind.WhiteCube,CurrencyKind.RedCube,CurrencyKind.BlueCube,CurrencyKind.ClearCube}
                .All(kind=>repository.LoadCharacter(owner,0).Inventory.AccountWallet.Get(kind)==0),
                "355按原资源一次精确消耗四种200和一种1000材料");
            peer.Send(29,[0,1,31,0,0,1,0]);var learned=SkillPacketsTests.DecodeSuccess(peer.Expect(1,29).Body);
            That(learned.Skills.Single().Id==31 && learned.Skills.Single().Level==1,"355完成后真实CMD29允许偷学");peer.Barrier();
            string before=f.Snapshot();Finish(peer,355,false);That(before==f.Snapshot(),"已交付材料任务不能重复扣料或发奖");
            peer.Send(3,[0]);peer.Expect(1,3,[1]);peer.Closed();
        }
        using(var peer=Open(f))
        {
            peer.Select(prepared:true);
            var trees=SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(peer.CompletedQuestHistory[353]==1 && peer.CompletedQuestHistory[359]==1 &&
                trees[0].Skills.Any(skill=>skill.Id==31 && skill.Level==1) && trees[1].Skills.All(skill=>skill.Id!=31),
                "真实重登恢复偷学资格、等级和独立方案，不向另一页复制");
        }
        await f.StopAsync();
    }

    static void Finish(TcpPeer peer,ushort quest,bool success,byte type=0)
    {
        peer.Send(34,[34,0,(byte)quest,(byte)(quest>>8),255,255,1,0,255,255]);
        var body=peer.Expect(1,34).Body;
        if(success) That(body[0]==1 && U16(body,1)==quest && body[3]==type,"真实任务按所属类型提交奖励");
        else Bytes(body,[0,19,0],"任务条件未满足或已交付时拒绝");
        QuestLifecycleTcpTests.DrainNotifications(peer);
    }
}
