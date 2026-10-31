using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Raids;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IAntonRaidLifecycleStore
{
    bool IAntonRaidLifecycleStore.IsRetryable(Exception error) => SqliteBusyRetry.IsBusy(error);
    // 复用666同事务签发的生命周期；阶段和通关次数独立于660领取，重复clear不重复计。
    public ImmutableArray<RaidPhaseEligibility> CommitAntonPhase(AntonRaidBattle.AntonPhaseSettlement candidate)
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitAntonPhase");
        using var tx = write.Transaction;
        using var command = db.CreateCommand(); command.Transaction = tx;
        string prefix = candidate.Phase == 0 ? "phase0" : "phase1";
        command.Parameters.AddWithValue("$instance", candidate.Instance.ToString("N"));
        command.Parameters.AddWithValue("$event", candidate.Event.ToString("N"));
        command.Parameters.AddWithValue("$anchor", candidate.Anchor.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$week", candidate.Week);
        command.Parameters.Add("$id", SqliteType.Integer); command.Parameters.Add("$account", SqliteType.Text);
        command.Parameters.Add("$receipt", SqliteType.Text); command.Parameters.Add("$state", SqliteType.Integer);
        command.Parameters.Add("$clear", SqliteType.Integer);
        var result = ImmutableArray.CreateBuilder<RaidPhaseEligibility>(candidate.Members.Length);
        foreach (var member in candidate.Members)
        {
            command.Parameters["$id"].Value = member.Character; command.Parameters["$account"].Value = member.Account;
            command.CommandText = $"SELECT state,source,{prefix}_receipt,phase0_receipt FROM character_raid_runs WHERE character_id=$id AND account=$account AND instance_id=$instance AND raid_type=0";
            int state;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || reader.GetString(1) != member.Policy.Source)
                    throw new InvalidDataException("安徒恩阶段缺少原666身份或来源已变化");
                state = reader.GetInt32(0);
                if (!reader.IsDBNull(2))
                {
                    var previous = JsonSerializer.Deserialize<RaidPhaseEligibility>(reader.GetString(2))
                        ?? throw new InvalidDataException("安徒恩阶段回执为空");
                    if (previous.Instance != candidate.Instance || previous.RaidType != RaidKind.Anton || previous.Phase != candidate.Phase ||
                        previous.Event != candidate.Event || previous.Anchor != candidate.Anchor.ToUnixTimeSeconds() ||
                        previous.Week != candidate.Week || previous.Character != member.Character || previous.Source != member.Policy.Source ||
                        candidate.TimedOut && (previous.Eligible || previous.CountsAsClear || previous.Reason != "phase-timeout") ||
                        !AntonRaidBattle.SameLukeOffers(previous.Offers, member.Policy.Offers))
                        throw new InvalidDataException("安徒恩阶段重放改变原事件或奖励");
                    result.Add(previous); continue;
                }
                if (candidate.Phase == 1 && reader.IsDBNull(3)) throw new InvalidDataException("安徒恩最终通关缺少阶段0回执");
            }
            bool eligible = !candidate.TimedOut && state == 0;
            if (eligible && candidate.Phase == 1)
            {
                command.CommandText = "SELECT count(*) FROM character_raid_runs WHERE character_id=$id AND account=$account AND raid_type=0 AND state=2 AND clear_week=$week AND instance_id<>$instance";
                eligible = Convert.ToInt32(command.ExecuteScalar()) < RaidRewardRules.WeeklyClearLimit;
            }
            bool counts = eligible && candidate.Phase == 1;
            var receipt = new RaidPhaseEligibility(candidate.Instance, RaidKind.Anton, candidate.Phase, candidate.Event,
                candidate.Anchor.ToUnixTimeSeconds(), member.Character, counts, eligible,
                candidate.TimedOut ? "phase-timeout" : eligible ? "eligible" : "admission-abandoned-or-weekly-limit", member.Policy.Source, candidate.Week, member.Policy.Offers);
            command.Parameters["$receipt"].Value = JsonSerializer.Serialize(receipt);
            command.Parameters["$state"].Value = candidate.TimedOut ? 1 : candidate.Phase == 1 ? counts ? 2 : 1 : state;
            command.Parameters["$clear"].Value = counts ? candidate.Week : DBNull.Value;
            command.CommandText = $"UPDATE character_raid_runs SET {prefix}_event=$event,{prefix}_anchor=$anchor,{prefix}_receipt=$receipt,state=$state" +
                (candidate.Phase == 1 ? ",clear_week=$clear" : "") +
                $" WHERE character_id=$id AND account=$account AND instance_id=$instance AND raid_type=0 AND {prefix}_event IS NULL";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("安徒恩阶段条件提交未命中原身份");
            result.Add(receipt);
        }
        if (candidate.Phase == 1 && !candidate.TimedOut)
            CommitRaidGuildContribution(db, tx, candidate.Instance, RaidKind.Anton, result,
                candidate.GuildParties, candidate.Anchor.ToUnixTimeSeconds());
        tx.Commit(); return result.MoveToImmutable();
    }
}
