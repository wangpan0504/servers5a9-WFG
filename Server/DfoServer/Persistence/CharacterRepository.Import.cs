using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 文件格式由导入工具解析；这里仅负责空账号约束和整批原子写入。
    public int Import(AccountOwners.Lease owner, IReadOnlyList<Character> rows) => owner.Execute(() =>
    {
        if (List(owner).Length != 0)
            throw new InvalidOperationException("Import requires an empty account");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.Import");
        using var transaction = write.Transaction;
        foreach (var row in rows)
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO characters(id,account,slot,name,town,area,x,y,direction,
                  return_town,return_area,return_x,return_y,return_direction)
                VALUES($id,$account,$slot,$name,$town,$area,$x,$y,$direction,$rtown,$rarea,$rx,$ry,$rdirection);
                """;
            command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$slot", row.Slot);
            command.Parameters.AddWithValue("$name", row.Name);
            Bind(command, "", row.Position);
            Bind(command, "r", row.Return);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return rows.Count;
    });
}
