using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public int? ReadTagEvildomRoute(AccountOwners.Lease owner, int day, int teamIndex) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadTagEvildomRoute(db, null, owner.Account, day, teamIndex);
    });

    static int? ReadTagEvildomRoute(SqliteConnection db, SqliteTransaction? tx, string account, int day, int teamIndex)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT maze_ordinal FROM account_tag_evildom_challenges WHERE account=$account AND day=$day AND team_index=$team";
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$team", teamIndex);
        return command.ExecuteScalar() is long ordinal ? checked((int)ordinal) : null;
    }

    static void CommitTagEvildomChallenge(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        TagEvildomAdmission admission, Guid instance)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(admission.AdmittedAt);
        if (!TagEvildomSchedule.CanEnter(at)) throw new InvalidOperationException("再战魔界05:30至06:00关闭入场");
        var period = TagEvildomPeriod.At(at);
        if (ReadTagEvildomRoute(db, tx, owner.Account, period.Day, admission.TeamIndex) is int previous && previous != admission.MazeOrdinal)
            throw new InvalidOperationException("再战魔界失败重进不能更换当天本队路线");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO account_tag_evildom_challenges(account,day,week,team_index,maze_ordinal,dungeon_id,instance_id,first,second,admitted_at,sequence,state)
            VALUES($account,$day,$week,$team,$maze,$dungeon,$instance,$first,$second,$at,
              COALESCE((SELECT max(sequence) FROM account_tag_evildom_challenges WHERE account=$account),0)+1,0)
            ON CONFLICT(account,day,team_index) DO UPDATE SET dungeon_id=excluded.dungeon_id,
              instance_id=excluded.instance_id,first=excluded.first,second=excluded.second,
              admitted_at=excluded.admitted_at,sequence=excluded.sequence,state=0;
            UPDATE account_tag_evildom_challenges SET state=1
              WHERE account=$account AND day=$day AND state=0 AND instance_id<>$instance;
            """;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$day", period.Day);
        command.Parameters.AddWithValue("$week", period.Week);
        command.Parameters.AddWithValue("$team", admission.TeamIndex);
        command.Parameters.AddWithValue("$maze", admission.MazeOrdinal);
        command.Parameters.AddWithValue("$dungeon", admission.DungeonId);
        command.Parameters.AddWithValue("$instance", instance.ToString("N"));
        command.Parameters.AddWithValue("$first", admission.Primary.Id);
        command.Parameters.AddWithValue("$second", admission.Standby.Id);
        command.Parameters.AddWithValue("$at", admission.AdmittedAt);
        command.ExecuteNonQuery();
    }

    public Character CommitTagEvildomReturn(AccountOwners.Lease owner, Character expected, Guid instance,
        Location position, Location? returnPoint, InventoryPlan? inventory, int day) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitTagEvildomReturn");
        using var tx = write.Transaction;
        var saved = expected.Position == position && inventory is null ? Require(db, tx, owner.Account, expected.Name)
            : SaveInTransaction(db, tx, owner, expected, position, returnPoint, inventory);
        // 角色资产版本仍由原保存事务检查；无位置/库存变化时也拒绝旧快照。
        if (saved.Id != expected.Id || expected.Position == position && inventory is null && saved.Revision != expected.Revision)
            throw new InvalidOperationException("再战退场角色快照已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE account_tag_evildom_challenges SET state=1
            WHERE account=$account AND instance_id=$instance AND first=$first AND state=0 AND day=$day;
            """;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$instance", instance.ToString("N"));
        command.Parameters.AddWithValue("$first", expected.Id);
        command.Parameters.AddWithValue("$day", day);
        command.ExecuteNonQuery();
        tx.Commit();
        return saved;
    });
}
