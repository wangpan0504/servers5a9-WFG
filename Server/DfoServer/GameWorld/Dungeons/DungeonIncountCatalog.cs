using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 副本入场次数表 Etc/DungeonEtc/DungeonIncountInfo.etc。客户端 022E6AA0 把它装进 0x62D3D38；
// 03FEFB50 解析 [dungeon incount info]：每行“重置类型 数量n 副本×n 上限 标志 第二值”，同一行的每个副本各建一个节点，
// 03FB9D50 取上限（节点 +0x30），03FB9DC0 取第二值（节点 +0x38）；世界地图 018EC6BD 把第二值当 67086“每周入场次数 %d/%d”的分母，即每周上限，
// 018EC6C6 jle：第二值 <=0 不显示每周，即无周上限。
// 其余段由同一解析器 03FEFA80 存入单例，但客户端没有读者（KB-20261020T165745-34493afd4f），语义只能由服务端约定：
//   [reset info] 键→值（PVF：0→-1，1→4）；[account dungeon incount info] 每条“重置键 副本 上限 标志”；
//   [enter add incount dungeon index] 成对“入场副本 连带副本”；[sub incount on clear dungeon] 通关才扣次的副本；
//   [special weekly reset day] 成对“副本 星期”。[single dungeon] 保留独立账号角色绑定周期。
internal sealed record DungeonIncountRow(int ResetType, ImmutableArray<int> Dungeons, int Limit, bool Flag, int Second)
{
    // 黎明裂缝复用角色日账本，并通过 1545 + 550 投影真实剩余。
    public bool HasCountNotification { get; init; } = true;
    public int WeeklyLimit => Second;
    // 次数存储键：同行共用一份次数，取该行第一个副本编号（结构推断：客户端只为 n>1 的行建组，未见读者）。
    public int CountKey => Dungeons[0];
}

// [account dungeon incount info] 一条：重置键（查 [reset info]）、副本、上限、标志（标志无读者，未使用）。
internal sealed record AccountIncountRow(int ResetType, int Dungeon, int Limit, bool Flag);

internal sealed class DungeonIncountCatalog
{
    public const string Path = "etc/dungeonetc/dungeonincountinfo.etc";
    // 默认每周重置星期（0=周日……6=周六，假设）：客户端文本 68076/68109/30466“每周四凌晨6点初始化/重置”，与 [reset info] 1→4 一致。
    public const int DefaultWeeklyResetDay = 4;
    // 黎明裂缝固定两次数，不跟随外部 crackofdaybreak.etc 的旧 3 次值。
    public const int DaybreakDailyLimit = 2;

    public ImmutableArray<DungeonIncountRow> Rows { get; }
    public ImmutableArray<AccountIncountRow> AccountRows { get; }
    public ImmutableDictionary<int, int> ResetInfo { get; }
    public ImmutableArray<(int Entered, int Linked)> EnterAdd { get; }
    public ImmutableHashSet<int> SubOnClear { get; }
    public ImmutableDictionary<int, int> SpecialWeeklyResetDay { get; }
    public DimensionDungeonCatalog? Dimension { get; private init; }
    public ImmutableArray<DungeonIncountRow> DaybreakRows { get; private init; } = [];
    public ImmutableHashSet<int>? AvailableDungeons { get; private init; }
    public ImmutableArray<SingleDungeonGroup> SingleGroups { get; private init; } = [];

    DungeonIncountCatalog(ImmutableArray<DungeonIncountRow> rows, ImmutableArray<AccountIncountRow> accountRows, ImmutableDictionary<int, int> resetInfo,
        ImmutableArray<(int, int)> enterAdd, ImmutableHashSet<int> subOnClear, ImmutableDictionary<int, int> specialWeekly)
    {
        Rows = rows;
        AccountRows = accountRows;
        ResetInfo = resetInfo;
        EnterAdd = enterAdd;
        SubOnClear = subOnClear;
        SpecialWeeklyResetDay = specialWeekly;
    }

