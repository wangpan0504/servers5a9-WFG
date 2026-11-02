using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassGuildHallEntryTests
{
    public static void RunCore()
    {
        int before = Count;
        var origin = new Location(38, 1, 100, 100);
        var target = new Location(8, 0, 600, 400);
        var character = new Character(1, 0, "HallFact", 7, origin, origin)
            { Guild = new(51, "Hall", 1, 1, "Master", "", 0, 0) };
        var at = new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.FromHours(8));
        var fact = new GuildHallEntryFact(Guid.NewGuid(), at, TimeProvider.System, character.Id, 51, origin, target);
        That(fact.Matches(character, target, 51), "原角色同会籍由厅外进入真实大厅落点");
        That(!(fact with { Id = Guid.Empty }).Matches(character, target, 51), "空事件不可计量");
        That(!(fact with { CharacterId = 2 }).Matches(character, target, 51), "其他角色入场不可借用");
        That(!fact.Matches(character with { Guild = null }, target, 51), "没有原会籍不可入场");
        That(!fact.Matches(character, target, 52), "writer当前会籍必须一致");
        That(!(fact with { GuildId = 0 }).Matches(character, target, 51), "零公会不计入场");
        That(!fact.Matches(character with { Position = target }, target, 51), "厅内移动或原大厅恢复不能借入场事实");
        That(!(fact with { Origin = target }).Matches(character with { Position = target }, target, 51), "同场景坐标变化不计入场");
        That(!fact.Matches(character, target with { X = 601 }, 51), "实际落点与冻结规划完整一致");
        var definition = new BattlePassMission(10, 1, 100, 10, "进入公会大厅", 8);
        That(BattlePassGuildHallEntrySource.Matches(definition, 8), "精确原定义与资源目标town8匹配");
        foreach (var other in new[] { definition with { Id = 9 }, definition with { RawTypeCode = 11 },
            definition with { RawParameter = -1 }, definition with { RequiredCount = 2 }, definition with { Experience = 101 } })
            That(!BattlePassGuildHallEntrySource.Matches(other, 8), "变异定义不借原大厅来源");
        That(!BattlePassGuildHallEntrySource.Matches(definition, 9), "另一城镇不冒公会大厅");
        Console.WriteLine($"PASS {Count - before} BattlePassGuildHallEntryTests core");
    }
}
