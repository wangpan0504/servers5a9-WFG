using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Characters;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    internal static void RunExperienceBonusStorage(CharacterGrowthCatalog growth)
    {
        string root = Path.Combine(Path.GetTempPath(),"s5a9-exp-storage-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root,"game.db");
        try
        {
            var database = new DfoServer.Persistence.SqliteGameDatabase(file);
            var repository = new DfoServer.Persistence.CharacterRepository(file);
            using var owner = new DfoServer.Game.Accounts.AccountOwners().Acquire("bonus-storage",() => { });
            var initial = repository.Create(owner,Fixture("BonusStorage"));
            var (run,authority) = CreateRun(initial);
            run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,20));
            var bonuses = ExperienceBonuses.Calculate(120,100,[new(ExperienceBonusSource.GrowthContract,20),
                new(ExperienceBonusSource.Potion,100),new(ExperienceBonusSource.Guild,10)]);
            var reward = new DungeonClearExperienceReward(100,20,bonuses.Total,0,"bonus-storage","fixture")
                { GrowthContractExperience=20,PotionExperience=100,BonusParts=bonuses.Parts };
            var prepared = run.PrepareSettlement(run.ClearFact!,new byte[] {1},reward).Pending!;
            using (var db = database.Open())
            {
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER bonus_storage_fail AFTER INSERT ON character_dungeon_settlement_receipts BEGIN SELECT RAISE(ABORT,'late xp failure');END";
                command.ExecuteNonQuery();
            }
            bool rejected = false;
            try { repository.CommitDungeonSettlement(owner,initial,run,prepared,growth); }
            catch (Microsoft.Data.Sqlite.SqliteException) { rejected=true; }
            var restored = repository.LoadCharacter(owner,initial.Slot);
            Check(rejected && restored.Revision == initial.Revision && restored.Learning.Experience == initial.Learning.Experience,
                "经验与技能真实写入后的晚SQL故障整笔回滚，保留原准备及角色版本");
            using (var db = database.Open())
            {
                using var command=db.CreateCommand();command.CommandText="DROP TRIGGER bonus_storage_fail";command.ExecuteNonQuery();
            }
            var first=repository.CommitDungeonSettlement(owner,initial,run,prepared,growth);
            var duplicate=repository.CommitDungeonSettlement(owner,initial,run,prepared,growth);
            Check(first.GainedExperience == bonuses.Total && duplicate.Duplicate &&
                duplicate.Character.Learning.Experience == first.Character.Learning.Experience,
                "同一原准备失败后重试成功，持久receipt恢复不重复发各来源经验");
            using (var db = new DfoServer.Persistence.SqliteGameDatabase(file).Open())
            {
                using var command=db.CreateCommand();
                command.CommandText="SELECT other_bonus_experience,json_array_length(bonus_json) FROM character_dungeon_settlement_receipts";
                using var reader=command.ExecuteReader();
                Check(reader.Read() && reader.GetInt64(0)==110 && reader.GetInt64(1)==3,
                    "秘药和公会等其他额度及完整冻结分项持久化，冷打开保持");
            }
        }
        finally { Directory.Delete(root,true); }
    }

    internal static void RunExperienceBonusCore()
    {
        var (run, authority) = CreateRun(new(1,0,"PotionClear",0,Location,Location));
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,20));
        var bonuses = ExperienceBonuses.Calculate(120,100,[new(ExperienceBonusSource.GrowthContract,20),
            new(ExperienceBonusSource.Potion,120),new(ExperienceBonusSource.Guild,10)]);
        var reward = new DungeonClearExperienceReward(100,20,bonuses.Total,0,"bonus-clear","fixture")
            { GrowthContractExperience = 20, PotionExperience = 120, BonusParts = bonuses.Parts };
        var prepared = run.PrepareSettlement(run.ClearFact!,new byte[] {1},reward);
        Check(prepared.Outcome == DungeonRunOutcome.Accepted &&
            ReferenceEquals(prepared.Pending,run.PrepareSettlement(run.ClearFact!,new byte[] {1},reward).Pending),
            "含秘药及未来系统的通关分项通过校验，重试保留同一准备");
        var capped = reward.Applied(127);
        Check(capped.BaseExperience == 100 && capped.RankExperience == 20 && capped.GrowthContractExperience == 7 &&
            capped.PotionExperience == 0 && capped.ExtraExperience == 7 && capped.TotalExperience == 127,
            "含多来源的通关封顶分项与实际经验一致");
    }
}