    public DungeonIncountRow? Find(int dungeon) => Rows.FirstOrDefault(row => row.Dungeons.Contains(dungeon)) ??
        DaybreakRows.FirstOrDefault(row => row.Dungeons.Contains(dungeon)) ??
        (Dimension?.Contains(dungeon) == true ? Dimension.DailyRow : null);
    public AccountIncountRow? FindAccount(int dungeon) => AccountRows.FirstOrDefault(row => row.Dungeon == dungeon);
    public bool ChargesOnClear(int dungeon) => SubOnClear.Contains(dungeon);
    // 角色次数的每周重置星期：[special weekly reset day] 有该副本时用它（星期编码为假设），否则默认周四。
    public int WeeklyResetDay(int dungeon) => SpecialWeeklyResetDay.TryGetValue(dungeon, out int day) ? day : DefaultWeeklyResetDay;
    // 进入 dungeon 时一起记账号次数的其它副本（[enter add incount dungeon index] 成对，去重、去自身）。
    public IEnumerable<int> Linked(int dungeon) => EnterAdd.Where(pair => pair.Entered == dungeon && pair.Linked != dungeon).Select(pair => pair.Linked).Distinct();

    public static DungeonIncountCatalog Load(PvfArchive archive)
    {
        var parsed = Parse(archive.ReadTokens(Path), archive.ResolveString);
        var available = archive.Contains("dungeon/dungeon.lst")
            ? DungeonCatalog.ParseDirectory(archive.ReadTokens("dungeon/dungeon.lst"), archive.ResolveString, "dungeon").Keys.ToImmutableHashSet()
            : null;
        var daybreak = ReadDaybreak(archive.ReadText("etc/crackofdaybreak.etc"));
        if (parsed.Rows.Any(row => row.Dungeons.Any(id => daybreak.Any(other => other.Dungeons.Contains(id)))))
            throw new InvalidDataException("黎明专表与通用次数重复登记");
        var catalog = new DungeonIncountCatalog(parsed.Rows, parsed.AccountRows, parsed.ResetInfo,
            parsed.EnterAdd, parsed.SubOnClear, parsed.SpecialWeeklyResetDay)
            { DaybreakRows = daybreak, AvailableDungeons = available, SingleGroups = parsed.SingleGroups };
        if (!archive.Contains(DimensionDungeonCatalog.Path)) return catalog;
        var dimension = new DimensionDungeonCatalog(archive);
        if (catalog.Rows.Concat(daybreak).Any(row => row.Dungeons.Any(dimension.Contains)))
            throw new InvalidDataException("异界专表与通用次数重复登记");
        return new(catalog.Rows, catalog.AccountRows, catalog.ResetInfo,
            catalog.EnterAdd, catalog.SubOnClear, catalog.SpecialWeeklyResetDay)
            { Dimension = dimension, DaybreakRows = daybreak, AvailableDungeons = available, SingleGroups = parsed.SingleGroups };
    }

    internal static ImmutableArray<DungeonIncountRow> ReadDaybreak(string text)
    {
        var document = new PvfDocument(text);
        int? limit = document.ReadRootInt32("daily enter count");
        var ids = PvfScript.Integers(document.Block("dungeon list"));
        if (limit is not (> 0 and <= byte.MaxValue) || ids.Length == 0 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length)
            throw new InvalidDataException("黎明每日次数或副本名册无效");
        // 专表没有共享组声明，每个副本独立记次；当前匹配PVF仅530。
        return [.. ids.Select(id => new DungeonIncountRow(0, [id], DaybreakDailyLimit, false, 0) { HasCountNotification = true })];
    }

    internal static DungeonIncountCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var all = Flatten(document.Sections).ToArray();
        int[] Ints(PvfTokenSection section, string name)
        {
            if (section.Values.Any(token => token.Type != 0)) throw new InvalidDataException($"[{name}] 只允许整数");
            return [.. section.Values.Select(token => token.Value)];
        }
        int[][] Sections(string name) => [.. all.Where(section => section.Name == name).Select(section => Ints(section, name))];

        var main = Sections("dungeon incount info");
        if (main.Length != 1) throw new InvalidDataException("缺少或重复 [dungeon incount info]");
        var values = main[0];
        var rows = ImmutableArray.CreateBuilder<DungeonIncountRow>();
        for (int index = 0; index < values.Length;)
        {
            if (index + 2 > values.Length) throw new InvalidDataException("[dungeon incount info] 行头不完整");
            int count = values[index + 1];
            if (count <= 0 || index + 2 + count + 3 > values.Length) throw new InvalidDataException("[dungeon incount info] 行宽不符");
            int tail = index + 2 + count;
            rows.Add(new(values[index], [.. values.Skip(index + 2).Take(count)], values[tail], values[tail + 1] != 0, values[tail + 2]));
            index = tail + 3;
        }

