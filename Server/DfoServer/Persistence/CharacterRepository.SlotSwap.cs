using DfoServer.Game.Accounts;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 选角页拖动重排只互换两个持久槽位；两个角色各自的任务、资产和revision以外身份不变。
    // UNIQUE(account,slot)按语句立即检查，直接对调会撞唯一索引，必须先经临时槽。
    public void SwapSlots(AccountOwners.Lease owner, ushort slotA, ushort slotB) => owner.Execute(() =>
    {
        if (slotA == slotB) return true;
        using var db = database.Open();
        using var transaction = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT slot FROM characters WHERE account=$account ORDER BY slot";
        command.Parameters.AddWithValue("$account", owner.Account);
        // schema上限固定为0..61；用全部62格判断空槽，避免容量被GM调小后误判占用。
        var occupied = new bool[62];
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                int slot = reader.GetInt32(0);
                if (slot is < 0 or >= 62) throw new InvalidDataException("Character slot out of range");
                occupied[slot] = true;
            }
        if (slotA >= 62 || slotB >= 62 || !occupied[slotA] || !occupied[slotB])
            throw new InvalidDataException("Character slot absent");
        int temp = Array.FindIndex(occupied, value => !value);
        // 62格全占时范围内没有空槽；临时放行CHECK后仍由UNIQUE索引保证不重号。
        bool widened = temp < 0;
        if (widened)
        {
            temp = -1;
            Execute("PRAGMA ignore_check_constraints=1");
        }
        try
        {
            Move(slotA, temp);
            Move(slotB, slotA);
            Move(temp, slotB);
        }
        finally
        {
            if (widened) Execute("PRAGMA ignore_check_constraints=0");
        }
        transaction.Commit();
        return true;

        void Execute(string sql)
        {
            command.CommandText = sql;
            command.Parameters.Clear();
            command.ExecuteNonQuery();
        }

        void Move(int from, int to)
        {
            command.CommandText = "UPDATE characters SET slot=$to,revision=revision+1 WHERE account=$account AND slot=$from";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$to", to);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$from", from);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Character slot swap changed");
        }
    });
}
