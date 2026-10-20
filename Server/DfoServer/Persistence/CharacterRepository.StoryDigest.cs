using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public Character AdvanceAcceptedStoryLevel(AccountOwners.Lease owner, Character expected) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT revision,level,accepted_story_level FROM characters WHERE id=$id AND account=$account";
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidDataException("角色与所有者不一致");
            if (reader.GetInt64(0) != expected.Revision || reader.GetInt64(1) != expected.Level ||
                reader.GetInt64(2) != expected.AcceptedStoryLevel)
                throw new InvalidOperationException("剧情接受的角色快照已变化");
        }
        // 重复接受或GM降低角色等级不回退剧情，也不申请写事务。
        if (expected.AcceptedStoryLevel >= expected.Level) return expected;
        var updated = expected with { AcceptedStoryLevel = expected.Level, Revision = checked(expected.Revision + 1) };
        using var write = db.BeginWrite("CharacterRepository.AdvanceAcceptedStoryLevel");
        using var transaction = write.Transaction;
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE characters SET accepted_story_level=$level,revision=revision+1
            WHERE id=$id AND account=$account AND revision=$revision AND level=$level AND accepted_story_level=$story
            """;
        command.Parameters.AddWithValue("$revision", expected.Revision);
        command.Parameters.AddWithValue("$level", expected.Level);
        command.Parameters.AddWithValue("$story", expected.AcceptedStoryLevel);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("剧情接受的角色快照已变化");
        transaction.Commit();
        return updated;
    });
}
