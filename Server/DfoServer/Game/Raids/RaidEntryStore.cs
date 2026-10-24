using System.Text.Json;

namespace DfoServer.Game.Raids;

// 时段表存在服务数据目录，不进角色库。重新打开同一个目录会读回上次写入的表。
internal sealed class RaidEntryStore
{
    readonly string path;
    readonly object gate = new();
    Dictionary<string, List<RaidEntrySchedule.Window>> tables = Empty();

    public RaidEntryStore(string directory)
    {
        path = Path.Combine(directory, "gm-raid-windows.json");
        tables = LoadFile();
    }

    public string ReadJson()
    {
        lock (gate) return Serialize(tables);
    }

    public bool IsOpen(string raid, DateTimeOffset instant)
    {
        lock (gate) return RaidEntrySchedule.IsOpen(tables[raid], instant);
    }

    public void Replace(string json)
    {
        var next = Parse(json);
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, Serialize(next));
            File.Move(temp, path, true);
            tables = next;
        }
    }

    public static Dictionary<string, List<RaidEntrySchedule.Window>> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("时段表须为对象");
        var next = Empty();
        foreach (var raid in RaidEntrySchedule.Names)
        {
            if (!document.RootElement.TryGetProperty(raid, out var rows)) continue;
            if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException(raid + " 须为数组");
            foreach (var row in rows.EnumerateArray())
            {
                int weekday = row.GetProperty("weekday").GetInt32();
                string start = row.GetProperty("start").GetString() ?? "";
                string end = row.GetProperty("end").GetString() ?? "";
                if (!RaidEntrySchedule.TryParse(start, end, weekday, out var window, out string error))
                    throw new InvalidDataException(error);
                next[raid].Add(window);
            }
        }
        return next;
    }

    Dictionary<string, List<RaidEntrySchedule.Window>> LoadFile()
    {
        try
        {
            if (!File.Exists(path)) return Empty();
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or KeyNotFoundException) { return Empty(); }
    }

    static string Serialize(Dictionary<string, List<RaidEntrySchedule.Window>> source) =>
        JsonSerializer.Serialize(source.ToDictionary(pair => pair.Key, pair => pair.Value.Select(window => new
        {
            weekday = window.Weekday,
            start = Clock(window.StartMinute),
            end = Clock(window.EndMinute)
        })));

    static string Clock(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    static Dictionary<string, List<RaidEntrySchedule.Window>> Empty() =>
        RaidEntrySchedule.Names.ToDictionary(name => name, _ => new List<RaidEntrySchedule.Window>());

    public void Reload()
    {
        lock (gate) tables = LoadFile();
    }
}
