using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassBraveCrystalSource
{
    public const string SourceKind = "earned_brave_crystal_acquisition";
    public static bool Matches(byte category, BattlePassMission mission) => category == 1 &&
        mission is { Id: 28, RawTypeCode: 26, RequiredCount: 1, Experience: 1000, RawParameter: -1 };

    public static uint Acquired(AdventureCreditPlan plan, AdventureCreditSource source, AdventureCatalog catalog)
    {
        if (source != AdventureCreditSource.Earned || plan.RawOverflow == 0) return 0;
        var before = plan.Before; var after = plan.After;
        if (before.Points.Length != 5 || after.Points.Length != 5 || before.Revision < 0 ||
            !before.Points.Skip(1).SequenceEqual(after.Points.Skip(1)))
            throw new InvalidDataException("骁勇结晶取得必须保留原账号余额和revision");
        if (!catalog.Shops.TryGetValue(AdventureProgression.BraveShop, out var shop) ||
            shop.ExperiencePerPoint == 0 || shop.PointsPerStep == 0) return 0;
        ulong total = checked(before.BraveExperience + plan.RawOverflow);
        ulong steps = Math.Min(uint.MaxValue, total / shop.ExperiencePerPoint);
        ulong add = Math.Min(uint.MaxValue, steps * shop.PointsPerStep);
        uint expected = checked((uint)Math.Min(shop.Maximum, before.Points[0] + add));
        if (before.Points[0] > shop.Maximum || after.Points[0] != expected ||
            after.BraveExperience != total % shop.ExperiencePerPoint)
            throw new InvalidDataException("骁勇结晶必须沿原溢出经验、兑换步长、余数和上限实际到账");
        return expected - before.Points[0];
    }

    public static DateTimeOffset OccurredAt(AdventureCreditPlan plan) =>
        plan.OccurredAt is { } at && at.ToUnixTimeSeconds() > 0 ? at :
        throw new InvalidDataException("骁勇结晶来源缺少原经验业务时点");

    public static string SourceId(long revision)
    {
        if (revision < 0) throw new InvalidDataException("骁勇结晶来源缺少原账号余额revision");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            SourceKind + ":" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture))).AsSpan(0, 16)).ToLowerInvariant();
    }
    public static long NextCount(BattlePassMission mission, long current, uint acquired)
    {
        if (!Matches(1, mission) || current < 0 || current > mission.RequiredCount || acquired == 0)
            throw new InvalidDataException("骁勇结晶原任务或真实取得数量无效");
        return Math.Min(mission.RequiredCount, checked(current + acquired));
    }
}
