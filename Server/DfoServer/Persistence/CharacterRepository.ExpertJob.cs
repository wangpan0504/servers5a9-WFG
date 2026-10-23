using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public Character CommitExpertJobClear(AccountOwners.Lease owner, Character expected) => owner.Execute(() =>
    {
        if (expected.ExpertJobType is < 1 or > 4)
            throw new InvalidDataException("没有可放弃的副职业资格");
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.CommandText = "SELECT revision FROM characters WHERE id=$id AND account=$account";
        if (command.ExecuteScalar() is not long revision || revision != expected.Revision)
            throw new InvalidOperationException("放弃副职业的角色快照已变化");
        var updated = expected with { ExpertJobType = 0, ExpertJobExperience = 0, ExpertJobRecipes = System.Collections.Immutable.ImmutableHashSet<int>.Empty, Revision = checked(expected.Revision + 1) };
        using var write = db.BeginWrite("CharacterRepository.CommitExpertJobClear");
        using var transaction = write.Transaction;
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM character_expert_job WHERE character_id=$id AND account=$account AND job_type=$type
            """;
        command.Parameters.AddWithValue("$type", expected.ExpertJobType);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业资格行已变化");
        command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
        command.Parameters.AddWithValue("$revision", expected.Revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("放弃副职业的角色快照已变化");
        transaction.Commit();
        return updated;
    });

    public Character CommitExpertJobExperience(AccountOwners.Lease owner, Character expected, int experience)
    {
        if (experience < 0 || expected.ExpertJobType is < 1 or > 4)
            throw new InvalidDataException("副职业熟练度超出已确认范围");
        return owner.Execute(() =>
        {
            using var db = database.Open();
            using var command = db.CreateCommand();
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$type", expected.ExpertJobType);
            command.Parameters.AddWithValue("$exp", experience);
            command.Parameters.AddWithValue("$revision", expected.Revision);
            using var write = db.BeginWrite("CharacterRepository.CommitExpertJobExperience");
            using var transaction = write.Transaction;
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE character_expert_job SET expertness=$exp
                WHERE character_id=$id AND account=$account AND job_type=$type
                """;
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业熟练度没有写入当前角色");
            command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业熟练度的角色快照已变化");
            transaction.Commit();
            return expected with { ExpertJobExperience = experience, Revision = checked(expected.Revision + 1) };
        });
    }

    public Character CommitExpertJobGiveUp(AccountOwners.Lease owner, Character expected, InventoryPlan wallet, int giveUpState)
    {
        if (expected.ExpertJobType is < 1 or > 4 || giveUpState is < 0 or > 3)
            throw new InvalidDataException("没有可放弃的副职业资格");
        return owner.Execute(() =>
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitExpertJobGiveUp");
            using var transaction = write.Transaction;
            var committed = CommitInventoryInTransaction(db, transaction, owner, expected, wallet);
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$type", expected.ExpertJobType);
            command.CommandText = "DELETE FROM character_expert_job WHERE character_id=$id AND account=$account AND job_type=$type";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业资格行已变化");
            UpsertExpertMeta(command, owner.Account, expected.Id, giveUpState, 1, 300);
            transaction.Commit();
            return committed with
            {
                ExpertJobType = 0,
                ExpertJobExperience = 0,
                ExpertJobRecipes = System.Collections.Immutable.ImmutableHashSet<int>.Empty,
                ExpertGiveUpState = giveUpState,
                ExpertMachineGrade = 1,
                ExpertMachineEndurance = 300
            };
        });
    }

    public Character CommitExpertJobMachine(AccountOwners.Lease owner, Character expected, InventoryPlan wallet, int grade,
        int endurance)
    {
        if (expected.ExpertJobType is < 1 or > 4 || grade is < 0 or > 11 || endurance < 0)
            throw new InvalidDataException("副职业机械状态超出已确认范围");
        return owner.Execute(() =>
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitExpertJobMachine");
            using var transaction = write.Transaction;
            var committed = CommitInventoryInTransaction(db, transaction, owner, expected, wallet);
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            UpsertExpertMeta(command, owner.Account, expected.Id, committed.ExpertGiveUpState, grade, endurance);
            transaction.Commit();
            return committed with { ExpertMachineGrade = grade, ExpertMachineEndurance = endurance };
        });
    }

    public Character CommitExpertJobAction(AccountOwners.Lease owner, DfoServer.Game.ExpertJob.ExpertJobActionPlan plan) =>
        owner.Execute(() =>
        {
            var expected = plan.Expected;
            if (expected.ExpertJobType is not (1 or 3) || plan.Experience < expected.ExpertJobExperience ||
                plan.Endurance < 0 || plan.Endurance >= expected.ExpertMachineEndurance || !plan.Inventory.Changed)
                throw new InvalidDataException("副职业操作计划非法");
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitExpertJobAction");
            using var transaction = write.Transaction;
            var current = CommitInventoryInTransaction(db, transaction, owner, expected, plan.Inventory);
            if (current.ExpertJobType != expected.ExpertJobType || current.ExpertJobExperience != expected.ExpertJobExperience ||
                current.ExpertMachineGrade != expected.ExpertMachineGrade || current.ExpertMachineEndurance != expected.ExpertMachineEndurance ||
                current.ExpertGiveUpState != expected.ExpertGiveUpState || !current.ExpertJobRecipes.SetEquals(expected.ExpertJobRecipes))
                throw new InvalidOperationException("副职业操作资格已经变化");
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE character_expert_job SET expertness=$exp WHERE character_id=$id AND account=$account AND job_type=$type";
            command.Parameters.AddWithValue("$exp", plan.Experience);
            command.Parameters.AddWithValue("$id", current.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$type", current.ExpertJobType);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业操作熟练度未写入");
            UpsertExpertMeta(command, owner.Account, current.Id, current.ExpertGiveUpState, current.ExpertMachineGrade, plan.Endurance);
            transaction.Commit();
            return current with { ExpertJobExperience = plan.Experience, ExpertMachineEndurance = plan.Endurance };
        });

    public Character CommitExpertJobLearning(AccountOwners.Lease owner, DfoServer.Game.ExpertJob.ExpertJobLearningPlan plan) =>
        owner.Execute(() =>
        {
            var expected = plan.Expected;
            if (expected.ExpertJobType != 1 || expected.ExpertJobRecipes.Contains(plan.RecipeId) ||
                expected.ExpertJobRecipes.Count >= byte.MaxValue || plan.RecipeId <= 0 || !plan.Inventory.Changed)
                throw new InvalidDataException("副职业设计图学习计划非法");
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitExpertJobLearning");
            using var transaction = write.Transaction;
            var committed = CommitInventoryInTransaction(db, transaction, owner, expected, plan.Inventory);
            if (committed.ExpertJobType != expected.ExpertJobType || committed.ExpertJobExperience != expected.ExpertJobExperience ||
                !committed.ExpertJobRecipes.SetEquals(expected.ExpertJobRecipes))
                throw new InvalidOperationException("副职业学习资格已经变化");
            var recipes = committed.ExpertJobRecipes.Add(plan.RecipeId);
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE character_expert_job SET learned_recipes=$recipes WHERE character_id=$id AND account=$account AND job_type=$type";
            command.Parameters.AddWithValue("$recipes", System.Text.Json.JsonSerializer.Serialize(recipes.Order().ToArray()));
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$type", expected.ExpertJobType);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业配方没有写入当前角色");
            transaction.Commit();
            return committed with { ExpertJobRecipes = recipes };
        });

    static void UpsertExpertMeta(SqliteCommand command, string account, ushort id, int giveUpState, int grade, int endurance)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$giveup", giveUpState);
        command.Parameters.AddWithValue("$grade", grade);
        command.Parameters.AddWithValue("$endurance", endurance);
        command.CommandText = """
            INSERT INTO character_expert_meta(character_id,account,giveup_state,machine_grade,machine_endurance)
            VALUES($id,$account,$giveup,$grade,$endurance)
            ON CONFLICT(character_id) DO UPDATE SET
              account=excluded.account,
              giveup_state=excluded.giveup_state,
              machine_grade=excluded.machine_grade,
              machine_endurance=excluded.machine_endurance
            WHERE character_expert_meta.account=$account
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业元数据没有写入当前角色");
    }
}
