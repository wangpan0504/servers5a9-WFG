using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public Character CommitClassChange(AccountOwners.Lease owner, ClassChangePlan plan) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        plan.Learning.Validate();
        if (plan.Request.Target is < 1 or > 5 || (expected.GrowType & 15) == plan.Request.Target ||
            (plan.GrowType & 15) != plan.Request.Target || (plan.GrowType & 240) != (expected.GrowType & 240) ||
            plan.Learning.SelectedTree != expected.Learning.SelectedTree ||
            plan.Learning.Experience != expected.Learning.Experience || plan.Learning.BonusSp != expected.Learning.BonusSp ||
            plan.Learning.BonusTp != expected.Learning.BonusTp || plan.Learning.ExpertContractExpiresAt != expected.Learning.ExpertContractExpiresAt ||
            !plan.Inventory.Changed || plan.Inventory.Reason != "class_change" ||
            plan.Completed.Any(id => id is <= 0 or >= 30000) || plan.Completed.Distinct().Count() != plan.Completed.Length)
            throw new InvalidDataException("职业互转计划改变了未授权字段");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitClassChange");
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        var source = current.Inventory.Find(new(DfoServer.Game.Inventory.Models.InventoryListType.Main, plan.Request.Slot));
        if (current.Id != expected.Id || current.Revision != expected.Revision || current.Job != expected.Job ||
            current.GrowType != expected.GrowType || current.Level != expected.Level || !SameLearning(current.Learning, expected.Learning) ||
            !current.Skills.OrderBy(skill => skill.Id).SequenceEqual(expected.Skills.OrderBy(skill => skill.Id)) ||
            !current.CompletedQuests.SequenceEqual(expected.CompletedQuests) ||
            !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) || source?.Uid != plan.SourceUid ||
            source.IsExpired(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            throw new InvalidOperationException("职业互转的角色、道具或学习快照已变化");
        var committed = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", expected.Revision + 1);
        command.Parameters.AddWithValue("$grow", plan.GrowType);
        command.CommandText = "UPDATE characters SET grow_type=$grow WHERE id=$id AND account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("职业互转的提交版本已变化");
        WriteLearning(db, tx, expected.Id, plan.Learning);
        WriteCanonicalSkills(db, tx, expected.Id, plan.Learning);
        ResetSkillSwitchProfession(db, tx, expected.Id, expected.GrowType, plan.GrowType);
        foreach (int id in plan.Completed)
        {
            command.Parameters.AddWithValue("$quest", id);
            command.CommandText = "INSERT INTO character_completed_quests(character_id,quest_id,daily_period) VALUES($id,$quest,0)";
            command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM character_active_main_quests WHERE character_id=$id AND quest_id=$quest";
            command.ExecuteNonQuery();
            command.Parameters.RemoveAt("$quest");
        }
        committed = Require(db, tx, owner.Account, expected.Name);
        InvalidateQuestStages(db, tx, expected.Id, "class_change");
        tx.Commit();
        return committed;
    });
}
