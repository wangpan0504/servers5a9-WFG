using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Tutorials;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static CharacterTutorialState? ReadTutorial(SqliteConnection db, ushort id, SqliteTransaction? tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT dungeon_id,completed,reward_received FROM character_tutorial_state WHERE character_id=$id";
        query.Parameters.AddWithValue("$id", id);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;
        var state = new CharacterTutorialState(checked((uint)reader.GetInt64(0)), reader.GetInt32(1) != 0, reader.GetInt32(2) != 0);
        state.Validate();
        return state;
    }

    static void CreateTutorial(SqliteConnection db, SqliteTransaction tx, string account, ushort id, CharacterTutorialState? state)
    {
        if (state is null) return;
        state.Validate();
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO character_tutorial_state VALUES($id,$account,$dungeon,$completed,$reward)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$dungeon", state.DungeonId); command.Parameters.AddWithValue("$completed", state.Completed);
        command.Parameters.AddWithValue("$reward", state.RewardReceived); command.ExecuteNonQuery();
    }

    public TutorialCheckpointResult CommitTutorialCheckpoint(AccountOwners.Lease owner, TutorialCheckpointPlan plan) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        plan.After.Validate();
        if (expected.Tutorial is not { } before || plan.EventId == Guid.Empty || plan.After.DungeonId != before.DungeonId ||
            before.Completed && !plan.After.Completed || before.RewardReceived && !plan.After.RewardReceived ||
            plan.Kind is not ("death" or "settlement" or "reward" or "skip" or "clear") ||
            plan.Identity is { } identity && (identity.CharacterId != expected.Id || identity.QuestId != 0 ||
                identity.TaskAttempt != expected.DungeonState?.LastAttempt || plan.MapId <= 0) ||
            plan.Experience == 0 && plan.Growth is not null || plan.Experience > 0 && (plan.Growth is not { } growth ||
                growth.CharacterId != expected.Id || growth.Revision != expected.Revision ||
                growth.RequestedExperience != plan.Experience || !SameLearning(growth.Before, expected.Learning)))
            throw new InvalidDataException("训练检查点不是原角色、实例和完整规划");
        if (plan.Kind != "reward" && (plan.Inventory.Changed || plan.After.RewardReceived != before.RewardReceived) ||
            plan.Kind == "reward" && (before.Completed || before.RewardReceived || !plan.After.RewardReceived ||
                plan.After.Completed != before.Completed || plan.Experience != 0 || plan.Position != expected.Position) ||
            plan.Kind is "skip" or "clear" && (!plan.After.Completed || plan.Experience != 0) ||
            plan.Kind == "death" && (plan.Death is null || plan.Death.Death.EventId != plan.EventId ||
                plan.Death.Death.Identity != plan.Identity || plan.Death.Death.MonsterKey != plan.Subject ||
                plan.Growth is null || plan.DeathReward is not { Amount: > 0 } reward || reward.Amount != plan.Experience ||
                reward.MonsterId != plan.Death.Monster.MonsterId || reward.MonsterLevel != plan.Death.Monster.CandidateLevel ||
                reward.ResourceSource != DungeonKillExperienceCatalog.ResourcePath || string.IsNullOrWhiteSpace(reward.RuleId) || reward.RuleId.Length > 128 ||
                plan.After.Completed != (before.Completed || plan.Death.Result.ClearFact is not null)) ||
            plan.Kind != "death" && (plan.Death is not null || plan.DeathReward is not null || plan.Loot is not null) ||
            plan.Kind == "settlement" && !plan.After.Completed)
            throw new InvalidDataException("训练检查点类型与状态或资产变化不一致");
        ValidateInventoryPlan(expected, plan.Inventory);
        if (plan.Inventory.ChangedSlots.Any(slot => slot.List != InventoryListType.Main) ||
            !SameInventory(plan.Inventory.After, new InventoryPlan(plan.Inventory.Before with { Items = plan.Inventory.After.Items },
                plan.Inventory.After, "tutorial_metadata")))
            throw new InvalidDataException("训练检查点不能修改钱包、仓库、容量或其他非物品资产");
        string snapshot = JsonSerializer.Serialize(new
        {
            Before = before, plan.After, plan.Position, plan.Growth, plan.DeathReward,
            plan.Inventory.Reason,
            Slots = plan.Inventory.ChangedSlots.Select(slot => new
            {
                Slot = slot, Before = plan.Inventory.BeforeIndex.Find(slot), After = plan.Inventory.AfterIndex.Find(slot)
            }),
            Currencies = plan.Inventory.ChangedCurrencies.Select(kind => new
            {
                Kind = kind, Before = DfoServer.Game.Currency.CurrencyWallet.Get(plan.Inventory.Before, kind),
                After = DfoServer.Game.Currency.CurrencyWallet.Get(plan.Inventory.After, kind)
            })
        });
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var current = Require(db, tx, owner.Account, expected.Name);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id); command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$event", plan.EventId.ToString("N")); command.Parameters.AddWithValue("$kind", plan.Kind);
        command.Parameters.AddWithValue("$instance", (plan.Identity?.InstanceId ?? Guid.Empty).ToString("N"));
        command.Parameters.AddWithValue("$attempt", plan.Identity?.TaskAttempt ?? 0); command.Parameters.AddWithValue("$epoch", plan.Identity?.Epoch ?? 0);
        command.Parameters.AddWithValue("$map", plan.MapId); command.Parameters.AddWithValue("$subject", plan.Subject);
        command.Parameters.AddWithValue("$experience", plan.Experience); command.Parameters.AddWithValue("$before", expected.Revision);
        command.Parameters.AddWithValue("$snapshot", snapshot);
        command.CommandText = """
            SELECT account,kind,instance_id,task_attempt,epoch,map_id,subject,experience,before_revision,plan_snapshot
            FROM character_tutorial_receipts WHERE character_id=$id AND event_id=$event
            """;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != owner.Account || reader.GetString(1) != plan.Kind ||
                    reader.GetString(2) != (plan.Identity?.InstanceId ?? Guid.Empty).ToString("N") ||
                    reader.GetInt64(3) != (plan.Identity?.TaskAttempt ?? 0) || reader.GetInt64(4) != (plan.Identity?.Epoch ?? 0) ||
                    reader.GetInt32(5) != plan.MapId || reader.GetInt64(6) != plan.Subject ||
                    reader.GetInt64(7) != plan.Experience || reader.GetInt64(8) != expected.Revision || reader.GetString(9) != snapshot)
                    throw new InvalidDataException("同一训练事件的来源或奖励发生变化");
                reader.Close();
                if (plan.Death is { } original) VerifyDungeonLoot(db, tx, owner.Account, expected.Id, original.Death.EventId, plan.Loot);
                tx.Commit();
                return new TutorialCheckpointResult(current, true);
            }
        }
        if (current.Id != expected.Id || current.Revision != expected.Revision || current.Tutorial != before ||
            current.DungeonState != expected.DungeonState || current.Level != expected.Level || current.Job != expected.Job ||
            current.GrowType != expected.GrowType || current.Position != expected.Position || !SameLearning(current.Learning, expected.Learning))
            throw new InvalidOperationException("训练角色、revision、状态或成长前提已变化");
        current = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        command.Parameters.AddWithValue("$revision", current.Revision);
        command.Parameters.AddWithValue("$increment", plan.Inventory.Changed ? 0 : 1);
        command.Parameters.AddWithValue("$level", plan.Growth?.TargetLevel ?? current.Level);
        command.Parameters.AddWithValue("$targetExperience", checked((long)(plan.Growth?.Learning.Experience ?? current.Learning.Experience)));
        Bind(command, "", plan.Position);
        command.CommandText = """
            UPDATE characters SET revision=revision+$increment,level=$level,experience=$targetExperience,
              town=$town,area=$area,x=$x,y=$y,direction=$direction
            WHERE id=$id AND account=$account AND revision=$revision
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("训练提交角色revision变化");
        if (plan.Growth is { } progress)
        {
            WriteLearning(db, tx, expected.Id, progress.Learning);
            WriteCanonicalSkills(db, tx, expected.Id, progress.Learning);
        }
        command.Parameters.AddWithValue("$completed", plan.After.Completed); command.Parameters.AddWithValue("$reward", plan.After.RewardReceived);
        command.CommandText = "UPDATE character_tutorial_state SET completed=$completed,reward_received=$reward WHERE character_id=$id AND account=$account";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("训练状态owner缺失");
        if (plan.Death is { } death)
        {
            WriteDungeonGrowthReceipt(db, tx, owner, expected, death, plan.DeathReward!, plan.Growth!);
            SaveDungeonLoot(db, tx, owner.Account, expected, death, plan.Loot);
        }
        command.Parameters["$revision"].Value = checked(expected.Revision + 1);
        command.CommandText = """
            INSERT INTO character_tutorial_receipts VALUES($id,$account,$event,$kind,$instance,$attempt,$epoch,$map,$subject,$experience,$before,$revision,$snapshot)
            """;
        command.ExecuteNonQuery();
        var committed = Require(db, tx, owner.Account, expected.Name);
        if (committed.Revision != expected.Revision + 1 || committed.Tutorial != plan.After || committed.Position != plan.Position)
            throw new InvalidOperationException("训练持久结果与规划不一致");
        tx.Commit();
        return new TutorialCheckpointResult(committed, false);
    });
}
