using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Pvp;
using DfoServer.Game.Quests;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    PvpQuestAdvance? CommitPvpQuestProgress(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, PvpRatingClaim claim, int grade)
    {
        if (quests is null || claim.Mode == PvpRatingMode.Total) return null;
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.Parameters.AddWithValue("$match", claim.MatchId.ToString("N"));
        query.CommandText = "SELECT result_json FROM pvp_arena_match_results WHERE match_id=$match";
        if (query.ExecuteScalar() is not string json) throw new InvalidDataException("决斗任务缺少原整局结果");
        var result = System.Text.Json.JsonSerializer.Deserialize<PvpArenaResultRecord>(json) ?? throw new InvalidDataException("决斗结果为空");
        result.Validate();
        var participant = result.Participants.SingleOrDefault(row => row.Account == owner.Account && row.CharacterId == claim.CharacterId);
        if (participant?.QuestBindings is not { IsDefaultOrEmpty: false }) return null;
        var before = ReadActiveMainQuests(db, claim.CharacterId, tx).ToImmutableArray();
        var after = QuestPvpProgress.Apply(before, quests, result, participant, grade);
        if (before.SequenceEqual(after)) return null;
        query.Parameters.AddWithValue("$id", claim.CharacterId); query.Parameters.AddWithValue("$account", owner.Account);
        query.CommandText = "SELECT revision FROM characters WHERE id=$id AND account=$account";
        long revision = query.ExecuteScalar() is long value ? value : throw new InvalidDataException("决斗任务角色不存在");
        WriteQuestConditions(db, tx, claim.CharacterId, before, after);
        query.Parameters.AddWithValue("$before", revision);
        query.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$before";
        if (query.ExecuteNonQuery() != 1) throw new InvalidOperationException("决斗任务角色版本已变化");
        return new(revision, checked(revision + 1), before, after);
    }
}
