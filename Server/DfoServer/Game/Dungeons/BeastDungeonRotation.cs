namespace DfoServer.Game.Dungeons;

// 时间广场 310–314 的每日两图。服务日沿 UTC+8 06:00，与副本次数同一日界。
internal static class BeastDungeonRotation
{
    public const int FirstDungeonId = 310;
    public const int FirstGatewayDungeonId = 5335;
    public const int DailyLimit = 2;
    public const int TimedStage = 3;
    public const int LimitSeconds = 180;

    public static int ServiceDay(DateTimeOffset utcNow) => DfoServer.Game.Inventory.Models.ShopPurchaseCount.Today(utcNow);

    public static (int First, int Second) Pair(int serviceDay)
    {
        int normalized = serviceDay % 5;
        if (normalized < 0) normalized += 5;
        int first = (normalized * 3 + 1) % 5;
        return (FirstDungeonId + first, FirstDungeonId + (first + 1) % 5);
    }

    public static bool Contains(int serviceDay, int dungeonId)
    {
        var pair = Pair(serviceDay);
        dungeonId = SourceDungeonId(dungeonId);
        return dungeonId == pair.First || dungeonId == pair.Second;
    }

    // 独立魔兽城镇使用普通选图合同；与原310–314共用同一套布局与次数，不能换入口绕过日限额。
    public static bool IsGateway(uint dungeonId) => dungeonId is >= FirstGatewayDungeonId and < FirstGatewayDungeonId + 5;
    public static int SourceDungeonId(int dungeonId) => IsGateway(unchecked((uint)dungeonId))
        ? FirstDungeonId + dungeonId - FirstGatewayDungeonId : dungeonId;
    public static bool SupportsDifficulty(uint dungeonId, byte difficulty) => IsGateway(dungeonId)
        ? difficulty == 0 : dungeonId is >= FirstDungeonId and < FirstDungeonId + 5 && difficulty == 4;

    // 第六难度 = 难度4 + MAZE2（用户 2026-10-28 口径：MAZE 0/1/2 分别是普通/深渊/魔兽拓扑）。
    // MAZE1 是深渊拓扑，必须留给原有深渊分支，不能吞成魔兽；难度4 + MAZE0 是普通噩梦按钮。
    public const byte MazeBeast = 2;

    public static bool MatchesEntry(int dungeonId, byte difficulty, byte maze) =>
        dungeonId is >= FirstDungeonId and <= FirstDungeonId + 4 && difficulty == 4 && maze == MazeBeast;

    public static bool CanEnter(int serviceDay, int dungeonId, int used) =>
        Contains(serviceDay, dungeonId) && used is >= 0 and < DailyLimit;

    // etc/BeastBalance.tbl 的普通档第一列。现有怪物生成没有生命和攻击字段，先保留倍率。
    public const double NormalHpScale = 5.0;
    public const double NormalAttackScale = 2.3;

    // 整合脚本里的阶段袖珍罐。10303241 在那份脚本里是首饰随机罐，不在这条阶段序列。
    public static readonly int[] StageRewardItems = [10303240, 10303242, 10303243, 10303244, 10303245, 10303246];

    public static int? StageRewardItem(int stage) =>
        stage is >= 0 and < 6 ? StageRewardItems[stage] : null;
}
