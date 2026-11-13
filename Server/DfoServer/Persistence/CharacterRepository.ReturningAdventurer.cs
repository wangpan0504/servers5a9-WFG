using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.GameWorld.Events;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IReturningAdventurerStore
{
    ReturningAdventurerCatalog? returningCatalog;
    AdventurerBenefitService? returningBenefits;
    TimeProvider returningClock = TimeProvider.System;
    Func<Character, int>? returningAntiEvil;

    internal void ConfigureReturningAdventurers(ReturningAdventurerCatalog catalog, AdventurerBenefitService benefits,
        Func<Character, int> antiEvil, TimeProvider? clock = null)
    {
        if (returningCatalog is not null) throw new InvalidOperationException("回归任务目录不能重复装配");
        returningCatalog = catalog; returningBenefits = benefits;
        returningClock = clock ?? TimeProvider.System; returningAntiEvil = antiEvil;
    }

    long ReturningPeriod(string account) => returningBenefits?.ReturningUntil(account,
        returningClock.GetUtcNow().ToUnixTimeSeconds()) ?? 0;

    public ReturningAdventurerState ReadReturningAdventurer(AccountOwners.Lease owner, Character expected) => owner.Execute(() =>
    {
        long until = ReturningPeriod(owner.Account);
        if (until == 0 || returningCatalog is null) return new ReturningAdventurerState(0, 0, 0, []);
        var observed = ObservedReturningMissions(expected);
        int day = PcRoomTimePointDay.At(returningClock.GetUtcNow());
        using var db = database.Open();
        using (var read = db.BeginRead())
        {
            var before = ReadReturningState(db, read, owner.Account, expected, until, day);
            if (observed.All(id => before.Missions.Single(row => row.Id == id).Progress >=
                returningCatalog.Missions.Single(row => row.Id == id).Count)) return before;
        }
        using var write = db.BeginWrite("CharacterRepository.ObserveReturningAdventurer");
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision)
            throw new InvalidOperationException("回归任务观察角色快照已失效");
        if (ReturningPeriod(owner.Account) != until) return new ReturningAdventurerState(0, 0, 0, []);
        foreach (int id in observed)
            SetReturningProgress(db, tx, owner.Account, until, id, returningCatalog.Missions.Single(row => row.Id == id).Count, false);
        var result = ReadReturningState(db, tx, owner.Account, expected, until, day);
        tx.Commit();
        return result;
    });

    ImmutableArray<int> ObservedReturningMissions(Character character)
    {
        if (returningCatalog is null) return [];
        int? antiEvil = null;
        return returningCatalog.Missions.Where(mission => character.Level >= mission.Level && (mission.Type switch
        {
            "join guild" => character.Guild is not null,
            "level up" => character.Level >= mission.Conditions[0],
            "quest clear - index" => mission.Conditions.All(id => character.CompletedQuests.Contains(id) || character.AccountCompletedQuests.Contains(id)),
            "buff switching setting" => character.SkillSwitchInventory.SkillId != 0,
            "buff switching equipment" => !character.SkillSwitchInventory.Items.IsEmpty,
            "achieve antievil" => (antiEvil ??= returningAntiEvil!(character)) >= mission.Conditions[0],
            _ => false
        })).Select(mission => mission.Id).ToImmutableArray();
    }

    public ReturningAdventurerCommit ClaimReturningAdventurer(AccountOwners.Lease owner, Character expected,
        ReturningAdventurerClaim claim) => owner.Execute(() =>
    {
        if (returningCatalog is null || ReturningPeriod(owner.Account) != claim.Until) return new ReturningAdventurerCommit(false);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ClaimReturningAdventurer");
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision)
            throw new InvalidOperationException("回归奖励角色快照已失效");
        int day = PcRoomTimePointDay.At(returningClock.GetUtcNow());
        if (ReturningPeriod(owner.Account) != claim.Until || day != claim.Day) return new ReturningAdventurerCommit(false);
        var state = ReadReturningState(db, tx, owner.Account, current, claim.Until, day);
        ReturningAdventurerReward? reward = claim.Action switch
        {
            1 when claim.Argument == 0 && state.Welcome == 1 => returningCatalog.Welcome,
            3 when claim.Argument == 0 && state.Daily == 1 => returningCatalog.Daily,
            4 when state.Missions.Any(row => row.Id == claim.Argument && row.State == 1) =>
                returningCatalog.Missions.Single(row => row.Id == claim.Argument).Reward,
            _ => null
        };
        if (reward is null || reward != claim.Reward) return new ReturningAdventurerCommit(false);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO account_returning_reward_claims(account,period_until,action,argument,day,recipient_id,item_id,item_count,claimed_at)
            VALUES($account,$until,$action,$argument,$day,$recipient,$item,$count,$at)
            """;
        command.Parameters.AddWithValue("$account", owner.Account); command.Parameters.AddWithValue("$until", claim.Until);
        command.Parameters.AddWithValue("$action", claim.Action); command.Parameters.AddWithValue("$argument", claim.Argument);
        command.Parameters.AddWithValue("$day", claim.Action == 3 ? day : 0); command.Parameters.AddWithValue("$recipient", current.Id);
        command.Parameters.AddWithValue("$item", reward.ItemId); command.Parameters.AddWithValue("$count", reward.Count);
        command.Parameters.AddWithValue("$at", returningClock.GetUtcNow().ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        var committed = CommitInventoryInTransaction(db, tx, owner, expected, claim.Inventory);
        tx.Commit();
        return new ReturningAdventurerCommit(true, committed);
    });

    ReturningAdventurerState ReadReturningState(SqliteConnection db, SqliteTransaction tx, string account, Character character,
        long until, int day)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$until", until);
        command.CommandText = "SELECT mission_id,progress FROM account_returning_mission_progress WHERE account=$account AND period_until=$until";
        var progress = new Dictionary<int, uint>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) progress.Add(reader.GetInt32(0), checked((uint)reader.GetInt64(1)));
        command.CommandText = "SELECT action,argument,day FROM account_returning_reward_claims WHERE account=$account AND period_until=$until";
        var claimed = new HashSet<int>(); ushort welcome = 1, daily = 1;
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            int action = reader.GetInt32(0);
            if (action == 1) welcome = 2;
            else if (action == 3 && reader.GetInt32(2) == day) daily = 2;
            else if (action == 4) claimed.Add(reader.GetInt32(1));
        }
        return new(until, welcome, daily, returningCatalog!.Missions.Select(mission =>
        {
            uint count = Math.Min(progress.GetValueOrDefault(mission.Id), checked((uint)mission.Count));
            uint state = claimed.Contains(mission.Id) ? 2u : character.Level >= mission.Level && count >= mission.Count ? 1u : 0u;
            return new ReturningMissionProgress(mission.Id, count, state);
        }).ToImmutableArray());
    }

    void RecordReturningEvent(SqliteConnection db, SqliteTransaction tx, string account, int level,
        string type, int condition = 0)
    {
        long until = ReturningPeriod(account);
        if (returningCatalog is null || until == 0) return;
        foreach (var mission in returningCatalog.Missions.Where(row => row.Type == type && row.Level <= level && row.Conditions.Contains(condition)))
            SetReturningProgress(db, tx, account, until, mission.Id, mission.Count, true);
    }

    static void SetReturningProgress(SqliteConnection db, SqliteTransaction tx, string account, long until, int id, int maximum, bool increment)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO account_returning_mission_progress(account,period_until,mission_id,progress) VALUES($account,$until,$id,$value)
            ON CONFLICT(account,period_until,mission_id) DO UPDATE SET progress=min($maximum,progress+$value)
            WHERE progress<$maximum
            """;
        command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$until", until);
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$maximum", maximum);
        command.Parameters.AddWithValue("$value", increment ? 1 : maximum);
        command.ExecuteNonQuery();
    }
}
