using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static void ReserveBeastAdmission(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        Character expected, DungeonEntryCharge charge, BeastDungeonAdmission admission)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_beast_dungeon_reservations
            (character_id,account,instance_id,attempt,entry_epoch,day,dungeon_id,expected_used,state)
            VALUES($id,$account,$run,$attempt,$epoch,$day,$dungeon,$used,0)
            """;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$run", charge.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", charge.Identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", charge.Identity.Epoch);
        command.Parameters.AddWithValue("$day", admission.Day);
        command.Parameters.AddWithValue("$dungeon", charge.DungeonId);
        command.Parameters.AddWithValue("$used", admission.ExpectedUsed);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("魔兽入场预约未提交");
    }

    public Character[] CommitBeastDungeonLoading(IReadOnlyList<BeastDungeonLoadingReservation> reservations)
    {
        if (reservations.Count is < 1 or > 4 ||
            reservations.Select(row => row.Identity.InstanceId).Distinct().Count() != 1 ||
            reservations.Select(row => (row.DungeonId, row.Admission.Day)).Distinct().Count() != 1)
            throw new InvalidDataException("魔兽加载需要同一实例及业务日的全员预约");
        return reservations.Count == 1 ? reservations[0].Owner.Execute(Commit) :
            AccountOwners.Lease.ExecuteGroup(reservations.Select(row => (row.Owner, row.Expected.Id)).ToArray(), Commit);

        Character[] Commit()
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitBeastDungeonLoading");
            using var tx = write.Transaction;
            var result = new Character[reservations.Count];
            for (int i = 0; i < reservations.Count; i++)
            {
                var row = reservations[i];
                var current = Require(db, tx, row.Owner.Account, row.Expected.Name);
                if (current.Id != row.Expected.Id || current.Revision != row.Expected.Revision ||
                    current.DungeonState?.LastAttempt != row.Identity.TaskAttempt ||
                    row.Identity.CharacterId != current.Id || !current.CompletedQuests.Contains(5927))
                    throw new BeastDungeonAdmissionRejectedException("魔兽加载角色或原实例身份已变化");
                using var command = db.CreateCommand();
                command.Transaction = tx;
                command.Parameters.AddWithValue("$id", current.Id);
                command.Parameters.AddWithValue("$account", row.Owner.Account);
                command.Parameters.AddWithValue("$run", row.Identity.InstanceId.ToString("N"));
                command.Parameters.AddWithValue("$attempt", row.Identity.TaskAttempt);
                command.Parameters.AddWithValue("$epoch", row.Identity.Epoch);
                command.Parameters.AddWithValue("$day", row.Admission.Day);
                command.Parameters.AddWithValue("$dungeon", row.DungeonId);
                command.Parameters.AddWithValue("$used", row.Admission.ExpectedUsed);
                command.CommandText = """
                    SELECT state FROM character_beast_dungeon_reservations
                    WHERE character_id=$id AND account=$account AND instance_id=$run AND attempt=$attempt AND entry_epoch=$epoch
                      AND day=$day AND dungeon_id=$dungeon AND expected_used=$used
                    """;
                if (command.ExecuteScalar() is not long state || state == 2)
                    throw new BeastDungeonAdmissionRejectedException("魔兽加载没有有效预约");
                if (state == 1) { result[i] = current; continue; }
                int used = ReadBeastDungeonUsed(db, tx, current.Id, row.Owner.Account, row.Admission.Day, row.DungeonId);
                if (used != row.Admission.ExpectedUsed || !BeastDungeonRotation.CanEnter(row.Admission.Day, row.DungeonId, used))
                    throw new BeastDungeonAdmissionRejectedException("魔兽加载次数已变化");
                var ticket = row.Admission.Ticket ?? throw new InvalidDataException("原图魔兽缺少门票计划");
                if (row.Admission.Day != BeastDungeonRotation.ServiceDay(row.Clock.GetUtcNow()) ||
                    ticket.Before.Items.Any(item => ticket.ChangedSlots.Contains(item.Position) && item.Core.IsExpired(row.Clock.GetUtcNow().ToUnixTimeSeconds())))
                    throw new BeastDungeonAdmissionRejectedException("魔兽加载已跨日或原门票过期");
                result[i] = CommitInventoryInTransaction(db, tx, row.Owner, row.Expected, ticket);
                command.CommandText = """
                    INSERT INTO character_beast_dungeon_entries(character_id,account,day,dungeon_id,used)
                    VALUES($id,$account,$day,$dungeon,$used+1)
                    ON CONFLICT(character_id,day,dungeon_id) DO UPDATE SET used=excluded.used;
                    """;
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("魔兽首房次数未提交");
                command.CommandText = """
                    UPDATE character_beast_dungeon_reservations SET state=1
                    WHERE character_id=$id AND account=$account AND instance_id=$run AND state=0
                    """;
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("魔兽预约状态已变化");
            }
            tx.Commit();
            return result;
        }
    }

    // 旧连接只取消自己的未提交run；新选角在当前租约下回收崩溃遗留的预约。
    public void CancelBeastDungeonReservation(string account, ushort characterId, Guid? instance)
    {
        using var db = database.Open();
        using (var probe = db.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM character_beast_dungeon_reservations WHERE character_id=$id AND account=$account AND state=0" +
                (instance is null ? "" : " AND instance_id=$run") + " LIMIT 1";
            probe.Parameters.AddWithValue("$id", characterId);
            probe.Parameters.AddWithValue("$account", account);
            if (instance is { } expectedRun) probe.Parameters.AddWithValue("$run", expectedRun.ToString("N"));
            if (probe.ExecuteScalar() is null) return;
        }
        using var write = db.BeginWrite("CharacterRepository.CancelBeastDungeonReservation");
        using var command = db.CreateCommand();
        command.Transaction = write.Transaction;
        command.CommandText = """
            UPDATE character_beast_dungeon_reservations SET state=2
            WHERE character_id=$id AND account=$account AND state=0
            """ + (instance is null ? "" : " AND instance_id=$run");
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", account);
        if (instance is { } run) command.Parameters.AddWithValue("$run", run.ToString("N"));
        command.ExecuteNonQuery();
        write.Transaction.Commit();
    }
}
