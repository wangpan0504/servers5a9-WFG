using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal readonly record struct ImperialArenaOpponent(int MapId, int MonsterId);
internal readonly record struct ImperialArenaReward(int Group, int ItemId, int Count, int Weight);

// 帝国与再战共用TAG客户端，但各自解释资源和费用，不把再战的周次数/复活规则带入帝国。
internal sealed class ImperialArenaCatalog
{
    public const string ResourcePath = "etc/tagtournament/tagtournament.etc";
    public int EntryGold { get; }
    public int SwitchCooldownMilliseconds { get; }
    public ImmutableArray<ImperialArenaOpponent> Normal { get; }
    public ImmutableArray<ImperialArenaOpponent> Special { get; }
    public ImmutableArray<ImperialArenaOpponent> Hard { get; }
    public ImmutableArray<ImperialArenaReward> NormalRewards { get; }
    public ImmutableArray<ImperialArenaReward> SpecialRewards { get; }
    public ImmutableArray<ImperialArenaReward> HardRewards { get; }
    public ImmutableArray<int> QuickItems { get; }
    public ImmutableArray<float> HardBalance { get; }
    public int NormalBuff { get; }
    public int HardBuff { get; }
    public int MinimumHardClearMilliseconds { get; }
    public int ChallengeStandardRank { get; }
    public int ChallengeDefaultMilliseconds { get; }
    public int ChallengeRewardItemId { get; }

    public ImperialArenaCatalog(PvfArchive archive, Items.ItemCatalog? items = null) : this(archive.ReadTokens(ResourcePath), archive.ResolveString)
    {
        if (items is not null) ValidateRewardTemplates(items);
    }

    internal void ValidateRewardTemplates(Items.ItemCatalog items)
    {
        // 统一结算会合并冻结附件；启动时保证完整困难累计仍是一份合法永久堆叠。
        foreach (var group in NormalRewards.Concat(SpecialRewards).Concat(HardRewards).GroupBy(row => row.ItemId))
        {
            var template = items.Get(group.Key);
            if (template.Kind != Items.ItemKind.Material || template.Expiration != new Items.ItemExpiration() ||
                !template.AttachType.Contains("[account]", StringComparison.Ordinal) || template.StackLimit < group.Max(row => row.Count))
                throw new InvalidDataException("帝国竞技场材料必须支持永久邮件与完整累计堆叠: " + group.Key);
        }
    }

