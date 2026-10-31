using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Titles;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public InventorySnapshot ReadPublicTitleBook(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT i.item_uid,b.list_type,b.slot_index,i.item_core,NULL,NULL FROM character_title_book_slots b
            JOIN character_inventory_items i ON i.item_uid=b.item_uid AND i.character_id=b.character_id AND i.account=b.account
            WHERE b.character_id=$id ORDER BY b.list_type,b.slot_index;
            """;
        command.Parameters.AddWithValue("$id", characterId);
        var items = ImmutableArray.CreateBuilder<InventoryItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) items.Add(ReadInventoryItem(reader));
        var snapshot = new InventorySnapshot(items.ToImmutable());
        snapshot.Validate();
        return snapshot;
    });

    static ImmutableDictionary<int, TitleProgress> ReadTitleProgress(SqliteConnection db, ushort id, SqliteTransaction? tx)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT quest_id,remaining1,remaining2,remaining3,completed_revision FROM character_title_progress WHERE character_id=$id";
        command.Parameters.AddWithValue("$id", id);
        var progress = ImmutableDictionary.CreateBuilder<int, TitleProgress>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) progress.Add(reader.GetInt32(0), new(new(checked((ushort)reader.GetInt32(1)),
            checked((ushort)reader.GetInt32(2)), checked((ushort)reader.GetInt32(3))), reader.GetInt64(4)));
        return progress.ToImmutable();
    }
    static ImmutableHashSet<int> ReadTitleSections(SqliteConnection db, ushort id, SqliteTransaction? tx)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT section_key FROM character_title_sections WHERE character_id=$id";
        command.Parameters.AddWithValue("$id", id);
        var sections = ImmutableHashSet.CreateBuilder<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) sections.Add(reader.GetInt32(0));
        return sections.ToImmutable();
    }

    public Character CommitTitleAchievement(AccountOwners.Lease owner, TitleAchievementPlan plan) => owner.Execute(() =>
    {
        if (!plan.Changed && plan.FiendwarFacts.IsDefaultOrEmpty) return plan.Before;
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitTitleAchievement");
        using var tx = write.Transaction;
        var current = CommitTitleAchievementInTransaction(db, tx, owner, plan);
        tx.Commit();
        return current;
    });

    Character CommitTitleAchievementInTransaction(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, TitleAchievementPlan plan)
    {
        var expected = plan.Before;
        var current = Require(db, tx, owner.Account, expected.Name);
        ValidateInventoryPlan(current, plan.Inventory);
        if (current.Id != expected.Id || current.Revision != expected.Revision ||
            !current.CompletedQuests.SequenceEqual(expected.CompletedQuests) || !current.AccountCompletedQuests.SequenceEqual(expected.AccountCompletedQuests) ||
            !current.TitleProgress.OrderBy(pair => pair.Key).SequenceEqual(expected.TitleProgress.OrderBy(pair => pair.Key)) ||
            !current.TitleSections.SetEquals(expected.TitleSections) || !plan.Sections.IsSupersetOf(expected.TitleSections) ||
            expected.TitleProgress.Keys.Any(id => !plan.Progress.ContainsKey(id))) throw new InvalidOperationException("称号规划所属角色、版本或进度已变化");
        if (!plan.Changed)
        {
            ApplyFiendwarTitleFacts(db, tx, owner, expected.Id, plan.FiendwarFacts);
            return current;
        }
        if (plan.Inventory.Changed) current = CommitInventoryInTransaction(db, tx, owner, current, plan.Inventory);
        else
        {
            using var update = db.CreateCommand(); update.Transaction = tx;
            update.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
            update.Parameters.AddWithValue("$id", expected.Id); update.Parameters.AddWithValue("$account", owner.Account);
            update.Parameters.AddWithValue("$revision", expected.Revision);
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("称号角色版本失配");
            current = current with { Revision = current.Revision + 1 };
        }
        using var command = db.CreateCommand(); command.Transaction = tx;
        foreach (var (id, value) in plan.Progress)
        {
            if (expected.TitleProgress.GetValueOrDefault(id) == value) continue;
            if (expected.TitleProgress.GetValueOrDefault(id)?.Remaining.Complete == true)
                throw new InvalidOperationException("已完成称号不能重写进度");
            command.Parameters.Clear(); command.Parameters.AddWithValue("$id", current.Id); command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$quest", id); command.Parameters.AddWithValue("$first", value.Remaining.First);
            command.Parameters.AddWithValue("$second", value.Remaining.Second); command.Parameters.AddWithValue("$third", value.Remaining.Third);
            command.Parameters.AddWithValue("$revision", value.Remaining.Complete ? current.Revision : 0);
            command.CommandText = """
                INSERT INTO character_title_progress(character_id,account,quest_id,remaining1,remaining2,remaining3,completed_revision)
                VALUES($id,$account,$quest,$first,$second,$third,$revision)
                ON CONFLICT(character_id,quest_id) DO UPDATE SET remaining1=excluded.remaining1,remaining2=excluded.remaining2,
                  remaining3=excluded.remaining3,completed_revision=excluded.completed_revision;
                """;
            command.ExecuteNonQuery();
        }
        foreach (int section in plan.Sections.Except(expected.TitleSections))
        {
            command.Parameters.Clear(); command.Parameters.AddWithValue("$id", current.Id); command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$section", section); command.Parameters.AddWithValue("$revision", current.Revision);
            command.CommandText = "INSERT INTO character_title_sections(character_id,account,section_key,revision) VALUES($id,$account,$section,$revision)";
            command.ExecuteNonQuery();
        }
        if (plan.Postals.Length != plan.PostalItems.Length) throw new InvalidDataException("称号邮件与完整附件规划不一致");
        for (int at = 0; at < plan.Postals.Length; at++)
        {
            var postal = plan.Postals[at];
            var delivery = new MailboxDelivery(postal.Key + ":" + current.Id, new(current.Id, owner.Account, current.Name, current.Job, current.GrowType, current.Level),
                postal.Sender, postal.Message, 0, [plan.PostalItems[at]], DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null);
            var (postalError, _) = DeliverMailInTransaction(db, tx, delivery);
            if (postalError != MailboxError.None) throw new InvalidOperationException("称号奖励邮件无法提交：" + postalError);
        }
        ApplyFiendwarTitleFacts(db, tx, owner, expected.Id, plan.FiendwarFacts);
        return current with { TitleProgress = plan.Progress, TitleSections = plan.Sections };
    }
}
