using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Settings;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed class ClientSettingsRepository(string path) : IClientSettingsStore
{
    readonly SqliteGameDatabase database = new(path);
    public bool? SaveCloneTitle(AccountOwners.Lease owner, ushort characterId, uint itemId, long? expiresAt = null) => owner.Execute<bool?>(() =>
    {
        using var db = database.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT clone_title_item_id FROM characters WHERE id=$id AND account=$account";
        command.Parameters.AddWithValue("$id", characterId); command.Parameters.AddWithValue("$account", owner.Account);
        if (command.ExecuteScalar() is not long previous) throw new InvalidDataException("称号动画角色不属于当前账号");
        if (expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
        if (previous == itemId) return false;
        using var write = db.BeginWrite("ClientSettingsRepository.SaveCloneTitle");
        using var tx = write.Transaction; command.Transaction = tx;
        // 写者排队可能跨过限时称号到期边界，取得实际事务后必须复核。
        if (expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
        command.CommandText = "UPDATE characters SET clone_title_item_id=$item WHERE id=$id AND account=$account AND clone_title_item_id=$previous";
        command.Parameters.AddWithValue("$item", (long)itemId); command.Parameters.AddWithValue("$previous", previous);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("称号动画保存快照已变化");
        tx.Commit(); return true;
    });

    public bool SaveEmotion(AccountOwners.Lease owner, ushort characterId, ushort emotion) => owner.Execute(() =>
    {
        if (emotion > short.MaxValue) throw new InvalidDataException("心情编号越界");
        using var db = database.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT emotion FROM characters WHERE id=$id AND account=$account";
        command.Parameters.AddWithValue("$id", characterId); command.Parameters.AddWithValue("$account", owner.Account);
        if (command.ExecuteScalar() is not long previous) throw new InvalidDataException("心情角色不属于当前账号");
        if (previous == emotion) return false;
        using var write = db.BeginWrite("ClientSettingsRepository.SaveEmotion");
        using var tx = write.Transaction; command.Transaction = tx;
        command.CommandText = "UPDATE characters SET emotion=$emotion WHERE id=$id AND account=$account AND emotion=$previous";
        command.Parameters.AddWithValue("$emotion", emotion); command.Parameters.AddWithValue("$previous", previous);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("心情保存快照已变化");
        tx.Commit(); return true;
    });

    public ClientSettingsSnapshot Load(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT a.game_options,c.character_options,g.game_options,h.hotkeys,q0.quick_chat,q1.quick_chat,e.entries,n.entries,b.entries FROM characters r
            LEFT JOIN account_client_settings a ON a.account=r.account
            LEFT JOIN character_client_settings c ON c.character_id=r.id AND c.account=r.account
            LEFT JOIN character_game_settings g ON g.character_id=r.id AND g.account=r.account
            LEFT JOIN character_hotkey_settings h ON h.character_id=r.id AND h.account=r.account AND h.profile=CASE WHEN r.job=10 THEN 1 ELSE 0 END
            LEFT JOIN account_quick_chat q0 ON q0.account=r.account AND q0.bank=0
            LEFT JOIN account_quick_chat q1 ON q1.account=r.account AND q1.bank=1
            LEFT JOIN account_recent_emoticons e ON e.account=r.account
            LEFT JOIN character_quest_notify n ON n.character_id=r.id AND n.account=r.account
            LEFT JOIN character_npc_bookmarks b ON b.character_id=r.id AND b.account=r.account
            WHERE r.id=$id AND r.account=$account
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", owner.Account);
        ClientSettingsSnapshot snapshot;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidDataException("设置角色不属于当前账号");
            snapshot = new(Read(reader, 0), Read(reader, 1), Read(reader, 2), Read(reader, 3), Read(reader, 4), Read(reader, 5),
                RecentEmoticons: Read(reader, 6), QuestNotify: Read(reader, 7), NpcBookmarks: Read(reader, 8));
        }
        command.CommandText = "SELECT emoticon_id,macro FROM account_chat_emoticons WHERE account=$account ORDER BY emoticon_id";
        using var macros = command.ExecuteReader();
        var entries = ImmutableArray.CreateBuilder<ChatEmoticonMacro>();
        while (macros.Read()) entries.Add(new(checked((ushort)macros.GetInt32(0)), ImmutableArray.Create((byte[])macros.GetValue(1))));
        return snapshot with { ChatEmoticons = entries.ToImmutable() };
    });

    public bool SaveChatEmoticons(AccountOwners.Lease owner, ImmutableArray<ChatEmoticonMacro> macros) => owner.Execute(() =>
    {
        if (macros.IsDefaultOrEmpty) return false;
        using var db = database.Open();
        var pending = new List<ChatEmoticonMacro>();
        using (var read = db.BeginRead())
        using (var query = db.CreateCommand())
        {
            query.Transaction = read;
            query.CommandText = "SELECT emoticon_id,macro FROM account_chat_emoticons WHERE account=$account";
            query.Parameters.AddWithValue("$account", owner.Account);
            var existing = new Dictionary<ushort, byte[]>();
            using var reader = query.ExecuteReader();
            while (reader.Read()) existing.Add(checked((ushort)reader.GetInt32(0)), (byte[])reader.GetValue(1));
            pending.AddRange(macros.Where(row => !existing.TryGetValue(row.Id, out var previous) || !row.Text.AsSpan().SequenceEqual(previous)));
        }
        if (pending.Count == 0) return false;
        using var write = db.BeginWrite("ClientSettingsRepository.SaveChatEmoticons");
        using var tx = write.Transaction;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO account_chat_emoticons(account,emoticon_id,macro) VALUES($account,$id,$macro)
            ON CONFLICT(account,emoticon_id) DO UPDATE SET macro=excluded.macro WHERE macro<>excluded.macro
            """;
        command.Parameters.AddWithValue("$account", owner.Account);
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var text = command.Parameters.Add("$macro", SqliteType.Blob);
        bool changed = false;
        foreach (var row in pending)
        {
            id.Value = row.Id;
            text.Value = row.Text.ToArray();
            changed |= command.ExecuteNonQuery() != 0;
        }
        command.CommandText = "SELECT COUNT(*),COALESCE(SUM(length(macro)),0) FROM account_chat_emoticons WHERE account=$account";
        using var total = command.ExecuteReader();
        if (!total.Read() || total.GetInt64(0) > 256 || total.GetInt64(1) > 63470)
            throw new InvalidDataException("聊天表情配置超出通知帧容量");
        total.Close();
        tx.Commit();
        return changed;
    });

    public bool SaveRecentEmoticons(AccountOwners.Lease owner, ImmutableArray<byte> entries) => owner.Execute(() =>
    {
        using var db = database.Open();
        using (var read = db.BeginRead())
        using (var query = db.CreateCommand())
        {
            query.Transaction = read;
            query.CommandText = "SELECT e.entries FROM accounts a LEFT JOIN account_recent_emoticons e ON e.account=a.account_id WHERE a.account_id=$account";
            query.Parameters.AddWithValue("$account", owner.Account);
            using var reader = query.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("最近表情账号不存在");
            if (!reader.IsDBNull(0) && entries.AsSpan().SequenceEqual((byte[])reader.GetValue(0))) return false;
        }
        using var write = db.BeginWrite("ClientSettingsRepository.SaveRecentEmoticons");
        using var tx = write.Transaction;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO account_recent_emoticons(account,entries) VALUES($account,$entries)
            ON CONFLICT(account) DO UPDATE SET entries=excluded.entries
            """;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.Add("$entries", SqliteType.Blob).Value = entries.ToArray();
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("最近表情未提交");
        tx.Commit();
        return true;
    });

    public bool SaveHotkeys(AccountOwners.Lease owner, ushort characterId, byte profile, ImmutableArray<byte> text) => owner.Execute(() =>
    {
        using var db = database.Open();
        using (var read = db.BeginRead())
        using (var query = db.CreateCommand())
        {
            query.Transaction = read;
            query.CommandText = """
                SELECT h.hotkeys FROM characters r
                LEFT JOIN character_hotkey_settings h ON h.character_id=r.id AND h.account=r.account AND h.profile=$profile
                WHERE r.id=$id AND r.account=$account AND $profile=CASE WHEN r.job=10 THEN 1 ELSE 0 END
                """;
            query.Parameters.AddWithValue("$id", characterId);
            query.Parameters.AddWithValue("$profile", profile);
            query.Parameters.AddWithValue("$account", owner.Account);
            using var reader = query.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("键位角色不属于当前账号或职业配置不匹配");
            if (!reader.IsDBNull(0) && text.AsSpan().SequenceEqual((byte[])reader.GetValue(0))) return false;
        }
        using var write = db.BeginWrite("ClientSettingsRepository.SaveHotkeys");
        using var tx = write.Transaction;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_hotkey_settings(character_id,account,profile,hotkeys) VALUES($id,$account,$profile,$text)
            ON CONFLICT(character_id,profile) DO UPDATE SET hotkeys=excluded.hotkeys
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.Add("$text", SqliteType.Blob).Value = text.ToArray();
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("角色键位未提交");
        tx.Commit();
        return true;
    });

    public bool SaveQuickChat(AccountOwners.Lease owner, byte bank, ImmutableArray<byte> text) => owner.Execute(() =>
    {
        using var db = database.Open();
        using (var read = db.BeginRead())
        using (var query = db.CreateCommand())
        {
            query.Transaction = read;
            query.CommandText = """
                SELECT q.quick_chat FROM accounts a
                LEFT JOIN account_quick_chat q ON q.account=a.account_id AND q.bank=$bank
                WHERE a.account_id=$account
                """;
            query.Parameters.AddWithValue("$bank", bank);
            query.Parameters.AddWithValue("$account", owner.Account);
            using var reader = query.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("快捷聊天账号不存在");
            if (!reader.IsDBNull(0) && text.AsSpan().SequenceEqual((byte[])reader.GetValue(0))) return false;
        }
        using var write = db.BeginWrite("ClientSettingsRepository.SaveQuickChat");
        using var tx = write.Transaction;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO account_quick_chat(account,bank,quick_chat) VALUES($account,$bank,$text)
            ON CONFLICT(account,bank) DO UPDATE SET quick_chat=excluded.quick_chat
            """;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$bank", bank);
        command.Parameters.Add("$text", SqliteType.Blob).Value = text.ToArray();
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("账号快捷聊天未提交");
        tx.Commit();
        return true;
    });

    public bool SaveGameOptions(AccountOwners.Lease owner, ImmutableArray<byte> payload, ushort? characterId = null,
        ImmutableArray<byte> characterGameOptions = default) => owner.Execute(() =>
    {
        using var db = database.Open();
        var characterPayload = characterGameOptions.IsDefault ? payload : characterGameOptions;
        bool accountChanged, characterChanged;
        // 先读取已提交值；相同设置不申请 writer，也不补写默认行。
        using (var read = db.BeginRead())
        using (var command = db.CreateCommand())
        {
            command.Transaction = read;
            command.CommandText = """
                SELECT s.game_options,g.game_options,c.id FROM accounts a
                LEFT JOIN account_client_settings s ON s.account=a.account_id
                LEFT JOIN characters c ON c.id=$id AND c.account=a.account_id
                LEFT JOIN character_game_settings g ON g.character_id=c.id AND g.account=a.account_id
                WHERE a.account_id=$account
                """;
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$id", (object?)characterId ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidDataException("设置账号不存在");
            if (characterId.HasValue && reader.IsDBNull(2))
                throw new InvalidDataException("设置角色不属于当前账号");
            accountChanged = reader.IsDBNull(0) || !payload.AsSpan().SequenceEqual((byte[])reader.GetValue(0));
            characterChanged = characterId.HasValue && (reader.IsDBNull(1) || !characterPayload.AsSpan().SequenceEqual((byte[])reader.GetValue(1)));
        }
        if (!accountChanged && !characterChanged)
            return false;
        using var write = db.BeginWrite("ClientSettingsRepository.SaveGameOptions");
        using var tx = write.Transaction;
        using var update = db.CreateCommand();
        update.Transaction = tx;
        update.Parameters.AddWithValue("$account", owner.Account);
        if (accountChanged)
        {
            update.Parameters.Add("$payload", SqliteType.Blob).Value = payload.ToArray();
            update.CommandText = """
                INSERT INTO account_client_settings(account,game_options) VALUES($account,$payload)
                ON CONFLICT(account) DO UPDATE SET game_options=excluded.game_options
                """;
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidDataException("账号设置未提交");
        }
        if (characterChanged)
        {
            update.Parameters.AddWithValue("$id", characterId!.Value);
            update.Parameters.Add("$characterPayload", SqliteType.Blob).Value = characterPayload.ToArray();
            update.CommandText = """
                INSERT INTO character_game_settings(character_id,account,game_options) VALUES($id,$account,$characterPayload)
                ON CONFLICT(character_id) DO UPDATE SET game_options=excluded.game_options
                WHERE character_game_settings.account=excluded.account
                """;
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidDataException("角色主设置未提交");
        }
        tx.Commit();
        return true;
    });

    public bool SaveCharacterOptions(AccountOwners.Lease owner, ushort characterId, ImmutableArray<byte> payload) => owner.Execute(() =>
    {
        using var db = database.Open();
        using (var read = db.BeginRead())
        using (var command = db.CreateCommand())
        {
            command.Transaction = read;
            command.CommandText = """
                SELECT s.character_options FROM characters c
                LEFT JOIN character_client_settings s ON s.character_id=c.id AND s.account=c.account
                WHERE c.id=$id AND c.account=$account
                """;
            command.Parameters.AddWithValue("$id", characterId);
            command.Parameters.AddWithValue("$account", owner.Account);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidDataException("设置角色不属于当前账号");
            if (!reader.IsDBNull(0) && payload.AsSpan().SequenceEqual((byte[])reader.GetValue(0)))
                return false;
        }
        using var write = db.BeginWrite("ClientSettingsRepository.SaveCharacterOptions");
        using var tx = write.Transaction;
        using var update = db.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            INSERT INTO character_client_settings(character_id,account,character_options) VALUES($id,$account,$payload)
            ON CONFLICT(character_id) DO UPDATE SET character_options=excluded.character_options
            WHERE character_client_settings.account=excluded.account
            """;
        update.Parameters.AddWithValue("$id", characterId);
        update.Parameters.AddWithValue("$account", owner.Account);
        update.Parameters.Add("$payload", SqliteType.Blob).Value = payload.ToArray();
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidDataException("角色设置未提交");
        tx.Commit();
        return true;
    });

    public bool SaveQuestNotify(AccountOwners.Lease owner, ushort characterId, ImmutableArray<byte> entries) => owner.Execute(() =>
    {
        QuestNotifyRules.Validate(entries);
        using var db = database.Open();
        using (var read = db.BeginRead())
        using (var query = db.CreateCommand())
        {
            query.Transaction = read;
            query.CommandText = """
                SELECT n.entries FROM characters c
                LEFT JOIN character_quest_notify n ON n.character_id=c.id AND n.account=c.account
                WHERE c.id=$id AND c.account=$account
                """;
            query.Parameters.AddWithValue("$id", characterId);
            query.Parameters.AddWithValue("$account", owner.Account);
            using var reader = query.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("任务提示角色不属于当前账号");
            if (reader.IsDBNull(0) ? entries.All(value => value == 0) : entries.AsSpan().SequenceEqual((byte[])reader.GetValue(0)))
                return false;
        }
        using var write = db.BeginWrite("ClientSettingsRepository.SaveQuestNotify");
        using var tx = write.Transaction;
        using var update = db.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            INSERT INTO character_quest_notify(character_id,account,entries) VALUES($id,$account,$entries)
            ON CONFLICT(character_id) DO UPDATE SET entries=excluded.entries
            WHERE character_quest_notify.account=excluded.account
            """;
        update.Parameters.AddWithValue("$id", characterId);
        update.Parameters.AddWithValue("$account", owner.Account);
        update.Parameters.Add("$entries", SqliteType.Blob).Value = entries.ToArray();
        if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("任务提示书签未提交");
        tx.Commit();
        return true;
    });

    public bool SaveNpcBookmarks(AccountOwners.Lease owner, ushort characterId, ImmutableArray<byte> entries) => owner.Execute(() =>
    {
        ClientSettingsService.ValidateNpcBookmarks(entries);
        using var db = database.Open();
        using (var read = db.BeginRead())
        using (var query = db.CreateCommand())
        {
            query.Transaction = read;
            query.CommandText = """
                SELECT n.entries FROM characters c
                LEFT JOIN character_npc_bookmarks n ON n.character_id=c.id AND n.account=c.account
                WHERE c.id=$id AND c.account=$account
                """;
            query.Parameters.AddWithValue("$id", characterId);
            query.Parameters.AddWithValue("$account", owner.Account);
            using var reader = query.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("人物辞典收藏角色不属于当前账号");
            if (reader.IsDBNull(0) ? entries.IsEmpty : entries.AsSpan().SequenceEqual((byte[])reader.GetValue(0)))
                return false;
        }
        using var write = db.BeginWrite("ClientSettingsRepository.SaveNpcBookmarks");
        using var tx = write.Transaction;
        using var update = db.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            INSERT INTO character_npc_bookmarks(character_id,account,entries) VALUES($id,$account,$entries)
            ON CONFLICT(character_id) DO UPDATE SET entries=excluded.entries
            WHERE character_npc_bookmarks.account=excluded.account
            """;
        update.Parameters.AddWithValue("$id", characterId);
        update.Parameters.AddWithValue("$account", owner.Account);
        update.Parameters.Add("$entries", SqliteType.Blob).Value = entries.ToArray();
        if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("人物辞典收藏书签未提交");
        tx.Commit();
        return true;
    });

    static ImmutableArray<byte> Read(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? default : [.. (byte[])reader.GetValue(ordinal)];
}
