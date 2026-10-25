using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestCashRewardTcpTests
{
    public static async Task RunAsync(ServerOptions options,string root,TcpTestResources resources)
    {
        // 合成公开任务仅验证已有现金奖励接口；不是开启真实旧补偿任务或证明其原服玩法。
        var definitions = new[] { QuestCashRewardTests.Quest(29940,2681762,7), QuestCashRewardTests.Quest(29941,2681917,9),
            QuestCashRewardTests.Quest(29942,2681762,5,2681917,6,2058,2) };
        var catalog = TcpSyntheticQuestCatalog.WithHopeMissions(new QuestCatalog(definitions.ToDictionary(row=>row.Id)),resources.Quests);
        await using var f = new TcpTestFixture(options,Path.Combine(root,"quest-cash-reward"),resources:resources,questCatalog:catalog);
        var repository = new CharacterRepository(f.Database,catalog);
        using (var owner = new AccountOwners().Acquire(f.Options.DevelopmentAccount,()=>{}))
        {
            var candidate = CharacterCreation.Prepare("CashWire",resources.Characters.Get(0),resources.Towns.Room,
                resources.Towns.InitialReturn,resources.Learning.Get);
            var character = repository.Create(owner,candidate with { AcceptedStoryLevel=1,
                ActiveMainQuests=definitions.Select((q,i)=>new ActiveMainQuest((byte)i,(ushort)q.Id,0)).ToArray(),
                Inventory=candidate.Inventory with { Items=candidate.Inventory.Items.Add(new(0,new(InventoryListType.Main,177),
                    InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(10165055),3))) } });
            repository.CommitInventory(owner,character,new(character.Inventory,character.Inventory with
                { AccountWallet=character.Inventory.AccountWallet.Set(CurrencyKind.Cera,11)
                    .Set(CurrencyKind.TokenCera,22).Set(CurrencyKind.HappyTokenCera,33) },"cash_wire_fixture"));
            repository.Create(owner,CharacterCreation.Prepare("CashTwin",resources.Characters.Get(0),resources.Towns.Room,
                resources.Towns.InitialReturn,resources.Learning.Get));
        }
        await f.StartAsync();
        int token=22,happy=33;
        using (var peer=Open(f))
        {
            peer.Select(prepared:true);
            foreach (var quest in definitions)
            {
                int id=quest.Id;
                peer.Send(34,Finish(id));
                var ack=peer.Expect(1,34).Body;
                int physical=id==29942?1:0;
                That(ack.Length==18+19*physical && ack[0]==1 && U16(ack,1)==id && ack[3]==0 && U32(ack,4)==0 &&
                    ack[8]==1 && ack[9]==0 && U16(ack,10)==177 && U32(ack,12)==1 && ack[16]==0 && ack[17]==physical,
                    "34保持寻物真实消耗，现金不虚构物品行或Main槽");
                if (physical==1) That(U32(ack,20)==2058 && U32(ack,24)==2,"混合奖励34只保留真实2058增量");
                var inventory=peer.Expect(0,14).Body;
                That(inventory[0]==0 && U16(inventory,1)==(physical==0?1:2),"14仅材料/实体奖励实际变化槽，不加现金伪槽");
                token+=id==29940?7:id==29942?5:0;
                happy+=id==29941?9:id==29942?6:0;
                peer.Expect(0,53,Cash(token,happy));
                peer.Expect(0,21); peer.Expect(0,356);
                peer.Send(34,Finish(id)); peer.Expect(1,34,[0,19,0]); peer.Barrier();
                That(f.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=$p0",id) is [[1L]],
                    "重放无新53或再次发钱，只有一份任务回执");
            }
            peer.Send(63); peer.Expect(0,53,Cash(token,happy));
            peer.Send(3,[0]); peer.Expect(1,3,[1]); peer.Closed();
        }
        using (var peer=Open(f))
        {
            peer.Select(prepared:true); peer.Send(63); peer.Expect(0,53,Cash(token,happy)); peer.Barrier();
            peer.Send(3,[0]); peer.Expect(1,3,[1]); peer.Closed();
        }
        using (var peer=Open(f))
        {
            peer.Select(1); peer.Send(63); peer.Expect(0,53,Cash(token,happy)); peer.Barrier();
            That(f.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id BETWEEN 29940 AND 29942",peer.Id) is [[0L]],
                "第二角色共享最终三现金余额，但不伪造完成历史");
        }
        static byte[] Finish(int id) => [34,0,(byte)id,(byte)(id>>8),255,255,1,0,255,255];
        static byte[] Cash(int token,int happy) => Build(w=>{w.Write((byte)1);w.Write(11);w.Write(token);w.Write(happy);});
    }
}
