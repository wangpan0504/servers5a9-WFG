using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public MailboxRecipient? FindMailRecipient(string name)
    {
        using var db = database.Open();
        return FindMailRecipient(db, null, name);
    }

    static MailboxRecipient? FindMailRecipient(SqliteConnection db, SqliteTransaction? tx, string name)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT id,account,name,job,grow_type,level FROM characters WHERE name=$name";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(checked((ushort)reader.GetInt32(0)), reader.GetString(1), reader.GetString(2),
            checked((byte)reader.GetInt32(3)), checked((byte)reader.GetInt32(4)), checked((byte)reader.GetInt32(5))) : null;
    }

    static void RequireMailOwner(SqliteConnection db, SqliteTransaction? tx, AccountOwners.Lease owner, ushort characterId)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT 1 FROM characters WHERE id=$id AND account=$account";
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", owner.Account);
        if (command.ExecuteScalar() is null)
            throw new InvalidOperationException("邮箱角色不属于当前账号");
    }

    public MailboxPage LoadMailbox(AccountOwners.Lease owner, ushort characterId, long now) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        RequireMailOwner(db, tx, owner, characterId);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$now", now);
        command.CommandText = """
            SELECT mail_id FROM mailbox_messages WHERE recipient_id=$id AND state>0
              AND (state=3 OR expires_at IS NULL OR expires_at>$now)
              AND (state=3 OR mail_id IN (
                SELECT mail_id FROM mailbox_messages WHERE recipient_id=$id AND state IN (1,2)
                  AND (expires_at IS NULL OR expires_at>$now) ORDER BY created_at,mail_id LIMIT 20))
            ORDER BY CASE WHEN state=3 THEN 1 ELSE 0 END,created_at DESC,mail_id DESC;
            """;
        var ids = new List<uint>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                ids.Add(checked((uint)reader.GetInt64(0)));
        var entries = ids.Select(id => ReadMail(db, tx, characterId, id)!).ToImmutableArray();
        command.CommandText = "SELECT count(*) FROM mailbox_messages WHERE recipient_id=$id AND state IN (1,2) AND (expires_at IS NULL OR expires_at>$now)";
        int total = checked((int)(long)command.ExecuteScalar()!);
        tx.Commit();
        return new MailboxPage(entries, total - entries.Count(entry => entry.State != 3));
    });

    static MailboxEntry? ReadMail(SqliteConnection db, SqliteTransaction tx, ushort characterId, uint id)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT mail_id,revision,recipient_id,sender_id,sender_name,body,gold,created_at,expires_at,state,system_mail,recipient_account
            FROM mailbox_messages WHERE mail_id=$mail AND recipient_id=$id;
            """;
        command.Parameters.AddWithValue("$mail", id);
        command.Parameters.AddWithValue("$id", characterId);
        MailboxEntry entry;
        string account;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                return null;
            entry = new(checked((uint)reader.GetInt64(0)), reader.GetInt64(1), checked((ushort)reader.GetInt32(2)),
                checked((ushort)reader.GetInt32(3)), reader.GetString(4), reader.GetString(5), checked((uint)reader.GetInt64(6)),
                reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), checked((ushort)reader.GetInt32(9)), reader.GetInt32(10) != 0, []);
            account = reader.GetString(11);
        }
        command.CommandText = """
            SELECT m.attachment_id,i.item_uid,m.source_list,m.source_slot,i.item_core,a.detail,p.detail,
                   i.account,i.character_id,(SELECT count(*) FROM character_inventory_slots s WHERE s.item_uid=i.item_uid)
            FROM mailbox_attachments m JOIN character_inventory_items i ON i.item_uid=m.item_uid
            LEFT JOIN inventory_avatar_details a ON a.item_uid=i.item_uid
            LEFT JOIN inventory_creature_details p ON p.item_uid=i.item_uid
            WHERE m.mail_id=$mail AND m.claimed=0 ORDER BY m.ordinal;
            """;
        var attachments = ImmutableArray.CreateBuilder<MailboxAttachment>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var core = JsonSerializer.Deserialize<ItemCore>(reader.GetString(4)) ?? throw new InvalidDataException("邮件核心缺失");
                var item = new InventoryItem(reader.GetInt64(1), new((InventoryListType)reader.GetInt32(2), checked((short)reader.GetInt32(3))), core)
                {
                    Avatar = reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<AvatarDetail>(reader.GetString(5)),
                    Creature = reader.IsDBNull(6) ? null : JsonSerializer.Deserialize<CreatureDetail>(reader.GetString(6))
                };
                core.Validate();
                item.ValidateDetails(false);
                if (reader.GetString(7) != account || reader.GetInt32(8) != characterId || reader.GetInt32(9) != 0)
                    throw new InvalidDataException("邮件附件所有权失配或同时存在真实库存槽位");
                attachments.Add(new(checked((uint)reader.GetInt64(0)), item));
            }
        return entry with { Attachments = attachments.ToImmutable() };
    }

    public ImmutableArray<MailboxClaim> LoadMailClaims(AccountOwners.Lease owner, ushort characterId, ImmutableArray<uint> ids, long now) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        RequireMailOwner(db, tx, owner, characterId);
        var claims = ReadClaims(db, tx, characterId, ids, now);
        tx.Commit();
        return claims;
    });

    static ImmutableArray<MailboxClaim> ReadClaims(SqliteConnection db, SqliteTransaction tx, ushort characterId, ImmutableArray<uint> ids, long now)
    {
        ValidateMailIds(ids);
        var mails = new Dictionary<uint, MailboxEntry?>();
        var claims = ImmutableArray.CreateBuilder<MailboxClaim>();
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT mail_id FROM mailbox_attachments WHERE attachment_id=$attachment AND claimed=0";
        var attachmentId = command.Parameters.Add("$attachment", SqliteType.Integer);
        foreach (uint id in ids)
        {
            uint mailId = id;
            bool attachment = (id & MailboxAttachment.ClaimFlag) != 0;
            if (attachment)
            {
                attachmentId.Value = id & ~MailboxAttachment.ClaimFlag;
                if (command.ExecuteScalar() is not long value)
                    continue;
                mailId = checked((uint)value);
            }
            if (!mails.TryGetValue(mailId, out var mail))
                mails.Add(mailId, mail = ReadMail(db, tx, characterId, mailId));
            if (mail?.Available(now) != true)
                continue;
            var item = attachment ? mail.Attachments.SingleOrDefault(item => item.ClaimId == id) : null;
            if (!attachment || item is not null)
                claims.Add(new(id, mail, item));
        }
        return claims.ToImmutable();
    }

    static void ValidateMailIds(ImmutableArray<uint> ids)
    {
        if (ids.IsDefault || ids.Length is < 1 or > 100 || ids.Any(id => id == 0 || id >= 0x80000000 || id == MailboxAttachment.ClaimFlag) ||
            ids.Distinct().Count() != ids.Length)
            throw new InvalidDataException("邮件对象列表无效");
    }

    public MailboxResult SendMail(AccountOwners.Lease owner, Character expected, MailboxSendPlan plan) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var recipient = FindMailRecipient(db, tx, plan.Request.Recipient);
        if (recipient is null || recipient.Id != plan.Recipient.Id || recipient.Account != plan.Recipient.Account)
            return MailboxResult.Reject(MailboxError.RecipientMissing);
        if (plan.Request.Gold != 0 && recipient.Level != plan.Recipient.Level)
            return MailboxResult.Reject(MailboxError.InvalidState);
        if (plan.Request.Gold > 0)
        {
            using var counter = db.CreateCommand();
            counter.Transaction = tx;
            counter.Parameters.AddWithValue("$id", expected.Id);
            counter.Parameters.AddWithValue("$day", plan.Day);
            counter.CommandText = "SELECT amount FROM mailbox_sent_gold WHERE character_id=$id AND day=$day";
            long sent = counter.ExecuteScalar() as long? ?? 0;
            if (sent + plan.Request.Gold > MailboxService.DailyGoldLimit(expected.Level))
                return MailboxResult.Reject(MailboxError.DailyGoldLimit);
            counter.Parameters.AddWithValue("$amount", sent + plan.Request.Gold);
            counter.CommandText = """
                INSERT INTO mailbox_sent_gold(character_id,day,amount) VALUES($id,$day,$amount)
                ON CONFLICT(character_id) DO UPDATE SET day=excluded.day,amount=excluded.amount;
                """;
            counter.ExecuteNonQuery();
        }
        var retained = plan.Attachments.Where(item => item.Uid != 0).Select(item => item.Uid).ToHashSet();
        var character = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory, retained);
        uint id = InsertMail(db, tx, plan.Recipient, expected.Id, expected.Name, plan.Request.Text, plan.Request.Gold,
            plan.Created, plan.Created + 15 * 86400, null, null);
        var held = ImmutableArray.CreateBuilder<InventoryItem>();
        for (int index = 0; index < plan.Attachments.Length; index++)
        {
            var item = plan.Attachments[index];
            var source = plan.Inventory.BeforeIndex.Find(item.Position) ?? throw new InvalidDataException("邮件源实例缺失");
            item = HoldMailItem(db, tx, plan.Recipient, item, owner.Account, expected.Id);
            InsertMailAttachment(db, tx, id, index, source.Uid, item);
            held.Add(item);
        }
        AuditMail(db, tx, id, owner.Account, expected.Id, owner.Generation, "send", plan.Created,
            new { Gold = plan.Request.Gold, plan.Fee, Items = held.ToImmutable() });
        tx.Commit();
        return new(MailboxError.None, character, id) { Recipient = plan.Recipient };
    });

    public MailboxResult ClaimMail(AccountOwners.Lease owner, Character expected, MailboxClaimPlan plan) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        RequireMailOwner(db, tx, owner, expected.Id);
        var ids = plan.Claims.Select(claim => claim.ClaimId).ToImmutableArray();
        long now = Math.Max(plan.Now, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var current = ReadClaims(db, tx, expected.Id, ids, now);
        if (current.Length != plan.Claims.Length || current.Where((claim, index) =>
            claim.Mail.Revision != plan.Claims[index].Mail.Revision || claim.Mail.Gold != plan.Claims[index].Mail.Gold ||
            !claim.Mail.Attachments.SequenceEqual(plan.Claims[index].Mail.Attachments)).Any())
            return MailboxResult.Reject(MailboxError.MailMissing);
        if (plan.Items.Any(item => item.IsExpired(now)))
            return MailboxResult.Reject(MailboxError.ExpiredItem);

        // 写入前读取原库存时，邮箱引用仍需存在；入包与分离引用一起提交。
        using var command = db.CreateCommand();
        command.Transaction = tx;
        var incoming = plan.Items.ToDictionary(item => item.Uid);
        var character = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory, incoming: incoming);
        foreach (var item in plan.Items)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$uid", item.Uid);
            command.CommandText = "UPDATE mailbox_attachments SET claimed=1,item_uid=NULL WHERE item_uid=$uid AND claimed=0";
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("邮件附件领取失去资格");
        }
        foreach (var group in current.GroupBy(claim => claim.Mail.Id))
        {
            var mail = group.First().Mail;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$mail", mail.Id);
            command.Parameters.AddWithValue("$revision", mail.Revision);
            command.Parameters.AddWithValue("$gold", plan.GoldMails.Contains(mail.Id) ? 0 : mail.Gold);
            command.CommandText = "UPDATE mailbox_messages SET gold=$gold,state=CASE WHEN state=3 THEN 3 ELSE 2 END,revision=revision+1 WHERE mail_id=$mail AND revision=$revision";
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("邮件领取版本已变化");
            AuditMail(db, tx, mail.Id, owner.Account, expected.Id, owner.Generation, "claim", plan.Now,
                new { Claims = group.Select(claim => claim.ClaimId).ToArray(), Gold = plan.GoldMails.Contains(mail.Id) ? mail.Gold : 0,
                    Items = plan.Items.Where(item => mail.Attachments.Any(attachment => attachment.Item.Uid == item.Uid)).ToArray() });
        }
        var retained = character.Inventory.Items.Select(item => item.Uid).ToHashSet();
        foreach (var item in plan.Items.Where(item => !retained.Contains(item.Uid)))
            DeleteHeldItem(db, tx, owner.Account, expected.Id, item.Uid);
        var claimed = ImmutableArray.CreateBuilder<MailboxClaimChange>();
        foreach (var claim in current)
        {
            if (claim.Attachment is { } attachment)
                claimed.Add(new(claim.Mail.Id, attachment.ClaimId));
            else if (claim.Mail.Attachments.IsEmpty)
                claimed.Add(new(claim.Mail.Id, claim.Mail.Id));
            else
                foreach (var item in claim.Mail.Attachments)
                    claimed.Add(new(claim.Mail.Id, item.ClaimId));
        }
        tx.Commit();
        return new(MailboxError.None, character) { Claimed = claimed.ToImmutable() };
    });

    public MailboxResult ChangeMailState(AccountOwners.Lease owner, ushort characterId, ImmutableArray<uint> ids, ushort state, long now) => owner.Execute(() =>
    {
        ValidateMailIds(ids);
        if (state is not (0 or 2 or 3) || ids.Any(id => id >= MailboxAttachment.ClaimFlag))
            return MailboxResult.Reject(MailboxError.InvalidRequest);
        using var db = database.Open();
        RequireMailOwner(db, null, owner, characterId);
        // 已读的重复请求保持只读，不为相同状态申请 writer。
        using (var read = db.BeginTransaction(deferred: true))
        {
            var entries = ids.Select(id => ReadMail(db, read, characterId, id)).ToArray();
            if (entries.All(mail => mail is not null && (state == 0 && mail.State == 0 || mail.Available(now) &&
                (state == 2 && mail.State is 2 or 3 || state == 3 && mail.State == 3))))
            {
                read.Commit();
                return new() { Changes = entries.Select(mail => new MailboxStateChange(mail!.Id, mail.State)).ToImmutableArray() };
            }
        }
        using var tx = db.BeginTransaction(deferred: false);
        var mails = ids.Select(id => ReadMail(db, tx, characterId, id)).ToArray();
        if (mails.Any(mail => mail is null || !(state == 0 && mail.State == 0) && !mail.Available(now)))
            return MailboxResult.Reject(MailboxError.MailMissing);
        if (state == 0 && mails.Any(mail => mail!.Gold != 0 || mail.Attachments.Length != 0))
            return MailboxResult.Reject(MailboxError.UnclaimedAssets);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", characterId);
        if (state == 3)
        {
            command.CommandText = "SELECT count(*) FROM mailbox_messages WHERE recipient_id=$id AND state=3";
            if ((long)command.ExecuteScalar()! + mails.Count(mail => mail!.State != 3) > MailboxService.StorageLimit)
                return MailboxResult.Reject(MailboxError.StorageFull);
        }
        foreach (var mail in mails.OfType<MailboxEntry>())
        {
            ushort next = state == 2 && mail.State == 3 ? (ushort)3 : state;
            if (next == mail.State)
                continue;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$mail", mail.Id);
            command.Parameters.AddWithValue("$state", next);
            command.CommandText = "UPDATE mailbox_messages SET state=$state,revision=revision+1 WHERE mail_id=$mail";
            command.ExecuteNonQuery();
            AuditMail(db, tx, mail.Id, owner.Account, characterId, owner.Generation, "state", now, new { Before = mail.State, After = next });
        }
        tx.Commit();
        return new() { Changes = mails.Select(mail => new MailboxStateChange(mail!.Id, state == 2 && mail.State == 3 ? (ushort)3 : state)).ToImmutableArray() };
    });

    public void ExpireMailbox(AccountOwners.Lease owner, ushort characterId, long now) => owner.Execute(() =>
    {
        using var db = database.Open();
        RequireMailOwner(db, null, owner, characterId);
        using var command = db.CreateCommand();
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$now", now);
        command.CommandText = "SELECT mail_id FROM mailbox_messages WHERE recipient_id=$id AND state IN (1,2) AND expires_at<=$now ORDER BY expires_at,mail_id LIMIT 200";
        var ids = new List<uint>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                ids.Add(checked((uint)reader.GetInt64(0)));
        if (ids.Count == 0)
            return true;
        using var tx = db.BeginTransaction(deferred: false);
        command.Transaction = tx;
        foreach (uint id in ids)
        {
            var mail = ReadMail(db, tx, characterId, id);
            if (mail is null || mail.State is not (1 or 2) || mail.Expires > now || mail.Expires is null)
                continue;
            AuditMail(db, tx, id, owner.Account, characterId, owner.Generation, "expire", now, new { mail.Gold, mail.Attachments });
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$mail", id);
            command.CommandText = "UPDATE mailbox_attachments SET claimed=1,item_uid=NULL WHERE mail_id=$mail AND claimed=0";
            command.ExecuteNonQuery();
            foreach (var attachment in mail.Attachments)
                DeleteHeldItem(db, tx, owner.Account, characterId, attachment.Item.Uid);
            command.CommandText = "UPDATE mailbox_messages SET state=0,gold=0,revision=revision+1 WHERE mail_id=$mail";
            command.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    });

    public MailboxResult DeliverMail(MailboxDelivery delivery)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var result = DeliverMailInTransaction(db, tx, delivery);
        tx.Commit();
        return result;
    }

    // 通关等资产业务复用外层事务；不能在奖励写事务内另开系统邮件写事务。
    static MailboxResult DeliverMailInTransaction(SqliteConnection db, SqliteTransaction tx, MailboxDelivery delivery)
    {
        if (string.IsNullOrWhiteSpace(delivery.Key) || delivery.Key.Length > 128 || delivery.Items.IsDefault || delivery.Items.Length > 10 ||
            delivery.Items.Any(item => item.Uid != 0) || delivery.Gold > int.MaxValue || delivery.Created < 0 || delivery.Expires <= delivery.Created ||
            delivery.SenderName.Contains('\0') || CharacterName.Encoding.GetByteCount(delivery.SenderName) > 29 ||
            delivery.Text.Contains('\0') || CharacterName.Encoding.GetByteCount(delivery.Text) > 511)
            return MailboxResult.Reject(MailboxError.InvalidRequest);
        foreach (var item in delivery.Items)
        {
            item.Core.Validate();
            item.ValidateDetails(true);
        }
        string content = JsonSerializer.Serialize(new { delivery.Recipient.Id, delivery.Recipient.Account, delivery.SenderName,
            delivery.Text, delivery.Gold, delivery.Expires, delivery.Items });
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$key", delivery.Key);
        command.CommandText = "SELECT mail_id,delivery_content FROM mailbox_messages WHERE delivery_key=$key";
        using (var reader = command.ExecuteReader())
            if (reader.Read())
            {
                if (reader.GetString(1) != content)
                    throw new InvalidOperationException("同一系统邮件 key 的资产或接收者已变化");
                return new(MailboxError.None, MailId: checked((uint)reader.GetInt64(0))) { Recipient = delivery.Recipient };
            }
        if (FindMailRecipient(db, tx, delivery.Recipient.Name) != delivery.Recipient)
            return MailboxResult.Reject(MailboxError.RecipientMissing);
        uint id = InsertMail(db, tx, delivery.Recipient, 0, delivery.SenderName, delivery.Text, delivery.Gold,
            delivery.Created, delivery.Expires, delivery.Key, content);
        var held = ImmutableArray.CreateBuilder<InventoryItem>();
        for (int index = 0; index < delivery.Items.Length; index++)
        {
            var item = HoldMailItem(db, tx, delivery.Recipient, delivery.Items[index], delivery.Recipient.Account, delivery.Recipient.Id);
            InsertMailAttachment(db, tx, id, index, 0, item);
            held.Add(item);
        }
        AuditMail(db, tx, id, delivery.Recipient.Account, delivery.Recipient.Id, 0, "system", delivery.Created,
            new { delivery.Key, delivery.Gold, Items = held.ToImmutable() });
        return new(MailboxError.None, MailId: id) { Recipient = delivery.Recipient };
    }

    static uint InsertMail(SqliteConnection db, SqliteTransaction tx, MailboxRecipient recipient, ushort senderId, string sender,
        string text, uint gold, long created, long? expires, string? key, string? content)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO mailbox_messages(recipient_id,recipient_account,sender_id,sender_name,body,gold,created_at,expires_at,system_mail,delivery_key,delivery_content)
            VALUES($id,$account,$senderId,$sender,$text,$gold,$created,$expires,$system,$key,$content) RETURNING mail_id;
            """;
        command.Parameters.AddWithValue("$id", recipient.Id);
        command.Parameters.AddWithValue("$account", recipient.Account);
        command.Parameters.AddWithValue("$senderId", senderId);
        command.Parameters.AddWithValue("$sender", sender);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$gold", (long)gold);
        command.Parameters.AddWithValue("$created", created);
        command.Parameters.AddWithValue("$expires", (object?)expires ?? DBNull.Value);
        command.Parameters.AddWithValue("$system", key is null ? 0 : 1);
        command.Parameters.AddWithValue("$key", (object?)key ?? DBNull.Value);
        command.Parameters.AddWithValue("$content", (object?)content ?? DBNull.Value);
        return checked((uint)(long)command.ExecuteScalar()!);
    }

    static InventoryItem HoldMailItem(SqliteConnection db, SqliteTransaction tx, MailboxRecipient recipient,
        InventoryItem item, string sourceAccount, ushort sourceCharacter)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", recipient.Id);
        command.Parameters.AddWithValue("$account", recipient.Account);
        command.Parameters.AddWithValue("$core", JsonSerializer.Serialize(item.Core));
        if (item.Uid == 0)
        {
            command.CommandText = "INSERT INTO character_inventory_items(character_id,account,item_core) VALUES($id,$account,$core) RETURNING item_uid";
            item = item with { Uid = (long)command.ExecuteScalar()! };
            if (item.Avatar is not null || item.Creature is not null)
                item = item with { Core = item.Core with { Value = checked((int)item.Uid) } };
        }
        command.Parameters.AddWithValue("$uid", item.Uid);
        command.Parameters.AddWithValue("$sourceAccount", sourceAccount);
        command.Parameters.AddWithValue("$sourceCharacter", sourceCharacter);
        command.Parameters["$core"].Value = JsonSerializer.Serialize(item.Core);
        // 新建行的 owner 已是接收者；整件转移只允许原发送者拥有且已脱离真实槽位的 UID。
        command.CommandText = """
            UPDATE character_inventory_items SET character_id=$id,account=$account,item_core=$core,updated_at=CURRENT_TIMESTAMP
            WHERE item_uid=$uid AND ((account=$sourceAccount AND character_id=$sourceCharacter) OR
              (account=$account AND character_id=$id))
              AND NOT EXISTS(SELECT 1 FROM character_inventory_slots WHERE item_uid=$uid);
            """;
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("邮件转移实例失去所有权或仍位于库存");
        if (item.Avatar is not null)
        {
            command.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(item.Avatar));
            command.CommandText = "INSERT INTO inventory_avatar_details(item_uid,detail) VALUES($uid,$detail) ON CONFLICT(item_uid) DO UPDATE SET detail=excluded.detail";
            command.ExecuteNonQuery();
        }
        if (item.Creature is not null)
        {
            command.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(item.Creature));
            command.CommandText = "INSERT INTO inventory_creature_details(item_uid,detail) VALUES($uid,$detail) ON CONFLICT(item_uid) DO UPDATE SET detail=excluded.detail";
            command.ExecuteNonQuery();
        }
        return item;
    }

    static void InsertMailAttachment(SqliteConnection db, SqliteTransaction tx, uint id, int ordinal, long sourceUid, InventoryItem item)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO mailbox_attachments(mail_id,ordinal,item_uid,source_uid,source_list,source_slot) VALUES($mail,$ordinal,$uid,$source,$list,$slot)";
        command.Parameters.AddWithValue("$mail", id);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$uid", item.Uid);
        command.Parameters.AddWithValue("$source", sourceUid);
        command.Parameters.AddWithValue("$list", (byte)item.Position.List);
        command.Parameters.AddWithValue("$slot", item.Position.Index);
        command.ExecuteNonQuery();
    }

    static void DeleteHeldItem(SqliteConnection db, SqliteTransaction tx, string account, ushort characterId, long uid)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM character_inventory_items WHERE item_uid=$uid AND account=$account AND character_id=$id AND NOT EXISTS(SELECT 1 FROM character_inventory_slots WHERE item_uid=$uid)";
        command.Parameters.AddWithValue("$uid", uid);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$id", characterId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("已领取或到期的邮件实例仍被库存引用");
    }

    static void AuditMail(SqliteConnection db, SqliteTransaction tx, uint mailId, string account, ushort id, long generation, string action, long now, object assets)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO mailbox_audit_log(mail_id,account,character_id,generation,action_name,created_at,assets) VALUES($mail,$account,$id,$generation,$action,$now,$assets)";
        command.Parameters.AddWithValue("$mail", mailId);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$assets", JsonSerializer.Serialize(assets));
        command.ExecuteNonQuery();
    }
}
