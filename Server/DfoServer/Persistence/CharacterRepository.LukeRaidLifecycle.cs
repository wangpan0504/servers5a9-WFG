using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Raids;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : ILukeRaidLifecycleStore
{
    // 调用方不持Account/Battle/Party领域gate；仅操作666原事务签发的身份行，不读写Selected或资产。
    bool ILukeRaidLifecycleStore.IsRetryable(Exception error) => SqliteBusyRetry.IsBusy(error);

    public LukePhaseCommit CommitLukePhase(AntonRaidBattle.LukePhaseSettlement candidate)
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitLukePhase");
        using var tx = write.Transaction;
        using var command = db.CreateCommand(); command.Transaction = tx;
        string instance = candidate.Instance.ToString("N"), prefix = candidate.Phase == 0 ? "phase0" : "phase1";
        command.Parameters.AddWithValue("$instance", instance);
        command.Parameters.AddWithValue("$type", (byte)candidate.RaidType);
        command.Parameters.AddWithValue("$event", candidate.Event.ToString("N"));
        command.Parameters.AddWithValue("$anchor", candidate.Anchor.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$week", candidate.Week);
        command.Parameters.Add("$id", SqliteType.Integer); command.Parameters.Add("$account", SqliteType.Text);
        command.Parameters.Add("$receipt", SqliteType.Text); command.Parameters.Add("$state", SqliteType.Integer);
        command.Parameters.Add("$clear", SqliteType.Integer);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM character_raid_reward_receipts WHERE instance_id=$instance AND raid_type<>$type)";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0) throw new InvalidDataException("卢克终结不能覆盖其它类型实例");
        var result = ImmutableArray.CreateBuilder<RaidPhaseEligibility>(candidate.Members.Length);
        foreach (var member in candidate.Members)
        {
            var frozenRewards = candidate.RewardsFor(member.Character);
            command.Parameters["$id"].Value = member.Character; command.Parameters["$account"].Value = member.Account;
            command.CommandText = $"SELECT state,source,{prefix}_event,{prefix}_anchor,{prefix}_receipt FROM character_raid_runs WHERE character_id=$id AND account=$account AND instance_id=$instance AND raid_type=$type";
            int state = -1; string? source = null;
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    state = reader.GetInt32(0); source = reader.GetString(1);
                    if (!reader.IsDBNull(4))
                    {
                        var previous = JsonSerializer.Deserialize<RaidPhaseEligibility>(reader.GetString(4))
                            ?? throw new InvalidDataException("卢克已提交阶段回执为空");
                        if (source != candidate.Source || reader.GetString(2) != candidate.Event.ToString("N") || reader.GetInt64(3) != candidate.Anchor.ToUnixTimeSeconds() ||
                            previous.Instance != candidate.Instance || previous.RaidType != candidate.RaidType || previous.Phase != candidate.Phase ||
                            previous.Event != candidate.Event || previous.Anchor != candidate.Anchor.ToUnixTimeSeconds() ||
                            previous.Character != member.Character || previous.Week != candidate.Week || previous.Source != frozenRewards.Source ||
                            !AntonRaidBattle.SameLukeOffers(previous.Offers, frozenRewards.Offers))
                            throw new InvalidDataException("卢克阶段终结事件或来源发生变化");
                        result.Add(previous); continue;
                    }
                }
            }
            bool present = state == 0 && member.Present && source == candidate.Source;
            bool counts = present && candidate.FinalPhase;
            string reason = state < 0 ? "admission-missing" : source != candidate.Source ? "admission-source-changed" :
                !present ? "admission-abandoned" : "eligible";
            if (counts)
            {
                command.CommandText = "SELECT count(*) FROM character_raid_runs WHERE character_id=$id AND account=$account AND raid_type=$type AND state=2 AND clear_week=$week AND instance_id<>$instance";
                if (Convert.ToInt32(command.ExecuteScalar()) >= candidate.WeeklyClearLimit)
                { counts = false; present = false; reason = "weekly-clear-limit"; }
            }
            bool eligible = present;
            if (eligible && member.CommittedRuns < candidate.Policy.MinimumCommittedRuns)
            { eligible = false; reason = "phase-participation"; }
            if (eligible)
            {
                command.CommandText = $"SELECT count(*) FROM character_raid_runs WHERE character_id=$id AND account=$account AND raid_type=$type AND instance_id<>$instance AND json_extract({prefix}_receipt,'$.Eligible')=1 AND json_extract({prefix}_receipt,'$.Week')=$week";
                if (Convert.ToInt32(command.ExecuteScalar()) >= candidate.Policy.WeeklyEligibilityLimit)
                { eligible = false; reason = "phase-eligibility-limit"; }
            }
            var receipt = new RaidPhaseEligibility(candidate.Instance, candidate.RaidType, candidate.Phase, candidate.Event,
                candidate.Anchor.ToUnixTimeSeconds(), member.Character, counts, eligible, reason, frozenRewards.Source, candidate.Week, frozenRewards.Offers);
            result.Add(receipt);
            // 缺行不能补造；删角后Import同id仍无原instance能力。已放弃行只记拒绝结果，不重新激活。
            if (state < 0) continue;
            command.Parameters["$receipt"].Value = JsonSerializer.Serialize(receipt);
            command.Parameters["$state"].Value = candidate.FinalPhase ? counts ? 2 : 1 : state;
            command.Parameters["$clear"].Value = counts ? candidate.Week : DBNull.Value;
            command.CommandText = $"UPDATE character_raid_runs SET {prefix}_event=$event,{prefix}_anchor=$anchor,{prefix}_receipt=$receipt,state=$state" +
                (candidate.FinalPhase ? ",clear_week=$clear" : "") +
                $" WHERE character_id=$id AND account=$account AND instance_id=$instance AND raid_type=$type AND {prefix}_event IS NULL";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("卢克阶段回执条件提交未命中原身份");
        }
        tx.Commit();
        return new(result.ToImmutable());
    }
}
