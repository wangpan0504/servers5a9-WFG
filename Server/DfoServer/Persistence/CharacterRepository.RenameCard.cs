using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : ICharacterRenameCardStore
{
    public Character? ActivateRenameCard(AccountOwners.Lease owner, Character expected, InventoryPlan inventory) => owner.Execute(() =>
    {
        if (inventory.Reason != "character_rename_card" || !ReferenceEquals(inventory.Before, expected.Inventory) ||
            inventory.ChangedSlots is not [var slot] || slot.List != InventoryListType.Main ||
            expected.Inventory.Find(slot) is not { Uid: > 0, Core: { Kind: ItemKind.Consumable, Count: > 0, EquipmentLockId: 0 } } source ||
            inventory.After.Find(slot) != (source.Core.Count == 1 ? null : source with { Core = source.Core with { Value = source.Core.Value - 1 } }) ||
            inventory.ChangedCurrencies.Length != 0 || !inventory.OverflowItems.IsEmpty)
            throw new InvalidDataException("角色更名卡必须仅消费指定Main源一件");
        var expectedItems = source.Core.Count == 1 ? expected.Inventory.Items.Remove(source) :
            expected.Inventory.Items.Replace(source, source with { Core = source.Core with { Value = source.Core.Value - 1 } });
        // 冷却和日次数由资源消费计划携带，其余资产不得跟随更名资格改变。
        if (!inventory.After.Items.SequenceEqual(expectedItems) || inventory.CharacterPrecondition is null ||
            inventory.After with { Items = expected.Inventory.Items, ConsumableStates = expected.Inventory.ConsumableStates,
                DailyBoosterUses = expected.Inventory.DailyBoosterUses } != expected.Inventory ||
            inventory.ExpBonus is not null || inventory.EquipmentSlotFlagsAfter is not null || inventory.UnlockSkillTree ||
            inventory.GuildHongbaoPurchase is not null || !inventory.AccountRefillClaims.IsEmpty ||
            !inventory.DailyItemMails.IsEmpty || !inventory.ResetItemIds.IsEmpty)
            throw new InvalidDataException("更名资格不能携带额外资产变化");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ActivateRenameCard");
        using var tx = write.Transaction;
        if (!CharacterRenameCard.AllowsMarriage(ReadMarriage(db, tx, expected.Id))) return null;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$uid", source.Uid);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM character_rename_pending WHERE character_id=$id)";
        if (command.ExecuteScalar() is 1L) return null;
        var committed = CommitInventoryInTransaction(db, tx, owner, expected, inventory);
        command.CommandText = "INSERT INTO character_rename_pending(character_id,account,source_uid) VALUES($id,$account,$uid)";
        command.ExecuteNonQuery();
        tx.Commit();
        return committed;
    });

    public CharacterRenameFailure CompleteRenameCard(AccountOwners.Lease owner, Character expected, string newName) => owner.Execute(() =>
    {
        if (!CharacterName.IsValid(newName)) return CharacterRenameFailure.InvalidName;
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CompleteRenameCard");
        using var tx = write.Transaction;
        if (!CharacterRenameCard.AllowsMarriage(ReadMarriage(db, tx, expected.Id))) return CharacterRenameFailure.Unavailable;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$old", expected.Name);
        command.Parameters.AddWithValue("$new", newName);
        command.Parameters.AddWithValue("$revision", expected.Revision);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM characters WHERE name=$new)";
        if (command.ExecuteScalar() is 1L) return CharacterRenameFailure.Duplicate;
        command.CommandText = """
            UPDATE characters SET name=$new,revision=revision+1
            WHERE id=$id AND account=$account AND name=$old AND revision=$revision
              AND EXISTS(SELECT 1 FROM character_rename_pending p WHERE p.character_id=$id AND p.account=$account)
            """;
        try
        {
            if (command.ExecuteNonQuery() != 1) return CharacterRenameFailure.Unavailable;
        }
        catch (SqliteException error) when (error.SqliteExtendedErrorCode == 2067)
        {
            return CharacterRenameFailure.Duplicate;
        }
        command.CommandText = "UPDATE auction_listings SET seller_name=$new WHERE seller_character_id=$id";
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM character_rename_pending WHERE character_id=$id AND account=$account";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("角色更名资格已变化");
        RecordFormerName(db, tx, owner, expected, newName);
        tx.Commit();
        return CharacterRenameFailure.None;
    });
}
