using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public Character CommitFatigueConsumable(AccountOwners.Lease owner, FatigueConsumablePlan plan, DateTimeOffset now) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        if (expected.Inventory.Find(plan.Source.Position) != plan.Source || plan.Source.Uid <= 0 ||
            plan.Source.Core.EquipmentLockId != 0 || plan.Source.IsExpired(now.ToUnixTimeSeconds()) ||
            plan.Definition.Item.Id != plan.Source.Core.ItemId || !plan.Definition.Item.AllowsUse(expected.Level, expected.Job) ||
            !ReferenceEquals(plan.Inventory.Before, expected.Inventory) || plan.Inventory.Reason != "use_fatigue_consumable" ||
            plan.Fatigue != FatigueConsumableService.Recover(expected.Fatigue, plan.Definition.Amount))
            throw new InvalidDataException("疲劳恢复必须绑定原角色、源实例及完整增量");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitFatigueConsumable", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Fatigue != expected.Fatigue || current.Level != expected.Level || current.Job != expected.Job)
            throw new InvalidOperationException("疲劳药剂的角色或余额快照已变化");
        CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE character_fatigue SET used=$used,remaining=$remaining WHERE character_id=$id AND account=$account";
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$used", plan.Fatigue.Used);
        command.Parameters.AddWithValue("$remaining", plan.Fatigue.Remaining);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("疲劳恢复owner已变化");
        var committed = Require(db, tx, owner.Account, expected.Name);
        tx.Commit();
        return committed;
    });
}
