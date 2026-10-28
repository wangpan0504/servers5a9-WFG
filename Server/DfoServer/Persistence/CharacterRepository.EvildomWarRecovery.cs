using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IEvildomWarStore
{
    static T ReadWarJson<T>(string json) => JsonSerializer.Deserialize<T>(json) ??
        throw new InvalidDataException("魔界持久快照为空");

    static SqliteCommand WarCommand(SqliteConnection db, SqliteTransaction? tx, string account, ushort character, Guid battle)
    {
        var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$id", character);
        command.Parameters.AddWithValue("$battle", battle.ToString("N"));
        return command;
    }

    static void CommitEvildomWarEntryInTransaction(SqliteConnection db, SqliteTransaction tx,
        IReadOnlyList<PartyDungeonReservation> reservations, PartyDungeonEntryCommit[] results)
    {
        if (reservations.All(r => r.EvildomWar is null)) return;
        var first = reservations[0].EvildomWar ?? throw new InvalidOperationException("魔界成员席位缺失");
        new EvildomWarCheckpoint(first.Before).Validate();
        if (reservations.Any(r => r.EvildomWar is not { } seat || seat.Before != first.Before || seat.BindingToken == Guid.Empty ||
            seat.FirstPayment != first.FirstPayment) || reservations.Select(r => r.EvildomWar!.Slot).Distinct().Count() != reservations.Count)
            throw new InvalidOperationException("魔界成员战役或支付阶段不一致");
        uint dungeon = first.Before.StageDungeon(checked((uint)reservations[0].Charge.DungeonId));
        if (!first.Before.Dungeons.Any(row => row.Id == dungeon && row.State == 0))
            throw new InvalidOperationException("魔界入场图未开放");
        var after = first.Before with { Revision = checked(first.Before.Revision + 1),
            Dungeons = first.Before.Dungeons.Select(row => row.Id == dungeon ? row with { State = 1 } : row).ToImmutableArray() };
        using (var command = WarCommand(db, tx, reservations[0].Owner.Account, reservations[0].Expected.Id, first.Before.BattleId))
        {
            command.Parameters.AddWithValue("$revision", first.Before.Revision);
            command.Parameters.AddWithValue("$next", after.Revision);
            command.Parameters.AddWithValue("$deadline", after.Deadline.ToUnixTimeSeconds());
            var checkpoint = new EvildomWarCheckpoint(after);
            if (!first.FirstPayment)
            {
                command.CommandText = "SELECT checkpoint_json FROM evildom_war_battles WHERE battle_id=$battle AND revision=$revision";
                var previous = ReadWarJson<EvildomWarCheckpoint>((string?)command.ExecuteScalar()
                    ?? throw new InvalidOperationException("魔界后续进图缺少原检查点"));
                previous.Validate();
                checkpoint = previous with { View = after };
            }
            checkpoint.Validate();
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(checkpoint));
            command.CommandText = first.FirstPayment
                ? "INSERT INTO evildom_war_battles(battle_id,revision,deadline,checkpoint_json) VALUES($battle,$next,$deadline,$json)"
                : "UPDATE evildom_war_battles SET revision=$next,checkpoint_json=$json WHERE battle_id=$battle AND revision=$revision";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("魔界入场检查点已变化");
        }
        for (int i = 0; i < reservations.Count; i++)
        {
            var r = reservations[i]; var seat = r.EvildomWar!;
            using var command = WarCommand(db, tx, r.Owner.Account, r.Expected.Id, first.Before.BattleId);
            command.Parameters.AddWithValue("$token", seat.BindingToken.ToString("N"));
            command.Parameters.AddWithValue("$instance", r.Charge.Identity.InstanceId.ToString("N"));
            if (seat.FirstPayment)
            {
                var count = r.Incount ?? throw new InvalidOperationException("魔界首费缺少次数凭据");
                if (!count.Increment || count.Row is null || !count.Account.IsDefaultOrEmpty || r.Charge.Items is not null)
                    throw new InvalidOperationException("魔界支付合同不支持额外账号费或物品费");
                var before = r.Expected.Fatigue; var paid = results[i].Character.Fatigue;
                var payment = new EvildomWarPayment(r.Charge.Identity, seat.Slot, seat.BusinessDay, count.Dungeon, count.Period,
                    r.Charge.Weekly, checked((ushort)((before?.Remaining ?? 0) - (paid?.Remaining ?? 0))),
                    checked((ushort)((paid?.Used ?? 0) - (before?.Used ?? 0))));
                command.Parameters.AddWithValue("$payment", JsonSerializer.Serialize(payment));
                command.CommandText = """
                    INSERT INTO character_evildom_war_seats(character_id,account,battle_id,binding_token,payment_json,latest_instance)
                    VALUES($id,$account,$battle,$token,$payment,$instance)
                    """;
            }
            else command.CommandText = """
                UPDATE character_evildom_war_seats SET latest_instance=$instance
                WHERE character_id=$id AND account=$account AND battle_id=$battle AND binding_token=$token AND state=0
                """;
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("魔界入场席位已变化");
        }
    }

    public EvildomWarRecovery? LoadEvildomWarRecovery(AccountOwners.Lease owner, ushort characterId, DateTimeOffset now) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var command = WarCommand(db, null, owner.Account, characterId, Guid.Empty);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        command.CommandText = """
            SELECT b.checkpoint_json,s.payment_json,s.binding_token,s.reconnect_until
            FROM character_evildom_war_seats s JOIN evildom_war_battles b ON b.battle_id=s.battle_id
            JOIN characters c ON c.id=s.character_id AND c.account=s.account
            WHERE s.character_id=$id AND s.account=$account AND s.state IN(0,1) AND b.deadline>$now
              AND (s.reconnect_until IS NULL OR s.reconnect_until>$now)
              AND NOT EXISTS(SELECT 1 FROM character_evildom_war_rewards r WHERE r.character_id=s.character_id AND r.battle_id=s.battle_id)
            ORDER BY b.deadline DESC LIMIT 1
            """;
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        var checkpoint = ReadWarJson<EvildomWarCheckpoint>(row.GetString(0));
        checkpoint.Validate();
        return checkpoint.Completion is not null ? null : new EvildomWarRecovery(checkpoint,
            ReadWarJson<EvildomWarPayment>(row.GetString(1)), Guid.ParseExact(row.GetString(2), "N"),
            row.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(row.GetInt64(3)));
    });

    public bool ClaimEvildomWarSeat(AccountOwners.Lease owner, ushort characterId, EvildomWarRecovery recovery,
        Guid bindingToken, DateTimeOffset now) => owner.Execute(() =>
    {
        if (bindingToken == Guid.Empty || recovery.Payment.FirstRun.CharacterId != characterId) return false;
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.ClaimEvildomWarSeat");
        using var tx = write.Transaction;
        using var command = WarCommand(db, tx, owner.Account, characterId, recovery.Checkpoint.View.BattleId);
        command.Parameters.AddWithValue("$old", recovery.BindingToken.ToString("N"));
        command.Parameters.AddWithValue("$token", bindingToken.ToString("N"));
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        command.CommandText = """
            UPDATE character_evildom_war_seats SET state=0,binding_token=$token,reconnect_until=NULL
            WHERE character_id=$id AND account=$account AND battle_id=$battle AND binding_token=$old AND state IN(0,1)
              AND (reconnect_until IS NULL OR reconnect_until>$now)
              AND EXISTS(SELECT 1 FROM evildom_war_battles b WHERE b.battle_id=$battle AND b.deadline>$now)
            """;
        bool claimed = command.ExecuteNonQuery() == 1; tx.Commit(); return claimed;
    });

    public void DisconnectEvildomWarSeat(AccountOwners.Lease owner, ushort characterId, Guid battleId,
        Guid bindingToken, DateTimeOffset until) => owner.Execute(() =>
    {
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.DisconnectEvildomWarSeat");
        using var tx = write.Transaction; using var command = WarCommand(db, tx, owner.Account, characterId, battleId);
        command.Parameters.AddWithValue("$token", bindingToken.ToString("N"));
        command.Parameters.AddWithValue("$until", until.ToUnixTimeSeconds());
        command.CommandText = """
            UPDATE character_evildom_war_seats SET state=1,reconnect_until=$until
            WHERE character_id=$id AND account=$account AND battle_id=$battle AND binding_token=$token AND state=0
            """;
        command.ExecuteNonQuery(); tx.Commit(); return true;
    });

    public void CommitEvildomWarCheckpoint(AccountOwners.Lease owner, ushort characterId, Guid bindingToken,
        EvildomWarView before, EvildomWarCheckpoint after) => owner.Execute(() =>
    {
        after.Validate();
        if (after.View.BattleId != before.BattleId || after.View.Revision != before.Revision + 1 ||
            after.View.Deadline != before.Deadline || after.View.Mode != before.Mode)
            throw new InvalidOperationException("魔界检查点身份不连续");
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.CommitEvildomWarCheckpoint");
        using var tx = write.Transaction; using var command = WarCommand(db, tx, owner.Account, characterId, before.BattleId);
        command.Parameters.AddWithValue("$token", bindingToken.ToString("N"));
        command.Parameters.AddWithValue("$revision", before.Revision);
        command.Parameters.AddWithValue("$next", after.View.Revision);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(after));
        command.CommandText = """
            UPDATE evildom_war_battles SET revision=$next,checkpoint_json=$json WHERE battle_id=$battle AND revision=$revision
              AND EXISTS(SELECT 1 FROM character_evildom_war_seats s WHERE s.battle_id=$battle AND s.character_id=$id
                AND s.account=$account AND s.binding_token=$token AND s.state IN(0,1))
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("魔界检查点或席位已变化");
        if (after.Completion is not null)
        {
            foreach (var fact in after.Completion.Facts)
            {
                command.Parameters["$id"].Value = fact.Identity.CharacterId;
                command.CommandText = "UPDATE character_evildom_war_seats SET state=2,reconnect_until=NULL WHERE battle_id=$battle AND character_id=$id AND state IN(0,1)";
                command.ExecuteNonQuery();
            }
            // 未参与王座终点的离线/等待成员没有通关资格；只结束席位，由本人owner稍后补偿。
            command.Parameters.AddWithValue("$completed", after.Completion.Created.ToUnixTimeSeconds());
            command.CommandText = "UPDATE character_evildom_war_seats SET state=1,reconnect_until=$completed WHERE battle_id=$battle AND state IN(0,1)";
            command.ExecuteNonQuery();
        }
        tx.Commit(); return true;
    });

    public EvildomWarRefund RefundExpiredEvildomWarSeats(AccountOwners.Lease owner, Character expected, DateTimeOffset now) => owner.Execute(() =>
    {
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.RefundExpiredEvildomWarSeats");
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision || current.Fatigue != expected.Fatigue)
            throw new InvalidOperationException("魔界补偿前角色快照已变化");
        var expired = new List<(Guid Battle, EvildomWarPayment Payment)>();
        using (var command = WarCommand(db, tx, owner.Account, expected.Id, Guid.Empty))
        {
            command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            command.CommandText = """
                SELECT s.battle_id,s.payment_json FROM character_evildom_war_seats s
                JOIN evildom_war_battles b ON b.battle_id=s.battle_id
                WHERE s.character_id=$id AND s.account=$account AND s.state IN(0,1)
                  AND (b.deadline<=$now OR s.reconnect_until<=$now)
                  AND NOT EXISTS(SELECT 1 FROM character_evildom_war_rewards r WHERE r.character_id=s.character_id AND r.battle_id=s.battle_id)
                """;
            using var rows = command.ExecuteReader();
            while (rows.Read()) expired.Add((Guid.ParseExact(rows.GetString(0), "N"), ReadWarJson<EvildomWarPayment>(rows.GetString(1))));
        }
        var fatigue = current.Fatigue;
        foreach (var (battle, payment) in expired)
        {
            using var command = WarCommand(db, tx, owner.Account, expected.Id, battle);
            command.Parameters.AddWithValue("$dungeon", payment.CountDungeon);
            command.Parameters.AddWithValue("$day", payment.CountPeriod.Day);
            command.Parameters.AddWithValue("$week", payment.CountPeriod.Week);
            command.CommandText = """
                UPDATE character_dungeon_incount SET
                  daily_used=CASE WHEN day=$day THEN MAX(0,daily_used-1) ELSE daily_used END,
                  weekly_used=CASE WHEN week=$week THEN MAX(0,weekly_used-1) ELSE weekly_used END
                WHERE character_id=$id AND account=$account AND dungeon=$dungeon
                """;
            command.ExecuteNonQuery();
            if (payment.Weekly is { } weekly)
            {
                command.Parameters.AddWithValue("$panelWeek", weekly.Week); command.Parameters.AddWithValue("$slot", weekly.Slot);
                command.CommandText = """
                    UPDATE character_weekly_dungeon SET used=MAX(0,used-1)
                    WHERE character_id=$id AND account=$account AND week=$panelWeek AND slot=$slot
                    """;
                command.ExecuteNonQuery();
            }
            if (fatigue is not null && payment.BusinessDay == QuestCycle.Day(now)) fatigue = fatigue with
            {
                Used = (ushort)Math.Max(0, fatigue.Used - payment.UsedAdded),
                Remaining = (ushort)Math.Min(fatigue.Maximum, fatigue.Remaining + payment.RemainingConsumed)
            };
            command.CommandText = "UPDATE character_evildom_war_seats SET state=3 WHERE character_id=$id AND account=$account AND battle_id=$battle AND state IN(0,1)";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("魔界补偿回执已变化");
        }
        if (expired.Count > 0)
        {
            using var command = WarCommand(db, tx, owner.Account, expected.Id, Guid.Empty);
            if (fatigue is not null && fatigue != current.Fatigue)
            {
                command.Parameters.AddWithValue("$used", fatigue.Used); command.Parameters.AddWithValue("$remaining", fatigue.Remaining);
                command.CommandText = "UPDATE character_fatigue SET used=$used,remaining=$remaining WHERE character_id=$id AND account=$account";
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("魔界补偿缺少疲劳账本");
            }
            command.Parameters.AddWithValue("$revision", expected.Revision);
            command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("魔界补偿角色版本已变化");
            current = Require(db, tx, owner.Account, expected.Name);
        }
        tx.Commit(); return new EvildomWarRefund(current, expired.Count);
    });
}
