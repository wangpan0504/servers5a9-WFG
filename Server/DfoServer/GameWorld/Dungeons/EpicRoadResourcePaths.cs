using System.Collections.Immutable;

namespace DfoServer.GameWorld.Dungeons;

// DGN 的地图编号/坐标与保留 MAP 的反向 dungeon 声明交叉核对后的复现映射。
// 原始 LST 未保留这些登记；此表不证明原始索引，也不决定路线、奖励或活动准入。
internal static class EpicRoadResourcePaths
{
    internal static ImmutableDictionary<int, string> Dungeons { get; } = new Dictionary<int, string>
    {
        [400000097] = "dungeon/gcontents/191210_tayberrs_road/01_easy_400000097.dgn",
        [400000098] = "dungeon/gcontents/191210_tayberrs_road/02_hard_400000098.dgn"
    }.ToImmutableDictionary();

    internal static ImmutableDictionary<int, string> Maps { get; } = new Dictionary<int, string>
    {
        [400000457] = "map/gcontents/191210_tayberrs_road/400000457_400000097_(0,0).map",
        [400000458] = "map/gcontents/191210_tayberrs_road/400000458_400000097_(1,0).map",
        [400000459] = "map/gcontents/191210_tayberrs_road/400000459_400000097_(2,0).map",
        [400000460] = "map/gcontents/191210_tayberrs_road/400000460_400000097_(0,0).map",
        [400000461] = "map/gcontents/191210_tayberrs_road/400000461_400000097_(1,0).map",
        [400000462] = "map/gcontents/191210_tayberrs_road/400000462_400000097_(2,0).map",
        [400000463] = "map/gcontents/191210_tayberrs_road/hard/400000463_400000098_(0,0).map",
        [400000464] = "map/gcontents/191210_tayberrs_road/hard/400000464_400000098_(1,0).map",
        [400000465] = "map/gcontents/191210_tayberrs_road/hard/400000465_400000098_(2,0).map",
        [400000618] = "map/gcontents/191210_tayberrs_road/hard/400000618_400000098_(1,0).map",
        [400000619] = "map/gcontents/191210_tayberrs_road/hard/400000619_400000098_(2,0).map"
    }.ToImmutableDictionary();
}
