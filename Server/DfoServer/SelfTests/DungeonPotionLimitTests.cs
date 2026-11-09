using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonPotionLimitTests
{
    internal static void RunCore()
    {
        int before = Count;
        var catalog = Parse(["[potion list]", 8, 7740, "[/potion list]", "[dungeon list]", 4988, 10, 4989, 0, "[/dungeon list]"]);
        var uses = new DungeonPotionUse(catalog, catalog.Dungeons[4988]);
        for (int count = 0; count < 10; count++)
        {
            That(uses.CanUse(8) && uses.CanUse(7740) && uses.Remaining == 10 - count, "受限药品共享本角色本次地下城额度");
            uses.Commit(count % 2 == 0 ? 8 : 7740);
        }
        That(!uses.CanUse(8) && !uses.CanUse(7740) && uses.CanUse(999), "用满只禁止表内药品，不扩大为所有道具禁用");
        uses.Commit(999);
        That(uses.Remaining == 0, "表外道具不消耗药品额度");
        Reject<InvalidOperationException>(() => uses.Commit(8), "用尽后不能再提交成功使用");
        That(new DungeonPotionUse(catalog, 10).Remaining == 10 && !new DungeonPotionUse(catalog, 0).CanUse(8),
            "新实例重置额度，明确零额度仍拒绝，不伪造无限次数");
        foreach (object[] invalid in new object[][]
        {
            ["[potion list]", 8, 8, "[/potion list]", "[dungeon list]", 4988, 10, "[/dungeon list]"],
            ["[potion list]", 8, "[/potion list]", "[dungeon list]", 4988, "[/dungeon list]"],
            ["[potion list]", 8, "[/potion list]", "[dungeon list]", 4988, -1, "[/dungeon list]"],
            ["[potion list]", 8, "[/potion list]", "[dungeon list]", 4988, 1, 4988, 2, "[/dungeon list]"]
        }) Reject<InvalidDataException>(() => Parse(invalid), "异常药品限额资源必须拒绝");
        Console.WriteLine($"PASS {Count - before} 地下城药品限额解析、共享计数和实例重置断言");

        static StackableDungeonLimitCatalog Parse(object[] values)
        {
            var strings = new List<string>();
            var tokens = values.Select(value => value is int number ? new PvfToken(0, number) :
                new PvfToken(3, Add((string)value))).ToArray();
            return new(tokens, index => strings[index]);
            int Add(string text) { strings.Add(text); return strings.Count - 1; }
        }
    }
}
