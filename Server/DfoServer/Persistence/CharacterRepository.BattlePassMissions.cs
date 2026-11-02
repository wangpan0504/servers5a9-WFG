using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IBattlePassMissionStore
{
    BattlePassMissionPolicy? battlePassMissionPolicy;
    Func<DateTimeOffset, bool>? battlePassAvailable;
    bool battlePassDefaultEnabled;
    public bool IsBattlePassAvailable() => battlePassAvailable?.Invoke(battlePassClock.GetUtcNow()) ?? true;
    TimeProvider battlePassClock = TimeProvider.System;
    public void ConfigureBattlePassEvents(Func<DateTimeOffset, bool> available, bool defaultEnabled, TimeProvider? clock = null)
    {
        battlePassAvailable = available; battlePassDefaultEnabled = defaultEnabled; battlePassClock = clock ?? TimeProvider.System;
    }
    static ImmutableArray<BattlePassMissionInstance> ActiveBattlePassMissions(SqliteConnection db, SqliteTransaction? tx,
        BattlePassMissionPolicy policy, DateTimeOffset now)
    {
        if (policy.EventControlled && !DfoServer.Game.Events.ServerEventService.BattlePassScheduleActive(
            ServerEventRepository.Read(db, tx, DfoServer.Game.Events.ServerEventService.BattlePassId), now, policy.DefaultEnabled)) return [];
        return policy.Active(now);
    }

    // 仅在Runtime开始接受连接之前装配一次；同season规则一经写入不能静默更改。
    public void ConfigureBattlePassMissions(BattlePassMissionPolicy policy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (battlePassMissionPolicy is { } previous)
        {
            if (previous.SeasonKey != policy.SeasonKey || previous.RulesJson != policy.RulesJson)
                throw new InvalidOperationException("运行中的战令任务不能更换赛季规则");
            return;
        }
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ConfigureBattlePassMissions");
        using var tx = write.Transaction;
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO battle_pass_season_rules VALUES($season,$start,$end,$rules) ON CONFLICT(season_key) DO NOTHING";
        command.Parameters.AddWithValue("$season", policy.SeasonKey);
        command.Parameters.AddWithValue("$start", policy.Start.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$end", policy.End.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$rules", policy.RulesJson);
        command.ExecuteNonQuery();
        command.CommandText = "SELECT rules_json FROM battle_pass_season_rules WHERE season_key=$season AND start_utc=$start AND end_utc=$end";
        if (command.ExecuteScalar() is not string actual || actual != policy.RulesJson)
            throw new InvalidDataException("已冻结的战令赛季规则与当前配置不同");
        tx.Commit();
        battlePassClock = clock ?? TimeProvider.System;
        battlePassMissionPolicy = policy;
    }

    public BattlePassMissionState? ReadBattlePassMissions(AccountOwners.Lease owner, DateTimeOffset now) => owner.Execute(() =>
    {
        if (battlePassMissionPolicy is not { } policy) return null;
        using var db = database.Open();
        using (var read = db.BeginRead())
        {
            using var exists = db.CreateCommand(); exists.Transaction = read;
            exists.CommandText = "SELECT 1 FROM account_battle_pass_seasons WHERE account=$account AND season_key=$season";
            exists.Parameters.AddWithValue("$account", owner.Account); exists.Parameters.AddWithValue("$season", policy.SeasonKey);
            if (exists.ExecuteScalar() is not null)
                return ReadBattlePassMissionState(db, read, owner.Account, policy, now);
            if (ActiveBattlePassMissions(db, read, policy, now).IsEmpty) return null;
        }
        // 仅缺失账号季才幂等初始化；已有季的一次只读快照同时提供season与missions。
        using var write = db.BeginWrite("CharacterRepository.ReadBattlePassMissions"); using var tx = write.Transaction;
        if (ActiveBattlePassMissions(db, tx, policy, now).IsEmpty) return null;
        using (var create = db.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = "INSERT INTO account_battle_pass_seasons(account,season_key) VALUES($account,$season) ON CONFLICT(account,season_key) DO NOTHING";
            create.Parameters.AddWithValue("$account", owner.Account); create.Parameters.AddWithValue("$season", policy.SeasonKey);
            create.ExecuteNonQuery();
        }
        var initialized = ReadBattlePassMissionState(db, tx, owner.Account, policy, now);
        tx.Commit();
        return initialized;
    });

    public BattlePassMissionCommit CommitBattlePassMission(AccountOwners.Lease owner, Character expected, long seasonRevision,
        DateTimeOffset sampledAt, byte action, ushort key, uint category, uint viewRow, TimeProvider clock) => owner.Execute(() =>
    {
        if (action == 0)
            return CommitNativeShopVisit(owner, expected, seasonRevision, sampledAt, key, category, viewRow, clock);
        if (battlePassMissionPolicy is not { } policy || action != 1 || category > 2 || key == 0)
            return BattlePassMissionCommit.Reject();
        // RowIndex仅是客户端显示行提示，不能选择权威18槽或参与唯一claim键。
        _ = viewRow;
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.CommitBattlePassMission");
        using var tx = write.Transaction;
        // 请求采样只确定原任务期段；等待writer后统一重采，旧期段不能挪到新日/周领取。
        var now = clock.GetUtcNow();
        if (now < sampledAt) return BattlePassMissionCommit.Reject();
        var requested = ActiveBattlePassMissions(db, tx, policy, sampledAt).SingleOrDefault(row => row.Key.Category == category && row.Key.Id == key);
        if (requested is null) return BattlePassMissionCommit.Reject();
        var character = Require(db, tx, owner.Account, expected.Name);
        if (character.Id != expected.Id || character.Slot != expected.Slot || character.Revision != expected.Revision)
            return BattlePassMissionCommit.Reject();
        var current = ReadBattlePassMissionState(db, tx, owner.Account, policy, now);
        if (current.Season.Revision != seasonRevision) return BattlePassMissionCommit.Reject();
        var slot = current.Slots.SingleOrDefault(row => row.Instance.Key.Category == category && row.Instance.Key.Id == key);
        if (slot is not { Ready: true } || slot.Instance.Key != requested.Key) return BattlePassMissionCommit.Reject();
        long experience, revision;
        try
        {
            experience = checked(current.Season.TotalExperience + slot.Instance.Experience);
            revision = checked(current.Season.Revision + 1);
        }
        catch (OverflowException) { return BattlePassMissionCommit.Reject(17); }
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "UPDATE account_battle_pass_seasons SET total_experience=$xp,revision=$next WHERE account=$account AND season_key=$season AND revision=$previous";
        command.Parameters.AddWithValue("$account", owner.Account); command.Parameters.AddWithValue("$season", policy.SeasonKey);
        command.Parameters.AddWithValue("$xp", experience); command.Parameters.AddWithValue("$next", revision);
        command.Parameters.AddWithValue("$previous", current.Season.Revision);
        if (command.ExecuteNonQuery() != 1) return BattlePassMissionCommit.Reject();
        command.CommandText = """
            INSERT INTO account_battle_pass_mission_claims
              (account,season_key,category,period_key,mission_id,experience,claimed_at,resulting_season_revision)
            VALUES($account,$season,$category,$period,$mission,$earned,$at,$next)
            """;
        command.Parameters.AddWithValue("$category", slot.Instance.Key.Category);
        command.Parameters.AddWithValue("$period", slot.Instance.Key.Period);
        command.Parameters.AddWithValue("$mission", slot.Instance.Key.Id);
        command.Parameters.AddWithValue("$earned", slot.Instance.Experience);
        command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        ApplyBattlePassMissionCompletion(db, tx, owner.Account, current, slot.Instance.Key.Category);
        var committed = ReadBattlePassMissionState(db, tx, owner.Account, policy, now);
        tx.Commit();
        return new BattlePassMissionCommit(true, 0, committed);
    });

    BattlePassMissionState ReadBattlePassMissionState(SqliteConnection db, SqliteTransaction tx, string account,
        BattlePassMissionPolicy policy, DateTimeOffset now)
    {
        var season = ReadBattlePassState(db, tx, account, policy.SeasonKey);
        var active = ActiveBattlePassMissions(db, tx, policy, now);
        if (active.IsEmpty) return new(season, []);
        var counts = new Dictionary<BattlePassMissionKey, long>();
        var claims = new HashSet<BattlePassMissionKey>();
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$season", policy.SeasonKey);
        command.Parameters.AddWithValue("$daily", active[0].Key.Period); command.Parameters.AddWithValue("$weekly", active[4].Key.Period);
        command.Parameters.AddWithValue("$whole", active[8].Key.Period);
        const string where = " WHERE account=$account AND season_key=$season AND ((category=0 AND period_key=$daily) OR (category=1 AND period_key=$weekly) OR (category=2 AND period_key=$whole))";
        command.CommandText = "SELECT category,period_key,mission_id,current_count FROM account_battle_pass_mission_progress" + where;
        using (var reader = command.ExecuteReader())
            while (reader.Read()) counts.Add(new(checked((byte)reader.GetInt32(0)), reader.GetInt64(1), reader.GetInt32(2)), reader.GetInt64(3));
        command.CommandText = "SELECT category,period_key,mission_id FROM account_battle_pass_mission_claims" + where;
        using (var reader = command.ExecuteReader())
            while (reader.Read()) claims.Add(new(checked((byte)reader.GetInt32(0)), reader.GetInt64(1), reader.GetInt32(2)));
        bool terminal = policy.Catalog.LevelAtTotalExperience(checked((ulong)season.TotalExperience)) >= 80;
        return new(season, active.Select(instance =>
        {
            long count = counts.GetValueOrDefault(instance.Key);
            bool completed = claims.Contains(instance.Key);
            return new BattlePassMissionSlot(instance, count, completed,
                !terminal && !completed && count >= instance.Definition.RequiredCount && (!instance.Premium || season.PassType == 1));
        }).ToImmutableArray());
    }

    // 此helper仅由真实远征原事务调用，绝不开第二连接，不作为客户端自由source/经验接口。
    void ApplyBattlePassExpeditionStart(SqliteConnection db, SqliteTransaction tx, string account, Guid runId, long started)
    {
        if (battlePassMissionPolicy is not { } policy || runId == Guid.Empty || started < 0) return;
        var at = DateTimeOffset.FromUnixTimeSeconds(started);
        if (ActiveBattlePassMissions(db, tx, policy, at).IsEmpty) return;
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$season", policy.SeasonKey);
        command.CommandText = "INSERT INTO account_battle_pass_seasons(account,season_key) VALUES($account,$season) ON CONFLICT(account,season_key) DO NOTHING";
        command.ExecuteNonQuery();
        var before = ReadBattlePassMissionState(db, tx, account, policy, at);
        command.CommandText = """
            INSERT INTO account_battle_pass_source_events(account,source_kind,source_id,season_key,occurred_at)
            VALUES($account,'adventure_expedition_start',$source,$season,$at)
            ON CONFLICT(account,source_kind,source_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$source", runId.ToString("N")); command.Parameters.AddWithValue("$at", started);
        if (command.ExecuteNonQuery() != 1) return;
        int changed = 0;
        foreach (var slot in before.Slots.Where(row => BattlePassMissionPolicy.IsExpeditionStart(row.Instance.Definition) &&
            !row.Completed && row.Count < row.Instance.Definition.RequiredCount))
        {
            using var update = db.CreateCommand(); update.Transaction = tx;
            update.CommandText = """
                INSERT INTO account_battle_pass_mission_progress(account,season_key,category,period_key,mission_id,current_count)
                VALUES($account,$season,$category,$period,$mission,$next)
                ON CONFLICT(account,season_key,category,period_key,mission_id)
                DO UPDATE SET current_count=$next WHERE current_count=$before
                """;
            update.Parameters.AddWithValue("$account", account); update.Parameters.AddWithValue("$season", policy.SeasonKey);
            update.Parameters.AddWithValue("$category", slot.Instance.Key.Category); update.Parameters.AddWithValue("$period", slot.Instance.Key.Period);
            update.Parameters.AddWithValue("$mission", slot.Instance.Key.Id); update.Parameters.AddWithValue("$before", slot.Count);
            update.Parameters.AddWithValue("$next", checked(slot.Count + 1));
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("远征任务进度在同事务内变化");
            changed++;
        }
        if (changed == 0) return;
        long revision = checked(before.Season.Revision + 1);
        command.CommandText = "UPDATE account_battle_pass_seasons SET revision=$next WHERE account=$account AND season_key=$season AND revision=$before";
        command.Parameters.AddWithValue("$next", revision); command.Parameters.AddWithValue("$before", before.Season.Revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("远征任务赛季版本在同事务内变化");
    }
}
