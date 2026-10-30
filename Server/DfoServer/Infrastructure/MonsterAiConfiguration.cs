using System.Text.Json;
using System.Text.Json.Serialization;
using DfoServer.Game.Dungeons;

namespace DfoServer.Infrastructure;

// 配置文件只在启动或显式GM重载时读取；解析失败保留整个旧快照。
internal sealed class MonsterAiConfiguration(string dataDirectory)
{
    public DungeonMonsterAiSettings Settings { get; } = new();
    sealed record Document
    {
        public bool Enabled { get; init; }
        public MonsterAiRule[] Rules { get; init; } = [];
    }
    public void Reload()
    {
        string path = Path.Combine(dataDirectory, "monster-ai.json");
        FileStream stream;
        try { stream = File.OpenRead(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            Settings.Replace([], false);
            return;
        }
        using var input = stream;
        if (stream.Length > 65536) throw new InvalidDataException("怪物AI配置不能超过64KiB");
        var document = JsonSerializer.Deserialize<Document>(stream, new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("怪物AI配置不能为空");
        Settings.Replace(document.Rules ?? throw new InvalidDataException("怪物AI规则集合不能为空"), document.Enabled);
    }

    public string? Answer(string line)
    {
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words[0] != "monster-ai") return null;
        if (words.Length != 2 || words[1] is not ("reload" or "status"))
            return "ERR usage: monster-ai status|reload";
        try
        {
            if (words[1] == "reload") Reload();
            var snapshot = Settings.Current;
            return "OK " + JsonSerializer.Serialize(new { enabled = snapshot.Enabled, rules = snapshot.Rules,
                effective = "new-rooms", allDungeons = true, clientProtocol = 2, healthDefense = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return "ERR " + error.Message; }
    }
}
