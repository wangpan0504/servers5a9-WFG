using System.Text.Json;
using System.Text;

namespace DfoServer.Infrastructure;

internal sealed class ServerLog(string directory, TextWriter? output = null, bool fileEnabled = true,
    TextWriter? failureOutput = null) : IDisposable
{
    readonly BufferedLogFile? file = fileEnabled
        ? new(Path.Combine(directory, "server.jsonl"), error => Console.Error.WriteLine("Server log failed: " + error.Message)) : null;
    readonly TextWriter? console = output is null ? null : TextWriter.Synchronized(output);
    int consoleFailed;
    int failureReported;

    public void Write(string kind, object detail)
    {
        if (file is null && console is null)
        {
            // 关闭日志也保留首个启动失败，不为普通事件分配JSON、写文件或输出包体。
            if ((kind is "server_failed" or "listener_failed") && Interlocked.CompareExchange(ref failureReported, 1, 0) == 0)
            {
                using var failure = JsonSerializer.SerializeToDocument(detail);
                string failureMessage = ConsoleMessage(kind, failure.RootElement).Replace('\r', ' ').Replace('\n', ' ');
                if (failureMessage.Length > 2048) failureMessage = failureMessage[..2045] + "...";
                try { (failureOutput ?? Console.Error).WriteLine(failureMessage); }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            }
            return;
        }
        var time = DateTimeOffset.Now;
        string line = JsonSerializer.Serialize(new { Time = time, Kind = kind, Detail = detail }) + "\n";
        file?.Append(line);
        if (console is null || Volatile.Read(ref consoleFailed) != 0 || !ShowsOnConsole(kind))
            return;

        using var json = JsonDocument.Parse(line);
        var fields = json.RootElement.GetProperty("Detail");
        string level = kind is "server_failed" or "listener_failed" ? "ERROR" :
            kind is "unimplemented" or "resource_warning" or "closed" or "auth_rejected" or "owner_rejected" or "db_busy" or "db_slow" or "battle_pass_default_unavailable" ||
            kind.EndsWith("_rejected", StringComparison.Ordinal) || kind.EndsWith("_failed", StringComparison.Ordinal) ||
            fields.TryGetProperty("Accepted", out var accepted) && accepted.ValueKind == JsonValueKind.False ? "WARN" : "INFO";
        string message = ConsoleMessage(kind, fields);
        try
        {
            string prefix = $"[{time:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{kind}] ";
            // 多行启动摘要一次写入，每行保留同一事件时间和前缀。
            console.WriteLine(prefix + message.Replace("\n", "\n" + prefix));
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // 控制台管道关闭不改变连接或原文件日志的生命周期。
            if (Interlocked.Exchange(ref consoleFailed, 1) == 0)
                Console.Error.WriteLine("Console log disabled: " + error.Message);
        }
    }

    static string ConsoleMessage(string kind, JsonElement fields)
    {
        string Value(string name) => fields.TryGetProperty(name, out var value) ? Scalar(value) : "unknown";
        if (kind == "startup")
        {
            string stage = Value("Stage");
            if (stage == "Configuration")
                return $"Configuration loaded. PID={Value("Pid")}\n" +
                    $"Listen address: {Value("ListenAddress")}; advertised address: {Value("AdvertisedAddress")}\n" +
                    $"Channel port: {Value("ChannelPort")}; game port: {Value("GamePort")}\n" +
                    $"Script.pvf: {Value("PvfPath")}\nDatabase: {Value("Database")}\n" +
                    $"Log directory: {Value("LogDirectory")}; dungeon entry probe: {Value("DevelopmentDungeonEntryProbe")}";
            if (stage == "Database schema")
                return $"Database schema: v{Value("Version")} (OK).";
            if (stage == "Command registration")
                return $"Command registration: OK; commands={Value("Commands")}.";
            if (stage == "PVF archive") stage = "Script.pvf";
            return Value("State") == "starting" ? $"Loading {stage}..." : $"{stage}: OK ({Value("ElapsedMs")} ms).";
        }
        if (kind == "listening")
            return $"Channel {Value("ChannelName")} listening on TCP {Value("Channel")}\n" +
                $"Game server listening on TCP {Value("Game")}\nAdvertised game endpoint: {Value("AdvertisedGame")}\n" +
                $"Native transport listening on TCP {Value("Native")}\n" +
                $"GM control: {Value("Gm")}\nGM web: {Value("GmWeb")}\nClient web/recharge: {Value("RechargeWeb")}\nGM packet queries: {Value("GmPacketQueries")}\nPacket HTTP: {Value("PacketHttp")}\nPacket capture: {Value("PacketCapture")}\nServer log: {Value("ServiceLog")}";
        if (kind == "server_ready")
        {
            // 版本/提交号/存档版本只有真正启动的宿主会带；自测等精简调用点维持原来的一行。
            if (!fields.TryGetProperty("Commit", out _)) return $"Server ready. PID={Value("Pid")}.";
            string commit = Value("Commit");
            return $"Server ready. PID={Value("Pid")} Version={Value("ServerVersion")} " +
                $"Commit={(commit.Length == 0 ? "unknown" : commit)} SaveVersion=v{Value("SaveVersion")}.";
        }
        if (kind == "stopped") return $"Server stopped. PID={Value("Pid")}.";
        if (kind is "server_failed" or "listener_failed")
            return $"Server failed at {Value("Stage")}: {Value("Reason")}: {Value("Message")}";
        if (kind == "connected")
            return $"{Value("Protocol")} client connected: Connection={Value("Connection")} Peer={Value("Peer")}";
        if (kind == "disconnected")
            return $"{Value("Protocol")} client disconnected: Connection={Value("Connection")}";
        if (kind == "unimplemented")
        {
            int opcode = fields.GetProperty("Opcode").GetInt32();
            string body = Value("Body");
            return $"Unhandled packet: Connection={Value("Connection")} Phase={Value("Phase")} " +
                $"Kind=0x{fields.GetProperty("Kind").GetInt32():X2} Opcode={opcode} (0x{opcode:X4}) " +
                $"Name={Value("Name")} Length={Value("Length")} Body={(body.Length == 0 ? "empty" : body)}";
        }
        string title = kind switch
        {
            "selected" => "Character selected", "authenticated" => "Authentication accepted",
            "character_deleted" => "Character deleted", "character_delete_rejected" => "Character deletion rejected",
            "auth_rejected" => "Authentication rejected", "owner_rejected" => "Account owner rejected",
            "closed" => "Connection closed", "db_busy" => "Database busy", "db_slow" => "Database slow",
            "battle_pass_default_unavailable" => "Default battle pass unavailable; server continues without battle pass",
            "channel" => "Channel handshake completed",
            "pcroom_reward" => "PC room reward committed", "pcroom_reward_rejected" => "PC room reward rejected",
            "daily_attendance_claimed" => "Daily attendance reward committed",
            "returning_adventurer_claim" => "Returning adventurer reward committed",
            "returning_adventurer_rejected" => "Returning adventurer reward rejected",
            "pcroom_weapon" => "PC room weapon rented", "pcroom_weapon_rejected" => "PC room rental rejected",
            "fatigue_acceleration" => "Fatigue acceleration selection",
            "fatigue_acceleration_rejected" => "Fatigue acceleration selection rejected",
            "resource_warning" => "Resource warning", "exit" => "Exit request", "travel" => "Town travel",
            "room_exit" => "Room exit", "dungeon_entry" => "Dungeon selection",
            "formal_dungeon_entry" => "Dungeon entry", "formal_dungeon_settlement" => "Dungeon settlement",
            "formal_tournament_reward_selected" => "Tournament card selection",
            "formal_tournament_reward_failed" => "Tournament card reward failed",
            "formal_quest_continuation_selection" => "Quest continuation selection",
            "formal_character_death" => "Dungeon character death", "formal_character_revive" => "Dungeon character revive",
            "dungeon_selection_return" => "Dungeon selection return", "formal_dungeon_return" => "Returned to town",
            "quest_lifecycle" => "Quest update", "quest_completion" => "Quest completion", "gm" => "GM command",
            "gm_notice" => "GM notice",
            "fatigue_daily_reset" => "Daily fatigue restored",
            "fatigue_daily_reset_failed" => "Daily fatigue restore failed",
            "consumable_use" => "Consumable use",
            "consumable_disguise" => "Consumable disguise",
            "chat_send" => "Chat message", "chat_delivered" => "Chat delivered", "chat_throttled" => "Chat throttled",
            "chat_whisper_offline" => "Whisper target offline", "chat_megaphone_rejected" => "Megaphone rejected",
            "chat_rejected" => "Chat rejected", "chat_blocked" => "Chat blocked",
            "item_hyperlink" => "Item hyperlink",
            "class_change" => "Class change",
            "growup_change" => "Grow up change",
            "creature_hatch" => "Creature egg hatch", "enhancement" => "Equipment enhancement",
            "chronicle_enchant" => "Chronicle enchant",
            "booster_open" => "Package open",
            "random_option_change" => "Equipment random option change",
            "equipment_texture_change" => "Equipment texture change",
            "charm_charging" => "Charm energy charging",
            "cash_shop_buy" => "Cash shop purchase",
            "avatar_dye" => "Avatar dye",
            "avatar_socket" => "Avatar socket opening",
            "avatar_emblem" => "Avatar emblem embedding",
            _ => kind.Replace('_', ' ')
        };
        return title + ": " + DetailText(fields);
    }

    static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Replace("\r", "\\r").Replace("\n", "\\n"),
        JsonValueKind.Null => "none",
        JsonValueKind.Object or JsonValueKind.Array => "empty",
        _ => value.ToString()
    };

    static string DetailText(JsonElement fields)
    {
        var text = new StringBuilder();
        foreach (var field in fields.EnumerateObject()) Append(field.Name, field.Value);
        return text.ToString();

        void Append(string name, JsonElement value)
        {
            // 保留嵌套业务字段及原路径，用点号展开，控制台不输出JSON对象或转义路径。
            if (value.ValueKind == JsonValueKind.Object)
            {
                bool any = false;
                foreach (var child in value.EnumerateObject()) { Append(name + "." + child.Name, child.Value); any = true; }
                if (any) return;
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var child in value.EnumerateArray()) Append($"{name}[{index++}]", child);
                if (index != 0) return;
            }
            if (text.Length != 0) text.Append(' ');
            text.Append(name).Append('=').Append(Scalar(value));
        }
    }

    static bool ShowsOnConsole(string kind) => kind is
        "startup" or "server_failed" or "listener_failed" or "listening" or "server_ready" or "stopped" or "resource_warning" or "battle_pass_default_unavailable" or
        "connected" or "disconnected" or "closed" or "db_busy" or "db_slow" or "channel" or "authenticated" or "auth_rejected" or "owner_rejected" or
        "selected" or "fatigue_daily_reset" or "pcroom_reward" or "daily_attendance_claimed" or "returning_adventurer_claim" or "pcroom_weapon" or "fatigue_acceleration" or "character_deleted" or "exit" or "travel" or "room_exit" or "dungeon_entry" or "formal_dungeon_entry" or
        "formal_dungeon_settlement" or "formal_tournament_reward_selected" or "formal_character_death" or "formal_character_revive" or "dungeon_selection_return" or "formal_dungeon_return" or
        "formal_quest_continuation" or "formal_quest_continuation_selection" or "dungeon_entry_probe" or "dungeon_probe_return" or
        "quest_lifecycle" or "quest_completion" or "consumable_use" or "consumable_disguise" or "class_change" or "growup_change" or "cube_contract_select" or "quest_quick_clear" or "quest_manual_complete" or "quest_hidden_open" or
        "quest_ticket_use" or "creature_hatch" or "enhancement" or "chronicle_enchant" or "booster_open" or "random_option_change" or "equipment_texture_change" or "charm_charging" or "cash_shop_buy" or "avatar_dye" or "avatar_socket" or "avatar_emblem" or "gm" or "unimplemented" or "raid_rejoin_notice" or "raid_rejoin_candidate" ||
        kind.EndsWith("_rejected", StringComparison.Ordinal) || kind.EndsWith("_failed", StringComparison.Ordinal);

    public void Flush() => file?.Flush();
    public void Dispose() => file?.Dispose();
}
