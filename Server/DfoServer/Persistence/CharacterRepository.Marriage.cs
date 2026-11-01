using System.Text.Json;
using System.Text.Json.Nodes;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IMarriageStore
{
    const string MarriageColumns = "r.marriage_id,r.context_id,r.revision,r.first_id,r.second_id,r.state,r.created_at,r.ended_at,r.aggregate_json";

    public Character CommitMarriageHallTravel(AccountOwners.Lease owner, Character expected, MarriageRecord wedding,
        Location destination, Location? returnPoint, long now, InventoryPlan? inventory = null,
        MarriageRecord? afterGift = null) => owner.Execute(() =>
    {
        if ((inventory is null) != (afterGift is null))
            throw new InvalidDataException("婚礼入场礼计划与领取凭据必须同时提交");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitMarriageHallTravel");
        using var tx = write.Transaction;
        var current = ReadMarriageById(db, tx, wedding.Id);
        if (current is null || !SameMarriage(current, wedding) || current.Wedding is not { State: 1 } ceremony ||
            now >= ceremony.StartsAt || destination.Town != 39 || destination.Area != ceremony.HallArea ||
            !current.IsMember(expected.Id) && (!ceremony.Invitations.Any(row => row.CharacterId == expected.Id) ||
                !ceremony.Gifts.Any(row => row.CharacterId == expected.Id && !row.Settled)))
            throw new InvalidOperationException("婚礼入场资格或预约版本已改变");
        var saved = SaveInTransaction(db, tx, owner, expected, destination, returnPoint, inventory);
        if (afterGift is not null) SaveMarriageInTransaction(db, tx, current, afterGift);
        tx.Commit();
        return saved with { WeddingHall = new(current.Id, ceremony.Id, ceremony.HallArea, ceremony.ServerIndex) };
    });

    public MailboxRecipient? FindMarriageRecipient(ushort characterId)
    {
        using var db = database.Open();
        return FindMailRecipientById(db, null, characterId);
    }

    public MarriageCharacterCard? FindMarriageCard(ushort characterId)
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        var recipient = FindMailRecipientById(db, tx, characterId);
        if (recipient is null) return null;
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT s.slot_index,json_extract(i.item_core,'$.ItemId'),COALESCE(json_extract(a.detail,'$.ClearAvatarId'),0)
            FROM character_inventory_slots s JOIN character_inventory_items i
              ON i.item_uid=s.item_uid AND i.account=s.account AND i.character_id=s.character_id
            LEFT JOIN inventory_avatar_details a ON a.item_uid=i.item_uid
            WHERE s.character_id=$id AND s.account=$account AND s.list_type=$equipment AND s.slot_index BETWEEN 0 AND 12
            ORDER BY s.slot_index
            """;
        query.Parameters.AddWithValue("$id", characterId);
        query.Parameters.AddWithValue("$account", recipient.Account);
        query.Parameters.AddWithValue("$equipment", (int)InventoryListType.Equipment);
        var equipment = new int[13];
        var clearAvatarIds = new int[13];
        using (var rows = query.ExecuteReader())
            while (rows.Read())
            {
                int slot = rows.GetInt32(0);
                equipment[slot] = checked((int)rows.GetInt64(1));
                clearAvatarIds[slot] = checked((int)rows.GetInt64(2));
            }
        tx.Commit();
        return new(recipient, Array.AsReadOnly(equipment)) { ClearAvatarIds = Array.AsReadOnly(clearAvatarIds) };
    }

    public IReadOnlyList<MarriageRecord> FindPendingWeddings()
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT {MarriageColumns} FROM marriage_records r WHERE r.state IN(2,3) ORDER BY r.context_id";
        var records = new List<MarriageRecord>();
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var record = ReadMarriageRow(rows);
            if (record.Wedding is { State: 1 }) records.Add(record);
        }
        return records;
    }

    // 定时恢复只结算已有账本和系统邮件，不改写任一角色库存，也不另建在线owner。
    public MarriageRecord CommitMarriageSettlement(MarriageRecord before, MarriageRecord after, IReadOnlyList<MailboxDelivery>? mail = null)
    {
        ValidateMarriageChange(before, after);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitMarriageSettlement");
        using var tx = write.Transaction;
        SaveMarriageInTransaction(db, tx, before, after);
        WriteMarriageMail(db, tx, mail);
        tx.Commit();
        return after;
    }

    public MarriageRecord? FindMarriage(ushort characterId)
    {
        using var db = database.Open();
        return ReadMarriage(db, null, characterId);
    }

    public MarriageRecord? FindMarriage(Guid id)
    {
        using var db = database.Open();
        return ReadMarriageById(db, null, id);
    }

    public MarriageRecord? FindMarriageByContext(uint contextId)
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT {MarriageColumns} FROM marriage_records r WHERE r.context_id=$context";
        command.Parameters.AddWithValue("$context", (long)contextId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMarriageRow(reader) : null;
    }

    public MarriageRecord? FindLatestMarriage(ushort characterId)
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT {MarriageColumns} FROM marriage_records r WHERE r.first_id=$id OR r.second_id=$id " +
            "ORDER BY r.created_at DESC,r.context_id DESC LIMIT 1";
        command.Parameters.AddWithValue("$id", characterId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMarriageRow(reader) : null;
    }

    // 共用角色装载事务；查询不读取配偶资产、不取得配偶owner，也不初始化或改写任何行。
    internal static MarriageRecord? ReadMarriage(SqliteConnection db, SqliteTransaction? tx, ushort characterId)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {MarriageColumns} FROM marriage_active_members m " +
            "JOIN marriage_records r ON r.marriage_id=m.marriage_id WHERE m.character_id=$id";
        command.Parameters.AddWithValue("$id", characterId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var record = ReadMarriageRow(reader);
        if (!record.Active || !record.IsMember(characterId)) throw new InvalidDataException("有效婚姻索引与聚合成员不一致");
        return record;
    }

    static MarriageRecord? ReadMarriageById(SqliteConnection db, SqliteTransaction? tx, Guid id)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {MarriageColumns} FROM marriage_records r WHERE r.marriage_id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMarriageRow(reader) : null;
    }

    static MarriageRecord ReadMarriageRow(SqliteDataReader row)
    {
        MarriageRecord record;
        try { record = JsonSerializer.Deserialize<MarriageRecord>(row.GetString(8)) ?? throw new JsonException("null marriage aggregate"); }
        catch (JsonException error) { throw new InvalidDataException("婚姻聚合存档无法读取", error); }
        ValidateMarriageRecord(record);
        if (record.Id.ToString("N") != row.GetString(0) || record.ContextId != row.GetInt64(1) || record.Revision != row.GetInt64(2) ||
            record.FirstId != row.GetInt32(3) || record.SecondId != row.GetInt32(4) || record.State != row.GetInt32(5) ||
            record.CreatedAt != row.GetInt64(6) || record.EndedAt != row.GetInt64(7))
            throw new InvalidDataException("婚姻聚合与关系元数据不一致");
        return record;
    }

    public MarriageCommit CommitMarriageSingle(AccountOwners.Lease owner, Character expected, InventoryPlan inventory,
        MarriageRecord? before, MarriageRecord after, IReadOnlyList<MailboxDelivery>? mail = null) => owner.Execute(() =>
    {
        if (before is null) throw new InvalidOperationException("新婚姻关系必须由双方联合提交");
        ValidateMarriageChange(before, after);
        ValidateInventoryPlan(expected, inventory);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitMarriageSingle");
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        ValidateMarriageActor(current, expected);
        // 来宾可按上层授权提交请柬/礼金；这里只保护同一关系身份、CAS及其全部资产。
        var committed = CommitInventoryInTransaction(db, tx, owner, current, expected, inventory);
        SaveMarriageInTransaction(db, tx, before, after);
        committed = ProjectMarriageCommit(committed, before, after);
        WriteMarriageMail(db, tx, mail);
        tx.Commit();
        return new MarriageCommit(after, committed);
    });

    public MarriageCommit CommitMarriageTogether(AccountOwners.Lease firstOwner, AccountOwners.Lease secondOwner,
        Character first, Character second, InventoryPlan firstInventory, InventoryPlan secondInventory,
        MarriageRecord? before, MarriageRecord after, IReadOnlyList<MailboxDelivery>? mail = null) =>
        AccountOwners.Lease.ExecuteTogether(firstOwner, secondOwner, () =>
    {
        if (StringComparer.Ordinal.Equals(firstOwner.Account, secondOwner.Account) || first.Id == second.Id ||
            !after.IsMember(first.Id) || !after.IsMember(second.Id)) throw new InvalidDataException("婚姻联合提交参与者不符");
        ValidateMarriageChange(before, after);
        ValidateInventoryPlan(first, firstInventory);
        ValidateInventoryPlan(second, secondInventory);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitMarriageTogether");
        using var tx = write.Transaction;
        var currentFirst = Require(db, tx, firstOwner.Account, first.Name);
        var currentSecond = Require(db, tx, secondOwner.Account, second.Name);
        ValidateMarriageActor(currentFirst, first);
        ValidateMarriageActor(currentSecond, second);
        var committedFirst = CommitInventoryInTransaction(db, tx, firstOwner, currentFirst, first, firstInventory);
        var committedSecond = CommitInventoryInTransaction(db, tx, secondOwner, currentSecond, second, secondInventory);
        SaveMarriageInTransaction(db, tx, before, after);
        WriteMarriageMail(db, tx, mail);
        tx.Commit();
        return new MarriageCommit(after, ProjectMarriageCommit(committedFirst, before, after),
            ProjectMarriageCommit(committedSecond, before, after));
    });

    static void ValidateMarriageActor(Character current, Character expected)
    {
        if (current.Id != expected.Id || current.Slot != expected.Slot || current.Level != expected.Level || current.Job != expected.Job ||
            current.GrowType != expected.GrowType || current.IsPvpPrivate != expected.IsPvpPrivate ||
            !current.CompletedQuests.Where(IsMarriageQuest).SequenceEqual(expected.CompletedQuests.Where(IsMarriageQuest)) ||
            !current.ActiveMainQuests.Where(quest => IsMarriageQuest(quest.QuestId)).SequenceEqual(
                expected.ActiveMainQuests.Where(quest => IsMarriageQuest(quest.QuestId))))
            throw new InvalidOperationException("婚姻操作角色身份或资格已变化");
    }

    static bool IsMarriageQuest(int id) => id is 20737 or 20738;

    static Character ProjectMarriageCommit(Character character, MarriageRecord? before, MarriageRecord after)
    {
        if (!after.IsMember(character.Id)) return character;
        character = character with { Marriage = after.Active ? after : null };
        if (before is not null && after.Active) return character;
        return character with
        {
            ActiveMainQuests = character.ActiveMainQuests.Where(quest => !IsMarriageQuest(quest.QuestId)).ToArray(),
            CompletedQuests = character.CompletedQuests.Where(id => !IsMarriageQuest(id)).ToArray(),
            QuestDailyClaims = character.QuestDailyClaims.Remove(20737).Remove(20738),
            QuestAnswers = character.QuestAnswers.Remove(20737).Remove(20738)
        };
    }

    // 只参与外层事务；副本冻结结算可共用相同CAS，不另开连接、writer或提交。
    internal static void SaveMarriageInTransaction(SqliteConnection db, SqliteTransaction tx, MarriageRecord? before, MarriageRecord after)
    {
        ValidateMarriageChange(before, after);
        WriteMarriage(db, tx, before, after);
        if (before is not null && after.Active) return;
        // 这两个PVF任务均为角色级[wedding]任务。保留完成/通关回执，新的接取身份不会复用旧奖。
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            DELETE FROM character_active_main_quests WHERE character_id IN($first,$second) AND quest_id IN(20737,20738);
            DELETE FROM character_completed_quests WHERE character_id IN($first,$second) AND quest_id IN(20737,20738);
            """;
        command.Parameters.AddWithValue("$first", after.FirstId);
        command.Parameters.AddWithValue("$second", after.SecondId);
        command.ExecuteNonQuery();
    }

    static void ValidateMarriageChange(MarriageRecord? before, MarriageRecord after)
    {
        ValidateMarriageRecord(after);
        if (before is null)
        {
            if (!after.Active || after.Revision != 1) throw new InvalidDataException("新婚姻关系必须从有效状态版本1开始");
            return;
        }
        ValidateMarriageRecord(before);
        if (!before.Active || before.Id != after.Id || before.ContextId != after.ContextId || before.FirstId != after.FirstId ||
            before.SecondId != after.SecondId || before.CreatedAt != after.CreatedAt || after.Revision != checked(before.Revision + 1))
            throw new InvalidDataException("婚姻身份不可修改、历史不可复活且版本必须递增1");
    }

    static void ValidateMarriageRecord(MarriageRecord record)
    {
        if (record.Id == Guid.Empty || record.ContextId == 0 || record.Revision <= 0 || record.FirstId == 0 || record.SecondId == 0 ||
            record.FirstId == record.SecondId || record.State > 4 || record.CreatedAt < 0 || record.MarriedAt < 0 || record.EndedAt < 0 ||
            record.Active && record.EndedAt != 0 || !record.Active && record.EndedAt < record.CreatedAt || record.PrepareClears < 0 ||
            record.Members is null || record.Members.Count != 2 || !record.Members.ContainsKey(record.FirstId) || !record.Members.ContainsKey(record.SecondId) ||
            record.Furniture is null || record.Storage is null || record.ClearEvents is null || record.Comments.IsDefault)
            throw new InvalidDataException("婚姻聚合身份、状态或集合无效");
        if (record.Members.Values.Any(member => member is null || member.LovePoints < 0 || member.LoveDay < 0 || member.CapsuleGauge < 0 ||
                member.CapsulesOwned < 0 || member.CapsulesTaken < 0 || member.CapsulesPending < 0 || member.CapsuleDay < 0) ||
            record.Furniture.Values.Concat(record.Storage.Values).Any(item => item.ItemId <= 0 || item.Count <= 0) ||
            record.ClearEvents.Contains(Guid.Empty) || record.Comments.Any(comment => comment is null || comment.Id == 0 || comment.AuthorId == 0 ||
                comment.Timestamp < 0 || comment.Name is null || comment.Text is null) ||
            record.Comments.Select(comment => comment.Id).Distinct().Count() != record.Comments.Length)
            throw new InvalidDataException("婚姻成员、家具或留言存档无效");
        if (record.Tree is { } tree && (tree.Seed <= 0 || tree.Expiry < 0 || tree.Growth < 0 || tree.WaterDay < 0 || tree.Waters < 0))
            throw new InvalidDataException("夫妻树存档无效");
        if (record.Wedding is not { } wedding) return;
        if (wedding.Id == Guid.Empty || wedding.Type < 0 || !record.IsMember(wedding.PayerId) || wedding.PaidGold > int.MaxValue ||
            wedding.PaidTicketId < 0 || wedding.PaidTicketCount < 0 || (wedding.PaidTicketId == 0) != (wedding.PaidTicketCount == 0) ||
            wedding.ReservedAt < 0 || wedding.StartsAt < 0 || wedding.EndsAt < 0 || wedding.SelectionSeconds < 0 || wedding.InviteRemaining is null ||
            wedding.InviteRemaining.Any(row => !record.IsMember(row.Key) || row.Value < 0) || wedding.Invitations.IsDefault || wedding.Gifts.IsDefault ||
            wedding.Invitations.Any(row => row is null || row.CharacterId == 0 || row.Name is null || row.SentAt < 0 || !record.IsMember(row.InviterId)) ||
            wedding.Gifts.Any(row => row is null || row.Id == Guid.Empty || row.CharacterId == 0 || row.Name is null ||
                row.Text is null || row.Text.Any(char.IsControl) || CharacterName.Encoding.GetByteCount(row.Text) > 255 ||
                row.Gold > int.MaxValue || row.SentAt < 0) ||
            wedding.Gifts.Select(row => row.Id).Distinct().Count() != wedding.Gifts.Length)
            throw new InvalidDataException("婚礼支付、邀请或礼金账本无效");
    }

    static void WriteMarriage(SqliteConnection db, SqliteTransaction tx, MarriageRecord? before, MarriageRecord after)
    {
        var current = ReadMarriageById(db, tx, after.Id);
        if (before is null ? current is not null : current is null || !SameMarriage(current, before))
            throw new InvalidOperationException("婚姻聚合版本或内容已变化");
        var identities = MarriageIdentities(db, tx, after);
        ValidateMarriageWeddingHall(db, tx, after);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", after.Id.ToString("N"));
        command.Parameters.AddWithValue("$context", (long)after.ContextId);
        command.Parameters.AddWithValue("$revision", after.Revision);
        command.Parameters.AddWithValue("$first", after.FirstId);
        command.Parameters.AddWithValue("$second", after.SecondId);
        command.Parameters.AddWithValue("$firstAccount", identities.First);
        command.Parameters.AddWithValue("$secondAccount", identities.Second);
        command.Parameters.AddWithValue("$state", after.State);
        command.Parameters.AddWithValue("$created", after.CreatedAt);
        command.Parameters.AddWithValue("$ended", after.EndedAt);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(after));
        if (before is null)
        {
            using (var active = db.CreateCommand())
            {
                active.Transaction = tx;
                active.CommandText = "SELECT count(*) FROM marriage_active_members WHERE character_id=$first OR character_id=$second";
                active.Parameters.AddWithValue("$first", after.FirstId);
                active.Parameters.AddWithValue("$second", after.SecondId);
                if (Convert.ToInt64(active.ExecuteScalar()) != 0) throw new InvalidOperationException("参与角色已有有效婚姻关系");
            }
            command.CommandText = """
                INSERT INTO marriage_records(marriage_id,context_id,revision,first_id,second_id,first_account,second_account,state,created_at,ended_at,aggregate_json)
                VALUES($id,$context,$revision,$first,$second,$firstAccount,$secondAccount,$state,$created,$ended,$json)
                """;
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO marriage_active_members(character_id,marriage_id) VALUES($first,$id),($second,$id)";
            if (command.ExecuteNonQuery() != 2) throw new InvalidOperationException("婚姻双方有效索引写入失败");
        }
        else
        {
            using (var active = db.CreateCommand())
            {
                active.Transaction = tx;
                active.CommandText = "SELECT character_id FROM marriage_active_members WHERE marriage_id=$id ORDER BY character_id";
                active.Parameters.AddWithValue("$id", before.Id.ToString("N"));
                using var rows = active.ExecuteReader();
                var members = new List<ushort>();
                while (rows.Read()) members.Add(checked((ushort)rows.GetInt32(0)));
                if (!members.SequenceEqual(new[] { after.FirstId, after.SecondId }.Order()))
                    throw new InvalidOperationException("婚姻有效成员索引已变化");
            }
            command.Parameters.AddWithValue("$before", before.Revision);
            command.CommandText = """
                UPDATE marriage_records SET revision=$revision,state=$state,ended_at=$ended,aggregate_json=$json
                WHERE marriage_id=$id AND context_id=$context AND revision=$before AND first_id=$first AND second_id=$second
                  AND first_account=$firstAccount AND second_account=$secondAccount
                """;
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("婚姻关系CAS提交失败");
            if (!after.Active)
            {
                command.CommandText = "DELETE FROM marriage_active_members WHERE marriage_id=$id";
                if (command.ExecuteNonQuery() != 2) throw new InvalidOperationException("婚姻结束未释放双方有效成员索引");
            }
        }
    }

    static void ValidateMarriageWeddingHall(SqliteConnection db, SqliteTransaction tx, MarriageRecord record)
    {
        if (record.State is not (2 or 3) || record.Wedding is not { State: 1 } wedding) return;
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT 1 FROM marriage_records WHERE marriage_id<>$id AND state IN(2,3)
              AND json_extract(aggregate_json,'$.Wedding.State')=1
              AND json_extract(aggregate_json,'$.Wedding.ServerIndex')=$server
              AND json_extract(aggregate_json,'$.Wedding.ChannelNumber')=$channel
              AND json_extract(aggregate_json,'$.Wedding.HallArea')=$hall LIMIT 1
            """;
        query.Parameters.AddWithValue("$id", record.Id.ToString("N"));
        query.Parameters.AddWithValue("$server", wedding.ServerIndex);
        query.Parameters.AddWithValue("$channel", wedding.ChannelNumber);
        query.Parameters.AddWithValue("$hall", wedding.HallArea);
        if (query.ExecuteScalar() is not null) throw new InvalidOperationException("该服务器频道的婚礼区域已被预约");
    }

    static (string First, string Second) MarriageIdentities(SqliteConnection db, SqliteTransaction tx, MarriageRecord record)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT id,account FROM characters WHERE id=$first OR id=$second";
        command.Parameters.AddWithValue("$first", record.FirstId);
        command.Parameters.AddWithValue("$second", record.SecondId);
        string? first = null, second = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt32(0) == record.FirstId) first = reader.GetString(1);
            else second = reader.GetString(1);
        }
        if (first is null || second is null)
            throw new InvalidOperationException("婚姻角色已删除");
        return (first, second);
    }

    static bool SameMarriage(MarriageRecord first, MarriageRecord second)
    {
        // JSON对象按键比较；通关事件是集合，独立排序后比较，不把集合枚举顺序当作修改。
        var left = JsonSerializer.SerializeToNode(first)!;
        var right = JsonSerializer.SerializeToNode(second)!;
        left[nameof(MarriageRecord.ClearEvents)] = JsonSerializer.SerializeToNode(first.ClearEvents.Order());
        right[nameof(MarriageRecord.ClearEvents)] = JsonSerializer.SerializeToNode(second.ClearEvents.Order());
        return JsonNode.DeepEquals(left, right);
    }

    static void WriteMarriageMail(SqliteConnection db, SqliteTransaction tx, IReadOnlyList<MailboxDelivery>? mail)
    {
        if (mail is null) return;
        foreach (var delivery in mail)
            if (DeliverMailInTransaction(db, tx, delivery).Error is not MailboxError.None)
                throw new InvalidOperationException("婚姻事务邮件投递失败，整笔操作回滚");
    }
}
