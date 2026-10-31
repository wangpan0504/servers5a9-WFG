using DfoServer.Game.Quests;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IExpertJobSetter
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

    // 开发GM：直接设置副职业类型与累计熟练度。没有资格时插入，已有资格时改类型与熟练度；
    // 已学附魔图沿用任务授职的语义保留在原行，不清空。
    public Character SetExpertJob(AccountOwners.Lease owner, Character expected, byte type, int experience)
    {
        if (type is < 1 or > 4 || experience < 0)
            throw new InvalidDataException("副职业类型或累计熟练度超出已确认范围");
        return owner.Execute(() =>
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.SetExpertJob");
            using var transaction = write.Transaction;
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$exp", experience);
            command.CommandText = """
                INSERT INTO character_expert_job(character_id,account,job_type,expertness) VALUES($id,$account,$type,$exp)
                ON CONFLICT(character_id) DO UPDATE SET
                  account=excluded.account,job_type=excluded.job_type,expertness=excluded.expertness
                WHERE character_expert_job.account=$account
                """;
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业资格没有写入当前角色");
            command.Parameters.AddWithValue("$revision", expected.Revision);
            command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业资格的角色快照已变化");
            transaction.Commit();
            return expected with { ExpertJobType = type, ExpertJobExperience = experience, Revision = checked(expected.Revision + 1) };
        });
    }
    public Character CommitExpertJobCraft(AccountOwners.Lease owner, Character expected, InventoryPlan inventory, int experience, QuestLifecyclePlan? tutorial = null) =>
        owner.Execute(() =>
        {
            if (expected.ExpertJobType is not (1 or 2 or 4) || experience < expected.ExpertJobExperience || !inventory.Changed)
                throw new InvalidDataException("副职业制作计划非法");
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitExpertJobCraft");
            using var transaction = write.Transaction;
            var current = CommitInventoryInTransaction(db, transaction, owner, expected, inventory);
            current = CommitExpertJobTutorial(db, transaction, expected, current, tutorial);

            ValidateExpertJobQualification(current, expected);
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE character_expert_job SET expertness=$exp WHERE character_id=$id AND account=$account AND job_type=$type";
            command.Parameters.AddWithValue("$exp", experience);
            command.Parameters.AddWithValue("$id", current.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$type", current.ExpertJobType);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业制作熟练度未写入");
            transaction.Commit();
            return current with { ExpertJobExperience = experience };
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

    public Character CommitExpertJobGiveUp(AccountOwners.Lease owner, Character expected, InventoryPlan wallet, int giveUpState, CharacterLearningState learning,
        IReadOnlyList<int>? clearQuests = null)
    {
        if (expected.ExpertJobType is < 1 or > 4 || giveUpState is < 0 or > 3)
            throw new InvalidDataException("没有可放弃的副职业资格");
        learning.Validate();
        var cleared = (clearQuests ?? []).ToHashSet();
        if (cleared.Any(id => id is <= 0 or >= 30000)) throw new InvalidDataException("副职业清理任务编号非法");
        return owner.Execute(() =>
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitExpertJobGiveUp");
            using var transaction = write.Transaction;
            var committed = CommitInventoryInTransaction(db, transaction, owner, expected, wallet);
            ValidateExpertJobQualification(committed, expected);
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$type", expected.ExpertJobType);
            command.CommandText = "DELETE FROM character_expert_job WHERE character_id=$id AND account=$account AND job_type=$type";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业资格行已变化");
            command.Parameters.AddWithValue("$quest", 0);
            foreach (int quest in cleared)
            {
                command.Parameters["$quest"].Value = quest;
                command.CommandText = "DELETE FROM character_active_main_quests WHERE character_id=$id AND quest_id=$quest";
                command.ExecuteNonQuery();
                command.CommandText = "DELETE FROM character_completed_quests WHERE character_id=$id AND quest_id=$quest";
                command.ExecuteNonQuery();
            }
            UpsertExpertMeta(command, owner.Account, expected.Id, giveUpState, 1, 300);
            if (!SameLearning(expected.Learning, learning))
            {
                WriteLearning(db, transaction, expected.Id, learning);
                WriteCanonicalSkills(db, transaction, expected.Id, learning);
            }
            transaction.Commit();
            return committed with
            {
                ExpertJobType = 0,
                Learning = learning,
                Skills = learning.Initialized ? learning.Trees.Single(tree => tree.Context == learning.ActiveContext).Skills
                    .Select(skill => new CharacterSkill(skill.Id, skill.Level)).OrderBy(skill => skill.Id).ToArray() : committed.Skills,
                ExpertJobExperience = 0,
                ExpertJobRecipes = System.Collections.Immutable.ImmutableHashSet<int>.Empty,
                ExpertGiveUpState = giveUpState,
                ExpertMachineGrade = 1,
                ExpertMachineEndurance = 300,
                ActiveMainQuests = committed.ActiveMainQuests.Where(row => !cleared.Contains(row.QuestId)).ToArray(),
                CompletedQuests = committed.CompletedQuests.Where(id => !cleared.Contains(id)).ToArray(),
                QuestDailyClaims = committed.QuestDailyClaims.RemoveRange(cleared),
                QuestAnswers = committed.QuestAnswers.RemoveRange(cleared)
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
            ValidateExpertJobQualification(committed, expected);
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
            if (!plan.Succeeded && plan.Tutorial is not null) throw new InvalidDataException("失败操作不能推进副职业教程");
            current = CommitExpertJobTutorial(db, transaction, expected, current, plan.Tutorial);
            ValidateExpertJobQualification(current, expected);
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
            if (expected.ExpertJobType is not (1 or 2 or 4) || expected.ExpertJobRecipes.Contains(plan.RecipeId) ||
                expected.ExpertJobRecipes.Count >= byte.MaxValue || plan.RecipeId <= 0 || !plan.Inventory.Changed)
                throw new InvalidDataException("副职业设计图学习计划非法");
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitExpertJobLearning");
            using var transaction = write.Transaction;
            var committed = CommitInventoryInTransaction(db, transaction, owner, expected, plan.Inventory);
            ValidateExpertJobQualification(committed, expected);
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

    static Character CommitExpertJobTutorial(SqliteConnection db, SqliteTransaction transaction, Character expected,
        Character current, QuestLifecyclePlan? tutorial)
    {
        if (tutorial is null) return current;
        if (!ReferenceEquals(tutorial.Expected, expected) || tutorial.Action != QuestLifecycleAction.Condition ||
            tutorial.QuestId != QuestExpertJobProgress.TutorialQuest(expected.ExpertJobType) || tutorial.AcceptanceId == Guid.Empty ||
            tutorial.ActiveAfter.Single(row => row.AcceptanceId == tutorial.AcceptanceId).ConditionState != 0)
            throw new InvalidDataException("副职业教程计划非法");
        var target = current.ActiveMainQuests.Single(row => row.AcceptanceId == tutorial.AcceptanceId && row.QuestId == tutorial.QuestId);
        if (target.ConditionState != 1) throw new InvalidOperationException("副职业教程接取身份已变化");
        var active = current.ActiveMainQuests.Select(row => row == target ? row with { ConditionState = 0 } : row).ToArray();
        WriteQuestConditions(db, transaction, current.Id, current.ActiveMainQuests, active);
        return current with { ActiveMainQuests = active };
    }

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
