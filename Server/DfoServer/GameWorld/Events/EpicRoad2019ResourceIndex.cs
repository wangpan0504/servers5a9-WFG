using PvfLib;

namespace DfoServer.GameWorld.Events;

// 同期官方目录确认的登记，仅由活动启用入口显式恢复；不修改归档或覆盖已有编号。
internal static class EpicRoad2019ResourceIndex
{
    internal static IReadOnlyDictionary<int, string> Restore(PvfArchive pvf, string root,
        IReadOnlyDictionary<int, string> index, bool relativePaths = false)
    {
        (int Id, string Path)[] entries = root switch
        {
            "town" => [(123, "chn_tayberrsroad.twn")],
            "npc" => [(400001102, "chn_tayberrs_road_arthur1.npc"),
                (400001103, "chn_tayberrs_road_arthur2.npc"), (400001105, "chn_tayberrs_road_blossom.npc")],
            "worldmap" => [(204, "chn_tayberrsroad.wdm")],
            "n_quest" => [(8712, "gcontents/191210_tayberrs_road/tayberrs_road_enter.qst")],
            _ => throw new ArgumentOutOfRangeException(nameof(root))
        };
        var restored = index.ToDictionary();
        foreach (var (id, relative) in entries)
        {
            string path = root + "/" + relative;
            string value = relativePaths ? relative : path;
            if (restored.TryGetValue(id, out var original) && original != value)
                throw new InvalidDataException("星河裂缝补充目录与原登记冲突: " + root + "/" + id);
            if (!pvf.Contains(path))
                throw new FileNotFoundException("星河裂缝活动资源缺失", path);
            restored[id] = value;
        }
        return restored.AsReadOnly();
    }
}
