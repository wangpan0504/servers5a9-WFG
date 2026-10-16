using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Characters;
using PvfLib;

namespace DfoServer;

internal static class CharacterGrowthCatalogTests
{
    public static void Run(PvfArchive pvf)
    {
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf);
        That(growth.CumulativeExperience.Count == 100 && growth.CumulativeExperience[0] == 0,
            "99项资源边界使用客户端的零起点索引");
        That(growth.GetLevelStartExperience(1) == 0 && growth.GetCumulativeExperienceBoundary(1) == 1000 &&
            growth.GetLevelStartExperience(2) == 1000 && growth.GetCumulativeExperienceBoundary(2) == 2035 &&
            growth.GetLevelStartExperience(3) == 2035 && growth.GetCumulativeExperienceBoundary(3) == 3315,
            "Lv1..3累计经验边界不再次累加原表");
        That(growth.GetCumulativeExperienceBoundary(90) == 2021310320UL &&
            growth.GetCumulativeExperienceBoundary(91) == 2315706532UL &&
            growth.GetLevelStartExperience(95) == 3320457250UL &&
            growth.GetCumulativeExperienceBoundary(95) == 3726597550UL,
            "Lv90、91与95保留超过有符号DWORD的大值");
        That(growth.CumulativeExperience[96] == 4140860656UL &&
            growth.CumulativeExperience[97] == 4563409023UL && growth.CumulativeExperience[99] == 5434027680UL,
            "资源后段跨2^32边界仍保留完整u64，仅作为原始资源声明");
        That(growth.GetLevelExperienceSpan(1) == 1000 && growth.GetLevelExperienceSpan(2) == 1035 &&
            growth.GetLevelExperienceSpan(3) == 1280 && growth.GetLevelExperienceSpan(91) == 294396212UL,
            "等级经验跨度使用相邻累计边界之差");
        That(growth.GetSpDelta(1, 1) == 0 && growth.GetSpDelta(1, 2) == 30 &&
            growth.GetSpDelta(1, 3) == 60 && growth.GetSpDelta(10, 11) == 60 &&
            growth.GetSpDelta(1, 95) == 13320 && growth.GetSpDelta(94, 95) == 270,
            "SP正常等级贡献覆盖重复等级及分段累计");
        Reject<ArgumentOutOfRangeException>(() => growth.GetSpDelta(3, 2));
        Reject<ArgumentOutOfRangeException>(() => growth.GetSpDelta(0, 2));
        Reject<ArgumentOutOfRangeException>(() => growth.GetSpDelta(1, 96));
        Reject<ArgumentOutOfRangeException>(() => growth.GetLevelStartExperience(0));
        Reject<ArgumentOutOfRangeException>(() => growth.GetCumulativeExperienceBoundary(96));
        Reject<ArgumentOutOfRangeException>(() => growth.GetLevelExperienceSpan(-1));

        ulong[] boundary = CharacterGrowthCatalog.ParseExperienceTable(
            [new(9, 0), new(9, -1), new(9, 1), new(9, 0), new(9, 1), new(9, 1)]);
        That(boundary.SequenceEqual(new ulong[] { 0, uint.MaxValue, 1UL << 32, (1UL << 32) + 1 }),
            "type9的高低DWORD顺序及负low位模式跨2^32保持精确");
        That(CharacterGrowthCatalog.ParseExperienceTable([new(9, -1), new(9, -1)])[1] == ulong.MaxValue,
            "type9两个DWORD都是位模式，不能当负signed边界拒绝");
        Reject<InvalidDataException>(() => CharacterGrowthCatalog.ParseExperienceTable([]));
        Reject<InvalidDataException>(() => CharacterGrowthCatalog.ParseExperienceTable([new(2, 1000)]));
        Reject<InvalidDataException>(() => CharacterGrowthCatalog.ParseExperienceTable([new(9, 0)]));
        Reject<InvalidDataException>(() => CharacterGrowthCatalog.ParseExperienceTable([new(9, 0), new(0, 1000)]));
        Reject<InvalidDataException>(() => CharacterGrowthCatalog.ParseExperienceTable([new(0, -1)]));
        Reject<InvalidDataException>(() => CharacterGrowthCatalog.ParseExperienceTable([new(0, 0)]));
        Reject<InvalidDataException>(() => CharacterGrowthCatalog.ParseExperienceTable([new(0, 1000), new(0, 1000)]));
        Reject<InvalidDataException>(() => CharacterGrowthCatalog.ParseExperienceTable([new(0, 1000), new(9, 0), new(9, 999)]));
        Console.WriteLine("PASS 角色累计经验、SP贡献及非法成长资源拒绝");
    }


}
