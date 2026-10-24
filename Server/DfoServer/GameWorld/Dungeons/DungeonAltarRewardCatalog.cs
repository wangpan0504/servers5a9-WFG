using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 祭坛结算读取自己的资源轴；不借用普通怪物掉落概率或普通副本通关倍率。
internal sealed record DungeonAltarRewardCatalog(ImmutableArray<int> ItemProbabilities, float GoldWeight,
    ImmutableArray<float> RoundExperience, ImmutableArray<float> EntryExperience,
    ImmutableArray<int> UltimatePoints, ImmutableArray<ImmutableArray<int>> UltimateRewards)
{
    public const string ResourcePath = "etc/bloodclearreward.etc";

    public static DungeonAltarRewardCatalog Load(PvfArchive archive) => Read(archive.ReadTokens(ResourcePath), archive.ResolveString);

    internal static DungeonAltarRewardCatalog Read(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("祭坛奖励资源闭合异常");
        ImmutableArray<PvfToken> Field(string name)
        {
            var matches = document.Sections.Where(row => row.Name == name).ToArray();
            return matches is [{ Children.Length: 0 } row] && !row.Values.IsEmpty ? row.Values :
                throw new InvalidDataException("祭坛奖励字段缺失、重复或嵌套：" + name);
        }
        ImmutableArray<int> Integers(string name, int? count = null)
        {
            var values = Field(name);
            if (count is { } expected && values.Length != expected || values.Any(token => token.Type != 0 || token.Value < 0))
                throw new InvalidDataException("祭坛奖励整数字段无效：" + name);
            return values.Select(token => token.Value).ToImmutableArray();
        }
        ImmutableArray<float> Numbers(string name)
        {
            var values = Field(name);
            if (values.Any(token => token.Type is not (0 or 2))) throw new InvalidDataException("祭坛奖励数值类型无效：" + name);
            var result = values.Select(token => token.Type == 0 ? token.Value : BitConverter.Int32BitsToSingle(token.Value)).ToImmutableArray();
            if (result.Any(value => !float.IsFinite(value) || value < 0)) throw new InvalidDataException("祭坛奖励倍率无效：" + name);
            return result;
        }
        var probability = Integers("reward item prob", 3);
        var gold = Numbers("reward gold weight");
        var rounds = Numbers("reward exp weight");
        var entries = Numbers("reward exp inout count weight");
        var points = Integers("ultimate difficulty point", 2);
        var ultimate = Integers("ultimate reward prob");
        if (probability.Any(value => value > 100) || probability[0] + probability[1] > 100 || gold.Length != 1 ||
            rounds.Length != 16 || entries.Length < 2 || ultimate.Length % 3 != 0)
            throw new InvalidDataException("祭坛奖励表维度或概率越界");
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<int>>();
        int previous = int.MaxValue;
        for (int at = 0; at < ultimate.Length; at += 3)
        {
            if (ultimate[at] >= previous || ultimate[at + 1] > 10000 || ultimate[at + 2] > 10000)
                throw new InvalidDataException("极限祭坛积分表未递减或概率越界");
            rows.Add(ultimate.AsSpan(at, 3).ToArray().ToImmutableArray());
            previous = ultimate[at];
        }
        return new(probability, gold[0], rounds, entries, points, rows.ToImmutable());
    }

    public uint Experience(int completedRounds, uint monsterExperience, int enteredToday, bool ultimate)
    {
        if (completedRounds < 0 || completedRounds >= RoundExperience.Length || enteredToday < 1)
            throw new ArgumentOutOfRangeException();
        // 历史端极限常量为10；无尽使用已经扣次数后的入场序号，超过表尾取最后一项。
        double amount = (double)completedRounds * monsterExperience * RoundExperience[completedRounds] *
            (ultimate ? 10 : EntryExperience[Math.Min(enteredToday, EntryExperience.Length - 1)]);
        if (!double.IsFinite(amount) || amount > uint.MaxValue) throw new InvalidDataException("祭坛经验溢出");
        // beginBloodClearProcess在所有加成完成后还有限制；基础计算不提前截断到该上限。
        return checked((uint)Math.Truncate(amount));
    }
}
