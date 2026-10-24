using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 合成权威仅验证领域与存储合同；不经过正式准入，不作为真实APC战斗或客户端验收。
internal static class GriefTowerClearTests
{
    internal static void RunResources(PvfArchive pvf,ItemCatalog items,EpicPieceCatalog pieces)
    {
        int checks=Count;
        string root=Path.Combine(Path.GetTempPath(),"S5A9-grief-clear-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path=Path.Combine(root,"game.db");
        try
        {
            var dungeons=SelfTestCatalogs.Dungeons(pvf);
            var rooms=SelfTestCatalogs.Rooms(pvf);
            var growth=SelfTestCatalogs.Growth(pvf);
            var repository=new CharacterRepository(path);
            foreach (var (overflow,roll) in new[] { (false,0),(true,0),(false,9999) })
            {
                using var owner=new AccountOwners().Acquire(overflow ? "grief-full" : roll==0 ? "grief-clear" : "grief-empty",()=>{});
                int rewardCount=roll==0 ? 8 : 0;
                var template=items.Get(10160931);
                var range=ItemSlotBoundService.Range(template.Kind,(ushort)0);
                var originalItems=overflow ? Enumerable.Range(range.Start,range.End-range.Start+1).Select(slot=>
                    new InventoryItem(0,new(InventoryListType.Main,checked((short)slot)),
                        InventoryRewardGrantService.Create(template,template.StackLimit))).ToArray() : [];
                long initialCount=originalItems.Sum(row=>(long)row.Core.Count);
                var character=repository.Create(owner,new NewCharacter("GriefClear",new(38,1,450,234),null)
                {
                    Level=90,DungeonState=CharacterDungeonState.NewOrdinary,
                    Inventory=new([..originalItems]),
                    InitialLearning=new(growth.GetLevelStartExperience(90),
                        Array.AsReadOnly(new[] { new CharacterSkillTree(0,0,0,[]),new CharacterSkillTree(1,0,0,[]) }))
                });
                var floor=dungeons.GriefTower.FloorsByDungeon[5115];
                var definition=dungeons.Get(5115);
                var maze=dungeons.ResolveOrdinaryMaze(definition,definition.Mazes.Single());
                var map=maze.GriefRoom!;
                var entry=new DungeonEntryPlan(character.Id,character.Revision+1,5115,0,byte.MaxValue,0,maze,map,map)
                    { BasisLevel=definition.BasisLevel,GriefLayer=1,DisableFatigueConsumption=true };
                var monsters=DungeonMonsterPlan.Create(rooms,map,character.Id,256,entry.BasisLevel);
                var authority=new Authority();
                That(SinglePlayerDungeonRun.TryCreate(entry,1,1,new([monsters],[]),authority,out var run,out _),
                    "真实悲叹资源可以建立用于存储回归的合成领域实例");
                var before=repository.ReadGriefTower(owner,character.Id);
                var admission=new GriefTowerAdmission(before,new(true,"fixture",20261023,1),5115);
                character=repository.CommitDungeonEntry(owner,character,monsters.NextObjectKey,
                    new(null,0,run!.Snapshot.Identity,map.Id,Grief:admission));
                var progress=repository.ReadGriefTower(owner,character.Id);
                run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
                foreach (var monster in monsters.Monsters.Where(row=>row.CanReportDeath))
                    run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,monster.ObjectKey));
                That(run.ClearFact is not null,"合成权威确认完整敌方演员后才有清图事实");
                int calls=0;
                var clear=GriefTowerClearReward.Prepare(dungeons.GriefTower,progress,5115,character,items,pieces,
                    _=>{calls++;return roll;},new(2026,10,23,7,0,0,TimeSpan.FromHours(8)));
                That(calls==8 && clear.Reward.Items.Length==rewardCount && clear.Delivery.MailItems.Length==(overflow ? rewardCount : 0) &&
                    clear.Delivery.Inventory.After.Items.Sum(row=>row.Core.ItemId==10160931 ? (long)row.Core.Count : 0)==initialCount+(overflow ? 0 : rewardCount),
                    "一次冻结八槽和完整核心，背包可用则合堆，真实满格满堆时整份转邮件");
                if (!overflow && roll==0)
                {
                    var specialFloor=dungeons.GriefTower.FloorsByDungeon.Values.Single(row=>row.Layer==50);
                    var at=new DateTimeOffset(2026,10,23,7,0,0,TimeSpan.FromHours(8));
                    int selectedSlot=2;
                    var special=GriefTowerClearReward.Prepare(dungeons.GriefTower,
                        progress with { State=progress.State with { Layer=50 } },specialFloor.DungeonId,character,items,pieces,
                        _=>++selectedSlot==9 ? 2850 : 9999,at);
                    That(special.Reward.Items is [{ Slot:9,ItemId:10303266,Count:1 },{ Slot:10,ItemId:10160930,Count:1 }] &&
                        special.Delivery.Rewards.SelectMany(row=>row.Items()).Single(core=>core.ItemId==10303266).ExpireTime==at.ToUnixTimeSeconds()+86400,
                        "50层规划同时保留固定岁月和隐藏罐，罐期限按真实模板冻结为获得后24小时");
                }
                var experience=new DungeonClearExperienceReward(1,0,1,0,"fixture","fixture");
                var prepared=run.PrepareSettlement(run.ClearFact!,[1,2,3],experience,grief:clear).Pending!;
                That(prepared is not null && ReferenceEquals(run.PrepareSettlement(run.ClearFact!,[1,2,3],experience,grief:clear).Pending,prepared) && calls==8,
                    "重复准备复用同一冻结结果，不重新抽奖");
                That(run.PrepareSettlement(run.ClearFact!,[1,2,3],experience,grief:clear with { }).Outcome==DungeonRunOutcome.Rejected,
                    "重建同值奖励对象不能替代原冻结候选");
                Execute("CREATE TRIGGER grief_clear_failure BEFORE INSERT ON character_grief_tower_clears BEGIN SELECT RAISE(ABORT,'grief clear rollback'); END");
                string original=JsonSerializer.Serialize(character);
                Reject<SqliteException>(()=>repository.CommitDungeonSettlement(owner,character,run,prepared!,growth),"末段悲叹回执失败");
                That(JsonSerializer.Serialize(repository.LoadCharacter(owner,character.Slot))==original &&
                    repository.ReadGriefTower(owner,character.Id)==progress && Query("SELECT count(*) FROM character_dungeon_settlement_receipts")==0 &&
                    Query("SELECT count(*) FROM mailbox_messages")==0,
                    "末段失败整笔回滚经验、物品、邮件、楼层和通关标志");
                Execute("DROP TRIGGER grief_clear_failure");
                var committed=repository.CommitDungeonSettlement(owner,character,run,prepared!,growth);
                That(!committed.Duplicate && committed.Character.Revision==character.Revision+1 && committed.GriefReward==clear.Reward &&
                    committed.Character.Inventory.Items.Where(row=>row.Core.ItemId==10160931).All(row=>row.Uid>0) &&
                    committed.Character.Inventory.Items.Sum(row=>row.Core.ItemId==10160931 ? (long)row.Core.Count : 0)==initialCount+(overflow ? 0 : rewardCount) &&
                    (committed.MailId is not null)==overflow && Query("SELECT count(*) FROM mailbox_messages")== (overflow ? 1 : 0) &&
                    repository.ReadGriefTower(owner,character.Id).State is { Layer:2,CharacterCleared:true,AccountUsed:1 },
                    "奖励UID、经验、楼层与当天已通关在同一事务提交，不再次扣账号次数");
                string committedSnapshot=JsonSerializer.Serialize(committed.Character);
                Execute("UPDATE character_grief_tower_clears SET reward_json='{}'");
                Reject<InvalidDataException>(()=>repository.CommitDungeonSettlement(owner,character,run,prepared!,growth),
                    "已提交回执奖励被篡改时拒绝恢复，不重新发奖补偿");
                That(JsonSerializer.Serialize(repository.LoadCharacter(owner,character.Slot))==committedSnapshot,
                    "异常回执恢复保持已提交资产不变");
                RestoreReceipt();
                var duplicate=repository.CommitDungeonSettlement(owner,character,run,prepared!,growth);
                That(duplicate.Duplicate && JsonSerializer.Serialize(duplicate.Character)==JsonSerializer.Serialize(committed.Character) &&
                    duplicate.MailId==committed.MailId && Query("SELECT count(*) FROM mailbox_messages")== (overflow ? 1 : 0) &&
                    Query("SELECT count(*) FROM character_grief_tower_clears")==1 && calls==8,
                    "提交后响应丢失精确恢复同一回执，不重复发奖、推进或重抽");
                repository=new(path);
                That(repository.ReadGriefTower(owner,character.Id).State is { Layer:2,CharacterCleared:true } &&
                    JsonSerializer.Serialize(repository.LoadCharacter(owner,character.Slot))==JsonSerializer.Serialize(committed.Character),
                    "重开仓储恢复实际奖励及通关层");
                repository.Delete(owner,repository.LoadDeletionTarget(owner,character.Slot));
                That(Query("SELECT count(*) FROM character_grief_tower_clears")==0 && Query("SELECT used FROM account_grief_tower")==1,
                    "删角清理通关与邮件依赖，账号已用次数保留");

                void RestoreReceipt()
                {
                    using var db=new SqliteConnection("Data Source="+path+";Pooling=False;Foreign Keys=True");db.Open();
                    using var command=db.CreateCommand();
                    command.CommandText="UPDATE character_grief_tower_clears SET reward_json=$reward";
                    command.Parameters.AddWithValue("$reward",JsonSerializer.Serialize(new { clear.Reward,Cores=clear.Delivery.Rewards }));
                    command.ExecuteNonQuery();
                }
            }
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine($"PASS {Count-checks} 悲叹冻结奖励、通关原子性及恢复（领域存储用例）");

        void Execute(string sql) { _=Query(sql); }
        long Query(string sql)
        {
            using var db=new SqliteConnection("Data Source="+path+";Pooling=False;Foreign Keys=True");db.Open();
            using var command=db.CreateCommand();command.CommandText=sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }
    sealed class Authority : DungeonCombatAuthority
    {
        internal ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity,ushort key) => ConfirmDeath(identity,Guid.NewGuid(),key,1);
    }
}
