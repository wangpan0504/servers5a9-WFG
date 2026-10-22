using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Game.Adventure;

internal sealed record AdventureMember(ushort Id, ushort Slot, string Name, byte Job, byte Grow, byte Level, uint Proficiency, long Revision)
{
    public byte ProfessionGrow => AdventureCatalog.ProfessionGrow(Job, Grow);
}
internal sealed record AdventureState(long Revision, string Name, int CreatedDate, int Level, ulong Experience, decimal Remainder,
    ulong CapsuleExperience, ulong BraveExperience, ImmutableArray<uint> Points, int Month)
{
    public bool NameInitialized { get; init; }
    // 成长胶囊计量条上次已结算的星期四06:00边界（yyyyMMdd）；0 表示迁移前未记录。
    public int CapsuleWeek { get; init; }
    // 连续登录天数及最近计入的06:00周期日期（yyyyMMdd）；0 表示迁移前未记录，首次登录计1。
    public int LoginStreakDays { get; init; }
    public int LastLoginDay { get; init; }
    public ImmutableDictionary<int, uint> Purchases { get; init; } = ImmutableDictionary<int, uint>.Empty;
    public ImmutableArray<AdventureSelection> Representatives { get; init; } = [];
    public ImmutableArray<AdventureExpedition> Expeditions { get; init; } = [];
    // 国服95开启账本时的精确经验总量；之后按角色里程增量累计，删除角色时把其净贡献并入这里。
    public decimal OpeningExperience { get; init; }
    // 不计入冒险团的来源累计（经验胶囊、升级券、GM改写等）；可能为负，表示排除来源降低了总量。
    public decimal ExcludedExperience { get; init; }
    public static AdventureState Initial(string account, DateTimeOffset now) => new(0, account.Length <= 16 ? account : account[..16],
        Date(now), 1, 0, 0, 0, 0, [0, 0, 0, 0, 0], AdventurePeriod.Month(now));
    public static int Date(DateTimeOffset now) => int.Parse(now.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
    public void Validate(AdventureCatalog catalog)
    {
        if (Revision < 0 || string.IsNullOrWhiteSpace(Name) || Name.Length > 16 || CharacterName.Encoding.GetByteCount(Name) > 32 ||
            Level < 1 || Level > catalog.MaximumLevel || Remainder is < 0 or >= 1 || Experience > long.MaxValue ||
            OpeningExperience < 0 || OpeningExperience > long.MaxValue ||
            Level < catalog.MaximumLevel && Experience >= catalog.Experience[Level + 1] ||
            CapsuleWeek < 0 || CapsuleExperience > catalog.CapsuleCapacity || Points.Length != 5 ||
            LoginStreakDays < 0 || LastLoginDay < 0 || LastLoginDay != 0 && (LastLoginDay < 19000101 || LastLoginDay > 99991231) ||
            LoginStreakDays > 0 && LastLoginDay == 0 ||
            Points.Where((count, index) => catalog.Shops.TryGetValue((byte)index, out var shop) && count > shop.Maximum).Any() ||
            Purchases.Count > 256 || Purchases.Any(pair => pair.Key <= 0 || pair.Value == 0) ||
            Representatives.Any(row => !catalog.Professions.ContainsKey((row.Job, AdventureCatalog.ProfessionGrow(row.Job, row.Grow)))) ||
            Representatives.Select(row => (row.Job, AdventureCatalog.ProfessionGrow(row.Job, row.Grow))).Distinct().Count() != Representatives.Length ||
            Expeditions.Any(run => run.Id == Guid.Empty || run.Area == 0 || run.Started < 0 || run.Ends <= run.Started ||
                run.Members.Length is < 1 or > 4 || run.FrozenMembers.Length != run.Members.Length ||
                !run.FrozenMembers.Select(row => row.Id).SequenceEqual(run.Members)) ||
            Expeditions.Select(run => run.Area).Distinct().Count() != Expeditions.Length ||
            Expeditions.SelectMany(run => run.Members).Distinct().Count() != Expeditions.Sum(run => run.Members.Length))
            throw new InvalidDataException("账号冒险团持久状态非法");
    }
}
// 内容通关累计按内容类型索引；type0为推荐地下城。当前只有推荐地下城有服务端通关来源（写入1343的u32@16），
// 其余内容未接入时不伪造数值；该数组不再写入20组收藏分类表。
internal static class AdventureContent
{
    public const int Count = 20;
    public const int RecommendedDungeon = 0;
}

internal sealed record AdventureSnapshot(AdventureState State, ImmutableArray<AdventureMember> Members)
{
    // 内容通关累计按账号汇总；默认全零，读取时由角色级计数填充。
    public ImmutableArray<ushort> ContentCounts { get; init; } = ImmutableArray.Create(new ushort[AdventureContent.Count]);

    public ImmutableArray<AdventureMember> Representatives(AdventureCatalog catalog)
    {
        var result = ImmutableArray.CreateBuilder<AdventureMember>();
        foreach (var group in Members.Where(row => catalog.Eligible(row.Job, row.Grow, row.Level))
            .GroupBy(row => (row.Job, Grow: row.ProfessionGrow)).OrderBy(group => group.Key.Job).ThenBy(group => group.Key.Grow))
        {
            var selected = State.Representatives.FirstOrDefault(row => row.Job == group.Key.Job && row.Grow == group.Key.Grow);
            if (State.Representatives.Any(row => row.Job == group.Key.Job && row.Grow == group.Key.Grow) && selected.CharacterId is null) continue;
            // 没有显式选择时按角色熟练度取最高；同熟练度再比等级，最后用UID保证稳定。
            result.Add(group.FirstOrDefault(row => row.Id == selected.CharacterId) ??
                group.OrderByDescending(row => row.Proficiency).ThenByDescending(row => row.Level).ThenBy(row => row.Id).First());
        }
        return result.ToImmutable();
    }
}

internal static class AdventurePeriod
{
    static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    // UTC+8的06:00日界：06:00前仍属于前一自然日；连续登录与远征属性轮换共用。
    public static DateTime ChinaDay(DateTimeOffset now)
    {
        var local = now.ToOffset(ChinaOffset);
        if (local.TimeOfDay < TimeSpan.FromHours(6)) local = local.AddDays(-1);
        return local.Date;
    }

    // 连续登录按UTC+8日期、06:00切换：06:00前仍属于前一自然日。
    public static int LoginDay(DateTimeOffset now) =>
        int.Parse(ChinaDay(now).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture);

    // 商店月度重置按官方公告“每月1日06:00”切换；沿用06:00日界，月初00:00至05:59仍属上个月。
    public static int Month(DateTimeOffset now)
    {
        var day = ChinaDay(now);
        return day.Year * 100 + day.Month;
    }

    // 同日或时钟回拨保持不变；上一周期+1（按真实日历跨月）；断一天或更久归零重计1。
    public static int NextLoginStreak(int previousDay, int previousStreak, int day)
    {
        if (previousDay == 0) return 1;
        if (day <= previousDay) return previousStreak;
        static DateTime Parse(int key) => DateTime.ParseExact(key.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        return Parse(day) == Parse(previousDay).AddDays(1) && previousStreak < int.MaxValue ? previousStreak + 1 : 1;
    }

    // 改版公告：累积的成长胶囊经验值在每周四06:00初始化；资源日期即该周期的锚点（2018-02-22 为周四）。
    public static int CapsuleWeek(AdventureCatalog catalog, DateTimeOffset now)
    {
        DateTime anchor = catalog.Capsule.ResetAnchor;
        DateTime local = now.ToOffset(TimeSpan.FromHours(8)).DateTime;
        if (local < anchor) return 0;
        long days = (local.Date - anchor.Date).Days;
        long weeks = days / 7;
        if (days % 7 == 0 && local.TimeOfDay < anchor.TimeOfDay) weeks--;
        if (weeks < 0) return 0;
        DateTime boundary = anchor.AddDays(weeks * 7);
        return boundary.Year * 10000 + boundary.Month * 100 + boundary.Day;
    }

    public static AdventureState Apply(AdventureState state, AdventureCatalog catalog, DateTimeOffset now)
    {
        // 旧版总容量按每格×颗数放大十倍；载入时截回真实容量，避免升级后校验拒绝登录。
        if (state.CapsuleExperience > catalog.CapsuleCapacity)
            state = state with { CapsuleExperience = catalog.CapsuleCapacity };
        // 旧版或外部写入可能把结晶堆到商店上限之上；载入时截回上限，写入路径仍由 Validate 守住。
        var capped = state.Points.ToBuilder();
        foreach (var shop in catalog.Shops.Values)
            if (shop.Index < capped.Count && capped[shop.Index] > shop.Maximum) capped[shop.Index] = shop.Maximum;
        if (!capped.SequenceEqual(state.Points)) state = state with { Points = capped.ToImmutable() };
        int week = CapsuleWeek(catalog, now);
        if (week > state.CapsuleWeek)
            state = state with { CapsuleWeek = week, CapsuleExperience = 0 };
        int month = Month(now);
        if (month <= state.Month) return state;
        var points = state.Points.ToBuilder();
        foreach (var shop in catalog.Shops.Values.Where(shop => shop.MonthlyReset)) points[shop.Index] = 0;
        var purchases = state.Purchases.Where(pair => !catalog.Products.Values.Any(product => product.ItemId == pair.Key && product.ResetType == 3))
            .ToImmutableDictionary();
        return state with { Month = month, Points = points.ToImmutable(), Purchases = purchases };
    }
}
