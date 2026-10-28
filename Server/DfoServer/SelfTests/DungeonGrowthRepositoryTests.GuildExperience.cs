using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonGrowthRepositoryTests
{
    static void RunGuildExperienceStorage(AccountOwners.Lease owner, NewCharacter fixture,
        CharacterGrowthCatalog growth, string root)
    {
        foreach (string state in new[] { "valid", "expired", "left", "rejoined" })
        {
            string path = Path.Combine(root, "guild-experience-" + state + ".db");
            var repository = new CharacterRepository(path);
            var initial = repository.Create(owner, fixture with { Name = "GuildXp" });
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True");
            db.Open();
            void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(1,'经验公会','guildxp',{initial.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{initial.Id},1,1000);
                INSERT INTO guild_benefits(guild_id,type,value,expires_unix) VALUES(1,1,10,{now + 600});
                """);
            var entitlement = repository.ReadGuildKillEntitlement(initial.Id, now)!;
            var bonuses = ExperienceBonuses.Calculate(100, 100,
                [new(ExperienceBonusSource.Guild, 10), new(ExperienceBonusSource.GrowthContract, 20)])
                with { GuildEntitlement = entitlement };
            var (run, authority) = CreateRun(initial);
            var pending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 20, 1)).Pending!;
            var reward = new DfoServer.Game.Dungeons.DungeonExperienceReward("fixture-only",
                DfoServer.GameWorld.Dungeons.DungeonKillExperienceCatalog.ResourcePath,
                pending.Monster.MonsterId, pending.Monster.CandidateLevel, bonuses.Total)
                { Bonuses = bonuses, BaseAmount = 100 };
            if (state == "valid")
            {
                Sql("CREATE TRIGGER fail_guild_xp BEFORE INSERT ON guild_experience_receipts BEGIN SELECT RAISE(ABORT,'guild xp rollback'); END");
                Reject<SqliteException>(() => repository.CommitDungeonGrowth(owner, initial, run, pending, reward, growth),
                    "公会经验分项回执晚期失败整笔回滚");
                That(repository.LoadCharacter(owner, initial.Slot).Revision == initial.Revision,
                    "公会分项故障不留下经验或角色revision");
                Sql("DROP TRIGGER fail_guild_xp");
            }
            if (state == "expired") Sql("UPDATE guild_benefits SET expires_unix=1");
            if (state == "left") Sql("DELETE FROM guild_members");
            if (state == "rejoined") Sql("UPDATE guild_members SET joined_at_unix=1001");
            var commit = repository.CommitDungeonGrowth(owner, initial, run, pending, reward, growth);
            uint expected = state == "valid" ? 130u : 120u;
            That(commit.GainedExperience == expected && commit.CommittedBonuses?.Total == expected &&
                commit.CommittedBonuses!.Amount(ExperienceBonusSource.GrowthContract) == 20,
                "提交重验公会及本次入会身份，失效仅去掉公会分项并保留其他冻结资格");
            Sql("DELETE FROM guild_members; UPDATE guild_benefits SET expires_unix=1");
            var replay = new CharacterRepository(path).CommitDungeonGrowth(owner, initial, run, pending, reward, growth);
            That(replay.Duplicate && replay.GainedExperience == expected && replay.CommittedBonuses?.Total == expected &&
                replay.Character.Revision == commit.Character.Revision,
                "提交返回丢失后从持久分项回执恢复，不随之后退会或到期重算已到账经验");
        }
    }
}
