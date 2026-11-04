namespace DfoServer.GameWorld.Raids;

// 正式孵化场MAP、monster.lst与各自last action的交集；普通/觉醒等同名资源不在许可内。
internal sealed record AntonConstructorDefinition(int Dungeon, int Map, int Monster, int Signal, string MonsterPath, string LastPath)
{
    internal static readonly AntonConstructorDefinition[] All =
    [
        new(221, 17181, 65650, 20000, "monster/newmonsters/anton/phase3/constructor/constructor.mob", "monster/newmonsters/anton/phase3/constructor/action/last.act"),
        new(222, 17184, 65674, 20001, "monster/newmonsters/anton/phase3/constructor/constructor_1.mob", "monster/newmonsters/anton/phase3/constructor/action/last_1.act"),
        new(223, 17189, 65675, 20002, "monster/newmonsters/anton/phase3/constructor/constructor_2.mob", "monster/newmonsters/anton/phase3/constructor/action/last_2.act"),
        new(224, 17192, 65676, 20003, "monster/newmonsters/anton/phase3/constructor/constructor_3.mob", "monster/newmonsters/anton/phase3/constructor/action/last_3.act")
    ];

    internal static AntonConstructorDefinition? Find(uint dungeon, int map) =>
        All.SingleOrDefault(row => row.Dungeon == dungeon && row.Map == map);
}
