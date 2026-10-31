using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        if (!Enum.IsDefined(prepared.RaidType) || prepared.Instance == Guid.Empty || prepared.Character != expected.Id || string.IsNullOrWhiteSpace(prepared.Source) ||
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
        command.Parameters.AddWithValue("$type", (byte)prepared.RaidType);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM character_raid_reward_receipts WHERE instance_id=$instance AND raid_type<>$type) OR EXISTS(SELECT 1 FROM character_raid_runs WHERE instance_id=$instance AND raid_type<>$type)";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0) throw new InvalidDataException("攻坚实例类型不可改变");
        command.CommandText = "SELECT plan_json,mail_ids,raid_type,account FROM character_raid_reward_receipts WHERE character_id=$id AND instance_id=$instance AND phase=$phase AND kind=$kind";
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetInt32(2) != (int)prepared.RaidType || reader.GetString(3) != owner.Account ||
                    !RaidRewardPlanMatches(reader.GetString(0), plan, prepared.RaidType))
                    throw new InvalidDataException("同次攻坚奖励的牌面或规则已变化");
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
        if (prepared.RaidType == RaidKind.Anton)
        {
            command.CommandText += " AND raid_type=0";
            if (Convert.ToInt32(command.ExecuteScalar()) >= RaidRewardRules.WeeklyClearLimit) return null;
            if (prepared.Source == AntonResourceRewards.Source)
            {
                if (prepared.Phase is not (0 or 1) || prepared.FinalPhase != (prepared.Phase == 1)) return null;
                string prefix = prepared.Phase == 0 ? "phase0" : "phase1";
                command.CommandText = $"SELECT {prefix}_receipt FROM character_raid_runs WHERE character_id=$id AND account=$account AND instance_id=$instance AND raid_type=0";
                if (command.ExecuteScalar() is not string json || JsonSerializer.Deserialize<RaidPhaseEligibility>(json) is not { Eligible: true } eligibility ||
                    eligibility.Instance != prepared.Instance || eligibility.Character != expected.Id || eligibility.Phase != prepared.Phase ||
                    eligibility.RaidType != RaidKind.Anton || eligibility.Week != prepared.Week || eligibility.Source != prepared.Source ||
                    eligibility.Offers.SingleOrDefault(offer => offer.Kind == prepared.Kind && offer.Card == prepared.Card) is not { } authorized ||
                    !authorized.Grants.SequenceEqual(prepared.Grants)) return null;
            }
        }
        else
        {
            // 仅Luke新发奖要求666签发的两阶段资格；旧Anton回执已在上方优先重放。
            if (prepared.Phase is not (0 or 1) || prepared.FinalPhase != (prepared.Phase == 1)) return null;
            string prefix = prepared.Phase == 0 ? "phase0" : "phase1";
            command.CommandText = $"SELECT {prefix}_receipt,{prefix}_event,{prefix}_anchor FROM character_raid_runs WHERE character_id=$id AND account=$account AND instance_id=$instance AND raid_type=$type";
            using var eligibilityRow = command.ExecuteReader();
            if (!eligibilityRow.Read() || eligibilityRow.IsDBNull(0) ||
                JsonSerializer.Deserialize<RaidPhaseEligibility>(eligibilityRow.GetString(0)) is not { Eligible: true } eligibility ||
                eligibility.Character != expected.Id || eligibility.Week != prepared.Week || eligibility.Source != prepared.Source ||
                eligibility.Instance != prepared.Instance || eligibility.RaidType != prepared.RaidType || eligibility.Phase != prepared.Phase ||
                eligibility.Event == Guid.Empty || eligibility.Event.ToString("N") != eligibilityRow.GetString(1) ||
                eligibility.Anchor != eligibilityRow.GetInt64(2) || eligibility.Offers.IsDefault ||
                eligibility.Offers.SingleOrDefault(offer => offer.Kind == prepared.Kind && offer.Card == prepared.Card) is not { } authorized ||
                !authorized.Grants.SequenceEqual(prepared.Grants)) return null;
        }
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
            INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type)
            VALUES($id,$account,$instance,$phase,$kind,$card,$week,$final,$plan,$mails,$now,$type)
            """;
        command.Parameters.AddWithValue("$card", prepared.Card); command.Parameters.AddWithValue("$final", prepared.FinalPhase ? 1 : 0);
        command.Parameters.AddWithValue("$plan", plan); command.Parameters.AddWithValue("$mails", JsonSerializer.Serialize(mailIds.ToImmutable()));
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds()); command.ExecuteNonQuery();
        tx.Commit(); return new RaidRewardCommit(current, false, mailIds.ToImmutable());
    });
    static bool RaidRewardPlanMatches(string stored, string expected, RaidKind kind)
    {
        var actual = JsonNode.Parse(stored) as JsonObject;
        var wanted = JsonNode.Parse(expected) as JsonObject;
        if (actual is null || wanted is null) return false;
        // 只规范化已证旧Anton缺失的类型；其余字段（包括未知字段）必须完整相等。
        if (!actual.ContainsKey(nameof(PreparedRaidReward.RaidType)))
        {
            if (kind != RaidKind.Anton) return false;
            actual[nameof(PreparedRaidReward.RaidType)] = 0;
        }
        return JsonNode.DeepEquals(actual, wanted);
    }
}
