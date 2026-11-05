using System.Security.Cryptography;
using System.Text;
using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

// raw14（参数-1）对应每日ID14（3,000,000）、每周ID27（10,000,000）及赛季ID52（2,000,000,000）的角色经验获取。
internal static class BattlePassExperienceGainSource
{
    public static bool Matches(byte category, BattlePassMission definition) =>
        definition.RawTypeCode == 14 && definition.RawParameter == -1 &&
        (category == 0 && definition is { Id: 14, RequiredCount: 3_000_000, Experience: 100 } ||
         category == 1 && definition is { Id: 27, RequiredCount: 10_000_000, Experience: 1000 } ||
         category == 2 && definition is { Id: 52, RequiredCount: 2_000_000_000, Experience: 1600 });

    public static string SourceKey(string domain, string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("character_experience_gain:" + domain + ":" + identity)).AsSpan(0, 16))
            .ToLowerInvariant();
}
