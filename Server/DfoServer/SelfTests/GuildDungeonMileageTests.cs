using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Guilds;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildDungeonMileageTests
{
    public static void RunCore()
    {
        int before = Count;
        var ordinary = new DungeonDefinition(3, "dungeon/fixture.dgn", 20, 25, [], []);
        That(GuildDungeonMileageEligibility.IsEligible(ordinary, 20) && GuildDungeonMileageEligibility.IsEligible(ordinary, 25) &&
            !GuildDungeonMileageEligibility.IsEligible(ordinary, 19) && !GuildDungeonMileageEligibility.IsEligible(ordinary, 26),
            "公会积分推荐区间采用DGN上下界，不套冒险团的等级±5规则");
        var explicitRange = ordinary with { RawSections = [new("recommended level", [new(0, 30), new(0, 35)])] };
        That(GuildDungeonMileageEligibility.IsEligible(explicitRange, 30) &&
            !GuildDungeonMileageEligibility.IsEligible(explicitRange, 25) &&
            !GuildDungeonMileageEligibility.IsEligible(ordinary with
                { RawSections = [new("recommended level", [new(0, 20)])] }, 20),
            "显式推荐等级优先，格式坏掉时不能回退成默认资格");
        foreach (string tag in new[] { "guild dungeon", "raid dungeon", "raid normal dungeon", "normal luke dungeon",
            "tower of grief", "blood dungeon", "ancient dungeon", "special dungeon", "crack of dimension dungeon" })
            That(!GuildDungeonMileageEligibility.IsEligible(ordinary with
                { RawSections = [new(tag, Array.Empty<PvfToken>())] }, 20), "特殊副本不取得普通公会积分: " + tag);
        That(!GuildDungeonMileageEligibility.IsEligible(ordinary with { DungeonType = "tower of dazzlement" }, 20) &&
            !GuildDungeonMileageEligibility.IsEligible(ordinary with { MinimumLevel = null }, 20) &&
            GuildDungeonMileageEligibility.IsEligible(ordinary with { DungeonType = "[normal]" }, 20),
            "普通类型可用，特殊类型和缺推荐范围不凭空获得资格");
        Console.WriteLine($"PASS {Count - before} 公会通关公共积分资源资格断言");
    }
}
