using System.Text.Json;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Raids;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IFiendwarRecoveryStore
{
    static void AdmitFiendwarRecovery(SqliteConnection db, SqliteTransaction tx, Guid instance,
        IReadOnlyList<RaidStartReservation> rows, GameWorld.Raids.RaidCost cost)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_fiendwar_recovery(character_id,instance_id,owner_generation,selection_epoch,state,payment_json)
            VALUES($id,$instance,$generation,$epoch,0,$payment)
            """;
        command.Parameters.AddWithValue("$instance", instance.ToString("N"));
        command.Parameters.Add("$id", SqliteType.Integer);
        command.Parameters.Add("$generation", SqliteType.Integer);
        command.Parameters.Add("$epoch", SqliteType.Integer);
        command.Parameters.Add("$payment", SqliteType.Text);
        foreach (var row in rows)
        {
            command.Parameters["$id"].Value = row.Actor.Character;
            command.Parameters["$generation"].Value = row.Actor.Owner.Generation;
            command.Parameters["$epoch"].Value = row.Actor.Epoch;
            command.Parameters["$payment"].Value = JsonSerializer.Serialize(FiendwarPaymentSnapshot.Capture(row, cost));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("漩涡原票持久快照提交失败");
        }
    }

    // 旧owner也会收尾；只能更新原实例、原generation和原选角代次，不能触碰接管后的身份。
    public void RecordFiendwarDeparture(RaidActor actor, Guid instance, DateTimeOffset? rejoinUntil)
    {
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.RecordFiendwarDeparture");
        using var tx = write.Transaction; using var command = db.CreateCommand(); command.Transaction = tx;
        AddFiendwarIdentity(command, actor, instance);
        command.Parameters.AddWithValue("$state", rejoinUntil is null ? 2 : 1);
        command.Parameters.AddWithValue("$until", rejoinUntil?.ToUnixTimeSeconds() ?? 0);
        command.CommandText = """
            UPDATE character_fiendwar_recovery SET state=$state,rejoin_until=$until
            WHERE character_id=$id AND instance_id=$instance AND owner_generation=$generation AND selection_epoch=$epoch AND state=0
              AND EXISTS(SELECT 1 FROM character_raid_runs r WHERE r.character_id=$id AND r.instance_id=$instance
                AND r.account=$account AND r.raid_type=2 AND r.state=0 AND r.phase0_event IS NULL AND r.clear_week IS NULL)
            """;
        command.ExecuteNonQuery(); tx.Commit();
    }

    public bool ChangeFiendwarRejoin(RaidActor original, RaidActor replacement, Guid instance, DateTimeOffset now, bool undo = false)
    {
        if (original.Character != replacement.Character || original.Owner.Account != replacement.Owner.Account ||
            original.Owner.IsCurrent || !replacement.IsCurrent) return false;
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.ChangeFiendwarRejoin");
        using var tx = write.Transaction; using var command = db.CreateCommand(); command.Transaction = tx;
        AddFiendwarIdentity(command, undo ? replacement : original, instance);
        command.Parameters.AddWithValue("$nextGeneration", (undo ? original : replacement).Owner.Generation);
        command.Parameters.AddWithValue("$nextEpoch", (undo ? original : replacement).Epoch);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        // 重返安装失败时只归还本次CAS取得的原断线期限，不另延长五分钟。
        command.CommandText = undo ? """
            UPDATE character_fiendwar_recovery SET state=1,owner_generation=$nextGeneration,selection_epoch=$nextEpoch
            WHERE character_id=$id AND instance_id=$instance AND owner_generation=$generation AND selection_epoch=$epoch AND state=0
            """ : """
            UPDATE character_fiendwar_recovery SET state=0,owner_generation=$nextGeneration,selection_epoch=$nextEpoch
            WHERE character_id=$id AND instance_id=$instance AND owner_generation=$generation AND selection_epoch=$epoch
              AND state=1 AND rejoin_until>$now
              AND EXISTS(SELECT 1 FROM character_raid_runs r WHERE r.character_id=$id AND r.instance_id=$instance
                AND r.account=$account AND r.raid_type=2 AND r.state=0 AND r.phase0_event IS NULL AND r.clear_week IS NULL)
            """;
        bool changed = command.ExecuteNonQuery() == 1; tx.Commit(); return changed;
    }

    static void AddFiendwarIdentity(SqliteCommand command, RaidActor actor, Guid instance)
    {
        command.Parameters.AddWithValue("$id", actor.Character);
        command.Parameters.AddWithValue("$account", actor.Owner.Account);
        command.Parameters.AddWithValue("$instance", instance.ToString("N"));
        command.Parameters.AddWithValue("$generation", actor.Owner.Generation);
        command.Parameters.AddWithValue("$epoch", actor.Epoch);
    }

    // 只由持server.lock、尚未启动监听的宿主调用；普通仓储构造和离线GM不能终结活跃攻坚。
    internal FiendwarInterruptionResult RecoverInterruptedFiendwar(DateTimeOffset now)
    {
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.RecoverInterruptedFiendwar");
        using var tx = write.Transaction; using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            SELECT r.character_id,r.account,r.instance_id,p.state,p.rejoin_until,p.payment_json
            FROM character_raid_runs r LEFT JOIN character_fiendwar_recovery p USING(character_id,instance_id)
            WHERE r.raid_type=2 AND r.state=0 AND r.phase0_event IS NULL AND r.clear_week IS NULL
              AND NOT EXISTS(SELECT 1 FROM character_raid_reward_receipts claimed
                WHERE claimed.character_id=r.character_id AND claimed.instance_id=r.instance_id)
            ORDER BY r.instance_id,r.character_id
            """;
        var rows = new List<(ushort Id, string Account, string Instance, int? State, long? Until, string? Payment)>();
        using (var reader = command.ExecuteReader()) while (reader.Read())
            rows.Add((checked((ushort)reader.GetInt32(0)), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        int refunded = 0, abandoned = 0, missing = 0;
        command.Parameters.Add("$id", SqliteType.Integer); command.Parameters.Add("$instance", SqliteType.Text);
        foreach (var row in rows)
        {
            if (row.Payment is null) missing++;
            else if (row.State == 0 || row.State == 1 && row.Until > now.ToUnixTimeSeconds())
            {
                var payment = JsonSerializer.Deserialize<FiendwarPaymentSnapshot>(row.Payment)
                    ?? throw new InvalidDataException("漩涡原票持久快照为空");
                payment.Validate();
                var recipient = FindMailRecipientById(db, tx, row.Id);
                if (recipient is null || recipient.Account != row.Account) throw new InvalidDataException("漩涡中断原收件人不一致");
                var delivery = new MailboxDelivery("fiendwar-refund:" + row.Instance + ":" + row.Id, recipient,
                    "攻坚战指挥部", "超时空漩涡攻坚失败，返还本次入场门票。", 0, payment.Items, now.ToUnixTimeSeconds());
                var result = DeliverMailInTransaction(db, tx, delivery);
                if (result.Error != MailboxError.None) throw new InvalidOperationException("漩涡中断退票提交失败：" + result.Error);
                refunded++;
            }
            else abandoned++;
            command.Parameters["$id"].Value = row.Id; command.Parameters["$instance"].Value = row.Instance;
            command.CommandText = """
                UPDATE character_raid_runs SET state=1 WHERE character_id=$id AND instance_id=$instance
                  AND raid_type=2 AND state=0 AND phase0_event IS NULL AND clear_week IS NULL
                """;
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("漩涡中断生命周期已变化");
        }
        tx.Commit(); return new(refunded, abandoned, missing);
    }
}
