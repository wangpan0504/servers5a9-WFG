using DfoServer.GameWorld.Guilds;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class GuildDungeonProgressTests
{
    public static void RunCore()
    {
        int before = Count;
        var catalog = Fixture();
        var root = catalog.Find(101)!;
        long anchor = 1000000, week = 7 * 86400L;
        var idle = new Game.Guild.GuildDungeonClearCount(101, uint.MaxValue, 67, anchor);
        That(idle.Effective(catalog, anchor + week - 1).Units == 67 &&
            idle.Effective(catalog, anchor + week).Units == 60 && idle.Effective(catalog, anchor + week * 2).Units == 54 &&
            idle.Effective(catalog, anchor - 1).Units == 67 && idle.Effective(catalog, anchor + week * 1000).Units == 0 &&
            idle.Clears == uint.MaxValue,
            "独立有效次数逐周期相对衰减并取整，整周期边界、时钟回拨和长时间闲置不改真实历史");
        That(new Game.Guild.GuildDungeonClearCount(101, uint.MaxValue).Effective(catalog, anchor).Units == 67,
            "无独立状态的旧数据夹至满净化次数，不能靠历史累计抵消闲置衰减");
        That(GuildDungeonPartyBuffs.Resolve(205, new HashSet<int> { 281, 282, 283 }) == 281 &&
            GuildDungeonPartyBuffs.Resolve(206, new HashSet<int> { 281, 282, 283 }) == 282 &&
            GuildDungeonPartyBuffs.Resolve(207, new HashSet<int> { 281, 282, 283 }) == 283 &&
            GuildDungeonPartyBuffs.Resolve(205, new HashSet<int>()) == 0 &&
            GuildDungeonPartyBuffs.Resolve(999, new HashSet<int> { 281 }) == 0,
            "本服公会组队旧索引显式映射，缺失或未知的实际资源效果不发送");
        That(root.Purification(0) == 0 && root.Purification(1) == 1 && root.Purification(33) == 49 &&
            root.Purification(34) == 51 && root.Purification(67) == 100 && root.Purification(uint.MaxValue) == 100,
            "公会副本净化按原生次数乘单次值取整，阈值不四舍五入且超大计数不溢出");
        That(catalog.IsUnlocked(101, new Dictionary<uint, uint>()) &&
            !catalog.IsUnlocked(102, new Dictionary<uint, uint> { [101] = 33 }) &&
            catalog.IsUnlocked(102, new Dictionary<uint, uint> { [101] = 34 }) &&
            !catalog.IsUnlocked(999, new Dictionary<uint, uint> { [101] = uint.MaxValue }),
            "开放判定使用前置图自己的征服阈值，拒绝未知图和不足阈值的真实计数");
        That(catalog.FinalDungeon == 102 && catalog.InactiveDays == 7 && catalog.InactiveReduction == 10 &&
            catalog.RepeatRewardItem == 10156320 && catalog.RepeatRewardAmount == 1 &&
            catalog.PartyBuffs.Count == 3 && catalog.PartyBuffs[4] == 207 &&
            root.Rewards is [{ Ratio: 99, ItemId: 490011649, Amount: 1, Contribution: 20 }, { Ratio: 100 }],
            "资源的奖励、闲置衰减声明和组队增益独立保存，不改成旧版物品或按月重置");
        Reject<InvalidDataException>(() => Fixture(gain: 0), "零单次净化值不能使非法资源进入准入");
        Reject<InvalidDataException>(() => Fixture(prerequisite: 999), "不存在的前置图不能默认为已解锁");
        Reject<InvalidDataException>(() => Fixture(cycle: true), "公会副本前置闭环在资源加载时拒绝");
        Reject<InvalidDataException>(() => Fixture(duplicate: true), "公会副本重复编号不能覆盖有效声明");
        byte[] status = Network.Builders.GuildDungeonPackets.Progress(catalog,
            new(1, [new(101, 67), new(102, 1)], 1000));
        That(Convert.ToHexString(status) == "016500000000000000000000000200000065000000430000006600000001000000",
            "744完整17字节头与每行8字节独立金样：发送通关次数67而非净化率100，无尾字段");
        RunPublicationAsync().GetAwaiter().GetResult();
        Console.WriteLine($"PASS {Count - before} guild dungeon catalog and native purification assertions");
    }

    public static void RunResources(PvfArchive pvf)
    {
        var catalog = new GuildDungeonCatalog(pvf);
        That(catalog.PartyBuff(1) == 0 && catalog.PartyBuff(2) == 281 && catalog.PartyBuff(3) == 282 && catalog.PartyBuff(4) == 283,
            "正式资源281～283为有效Guild_Party场次效果，人数2～4按本服兼容映射选择，单人无增益");
        That(catalog.Definitions.Count == 6 && catalog.FinalDungeon == 8508 &&
            catalog.Find(8500) is { GaugeLimit: 10000, GaugeOnClear: 150, ConquestRatio: 50 } &&
            catalog.Find(8504) is { GaugeOnClear: 75, Prerequisites: [8500] } &&
            catalog.Find(8507) is { GaugeOnClear: 50, Prerequisites: [8500] } &&
            catalog.Find(8505) is { Prerequisites: [8504] } && catalog.Find(8503) is { Prerequisites: [8507] } &&
            catalog.Find(8508) is { GaugeOnClear: 30, ConquestRatio: 100, Prerequisites: [8503, 8505] },
            "正式公会副本六图目录及双前置最终图按当前资源加载");
        That(catalog.Definitions.All(row => row.Rewards is
            [{ Ratio: 99, ItemId: 490011649, Amount: 1, Contribution: 20 },
             { Ratio: 100, ItemId: 490011649, Amount: 1, Contribution: 20 }]),
            "正式六图净化奖励使用当前490011649，不移植旧版奖励");
    }

    internal static GuildDungeonCatalog Fixture(int gain = 150, int prerequisite = 101, bool cycle = false, bool duplicate = false)
    {
        var tokens = new List<PvfToken>();
        var strings = new List<string>();
        void Tag(string name)
        {
            strings.Add("[" + name + "]"); tokens.Add(new(3, strings.Count - 1));
        }
        void Field(string name, params int[] values)
        {
            Tag(name); foreach (int value in values) tokens.Add(new(0, value));
        }
        void Dungeon(int id, int perClear, params int[] required)
        {
            Tag("dungeon"); Field("dungeon index", id); Field("gauge limit", 10000);
            Field("gauge on clear", perClear); Field("conquest gauge ratio", id == 101 ? 50 : 100);
            if (required.Length != 0) { Field("required dungeon conquest", required); Tag("/required dungeon conquest"); }
            Tag("reward");
            foreach (int ratio in new[] { 99, 100 })
            {
                Field("gauge ratio", ratio); Field("item", 490011649, 1); Field("contribute", 20); Tag("/gauge ratio");
            }
            Tag("/reward"); Tag("/dungeon");
        }
        Tag("weekly reward"); Field("final dungeon", duplicate ? 101 : 102); Tag("/weekly reward");
        Dungeon(101, gain, cycle ? [102] : []); Dungeon(duplicate ? 101 : 102, 75, prerequisite);
        Field("dungeon reset info", 7, 10); Field("repeat clear bonus reward", 10156320, 1);
        Field("level difference", 10); Field("delete item", 10154439); Field("party buff list", 2, 205, 3, 206, 4, 207);
        return new(tokens, index => strings[index]);
    }
}
