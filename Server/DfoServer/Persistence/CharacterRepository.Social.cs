using DfoServer.Game.Social;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : ISocialStore
{
    public AccountFriendGroup[] AccountGroups(SocialActor actor) => actor.Execute(() =>
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT slot,icon,name FROM account_social_groups WHERE account=$account ORDER BY slot";
        query.Parameters.AddWithValue("$account", actor.Owner.Account);
        var groups = new List<AccountFriendGroup>(4);
        using var row = query.ExecuteReader();
        while (row.Read())
        {
            if (row.GetInt32(0) != groups.Count) throw new InvalidDataException("冒险团分组槽位不连续");
            groups.Add(new(checked((byte)row.GetInt32(1)), row.GetString(2)));
        }
        if (groups.Count == 0) return AccountFriendGroup.Defaults();
        var result = groups.ToArray();
        if (!AccountFriendGroup.Valid(result)) throw new InvalidDataException("冒险团分组存档非法或不完整");
        return result;
    });
    public void SaveAccountGroups(SocialActor actor, AccountFriendGroup[] groups)
    {
        // 防止调用方在校验与事务之间替换数组元素；记录本身不可变。
        groups = groups.ToArray();
        if (!AccountFriendGroup.Valid(groups)) throw new InvalidDataException("冒险团分组存档输入非法");
        actor.Execute(() =>
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.SaveAccountGroups");
            using var tx = write.Transaction;
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.Parameters.AddWithValue("$account", actor.Owner.Account);
            command.CommandText = "DELETE FROM account_social_groups WHERE account=$account";
            command.ExecuteNonQuery();
            var slot = command.Parameters.Add("$slot", SqliteType.Integer);
            var icon = command.Parameters.Add("$icon", SqliteType.Integer);
            var name = command.Parameters.Add("$name", SqliteType.Text);
            command.CommandText = "INSERT INTO account_social_groups(account,slot,icon,name) VALUES($account,$slot,$icon,$name)";
            for (int i = 0; i < groups.Length; i++)
            {
                slot.Value = i; icon.Value = groups[i].Icon; name.Value = groups[i].Name;
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("冒险团分组提交失败");
            }
            tx.Commit();
            return true;
        });
    }
    const string SocialPersonColumns = "c.account,c.id,c.name,c.level,c.job,c.grow_type,COALESCE(a.name,''),COALESCE(a.level,1)";
    const string AccountPersonColumns = "x.account_id,COALESCE(c.id,0),COALESCE(c.name,''),COALESCE(c.level,1),COALESCE(c.job,0),COALESCE(c.grow_type,0),COALESCE(a.name,''),COALESCE(a.level,1)";
    static SocialPerson ReadSocialPerson(SqliteDataReader row) => new(row.GetString(0), checked((ushort)row.GetInt32(1)),
        row.GetString(2), checked((byte)row.GetInt32(3)), checked((byte)row.GetInt32(4)), checked((byte)row.GetInt32(5)),
        row.GetString(6), row.GetInt32(7));
    static SocialPerson? FindSocialPerson(SqliteConnection db, SqliteTransaction? tx, string name, bool adventure)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = adventure
            ? $"SELECT {AccountPersonColumns} FROM accounts x JOIN account_adventures a ON a.account=x.account_id " +
                "LEFT JOIN characters c ON c.id=(SELECT c2.id FROM characters c2 WHERE c2.account=x.account_id ORDER BY c2.slot,c2.id LIMIT 1) WHERE a.name=$name"
            : $"SELECT {SocialPersonColumns} FROM characters c LEFT JOIN account_adventures a ON a.account=c.account WHERE c.name=$name";
        query.Parameters.AddWithValue("$name", name);
        using var row = query.ExecuteReader();
        return row.Read() ? ReadSocialPerson(row) : null;
    }
    public SocialPerson? FindSocialPerson(string name, bool adventure = false)
    {
        using var db = database.Open();
        return FindSocialPerson(db, null, name, adventure);
    }
    public SocialLink[] CharacterLinks(ushort owner, bool blocked)
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = $"SELECT {SocialPersonColumns},l.created,l.memo FROM character_social_links l JOIN characters c ON c.id=l.target_id " +
            "LEFT JOIN account_adventures a ON a.account=c.account WHERE l.owner_id=$owner AND l.kind=$kind ORDER BY l.created,c.id";
        query.Parameters.AddWithValue("$owner", owner);
        query.Parameters.AddWithValue("$kind", blocked ? 1 : 0);
        var rows = new List<SocialLink>();
        using var row = query.ExecuteReader();
        while (row.Read()) rows.Add(new(ReadSocialPerson(row), DateTimeOffset.FromUnixTimeSeconds(row.GetInt64(8)), row.GetString(9)));
        return rows.ToArray();
    }
    public SocialResult ChangeCharacterLink(SocialActor actor, string name, bool blocked, bool remove, string memo, DateTimeOffset now) => actor.Execute(() =>
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ChangeCharacterLink");
        using var tx = write.Transaction;
        var target = FindSocialPerson(db, tx, name, false);
        if (target is null) return new SocialResult(blocked ? (ushort)76 : (ushort)21);
        if (target.Id == actor.Character.Id) return new SocialResult(3);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$owner", actor.Character.Id);
        command.Parameters.AddWithValue("$target", target.Id);
        command.Parameters.AddWithValue("$kind", blocked ? 1 : 0);
        command.CommandText = "SELECT count(*) FROM character_social_links WHERE owner_id=$owner AND target_id=$target AND kind=$kind";
        bool exists = Convert.ToInt64(command.ExecuteScalar()) != 0;
        if (remove)
        {
            if (!exists) return new SocialResult(blocked ? (ushort)75 : (ushort)5);
            command.CommandText = "DELETE FROM character_social_links WHERE owner_id=$owner AND target_id=$target AND kind=$kind";
        }
        else
        {
            if (exists) return new SocialResult(blocked ? (ushort)74 : (ushort)0, target);
            command.CommandText = "SELECT count(*) FROM character_social_links WHERE owner_id=$owner AND kind=$kind";
            if (Convert.ToInt64(command.ExecuteScalar()) >= (blocked ? 100 : 64)) return new SocialResult(blocked ? (ushort)78 : (ushort)4);
            command.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$memo", memo);
            command.CommandText = "INSERT INTO character_social_links(owner_id,target_id,kind,created,memo) VALUES($owner,$target,$kind,$created,$memo)";
        }
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("社交关系提交失败");
        if (blocked)
        {
            command.CommandText = remove
                ? "UPDATE characters SET blacklist_count=max(blacklist_count-1,0) WHERE id=$target"
                : "UPDATE characters SET blacklist_count=min(blacklist_count+1,2147483647) WHERE id=$target";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("被拉黑计分同步失败");
        }
        tx.Commit();
        return new SocialResult(0, target);
    });

    public SocialLink[] AccountFriends(string account)
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = $"SELECT {AccountPersonColumns},l.created,l.memo,l.group_id FROM account_social_links l " +
            "JOIN accounts x ON x.account_id=l.target_account " +
            "LEFT JOIN characters c ON c.id=(SELECT c2.id FROM characters c2 WHERE c2.account=x.account_id ORDER BY c2.slot,c2.id LIMIT 1) " +
            "LEFT JOIN account_adventures a ON a.account=x.account_id WHERE l.owner_account=$account ORDER BY l.created,l.target_account";
        query.Parameters.AddWithValue("$account", account);
        var rows = new List<SocialLink>();
        using var row = query.ExecuteReader();
        while (row.Read()) rows.Add(new(ReadSocialPerson(row), DateTimeOffset.FromUnixTimeSeconds(row.GetInt64(8)), row.GetString(9), checked((byte)row.GetInt32(10))));
        return rows.ToArray();
    }

    public IReadOnlyDictionary<string, long> AccountLastSeenUnix(IEnumerable<string> accounts)
    {
        string[] distinct = accounts.Where(account => !string.IsNullOrEmpty(account))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0) return new Dictionary<string, long>(StringComparer.Ordinal);
        using var db = database.Open();
        using var query = db.CreateCommand();
        var placeholders = new string[distinct.Length];
        for (int i = 0; i < distinct.Length; i++)
        {
            placeholders[i] = "$account" + i;
            query.Parameters.AddWithValue(placeholders[i], distinct[i]);
        }
        query.CommandText = $"SELECT account,last_seen_unix FROM account_social_presence WHERE account IN ({string.Join(',', placeholders)})";
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        using var row = query.ExecuteReader();
        while (row.Read()) result[row.GetString(0)] = row.GetInt64(1);
        return result;
    }
    public AccountFriendRequest[] AccountRequests(string account)
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = $"SELECT {AccountPersonColumns},r.source_account,r.message,r.created FROM account_social_requests r " +
            "JOIN accounts x ON x.account_id=CASE WHEN r.source_account=$account THEN r.target_account ELSE r.source_account END " +
            "LEFT JOIN characters c ON c.id=(SELECT c2.id FROM characters c2 WHERE c2.account=x.account_id ORDER BY c2.slot,c2.id LIMIT 1) " +
            "LEFT JOIN account_adventures a ON a.account=x.account_id WHERE r.source_account=$account OR r.target_account=$account ORDER BY r.created";
        query.Parameters.AddWithValue("$account", account);
        var rows = new List<AccountFriendRequest>();
        using var row = query.ExecuteReader();
        while (row.Read()) rows.Add(new(ReadSocialPerson(row), row.GetString(8) == account, row.GetString(9), row.GetInt64(10)));
        return rows.ToArray();
    }

    public SocialResult RequestAccountFriend(SocialActor actor, string adventure, string message, byte group, string memo, DateTimeOffset now) => actor.Execute(() =>
    {
        if (group > 4) return new SocialResult(3);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.RequestAccountFriend");
        using var tx = write.Transaction;
        var target = FindSocialPerson(db, tx, adventure, true);
        if (target is null) return new SocialResult(3);
        if (target.Account == actor.Owner.Account) return new SocialResult(3);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$owner", actor.Owner.Account);
        command.Parameters.AddWithValue("$target", target.Account);
        command.Parameters.AddWithValue("$sender", actor.Character.Id);
        command.CommandText = "SELECT 1 FROM character_social_links l JOIN characters c ON c.id=l.owner_id WHERE c.account=$target AND l.target_id=$sender AND l.kind=1";
        if (command.ExecuteScalar() is not null) return new SocialResult(3);
        command.CommandText = "SELECT count(*) FROM account_social_links WHERE owner_account=$owner AND target_account=$target";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) return new SocialResult(4);
        command.CommandText = "SELECT count(*) FROM account_social_requests WHERE source_account=$owner OR target_account=$owner";
        if (Convert.ToInt64(command.ExecuteScalar()) >= 100) return new SocialResult(4);
        command.CommandText = "SELECT count(*) FROM account_social_requests WHERE source_account=$target OR target_account=$target";
        if (Convert.ToInt64(command.ExecuteScalar()) >= 100) return new SocialResult(4);
        command.CommandText = "SELECT count(*) FROM account_social_requests WHERE source_account=$owner AND target_account=$target OR source_account=$target AND target_account=$owner";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) return new SocialResult(4);
        command.CommandText = "INSERT INTO account_social_requests(source_account,target_account,message,created,memo,group_id) VALUES($owner,$target,$message,$created,$memo,$group)";
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$memo", memo);
        command.Parameters.AddWithValue("$group", group);
        command.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        tx.Commit();
        return new SocialResult(0, target);
    });

    public SocialResult ResolveAccountFriend(SocialActor actor, string adventure, bool accept, bool remove, bool cancel, string memo = "", byte group = 0) => actor.Execute(() =>
    {
        if (group > 4) return new SocialResult(3);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ResolveAccountFriend");
        using var tx = write.Transaction;
        var target = FindSocialPerson(db, tx, adventure, true);
        if (target is null || target.Account == actor.Owner.Account) return new SocialResult(3);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$owner", actor.Owner.Account);
        command.Parameters.AddWithValue("$target", target.Account);
        if (remove)
            command.CommandText = "DELETE FROM account_social_links WHERE owner_account=$owner AND target_account=$target OR owner_account=$target AND target_account=$owner";
        else
        {
            command.CommandText = cancel
                ? "SELECT created,memo,group_id FROM account_social_requests WHERE source_account=$owner AND target_account=$target"
                : "SELECT created,memo,group_id FROM account_social_requests WHERE source_account=$target AND target_account=$owner";
            long created; string senderMemo; byte senderGroup;
            using (var pending = command.ExecuteReader())
            {
                if (!pending.Read()) return new SocialResult(3);
                created = pending.GetInt64(0); senderMemo = pending.GetString(1); senderGroup = checked((byte)pending.GetInt32(2));
            }
            if (accept)
            {
                command.CommandText = "SELECT count(*) FROM account_social_links WHERE owner_account=$owner OR owner_account=$target GROUP BY owner_account";
                using (var counts = command.ExecuteReader())
                    while (counts.Read()) if (counts.GetInt64(0) >= 100) return new SocialResult(4);
                command.CommandText = "INSERT INTO account_social_links(owner_account,target_account,created,memo,group_id) VALUES($owner,$target,$created,$memo,$group),($target,$owner,$created,$senderMemo,$senderGroup)";
                command.Parameters.AddWithValue("$created", created);
                command.Parameters.AddWithValue("$memo", memo);
                command.Parameters.AddWithValue("$group", group);
                command.Parameters.AddWithValue("$senderMemo", senderMemo);
                command.Parameters.AddWithValue("$senderGroup", senderGroup);
                command.ExecuteNonQuery();
            }
            command.CommandText = cancel
                ? "DELETE FROM account_social_requests WHERE source_account=$owner AND target_account=$target"
                : "DELETE FROM account_social_requests WHERE source_account=$target AND target_account=$owner";
        }
        if (command.ExecuteNonQuery() == 0) return new SocialResult(3);
        tx.Commit();
        return new SocialResult(0, target);
    });

    public SocialResult UpdateSocialMemo(SocialActor actor, string name, string memo, bool blocked, byte group = 0) => actor.Execute(() =>
    {
        if (!blocked && group > 4) return new SocialResult(3);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.UpdateSocialMemo");
        using var tx = write.Transaction;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = blocked
            ? "UPDATE character_social_links SET memo=$memo WHERE owner_id=$owner AND kind=1 AND target_id=(SELECT id FROM characters WHERE name=$name)"
            : "UPDATE account_social_links SET memo=$memo,group_id=$group WHERE owner_account=$owner AND target_account=(SELECT account FROM account_adventures WHERE name=$name)";
        command.Parameters.AddWithValue("$memo", memo);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$owner", blocked ? (object)actor.Character.Id : actor.Owner.Account);
        command.Parameters.AddWithValue("$group", group);
        if (command.ExecuteNonQuery() != 1) return new SocialResult(blocked ? (ushort)75 : (ushort)3);
        tx.Commit();
        return new SocialResult(0);
    });
    public HashSet<ushort> BlockingCharacters(ushort sender)
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT owner_id FROM character_social_links WHERE target_id=$target AND kind=1";
        query.Parameters.AddWithValue("$target", sender);
        var rows = new HashSet<ushort>();
        using var row = query.ExecuteReader();
        while (row.Read()) rows.Add(checked((ushort)row.GetInt32(0)));
        return rows;
    }
    public bool IsBlocked(ushort recipient, ushort sender)
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT 1 FROM character_social_links WHERE owner_id=$owner AND target_id=$target AND kind=1";
        query.Parameters.AddWithValue("$owner", recipient);
        query.Parameters.AddWithValue("$target", sender);
        return query.ExecuteScalar() is not null;
    }
    public string[] SocialObservers(ushort character, string account)
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT c.account FROM character_social_links l JOIN characters c ON c.id=l.owner_id WHERE l.target_id=$id AND l.kind=0 " +
            "UNION SELECT owner_account FROM account_social_links WHERE target_account=$account";
        query.Parameters.AddWithValue("$id", character);
        query.Parameters.AddWithValue("$account", account);
        var rows = new List<string>();
        using var row = query.ExecuteReader();
        while (row.Read()) rows.Add(row.GetString(0));
        return rows.ToArray();
    }
}
