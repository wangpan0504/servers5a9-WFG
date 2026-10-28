using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.ImperialArena;

internal enum ImperialArenaMode : byte { None, Normal, Hard }

// 帝国的日界为本服06点，困难排名周界为周四；与再战的周一账本互不相借。
internal readonly record struct ImperialArenaPeriod(int Day, int Week)
{
    public static ImperialArenaPeriod At(DateTimeOffset now)
    {
        var day = now.UtcDateTime.AddHours(2).Date;
        var thursday = day.AddDays(-((int)day.DayOfWeek + 3) % 7);
        static int Key(DateTime value) => value.Year * 10000 + value.Month * 100 + value.Day;
        return new(Key(day), Key(thursday));
    }

    public static bool RankingClosed(DateTimeOffset now)
    {
        var local = now.ToOffset(TimeSpan.FromHours(8));
        return local.DayOfWeek == DayOfWeek.Thursday && local.Hour == 5 && local.Minute >= 30;
    }
}

internal sealed record ImperialArenaConfiguration(long Revision, ushort First, ushort Second)
{
    public static readonly ImperialArenaConfiguration Empty = new(0, 0, 0);
    public bool IsEmpty => First == 0 && Second == 0;
    public void Validate()
    {
        if (Revision < 0 || !IsEmpty && (First == 0 || Second == 0 || First == Second))
            throw new InvalidDataException("帝国竞技场必须保存两名不同角色的稳定身份");
    }
}

internal sealed record ImperialArenaState(ImperialArenaConfiguration Configuration, ImperialArenaPeriod Period,
    ImperialArenaMode PaidMode, ImmutableHashSet<int> RewardedMonsters, int HardRewardRound);

internal sealed record ImperialArenaProjection(ImperialArenaState State, byte Server,
    CharacterRosterSnapshot Roster, long Generation, long Epoch, ushort CharacterId);

internal sealed record ImperialArenaCharacters(ImperialArenaProjection Projection, Character Primary, Character Standby);

internal sealed record ImperialArenaDailyRoute(ImperialArenaPeriod Period,
    ImmutableArray<ImperialArenaOpponent> Normal, ImmutableArray<ImperialArenaOpponent> Hard)
{
    // 0176C6D0取值后加21作为图标索引；本版21..25依次为火、水、暗、光、无。
    public ImmutableArray<byte> NormalWeakness { get; init; } = [];
    public ImmutableArray<byte> HardWeakness { get; init; } = [];
    // 0153129F经共享getter00806E70读取+FC；本版卢克卡片图标15为魔法，14为物理。
    public byte CommonWeakness { get; init; }
    public void Validate(ImperialArenaCatalog catalog)
    {
        if (Normal.IsDefault || Hard.IsDefault || Normal.Length != 5 || Hard.Length != 7 ||
            NormalWeakness.IsDefault || HardWeakness.IsDefault || NormalWeakness.Length != 5 || HardWeakness.Length != 7 ||
            NormalWeakness[^1] != 4 || NormalWeakness.Take(4).Concat(HardWeakness).Any(value => value > 3) || CommonWeakness > 1 ||
            Normal.Take(4).Any(row => !catalog.Normal.Contains(row)) || !catalog.Special.Contains(Normal[^1]) ||
            Hard.Any(row => !catalog.Hard.Contains(row)) || Normal.Select(row => row.MapId).Distinct().Count() != 5 ||
            Normal.Select(row => row.MonsterId).Distinct().Count() != 5 || Hard.Select(row => row.MapId).Distinct().Count() != 7 ||
            Hard.Select(row => row.MonsterId).Distinct().Count() != 7)
            throw new InvalidDataException("帝国每日路线对手或弱点不合法");
    }
    // 用固定业务日及候选身份排列，跨账号、重登与进程重启不重新抽取当天对手。
    public static ImperialArenaDailyRoute Create(ImperialArenaCatalog catalog, ImperialArenaPeriod period)
    {
        ImmutableArray<ImperialArenaOpponent> Shuffle(ImmutableArray<ImperialArenaOpponent> source, int salt) =>
            source.OrderBy(row => Score(row.MapId, salt)).ThenBy(row => row.MapId).ToImmutableArray();
        ulong Score(int id, int salt)
        {
            Span<byte> input = stackalloc byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(input, period.Day);
            BinaryPrimitives.WriteInt32LittleEndian(input[4..], id);
            BinaryPrimitives.WriteInt32LittleEndian(input[8..], salt);
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(input, digest);
            return BinaryPrimitives.ReadUInt64LittleEndian(digest);
        }
        var normal = Shuffle(catalog.Normal, 1).Take(4).Append(Shuffle(catalog.Special, 2)[0]).ToImmutableArray();
        var hard = Shuffle(catalog.Hard, 3).Take(7).ToImmutableArray();
        return new(period, normal, hard)
        {
            NormalWeakness = [.. normal.Take(4).Select(row => (byte)(Score(row.MapId, 4) % 4)), 4],
            HardWeakness = [.. hard.Select(row => (byte)(Score(row.MapId, 5) % 4))],
            CommonWeakness = (byte)(Score(0, 6) % 2)
        };
    }

    public ImmutableArray<ImperialArenaOpponent> Opponents(ImperialArenaMode mode) => mode switch
    {
        ImperialArenaMode.Normal => Normal,
        ImperialArenaMode.Hard => Hard,
        _ => throw new InvalidDataException("帝国竞技场模式无效")
    };
}

