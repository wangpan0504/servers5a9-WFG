using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static int ResetSingleDungeonBindings(SqliteConnection db, SqliteTransaction tx, string account,
        DungeonIncountTicketPlan plan, DateTimeOffset now, bool apply)
    {
        var group = plan.ResetSingleGroup ?? throw new InvalidDataException("缺少绑定重置组");
        if (plan.SingleWeek != DungeonIncountPolicy.Period(now, group.ResetDay).Week)
            throw new InvalidOperationException("单人绑定重置周期已变化");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$week", plan.SingleWeek);
        string table, predicate;
        if (group.Id == 2)
        {
            table = "account_fiendwar_guides";
            predicate = "account=$account AND week=$week AND entry_epoch=$epoch";
            command.Parameters.AddWithValue("$epoch", ReadRaidQuotaEpochs(db, tx, account, RaidKind.Fiendwar).Entry);
        }
        else if (group.Id == 4)
        {
            table = "account_evildom_war_guides"; predicate = "account=$account AND week=$week";
        }
        else if (group.Id is 1 or 3)
        {
            table = "account_single_dungeon_bindings"; predicate = "account=$account AND group_id=$group AND period=$week";
            command.Parameters.AddWithValue("$group", group.Id);
        }
        else throw new InvalidDataException("未知单人绑定组");
        command.CommandText = (apply ? "DELETE FROM " : "SELECT count(*) FROM ") + table + " WHERE " + predicate;
        return apply ? command.ExecuteNonQuery() : Convert.ToInt32(command.ExecuteScalar());
    }

    internal static void BindSingleDungeonInTransaction(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, Character character, SingleDungeonAdmission admission)
    {
        var group = admission.Group;
        if (group.Id is not (1 or 3) || group.CharacterLimit <= 0 ||
            admission.Week != DungeonIncountPolicy.Period(admission.Clock.GetUtcNow(), group.ResetDay).Week)
            throw new InvalidOperationException("单人绑定的组或周期已变化");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account); command.Parameters.AddWithValue("$group", group.Id);
        command.Parameters.AddWithValue("$period", admission.Week); command.Parameters.AddWithValue("$id", character.Id);
        command.CommandText = """
            SELECT count(*),COALESCE(max(character_id=$id),0) FROM account_single_dungeon_bindings
            WHERE account=$account AND group_id=$group AND period=$period
            """;
        int count; bool bound;
        using (var reader = command.ExecuteReader()) { reader.Read(); count = reader.GetInt32(0); bound = reader.GetInt32(1) != 0; }
        if (bound) return;
        if (count >= group.CharacterLimit) throw new SingleDungeonAdmissionRejectedException();
        command.CommandText = "DELETE FROM account_single_dungeon_bindings WHERE account=$account AND group_id=$group AND period<>$period";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO account_single_dungeon_bindings(account,group_id,period,character_id) VALUES($account,$group,$period,$id)";
        command.ExecuteNonQuery();
    }
}