    internal ImperialArenaCatalog(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        // 当前正式表的hard reward以[/tag tournament reward hard]结束，保留这项已核资源别名；其余闭合仍严格核验。
        var fields = new Dictionary<string, ImmutableArray<PvfToken>>(StringComparer.Ordinal);
        for (int index = 0; index < tokens.Count;)
        {
            if (tokens[index].Type != 3) throw new InvalidDataException("帝国竞技场字段缺少标签");
            string tag = resolve(tokens[index++].Value).Trim();
            if (tag.Length < 3 || tag[0] != '[' || tag[^1] != ']' || tag[1] == '/')
                throw new InvalidDataException("帝国竞技场标签无效");
            string name = tag[1..^1];
            int start = index;
            while (index < tokens.Count && tokens[index].Type != 3) index++;
            if (!fields.TryAdd(name, tokens.Skip(start).Take(index - start).ToImmutableArray()))
                throw new InvalidDataException("帝国竞技场字段重复: " + name);
            if (index < tokens.Count && resolve(tokens[index].Value).StartsWith("[/", StringComparison.Ordinal))
            {
                string close = resolve(tokens[index++].Value);
                if (close != "[/" + name + "]" && !(name == "tag tournament hard reward" && close == "[/tag tournament reward hard]"))
                    throw new InvalidDataException("帝国竞技场字段未正确闭合: " + name);
            }
        }
        EntryGold = Scalar("tag tournament enter gold");
        SwitchCooldownMilliseconds = checked(Scalar("tag tournament dungeon charac tag cool time") * 1000);
        NormalBuff = Scalar("tag tournament buff add stat");
        HardBuff = Scalar("tag tournament hard mode buff add state");
        MinimumHardClearMilliseconds = Scalar("tag tournament hard mode min clear time");
        var standard = Integers("tag tournament hard mode time attack standard rank");
        var challengeReward = Integers("tag tournament hard mode time attack reward");
        if (standard is not [> 0, > 0] || challengeReward is not [> 0, > 0, > 0, > 0] ||
            standard[1] < MinimumHardClearMilliseconds)
            throw new InvalidDataException("帝国每周挑战配置不完整");
        ChallengeStandardRank = standard[0]; ChallengeDefaultMilliseconds = standard[1];
        ChallengeRewardItemId = challengeReward[0];
        Normal = Opponents("tag tournament random map", 4);
        Special = Opponents("tag tournament special map", 1);
        Hard = Opponents("tag tournament hard map", 7);
        NormalRewards = Rewards("tag tournament reward", true);
        SpecialRewards = Rewards("tag tournament special reward", true);
        HardRewards = Rewards("tag tournament hard reward", false);
        var hardItemIds = HardRewards.Where(row => row.Group == 0).Select(row => row.ItemId).Order().ToArray();
        if (HardRewards.GroupBy(row => row.Group).Any(group => !group.Select(row => row.ItemId).Order().SequenceEqual(hardItemIds)) ||
            HardRewards.GroupBy(row => row.ItemId).Any(group => group.OrderBy(row => row.Group).Select(row => row.Count)
                .Zip(group.OrderBy(row => row.Group).Skip(1).Select(row => row.Count)).Any(pair => pair.Second < pair.First)))
            throw new InvalidDataException("帝国困难累计奖励必须保持同一材料并逐回合不递减");
        QuickItems = Integers("tag tournament stackable");
        if (QuickItems.Length != 6 || QuickItems.Any(id => id <= 0) || QuickItems.Distinct().Count() != 6)
            throw new InvalidDataException("帝国竞技场专用快槽必须为六个不同物品");
        var balance = Field("tag tournament hard mode balance");
        if (balance.Length != 8 || balance[0] != new PvfToken(0, 7) || balance.Skip(1).Any(token => token.Type != 2))
            throw new InvalidDataException("帝国竞技场困难倍率必须完整覆盖七回合");
        HardBalance = balance.Skip(1).Select(token => BitConverter.Int32BitsToSingle(token.Value)).ToImmutableArray();
        if (HardBalance.Any(value => !float.IsFinite(value) || value <= 0))
            throw new InvalidDataException("帝国竞技场困难倍率无效");
        if (!Integers("tag tournament dungeon list").SequenceEqual([320, 336, 9608, 400000095]))
            throw new InvalidDataException("帝国竞技场副本类型表与当前客户端不一致");

        ImmutableArray<PvfToken> Field(string name) => fields.TryGetValue(name, out var value)
            ? value : throw new InvalidDataException("帝国竞技场缺少字段: " + name);
        ImmutableArray<int> Integers(string name)
        {
            var value = Field(name);
            if (value.IsEmpty || value.Any(token => token.Type != 0)) throw new InvalidDataException("帝国竞技场整数字段无效: " + name);
            return value.Select(token => token.Value).ToImmutableArray();
        }
        int Scalar(string name) => Integers(name) is [var value] && value > 0 ? value
            : throw new InvalidDataException("帝国竞技场标量无效: " + name);
        ImmutableArray<ImperialArenaOpponent> Opponents(string name, int rounds)
        {
            var value = Integers(name);
            if (value.Length < 3 || value.Length % 2 != 1 || value[0] != rounds || (value.Length - 1) / 2 < rounds)
                throw new InvalidDataException("帝国竞技场地图候选数量无效: " + name);
            var rows = Enumerable.Range(0, (value.Length - 1) / 2).Select(i => new ImperialArenaOpponent(value[1 + i * 2], value[2 + i * 2])).ToImmutableArray();
            if (rows.Any(row => row.MapId <= 0 || row.MonsterId <= 0) || rows.Select(row => row.MapId).Distinct().Count() != rows.Length)
                throw new InvalidDataException("帝国竞技场地图候选重复或无效");
            return rows;
        }
        ImmutableArray<ImperialArenaReward> Rewards(string name, bool weighted)
        {
            var value = Integers(name);
            int width = weighted ? 4 : 3;
            if (value.Length % width != 0) throw new InvalidDataException("帝国竞技场奖励行截断");
            var rows = Enumerable.Range(0, value.Length / width).Select(i => new ImperialArenaReward(value[i * width], value[i * width + 1],
                value[i * width + 2], weighted ? value[i * width + 3] : 1)).ToImmutableArray();
            if (rows.IsEmpty || rows.Any(row => row.Group < 0 || row.ItemId <= 0 || row.Count <= 0 || row.Weight <= 0) ||
                weighted && rows.GroupBy(row => row.Group).Any(group => group.Sum(row => (long)row.Weight) != 100) ||
                !weighted && (rows.Length != 14 || !rows.Select(row => row.Group).Distinct().Order().SequenceEqual(Enumerable.Range(0, 7)) ||
                    rows.GroupBy(row => row.Group).Any(group => group.Count() != 2 || group.Select(row => row.ItemId).Distinct().Count() != 2)))
                throw new InvalidDataException("帝国竞技场奖励组或权重无效");
            return rows;
        }
    }
}
