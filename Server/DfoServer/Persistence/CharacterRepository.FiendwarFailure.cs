using DfoServer.Game.Mailbox;
using DfoServer.Game.Raids;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IFiendwarFailureStore
{
    // 已有入场行证明真实扣费；既有系统邮件的稳定key和完整内容即为退款收据，不另造资产账本。
    public void CommitFiendwarFailure(AntonRaidBattle.FiendwarFailure candidate)
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitFiendwarFailure");
        using var tx = write.Transaction;
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$instance", candidate.Instance.ToString("N"));
        command.Parameters.AddWithValue("$source", candidate.Source);
        command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Parameters.Add("$account", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$admitted", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Parameters.Add("$key", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var member in candidate.Members)
        {
            command.Parameters["$id"].Value = member.Character;
            command.Parameters["$account"].Value = member.Account;
            command.Parameters["$admitted"].Value = member.Admitted;
            string key = "fiendwar-refund:" + candidate.Instance.ToString("N") + ":" + member.Character;
            command.Parameters["$key"].Value = key;
            command.CommandText = """
                SELECT state FROM character_raid_runs WHERE character_id=$id AND account=$account
                  AND instance_id=$instance AND raid_type=2 AND source=$source AND admitted_at=$admitted
                  AND phase0_event IS NULL AND clear_week IS NULL
                """;
            object? value = command.ExecuteScalar();
            if (value is null) continue;
            int state = Convert.ToInt32(value);
            if (state == 1)
            {
                // 新开战放弃的旧行不能补退；只有原事务已落盘的邮件才允许回放。
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM mailbox_messages WHERE delivery_key=$key AND recipient_id=$id AND recipient_account=$account)";
                if (Convert.ToInt32(command.ExecuteScalar()) == 0) continue;
            }
            else if (state != 0) continue;
            if (member.Refund && FindMailRecipientById(db, tx, member.Character) is { } recipient && recipient.Account == member.Account)
            {
                var delivery = new MailboxDelivery(key, recipient,
                    "攻坚战指挥部", "超时空漩涡攻坚失败，返还本次入场门票。", 0, member.Items, candidate.At.ToUnixTimeSeconds());
                var result = DeliverMailInTransaction(db, tx, delivery);
                if (result.Error != MailboxError.None) throw new InvalidOperationException("漩涡退票邮件提交失败：" + result.Error);
            }
            command.CommandText = """
                UPDATE character_raid_runs SET state=1 WHERE character_id=$id AND account=$account
                  AND instance_id=$instance AND raid_type=2 AND source=$source AND admitted_at=$admitted
                  AND state=0 AND phase0_event IS NULL AND clear_week IS NULL
                """;
            command.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
