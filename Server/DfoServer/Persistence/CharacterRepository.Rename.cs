using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : ICharacterRenamer
{
    // GM 改名：只改 characters.name（NOCASE 唯一）与拍卖行里冗余的卖家名；邮件历史发件人名等快照保持原样。
    // 同一事务内校验 revision，被占用的名字以 InvalidDataException 返回，不改任何数据。
    public Character Rename(AccountOwners.Lease owner, Character expected, string newName) => owner.Execute(() =>
    {
        if (!CharacterName.IsValid(newName)) throw new InvalidDataException("新角色名无效");
        using (var db = database.Open())
        using (var write = db.BeginWrite("CharacterRepository.Rename"))
        using (var tx = write.Transaction)
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$old", expected.Name);
            command.Parameters.AddWithValue("$new", newName);
            command.Parameters.AddWithValue("$revision", expected.Revision);
            command.CommandText = "UPDATE characters SET name=$new,revision=revision+1 WHERE id=$id AND account=$account AND name=$old AND revision=$revision";
            int changed;
            try { changed = command.ExecuteNonQuery(); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidDataException("角色名已被占用"); }
            if (changed != 1) throw new InvalidOperationException("角色 revision 或名称已变化，请重试");
            command.CommandText = "UPDATE auction_listings SET seller_name=$new WHERE seller_character_id=$id";
            command.ExecuteNonQuery();
            tx.Commit();
        }
        return LoadCharacter(owner, expected.Slot);
    });
}