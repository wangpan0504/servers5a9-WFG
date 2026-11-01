using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Guild;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    internal static void RunGuildMileageStorage(CharacterGrowthCatalog growth)
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "guild-clear-mileage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path, guildDungeonMileage: 10);
            var owners = new AccountOwners(); using var owner = owners.Acquire("guild-clear", static () => { });
            var initial = repository.Create(owner, Fixture("GuildClear"));
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            long Number(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
            }
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(1,'清图积分','clear-mileage',{initial.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{initial.Id},1,1000);
                """);
            (SinglePlayerDungeonRun Run, PreparedDungeonSettlement Pending) Prepare(Character role,
                Func<DungeonEntryPlan, DungeonEntryPlan>? configure = null)
            {
                var (run, authority) = CreateRun(role, entry =>
                {
                    var ordinary = entry with
                    {
                        GuildMileageEligible = true, QuestId = 0, RouteQuestId = 0, ActiveQuestSlot = byte.MaxValue
                    };
                    return configure?.Invoke(ordinary) ?? ordinary;
                });
                run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
                var prepared = run.PrepareSettlement(run.ClearFact!, [1, 2, 3], Reward).Pending!;
                uint guildId = checked((uint)Number($"SELECT guild_id FROM guild_members WHERE character_id={role.Id}"));
                prepared.FreezeGuildContribution([new(role.Id, guildId, guildId == 0 ? 0 : 1000)],
                    run.Entry.GuildMileageEligible && !run.Entry.IsHellLegend && !run.Entry.IsQuestReplay
                        ? GuildContributionSource.RecommendedDungeon : null);
                return (run, prepared);
            }
            var pending = Prepare(initial);
            Sql("CREATE TRIGGER mileage_clear_fail AFTER INSERT ON guild_economy_events BEGIN SELECT RAISE(ABORT,'guild mileage rollback'); END");
            Reject<SqliteException>(() => repository.CommitDungeonSettlement(owner, initial, pending.Run, pending.Pending, growth),
                "真实通关在公会积分回执末段失败");
            That(SkillRepositoryTests.Equal(repository.LoadCharacter(owner, initial.Slot), initial) &&
                Number("SELECT public_funds FROM guilds WHERE guild_id=1") == 0 &&
                Number("SELECT count(*) FROM guild_economy_events") == 0 &&
                Number("SELECT count(*) FROM character_dungeon_settlement_receipts") == 0,
                "公共积分失败时原通关经验、角色版本和所有耐久回执同事务回滚");
            Sql("DROP TRIGGER mileage_clear_fail");
            var first = repository.CommitDungeonSettlement(owner, initial, pending.Run, pending.Pending, growth);
            That(first.GuildMileageGuildId == 1 && Number("SELECT public_funds FROM guilds WHERE guild_id=1") == 10 &&
                Number("SELECT count(*) FROM guild_economy_events WHERE guild_id=1 AND amount=10") == 1 &&
                Number("SELECT count(*) FROM guild_mileage_history WHERE kind=3 AND amount=10") == 1,
                "自然通关和公共积分10一次提交，记录kind3真实收入");
            That(first.GuildContribution is { Points: 10, GuildId: 1 } &&
                Number($"SELECT contribution FROM guild_members WHERE character_id={initial.Id}") == 10 &&
                Number("SELECT count(*) FROM guild_contribution_events WHERE source=3 AND points=10") == 1,
                "真实通关独立累计个人贡献10，不混入公共积分账本");
            var replay = new CharacterRepository(path, guildDungeonMileage: 10)
                .CommitDungeonSettlement(owner, initial, pending.Run, pending.Pending, growth);
            That(replay.Duplicate && replay.GuildMileageGuildId == 1 &&
                Number("SELECT public_funds FROM guilds WHERE guild_id=1") == 10 &&
                Number("SELECT count(*) FROM guild_mileage_history") == 1,
                "重新打开仓储恢复原通关回执，不重复增加公共积分，仍携带原刷新归属");
            That(Number($"SELECT contribution FROM guild_members WHERE character_id={initial.Id}") == 10 &&
                Number("SELECT count(*) FROM guild_contribution_events WHERE source=3") == 1,
                "重新打开仓储重放原通关不重复累计个人贡献");
            foreach (var (name, configure) in new (string, Func<DungeonEntryPlan, DungeonEntryPlan>)[]
            {
                ("NoCandidate", entry => entry with { GuildMileageEligible = false }),
                ("HellClear", entry => entry with { IsHellLegend = true }),
                ("ClassClear", entry => entry with { IsQuestReplay = true })
            })
            {
                var role = repository.Create(owner, Fixture(name));
                Sql($"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{role.Id},4,1000)");
                var excluded = Prepare(role, configure);
                var result = repository.CommitDungeonSettlement(owner, role, excluded.Run, excluded.Pending, growth);
                That(result.GuildMileageGuildId == 0 && Number("SELECT public_funds FROM guilds WHERE guild_id=1") == 10,
                    "非普通合资格路线不借结算重复获得公会积分: " + name);
            }
            var outsider = repository.Create(owner, Fixture("ClearNoGuild"));
            var beforeJoin = Prepare(outsider);
            var unjoined = repository.CommitDungeonSettlement(owner, outsider, beforeJoin.Run, beforeJoin.Pending, growth);
            Sql($"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{outsider.Id},4,1000)");
            var joinedReplay = repository.CommitDungeonSettlement(owner, outsider, beforeJoin.Run, beforeJoin.Pending, growth);
            That(unjoined.GuildMileageGuildId == 0 && joinedReplay.GuildMileageGuildId == 0 &&
                Number("SELECT public_funds FROM guilds WHERE guild_id=1") == 10 &&
                Number("SELECT count(*) FROM guild_economy_events WHERE guild_id=0 AND amount=0") == 1,
                "通关时无会籍记零耐久事实，事后入会不能追领旧实例积分");
            That(Number($"SELECT contribution FROM guild_members WHERE character_id={outsider.Id}") == 0 &&
                Number("SELECT count(*) FROM guild_contribution_events WHERE guild_id IS NULL AND points=0") == 1,
                "入场无会籍的个人贡献记录零归属，后续入会不能追领");
            var midJoinRole = repository.Create(owner, Fixture("JoinMid"));
            var midJoin = Prepare(midJoinRole);
            Sql($"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{midJoinRole.Id},4,1000)");
            var midJoinResult = repository.CommitDungeonSettlement(owner, midJoinRole, midJoin.Run, midJoin.Pending, growth);
            That(midJoinResult.GuildMileageGuildId == 0 && midJoinResult.GuildContribution is { Points: 0, GuildId: 0 } &&
                Number("SELECT public_funds FROM guilds WHERE guild_id=1") == 10 &&
                Number($"SELECT contribution FROM guild_members WHERE character_id={midJoinRole.Id}") == 0,
                "入场无会籍而清图前入会不能追取新会公共积分或个人贡献");
            var rejoinRole = repository.Create(owner, Fixture("RejoinMid"));
            Sql($"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{rejoinRole.Id},4,1000)");
            var rejoin = Prepare(rejoinRole);
            Sql($"UPDATE guild_members SET joined_at_unix=1001 WHERE character_id={rejoinRole.Id}");
            var rejoinResult = repository.CommitDungeonSettlement(owner, rejoinRole, rejoin.Run, rejoin.Pending, growth);
            That(rejoinResult.GuildMileageGuildId == 0 && rejoinResult.GuildContribution is { Points: 0, GuildId: 0 } &&
                Number("SELECT public_funds FROM guilds WHERE guild_id=1") == 10,
                "副本中退会重入的新会籍代次不能延续旧副本公共积分或个人贡献");
            var rollbackRole = repository.Create(owner, Fixture("ContribFail"));
            Sql($"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{rollbackRole.Id},4,1000)");
            var rollback = Prepare(rollbackRole);
            Sql("CREATE TRIGGER contribution_clear_fail AFTER INSERT ON guild_contribution_events BEGIN SELECT RAISE(ABORT,'guild contribution rollback'); END");
            Reject<SqliteException>(() => repository.CommitDungeonSettlement(owner, rollbackRole, rollback.Run, rollback.Pending, growth),
                "真实通关个人贡献写入末段故障");
            That(SkillRepositoryTests.Equal(repository.LoadCharacter(owner, rollbackRole.Slot), rollbackRole) &&
                Number("SELECT public_funds FROM guilds WHERE guild_id=1") == 10 &&
                Number($"SELECT contribution FROM guild_members WHERE character_id={rollbackRole.Id}") == 0 &&
                Number($"SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id={rollbackRole.Id}") == 0,
                "个人贡献末段失败时原经验、公共资金、贡献及结算回执全部回滚");
            Sql("DROP TRIGGER contribution_clear_fail");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} 自然通关公共积分原子事务及重开幂等断言");
    }
}
