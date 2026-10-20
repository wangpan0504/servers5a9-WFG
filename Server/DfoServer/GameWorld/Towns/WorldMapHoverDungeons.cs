namespace DfoServer.GameWorld.Towns;

// N 键世界地图「魔界」羊皮纸 region/minimap/ashy_graveyard.mm 的 [world] 节点里，
// 哈林 49 的 [dungeon] 含 -1 无条件入口，客户端本就能列出副本名。
// 封锁区 77、夜之摩天楼 148、东哈林 153 的主 [dungeon] 块没有 -1，
// 选角时用 NOTI5 把这些地下城编号交给客户端做悬浮开通。
internal static class WorldMapHoverDungeons
{
    internal static readonly int[] MapIds = [77, 148, 153];

    internal static int[] DungeonIds(TownCatalog towns)
    {
        ArgumentNullException.ThrowIfNull(towns);
        var ids = new SortedSet<int>();
        foreach (int mapId in MapIds)
        {
            if (!towns.DungeonMaps.TryGetValue(mapId, out var map) || map.Error is not null)
                continue;
            foreach (var option in map.Options)
                if (option.DungeonId > 0)
                    ids.Add(option.DungeonId);
        }
        return [.. ids];
    }
}
