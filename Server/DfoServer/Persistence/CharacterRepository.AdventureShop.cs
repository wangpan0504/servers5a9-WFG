using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public AdventureShopCommit CommitAdventureShop(AccountOwners.Lease owner, AdventureShopPlan plan, AdventureCatalog catalog,
        DateTimeOffset now) => owner.Execute(() =>
    {
        plan.After.Validate(catalog);
        var allowed = plan.Before.State with { Points = plan.After.Points, Purchases = plan.After.Purchases };
        if (!plan.Inventory.Accepted || plan.Inventory.Plan is not { } inventory ||
            JsonSerializer.Serialize(allowed) != JsonSerializer.Serialize(plan.After)) throw new InvalidDataException("冒险团购买计划越界");
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.CommitAdventureShop", deferred: true);
        using var tx = write.Transaction;
        var current = ReadAdventure(db, tx, owner.Account, catalog, now);
        if (!current.Members.Any(member => member.Id == plan.Expected.Id) || !current.Members.SequenceEqual(plan.Before.Members) ||
            JsonSerializer.Serialize(current.State) != JsonSerializer.Serialize(plan.Before.State))
            throw new InvalidOperationException("购买后账号或角色名册已变化");
        var character = CommitInventoryInTransaction(db, tx, owner, plan.Expected, inventory);
        WriteAdventureBalances(db, tx, owner.Account, current.State, plan.After, catalog);
        using var audit = db.CreateCommand(); audit.Transaction = tx;
        audit.CommandText = "INSERT INTO account_adventure_receipts VALUES($account,$event,$character,'shop_purchase',0,$before,$revision)";
        audit.Parameters.AddWithValue("$account", owner.Account); audit.Parameters.AddWithValue("$event", Guid.NewGuid().ToString("N"));
        audit.Parameters.AddWithValue("$character", character.Id); audit.Parameters.AddWithValue("$before", current.State.Revision);
        audit.Parameters.AddWithValue("$revision", current.State.Revision + 1); audit.ExecuteNonQuery();
        var committed = ReadAdventure(db, tx, owner.Account, catalog, now);
        tx.Commit(); return new AdventureShopCommit(committed, character);
    });

    static void WriteAdventureBalances(SqliteConnection db, SqliteTransaction tx, string account, AdventureState before,
        AdventureState after, AdventureCatalog catalog)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.CommandText = "SELECT month FROM account_adventures WHERE account=$account";
        int storedMonth = Convert.ToInt32(command.ExecuteScalar());
        byte[] points = new byte[20];
        for (int index = 0; index < 5; index++) BinaryPrimitives.WriteUInt32LittleEndian(points.AsSpan(index * 4), after.Points[index]);
        command.Parameters.AddWithValue("$points", points); command.Parameters.AddWithValue("$month", after.Month);
        command.Parameters.AddWithValue("$capsule", checked((long)after.CapsuleExperience));
        command.Parameters.AddWithValue("$week", after.CapsuleWeek);
        command.Parameters.AddWithValue("$level", after.Level);
        command.Parameters.AddWithValue("$experience", checked((long)after.Experience));
        command.Parameters.AddWithValue("$remainder", after.Remainder.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$brave", checked((long)after.BraveExperience));
        command.Parameters.AddWithValue("$excluded", after.ExcludedExperience.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$before", before.Revision);
        // 等级、经验、勇者经验与排除额随余额一并写入：冒险团经验来源与购买/胶囊/远征共用一次 revision 递增。
        command.CommandText = """
            UPDATE account_adventures SET revision=revision+1,level=$level,experience=$experience,remainder=$remainder,
              brave_experience=$brave,excluded_experience=$excluded,points=$points,month=$month,
              capsule_experience=$capsule,capsule_week=$week
            WHERE account=$account AND revision=$before
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("冒险团余额或周期版本失配");
        command.Parameters.Clear(); command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$item", 0);
        // ReadAdventure已投影周期；必须比较数据库原月份，不能保存新月份却遗漏清空旧限购。
        if (storedMonth < after.Month)
            foreach (int id in catalog.Products.Values.Where(row => row.ResetType == 3).Select(row => row.ItemId).Distinct())
            {
                command.Parameters["$item"].Value = id;
                command.CommandText = "DELETE FROM account_adventure_purchases WHERE account=$account AND item_id=$item";
                command.ExecuteNonQuery();
            }
        foreach (var pair in after.Purchases.Where(pair => before.Purchases.GetValueOrDefault(pair.Key) != pair.Value))
        {
            command.Parameters["$item"].Value = pair.Key; command.Parameters.AddWithValue("$count", pair.Value);
            command.CommandText = "INSERT INTO account_adventure_purchases VALUES($account,$item,$count) ON CONFLICT(account,item_id) DO UPDATE SET count=$count";
            command.ExecuteNonQuery(); command.Parameters.RemoveAt("$count");
        }
    }
}
