using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static CharacterFatigueState? ReadFatigue(SqliteConnection db, ushort id, SqliteTransaction? tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT f.used,f.maximum,f.used_maximum,f.remaining,f.context_auxiliary,f.selection_auxiliary,f.account,c.account,
              COALESCE(d.service_day,0)
            FROM character_fatigue f JOIN characters c ON c.id=f.character_id
              LEFT JOIN character_fatigue_reset_days d ON d.character_id=f.character_id WHERE f.character_id=$id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        if (reader.GetString(6) != reader.GetString(7))
            throw new InvalidDataException("疲劳owner账号归属损坏");
        var state = new CharacterFatigueState(checked((ushort)reader.GetInt32(0)), checked((ushort)reader.GetInt32(1)),
            checked((ushort)reader.GetInt32(2)), checked((ushort)reader.GetInt32(3)), checked((ushort)reader.GetInt32(4)),
            checked((ushort)reader.GetInt32(5))) { ResetDay = reader.GetInt32(8) };
        state.Validate();
        return state;
    }

    static void CreateFatigue(SqliteConnection db, SqliteTransaction tx, string account, ushort id)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        ushort maximum = ReadAccountWallet(db, account, tx).ContractExpiry(56) > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            ? CharacterFatigueState.BlackDiamondMaximum : CharacterFatigueState.OrdinaryMaximum;
        command.CommandText = "INSERT INTO character_fatigue VALUES($id,$account,0,$maximum,0,$maximum,0,0)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$maximum", maximum);
        command.ExecuteNonQuery();
        command.Parameters.AddWithValue("$day", FatigueDailyReset.Day(DateTimeOffset.UtcNow, TimeZoneInfo.Local));
        command.CommandText = "INSERT INTO character_fatigue_reset_days(character_id,service_day) VALUES($id,$day)";
        command.ExecuteNonQuery();
    }

    public Character ResetDailyFatigue(AccountOwners.Lease owner, Character expected, int day, DateTimeOffset now) => owner.Execute(() =>
    {
        if (expected.Fatigue is not { } before || day <= before.ResetDay) return expected;
        var target = FatigueDailyReset.Restore(before, day, expected.BlackDiamondExpiresAt > now.ToUnixTimeSeconds());
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ResetDailyFatigue", deferred: true);
        using var tx = write.Transaction;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", expected.Revision);
        command.Parameters.AddWithValue("$name", expected.Name);
        command.CommandText = "SELECT revision FROM characters WHERE id=$id AND account=$account AND name=$name";
        if (command.ExecuteScalar() is not long revision || revision != expected.Revision || ReadFatigue(db, expected.Id, tx) != before ||
            ReadAccountWallet(db, owner.Account, tx).ContractExpiry(56) != expected.BlackDiamondExpiresAt)
            throw new InvalidOperationException("每日疲劳恢复的角色或资格快照已变化");
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$maximum", target.Maximum);
        command.Parameters.AddWithValue("$remaining", target.Remaining);
        command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("每日疲劳恢复revision冲突");
        command.CommandText = "UPDATE character_fatigue SET used=0,maximum=$maximum,remaining=$remaining WHERE character_id=$id AND account=$account";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("每日疲劳恢复缺少原owner");
        command.CommandText = """
            INSERT INTO character_fatigue_reset_days(character_id,service_day) VALUES($id,$day)
            ON CONFLICT(character_id) DO UPDATE SET service_day=excluded.service_day
            WHERE service_day<excluded.service_day
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("每日疲劳恢复日戳冲突");
        // 只改疲劳与版本，不在每日定时器的writer内重载两遍整套角色资产。
        var committed = expected with { Fatigue = target, Revision = checked(expected.Revision + 1) };
        tx.Commit();
        return committed;
    });

    public Character GrantBlackDiamondFatigue(AccountOwners.Lease owner, Character expected, DateTimeOffset now) => owner.Execute(() =>
    {
        bool expand = expected.Fatigue?.Maximum == CharacterFatigueState.OrdinaryMaximum;
        if (expected.BlackDiamondExpiresAt <= now.ToUnixTimeSeconds() ||
            !expand && expected.Inventory.StaminaUntil == 0) return expected;
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.GrantBlackDiamondFatigue", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision || current.Fatigue != expected.Fatigue ||
            current.BlackDiamondExpiresAt != expected.BlackDiamondExpiresAt || current.Inventory.StaminaUntil != expected.Inventory.StaminaUntil)
            throw new InvalidOperationException("黑钻疲劳补齐的原角色快照已变化");
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$id", current.Id);
        query.Parameters.AddWithValue("$account", owner.Account);
        query.Parameters.AddWithValue("$revision", current.Revision);
        // 选角显式修复旧版误罚，与疲劳补齐共用一次事务；普通读取仍保持只读。
        query.CommandText = "UPDATE characters SET revision=revision+1,stamina_until=0 WHERE id=$id AND account=$account AND revision=$revision";
        if (query.ExecuteNonQuery() != 1) throw new InvalidOperationException("黑钻疲劳补齐revision冲突");
        if (expand)
        {
            var target = current.Fatigue!.GrantBlackDiamondCapacity();
            query.Parameters.AddWithValue("$maximum", target.Maximum);
            query.Parameters.AddWithValue("$remaining", target.Remaining);
            query.CommandText = "UPDATE character_fatigue SET maximum=$maximum,remaining=$remaining WHERE character_id=$id AND account=$account";
            if (query.ExecuteNonQuery() != 1) throw new InvalidOperationException("黑钻疲劳补齐缺少原owner");
        }
        var committed = Require(db, tx, owner.Account, current.Name);
        tx.Commit();
        return committed;
    });

    public Character ConsumeDungeonRoomFatigue(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run, TimeProvider? battlePassClock = null,
        DfoServer.Game.Inventory.Models.InventoryItem? energySource = null) => owner.Execute(() =>
    {
        var snapshot = run.Snapshot;
        var identity = snapshot.Identity;
        var state = expected.Fatigue ?? throw new InvalidOperationException("普通副本疲劳owner未初始化");
        state.Validate();
        bool ordinary = run.Entry.UsesRoomFatigue;
        string rule = CharacterFatigueState.NormalRoomRuleId;
        ushort amount = DfoServer.Game.PcRoom.FatigueAccelerationRules.Cost(1, run.FatigueAccelerationPercent);
        if (!ordinary || run.CurrentMonsterPlan.Room.Map != snapshot.CurrentMap ||
            snapshot.Phase != SinglePlayerDungeonPhase.Loading || identity.CharacterId != expected.Id ||
            identity.InstanceId == Guid.Empty || identity.TaskAttempt != expected.DungeonState?.LastAttempt)
            throw new InvalidDataException("疲劳扣点需要本服已准入实例的真实首次房间加载");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ConsumeDungeonRoomFatigue", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.DungeonState != expected.DungeonState)
            throw new InvalidOperationException("扣点实例或角色已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        var receipts = DungeonTransientReceipts.Get(owner, identity);
        if (receipts.Fatigue.TryGetValue(snapshot.CurrentMap.Id, out var saved) && saved.Committed)
        {
            if (saved.Rule != rule || saved.After != saved.Before.ConsumeOrdinaryRoom(amount))
                throw new InvalidDataException("房间扣点缓存的原始规则或余额变化");
            if (current.Revision == expected.Revision && current.Fatigue == state) return expected;
            if (expected.Revision != saved.BeforeRevision || current.Revision != saved.Revision ||
                saved.Before != state || current.Fatigue != saved.After)
                throw new InvalidOperationException("已提交扣点的原快照精确恢复失效");
            return current;
        }
        if (current.Revision != expected.Revision || current.Fatigue != state)
            throw new InvalidOperationException("扣点的原revision或疲劳owner已变化");
        var target = state.ConsumeOrdinaryRoom(amount);
        var energy = DfoServer.Game.Inventory.Services.QuickSlotEquipmentEnergy.Plan(expected, expected.Inventory,
            energySource, state.Remaining - target.Remaining);
        if (energy is not null)
            current = CommitInventoryInTransaction(db, tx, owner, current, expected, energy);
        else
        {
            command.Parameters.AddWithValue("$revision", expected.Revision);
            command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("首次房间扣点revision冲突");
        }
        command.Parameters.AddWithValue("$used", target.Used);
        command.Parameters.AddWithValue("$remaining", target.Remaining);
        command.CommandText = "UPDATE character_fatigue SET used=$used,remaining=$remaining WHERE character_id=$id AND account=$account";
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("首次房间扣点缺少原疲劳owner");
        ApplyBattlePassDungeonActivity(db,tx,owner.Account,DfoServer.Game.BattlePass.BattlePassDungeonActivity.PrepareFatigue(
            identity,"room",snapshot.CurrentMap.Id,state,target,battlePassClock ?? TimeProvider.System));
        var committed = Require(db, tx, owner.Account, expected.Name, current.Inventory);
        var cached = new DungeonTransientFatigue(rule, expected.Revision, state, target);
        receipts.Fatigue[snapshot.CurrentMap.Id] = cached;
        tx.Commit();
        cached.Committed = true;
        return committed;
    });
}
