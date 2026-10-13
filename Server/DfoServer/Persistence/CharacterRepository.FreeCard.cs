using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public DungeonFreeCardCommit CommitFreeDungeonCard(AccountOwners.Lease owner, Character expected,
        PreparedDungeonSettlement prepared, DungeonCardDelivery delivery) => owner.Execute(() =>
    {
        var frozen = prepared.Card ?? throw new InvalidDataException("本实例没有免费牌计划");
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || prepared.Fact.Identity.CharacterId != expected.Id ||
            current.DungeonState?.LastAttempt != prepared.Fact.Identity.TaskAttempt)
            throw new InvalidOperationException("免费牌账号、角色或实例前提已变化");
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$id", expected.Id);
        query.Parameters.AddWithValue("$instance", prepared.Fact.Identity.InstanceId.ToString("N"));
        DungeonCardReward? actual = null;
        uint? mail = null;
        query.CommandText = "SELECT account,plan_json,reward_json,mail_id,claimed FROM character_dungeon_card_rewards WHERE character_id=$id AND instance_id=$instance";
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != owner.Account || reader.GetString(1) != JsonSerializer.Serialize(frozen))
                throw new InvalidDataException("免费牌冻结计划缺失或变化");
            if (reader.GetInt64(4) == 1)
            {
                actual = JsonSerializer.Deserialize<DungeonCardReward>(reader.GetString(2)) ?? throw new InvalidDataException("免费牌回执为空");
                VerifyActualCard(actual, frozen);
                mail = reader.IsDBNull(3) ? null : checked((uint)reader.GetInt64(3));
            }
        }
        DungeonCardReward? bonus = null;
        if (prepared.Options is { } options)
        {
            query.CommandText = "SELECT account,options_json,black_diamond_reward_json FROM character_dungeon_card_options WHERE character_id=$id AND instance_id=$instance";
            using var reader = query.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != owner.Account || reader.GetString(1) != JsonSerializer.Serialize(options))
                throw new InvalidDataException("会员冻结资格缺失或变化");
            if (!reader.IsDBNull(2))
            {
                bonus = JsonSerializer.Deserialize<DungeonCardReward>(reader.GetString(2)) ?? throw new InvalidDataException("黑钻回执为空");
                VerifyActualCard(bonus, options.BlackDiamond ?? throw new InvalidDataException("没有黑钻冻结资格"));
            }
        }
        if (actual is not null)
        {
            if ((bonus is not null) != (prepared.Options?.BlackDiamond is not null))
                throw new InvalidDataException("已领取免费牌缺少完整会员回执");
            tx.Commit();
            return new DungeonFreeCardCommit(current, actual, bonus,
                new(expected.Inventory, current.Inventory, "dungeon_card_reward"), mail, true);
        }
        if (current.Revision != expected.Revision || bonus is not null)
            throw new InvalidOperationException("免费牌revision或领取状态已变化");
        DungeonCardReward[] requested = prepared.Options?.BlackDiamond is { } black ? [frozen, black] : [frozen];
        ValidateCardDelivery(delivery, requested, 0);
        current = CommitInventoryInTransaction(db, tx, owner, current, delivery.Inventory);
        mail = DeliverCardItems(db, tx, owner, current, delivery.MailItems, prepared, "free");
        actual = delivery.Rewards[0];
        bonus = delivery.Rewards.Length == 2 ? delivery.Rewards[1] : null;
        query.Parameters.AddWithValue("$reward", JsonSerializer.Serialize(actual));
        query.Parameters.AddWithValue("$mail", (object?)mail ?? DBNull.Value);
        query.Parameters.AddWithValue("$revision", current.Revision);
        query.CommandText = "UPDATE character_dungeon_card_rewards SET reward_json=$reward,mail_id=$mail,revision=$revision,claimed=1 WHERE character_id=$id AND instance_id=$instance AND claimed=0";
        if (query.ExecuteNonQuery() != 1) throw new InvalidOperationException("免费牌资格已被消费");
        if (prepared.Options is not null)
        {
            query.Parameters.AddWithValue("$bonus", bonus is null ? DBNull.Value : (object)JsonSerializer.Serialize(bonus));
            query.CommandText = "UPDATE character_dungeon_card_options SET black_diamond_reward_json=$bonus,bonus_mail_id=$mail WHERE character_id=$id AND instance_id=$instance AND black_diamond_reward_json IS NULL";
            if (query.ExecuteNonQuery() != 1) throw new InvalidOperationException("免费牌会员回执已变化");
        }
        tx.Commit();
        return new DungeonFreeCardCommit(current, actual, bonus,
            new(expected.Inventory, current.Inventory, "dungeon_card_reward"), mail);
    });
}
