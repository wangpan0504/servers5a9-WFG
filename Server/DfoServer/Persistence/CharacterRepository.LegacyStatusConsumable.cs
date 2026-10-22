using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public Character CommitLegacyStatusConsumable(AccountOwners.Lease owner, LegacyStatusConsumablePlan plan, DateTimeOffset now) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        if (expected.Inventory.Find(plan.Source.Position) != plan.Source || plan.Source.Uid <= 0 ||
            plan.Source.Core.EquipmentLockId != 0 || plan.Source.IsExpired(now.ToUnixTimeSeconds()) ||
            plan.Definition.Item.Id != plan.Source.Core.ItemId || !plan.Definition.Item.AllowsUse(expected.Level, expected.Job) ||
            !ReferenceEquals(plan.Inventory.Before, expected.Inventory) || plan.Inventory.Reason != "use_legacy_status" ||
            plan.Definition.Kind != LegacyStatusConsumableKind.ReviveCoins ||
            (long)plan.Inventory.After.ReviveCoins - expected.Inventory.ReviveCoins != plan.Definition.Amount)
            throw new InvalidDataException("旧状态道具必须绑定原角色、源实例及对应的消费结果");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitLegacyStatusConsumable", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Level != expected.Level || current.Job != expected.Job || current.GrowType != expected.GrowType ||
            !SameLearning(current.Learning, expected.Learning))
            throw new InvalidOperationException("旧状态道具的角色或技能快照已变化");
        CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        var committed = Require(db, tx, owner.Account, expected.Name);
        tx.Commit();
        return committed;
    });
}
