using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal readonly record struct TitleChangeWeightedItem(int TitleItemId, int Weight);

internal sealed record TitleChangeMainEntry(
    int ThreadItemId,
    int SuccessRate,
    FrozenDictionary<int, int> ResourceGoldCosts,
    FrozenSet<int> FinalTitleItemIds);

internal sealed record TitleChangeSubEntry(
    int ResourceTitleItemId,
    ImmutableArray<TitleChangeWeightedItem> SuccessPool,
    ImmutableArray<TitleChangeWeightedItem> FailPool)
{
    public int Select(bool isSuccess, Func<int, int> nextRandom)
    {
        var pool = isSuccess ? SuccessPool : FailPool;
        int totalWeight = pool.Sum(item => item.Weight);
        int roll = nextRandom(totalWeight);
        if (roll < 0 || roll >= totalWeight)
            throw new InvalidOperationException("称号变更权重抽样越界");
        int accumulated = 0;
        foreach (var entry in pool)
        {
            accumulated += entry.Weight;
            if (roll < accumulated) return entry.TitleItemId;
        }
        return pool[^1].TitleItemId;
    }
}

// etc/aradtitlechange_main.etc 与 etc/aradtitlechange_sub.etc：
// 主表定义变更道具的成功率（万分比）、可洗练称号及金币费用、最终毕业称号列表；
// 子表定义每个可洗练称号在成功分支与失败分支下的目标称号权重池。
internal sealed class TitleChangeCatalog
{
    public const string MainResourcePath = "etc/aradtitlechange_main.etc";
    public const string SubResourcePath = "etc/aradtitlechange_sub.etc";

    readonly FrozenDictionary<int, TitleChangeMainEntry> mains;
    readonly FrozenDictionary<int, TitleChangeSubEntry> subs;

    public TitleChangeCatalog(PvfArchive archive)
        : this(
            archive.Contains(MainResourcePath) ? archive.ReadText(MainResourcePath)
                : throw new InvalidDataException("PVF缺少称号变更主表: " + MainResourcePath),
            archive.Contains(SubResourcePath) ? archive.ReadText(SubResourcePath)
                : throw new InvalidDataException("PVF缺少称号变更子表: " + SubResourcePath))
    { }

    internal TitleChangeCatalog(string mainText, string subText)
    {
        var parsedSubs = new Dictionary<int, TitleChangeSubEntry>();
        int subIndex = 0;
        foreach (Match match in Regex.Matches(subText, @"\[resource title item id\](.*?)\[/resource title item id\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            subIndex++;
            string body = match.Groups[1].Value;
            int successPos = body.IndexOf("[success title item]", StringComparison.OrdinalIgnoreCase);
            if (successPos < 0)
                throw new InvalidDataException($"PVF称号变更子表缺少成功池: 第{subIndex}项");
            int[] resourceIds = Integers(body[..successPos]);
            var successPool = ParsePool(Block(body, "success title item"), subIndex, "success");
            var failPool = ParsePool(Block(body, "fail title item"), subIndex, "fail");
            if (resourceIds.Length != 1 || resourceIds[0] <= 0 || !parsedSubs.TryAdd(resourceIds[0], new(resourceIds[0], successPool, failPool)))
                throw new InvalidDataException($"PVF称号变更子表项无效: 第{subIndex}项");
        }
        if (subIndex == 0)
            throw new InvalidDataException("PVF称号变更子表为空");

        var parsedMains = new Dictionary<int, TitleChangeMainEntry>();
        int mainIndex = 0;
        foreach (Match match in Regex.Matches(mainText, @"\[title change item id\](.*?)\[/title change item id\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            mainIndex++;
            string body = match.Groups[1].Value;
            int successPos = body.IndexOf("[success rate]", StringComparison.OrdinalIgnoreCase);
            if (successPos < 0)
                throw new InvalidDataException($"PVF称号变更主表缺少成功率: 第{mainIndex}项");
            int[] itemIds = Integers(body[..successPos]);
            int[] rates = Integers(Section(body, "success rate"));
            int[] resourcePairs = Integers(Block(body, "resource title item"));
            int[] finalIds = Integers(Block(body, "final title item id"));
            if (itemIds.Length != 1 || itemIds[0] <= 0 || rates.Length != 1 || rates[0] is < 0 or > 10_000 ||
                resourcePairs.Length == 0 || resourcePairs.Length % 2 != 0 || finalIds.Any(id => id <= 0))
                throw new InvalidDataException($"PVF称号变更主表项无效: 第{mainIndex}项");
            var costs = new Dictionary<int, int>(resourcePairs.Length / 2);
            for (int index = 0; index < resourcePairs.Length; index += 2)
            {
                int titleId = resourcePairs[index];
                int goldCost = resourcePairs[index + 1];
                if (titleId <= 0 || goldCost < 0 || !parsedSubs.ContainsKey(titleId) || !costs.TryAdd(titleId, goldCost))
                    throw new InvalidDataException($"PVF称号变更主表资源称号无效: 第{mainIndex}项 {titleId}");
            }
            var finals = finalIds.ToFrozenSet();
            if (costs.Keys.Any(finals.Contains) ||
                !parsedMains.TryAdd(itemIds[0], new(itemIds[0], rates[0], costs.ToFrozenDictionary(), finals)))
                throw new InvalidDataException($"PVF称号变更主表冲突: 第{mainIndex}项");
        }
        if (mainIndex == 0)
            throw new InvalidDataException("PVF称号变更主表为空");

        mains = parsedMains.ToFrozenDictionary();
        subs = parsedSubs.ToFrozenDictionary();
    }

    public bool TryGetMain(int threadItemId, out TitleChangeMainEntry entry) => mains.TryGetValue(threadItemId, out entry!);
    public bool TryGetSub(int resourceTitleItemId, out TitleChangeSubEntry entry) => subs.TryGetValue(resourceTitleItemId, out entry!);

    static ImmutableArray<TitleChangeWeightedItem> ParsePool(string text, int subIndex, string branch)
    {
        int[] pairs = Integers(text);
        if (pairs.Length == 0 || pairs.Length % 2 != 0)
            throw new InvalidDataException($"PVF称号变更子表权重池无效: 第{subIndex}项 {branch}");
        var pool = ImmutableArray.CreateBuilder<TitleChangeWeightedItem>(pairs.Length / 2);
        long totalWeight = 0;
        for (int index = 0; index < pairs.Length; index += 2)
        {
            int titleId = pairs[index];
            int weight = pairs[index + 1];
            if (titleId <= 0 || weight <= 0)
                throw new InvalidDataException($"PVF称号变更子表权重项无效: 第{subIndex}项 {branch}");
            totalWeight += weight;
            pool.Add(new(titleId, weight));
        }
        if (totalWeight <= 0 || totalWeight > int.MaxValue)
            throw new InvalidDataException($"PVF称号变更子表总权重越界: 第{subIndex}项 {branch}");
        return pool.MoveToImmutable();
    }
}
