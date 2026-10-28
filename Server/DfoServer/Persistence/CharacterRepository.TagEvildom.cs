using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.TagEvildom;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : ITagEvildomConfigurationStore
{
    public bool MatchesTagEvildomCharacters(AccountOwners.Lease owner, TagEvildomCharacters expected) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        // 发布/提交guard只查身份、版本与有效契约，不重新装载背包、技能或其它完整资产。
        command.CommandText = """
            SELECT COALESCE((SELECT revision FROM account_tag_evildom_configuration WHERE account=$account),0)=$configuration
              AND EXISTS(SELECT 1 FROM characters WHERE account=$account AND id=$primary AND revision=$primaryRevision AND clone_title_item_id=$primaryTitle)
              AND EXISTS(SELECT 1 FROM characters WHERE account=$account AND id=$standby AND revision=$standbyRevision AND clone_title_item_id=$standbyTitle)
              AND COALESCE((SELECT revision FROM account_wallets WHERE account=$account),0)=$walletRevision
            """;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$configuration", expected.Projection.State.Configuration.Revision);
        command.Parameters.AddWithValue("$primary", expected.Primary.Id);
        command.Parameters.AddWithValue("$primaryRevision", expected.Primary.Revision);
        command.Parameters.AddWithValue("$standby", expected.Standby.Id);
        command.Parameters.AddWithValue("$standbyRevision", expected.Standby.Revision);
        command.Parameters.AddWithValue("$primaryTitle", expected.Primary.CloneTitleItemId);
        command.Parameters.AddWithValue("$standbyTitle", expected.Standby.CloneTitleItemId);
        command.Parameters.AddWithValue("$walletRevision", expected.Standby.Inventory.AccountWallet.Revision);
        bool matches = command.ExecuteScalar() is 1L &&
            ReadEffectiveExpertContractExpiry(db, expected.Primary.Id, tx) == expected.Primary.Learning.ExpertContractExpiresAt &&
            ReadEffectiveExpertContractExpiry(db, expected.Standby.Id, tx) == expected.Standby.Learning.ExpertContractExpiresAt;
        tx.Commit();
        return matches;
    });

    public TagEvildomConfiguration ReadTagEvildomConfiguration(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadTagEvildomConfiguration(db, null, owner.Account);
    });

    public TagEvildomState ReadTagEvildomState(AccountOwners.Lease owner, TagEvildomPeriod period) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var state = ReadTagEvildomState(db, tx, owner.Account, period);
        tx.Commit();
        return state;
    });

    static TagEvildomState ReadTagEvildomState(SqliteConnection db, SqliteTransaction? tx, string account, TagEvildomPeriod period)
    {
        var configuration = ReadTagEvildomConfiguration(db, tx, account);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT day,week,first,second FROM account_tag_evildom_clearances WHERE account=$account AND (day=$day OR week=$week)";
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$day", period.Day);
        command.Parameters.AddWithValue("$week", period.Week);
        var protectedMembers = ImmutableHashSet.CreateBuilder<ushort>();
        int used = 0;
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (reader.GetInt32(1) == period.Week) used++;
                if (reader.GetInt32(0) != period.Day) continue;
                protectedMembers.Add(checked((ushort)reader.GetInt32(2)));
                protectedMembers.Add(checked((ushort)reader.GetInt32(3)));
            }
        // 跨过06点仍未明确退场或通关的旧准入只占原周次数，不伪造通关、保护或奖励。
        command.CommandText = "SELECT count(*) FROM account_tag_evildom_challenges WHERE account=$account AND week=$week AND day<$day AND state=0";
        used += checked((int)(long)command.ExecuteScalar()!);
        command.CommandText = "SELECT dungeon_id FROM account_tag_evildom_challenges WHERE account=$account ORDER BY sequence DESC LIMIT 1";
        int mode = command.ExecuteScalar() is long dungeon && dungeon == 400000095 ? 4 : 3;
        if (used > 3 || protectedMembers.Count > 6) throw new InvalidDataException("再战魔界通关事实超出本版上限");
        return new(configuration, used, protectedMembers.ToImmutable()) { Mode = mode };
    }

    static TagEvildomConfiguration ReadTagEvildomConfiguration(SqliteConnection db, SqliteTransaction? tx, string account)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT revision,selected_team,first0,second0,first1,second1,first2,second2
            FROM account_tag_evildom_configuration WHERE account=$account
            """;
        command.Parameters.AddWithValue("$account", account);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return TagEvildomConfiguration.Empty;
        var teams = ImmutableArray.CreateBuilder<TagEvildomTeam>(3);
        for (int index = 0; index < 3; index++)
            teams.Add(new(checked((ushort)reader.GetInt32(2 + index * 2)), checked((ushort)reader.GetInt32(3 + index * 2))));
        var configuration = new TagEvildomConfiguration(reader.GetInt64(0), reader.GetInt32(1), teams.MoveToImmutable());
        configuration.Validate();
        return configuration;
    }

    public TagEvildomConfiguration CommitTagEvildomConfiguration(AccountOwners.Lease owner,
        TagEvildomConfiguration expected, TagEvildomConfiguration desired, TagEvildomPeriod? period = null) => owner.Execute(() =>
    {
        expected.Validate();
        desired.Validate();
        if (expected.Revision != desired.Revision) throw new InvalidOperationException("再战魔界配置规划版本不一致");
        using var db = database.Open();
        var current = ReadTagEvildomConfiguration(db, null, owner.Account);
        RequireTagEvildomConfiguration(current, expected);
        // 合法重复保存仍核对 owner 和配置版本，但不占写锁、不推进角色或配置 revision。
        if (current.SameAssignments(desired)) return current;
        using var write = db.BeginWrite("CharacterRepository.CommitTagEvildomConfiguration");
        using var tx = write.Transaction;
        current = ReadTagEvildomConfiguration(db, tx, owner.Account);
        RequireTagEvildomConfiguration(current, expected);
        var state = ReadTagEvildomState(db, tx, owner.Account, period ?? TagEvildomPeriod.At(DateTimeOffset.UtcNow));
        for (int index = 0; index < 3; index++)
        {
            var previous = current.Teams[index];
            var next = desired.Teams[index];
            if ((state.ProtectedMembers.Contains(previous.First) || state.ProtectedMembers.Contains(previous.Second)) && next != previous ||
                new[] { next.First, next.Second }.Any(member => state.ProtectedMembers.Contains(member) && !previous.Contains(member)))
                throw new InvalidOperationException("再战魔界当天已领奖角色不能变更编队");
        }
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT level,EXISTS(SELECT 1 FROM account_pvp_private_characters p WHERE p.character_id=c.id)
            FROM characters c WHERE c.account=$account AND c.id=$id
            """;
        query.Parameters.AddWithValue("$account", owner.Account);
        var id = query.Parameters.Add("$id", SqliteType.Integer);
        foreach (var team in desired.Teams)
        {
            foreach (ushort member in new[] { team.First, team.Second })
            {
                if (member == 0) continue;
                id.Value = member;
                using var reader = query.ExecuteReader();
                if (!reader.Read() || reader.GetInt32(0) < 95 || reader.GetBoolean(1))
                    throw new InvalidOperationException("再战魔界编队角色归属或资格已变化");
            }
        }
        var committed = WriteTagEvildomConfiguration(db, tx, owner.Account, expected, desired);
        tx.Commit();
        return committed;
    });

    static void RequireTagEvildomConfiguration(TagEvildomConfiguration current, TagEvildomConfiguration expected)
    {
        if (current.Revision != expected.Revision || !current.SameAssignments(expected))
            throw new InvalidOperationException("再战魔界编队配置已变化");
    }

    static TagEvildomConfiguration WriteTagEvildomConfiguration(SqliteConnection db, SqliteTransaction tx, string account,
        TagEvildomConfiguration expected, TagEvildomConfiguration desired)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO account_tag_evildom_configuration(account,revision,selected_team,first0,second0,first1,second1,first2,second2)
            VALUES($account,$revision,$selected,$first0,$second0,$first1,$second1,$first2,$second2)
            ON CONFLICT(account) DO UPDATE SET revision=excluded.revision,selected_team=excluded.selected_team,
              first0=excluded.first0,second0=excluded.second0,first1=excluded.first1,second1=excluded.second1,
              first2=excluded.first2,second2=excluded.second2
            WHERE account_tag_evildom_configuration.revision=$before
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$revision", checked(expected.Revision + 1));
        command.Parameters.AddWithValue("$before", expected.Revision);
        command.Parameters.AddWithValue("$selected", desired.SelectedTeam);
        for (int index = 0; index < 3; index++)
        {
            command.Parameters.AddWithValue("$first" + index, desired.Teams[index].First);
            command.Parameters.AddWithValue("$second" + index, desired.Teams[index].Second);
        }
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("再战魔界配置提交冲突");
        return desired with { Revision = expected.Revision + 1 };
    }

    // 删角在原删除事务里清理整支双角色队伍；旧列表索引或复用槽位不能指向新角色。
    static void RemoveTagEvildomCharacter(SqliteConnection db, SqliteTransaction tx, string account, ushort character)
    {
        var current = ReadTagEvildomConfiguration(db, tx, account);
        if (!current.Teams.Any(team => team.Contains(character))) return;
        var desired = current with { Teams = current.Teams.Select(team => team.Contains(character) ? default : team).ToImmutableArray() };
        WriteTagEvildomConfiguration(db, tx, account, current, desired);
    }
}
