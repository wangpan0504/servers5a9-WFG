using DfoServer.Game.Accounts;
using DfoServer.Game.Pvp;
using DfoServer.Game.Characters;
using Microsoft.Data.Sqlite;
using System.Collections.Immutable;
using System.Text.Json;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IPvpMissionProgressStore
{
    public PvpMissionProgress ReadPvpMissionProgress(AccountOwners.Lease owner, ushort characterId) =>
        ReadPvpMissionState(owner, characterId).Progress;

    public PvpMissionState ReadPvpMissionState(AccountOwners.Lease owner, ushort characterId, PvpMissionPeriod? period = null) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        return ReadPvpMissionState(db, tx, owner.Account, characterId, period);
    });

    static PvpMissionState ReadPvpMissionState(SqliteConnection db, SqliteTransaction tx, string account, ushort characterId, PvpMissionPeriod? period = null)
    {
        RequirePvpRatingCharacter(db, tx, account, characterId);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        // 同一MatchId/角色只有一份来源，已落库且结算完成才计入；总力的三份角色来源不互相重复累加。
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(CASE WHEN outcome=$win THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN rating_mode=$total THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN rating_mode=$total AND outcome=$win THEN 1 ELSE 0 END),0)
            FROM character_pvp_rating_claims
            WHERE account=$account AND character_id=$id AND receipt_json IS NOT NULL AND outcome<>$cancelled
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$win", (int)PvpArenaOutcome.Win);
        command.Parameters.AddWithValue("$total", (int)PvpRatingMode.Total);
        command.Parameters.AddWithValue("$cancelled", (int)PvpArenaOutcome.Cancelled);
        PvpMissionProgress progress;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidDataException("决斗目标进度聚合缺少结果");
            int Count(int column) => (int)Math.Min(int.MaxValue, reader.GetInt64(column));
            progress = new(Count(0), Count(1), Count(2), Count(3));
        }
        command.CommandText = "SELECT kind,job,parameter FROM character_pvp_mission_facts WHERE account=$account AND character_id=$id";
        var combos = ImmutableHashSet.CreateBuilder<int>();
        int arcade = 0;
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (reader.GetInt32(0) == 0) combos.Add(reader.GetInt32(1) * 12 + reader.GetInt32(2));
                else arcade |= 1 << reader.GetInt32(2);
        command.CommandText = "SELECT mission_id FROM character_pvp_mission_rewards WHERE account=$account AND character_id=$id";
        var claimed = ImmutableHashSet.CreateBuilder<ushort>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) claimed.Add(checked((ushort)reader.GetInt32(0)));
        var state = new PvpMissionState(progress with { ComboKeys = combos.ToImmutable(), ArcadeMask = arcade }, claimed.ToImmutable());
        return period is null ? state : ReadPvpDailyState(db, tx, account, characterId, period, state);
    }

    public Character CommitPvpMissions(AccountOwners.Lease owner, PvpMissionPlan plan) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitPvpMissions");
        using var tx = write.Transaction;
        if (!ReadPvpMissionState(db, tx, owner.Account, plan.Expected.Id, plan.Period).Same(plan.Before))
            throw new InvalidOperationException("决斗任务规划来源已变化");
        var inventory = plan.Inventory.Plan ?? throw new InvalidDataException("决斗任务缺少奖励计划");
        // 角色revision、完整资产、溢出邮件均由公共提交器在本事务内处理。
        var committed = CommitInventoryInTransaction(db, tx, owner, plan.Expected, inventory);
        if (plan.Fact is { } fact)
        {
            _ = plan.Before.Progress.Apply(fact);
            if (fact.Job != plan.Expected.Job) throw new InvalidDataException("训练事实职业不匹配");
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO character_pvp_mission_facts(character_id,account,kind,job,parameter,instance_id)
                VALUES($id,$account,$kind,$job,$parameter,$instance) ON CONFLICT(character_id,kind,job,parameter) DO NOTHING
                """;
            command.Parameters.AddWithValue("$id", plan.Expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$kind", fact.Kind);
            command.Parameters.AddWithValue("$job", fact.Job);
            command.Parameters.AddWithValue("$parameter", fact.Value);
            command.Parameters.AddWithValue("$instance", fact.Instance.ToString("N"));
            command.ExecuteNonQuery();
        }
        foreach (ushort id in plan.Missions)
        {
            using var claim = db.CreateCommand();
            claim.Transaction = tx;
            claim.CommandText = "INSERT INTO character_pvp_mission_rewards VALUES($id,$account,$mission,$rewards)";
            claim.Parameters.AddWithValue("$id", plan.Expected.Id);
            claim.Parameters.AddWithValue("$account", owner.Account);
            claim.Parameters.AddWithValue("$mission", id);
            claim.Parameters.AddWithValue("$rewards", JsonSerializer.Serialize(plan.Rewards[id]));
            claim.ExecuteNonQuery();
        }
        if (plan.DailyChanged) WritePvpDailyState(db, tx, owner.Account, plan);
        tx.Commit();
        return inventory.Changed ? committed : plan.Expected;
    });
}
