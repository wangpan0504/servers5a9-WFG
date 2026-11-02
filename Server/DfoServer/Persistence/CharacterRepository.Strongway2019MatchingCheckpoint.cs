using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Strongway2019;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    sealed record Strongway2019MatchingFact(Strongway2019StoredCompletion Completion, string StatisticsJson, long? Elapsed);

    // 与魔界共享完成检查点同责：当前报告者保存原比赛权威事实，不代旧lease修改任何角色快照。
    public void FreezeStrongway2019MatchingCheckpoint(AccountOwners.Lease reporter, Strongway2019Competition competition,
        IReadOnlyList<(string Account, ushort Character)> members, InventoryItem item) => reporter.Execute(() =>
    {
        var terminal = competition.Snapshot.Terminal;
        if (terminal is not { Reason: Strongway2019FinishReason.Clear or Strongway2019FinishReason.Exit, WinnerSide: not null } ||
            terminal.Created < 0 || !Strongway2019EntryPolicy.AllowsMemberCount(terminal.FrozenRewardParticipants.Length) ||
            terminal.FrozenRewardParticipants.Length != competition.Participants.Count(row => row.Side == terminal.WinnerSide) ||
            members.Count != competition.Participants.Length ||
            members.Select(row => row.Account).Distinct(StringComparer.Ordinal).Count() != members.Count ||
            members.Select(row => row.Character).Distinct().Count() != members.Count ||
            !members.Any(row => row.Account == reporter.Account) ||
            competition.Participants.Any(member => members.Count(row => row.Character == member.Identity.CharacterId) != 1) ||
            item.Uid != 0 || item.Core.ItemId != 490012737 || !item.Core.IsStackable || item.Core.Count != 1 || item.IsExpired(terminal.Created))
            throw new InvalidDataException("匹配共享检查点缺少原完整终局、成员或附件");
        item.ValidateDetails(allocating: true);
        var period = Strongway2019RewardPeriod.At(DateTimeOffset.FromUnixTimeSeconds(terminal.Created));
        string rewardJson = JsonSerializer.Serialize(new { Item = item });
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.FreezeStrongway2019MatchingCheckpoint");
        using var tx = write.Transaction;
        foreach (var frozen in terminal.FrozenRewardParticipants)
        {
            var participant = frozen.Participant; var identity = frozen.Identity; var fact = frozen.Fact;
            if (!terminal.RewardParticipants.Any(row => ReferenceEquals(row, participant)) ||
                participant.Side != terminal.WinnerSide || !SameStrongway2019Attempt(participant.Identity, identity))
                throw new InvalidDataException("匹配检查点收件事实不是原终局签发成员");
            string account = members.Single(row => row.Character == identity.CharacterId).Account;
            // 败方可在重试前删角；原比赛已签发的全部胜者事实不再依赖败方角色行。
            RequireStrongway2019StatisticsOwner(db, tx, account, identity.CharacterId);
            string factJson = JsonSerializer.Serialize(new { Identity = identity, Fact = fact, Competition = (Guid?)competition.Id,
                Reason = (Strongway2019FinishReason?)terminal.Reason, WinnerSide = terminal.WinnerSide,
                TerminalFact = terminal.ClearFact, Member = participant.Identity, Side = (int?)participant.Side });
            long? elapsed = terminal.Reason == Strongway2019FinishReason.Clear ? terminal.CombatElapsedMilliseconds : null;
            string statisticsJson = JsonSerializer.Serialize(new { Identity = identity, Fact = fact, Competition = (Guid?)competition.Id,
                Reason = (Strongway2019FinishReason?)terminal.Reason, WinnerSide = terminal.WinnerSide,
                TerminalFact = terminal.ClearFact, Member = participant.Identity, Side = (int?)participant.Side, Elapsed = elapsed });
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
            command.CommandText = "SELECT snapshot_json FROM character_strongway2019_match_facts WHERE account=$account AND instance_id=$instance";
            if (command.ExecuteScalar() is string existing)
            {
                var saved = JsonSerializer.Deserialize<Strongway2019MatchingFact>(existing) ?? throw new InvalidDataException("匹配检查点损坏");
                var c = saved.Completion;
                if (c.Character != identity.CharacterId || c.Attempt != identity.TaskAttempt || c.Epoch != identity.Epoch ||
                    c.Created != terminal.Created || c.RewardJson != rewardJson || c.FactJson != factJson ||
                    saved.Elapsed != elapsed || saved.StatisticsJson != statisticsJson)
                    throw new InvalidDataException("匹配原共享终局快照发生变化");
                continue;
            }
            var quota = ReadStrongway2019RewardState(db, tx, account, period, Strongway2019RewardBucket.Matching);
            int state = quota.DailyUsed >= 1 || quota.WeeklyUsed >= 2 ? 2 : 0;
            var completion = new Strongway2019StoredCompletion(account, identity.InstanceId, identity.CharacterId,
                identity.TaskAttempt, identity.Epoch, 5360, Strongway2019RewardBucket.Matching, period,
                terminal.Created, rewardJson, factJson, state, null);
            command.Parameters.AddWithValue("$character", identity.CharacterId); command.Parameters.AddWithValue("$competition", competition.Id.ToString("N"));
            command.Parameters.AddWithValue("$day", period.Day); command.Parameters.AddWithValue("$week", period.Week);
            command.Parameters.AddWithValue("$created", terminal.Created); command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new Strongway2019MatchingFact(completion, statisticsJson, elapsed)));
            command.CommandText = """
                INSERT INTO character_strongway2019_match_facts(account,character_id,instance_id,competition_id,bucket,day,week,created_at,state,snapshot_json)
                VALUES($account,$character,$instance,$competition,1,$day,$week,$created,$state,$json)
                """;
            command.ExecuteNonQuery();
        }
        tx.Commit(); return true;
    });

    bool ClaimStrongway2019MatchingFacts(AccountOwners.Lease owner, ushort character)
    {
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.ClaimStrongway2019MatchingFacts");
        using var tx = write.Transaction;
        RequireStrongway2019StatisticsOwner(db, tx, owner.Account, character);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account); command.Parameters.AddWithValue("$character", character);
        command.CommandText = """
            SELECT f.instance_id,f.snapshot_json,f.day,f.week,f.created_at,f.state FROM character_strongway2019_match_facts f
            WHERE f.account=$account AND f.character_id=$character AND NOT EXISTS(
              SELECT 1 FROM character_strongway2019_completions c WHERE c.account=f.account AND c.instance_id=f.instance_id)
            ORDER BY f.created_at,f.instance_id
            """;
        var facts = new List<Strongway2019MatchingFact>();
        using (var rows = command.ExecuteReader())
        while (rows.Read())
        {
            var fact = JsonSerializer.Deserialize<Strongway2019MatchingFact>(rows.GetString(1)) ?? throw new InvalidDataException("匹配持久事实损坏");
            var c = fact.Completion;
            if (c.Account != owner.Account || c.Character != character || c.Instance.ToString("N") != rows.GetString(0) ||
                c.Dungeon != 5360 || c.Bucket != Strongway2019RewardBucket.Matching || c.Period.Day != rows.GetInt32(2) ||
                c.Period.Week != rows.GetInt32(3) || c.Created != rows.GetInt64(4) || c.State != rows.GetInt32(5) ||
                c.State is not (0 or 2) || c.Mail is not null || fact.Elapsed is < 0 or > uint.MaxValue)
                throw new InvalidDataException("匹配共享事实的原收件人、周期或终局损坏");
            facts.Add(fact);
        }
        bool changed = false;
        foreach (var fact in facts)
        {
            var c = fact.Completion;
            using var insert = db.CreateCommand(); insert.Transaction = tx; AddStrongway2019CompletionParameters(insert, c);
            insert.Parameters.AddWithValue("$state", c.State);
            insert.CommandText = """
                INSERT INTO character_strongway2019_completions(account,instance_id,character_id,task_attempt,run_epoch,
                  dungeon_id,bucket,day,week,created_at,reward_json,fact_json,state,mail_id)
                VALUES($account,$instance,$character,$attempt,$epoch,$dungeon,$bucket,$day,$week,$created,$reward,$fact,$state,NULL)
                """;
            insert.ExecuteNonQuery();
            insert.Parameters.AddWithValue("$elapsed", (object?)fact.Elapsed ?? DBNull.Value);
            insert.Parameters.AddWithValue("$statistics", fact.StatisticsJson);
            insert.CommandText = """
                INSERT INTO character_strongway2019_statistics(account,character_id,instance_id,task_attempt,run_epoch,dungeon_id,mode,elapsed_ms,fact_json)
                SELECT $account,$character,$instance,$attempt,$epoch,5360,3,$elapsed,$statistics WHERE NOT EXISTS(
                  SELECT 1 FROM character_strongway2019_statistics WHERE character_id=$character AND instance_id=$instance)
                """;
            changed |= insert.ExecuteNonQuery() != 0;
            insert.CommandText = """
                SELECT 1 FROM character_strongway2019_statistics WHERE account=$account AND character_id=$character AND instance_id=$instance
                  AND task_attempt=$attempt AND run_epoch=$epoch AND dungeon_id=5360 AND mode=3 AND elapsed_ms IS $elapsed AND fact_json=$statistics
                """;
            if (insert.ExecuteScalar() is null) throw new InvalidDataException("匹配已有统计与原共享事实不一致");
        }
        tx.Commit();
        return changed;
    }
}
