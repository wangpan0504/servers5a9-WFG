using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BattlePassRewardTests
{
    // 不读取PVF、不建网络连接、不接触玩家数据；在Core中保留多奖事务不变量。
    static void Persistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-reward-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool succeeded = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            const string season = "core-persistence-season";
            const string marker = "BP Core late complete reward reached";
            var fixture = new Fixture();
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("pass-core", () => { });
            var point = new Location(1, 0, 10, 10);
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), point, point,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var created = repository.Create(owner, new NewCharacter("PassCore", point, null) { Level = 95 });
            var session = new GameSession(repository, towns, owner, timeProvider: new FixedClock());
            session.Select(created.Slot);
            session.MarkReady();
            var empty = repository.ReadBattlePass(owner, season);
            That(empty.TotalExperience == 0 && empty.PassType == 0 && empty.Revision == 0 && empty.Claims.IsEmpty,
                "new real account-season starts with no manufactured progress, premium entitlement or claims");
            long experience = checked((long)fixture.Catalog.TotalExperienceToReach(4));
            // 资格仅种入本组一次性的Core夹具数据库。
            Sql("UPDATE account_battle_pass_seasons SET total_experience=$p0,pass_type=1 WHERE account=$p1 AND season_key=$p2",
                experience, owner.Account, season);
            var selected = session.Selected ?? throw new InvalidOperationException("persistence fixture selection missing");
            string before = Snapshot();
            Sql($"""
                CREATE TRIGGER fail_core_pass_claim AFTER INSERT ON account_battle_pass_claims
                WHEN NEW.level=4 AND NEW.track=1
                BEGIN
                  SELECT CASE WHEN
                    (SELECT revision FROM characters WHERE id=NEW.recipient_character_id)={selected.Revision + 1}
                    AND (SELECT revision FROM account_battle_pass_seasons WHERE account=NEW.account AND season_key=NEW.season_key)=1
                    AND (SELECT count(*) FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid
                         WHERE i.account=NEW.account AND i.character_id=NEW.recipient_character_id AND s.character_id=NEW.recipient_character_id
                           AND ((json_extract(i.item_core,'$.ItemId')=490705122 AND json_extract(i.item_core,'$.Value')=1)
                             OR (json_extract(i.item_core,'$.ItemId')=490705101 AND json_extract(i.item_core,'$.Value')=3)))=2
                    AND (SELECT count(*) FROM inventory_audit_log WHERE character_id=NEW.recipient_character_id AND action_name='battle_pass_reward')=2
                    AND (SELECT count(*) FROM account_battle_pass_claims WHERE account=NEW.account AND season_key=NEW.season_key AND level=4 AND track=1)=1
                  THEN RAISE(ABORT,'{marker}') ELSE RAISE(ABORT,'BP Core late prerequisites missing') END;
                END;
                """);
            bool late = false;
            try { session.ClaimBattlePassReward(fixture.Service, season, 0, 1, 4); }
            catch (SqliteException error) when (error.SqliteErrorCode == 19 &&
                error.Message == $"SQLite Error 19: '{marker}'.") { late = true; }
            That(late, "exact late failure observes both reward entities/slots/audits, claim and both incremented revisions");
            That(Snapshot() == before && ReferenceEquals(session.Selected, selected),
                "late SQL failure rolls back complete assets, character, audits, UID sequence, season and claims without replacing Session selection");
            Sql("DROP TRIGGER fail_core_pass_claim");
            var committed = session.ClaimBattlePassReward(fixture.Service, season, 0, 1, 4);
            That(committed.Inventory is { Accepted: true, Receipt: not null } && committed.State is { Revision: 1 } &&
                session.Selected is { } current && current.Revision == selected.Revision + 1 &&
                !ReferenceEquals(current, selected), "same Session retries after rollback and installs one committed revision with receipt");
            var granted = session.Selected ?? throw new InvalidOperationException("committed selection missing");
            That(Totals(granted.Inventory).SequenceEqual(new[] { (490705101, 3), (490705122, 1) }) &&
                granted.Inventory.Items.Length == 2 && granted.Inventory.Items.All(item => item.Uid > 0) &&
                granted.Inventory.Items.Select(item => item.Uid).Distinct().Count() == 2,
                "successful retry grants the full two-entity/four-unit premium reward with real unique UIDs");
            string committedState = Snapshot();
            var repeated = session.ClaimBattlePassReward(fixture.Service, season, 0, 1, 4);
            That(!repeated.Inventory.Accepted && Snapshot() == committedState && ReferenceEquals(session.Selected, granted),
                "same request replay refuses without any persisted or online state change");
            var restoredRepository = new CharacterRepository(path);
            var restoredSession = new GameSession(restoredRepository, towns, owner, timeProvider: new FixedClock());
            restoredSession.Select(created.Slot);
            restoredSession.MarkReady();
            var state = restoredSession.ReadBattlePass(season)?.State ?? throw new InvalidOperationException("restored season missing");
            That(JsonSerializer.Serialize(restoredSession.Selected!.Inventory) == JsonSerializer.Serialize(granted.Inventory) &&
                state.TotalExperience == experience && state.PassType == 1 && state.Revision == 1 &&
                state.Claims.SetEquals([new BattlePassRewardKey(1, 4)]),
                "new Repository/Session restores complete inventory/core, authoritative progress, premium type and exact claim");
            That(Query("PRAGMA integrity_check").Single().Single()?.ToString() == "ok" && Query("PRAGMA foreign_key_check").Length == 0,
                "persistent rollback/retry/replay preserve isolated database integrity and foreign keys");
            var batchPlan = fixture.Service.Plan(granted, state, 1, 1, 4).Plan ??
                throw new InvalidOperationException("persistent batch plan missing");
            That(batchPlan.Rewards.SequenceEqual(new[] { new BattlePassRewardKey(1, 1), new(1, 2), new(1, 3), new(0, 4) }),
                "persistent batch freezes every remaining key around previous single claim");
            const string batchMarker = "BP batch last claim reached";
            string batchBefore = Snapshot();
            Sql($"""
                CREATE TRIGGER fail_batch_claim AFTER INSERT ON account_battle_pass_claims
                WHEN NEW.level=4 AND NEW.track=0
                BEGIN
                  SELECT CASE WHEN
                    (SELECT revision FROM characters WHERE id=NEW.recipient_character_id)={granted.Revision + 1}
                    AND (SELECT revision FROM account_battle_pass_seasons WHERE account=NEW.account AND season_key=NEW.season_key)=2
                    AND (SELECT count(*) FROM account_battle_pass_claims WHERE account=NEW.account AND season_key=NEW.season_key)=5
                    AND (SELECT count(*) FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid
                         WHERE i.account=NEW.account AND i.character_id=NEW.recipient_character_id AND s.character_id=NEW.recipient_character_id
                           AND ((json_extract(i.item_core,'$.ItemId')=490705120 AND json_extract(i.item_core,'$.Value')=3)
                             OR (json_extract(i.item_core,'$.ItemId')=15 AND json_extract(i.item_core,'$.Value')=1)))=2
                    AND (SELECT count(*) FROM inventory_audit_log WHERE character_id=NEW.recipient_character_id AND action_name='battle_pass_reward')=4
                  THEN RAISE(ABORT,'{batchMarker}') ELSE RAISE(ABORT,'BP batch prerequisites missing') END;
                END;
                """);
            bool batchLate = false;
            try { repository.CommitBattlePassReward(owner, granted, batchPlan); }
            catch (SqliteException error) when (error.SqliteErrorCode == 19 &&
                error.Message == $"SQLite Error 19: '{batchMarker}'.") { batchLate = true; }
            That(batchLate && Snapshot() == batchBefore && ReferenceEquals(session.Selected, granted),
                "last batch claim failure rolls back all four new claims, full inventory, audits, UID and both revisions");
            Sql("DROP TRIGGER fail_batch_claim");
            var batchCommitted = repository.CommitBattlePassReward(owner, granted, batchPlan);
            That(batchCommitted.State.Revision == 2 && batchCommitted.Character.Revision == granted.Revision + 1 &&
                batchCommitted.State.Claims.SetEquals(batchPlan.After.Claims) && batchCommitted.State.Claims.Count == 5 &&
                Totals(batchCommitted.Character.Inventory).SequenceEqual(new[] { (15, 1), (490705101, 3), (490705120, 3), (490705122, 1) }),
                "same frozen batch retries atomically after rollback and increments season only once for four keys");
            string afterBatch = Snapshot();
            bool staleBatch = false;
            try { repository.CommitBattlePassReward(owner, granted, batchPlan); }
            catch (BattlePassRewardChangedException) { staleBatch = true; }
            That(staleBatch && Snapshot() == afterBatch, "old batch plan refuses with no second grant after successful retry");
            var finalRepository = new CharacterRepository(path);
            var finalSession = new GameSession(finalRepository, towns, owner, timeProvider: new FixedClock());
            finalSession.Select(created.Slot); finalSession.MarkReady();
            var final = finalSession.ReadBattlePass(season)?.State ?? throw new InvalidOperationException("batch restoration missing");
            That(final.Revision == 2 && final.Claims.SetEquals(batchPlan.After.Claims) &&
                JsonSerializer.Serialize(finalSession.Selected!.Inventory) == JsonSerializer.Serialize(batchCommitted.Character.Inventory),
                "new repository/session restores complete atomic batch assets and all claim keys");
            finalSession.End();
            restoredSession.End();
            session.End();
            succeeded = true;

            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
                db.Open();
                return db;
            }
            void Sql(string sql, params object[] values)
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
                for (int i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i]);
                command.ExecuteNonQuery();
            }
            object?[][] Query(string sql)
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
                using var reader = command.ExecuteReader();
                var rows = new List<object?[]>();
                while (reader.Read())
                {
                    var row = new object?[reader.FieldCount];
                    for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
                return rows.ToArray();
            }
            string Snapshot() => JsonSerializer.Serialize(new[] {
                "SELECT * FROM characters ORDER BY id", "SELECT * FROM character_inventory_items ORDER BY item_uid",
                "SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index",
                "SELECT * FROM inventory_avatar_details ORDER BY item_uid", "SELECT * FROM inventory_creature_details ORDER BY item_uid",
                "SELECT * FROM account_wallets ORDER BY account", "SELECT * FROM account_currency_balances ORDER BY account,currency_kind",
                "SELECT * FROM inventory_audit_log ORDER BY audit_id", "SELECT * FROM currency_audit_log ORDER BY audit_id",
                "SELECT * FROM epic_piece_audit_log ORDER BY audit_id", "SELECT * FROM account_epic_books ORDER BY account",
                "SELECT * FROM account_battle_pass_seasons ORDER BY account,season_key",
                "SELECT * FROM account_battle_pass_claims ORDER BY account,season_key,level,track", "SELECT * FROM sqlite_sequence ORDER BY name"
            }.Select(Query).ToArray());
        }
        finally
        {
            if (succeeded) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.Error.WriteLine($"战令持久化回归失败，隔离现场保留于：{root}");
        }
    }
}
