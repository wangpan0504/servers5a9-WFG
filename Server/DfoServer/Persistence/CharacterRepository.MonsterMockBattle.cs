using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.MonsterMockBattle;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IMonsterMockBattleStore
{
    public MockBattleAccount? ReadMockBattle(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        return ReadMockBattle(db, tx, owner.Account);
    });

    static MockBattleAccount? ReadMockBattle(SqliteConnection db, SqliteTransaction tx, string account)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT revision,state FROM account_monster_mock_battle WHERE account=$account";
        command.Parameters.AddWithValue("$account", account);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadMockBattle(reader.GetInt64(0), reader.GetString(1));
    }

    static MockBattleAccount ReadMockBattle(long revision, string json)
    {
        var state = JsonSerializer.Deserialize<MockBattleAccount>(json)
            ?? throw new InvalidDataException("怪物模拟战账号状态为空");
        if (state.Revision != revision || revision <= 0 || state.HeartAt < 0)
            throw new InvalidDataException("怪物模拟战状态版本或时钟无效");
        state.Snapshot.Validate();
        return state;
    }

    public MockBattleCommit CommitMockBattle(AccountOwners.Lease owner, Character expected, MockBattlePlan plan) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitMockBattle");
        using var tx = write.Transaction;
        var actual = ReadMockBattle(db, tx, owner.Account);
        if ((actual?.Revision ?? 0) != plan.Before.Revision)
            throw new MockBattleRejectedException("活动状态已改变");
        var schedule = ServerEventRepository.Read(db, tx, ServerEventService.NolanVisibilityId);
        if (schedule != plan.Schedule || !schedule.IsActive(plan.At))
            throw new MockBattleRejectedException("活动开关或时间表已改变");
        // 费用、奖励、角色revision与活动状态共用原背包事务；SQL失败全部回滚。
        var character = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        var after = plan.Mutation.After with { Revision = checked(plan.Before.Revision + 1) };
        after.Snapshot.Validate();
        WriteMockBattle(db, tx, owner.Account, plan.Before.Revision, after);
        if (plan.Mail is { } mail && DeliverMailInTransaction(db, tx, mail).Error != MailboxError.None)
            throw new InvalidOperationException("怪物模拟战奖励邮件投递失败");
        tx.Commit();
        return new MockBattleCommit(after, character);
    });

    static void WriteMockBattle(SqliteConnection db, SqliteTransaction tx, string account, long before, MockBattleAccount after)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO account_monster_mock_battle(account,revision,display_name,lineup_stars,state)
            VALUES($account,$revision,$name,$stars,$state)
            ON CONFLICT(account) DO UPDATE SET revision=excluded.revision,display_name=excluded.display_name,
              lineup_stars=excluded.lineup_stars,state=excluded.state WHERE revision=$before
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$revision", after.Revision);
        command.Parameters.AddWithValue("$before", before);
        command.Parameters.AddWithValue("$name", after.DisplayName);
        command.Parameters.AddWithValue("$stars", after.Snapshot.Cards.Where(card => card.Position >= 0).Sum(card => card.Stars));
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(after));
        if (command.ExecuteNonQuery() != 1) throw new MockBattleRejectedException("活动状态提交冲突");
    }

    static void CommitMockBattleAdmission(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        Character character, DfoServer.Game.Dungeons.DungeonEntryCharge charge)
    {
        if (charge.MockBattle is not { } entry) return;
        if (entry.World.DungeonId != charge.DungeonId || entry.Before.Revision !=
            (ReadMockBattle(db, tx, owner.Account)?.Revision ?? 0))
            throw new MockBattleRejectedException("入场卡册版本已改变");
        var paid = entry.Paid with { Revision = checked(entry.Before.Revision + 1), DisplayName = character.Name };
        WriteMockBattle(db, tx, owner.Account, entry.Before.Revision, paid);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.CommandText = "UPDATE account_monster_mock_battle_runs SET state=2 WHERE account=$account AND state=0";
        command.ExecuteNonQuery();
        command.Parameters.AddWithValue("$run", charge.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$character", character.Id);
        command.Parameters.AddWithValue("$world", entry.World.Id);
        command.Parameters.AddWithValue("$at", entry.At.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$revision", paid.Revision);
        command.CommandText = """
            INSERT INTO account_monster_mock_battle_runs(run_id,account,character_id,world_id,opened_at,paid_revision,state)
            VALUES($run,$account,$character,$world,$at,$revision,0)
            """;
        command.ExecuteNonQuery();
    }

    (Character Character, uint Mail) CommitMockBattleVictory(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        Character character, SinglePlayerDungeonRun run, PreparedDungeonSettlement prepared)
    {
        if (run.Entry.MockBattle is not { } admission) return (character, 0);
        if (prepared.MockBattle is not { } victory || !ReferenceEquals(admission, victory.Admission) ||
            victory.Before.Revision != admission.Before.Revision + 1 ||
            ReadMockBattle(db, tx, owner.Account)?.Revision != victory.Before.Revision)
            throw new InvalidOperationException("怪物模拟战胜利缺少原入场卡册与冻结奖励");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$run", prepared.Fact.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$character", character.Id);
        command.Parameters.AddWithValue("$world", admission.World.Id);
        command.Parameters.AddWithValue("$revision", victory.Before.Revision);
        command.CommandText = """
            UPDATE account_monster_mock_battle_runs SET state=1
            WHERE run_id=$run AND account=$account AND character_id=$character AND world_id=$world
              AND paid_revision=$revision AND state=0
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("怪物模拟战实例已结算或入场身份不符");
        WriteMockBattle(db, tx, owner.Account, victory.Before.Revision,
            victory.After with { Revision = checked(victory.Before.Revision + 1) });
        if (victory.Inventory.Changed) character = CommitInventoryInTransaction(db, tx, owner, character, victory.Inventory);
        if (victory.Mail is not { } mail) return (character, 0);
        var delivery = DeliverMailInTransaction(db, tx, mail);
        if (delivery.Error != MailboxError.None) throw new InvalidOperationException("怪物模拟战通关奖励邮件投递失败");
        return (character, delivery.MailId);
    }

    static uint? ReadMockBattleVictoryMail(SqliteConnection db, SqliteTransaction tx, PreparedDungeonSettlement prepared)
    {
        if (prepared.MockBattle?.Mail is not { } mail) return null;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT mail_id FROM mailbox_messages WHERE delivery_key=$key";
        command.Parameters.AddWithValue("$key", mail.Key);
        return command.ExecuteScalar() is long id ? checked((uint)id) : null;
    }

    public ImmutableArray<MockBattleOpponent> FindMockBattleOpponents(string account, string? name, int stars, int limit, string? after)
    {
        if (limit is < 1 or > 3 || stars is < 0 or > 25 || name?.Length > 32)
            throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = database.Open();
        using var tx = db.BeginRead();
        var opponents = ImmutableArray.CreateBuilder<MockBattleOpponent>();
        var selected = new HashSet<string>(StringComparer.Ordinal);
        void Query(int bucket, string? cursor)
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = name is not null ? """
                SELECT m.account,a.name,m.revision,m.state FROM account_adventures a
                JOIN account_monster_mock_battle m ON m.account=a.account
                WHERE m.account<>$account AND m.lineup_stars>0 AND a.name_initialized=1
                  AND a.name=$name COLLATE NOCASE ORDER BY m.account LIMIT $limit
                """ : """
                SELECT m.account,a.name,m.revision,m.state FROM account_monster_mock_battle m
                JOIN account_adventures a ON a.account=m.account
                WHERE m.account<>$account AND m.lineup_stars=$stars AND m.account>$after AND a.name_initialized=1
                ORDER BY m.account LIMIT $limit
                """;
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$stars", bucket);
            command.Parameters.AddWithValue("$after", cursor ?? "");
            command.Parameters.AddWithValue("$name", name ?? "");
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read() && opponents.Count < limit)
                if (selected.Add(reader.GetString(0))) opponents.Add(new(reader.GetString(0), reader.GetString(1),
                    ReadMockBattle(reader.GetInt64(2), reader.GetString(3))));
        }
        if (name is not null) Query(0, null);
        else
        {
            Query(stars, after);
            if (opponents.Count < limit && after is not null) Query(stars, null);
        }
        return opponents.ToImmutable();
    }
}
