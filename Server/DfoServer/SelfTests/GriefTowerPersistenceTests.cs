using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GriefTowerPersistenceTests
{
    internal static void RunCore()
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(),"S5A9-grief-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root,"game.db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("grief-test",()=>{});
            using var stranger = owners.Acquire("grief-other",()=>{});
            var role = repository.Create(owner,new NewCharacter("GriefTest",new(38,1,450,234),null)
            {
                Level = 90, DungeonState = CharacterDungeonState.NewOrdinary,
                Inventory = new([new(0,new(InventoryListType.Main,65),
                    new(GriefTowerEntryDecision.InvitationItem,ItemKind.Consumable,2))])
            });
            var other = repository.Create(owner,new NewCharacter("GriefOther",new(38,1,450,234),null)
                { Level=90,DungeonState=CharacterDungeonState.NewOrdinary });
            var fresh = repository.ReadGriefTower(owner,role.Id);
            That(fresh == new GriefTowerProgress(new(0,0,0,false,1),1,Guid.Empty,0),"未参与悲叹的角色读取初始楼层，不写空行");
            Reject<InvalidOperationException>(()=>repository.ReadGriefTower(stranger,role.Id),"悲叹状态拒绝跨账号读取");
            var basic = new GriefTowerAdmission(fresh,new(true,"test-basic",20261023,1),5115);
            var first = Charge(role,basic);
            var before = role;
            role = repository.CommitDungeonEntry(owner,role,300,first);
            var entered = repository.ReadGriefTower(owner,role.Id);
            That(entered.State == new GriefTowerEntryState(20261023,1,20261023,false,1) &&
                entered.InstanceId == first.Identity.InstanceId && entered.TaskAttempt == 1 &&
                role.Revision==before.Revision+1 && role.Inventory.Items.Single().Core.Count==2,
                "基础次数、角色挑战和尝试号共同落库，免费入场不扣票");
            Reject<InvalidOperationException>(()=>repository.CommitDungeonEntry(owner,before,300,first),"重复旧准入不能重复计次");
            var otherProgress = repository.ReadGriefTower(owner,other.Id);
            That(otherProgress.State.AccountUsed==1 && otherProgress.State.CharacterDay==0,"同账号换角共享次数，不共享重试身份");
            var exhausted = new GriefTowerAdmission(otherProgress,new(true,"forged-basic",20261023,1),5115);
            Reject<InvalidDataException>(()=>repository.CommitDungeonEntry(owner,other,300,Charge(other,exhausted)),"存储边界拒绝已耗尽额度的规划");
            var retry = new GriefTowerAdmission(entered,new(true,"test-retry",20261023,1,true),5115);
            var item = role.Inventory.Items.Single();
            var inventory = new InventoryPlan(role.Inventory,role.Inventory with
                { Items=[item with { Core=item.Core with { Value=1 } }] },"grief_retry");
            var retryCharge = Charge(role,retry) with { Items=inventory };
            Execute("CREATE TRIGGER grief_late_failure BEFORE INSERT ON character_grief_tower_entries BEGIN SELECT RAISE(ABORT,'grief rollback'); END");
            Reject<SqliteException>(()=>repository.CommitDungeonEntry(owner,role,301,retryCharge),"悲叹入场末段失败");
            That(JsonSerializer.Serialize(repository.LoadCharacter(owner,role.Slot))==JsonSerializer.Serialize(role) &&
                repository.ReadGriefTower(owner,role.Id)==entered,"账本失败完整回滚门票、角色revision及尝试号");
            Execute("DROP TRIGGER grief_late_failure");
            role=repository.CommitDungeonEntry(owner,role,301,retryCharge);
            That(role.Inventory.Items.Single().Core.Count==1 && repository.ReadGriefTower(owner,role.Id).State.AccountUsed==1,
                "同角色重试恰扣一张邀请函，不再扣账号基础次数");
            repository=new(path);
            That(repository.ReadGriefTower(owner,role.Id).InstanceId==retryCharge.Identity.InstanceId &&
                repository.LoadCharacter(owner,role.Slot).Inventory.Items.Single().Core.Count==1,"重开仓储恢复悲叹挑战与门票");
            Execute("DROP TABLE character_grief_tower_clears; PRAGMA user_version=83");
            repository=new(path);
            That(repository.ReadGriefTower(owner,role.Id).InstanceId==retryCharge.Identity.InstanceId &&
                JsonSerializer.Serialize(repository.LoadCharacter(owner,role.Slot))==JsonSerializer.Serialize(role),
                "v83升级v84只加空通关表，不改准入、库存或revision");
            Execute("DROP TABLE character_grief_tower_clears; CREATE TABLE character_grief_tower_clears(unknown INTEGER); PRAGMA user_version=83");
            Reject<InvalidDataException>(()=>_=new CharacterRepository(path),"v84拒绝接管同名未知通关表");
            Execute("DROP TABLE character_grief_tower_clears; "+GriefTowerSchema.ClearSql);
            repository=new(path);
            var nextDay = new GriefTowerAdmission(repository.ReadGriefTower(owner,other.Id),new(true,"test-next-day",20261024,1),5115);
            other=repository.CommitDungeonEntry(owner,other,300,Charge(other,nextDay));
            That(repository.ReadGriefTower(owner,role.Id).State is { AccountDay:20261024,AccountUsed:1,CharacterDay:20261023 },
                "新游戏日只重置账号日额度，不把其它角色旧挑战改成当天重试");
            repository.Delete(owner,repository.LoadDeletionTarget(owner,role.Slot));
            That(repository.ReadGriefTower(owner,other.Id).State.AccountUsed==1,"删除角色不能返还或清空账号次数");
            Execute("DROP TABLE character_grief_tower_entries; DROP TABLE character_grief_tower; DROP TABLE account_grief_tower; PRAGMA user_version=82");
            string previous=JsonSerializer.Serialize(repository.LoadCharacter(owner,other.Slot));
            repository=new(path);
            That(previous==JsonSerializer.Serialize(repository.LoadCharacter(owner,other.Slot)) &&
                repository.ReadGriefTower(owner,other.Id).State==fresh.State,"v82升级仅建立新账本，不改角色资产和revision");
            Execute("DROP TABLE character_grief_tower_entries; DROP TABLE character_grief_tower; DROP TABLE account_grief_tower; CREATE TABLE character_grief_tower_entries(unknown INTEGER); PRAGMA user_version=82");
            Reject<InvalidDataException>(()=>_=new CharacterRepository(path),"同名未知悲叹表拒绝迁移");
            using var db = Open();
            using var command=db.CreateCommand();command.CommandText="PRAGMA user_version";
            That((long)command.ExecuteScalar()! == 82,"失败迁移不推进版本");
            command.CommandText="SELECT count(*) FROM sqlite_schema WHERE name IN('account_grief_tower','character_grief_tower')";
            That((long)command.ExecuteScalar()! == 0,"末表冲突回滚前两张新表，保留原冲突现场");
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine($"PASS {Count-checks} 悲叹入场账本、原子扣票与升级");

        SqliteConnection Open()
        {
            var db=new SqliteConnection("Data Source="+path+";Pooling=False;Foreign Keys=True");db.Open();return db;
        }
        void Execute(string sql)
        {
            using var db=Open();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();
        }
    }

    static DungeonEntryCharge Charge(Character role,GriefTowerAdmission admission) =>
        new(null,0,new(role.Id,0,role.DungeonState!.LastAttempt+1,Guid.NewGuid(),1),100247,Grief:admission);
}
