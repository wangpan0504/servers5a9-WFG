using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public DungeonAltarSettlementCommit CommitDungeonAltarSettlement(AccountOwners.Lease owner, SinglePlayerDungeonRun run,
        PreparedDungeonAltarSettlement prepared, CharacterGrowthCatalog growth) => owner.Execute(() =>
    {
        if (!run.IsPendingAltarSettlement(prepared)) throw new InvalidOperationException("祭坛结算不是原实例冻结结果");
        var expected = prepared.Expected;
        var fact = prepared.Fact;
        prepared.Reward.Validate();
        ValidateInventoryPlan(expected, prepared.Inventory);
        if (fact.Identity.CharacterId != expected.Id || fact.DungeonId is not (11006 or 11007) ||
            fact.CompletedRounds != prepared.Reward.CompletedRounds || fact.MaximumRounds != prepared.Reward.MaximumRounds ||
            prepared.Inventory.Reason != "altar_clear_reward") throw new InvalidDataException("祭坛结算身份或奖励变化");
        string rewardJson = JsonSerializer.Serialize(prepared.Reward);
        string deathsJson = JsonSerializer.Serialize(fact.DeathEvents);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitDungeonAltarSettlement", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.DungeonState?.LastAttempt != fact.Identity.TaskAttempt)
            throw new InvalidOperationException("祭坛结算角色或尝试已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$instance", fact.Identity.InstanceId.ToString("N"));
        command.CommandText = """
            SELECT event_id,task_attempt,epoch,dungeon_id,completed_rounds,maximum_rounds,play_milliseconds,
              cleared,death_events,reward_json,gained_experience,proficiency_gained
            FROM dungeon_altar_clear_receipts WHERE character_id=$id AND account=$account AND instance_id=$instance
            """;
        bool duplicate;
        uint gained = 0, proficiency = 0;
        using (var reader = command.ExecuteReader())
        {
            duplicate = reader.Read();
            if (duplicate)
            {
                if (reader.GetString(0) != prepared.EventId.ToString("N") || reader.GetInt64(1) != fact.Identity.TaskAttempt ||
                    reader.GetInt64(2) != fact.Identity.Epoch || reader.GetInt64(3) != fact.DungeonId ||
                    reader.GetInt64(4) != fact.CompletedRounds || reader.GetInt64(5) != fact.MaximumRounds ||
                    reader.GetInt64(6) != fact.PlayMilliseconds || reader.GetBoolean(7) != fact.Cleared ||
                    reader.GetString(8) != deathsJson || reader.GetString(9) != rewardJson)
                    throw new InvalidDataException("同一祭坛实例的冻结奖励被修改");
                gained = checked((uint)reader.GetInt64(10));
                proficiency = checked((uint)reader.GetInt64(11));
            }
        }
        if (!duplicate)
        {
            ValidateCurrentInventoryPlan(current, expected, prepared.Inventory);
            if (!SameLearning(current.Learning, expected.Learning) || current.Level != expected.Level || current.Job != expected.Job ||
                current.GrowType != expected.GrowType || !current.Skills.OrderBy(row => row.Id).SequenceEqual(expected.Skills.OrderBy(row => row.Id)))
                throw new InvalidOperationException("祭坛结算完整成长前提变化");
            command.CommandText = """
                SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$id AND account=$account AND
                  instance_id=$instance AND task_attempt=$attempt AND dungeon_id=$dungeon AND kind=$kind
                """;
            command.Parameters.AddWithValue("$attempt", fact.Identity.TaskAttempt);
            command.Parameters.AddWithValue("$dungeon", fact.DungeonId);
            command.Parameters.AddWithValue("$kind", fact.DungeonId == 11006 ? 1 : 2);
            if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new InvalidOperationException("祭坛结算缺少已提交入场事实");
            // 复用库存唯一写入入口，包含金币、UID、宠物/装扮detail、溢出邮件与任务刷新；事务由本次结算持有。
            var assets = CommitInventoryInTransaction(db, tx, owner, current, expected, prepared.Inventory);
            if (prepared.Reward.Experience > 0)
            {
                var result = CharacterGrowthPlan.AddExperience(assets, prepared.Reward.Experience, growth);
                if (!result.Success || result.Plan is not { } plan) throw new InvalidOperationException("祭坛成长规划失败");
                command.Parameters.AddWithValue("$revision", assets.Revision);
                command.Parameters.AddWithValue("$level", plan.TargetLevel);
                command.Parameters.AddWithValue("$experience", checked((long)plan.Learning.Experience));
                command.CommandText = """
                    UPDATE characters SET level=$level,experience=$experience,revision=revision+1
                    WHERE id=$id AND account=$account AND revision=$revision
                    """;
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("祭坛结算revision变化");
                WriteLearning(db, tx, expected.Id, plan.Learning);
                WriteCanonicalSkills(db, tx, expected.Id, plan.Learning);
                if (quests is not null)
                    WriteQuestConditions(db, tx, current.Id, assets.ActiveMainQuests, Game.Quests.QuestStateProgress.Refresh(
                        assets.ActiveMainQuests, assets with { Level = plan.TargetLevel, Learning = plan.Learning }, quests));
                if (current.DungeonState is { } dungeon)
                {
                    var amount = ProficiencyProgression.Gain(plan.PreviousLevel, plan.Before.Experience, prepared.Reward.Experience, growth);
                    if (ProficiencyProgression.Advance(new(dungeon.ProficiencyLevel, dungeon.ProficiencyExperience), amount, growth) is { } advanced)
                    {
                        WriteProficiency(db, tx, owner.Account, expected.Id, advanced.Before, advanced.After);
                        proficiency = checked((uint)advanced.Credited);
                    }
                }
                if (CreditAdventure(db, tx, owner.Account, expected.Id, Game.Adventure.AdventureCreditSource.Earned,
                    prepared.Reward.Experience, EffectivePosition(current.Level, current.Learning.Experience), current.Level,
                    plan.TargetLevel, DateTimeOffset.UtcNow) is { } credit)
                    WriteAdventureCredit(db, tx, owner.Account, credit, Game.Adventure.AdventureCreditSource.Earned, adventureCatalog!);
                gained = checked((uint)plan.GainedExperience);
            }
            command.Parameters.AddWithValue("$event", prepared.EventId.ToString("N"));
            command.Parameters.AddWithValue("$epoch", fact.Identity.Epoch);
            command.Parameters.AddWithValue("$rounds", fact.CompletedRounds);
            command.Parameters.AddWithValue("$maximum", fact.MaximumRounds);
            command.Parameters.AddWithValue("$milliseconds", (long)fact.PlayMilliseconds);
            command.Parameters.AddWithValue("$cleared", fact.Cleared ? 1 : 0);
            command.Parameters.AddWithValue("$deaths", deathsJson);
            command.Parameters.AddWithValue("$reward", rewardJson);
            command.Parameters.AddWithValue("$gained", gained);
            command.Parameters.AddWithValue("$proficiency", proficiency);
            command.CommandText = """
                INSERT INTO dungeon_altar_clear_receipts(character_id,account,instance_id,event_id,task_attempt,epoch,dungeon_id,
                  completed_rounds,maximum_rounds,play_milliseconds,cleared,death_events,reward_json,gained_experience,proficiency_gained)
                VALUES($id,$account,$instance,$event,$attempt,$epoch,$dungeon,$rounds,$maximum,$milliseconds,$cleared,$deaths,$reward,$gained,$proficiency)
                """;
            command.ExecuteNonQuery();
            current = Require(db, tx, owner.Account, expected.Name);
        }
        command.CommandText = """
            SELECT play_milliseconds,completed_rounds FROM dungeon_altar_clear_receipts WHERE character_id=$id AND account=$account
              AND dungeon_id=$dungeon ORDER BY completed_rounds DESC,play_milliseconds ASC LIMIT 1
            """;
        if (!command.Parameters.Contains("$dungeon")) command.Parameters.AddWithValue("$dungeon", fact.DungeonId);
        uint bestTime;
        int bestRounds;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidDataException("祭坛历史最佳缺少本次回执");
            bestTime = checked((uint)reader.GetInt64(0));
            bestRounds = checked((int)reader.GetInt64(1));
        }
        tx.Commit();
        return new DungeonAltarSettlementCommit(current, duplicate, gained, proficiency,
            new(expected.Inventory, current.Inventory, "altar_clear_reward", overflowItems: prepared.Inventory.OverflowItems), bestTime, bestRounds);
    });
}
