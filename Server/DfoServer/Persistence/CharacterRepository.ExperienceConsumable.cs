using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public Character CommitExperienceConsumable(AccountOwners.Lease owner, ExperienceConsumablePlan plan, DateTimeOffset now) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        var growth = plan.Growth;
        if (expected.DungeonState is null || expected.Inventory.Find(plan.Source.Position) != plan.Source ||
            plan.Source.Uid <= 0 || plan.Source.Core.Count <= 0 || plan.Source.Core.EquipmentLockId != 0 ||
            !plan.Definition.Allows(expected.Level, expected.Job) || plan.Source.Core.ItemId != plan.Definition.Item.Id ||
            !ReferenceEquals(plan.Inventory.Before, expected.Inventory) || plan.Inventory.Reason != "use_experience_consumable" ||
            growth.CharacterId != expected.Id || growth.Revision != expected.Revision || growth.Job != expected.Job ||
            growth.GrowType != expected.GrowType || growth.PreviousLevel != expected.Level || growth.TargetLevel < expected.Level ||
            growth.TargetLevel > GameWorld.Characters.CharacterGrowthCatalog.MaximumSupportedLevel ||
            growth.GainedExperience is 0 or > uint.MaxValue || !SameLearning(growth.Before, expected.Learning))
            throw new InvalidDataException("经验物品必须绑定原角色、成长及消费计划");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitExperienceConsumable", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Level != expected.Level || current.Job != expected.Job || current.GrowType != expected.GrowType ||
            current.DungeonState != expected.DungeonState || !SameLearning(current.Learning, expected.Learning) ||
            plan.Inventory.ChangedSlots.Any(slot => plan.Inventory.Before.Find(slot) is { } item && item.IsExpired(now.ToUnixTimeSeconds())))
            throw new InvalidOperationException("经验物品成长资格或材料期限已变化");
        var committed = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE characters SET level=$level,experience=$experience WHERE id=$id AND account=$account AND revision=$revision";
        command.Parameters.AddWithValue("$level", growth.TargetLevel);
        command.Parameters.AddWithValue("$experience", checked((long)growth.Learning.Experience));
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", expected.Revision + 1);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("经验物品角色版本已变化");
        WriteLearning(db, tx, expected.Id, growth.Learning);
        WriteCanonicalSkills(db, tx, expected.Id, growth.Learning);
        if (quests is not null)
            WriteQuestConditions(db, tx, expected.Id, committed.ActiveMainQuests,
                QuestStateProgress.Refresh(committed.ActiveMainQuests, committed with { Level = growth.TargetLevel, Learning = growth.Learning }, quests));
        committed = Require(db, tx, owner.Account, expected.Name);
        tx.Commit();
        return committed;
    });
}