        var resetInfo = ImmutableDictionary.CreateBuilder<int, int>();
        foreach (var info in Sections("reset info"))
        {
            if (info.Length != 2 || !resetInfo.TryAdd(info[0], info[1])) throw new InvalidDataException("[reset info] 应为唯一的 键 值");
            if (info[1] is < -1 or > 6) throw new InvalidDataException("[reset info] 值超出 -1..6: " + info[1]);
        }

        var accountRows = ImmutableArray.CreateBuilder<AccountIncountRow>();
        foreach (var account in Sections("account dungeon incount info"))
        {
            if (account.Length % 4 != 0) throw new InvalidDataException("[account dungeon incount info] 每条 4 个整数");
            for (int at = 0; at < account.Length; at += 4)
            {
                if (!resetInfo.ContainsKey(account[at])) throw new InvalidDataException("[account dungeon incount info] 重置键不在 [reset info]: " + account[at]);
                accountRows.Add(new(account[at], account[at + 1], account[at + 2], account[at + 3] != 0));
            }
        }
        if (accountRows.GroupBy(row => row.Dungeon).Any(group => group.Count() > 1)) throw new InvalidDataException("[account dungeon incount info] 副本重复");

        var enterAdd = ImmutableArray.CreateBuilder<(int, int)>();
        foreach (var pairs in Sections("enter add incount dungeon index"))
        {
            if (pairs.Length % 2 != 0) throw new InvalidDataException("[enter add incount dungeon index] 应成对");
            for (int at = 0; at < pairs.Length; at += 2) enterAdd.Add((pairs[at], pairs[at + 1]));
        }

        var special = ImmutableDictionary.CreateBuilder<int, int>();
        foreach (var pairs in Sections("special weekly reset day"))
        {
            if (pairs.Length % 2 != 0) throw new InvalidDataException("[special weekly reset day] 应成对");
            for (int at = 0; at < pairs.Length; at += 2)
                if (pairs[at + 1] is < 0 or > 6 || !special.TryAdd(pairs[at], pairs[at + 1])) throw new InvalidDataException("[special weekly reset day] 无效");
        }

        var singles = ImmutableArray.CreateBuilder<SingleDungeonGroup>();
        foreach (var block in document.Sections.Where(section => section.Name == "single dungeon"))
            foreach (var group in block.Children)
            {
                if (group.Name != "group" || Ints(group, "group") is not [> 0] ||
                    group.Children.Any(child => child.Name is not ("dungeon index" or "character limit" or "reset day")))
                    throw new InvalidDataException("单人绑定组声明无效");
                int[] Field(string name) => group.Children.Where(child => child.Name == name).ToArray() is [var child]
                    ? Ints(child, name) : [];
                var ids = Field("dungeon index"); var limit = Field("character limit"); var reset = Field("reset day");
                if (ids.Length == 0 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length ||
                    limit is not [> 0 and <= 100] || reset.Length > 1 || reset.Any(day => day is < 0 or > 6))
                    throw new InvalidDataException("单人绑定组目标、角色数量或周期无效");
                singles.Add(new(group.Values[0].Value, [.. ids], limit[0], reset.Length == 1 ? reset[0] : DefaultWeeklyResetDay));
            }
        if (singles.Select(group => group.Id).Distinct().Count() != singles.Count ||
            singles.SelectMany(group => group.Dungeons).GroupBy(id => id).Any(group => group.Count() > 1))
            throw new InvalidDataException("单人绑定组重复");
        return new(rows.ToImmutable(), accountRows.ToImmutable(), resetInfo.ToImmutable(), enterAdd.ToImmutable(),
            [.. Sections("sub incount on clear dungeon").SelectMany(list => list)], special.ToImmutable()) { SingleGroups = singles.ToImmutable() };

        static IEnumerable<PvfTokenSection> Flatten(IEnumerable<PvfTokenSection> sections) =>
            sections.SelectMany(section => new[] { section }.Concat(Flatten(section.Children)));
    }
}
