using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public FatigueBatteryFillResult FillFatigueBatteries(AccountOwners.Lease owner, Character? expected = null) => owner.Execute(() =>
    {
        var catalog = FatigueBatteryCatalog ?? throw new InvalidOperationException("疲劳蓄电池资源未装配");
        using var db = database.Open();
        var rows = new List<(ushort Id, long Revision, int Level)>();
        using (var query = db.CreateCommand())
        {
            query.CommandText = "SELECT id,revision,level FROM characters WHERE account=$account ORDER BY id";
            query.Parameters.AddWithValue("$account", owner.Account);
            using var reader = query.ExecuteReader();
            while (reader.Read()) rows.Add((checked((ushort)reader.GetInt32(0)), reader.GetInt64(1), reader.GetInt32(2)));
        }
        if (rows.Count == 0) throw new InvalidDataException("账号没有角色");
        var before = rows.Select(row => new FatigueBatteryFillCharacter(row.Id, row.Revision,
            ReadFatigue(db, row.Id, null) ?? throw new InvalidDataException("角色缺少疲劳状态"))).ToArray();
        if (expected is not null && !before.Any(row => row.Id == expected.Id && row.Revision == expected.Revision && row.Fatigue == expected.Fatigue))
            throw new InvalidOperationException("填满蓄电池的在线角色快照已变化");
        var after = before.Select((row, index) =>
        {
            ushort limit = catalog.BuffLimit(rows[index].Level);
            if (limit == 0) throw new InvalidDataException("角色等级缺少蓄电池上限");
            return row.Fatigue.BatteryBuff == limit ? row : row with
            {
                Revision = checked(row.Revision + 1), Fatigue = row.Fatigue with { BatteryBuff = limit }
            };
        }).ToArray();
        int changed = after.Where((row, index) => row != before[index]).Count();
        if (changed == 0) return new FatigueBatteryFillResult(after, 0);
        using var write = db.BeginWrite("CharacterRepository.FillFatigueBatteries");
        using var tx = write.Transaction;
        for (int i = 0; i < after.Length; i++)
        {
            var original = before[i];
            if (ReadFatigue(db, original.Id, tx) != original.Fatigue)
                throw new InvalidOperationException("填满蓄电池的原疲劳状态已变化");
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.Parameters.AddWithValue("$id", original.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$revision", original.Revision);
            command.Parameters.AddWithValue("$level", rows[i].Level);
            command.CommandText = "SELECT count(*) FROM characters WHERE id=$id AND account=$account AND revision=$revision AND level=$level";
            if ((long)command.ExecuteScalar()! != 1) throw new InvalidOperationException("填满蓄电池的角色版本已变化");
            if (after[i] == original) continue;
            command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision AND level=$level";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("填满蓄电池版本冲突");
            WriteFatigueBattery(db, tx, owner.Account, original.Id, after[i].Fatigue);
        }
        tx.Commit();
        return new FatigueBatteryFillResult(after, changed);
    });
}
