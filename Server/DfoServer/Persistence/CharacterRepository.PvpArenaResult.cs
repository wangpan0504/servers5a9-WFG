using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Pvp;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IPvpArenaResultStore
{
    public PvpArenaResultRecord CommitPvpArenaResult(AccountOwners.Lease reporter, PvpArenaResultRecord result) =>
        reporter.Execute(() =>
        {
            result.Validate();
            if (!result.Participants.Any(row => row.Account == reporter.Account))
                throw new InvalidOperationException("非本局账号不能提交决斗结果");
            string snapshot = JsonSerializer.Serialize(result);
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitPvpArenaResult");
            using var tx = write.Transaction;
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.Parameters.AddWithValue("$match", result.MatchId.ToString("N"));
            command.CommandText = "SELECT result_json FROM pvp_arena_match_results WHERE match_id=$match";
            if (command.ExecuteScalar() is string existing)
            {
                if (!string.Equals(existing, snapshot, StringComparison.Ordinal))
                    throw new InvalidOperationException("同一决斗局不能提交不同结果");
                tx.Commit();
                return result;
            }
            // 一行保存整局所有成员，任何异常均不会留下部分胜负；不改写对手在线角色或资产。
            command.Parameters.AddWithValue("$result", snapshot);
            command.Parameters.AddWithValue("$at", result.FinishedAt);
            command.CommandText = "INSERT INTO pvp_arena_match_results(match_id,result_json,finished_at) VALUES($match,$result,$at)";
            command.ExecuteNonQuery();
            // 只为这次新提交生成来源，不给既有历史回执补造奖励；离线者由自己的新owner恢复消费。
            command.Parameters.AddWithValue("$mode", (int)result.RatingMode);
            var account = command.Parameters.Add("$account", Microsoft.Data.Sqlite.SqliteType.Text);
            var character = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            var outcome = command.Parameters.Add("$outcome", Microsoft.Data.Sqlite.SqliteType.Integer);
            command.CommandText = """
                INSERT INTO character_pvp_rating_claims(match_id,character_id,account,rating_mode,outcome)
                SELECT $match,id,account,$mode,$outcome FROM characters WHERE id=$id AND account=$account
                """;
            foreach (var participant in result.Participants)
            {
                account.Value = participant.Account;
                outcome.Value = (int)participant.Outcome;
                foreach (ushort id in result.RatingMode == PvpRatingMode.Total
                    ? participant.TotalTeam!.Characters.Select(id => id!.Value) : [participant.CharacterId])
                {
                    character.Value = id;
                    command.ExecuteNonQuery();
                }
            }
            if (result.RatingMode == PvpRatingMode.Total) InsertPvpTotalRatingClaims(db, tx, result);
            tx.Commit();
            return result;
        });

    public PvpArenaResultRecord? ReadPvpArenaResult(AccountOwners.Lease reader, Guid matchId) => reader.Execute(() =>
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.Parameters.AddWithValue("$match", matchId.ToString("N"));
        command.CommandText = "SELECT result_json FROM pvp_arena_match_results WHERE match_id=$match";
        if (command.ExecuteScalar() is not string snapshot) return null;
        var result = JsonSerializer.Deserialize<PvpArenaResultRecord>(snapshot) ??
            throw new InvalidDataException("整局决斗回执为空");
        result.Validate();
        return result.Participants.Any(row => row.Account == reader.Account) ? result : null;
    });
}
