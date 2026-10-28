using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Pvp;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IPvpTotalRatingStore
{
    public PvpRatingSnapshot? ReadPvpTotalRating(AccountOwners.Lease owner, Guid teamId) => owner.Execute(() =>
    {
        using var db = database.Open();
        RequirePvpTotalTeam(db, null, owner.Account, teamId);
        return ReadPvpTotalRating(db, null, owner.Account, teamId);
    });

    static PvpRatingSnapshot? ReadPvpTotalRating(SqliteConnection db, SqliteTransaction? tx, string account, Guid teamId)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT revision,state_json FROM account_pvp_total_ratings WHERE account=$account AND team_id=$team";
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$team", teamId.ToString("N"));
        using var reader = query.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), DecodePvpRating<PvpRatingState>(reader.GetString(1))) : null;
    }

    public PvpTotalRatingClaim? ReadPvpTotalRatingClaim(AccountOwners.Lease owner, Guid matchId) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadPvpTotalRatingClaim(db, null, owner.Account, matchId);
    });

    static PvpTotalRatingClaim? ReadPvpTotalRatingClaim(SqliteConnection db, SqliteTransaction? tx, string account, Guid matchId)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT c.team_id,c.outcome,c.receipt_json,r.result_json FROM account_pvp_total_rating_claims c
            JOIN pvp_arena_match_results r ON r.match_id=c.match_id WHERE c.account=$account AND c.match_id=$match
            """;
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$match", matchId.ToString("N"));
        Guid teamId; PvpArenaOutcome outcome; PvpTotalRatingReceipt? receipt; PvpArenaResultRecord result;
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) return null;
            teamId = Guid.ParseExact(reader.GetString(0), "N");
            outcome = (PvpArenaOutcome)reader.GetInt32(1);
            receipt = reader.IsDBNull(2) ? null : DecodePvpRating<PvpTotalRatingReceipt>(reader.GetString(2));
            result = DecodePvpRating<PvpArenaResultRecord>(reader.GetString(3));
        }
        result.Validate();
        var participant = result.Participants.SingleOrDefault(row => row.Account == account);
        if (result.MatchId != matchId || result.RatingMode != PvpRatingMode.Total || participant?.TotalTeam?.Id != teamId ||
            participant.Outcome != outcome) throw new InvalidDataException("总力评分来源与冻结事实不一致");
        // 已删除角色不恢复经验；原整局及队伍来源仍保留，不能奖励同槽新建角色。
        query.CommandText = "SELECT id FROM characters WHERE account=$account";
        var existing = new HashSet<ushort>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) existing.Add(checked((ushort)reader.GetInt32(0)));
        return new(matchId, teamId, outcome, participant.Rating!,
            result.Participants.Single(row => row.Account != account).Rating!.State,
            participant.TotalTeam.Characters.Select(id => id!.Value).Where(existing.Contains).ToImmutableArray(), receipt);
    }

    public Guid[] ReadPendingPvpTotalRatings(AccountOwners.Lease owner, int limit) => owner.Execute(() =>
    {
        if (limit is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = """
            SELECT match_id FROM account_pvp_total_rating_claims WHERE account=$account AND receipt_json IS NULL
            ORDER BY sequence LIMIT $limit
            """;
        query.Parameters.AddWithValue("$account", owner.Account); query.Parameters.AddWithValue("$limit", limit);
        using var reader = query.ExecuteReader();
        var rows = new List<Guid>();
        while (reader.Read()) rows.Add(Guid.ParseExact(reader.GetString(0), "N"));
        return rows.ToArray();
    });

    public PvpTotalRatingReceipt CommitPvpTotalRating(AccountOwners.Lease owner, PvpTotalRatingPlan plan) => owner.Execute(() =>
    {
        if (plan.Characters.IsDefault || plan.Claim.Receipt is not null || plan.Change.Before != plan.Claim.Before.State ||
            plan.Characters.Any(row => row.Claim.MatchId != plan.Claim.MatchId || row.Claim.Mode != PvpRatingMode.Total ||
                !plan.Claim.Characters.Contains(row.Claim.CharacterId)) ||
            plan.Characters.Select(row => row.Claim.CharacterId).Distinct().Count() != plan.Characters.Length)
            throw new InvalidOperationException("总力队伍或角色评分规划无效");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitPvpTotalRating");
        using var tx = write.Transaction;
        var claim = ReadPvpTotalRatingClaim(db, tx, owner.Account, plan.Claim.MatchId) ??
            throw new InvalidOperationException("总力来源不存在或不属于本账号");
        if (claim.TeamId != plan.Claim.TeamId || claim.Outcome != plan.Claim.Outcome || claim.Before != plan.Claim.Before ||
            claim.Opponent != plan.Claim.Opponent || !claim.Characters.SequenceEqual(plan.Claim.Characters))
            throw new InvalidOperationException("总力来源已变化");
        if (claim.Receipt is { } replay)
        {
            if (replay.Change != plan.Change) throw new InvalidOperationException("总力回执不允许冲突重放");
            tx.Commit(); return replay;
        }
        RequirePvpTotalTeam(db, tx, owner.Account, claim.TeamId);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$match", claim.MatchId.ToString("N"));
        command.Parameters.AddWithValue("$team", claim.TeamId.ToString("N"));
        command.CommandText = "SELECT match_id FROM account_pvp_total_rating_claims WHERE account=$account AND receipt_json IS NULL ORDER BY sequence LIMIT 1";
        if (command.ExecuteScalar() is not string first || first != claim.MatchId.ToString("N"))
            throw new InvalidOperationException("总力评分必须按来源顺序消费");
        var current = ReadPvpTotalRating(db, tx, owner.Account, claim.TeamId);
        if (current is null ? claim.Before.Revision != 0 : current != claim.Before)
            throw new InvalidOperationException("总力队伍评分版本已失效");
        var characterReceipts = ImmutableArray.CreateBuilder<PvpRatingReceipt>();
        foreach (ushort id in claim.Characters)
        {
            var prior = ReadPvpRatingClaim(db, tx, owner.Account, id, claim.MatchId) ??
                throw new InvalidOperationException("总力经验来源缺失");
            var characterPlan = plan.Characters.SingleOrDefault(row => row.Claim.CharacterId == id);
            if (prior.Receipt is not null)
            {
                if (characterPlan is not null && prior.Receipt.Change != characterPlan.Change)
                    throw new InvalidOperationException("总力角色经验规划冲突");
                characterReceipts.Add(prior.Receipt);
            }
            else characterReceipts.Add(CommitPvpRatingInTransaction(owner, characterPlan ??
                throw new InvalidOperationException("总力角色经验尚未规划"), db, tx));
        }
        long revision = checked(claim.Before.Revision + 1);
        var receipt = new PvpTotalRatingReceipt(claim.MatchId, claim.TeamId, owner.Account, claim.Outcome,
            claim.Before.Revision, revision, plan.Change, characterReceipts.ToImmutable(), PvpRatingCalculator.RuleVersion);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(plan.Change.After));
        command.CommandText = """
            INSERT INTO account_pvp_total_ratings(account,team_id,revision,state_json) VALUES($account,$team,$revision,$state)
            ON CONFLICT(account) DO UPDATE SET team_id=excluded.team_id,revision=excluded.revision,state_json=excluded.state_json
            """;
        command.ExecuteNonQuery();
        command.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(receipt));
        command.CommandText = "UPDATE account_pvp_total_rating_claims SET receipt_json=$receipt WHERE account=$account AND match_id=$match AND receipt_json IS NULL";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("总力来源不能重复消费");
        tx.Commit(); return receipt;
    });

    static void InsertPvpTotalRatingClaims(SqliteConnection db, SqliteTransaction tx, PvpArenaResultRecord result)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO account_pvp_total_rating_claims(match_id,account,team_id,outcome) VALUES($match,$account,$team,$outcome)";
        command.Parameters.AddWithValue("$match", result.MatchId.ToString("N"));
        var account = command.Parameters.Add("$account", SqliteType.Text);
        var team = command.Parameters.Add("$team", SqliteType.Text);
        var outcome = command.Parameters.Add("$outcome", SqliteType.Integer);
        foreach (var participant in result.Participants)
        {
            RequirePvpTotalTeam(db, tx, participant.Account, participant.TotalTeam!.Id);
            account.Value = participant.Account; team.Value = participant.TotalTeam.Id.ToString("N"); outcome.Value = (int)participant.Outcome;
            command.ExecuteNonQuery();
        }
    }

    static void RequirePvpTotalTeam(SqliteConnection db, SqliteTransaction? tx, string account, Guid team)
    {
        if (ReadPvpTotalTeam(db, tx, account)?.Id != team) throw new InvalidOperationException("总力队伍不存在或不属于本账号");
    }
}
