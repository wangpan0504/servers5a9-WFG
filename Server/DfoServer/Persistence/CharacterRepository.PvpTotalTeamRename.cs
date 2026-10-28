using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Items;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public PvpTotalTeamRenameCommit? RenamePvpTotalTeam(AccountOwners.Lease owner, PvpTotalTeamRenamePlan plan) => owner.Execute(() =>
    {
        if (!PvpTotalTeam.ValidName(plan.Name) || plan.Team.Id == Guid.Empty)
            throw new InvalidDataException("总力改名规划无效");
        using var db = database.Open();
        // 零变化只读，不申请writer，不再次消费原槽或重写角色。
        if (plan.Name == plan.Team.Name)
        {
            using var read = db.BeginRead();
            var unchanged = ReadPvpTotalTeam(db, read, owner.Account);
            if (plan.Inventory is not null || plan.Source is not null || unchanged is null || !unchanged.SameLineup(plan.Team))
                throw new InvalidOperationException("总力改名重放的队伍版本已变化");
            ValidateCurrentInventoryPlan(Require(db, read, owner.Account, plan.Expected.Name), plan.Expected,
                new(plan.Expected.Inventory, plan.Expected.Inventory, "pvp_total_team_rename", new(plan.Expected)));
            return new PvpTotalTeamRenameCommit(unchanged, plan.Expected);
        }
        if (plan.Source is not { Uid: > 0, Position.List: InventoryListType.Main,
                Core: { ItemId: PvpTotalTeamRename.CardId, Kind: ItemKind.Material, Count: > 0, EquipmentLockId: 0 } } source ||
            plan.Expected.Inventory.Find(source.Position) != source || plan.Inventory is not { } inventory ||
            !ReferenceEquals(inventory.Before, plan.Expected.Inventory) || inventory.Reason != "pvp_total_team_rename" ||
            inventory.ChangedSlots.Length != 1 || inventory.ChangedSlots[0] != source.Position ||
            inventory.After.Find(source.Position) is { } remaining &&
                (remaining != source with { Core = source.Core with { Value = source.Core.Value - 1 } }) ||
            inventory.After.Find(source.Position) is null && source.Core.Count != 1 || inventory.ChangedCurrencies.Length != 0)
            throw new InvalidDataException("总力改名必须只消费指定槽的一张更名券");
        var expectedItems = source.Core.Count == 1 ? plan.Expected.Inventory.Items.Remove(source) :
            plan.Expected.Inventory.Items.Replace(source, source with { Core = source.Core with { Value = source.Core.Value - 1 } });
        if (inventory.After with { Items = plan.Expected.Inventory.Items } != plan.Expected.Inventory ||
            !inventory.After.Items.SequenceEqual(expectedItems) || inventory.OverflowItems.Length != 0 ||
            inventory.CharacterPrecondition is null)
            throw new InvalidDataException("总力改名不能包含更名券以外的资产变化");
        using var write = db.BeginWrite("CharacterRepository.RenamePvpTotalTeam");
        using var tx = write.Transaction;
        var current = ReadPvpTotalTeam(db, tx, owner.Account);
        if (current is null || !current.SameLineup(plan.Team))
            throw new InvalidOperationException("总力改名的队伍版本已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$name", plan.Name);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM account_pvp_total_teams WHERE name=$name AND account<>$account)";
        if (command.ExecuteScalar() is 1L) return null;
        // 唯一库存提交器参与原事务；队名更新或Commit失败时扣券及审计一起回滚。
        var character = CommitInventoryInTransaction(db, tx, owner, plan.Expected, inventory);
        long revision = checked(current.Revision + 1);
        command.Parameters.AddWithValue("$id", current.Id.ToString("N"));
        command.Parameters.AddWithValue("$before", current.Revision);
        command.Parameters.AddWithValue("$revision", revision);
        command.CommandText = "UPDATE account_pvp_total_teams SET name=$name,revision=$revision WHERE account=$account AND team_id=$id AND revision=$before";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("总力改名提交冲突");
        tx.Commit();
        return new(current with { Name = plan.Name, Revision = revision }, character);
    });
}
