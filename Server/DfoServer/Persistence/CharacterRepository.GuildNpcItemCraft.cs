using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Guilds;

namespace DfoServer.Persistence;

internal static class GuildNpcItemCraftSchema
{
    internal const string Sql = """
        CREATE TABLE IF NOT EXISTS character_guild_npc_item_crafts(
          craft_id TEXT PRIMARY KEY CHECK(length(craft_id)=64),
          character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
          guild_id INTEGER NOT NULL,joined_at INTEGER NOT NULL CHECK(joined_at>=0),
          npc_id INTEGER NOT NULL CHECK(npc_id=400001065),
          benefit_id INTEGER NOT NULL CHECK(benefit_id BETWEEN 7001 AND 7005),
          deadline INTEGER NOT NULL CHECK(deadline BETWEEN 1 AND 2147483647),
          item_core TEXT NOT NULL CHECK(json_valid(item_core)),
          state INTEGER NOT NULL DEFAULT 0 CHECK(state IN(0,1,2)),
          mail_id INTEGER NOT NULL DEFAULT 0 CHECK(mail_id>=0),
          CHECK((state=1 AND mail_id>0) OR (state IN(0,2) AND mail_id=0))
        );
        CREATE UNIQUE INDEX IF NOT EXISTS guild_npc_item_craft_active ON character_guild_npc_item_crafts(character_id,npc_id) WHERE state=0;
        CREATE INDEX IF NOT EXISTS guild_npc_item_craft_due ON character_guild_npc_item_crafts(deadline) WHERE state=0;
        """;
}

internal sealed partial class CharacterRepository
{
    public GuildNpcBenefitCommit ChangeGuildNpcItemCraft(GuildManagementIdentity actor, Character expected,
        GuildNpcCatalog npcs, GuildNpcBenefitDefinition definition, uint action, string requestId, long now,
        ItemCore? reward, Func<Character, InventoryPlan?> prepare) => actor.Owner.Execute<GuildNpcBenefitCommit>(() =>
    {
        if (!actor.IsCurrent() || expected.Id != actor.CharacterId || !GuildEconomyService.ValidRequestId(requestId) ||
            action is not (4 or 5) || now is <= 0 or > int.MaxValue || definition.Effect != "crafting item" ||
            definition.Common || definition.NpcId != 400001065 || definition.Parameters is not [> 0, > 0] ||
            definition.DurationSeconds <= 0 || npcs.Find(definition.NpcId) is not { } npc) return new(14);
        long deadline = now + definition.DurationSeconds;
        if (deadline > int.MaxValue || action == 4 && (reward is null || !reward.IsStackable ||
            reward.ItemId != definition.Parameters[0] || reward.Count != definition.Parameters[1] || reward.IsExpired(deadline))) return new(14);
        using var db = database.Open(); using var write = db.BeginWrite("GuildNpcItemCraft.Change");
        using var tx = write.Transaction;
        var state = ReadNpcs(db, tx, actor.Owner.Account, actor.CharacterId, npcs);
        if (state is null || !GuildRequestAvailable(db, tx, requestId, 4, actor.Owner.Account, actor.CharacterId)) return new(14);
        uint guild = state.Management.Guild.Id;
        using var command = ManagementCommand(db, tx,
            "SELECT joined_at_unix FROM guild_members WHERE character_id=$id AND guild_id=$guild",
            ("$id", actor.CharacterId), ("$guild", guild));
        long joined = Convert.ToInt64(command.ExecuteScalar());
        var replay = ReplayNpcBenefit(db, tx, actor, guild, joined, definition.NpcId, action, definition.Id, requestId, now);
        if (replay is not null) return replay;
        command.Parameters.AddWithValue("$npc", definition.NpcId);
        command.Parameters.AddWithValue("$benefit", definition.Id);
        command.Parameters.AddWithValue("$joined", joined);
        command.Parameters.AddWithValue("$now", now);
        Character? committed = null; InventoryPlan? inventory = null;
        if (action == 4)
        {
            var progress = state.Npcs.Single(row => row.NpcId == definition.NpcId);
            if (progress.Level < definition.Level || progress.UpgradeUntil > 0 || npc.Contract && progress.ContractUntil <= now) return new(14);
            command.CommandText = "SELECT 1 FROM character_guild_npc_item_crafts WHERE character_id=$id AND npc_id=$npc AND state=0";
            if (command.ExecuteScalar() is not null) return new(14);
            var current = Require(db, tx, actor.Owner.Account, actor.Name);
            if (current.Revision != expected.Revision || current.Id != expected.Id || current.Position != expected.Position) return new(14);
            inventory = prepare(current);
            if (inventory is null) return new(17);
            committed = CommitInventoryInTransaction(db, tx, actor.Owner, current, inventory);
            command.Parameters.AddWithValue("$craft", requestId);
            command.Parameters.AddWithValue("$deadline", deadline);
            command.Parameters.AddWithValue("$core", JsonSerializer.Serialize(reward));
            command.CommandText = """
                INSERT INTO character_guild_npc_item_crafts(craft_id,character_id,guild_id,joined_at,npc_id,benefit_id,deadline,item_core)
                VALUES($craft,$id,$guild,$joined,$npc,$benefit,$deadline,$core)
                """;
            command.ExecuteNonQuery();
        }
        else
        {
            command.CommandText = """
                UPDATE character_guild_npc_item_crafts SET state=2
                WHERE character_id=$id AND guild_id=$guild AND joined_at=$joined AND npc_id=$npc
                  AND benefit_id=$benefit AND state=0 AND deadline>$now
                RETURNING deadline
                """;
            if (command.ExecuteScalar() is not long savedDeadline) return new(14);
            deadline = savedDeadline;
        }
        SaveNpcBenefitReceipt(db, tx, actor, guild, joined, definition.NpcId, action, definition.Id, requestId, deadline);
        TouchManagement(db, tx, guild);
        var result = ReadNpcBenefits(db, tx, actor.Owner.Account, actor.CharacterId, now);
        if (!actor.IsCurrent()) return new(14);
        tx.Commit(); return new(0, result, Character: committed, Inventory: inventory);
    });

