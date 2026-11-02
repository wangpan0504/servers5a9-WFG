using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Pvp;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IPvpRatingStore
{
    static void VerifyQuestPvpRating(SqliteConnection db, SqliteTransaction tx, string account, ushort characterId,
        PvpRatingSnapshot? expected)
    {
        if (expected is null) return;
        var current = ReadPvpRating(db,tx,account,characterId);
        if ((current?.Revision ?? 0) != expected.Revision || current is not null && current.State != expected.State)
            throw new InvalidOperationException("任务规划后的决斗段位或评分版本已变化");
    }
    public PvpRatingSnapshot? ReadPvpRating(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        RequirePvpRatingCharacter(db, null, owner.Account, characterId);
        return ReadPvpRating(db, null, owner.Account, characterId);
    });

    static PvpRatingSnapshot? ReadPvpRating(SqliteConnection db, SqliteTransaction? tx, string account, ushort characterId)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT revision,state_json FROM character_pvp_ratings WHERE account=$account AND character_id=$id";
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$id", characterId);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), DecodePvpRating<PvpRatingState>(reader.GetString(1))) : null;
    }

    public PvpRatingClaim? ReadPvpRatingClaim(AccountOwners.Lease owner, ushort characterId, Guid matchId) => owner.Execute(() =>
    {
        using var db = database.Open();
        RequirePvpRatingCharacter(db, null, owner.Account, characterId);
        return ReadPvpRatingClaim(db, null, owner.Account, characterId, matchId);
    });

    static PvpRatingClaim? ReadPvpRatingClaim(SqliteConnection db, SqliteTransaction? tx, string account,
        ushort characterId, Guid matchId)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT c.rating_mode,c.outcome,c.receipt_json,r.result_json FROM character_pvp_rating_claims c
            JOIN pvp_arena_match_results r ON r.match_id=c.match_id
            WHERE c.account=$account AND c.character_id=$id AND c.match_id=$match
            """;
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$id", characterId);
        query.Parameters.AddWithValue("$match", matchId.ToString("N"));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;
        var result = DecodePvpRating<PvpArenaResultRecord>(reader.GetString(3));
        result.Validate();
        var participant = result.Participants.SingleOrDefault(row => row.Account == account &&
            (row.CharacterId == characterId || row.TotalTeam?.Characters.Contains(characterId) == true));
        var mode = (PvpRatingMode)reader.GetInt32(0);
        var outcome = (PvpArenaOutcome)reader.GetInt32(1);
        if (participant is null || result.MatchId != matchId || result.RatingMode != mode || participant.Outcome != outcome)
            throw new InvalidDataException("决斗结算来源与原整局事实不符");
        return new(matchId, characterId, mode, outcome,
            reader.IsDBNull(2) ? null : DecodePvpRating<PvpRatingReceipt>(reader.GetString(2)))
        {
            FrozenBefore = participant.Rating,
            Opponent = mode == PvpRatingMode.Matching
                ? result.Participants.Single(row => row.CharacterId != characterId).Rating!.State : null
        };
    }

    public Guid[] ReadPendingPvpRatings(AccountOwners.Lease owner, ushort characterId, int limit) => owner.Execute(() =>
    {
        if (limit is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = database.Open();
        RequirePvpRatingCharacter(db, null, owner.Account, characterId);
        using var query = db.CreateCommand();
        query.CommandText = """
            SELECT match_id FROM character_pvp_rating_claims
            WHERE account=$account AND character_id=$id AND rating_mode IN (0,1,2) AND receipt_json IS NULL
            ORDER BY sequence LIMIT $limit
            """;
        query.Parameters.AddWithValue("$account", owner.Account);
        query.Parameters.AddWithValue("$id", characterId);
        query.Parameters.AddWithValue("$limit", limit);
        using var reader = query.ExecuteReader();
        var matches = new List<Guid>();
        while (reader.Read()) matches.Add(Guid.ParseExact(reader.GetString(0), "N"));
        return matches.ToArray();
    });

    public PvpRatingReceipt CommitPvpRating(AccountOwners.Lease owner, PvpRatingPlan plan) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitPvpRating");
        using var tx = write.Transaction;
        var receipt = CommitPvpRatingInTransaction(owner, plan, db, tx);
        tx.Commit();
        return receipt;
    });

    PvpRatingReceipt CommitPvpRatingInTransaction(AccountOwners.Lease owner, PvpRatingPlan plan,
        SqliteConnection db, SqliteTransaction tx)
    {
        if (plan.Claim.Mode is not (PvpRatingMode.Ordinary or PvpRatingMode.Matching or PvpRatingMode.Total) || plan.Claim.Receipt is not null ||
            plan.Before.Revision < 0 || plan.Before.State != plan.Change.Before ||
            plan.Claim.Mode is (PvpRatingMode.Ordinary or PvpRatingMode.Total) && (plan.Before.State with
            { Experience = plan.Change.After.Experience, HighestGrade = plan.Change.After.HighestGrade }) != plan.Change.After ||
            plan.Claim.Mode == PvpRatingMode.Matching && (plan.Claim.FrozenBefore != plan.Before || plan.Claim.Opponent is null))
            throw new InvalidOperationException("决斗评分规划与模式或冻结来源不符");
        RequirePvpRatingCharacter(db, tx, owner.Account, plan.Claim.CharacterId);
        var claim = ReadPvpRatingClaim(db, tx, owner.Account, plan.Claim.CharacterId, plan.Claim.MatchId) ??
            throw new InvalidOperationException("决斗评分来源不存在或不属于本账号");
        if (claim with { Receipt = null } != plan.Claim) throw new InvalidOperationException("决斗评分来源已变化");
        if (claim.Receipt is { } replay)
        {
            if (replay.BeforeRevision != plan.Before.Revision || replay.Change != plan.Change)
                throw new InvalidOperationException("同一局角色经验不能提交冲突规划");
            return replay;
        }
        // 必须按持久来源顺序消费，避免重连和新一局交错导致段位点表选择不同。
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$id", claim.CharacterId);
        command.Parameters.AddWithValue("$match", claim.MatchId.ToString("N"));
        command.CommandText = """
            SELECT match_id FROM character_pvp_rating_claims
            WHERE account=$account AND character_id=$id AND rating_mode IN (0,1,2) AND receipt_json IS NULL ORDER BY sequence LIMIT 1
            """;
        if (command.ExecuteScalar() is not string first || first != claim.MatchId.ToString("N"))
            throw new InvalidOperationException("决斗评分必须先恢复更早的待结算局");
        var current = ReadPvpRating(db, tx, owner.Account, claim.CharacterId);
        if (current is null ? plan.Before.Revision != 0 : current != plan.Before)
            throw new InvalidOperationException("决斗评分规划版本已失效");
        long revision = checked(plan.Before.Revision + 1);
        var receipt = new PvpRatingReceipt(claim.MatchId, claim.CharacterId, owner.Account, claim.Outcome,
            plan.Before.Revision, revision, plan.Change, PvpRatingCalculator.RuleVersion)
            { QuestAdvance = CommitPvpQuestProgress(db, tx, owner, claim, plan.Change.Grade) };
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(plan.Change.After));
        command.CommandText = """
            INSERT INTO character_pvp_ratings(character_id,account,revision,state_json) VALUES($id,$account,$revision,$state)
            ON CONFLICT(character_id) DO UPDATE SET revision=excluded.revision,state_json=excluded.state_json
            """;
        command.ExecuteNonQuery();
        command.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(receipt));
        command.CommandText = """
            UPDATE character_pvp_rating_claims SET receipt_json=$receipt
            WHERE account=$account AND character_id=$id AND match_id=$match AND receipt_json IS NULL
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("决斗评分来源不能重复消费");
        return receipt;
    }

    static void RequirePvpRatingCharacter(SqliteConnection db, SqliteTransaction? tx, string account, ushort characterId)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT EXISTS(SELECT 1 FROM characters WHERE account=$account AND id=$id)";
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$id", characterId);
        if (query.ExecuteScalar() is not 1L) throw new InvalidOperationException("决斗评分角色不存在或不属于本账号");
    }

    static T DecodePvpRating<T>(string json) where T : class => JsonSerializer.Deserialize<T>(json) ??
        throw new InvalidDataException("决斗评分持久快照为空");
}
