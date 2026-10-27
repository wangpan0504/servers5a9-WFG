using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public ServerItemCycleCommit CommitServerItemCycle(AccountOwners.Lease owner, Character expected, InventoryPlan plan) => owner.Execute(() =>
    {
        using var db = database.Open();
        var reset = plan.ResetItemIds.ToHashSet();
        List<(long Attachment, long Uid, uint Mail, string Core)> ResetMail(SqliteTransaction? transaction = null, bool firstOnly = false)
        {
            var rows = new List<(long Attachment, long Uid, uint Mail, string Core)>();
            if (reset.Count == 0) return rows;
            using var query = db.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = """
                SELECT a.attachment_id,a.item_uid,a.mail_id,i.item_core,CAST(json_extract(i.item_core,'$.ItemId') AS INTEGER)
                FROM mailbox_attachments a JOIN mailbox_messages m USING(mail_id)
                JOIN character_inventory_items i USING(item_uid)
                WHERE m.recipient_id=$id AND m.recipient_account=$account AND a.claimed=0
                """;
            query.Parameters.AddWithValue("$id", expected.Id); query.Parameters.AddWithValue("$account", owner.Account);
            using var reader = query.ExecuteReader();
            while (reader.Read())
                if (reset.Contains(reader.GetInt32(4)))
                {
                    rows.Add((reader.GetInt64(0),reader.GetInt64(1),checked((uint)reader.GetInt64(2)),reader.GetString(3)));
                    if (firstOnly) break;
                }
            return rows;
        }
        if (!plan.Changed && ResetMail(firstOnly:true).Count == 0)
        {
            ValidateInventoryPlan(expected,plan);
            using var read=db.BeginRead();
            ValidateCurrentInventoryPlan(Require(db,read,owner.Account,expected.Name),expected,plan);
            if (plan.CharacterPrecondition?.IsExpired()==true) throw new InventoryQualificationExpiredException();
            return new ServerItemCycleCommit(expected, false);
        }
        using var write = db.BeginWrite("CharacterRepository.CommitServerItemCycle"); using var tx = write.Transaction;
        var current = CommitInventoryInTransaction(db, tx, owner, expected, plan);
        bool mailChanged = !plan.DailyItemMails.IsEmpty;
        // 一次读取当前收件人的候选，避免为每个历史模板重复扫描全部邮件。
        foreach (var row in ResetMail(tx))
        {
            using var query = db.CreateCommand(); query.Transaction = tx;
            // 保留邮件文字、金币及不相关附件；仅回收本收件人的未领取目标核心。
            query.Parameters.AddWithValue("$attachment", row.Attachment);
            query.Parameters.AddWithValue("$mail", row.Mail);
            query.CommandText = "DELETE FROM mailbox_attachments WHERE attachment_id=$attachment; UPDATE mailbox_messages SET revision=revision+1 WHERE mail_id=$mail";
            query.ExecuteNonQuery();
            DeleteHeldItem(db,tx,owner.Account,expected.Id,row.Uid);
            AuditMail(db,tx,row.Mail,owner.Account,expected.Id,owner.Generation,"server_item_reset",DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                new { row.Uid, Core = JsonSerializer.Deserialize<ItemCore>(row.Core) });
            mailChanged = true;
        }
        foreach (var candidate in plan.DailyItemMails)
        {
            var delivery = candidate with { Recipient = candidate.Recipient with { Account = owner.Account } };
            if (DeliverMailInTransaction(db, tx, delivery).Error != MailboxError.None)
                throw new InvalidOperationException("每日物品邮件投递失败，周期状态已回滚");
        }
        tx.Commit(); return new ServerItemCycleCommit(current, mailChanged);
    });
    static ServerItemUsageState ReadServerItems(SqliteConnection db, SqliteTransaction? tx, ushort id)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT state_json FROM character_server_item_usage WHERE character_id=$id";
        query.Parameters.AddWithValue("$id", id);
        var state = query.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ServerItemUsageState>(json) ??
            throw new InvalidDataException("全局物品周期状态缺失") : ServerItemUsageState.Empty;
        state.Validate(); return state;
    }
    static void WriteServerItems(SqliteConnection db, SqliteTransaction tx, ushort id, ServerItemUsageState state)
    {
        state.Validate();
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "INSERT INTO character_server_item_usage(character_id,state_json) VALUES($id,$state) ON CONFLICT(character_id) DO UPDATE SET state_json=excluded.state_json";
        query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state)); query.ExecuteNonQuery();
    }
    public ImmutableDictionary<int, AccountItemRefillState> ReadAccountItemRefills(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open(); using var query = db.CreateCommand();
        query.CommandText = "SELECT item_id,service_day,character_id,completed FROM account_server_item_refills WHERE account=$account";
        query.Parameters.AddWithValue("$account", owner.Account);
        using var reader = query.ExecuteReader(); var rows = ImmutableDictionary.CreateBuilder<int, AccountItemRefillState>();
        while (reader.Read()) rows.Add(reader.GetInt32(0), new(reader.GetInt32(1), checked((ushort)reader.GetInt32(2)), reader.GetBoolean(3)));
        return rows.ToImmutable();
    });
    static void WriteAccountItemRefills(SqliteConnection db, SqliteTransaction tx, string account, ushort characterId, ImmutableArray<AccountItemRefillClaim> claims)
    {
        foreach (var claim in claims)
        {
            var after = claim.After;
            if (claim.ItemId <= 0 || after.Day < 1 || after.Day > DateOnly.MaxValue.DayNumber || after.CharacterId != characterId ||
                claim.Before is { } before && (after.Day < before.Day || after.Day == before.Day &&
                    (after.CharacterId != before.CharacterId || before.Completed || !after.Completed)))
                throw new InvalidDataException("账号补给日戳或首个领取角色无效");
            using var query = db.CreateCommand(); query.Transaction = tx;
            query.CommandText = "SELECT service_day,character_id,completed FROM account_server_item_refills WHERE account=$account AND item_id=$item";
            query.Parameters.AddWithValue("$account", account); query.Parameters.AddWithValue("$item", claim.ItemId);
            using (var reader = query.ExecuteReader())
            {
                AccountItemRefillState? actual = reader.Read() ? new(reader.GetInt32(0), checked((ushort)reader.GetInt32(1)), reader.GetBoolean(2)) : null;
                if (actual != claim.Before) throw new InvalidOperationException("账号补给已被其它角色领取");
            }
            query.Parameters.AddWithValue("$day", after.Day);
            query.Parameters.AddWithValue("$character", after.CharacterId);
            query.Parameters.AddWithValue("$completed", after.Completed ? 1 : 0);
            query.CommandText = """
                INSERT INTO account_server_item_refills(account,item_id,service_day,character_id,completed)
                VALUES($account,$item,$day,$character,$completed)
                ON CONFLICT(account,item_id) DO UPDATE SET service_day=excluded.service_day,
                  character_id=excluded.character_id,completed=excluded.completed
                """;
            query.ExecuteNonQuery();
        }
    }
}
