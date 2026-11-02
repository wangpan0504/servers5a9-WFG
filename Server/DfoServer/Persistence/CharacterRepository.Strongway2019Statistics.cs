using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Strongway2019;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IStrongway2019StatisticsStore
{
    public Strongway2019Statistics ReadStrongway2019Statistics(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        RequireStrongway2019StatisticsOwner(db, tx, owner.Account, characterId);
        var result = ReadStrongway2019Statistics(db, tx, characterId);
        tx.Commit();
        return result;
    });

    public Strongway2019StatisticsCommit CommitStrongway2019Statistics(AccountOwners.Lease owner,
        Strongway2019StatisticsPlan plan) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitStrongway2019Statistics");
        using var tx = write.Transaction;
        var result = CommitStrongway2019StatisticsInTransaction(db, tx, owner, plan);
        tx.Commit();
        return result;
    });

    Strongway2019StatisticsCommit CommitStrongway2019StatisticsInTransaction(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, Strongway2019StatisticsPlan plan)
    {
        var run = plan.Run;
        var fact = plan.Fact;
        var identity = plan.Identity;
        uint dungeon = run.Entry.DungeonId;
        int mode = dungeon switch { 5359 => 0, 5363 => 2, 5360 => 3, _ => -1 };
        if (mode < 0 || plan.Expected.Id != identity.CharacterId || run.Entry.CharacterId != plan.Expected.Id)
            throw new InvalidDataException("首期统计必须属于已开放地下城和固定角色");
        Strongway2019Participant? participant = null;
        long? elapsed;
        if (dungeon == 5360)
        {
            if (plan.Competition is not { } competition || plan.Terminal is not { } terminal ||
                !ReferenceEquals(competition.Snapshot.Terminal, terminal))
                throw new InvalidDataException("匹配统计必须使用原比赛冻结的权威终局");
            participant = competition.Participants.SingleOrDefault(row => ReferenceEquals(row.Run, run));
            if (participant is null || !SameStrongway2019Attempt(participant.Identity, identity) ||
                fact is not null && (!ReferenceEquals(run.ClearFact, fact) || fact.Identity != identity))
                throw new InvalidDataException("匹配统计成员或个人终点不是原固定Run事实");
            if (terminal.Reason == Strongway2019FinishReason.Timeout || terminal.WinnerSide != participant.Side)
                return new Strongway2019StatisticsCommit(false, false);
            if (terminal.Reason is not (Strongway2019FinishReason.Clear or Strongway2019FinishReason.Exit) ||
                !terminal.RewardParticipants.Any(row => ReferenceEquals(row, participant)))
                throw new InvalidDataException("匹配统计角色不在原终局胜队中");
            elapsed = terminal.Reason == Strongway2019FinishReason.Clear ? terminal.CombatElapsedMilliseconds : null;
        }
        else
        {
            if (plan.Competition is not null || plan.Terminal is not null || fact is null ||
                !ReferenceEquals(run.ClearFact, fact) || fact.Identity != identity)
                throw new InvalidDataException("普通统计必须使用原Run确认的真实清图事实");
            elapsed = run.Strongway2019ElapsedMilliseconds ?? throw new InvalidDataException("普通真实终点缺少服务器冻结耗时");
        }
        if (elapsed is < 0 or > uint.MaxValue)
            throw new InvalidDataException("首期统计耗时不在客户端可表示范围");
        string factJson = JsonSerializer.Serialize(new { Identity = identity, Fact = fact,
            Competition = plan.Competition?.Id, Reason = plan.Terminal?.Reason, WinnerSide = plan.Terminal?.WinnerSide,
            TerminalFact = plan.Terminal?.ClearFact, Member = participant?.Identity, Side = participant?.Side, Elapsed = elapsed });
        RequireStrongway2019StatisticsOwner(db, tx, owner.Account, plan.Expected.Id);
        using (var receipt = db.CreateCommand())
        {
            receipt.Transaction = tx;
            receipt.CommandText = """
                SELECT account,task_attempt,run_epoch,dungeon_id,mode,elapsed_ms,fact_json
                FROM character_strongway2019_statistics WHERE character_id=$character AND instance_id=$instance
                """;
            receipt.Parameters.AddWithValue("$character", plan.Expected.Id);
            receipt.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
            using var reader = receipt.ExecuteReader();
            if (reader.Read())
            {
                long? recordedElapsed = reader.IsDBNull(5) ? null : reader.GetInt64(5);
                if (reader.GetString(0) != owner.Account || reader.GetInt64(1) != identity.TaskAttempt ||
                    reader.GetInt64(2) != identity.Epoch || reader.GetInt64(3) != dungeon || reader.GetInt32(4) != mode ||
                    recordedElapsed != elapsed || reader.GetString(6) != factJson)
                    throw new InvalidDataException("首期已记录统计的角色或完整终局发生变化");
                reader.Close();
                return new Strongway2019StatisticsCommit(true, true);
            }
        }
        if (identity != run.Snapshot.Identity || run.HasPendingDeath ||
            (dungeon == 5360 ? run.Phase is not (SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement) &&
                !(run.Phase == SinglePlayerDungeonPhase.Loading && plan.Terminal?.Reason == Strongway2019FinishReason.Exit) :
                run.Phase != SinglePlayerDungeonPhase.Settlement) ||
            fact is not null && (fact.DungeonId != dungeon || fact.ClearMap is not { } clearMap ||
                !run.Entry.IsTerminalMap(clearMap) || fact.ConfirmedDeathEvents.Count == 0))
            throw new InvalidDataException("首期首次统计必须来自当前Run确认的终点或原比赛胜队");
        var current = Require(db, tx, owner.Account, plan.Expected.Name);
        if (current.Id != plan.Expected.Id || current.Revision != plan.Expected.Revision ||
            ReadDungeonState(db, current.Id, tx)?.LastAttempt != identity.TaskAttempt)
            throw new InvalidOperationException("首期统计角色版本或正式入场尝试已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_strongway2019_statistics(account,character_id,instance_id,task_attempt,run_epoch,dungeon_id,mode,elapsed_ms,fact_json)
            VALUES($account,$character,$instance,$attempt,$epoch,$dungeon,$mode,$elapsed,$fact)
            """;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$character", plan.Expected.Id);
        command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", identity.Epoch);
        command.Parameters.AddWithValue("$dungeon", dungeon);
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$elapsed", (object?)elapsed ?? DBNull.Value);
        command.Parameters.AddWithValue("$fact", factJson);
        command.ExecuteNonQuery();
        return new Strongway2019StatisticsCommit(true, false);
    }

    static void RequireStrongway2019StatisticsOwner(SqliteConnection db, SqliteTransaction tx, string account, ushort character)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT 1 FROM characters WHERE id=$character AND account=$account";
        command.Parameters.AddWithValue("$character", character);
        command.Parameters.AddWithValue("$account", account);
        if (command.ExecuteScalar() is null)
            throw new InvalidOperationException("首期统计角色不属于当前账号");
    }

    static Strongway2019Statistics ReadStrongway2019Statistics(SqliteConnection db, SqliteTransaction tx, ushort character)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT
              (SELECT count(*) FROM character_strongway2019_statistics WHERE character_id=$character AND mode=3),
              coalesce((SELECT min(elapsed_ms) FROM character_strongway2019_statistics WHERE character_id=$character AND mode=3),0),
              coalesce((SELECT mode FROM character_strongway2019_statistics WHERE character_id=$character AND mode<>3 ORDER BY mode DESC,elapsed_ms LIMIT 1),0),
              coalesce((SELECT elapsed_ms FROM character_strongway2019_statistics WHERE character_id=$character AND mode<>3 ORDER BY mode DESC,elapsed_ms LIMIT 1),0)
            """;
        command.Parameters.AddWithValue("$character", character);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("首期角色历史读取失败");
        return new(checked((uint)reader.GetInt64(0)), checked((uint)reader.GetInt64(1)),
            checked((uint)reader.GetInt64(2)), checked((uint)reader.GetInt64(3)));
    }
}
