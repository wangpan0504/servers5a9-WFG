using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IQuestHiddenHistoryStore
{
    public bool HasQuestDungeonClear(AccountOwners.Lease owner, Character expected, int dungeonId, int count) => owner.Execute(() =>
    {
        if (dungeonId <= 0 || count <= 0) return false;
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", expected.Revision);
        command.Parameters.AddWithValue("$dungeon", dungeonId);
        command.Parameters.AddWithValue("$count", count);
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM characters WHERE id=$id AND account=$account AND revision=$revision)
              AND (SELECT count(*) FROM
                (SELECT 1 FROM character_dungeon_settlement_receipts
                 WHERE character_id=$id AND account=$account AND dungeon_id=$dungeon LIMIT $count))=$count
            """;
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    });
}
