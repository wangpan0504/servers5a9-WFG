using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 冒险团「每周地下城信息」面板的9个列与正式副本内容的对应关系：列名来自实机截图，
// 副本路径来自PVF资源（`dungeon/...` 下的实际目录）。未列出的副本不消耗每周次数。
// 5号列在客户端资源里没有可识别的内容，保持无统计来源，客户端显示"-"。
internal static class WeeklyDungeonPolicy
{
    public const int SlotCount = 9;

    // 面板列号按客户端渲染顺序：0伊希斯攻坚战、1超时空漩涡、2卢克攻坚战、3泰波尔斯、
    // 4代号:希望、6眩惑之塔、7魔界大战、8公会地下城。超时空漩涡的副本目录沿用KR资源名fiendwar_kr，
    // 内部图名cosmo_*与「作战区域」剧情可核对；魔界大战在kcontents2/theevildomwar。
    static readonly (string Prefix, int Slot)[] Contents =
    [
        ("dungeon/gcontents/190307_prey_raid/", 0),
        ("dungeon/gcontents/190606_prey_raid_normal/", 0),
        ("dungeon/fiendwar_kr/", 1),
        ("dungeon/luke/", 2),
        ("dungeon/luke_normal/", 2),
        ("dungeon/legend/tayberrs/", 3),
        ("dungeon/op_hope/", 4),
        ("dungeon/towers/towerofdazzlement", 6),
        ("dungeon/kcontents2/theevildomwar/", 7),
        ("dungeon/guilddungeon/", 8),
    ];

    // 每周四06:00（UTC+8）重置；锚点同为周四即可，周期边界与成长胶囊等其它周重置一致。
    static readonly DateTimeOffset WeekAnchor = new(2018, 2, 22, 6, 0, 0, TimeSpan.FromHours(8));

    public static int? SlotOf(DungeonDefinition definition)
    {
        foreach (var (prefix, slot) in Contents)
            if (definition.Path.StartsWith(prefix, StringComparison.Ordinal)) return slot;
        return null;
    }

    public static bool Mapped(int slot) => slot is >= 0 and < SlotCount && Array.Exists(Contents, row => row.Slot == slot);

    // 返回当前周边界日期（yyyyMMdd）；资源锚点之前返回0，与冒险团周计数口径一致。
    public static int WeekKey(DateTimeOffset now)
    {
        var local = now.ToOffset(TimeSpan.FromHours(8));
        if (local < WeekAnchor) return 0;
        long days = (local.Date - WeekAnchor.Date).Days;
        long weeks = days / 7;
        if (days % 7 == 0 && local.TimeOfDay < WeekAnchor.TimeOfDay) weeks--;
        if (weeks < 0) return 0;
        var boundary = WeekAnchor.AddDays(weeks * 7);
        return boundary.Year * 10000 + boundary.Month * 100 + boundary.Day;
    }
}

// 一次正式入场计入的每周地下城列：Week 是周边界日期（yyyyMMdd），Slot 是面板9列中的列号。
// 与尝试号预留同一事务写入，重复提交不会多计。
internal sealed record WeeklyDungeonEntry(int Week, int Slot);

// GM 刷新账号地下城入场次数的结果：按存储分类统计清零的行数。
// Week 是冒险团面板当前周；各玩法的周期键按各自规则计算（日键/周键/自定义游戏日）。
internal sealed record DungeonEntryResetResult(string Account, int Week,
    int PanelRows, int CharacterDailyRows, int CharacterWeeklyRows, int AccountRows, int AssaultRows,
    int DazzlementRows, int GriefAccountRows, int GriefCharacterRows, int SpecialEntryRows, int DespairRows,
    int RaidRows, int TagClearanceRows, int TagChallengeRows);

// GM 刷新账号奖励限额的结果。历史回执和已发资产永远保留；这里只迁移当前周期的资格/状态行。
internal sealed record RewardLimitResetResult(string Account, int Day, int Week,
    int StrongwayRewardRows, int StrongwayCompletionRows, int StrongwayMatchRows,
    int ImperialDayRows, int ImperialRunRows, int ImperialRewardRows, int ImperialChallengeRows,
    int TagClearanceRows, int TagChallengeRows, int GriefAccountRows, int GriefCharacterRows,
    int SpecialEntryRows, int DespairRows, int RaidRows, int RaidReceiptRows);
