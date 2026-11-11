using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SinglePlayerDungeonRunTests
{
    static void VerifyHelperLanding()
    {
        byte[] grid = new byte[30 * 30];
        var selected = HelperHellLanding.Pick(248, 248, 30, 30, grid);
        That(selected == new DungeonLanding(152, 248), "柱旁优先固定水平安全格，距离柱子96像素");
        grid[15 * 30 + 9] = 2;
        That(HelperHellLanding.Pick(248, 248, 30, 30, grid) == new DungeonLanding(344, 248), "阻挡左侧时选择右侧，不落入非零通行格");
        Array.Fill(grid, (byte)2);
        That(HelperHellLanding.Pick(248, 248, 30, 30, grid) is null &&
            HelperHellLanding.Pick(0, 0, 1, 1, [0]) is null, "没有九格安全区或超出地图时保留原生落点");

        var names = new[] { "[tile]", "../tile/base.til", "[pass type]", "[tile files]", "[tile map]",
            "[virtual movable area]", "[/virtual movable area]", "[force map width]", "", "tile/extended.til" };
        string Resolve(int value) => names[value];
        var map = new DungeonMap("map", new(0, 0), 1, "map/test/hell.map");
        var room = new DungeonRoomDefinition(map, [], [], []) { SpecialObjects = [new(0, 99, [99, 152, 152, 0, 1],
            "passiveobject/pillar.obj", [new(0, "[hellparty]", [], null)])] };
        PvfToken[] mapTokens = [new(3, 0), new(6, 1), new(3, 3), new(3, 4)];
        PvfToken[] Read(string path) => path == "map/tile/base.til"
            ? [new(3, 2), .. Enumerable.Repeat(new PvfToken(0, 0), 14 * 20)] : [];
        That(HelperHellLanding.Read(room, mapTokens, Resolve, Read, null) == new DungeonLanding(56, 152),
            "从真实相对tile引用和pass type构造几何，不用默认门口坐标");
        That(HelperHellLanding.Read(room with { SpecialObjects = [] }, mapTokens, Resolve, Read, null) is null &&
            HelperHellLanding.Read(room, mapTokens, Resolve, _ => [], null) is null,
            "无柱子或缺失tile时不猜落点");
        PvfToken[] rectangles = [.. mapTokens, new(3, 7), new(0, 448), new(3, 5),
            new(0, 0), new(0, 0), new(0, 448), new(0, 320), new(3, 6)];
        That(HelperHellLanding.Read(room, rectangles, Resolve, _ => [], null) == new DungeonLanding(56, 152),
            "静态虚拟活动矩形沿原生覆盖通行语义，不受旧tile阻挡值误拦");
        That(HelperHellLanding.Read(room, rectangles[..^1], Resolve, Read, null) is null &&
            HelperHellLanding.Read(room, [.. rectangles, new(3, 5), new(0, 1)], Resolve, Read, null) is null,
            "虚拟矩形缺闭合或重复声明不能授权安全落点");
        PvfToken[] blankExtended = [new(3, 0), new(6, 1), new(6, 8), new(3, 3), new(0, 0), new(6, 9),
            new(3, 4), new(0, 0), new(0, -1)];
        var lowerPillar = room with { SpecialObjects = [room.SpecialObjects[0] with { RawValues = [99, 152, 380, 0, 1] }] };
        PvfToken[] Extended(string path) => path == "map/test/tile/extended.til"
            ? [new(3, 2), .. Enumerable.Repeat(new PvfToken(0, 2), 140), .. Enumerable.Repeat(new PvfToken(0, 0), 280)] : Read(path);
        That(HelperHellLanding.Read(lowerPillar, blankExtended, Resolve, Extended, null) == new DungeonLanding(152, 280),
            "空地板槽与扩展-1仍阻挡，420值扩展地板仅取首140，不误用尾部可行值");

        var body = MonsterAiPackets.Encode(7, 8, 9, 10, new(2, 3), MonsterAiProfile.Native, new(152, 248));
        That(body.Length == 96 && body.AsSpan(0, 4).SequenceEqual("MAI4"u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(80)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(84)) == 152 &&
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(88)) == 248 &&
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(92)) == 0 && body.AsSpan(24, 56).IndexOfAnyExcept((byte)0) < 0,
            "MAI4独立字节合同：七项原生参数、单次标记、地面XYZ无随机范围");
        Reject<ArgumentOutOfRangeException>(() => MonsterAiPackets.Encode(7, 8, 9, 10, new(2, 3),
            MonsterAiProfile.Native, new(-1, 0)), "拒绝越界落点");
        That(MonsterAiPackets.Encode(7, 8, 9, 10, new(2, 3), MonsterAiProfile.Native).Length == 56,
            "无落点时旧版包体和普通加载合同保持");
    }
}
