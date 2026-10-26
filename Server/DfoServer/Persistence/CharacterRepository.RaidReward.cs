using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Raids;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IRaidRewardStore
{
    // 与普通翻牌共用库存/UID/满包邮件提交；回执与资产在同一writer事务中，不提前ACK。
    public RaidRewardCommit? CommitRaidReward(AccountOwners.Lease owner, Character expected, PreparedRaidReward prepared,
        InventoryPlan inventory, DateTimeOffset now) => owner.Execute(() =>
    {
        if (prepared.Instance == Guid.Empty || prepared.Character != expected.Id || string.IsNullOrWhiteSpace(prepared.Source) ||
            inventory.Reason != "raid_reward" || inventory.Before != expected.Inventory)
            throw new InvalidDataException("攻坚奖励身份或规划不匹配");
        string instance = prepared.Instance.ToString("N"), plan = JsonSerializer.Serialize(prepared);
        ValidateInventoryPlan(expected, inventory);
        int week = prepared.Week;
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitRaidReward");
        using var tx = write.Transaction;
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id); command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$instance", instance); command.Parameters.AddWithValue("$phase", prepared.Phase);
        command.Parameters.AddWithValue("$kind", prepared.Kind);
        command.CommandText = "SELECT plan_json,mail_ids FROM character_raid_reward_receipts WHERE character_id=$id AND instance_id=$instance AND phase=$phase AND kind=$kind";
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != plan) throw new InvalidDataException("同次攻坚奖励的牌面或规则已变化");
                var mails = JsonSerializer.Deserialize<ImmutableArray<uint>>(reader.GetString(1));
                reader.Close();
                var replay = Require(db, tx, owner.Account, expected.Name);
                tx.Commit(); return new RaidRewardCommit(replay, true, mails);
            }
        }
        command.Parameters.AddWithValue("$week", week);
        // 产品规格KB-20261026T064659-c2fb533dc5：每角色每周2次，周四06:00。
        // 用完最终奖励次数后，另一实例的第一阶段也不能继续领奖；同一实例的后续牌种不再占一次。
        command.CommandText = "SELECT COUNT(DISTINCT instance_id) FROM character_raid_reward_receipts WHERE character_id=$id AND week_key=$week AND final_phase=1 AND instance_id<>$instance";
        if (Convert.ToInt32(command.ExecuteScalar()) >= RaidRewardRules.WeeklyClearLimit) return null;
        var current = CommitInventoryInTransaction(db, tx, owner, expected, inventory);
        // WriteOverflowMail由库存writer调用；只读取本次已投递邮件的稳定键，不另建投递事务。
        var mailIds = ImmutableArray.CreateBuilder<uint>();
        for (int offset = 0; offset < inventory.OverflowItems.Length; offset += 10)
        {
            command.CommandText = "SELECT mail_id FROM mailbox_messages WHERE delivery_key=$key";
            if (command.Parameters.Contains("$key")) command.Parameters.RemoveAt("$key");
            command.Parameters.AddWithValue("$key", $"inventory-overflow:{expected.Id}:{expected.Revision + 1}:{offset / 10}");
            mailIds.Add(checked((uint)Convert.ToInt64(command.ExecuteScalar())));
        }
        command.CommandText = """
            INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at)
            VALUES($id,$account,$instance,$phase,$kind,$card,$week,$final,$plan,$mails,$now)
            """;
        command.Parameters.AddWithValue("$card", prepared.Card); command.Parameters.AddWithValue("$final", prepared.FinalPhase ? 1 : 0);
        command.Parameters.AddWithValue("$plan", plan); command.Parameters.AddWithValue("$mails", JsonSerializer.Serialize(mailIds.ToImmutable()));
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds()); command.ExecuteNonQuery();
        tx.Commit(); return new RaidRewardCommit(current, false, mailIds.ToImmutable());
    });
}
