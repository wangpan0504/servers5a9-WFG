using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.ImperialArena;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IImperialArenaStore
{
    public ImperialArenaDailyRoute ReadImperialArenaRoute(AccountOwners.Lease owner, ImperialArenaCatalog catalog, ImperialArenaPeriod period) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.Parameters.AddWithValue("$day", period.Day);
        command.CommandText = "SELECT route_json FROM imperial_arena_daily_routes WHERE day=$day";
        ImperialArenaDailyRoute Decode(string json)
        {
            var route = JsonSerializer.Deserialize<ImperialArenaDailyRoute>(json) ?? throw new InvalidDataException("帝国每日路线为空");
            if (route.Period != period)
                throw new InvalidDataException("帝国每日路线结构无效");
            route.Validate(catalog);
            return route;
        }
        if (command.ExecuteScalar() is string existing) return Decode(existing);
        var candidate = ImperialArenaDailyRoute.Create(catalog, period);
        using var write = db.BeginWrite("CharacterRepository.ReadImperialArenaRoute");
        using var tx = write.Transaction;
        command.Transaction = tx;
        command.Parameters.AddWithValue("$route", JsonSerializer.Serialize(candidate));
        command.CommandText = "INSERT INTO imperial_arena_daily_routes(day,route_json) VALUES($day,$route) ON CONFLICT(day) DO NOTHING";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT route_json FROM imperial_arena_daily_routes WHERE day=$day";
        var result = Decode((string)command.ExecuteScalar()!);
        tx.Commit();
        return result;
    });

    public ImperialArenaState ReadImperialArenaState(AccountOwners.Lease owner, ImperialArenaPeriod period) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        var result = ReadImperialArenaState(db, tx, owner.Account, period);
        tx.Commit();
        return result;
    });

    static ImperialArenaConfiguration ReadImperialArenaConfiguration(SqliteConnection db, SqliteTransaction? tx, string account)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT revision,first,second FROM account_imperial_arena_configuration WHERE account=$account";
        command.Parameters.AddWithValue("$account", account);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return ImperialArenaConfiguration.Empty;
        var result = new ImperialArenaConfiguration(reader.GetInt64(0), checked((ushort)reader.GetInt32(1)), checked((ushort)reader.GetInt32(2)));
        result.Validate();
        return result;
    }

    static ImperialArenaState ReadImperialArenaState(SqliteConnection db, SqliteTransaction tx, string account, ImperialArenaPeriod period)
    {
        var configuration = ReadImperialArenaConfiguration(db, tx, account);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$day", period.Day);
        command.CommandText = "SELECT mode,hard_round FROM account_imperial_arena_days WHERE account=$account AND day=$day";
        ImperialArenaMode mode = ImperialArenaMode.None;
        int round = 0;
        using (var reader = command.ExecuteReader())
            if (reader.Read()) { mode = (ImperialArenaMode)reader.GetInt32(0); round = reader.GetInt32(1); }
        command.CommandText = "SELECT monster FROM account_imperial_arena_rewards WHERE account=$account AND day=$day";
        var rewarded = ImmutableHashSet.CreateBuilder<int>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) rewarded.Add(reader.GetInt32(0));
        return new(configuration, period, mode, rewarded.ToImmutable(), round);
    }

    public ImperialArenaConfiguration SaveImperialArenaConfiguration(AccountOwners.Lease owner,
        ImperialArenaConfiguration expected, ImperialArenaConfiguration desired) => owner.Execute(() =>
    {
        expected.Validate(); desired.Validate();
        if (expected.Revision != desired.Revision) throw new InvalidDataException("帝国竞技场配置规划版本不一致");
        using var db = database.Open();
        if (ReadImperialArenaConfiguration(db, null, owner.Account) != expected)
            throw new InvalidOperationException("帝国竞技场配置已变化");
        if (expected == desired) return expected;
        using var write = db.BeginWrite("CharacterRepository.SaveImperialArenaConfiguration");
        using var tx = write.Transaction;
        if (ReadImperialArenaConfiguration(db, tx, owner.Account) != expected)
            throw new InvalidOperationException("帝国竞技场配置已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$first", desired.First);
        command.Parameters.AddWithValue("$second", desired.Second);
        if (!desired.IsEmpty)
        {
            command.CommandText = """
                SELECT count(*) FROM characters c WHERE c.account=$account AND c.id IN($first,$second) AND c.level>=90
                  AND NOT EXISTS(SELECT 1 FROM account_pvp_private_characters p WHERE p.character_id=c.id)
                """;
            if (command.ExecuteScalar() is not 2L) throw new InvalidOperationException("帝国竞技场角色资格或账号归属已变化");
        }
        var result = desired with { Revision = checked(expected.Revision + 1) };
        command.Parameters.AddWithValue("$revision", result.Revision);
        command.Parameters.AddWithValue("$before", expected.Revision);
        command.CommandText = """
            INSERT INTO account_imperial_arena_configuration(account,revision,first,second) VALUES($account,$revision,$first,$second)
            ON CONFLICT(account) DO UPDATE SET revision=excluded.revision,first=excluded.first,second=excluded.second
            WHERE account_imperial_arena_configuration.revision=$before
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("帝国竞技场配置提交冲突");
        tx.Commit();
        return result;
    });

    public bool MatchesImperialArenaCharacters(AccountOwners.Lease owner, ImperialArenaCharacters expected) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM account_imperial_arena_configuration WHERE account=$account AND revision=$configuration AND first=$first AND second=$second)
              AND EXISTS(SELECT 1 FROM characters WHERE account=$account AND id=$first AND revision=$primary AND clone_title_item_id=$primaryTitle)
              AND EXISTS(SELECT 1 FROM characters WHERE account=$account AND id=$second AND revision=$standby AND clone_title_item_id=$standbyTitle)
              AND COALESCE((SELECT revision FROM account_wallets WHERE account=$account),0)=$wallet
            """;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$configuration", expected.Projection.State.Configuration.Revision);
        command.Parameters.AddWithValue("$first", expected.Primary.Id);
        command.Parameters.AddWithValue("$second", expected.Standby.Id);
        command.Parameters.AddWithValue("$primary", expected.Primary.Revision);
        command.Parameters.AddWithValue("$standby", expected.Standby.Revision);
        command.Parameters.AddWithValue("$primaryTitle", expected.Primary.CloneTitleItemId);
        command.Parameters.AddWithValue("$standbyTitle", expected.Standby.CloneTitleItemId);
        command.Parameters.AddWithValue("$wallet", expected.Standby.Inventory.AccountWallet.Revision);
        bool result = command.ExecuteScalar() is 1L &&
            ReadEffectiveExpertContractExpiry(db, expected.Primary.Id, tx) == expected.Primary.Learning.ExpertContractExpiresAt &&
            ReadEffectiveExpertContractExpiry(db, expected.Standby.Id, tx) == expected.Standby.Learning.ExpertContractExpiresAt;
        tx.Commit();
        return result;
    });

    public Character PurchaseImperialArenaEntry(AccountOwners.Lease owner, Character expected, ImperialArenaMode mode,
        ImperialArenaPeriod period, int gold) => owner.Execute(() =>
    {
        if (mode is not (ImperialArenaMode.Normal or ImperialArenaMode.Hard) || gold <= 0 || expected.Level < 90 || expected.IsPvpPrivate)
            throw new InvalidDataException("帝国竞技场付费请求无效");
        using var db = database.Open();
        using (var read = db.BeginRead())
        {
            var state = ReadImperialArenaState(db, read, owner.Account, period);
            RequireImperialArenaPurchaseTeam(db, read, owner.Account, expected.Id, state.Configuration);
            if (state.PaidMode == mode)
            {
                var actual = Require(db, read, owner.Account, expected.Name);
                if (actual.Id != expected.Id || actual.Revision != expected.Revision)
                    throw new InvalidOperationException("帝国竞技场角色版本已变化");
                read.Commit(); return expected;
            }
            if (state.PaidMode != ImperialArenaMode.None) throw new InvalidOperationException("帝国竞技场本日模式已固定");
        }
        using var write = db.BeginWrite("CharacterRepository.PurchaseImperialArenaEntry");
        using var tx = write.Transaction;
        var current = ReadImperialArenaState(db, tx, owner.Account, period);
        RequireImperialArenaPurchaseTeam(db, tx, owner.Account, expected.Id, current.Configuration);
        if (current.PaidMode != ImperialArenaMode.None)
            throw new InvalidOperationException("帝国竞技场入场券状态已变化");
        if (expected.Inventory.Gold < gold) throw new InvalidOperationException("帝国竞技场入场金币不足");
        var plan = new InventoryPlan(expected.Inventory, expected.Inventory with { Gold = expected.Inventory.Gold - checked((uint)gold) }, "imperial_arena_ticket");
        var committed = CommitInventoryInTransaction(db, tx, owner, expected, plan);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO account_imperial_arena_days(account,day,mode) VALUES($account,$day,$mode)";
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$day", period.Day);
        command.Parameters.AddWithValue("$mode", (int)mode);
        command.ExecuteNonQuery();
        tx.Commit();
        return committed;
    });

    static void RequireImperialArenaPurchaseTeam(SqliteConnection db, SqliteTransaction tx, string account,
        ushort primary, ImperialArenaConfiguration configuration)
    {
        if (configuration.IsEmpty || configuration.First != primary)
            throw new InvalidOperationException("帝国竞技场购券需要当前有效队首和双角色编队");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT count(*) FROM characters c WHERE c.account=$account AND c.id IN($first,$second) AND c.level>=90
              AND NOT EXISTS(SELECT 1 FROM account_pvp_private_characters p WHERE p.character_id=c.id)
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$first", configuration.First);
        command.Parameters.AddWithValue("$second", configuration.Second);
        if (command.ExecuteScalar() is not 2L) throw new InvalidOperationException("帝国竞技场购券编队资格已失效");
    }

    static void RemoveImperialArenaCharacter(SqliteConnection db, SqliteTransaction tx, string account, ushort character)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$id", character);
        // 先结清固定收件人的冻结奖励，再沿删角既有规则清邮箱；账号日资格和周成绩继续保留。
        command.CommandText = "SELECT instance_id FROM account_imperial_arena_runs WHERE account=$account AND first=$id AND mode=2 AND closed=0";
        var pending = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) pending.Add(reader.GetString(0));
        foreach (var instance in pending) CompleteImperialArenaRun(db, tx, account, instance);
        command.CommandText = "UPDATE account_imperial_arena_configuration SET first=0,second=0,revision=revision+1 WHERE account=$account AND (first=$id OR second=$id)";
        command.ExecuteNonQuery();
    }
}
