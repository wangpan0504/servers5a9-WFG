using DfoServer.GameWorld.Pvp;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpArenaCatalogTests
{
    public static void RunCore()
    {
        int before = Count;
        var catalog = Fixture();
        That(catalog.MapOrder.SequenceEqual([50042, 50000]) && catalog.IsMapIndex(0) &&
            catalog.IsMapIndex(1) && !catalog.IsMapIndex(2) && !catalog.IsMapIndex(50042),
            "地图序号保留声明顺序，与排序结果及脚本ID严格区分");
        That(catalog.CanEnter(PvpArenaEligibility.Ordinary, 0, 0) &&
            !catalog.CanEnter(PvpArenaEligibility.Matching, 0, 0) &&
            catalog.CanEnter(PvpArenaEligibility.Matching, 0, 1) &&
            !catalog.CanEnter(PvpArenaEligibility.Ordinary, 0, 1), "普通与匹配资格按各自原表解释");
        That(catalog.CanEnter(PvpArenaEligibility.Free, 9, 1) &&
            !catalog.CanEnter(PvpArenaEligibility.Tournament, 9, 1) &&
            catalog.CanEnter(PvpArenaEligibility.Tournament, 3, 2) &&
            !catalog.CanEnter(PvpArenaEligibility.Free, 3, 2), "自由及大会资格不借用普通或匹配池");
        foreach (var table in new[] { new[] { 0 }, new[] { -1, 0 }, new[] { 0, 256 }, new[] { 0, 0, 0, 0 } })
            Reject<InvalidDataException>(() => new PvpArenaCatalog(Parameters(table), Maps(), Rules()), "残缺、越界或重复职业配对拒绝");
        foreach (var order in new[] { Array.Empty<int>(), new[] { 50000, 50000 }, new[] { 50001 } })
            Reject<InvalidDataException>(() => new PvpArenaCatalog(Parameters(), Maps(order), Rules()), "空地图、重复及缺失引用拒绝");
        Reject<InvalidDataException>(() => new PvpArenaCatalog(Doc(), Maps(), Rules()), "缺少准入表不能当作全部职业开放");
        Reject<InvalidDataException>(() => new PvpArenaCatalog(Parameters(), Doc("[pvp map order]", 50000,
            "[/pvp map order]", "[pvp map basic data]", 50000, 1, "[/pvp map basic data]"), Rules()), "地图基础行截断拒绝");
        That(catalog.BattleDuration == TimeSpan.FromMilliseconds(91000), "普通局时长按资源毫秒单位读取");
        That(catalog.BattleDurationFor(8) == TimeSpan.FromSeconds(240) &&
            catalog.BattleDurationFor(24) == TimeSpan.FromSeconds(240) &&
            catalog.BattleDurationFor(0) == TimeSpan.FromSeconds(91) &&
            catalog.BattleDurationFor(4) == TimeSpan.FromSeconds(91) &&
            catalog.BattleDurationFor(24, true) == TimeSpan.FromSeconds(180) &&
            catalog.BattleDurationFor(0, true) == TimeSpan.FromSeconds(120), "四种战斗时长独立装配，匹配与传统频道采用兼容大会列");
        foreach (int duration in new[] { 0, -1, 3_600_001 })
            Reject<InvalidDataException>(() => new PvpArenaCatalog(Parameters(), Maps(), Rules(duration)), "战斗时长边界拒绝");
        Reject<InvalidDataException>(() => new PvpArenaCatalog(Parameters(), Maps(), Doc()), "缺少普通局时长拒绝");
        Console.WriteLine($"PASS {Count - before} 决斗场独立准入表及地图引用断言");
    }

    public static void RunResources(PvfArchive pvf)
    {
        int before = Count;
        PvpRatingTests.RunResources(pvf);
        var catalog = DfoServer.Testing.SelfTestCatalogs.PvpArena(pvf);
        That(catalog.MapOrder.SequenceEqual([50000, 50042, 50041, 50036, 50025, 50035]), "真实PVF六张普通地图按原顺序装配");
        That(catalog.BattleDuration == TimeSpan.FromMinutes(3), "真实PVF普通局为180秒");
        That(catalog.CanEnter(PvpArenaEligibility.Ordinary, 10, 0) &&
            !catalog.CanEnter(PvpArenaEligibility.Matching, 10, 0), "缔造者普通准入不意味着可匹配");
        That(catalog.CanEnter(PvpArenaEligibility.Free, 9, 1) &&
            !catalog.CanEnter(PvpArenaEligibility.Ordinary, 9, 1), "自由表独有黑暗武士配对不回灌普通表");
        That(catalog.CanEnter(PvpArenaEligibility.Matching, 9, 0) &&
            !catalog.CanEnter(PvpArenaEligibility.Tournament, 9, 0), "匹配与大会职业范围独立");
        That(catalog.CanEnter(PvpArenaEligibility.Ordinary, 15, 4) &&
            !catalog.CanEnter(PvpArenaEligibility.Matching, 15, 4), "普通表后续职业不被旧职业范围截断");
        Console.WriteLine($"PASS {Count - before} 真实PVF决斗场地图与四份资格表断言");
    }

    internal static PvpArenaCatalog Fixture(int[]? ordinary = null) => new(Parameters(ordinary), Maps(), Rules());
    static PvfTokenDocument Rules(int milliseconds = 91000) => Doc("[pvp normal battle time]", milliseconds,
        "[pvp normal battle time for tournament]", 240000, "[pvp relay battle time]", 120000,
        "[pvp relay battle time for tournament]", 180000);
    static PvfTokenDocument Parameters(int[]? ordinary = null) => Doc([
        "[pvp enterable character]", ..(ordinary ?? [0, 0]).Cast<object>(), "[/pvp enterable character]",
        "[pvp matching possible character]", 0, 1, "[/pvp matching possible character]",
        "[free pvp enterable character]", 9, 1, "[/free pvp enterable character]",
        "[tournament enterable character]", 3, 2, "[/tournament enterable character]"]);
    static PvfTokenDocument Maps(int[]? order = null) => Doc([
        "[pvp map order]", ..(order ?? [50042, 50000]).Cast<object>(), "[/pvp map order]",
        "[pvp map basic data]", 50000, 21, 34, 1, 1, 1, 0, "[/pvp map basic data]",
        "[pvp map basic data]", 50042, 12, 28, 1, 1, 1, 0, "[/pvp map basic data]"]);
    static PvfTokenDocument Doc(params object[] values)
    {
        var strings = new List<string>();
        var tokens = values.Select(value => value is int number ? new PvfToken(0, number) : Tag((string)value)).ToArray();
        return new(tokens, id => strings[id]);
        PvfToken Tag(string text) { strings.Add(text); return new(3, strings.Count - 1); }
    }
}
