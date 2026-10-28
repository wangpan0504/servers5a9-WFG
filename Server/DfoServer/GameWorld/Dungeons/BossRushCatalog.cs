using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal readonly record struct BossRushRankReward(int Rank, int ItemId, int Count, int Weight);

internal sealed class BossRushCatalog
{
    public const string ResourcePath = "etc/dungeonetc/bossrushdungeoninfo.etc";
    public ImmutableArray<int> Bosses { get; }
    public int BossTypeCount { get; }
    public int NamedTypeCount { get; }
    public ImmutableArray<int> SoulPoints { get; }
    public ImmutableArray<int> RankThresholds { get; }
    public ImmutableArray<BossRushRankReward> RankRewards { get; }

    public BossRushCatalog(PvfArchive archive) : this(archive.ReadTokens(ResourcePath), archive.ResolveString) { }

    internal BossRushCatalog(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("BossRush 专表块结构异常");
        var sections = document.Sections;
        Bosses = Integers("boss list");
        if (Bosses.Length != 24 || Bosses.Any(id => id <= 0) || Bosses.Distinct().Count() != Bosses.Length)
            throw new InvalidDataException("BossRush [boss list] 必须是 24 个不重复正整数");
        BossTypeCount = Scalar("boss type count", 1, Bosses.Length);
        NamedTypeCount = Scalar("named type count", 1, Bosses.Length - BossTypeCount);
        if (BossTypeCount + NamedTypeCount >= Bosses.Length)
            throw new InvalidDataException("BossRush 具名数量不能覆盖整份名单");
        SoulPoints = Integers("soul point");
        if (SoulPoints.Length != 3 || SoulPoints.Any(value => value <= 0))
            throw new InvalidDataException("BossRush [soul point] 必须是三个正整数");
        RankThresholds = Integers("rank info");
        if (RankThresholds.Length is < 1 or > 4 || RankThresholds.Any(value => value <= 0) ||
            RankThresholds.Zip(RankThresholds.Skip(1), (left, right) => left < right).Any(ordered => !ordered))
            throw new InvalidDataException("BossRush [rank info] 必须是递增正阈值");
        var rewardValues = Integers("rank reward");
        if (rewardValues.Length == 0 || rewardValues.Length % 4 != 0)
            throw new InvalidDataException("BossRush [rank reward] 记录不完整");
        var rewards = ImmutableArray.CreateBuilder<BossRushRankReward>(rewardValues.Length / 4);
        for (int i = 0; i < rewardValues.Length; i += 4)
        {
            int rank = rewardValues[i], item = rewardValues[i + 1], count = rewardValues[i + 2], weight = rewardValues[i + 3];
            if (rank is < 1 or > 4 || item <= 0 || count <= 0 || weight <= 0)
                throw new InvalidDataException("BossRush 段位奖励行列非法");
            rewards.Add(new(rank, item, count, weight));
        }
        RankRewards = rewards.ToImmutable();
        for (int rank = 1; rank <= RankThresholds.Length; rank++)
        {
            var group = RankRewards.Where(row => row.Rank == rank).ToArray();
            if (group.Length == 0 || group.Sum(row => row.Weight) != 10000)
                throw new InvalidDataException("BossRush 段位权重必须互斥且合计 10000");
        }

        int Scalar(string name, int minimum, int maximum)
        {
            var row = Required(name);
            if (row.IsBlock || row.Values.Length != 1) throw new InvalidDataException("BossRush 标量形状非法: " + name);
            int value = Integer(row.Values[0]);
            return value >= minimum && value <= maximum ? value : throw new InvalidDataException("BossRush 标量越界: " + name);
        }
        ImmutableArray<int> Integers(string name)
        {
            var row = Required(name);
            if (!row.IsBlock || row.Values.Length == 0) throw new InvalidDataException("BossRush 列表缺失: " + name);
            return [.. row.Values.Select(Integer)];
        }
        PvfTokenSection Required(string name)
        {
            var found = sections.Where(row => row.Name == name).ToArray();
            return found.Length == 1 ? found[0] : throw new InvalidDataException("BossRush 专表字段缺失或重复: " + name);
        }
        static int Integer(PvfToken value) => value.Type == 0 ? value.Value : throw new InvalidDataException("BossRush 专表需要整数");
    }

    public int IndexOf(int monsterId)
    {
        for (int i = 0; i < Bosses.Length; i++)
            if (Bosses[i] == monsterId) return i;
        return -1;
    }

    public int SoulFor(int index)
    {
        if (index < 0 || index >= Bosses.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (index < BossTypeCount) return SoulPoints[2];
        if (index < BossTypeCount + NamedTypeCount) return SoulPoints[1];
        return SoulPoints[0];
    }

    public byte RankOf(int score)
    {
        byte rank = 0;
        for (int i = 0; i < RankThresholds.Length; i++)
            if (score >= RankThresholds[i]) rank = (byte)(i + 1);
        return rank;
    }

    public BossRushRankReward? Roll(byte rank, Func<int, int> draw)
    {
        if (rank == 0) return null;
        var group = RankRewards.Where(row => row.Rank == rank).ToArray();
        if (group.Length == 0) return null;
        int total = group.Sum(row => row.Weight);
        int roll = draw(total);
        if (roll < 0 || roll >= total) throw new InvalidDataException("BossRush 段位抽签越界");
        int acc = 0;
        foreach (var row in group)
        {
            acc += row.Weight;
            if (roll < acc) return row;
        }
        return null;
    }
}
