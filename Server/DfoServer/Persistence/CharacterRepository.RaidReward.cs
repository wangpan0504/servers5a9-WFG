using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Raids;
using Microsoft.Data.Sqlite;

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
                string storedPlan = reader.GetString(0);
                if (reader.GetInt32(2) != (int)prepared.RaidType || reader.GetString(3) != owner.Account ||
                    !RaidRewardPlanMatches(storedPlan, plan, prepared.RaidType))
                    throw new InvalidDataException("同次攻坚奖励的牌面或规则已变化");
                var mails = JsonSerializer.Deserialize<ImmutableArray<uint>>(reader.GetString(1));
                reader.Close();
                var replay = Require(db, tx, owner.Account, expected.Name);
                var settled = JsonSerializer.Deserialize<PreparedRaidReward>(storedPlan);
                var grants = settled is not null && !settled.Grants.IsDefault && !settled.Grants.SequenceEqual(prepared.Grants)
                    ? settled.Grants : default;
                tx.Commit(); return new RaidRewardCommit(replay, true, mails) { SettledGrants = grants };
            }
        }
        command.Parameters.AddWithValue("$week", week);
        var runQuota = ReadRaidRunQuotaEpochs(db, tx, owner.Account, expected.Id, prepared.Instance, prepared.RaidType);
        if (runQuota is null && (prepared.RaidType != RaidKind.Anton || AntonResourceRewards.IsFormalSource(prepared.Source))) return null;
        long rewardEpoch = runQuota?.Reward ?? ReadRaidQuotaEpochs(db, tx, owner.Account, prepared.RaidType).Reward;
        command.Parameters.AddWithValue("$rewardEpoch", rewardEpoch);
        // 产品规格KB-20261026T064659-c2fb533dc5：每角色每周2次，周四06:00。
        // 用完最终奖励次数后，另一实例的第一阶段也不能继续领奖；同一实例的后续牌种不再占一次。
        command.CommandText = "SELECT COUNT(DISTINCT instance_id) FROM character_raid_reward_receipts WHERE character_id=$id AND week_key=$week AND reward_epoch=$rewardEpoch AND final_phase=1 AND instance_id<>$instance";
        if (prepared.RaidType == RaidKind.Anton)
        {
            command.CommandText += " AND raid_type=0";
            if (Convert.ToInt32(command.ExecuteScalar()) >= RaidRewardRules.WeeklyClearLimit) return null;
            if (AntonResourceRewards.IsFormalSource(prepared.Source))
            {
                if (prepared.Phase is not (0 or 1) || prepared.FinalPhase != (prepared.Phase == 1)) return null;
                string prefix = prepared.Phase == 0 ? "phase0" : "phase1";
                command.CommandText = $"SELECT {prefix}_receipt FROM character_raid_runs WHERE character_id=$id AND account=$account AND instance_id=$instance AND raid_type=0";
                if (command.ExecuteScalar() is not string json || JsonSerializer.Deserialize<RaidPhaseEligibility>(json) is not { Eligible: true } eligibility ||
                    eligibility.Instance != prepared.Instance || eligibility.Character != expected.Id || eligibility.Phase != prepared.Phase ||
                    eligibility.RaidType != RaidKind.Anton || eligibility.Week != prepared.Week || eligibility.Source != prepared.Source ||
                    prepared.Card > 3 ||
                    eligibility.Offers.SingleOrDefault(offer => offer.Kind == prepared.Kind && offer.Card == 0) is not { } authorized ||
                    !authorized.Grants.SequenceEqual(prepared.Grants)) return null;
            }
        }
        else
        {
            // 仅Luke新发奖要求666签发的两阶段资格；旧Anton回执已在上方优先重放。
            if (prepared.RaidType == RaidKind.Fiendwar ? prepared.Phase != 0 || !prepared.FinalPhase :
                prepared.Phase is not (0 or 1) || prepared.FinalPhase != (prepared.Phase == 1)) return null;
            string prefix = prepared.Phase == 0 ? "phase0" : "phase1";
            command.CommandText = $"SELECT {prefix}_receipt,{prefix}_event,{prefix}_anchor FROM character_raid_runs WHERE character_id=$id AND account=$account AND instance_id=$instance AND raid_type=$type";
            using var eligibilityRow = command.ExecuteReader();
            if (!eligibilityRow.Read() || eligibilityRow.IsDBNull(0) ||
                JsonSerializer.Deserialize<RaidPhaseEligibility>(eligibilityRow.GetString(0)) is not { Eligible: true } eligibility ||
                eligibility.Character != expected.Id || eligibility.Week != prepared.Week || eligibility.Source != prepared.Source ||
                eligibility.Instance != prepared.Instance || eligibility.RaidType != prepared.RaidType || eligibility.Phase != prepared.Phase ||
                eligibility.Event == Guid.Empty || eligibility.Event.ToString("N") != eligibilityRow.GetString(1) ||
                eligibility.Anchor != eligibilityRow.GetInt64(2) || eligibility.Offers.IsDefault ||
                (prepared.RaidType == RaidKind.Luke && LukeResourceRewards.IsFormalSource(prepared.Source) || prepared.RaidType == RaidKind.Fiendwar) && prepared.Card > 3 ||
                eligibility.Offers.SingleOrDefault(offer => offer.Kind == prepared.Kind && offer.Card ==
                (((prepared.RaidType == RaidKind.Luke && LukeResourceRewards.IsFormalSource(prepared.Source)) || prepared.RaidType == RaidKind.Fiendwar) ? 0 : prepared.Card)) is not { } authorized ||
                !authorized.Grants.SequenceEqual(prepared.Grants)) return null;
        }
        if (inventory.AccountMaterialRaidWeek is { } materialWeek &&
            materialWeek != DfoServer.Game.Inventory.Services.AccountMaterialService.RaidWeek(now))
            return null;
        var settledPrepared = prepared;
        var settledPlan = inventory;
        bool songAdjusted = false;
        if (prepared.RaidType is RaidKind.Luke or RaidKind.Prey or RaidKind.Fiendwar &&
            inventory.AccountMaterialRaidDefinitions.TryGetValue(RaidSongQuota.ItemId, out var songLimit) &&
            prepared.Grants.Any(row => row.ItemId == RaidSongQuota.ItemId && row.Count > 0))
        {
            var materials = ReadAccountMaterials(db, owner.Account, tx);
            var dbBalance = materials.TryGetValue(RaidSongQuota.ItemId, out var found) ? found : AccountMaterialBalance.Empty;
            int weekNow = DfoServer.Game.Inventory.Services.AccountMaterialService.RaidWeek(now);
            var settledGrants = RaidSongQuota.ClipGrantList(prepared.Grants, dbBalance, expected.Id, songLimit, weekNow, out var credited);
            bool grantsChanged = !settledGrants.SequenceEqual(prepared.Grants);
            bool balanceChanged = !credited.Same(inventory.After.AccountWallet.Material(RaidSongQuota.ItemId));
            if (grantsChanged || balanceChanged)
            {
                songAdjusted = true;
                if (balanceChanged)
                    settledPlan = inventory.WithAfter(inventory.After with
                    {
                        AccountWallet = inventory.After.AccountWallet.SetMaterial(RaidSongQuota.ItemId, credited)
                    });
                if (grantsChanged)
                {
                    settledPrepared = prepared with { Grants = settledGrants };
                    plan = JsonSerializer.Serialize(settledPrepared);
                    UpdateRaidSongEligibility(db, tx, owner.Account, expected.Id, settledPrepared);
                }
            }
        }
        var current = CommitInventoryInTransaction(db, tx, owner, expected, settledPlan);
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
            INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type,reward_epoch)
            VALUES($id,$account,$instance,$phase,$kind,$card,$week,$final,$plan,$mails,$now,$type,$rewardEpoch)
            """;
        command.Parameters.AddWithValue("$card", prepared.Card); command.Parameters.AddWithValue("$final", prepared.FinalPhase ? 1 : 0);
        command.Parameters.AddWithValue("$plan", plan); command.Parameters.AddWithValue("$mails", JsonSerializer.Serialize(mailIds.ToImmutable()));
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds()); command.ExecuteNonQuery();
        // 原资格、库存与奖励回执同事务内，只按本笔真实入包材料推进任务。
        ApplyBattlePassUndefeatedSong(db, tx, owner.Account, settledPrepared, settledPlan, now,
            songAdjusted ? RaidSongQuota.PositiveAmount(settledPrepared.Grants) : null);
        tx.Commit(); return new RaidRewardCommit(current, false, mailIds.ToImmutable())
        {
            SettledGrants = songAdjusted && !settledPrepared.Grants.SequenceEqual(prepared.Grants) ? settledPrepared.Grants : default
        };
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
        if (JsonNode.DeepEquals(actual, wanted)) return true;
        return RaidSongQuota.IsSongCapSettlement(stored, expected);
    }

    static void UpdateRaidSongEligibility(SqliteConnection db, SqliteTransaction tx, string account, ushort character, PreparedRaidReward prepared)
    {
        string prefix = prepared.Phase == 0 ? "phase0" : "phase1";
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", character);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$instance", prepared.Instance.ToString("N"));
        command.Parameters.AddWithValue("$type", (byte)prepared.RaidType);
        command.CommandText = $"SELECT {prefix}_receipt FROM character_raid_runs WHERE character_id=$id AND account=$account AND instance_id=$instance AND raid_type=$type";
        if (command.ExecuteScalar() is not string json)
            throw new InvalidDataException("不渝之咏额度结算缺少原阶段资格");
        var eligibility = JsonSerializer.Deserialize<RaidPhaseEligibility>(json)
            ?? throw new InvalidDataException("不渝之咏额度结算资格为空");
        byte card = prepared.RaidType == RaidKind.Fiendwar ||
            prepared.RaidType == RaidKind.Luke && LukeResourceRewards.IsFormalSource(prepared.Source)
            ? (byte)0 : prepared.Card;
        bool found = false;
        var offers = eligibility.Offers.Select(offer =>
        {
            if (offer.Kind != prepared.Kind || offer.Card != card) return offer;
            found = true;
            return RaidSongQuota.ApplyGrants(offer, prepared.Grants);
        }).ToImmutableArray();
        if (!found) throw new InvalidDataException("不渝之咏额度结算未命中原牌面");
        command.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(eligibility with { Offers = offers }));
        command.CommandText = $"UPDATE character_raid_runs SET {prefix}_receipt=$receipt WHERE character_id=$id AND account=$account AND instance_id=$instance AND raid_type=$type";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("不渝之咏额度结算未写回原阶段资格");
    }
}
