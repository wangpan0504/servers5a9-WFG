using System.Text.Json;
using System.Text.Json.Serialization;
using DfoServer.Game.Dungeons;

namespace DfoServer.Infrastructure;

internal sealed class DungeonDropConfiguration(string dataDirectory)
{
    public DungeonDropControlSettings Settings { get; } = new();
    public Func<int, DungeonDropScope, byte, int, int, string>? Inspect { get; set; }
    static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<DungeonDropScope>(allowIntegerValues: false) }
    };
    sealed record Document(bool Enabled, DungeonDropControlRule[] Rules);
    public void Reload()
    {
        var path = Path.Combine(dataDirectory, "drop-control.json");
        FileStream stream;
        try { stream = File.OpenRead(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { Settings.Replace([], false); return; }
        using var input = stream;
        if (stream.Length > 65536) throw new InvalidDataException("掉落配置不能超过64KiB");
        var document = JsonSerializer.Deserialize<Document>(stream, Json) ?? throw new InvalidDataException("掉落配置不能为空");
        Settings.Replace(document.Rules ?? throw new InvalidDataException("掉落规则不能为空"), document.Enabled);
    }
    public string? Answer(string line)
    {
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words[0] != "drop-control") return null;
        try
        {
            if (words.Length == 7 && words[1] == "inspect" && int.TryParse(words[2], out var dungeon) && dungeon > 0 &&
                Enum.TryParse<DungeonDropScope>(words[3], out var scope) && Enum.IsDefined(scope) && byte.TryParse(words[4], out var difficulty) &&
                int.TryParse(words[5], out var monster) && int.TryParse(words[6], out var page))
                return Inspect?.Invoke(dungeon, scope, difficulty, monster, page) ?? "ERR drop inspection unavailable";
            if (words.Length != 2 || words[1] is not ("reload" or "status")) return "ERR usage: drop-control status|reload|inspect dungeon scope difficulty monster page";
            if (words[1] == "reload") Reload();
            var snapshot = Settings.Current;
            return "OK " + JsonSerializer.Serialize(new { enabled = snapshot.Enabled, rules = snapshot.Rules,
                effective = "new-entries", probabilityCap = 1, version = 1 }, Json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or KeyNotFoundException or ArgumentException)
        { return "ERR " + error.Message; }
    }
}
