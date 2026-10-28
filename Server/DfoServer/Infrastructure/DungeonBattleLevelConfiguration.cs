using System.Text.Json;
using System.Text.Json.Serialization;
using DfoServer.Game.Dungeons;

namespace DfoServer.Infrastructure;

// 配置文件只在启动或显式GM重载时读取；解析失败保留整个旧快照。
internal sealed class DungeonBattleLevelConfiguration(string dataDirectory)
{
    public DungeonBattleLevelSettings Settings { get; } = new();
    sealed record Document
    {
        public DungeonBattleLevelRule[] Rules { get; init; } = [];
    }
    public void Reload()
    {
        string path = Path.Combine(dataDirectory, "monster-battle-level.json");
        FileStream stream;
        try { stream = File.OpenRead(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            Settings.Replace([]);
            return;
        }
        using var input = stream;
        if (stream.Length > 65536) throw new InvalidDataException("怪物等级配置不能超过64KiB");
        var document = JsonSerializer.Deserialize<Document>(stream, new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("怪物等级配置不能为空");
        Settings.Replace(document.Rules ?? throw new InvalidDataException("怪物等级规则集合不能为空"));
    }

    public string? Answer(string line)
    {
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words[0] != "monster-battle-level") return null;
        if (words.Length != 2 || words[1] is not ("reload" or "status"))
            return "ERR usage: monster-battle-level status|reload";
        try
        {
            if (words[1] == "reload") Reload();
            return "OK " + JsonSerializer.Serialize(new { rules = Settings.Current.Rules, effective = "new-rooms" });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return "ERR " + error.Message; }
    }
}
