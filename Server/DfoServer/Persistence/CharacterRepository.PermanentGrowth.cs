using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static PermanentStatBonuses ReadPermanentStats(SqliteConnection db, ushort id, SqliteTransaction? tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT max_hp,max_mp,strength,vitality,intelligence,spirit,move_speed,all_element_resistance FROM character_permanent_stats WHERE character_id=$id";
        query.Parameters.AddWithValue("$id", id);
        using var row = query.ExecuteReader();
        if (!row.Read()) return PermanentStatBonuses.Empty;
        var value = new PermanentStatBonuses(checked((uint)row.GetInt64(0)), checked((uint)row.GetInt64(1)),
            row.GetInt32(2), row.GetInt32(3), row.GetInt32(4), row.GetInt32(5), checked((uint)row.GetInt64(6)), row.GetInt32(7));
        value.Validate();
        return value;
    }

    static void WritePermanentStats(SqliteConnection db, SqliteTransaction tx, string account, ushort id, PermanentStatBonuses value)
    {
        value.Validate();
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            INSERT INTO character_permanent_stats(character_id,account,max_hp,max_mp,strength,vitality,intelligence,spirit,move_speed,all_element_resistance)
            VALUES($id,$account,$hp,$mp,$str,$vit,$int,$spi,$move,$res)
            ON CONFLICT(character_id) DO UPDATE SET max_hp=$hp,max_mp=$mp,strength=$str,vitality=$vit,
              intelligence=$int,spirit=$spi,move_speed=$move,all_element_resistance=$res WHERE account=$account
            """;
        query.Parameters.AddWithValue("$id", id);
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$hp", (long)value.MaxHp);
        query.Parameters.AddWithValue("$mp", (long)value.MaxMp);
        query.Parameters.AddWithValue("$str", value.Strength);
        query.Parameters.AddWithValue("$vit", value.Vitality);
        query.Parameters.AddWithValue("$int", value.Intelligence);
        query.Parameters.AddWithValue("$spi", value.Spirit);
        query.Parameters.AddWithValue("$move", (long)value.MoveSpeed);
        query.Parameters.AddWithValue("$res", value.AllElementResistance);
        if (query.ExecuteNonQuery() != 1) throw new InvalidOperationException("永久属性来源不属于当前角色账号");
    }

    public Character CommitPermanentGrowth(AccountOwners.Lease owner, PermanentGrowthPlan plan, DateTimeOffset now) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        var calculated = PermanentGrowthService.Apply(expected, plan.Definition);
        if (expected.Inventory.Find(plan.Source.Position) != plan.Source || plan.Source.Uid <= 0 ||
            plan.Source.Core.EquipmentLockId != 0 || plan.Source.IsExpired(now.ToUnixTimeSeconds()) ||
            plan.Definition.Item.Id != plan.Source.Core.ItemId || !plan.Definition.Item.AllowsUse(expected.Level, expected.Job) ||
            !ReferenceEquals(plan.Inventory.Before, expected.Inventory) || plan.Inventory.Reason != "use_permanent_growth" ||
            calculated.Bonuses != plan.Bonuses || !SameLearning(calculated.Learning, plan.Learning))
            throw new InvalidDataException("永久成长必须绑定原角色、源实例及一次消费计划");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitPermanentGrowth", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Level != expected.Level || current.Job != expected.Job || current.GrowType != expected.GrowType ||
            current.PermanentStats != expected.PermanentStats || !SameLearning(current.Learning, expected.Learning))
            throw new InvalidOperationException("永久成长的角色或来源快照已变化");
        CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        if (plan.Bonuses != expected.PermanentStats)
            WritePermanentStats(db, tx, owner.Account, expected.Id, plan.Bonuses);
        if (!SameLearning(plan.Learning, expected.Learning))
            WriteLearning(db, tx, expected.Id, plan.Learning);
        var committed = Require(db, tx, owner.Account, expected.Name);
        tx.Commit();
        return committed;
    });
}
