using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static uint? ReadGuildDungeonReward(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        PreparedDungeonSettlement prepared)
    {
        using var query = ManagementCommand(db, tx, """
            SELECT account,event_id,dungeon_id,reward_json,mail_id FROM guild_dungeon_reward_receipts
            WHERE character_id=$id AND instance_id=$instance
            """, ("$id", prepared.Fact.Identity.CharacterId), ("$instance", prepared.Fact.Identity.InstanceId.ToString("N")));
        using var row = query.ExecuteReader();
        if (!row.Read()) return null;
        var grant = JsonSerializer.Deserialize<GuildDungeonRewardGrant>(row.GetString(3));
        var expected = grant is null ? null : prepared.GuildDungeonReward?.Select(grant.Ratio);
        // v1回执只有基础奖，升级后重放仍返回原邮件，不追补或改变已提交产物。
        if (grant?.Version == 1 && grant.RepeatCore is null && expected is not null)
            expected = expected with { RepeatCore = null, Version = 1 };
        if (row.GetString(0) != owner.Account || row.GetString(1) != prepared.EventId.ToString("N") ||
            row.GetInt64(2) != prepared.Fact.DungeonId || grant is null ||
            expected != grant)
            throw new InvalidDataException("公会副本已发奖励与原冻结结算不一致");
        return checked((uint)row.GetInt64(4));
    }

    (uint? Mail, int Contribution) CommitGuildDungeonReward(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, Character expected, PreparedDungeonSettlement prepared, long now)
    {
        if (prepared.GuildDungeonCatalog?.Find(prepared.Fact.DungeonId) is not { } definition) return (null, 0);
        var frozen = prepared.GuildMemberships.SingleOrDefault(row => row.CharacterId == expected.Id);
        if (frozen is null || frozen.GuildId == 0 || ReadDungeonGuildMembership(db, tx, expected.Id) != frozen) return (null, 0);
        var reward = prepared.GuildDungeonReward;
        if (reward is null || reward.DungeonId != definition.Id || reward.Created <= 0 ||
            reward.RepeatCore is not { IsStackable: true } repeat || repeat.ItemId != prepared.GuildDungeonCatalog.RepeatRewardItem ||
            repeat.Count != prepared.GuildDungeonCatalog.RepeatRewardAmount || repeat.IsExpired(reward.Created) ||
            !reward.Choices.Select(row => row.Definition).SequenceEqual(definition.Rewards) ||
            reward.Choices.Any(row => !row.Core.IsStackable || row.Core.ItemId != row.Definition.ItemId ||
                row.Core.Count != row.Definition.Amount || row.Core.IsExpired(reward.Created)))
            throw new InvalidDataException("公会副本奖励缺少匹配资源的冻结完整物品核心");
        string instance = prepared.Fact.Identity.InstanceId.ToString("N");
        using var command = ManagementCommand(db, tx, """
            SELECT json_extract(reward_json,'$.Ratio') FROM guild_dungeon_reward_receipts
            WHERE guild_id=$guild AND instance_id=$instance AND dungeon_id=$dungeon ORDER BY character_id LIMIT 1
            """, ("$guild", frozen.GuildId), ("$instance", instance), ("$dungeon", prepared.Fact.DungeonId));
        // 同会同实例的成员共享首次结算前比例；第二人不能使用首人已经增加的净化值。
        int ratio = command.ExecuteScalar() is long first ? checked((int)first) : definition.Purification(
            ReadGuildDungeonProgress(db, tx, frozen.GuildId).Counts.FirstOrDefault(row => row.DungeonId == definition.Id)?
                .Effective(prepared.GuildDungeonCatalog, now).Units ?? 0);
        var grant = reward.Select(ratio);
        var current = Require(db, tx, owner.Account, expected.Name);
        var recipient = new MailboxRecipient(expected.Id, owner.Account, expected.Name, expected.Job, expected.GrowType, current.Level);
        var attachments = new List<InventoryItem> { new(0, new(InventoryListType.Main, 9), grant.Choice.Core) };
        if (grant.RepeatCore is { } bonus) attachments.Add(new(0, new(InventoryListType.Main, 10), bonus));
        var delivery = new MailboxDelivery("guild-dungeon:" + prepared.EventId.ToString("N"), recipient,
            "公会副本奖励", "真实通关奖励已通过永久邮件保存。", 0,
            [.. attachments], grant.Created);
        var (error, mail) = DeliverMailInTransaction(db, tx, delivery);
        if (error != MailboxError.None || mail == 0) throw new InvalidOperationException("公会副本奖励邮件失败: " + error);
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$event", prepared.EventId.ToString("N"));
        command.Parameters.AddWithValue("$reward", JsonSerializer.Serialize(grant));
        command.Parameters.AddWithValue("$mail", mail);
        command.CommandText = """
            INSERT INTO guild_dungeon_reward_receipts(character_id,account,guild_id,instance_id,event_id,dungeon_id,reward_json,mail_id)
            VALUES($id,$account,$guild,$instance,$event,$dungeon,$reward,$mail)
            """;
        command.ExecuteNonQuery();
        return (mail, grant.Choice.Definition.Contribution);
    }
}