    public GuildNpcItemCraftCompletion[] CompleteGuildNpcItemCrafts(long now)
    {
        if (now is <= 0 or > int.MaxValue) return [];
        using var db = database.Open();
        using var due = ManagementCommand(db, null, """
            SELECT craft_id FROM character_guild_npc_item_crafts WHERE state=0 AND deadline<=$now ORDER BY deadline,craft_id LIMIT 64
            """, ("$now", now));
        var keys = new List<string>();
        using (var reader = due.ExecuteReader()) while (reader.Read()) keys.Add(reader.GetString(0));
        if (keys.Count == 0) return [];
        using var write = db.BeginWrite("GuildNpcItemCraft.Complete"); using var tx = write.Transaction;
        var notices = new List<GuildNpcItemCraftCompletion>();
        foreach (string key in keys)
        {
            using var command = ManagementCommand(db, tx, """
                SELECT character_id,guild_id,deadline,item_core FROM character_guild_npc_item_crafts
                WHERE craft_id=$key AND state=0 AND deadline<=$now
                """, ("$key", key), ("$now", now));
            ushort characterId; uint guild; long deadline; ItemCore core;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) continue;
                characterId = checked((ushort)reader.GetInt32(0)); guild = checked((uint)reader.GetInt64(1));
                deadline = reader.GetInt64(2);
                core = JsonSerializer.Deserialize<ItemCore>(reader.GetString(3)) ?? throw new InvalidDataException("公会炼金产物核心缺失");
            }
            var recipient = FindMailRecipientById(db, tx, characterId) ?? throw new InvalidDataException("公会炼金生产缺少收件人");
            var delivery = new MailboxDelivery("guild-npc-item:" + key, recipient, "公会炼金术师", "委托生产已完成，请领取产物。", 0,
                [new(0, new(InventoryListType.Main, 0), core)], deadline);
            var mailed = DeliverMailInTransaction(db, tx, delivery);
            if (mailed.Error != MailboxError.None) throw new InvalidDataException("公会炼金产物邮件提交失败");
            command.CommandText = "UPDATE character_guild_npc_item_crafts SET state=1,mail_id=$mail WHERE craft_id=$key AND state=0";
            command.Parameters.AddWithValue("$mail", mailed.MailId);
            if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("公会炼金生产完成状态提交失败");
            GuildManagementCommit? notice = null;
            if (ReadManagement(db, tx, recipient.Account, characterId) is { } management && management.Guild.Id == guild)
            {
                TouchManagement(db, tx, guild);
                notice = new(0, guild, Members: management.Members) { NpcsChanged = true };
            }
            notices.Add(new(new(MailboxError.None, MailId: mailed.MailId) { Recipient = recipient }, notice));
        }
        tx.Commit(); return notices.ToArray();
    }
}