internal sealed record ImperialArenaAdmission(ImperialArenaCharacters Characters, ImperialArenaMode Mode,
    ImperialArenaDailyRoute Route, long AdmittedAt, int MinimumHardClearMilliseconds)
{
    public Character Primary => Characters.Primary;
    public Character Standby => Characters.Standby;
    public ImperialArenaConfiguration Configuration => Characters.Projection.State.Configuration;
    public int DungeonId => Mode switch { ImperialArenaMode.Normal => 320, ImperialArenaMode.Hard => 336, _ => 0 };
    public void Validate()
    {
        Configuration.Validate();
        if (Configuration.Revision <= 0 || Configuration.First != Primary.Id || Configuration.Second != Standby.Id ||
            Primary.Level < 90 || Standby.Level < 90 || Primary.IsPvpPrivate || Standby.IsPvpPrivate ||
            !Primary.Learning.Initialized || !Standby.Learning.Initialized || DungeonId == 0 || AdmittedAt < 0 || MinimumHardClearMilliseconds <= 0 ||
            Route.Period != Characters.Projection.State.Period || Route.Normal.Length != 5 || Route.Hard.Length != 7 ||
            Characters.Projection.State.PaidMode != Mode)
            throw new InvalidDataException("帝国竞技场准入缺少本日已付费模式及真实双角色资料");
    }
}

internal readonly record struct ImperialArenaRewardItem(int ItemId, int Count);

internal static class ImperialArenaRewardPolicy
{
    public static ImmutableArray<ImperialArenaRewardItem> Normal(ImperialArenaCatalog catalog, bool special, Func<int, int> draw)
    {
        var result = ImmutableArray.CreateBuilder<ImperialArenaRewardItem>();
        foreach (var group in (special ? catalog.SpecialRewards : catalog.NormalRewards).GroupBy(row => row.Group).OrderBy(row => row.Key))
        {
            int value = draw(100);
            if (value is < 0 or >= 100) throw new InvalidDataException("帝国竞技场奖励抽样越界");
            int accumulated = 0;
            foreach (var row in group)
            {
                accumulated += row.Weight;
                if (value >= accumulated) continue;
                result.Add(new(row.ItemId, row.Count));
                break;
            }
        }
        return result.ToImmutable();
    }

    // 困难配置是累计表，发放本日新增差额，不能把每一行再相加或重复领前几轮。
    public static ImmutableArray<ImperialArenaRewardItem> HardDelta(ImperialArenaCatalog catalog, int previousRound, int clearedRound)
    {
        if (previousRound is < 0 or > 7 || clearedRound is < 1 or > 7)
            throw new InvalidDataException("帝国竞技场困难奖励回合越界");
        if (clearedRound <= previousRound) return [];
        var previous = catalog.HardRewards.Where(row => row.Group == previousRound - 1).ToDictionary(row => row.ItemId, row => row.Count);
        return catalog.HardRewards.Where(row => row.Group == clearedRound - 1).Select(row => new ImperialArenaRewardItem(row.ItemId,
            row.Count - previous.GetValueOrDefault(row.ItemId))).Where(row => row.Count > 0).ToImmutableArray();
    }
}

internal interface IImperialArenaStore
{
    ImmutableArray<uint> CompleteImperialArenaRuns(AccountOwners.Lease owner, Guid? instance = null);
    ImperialArenaDailyRoute ReadImperialArenaRoute(AccountOwners.Lease owner, ImperialArenaCatalog catalog, ImperialArenaPeriod period);
    ImperialArenaState ReadImperialArenaState(AccountOwners.Lease owner, ImperialArenaPeriod period);
    ImperialArenaConfiguration SaveImperialArenaConfiguration(AccountOwners.Lease owner, ImperialArenaConfiguration expected,
        ImperialArenaConfiguration desired);
    bool MatchesImperialArenaCharacters(AccountOwners.Lease owner, ImperialArenaCharacters expected);
    Character PurchaseImperialArenaEntry(AccountOwners.Lease owner, Character expected, ImperialArenaMode mode,
        ImperialArenaPeriod period, int gold);
}
