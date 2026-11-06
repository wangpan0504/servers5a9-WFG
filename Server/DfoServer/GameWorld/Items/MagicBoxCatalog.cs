using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record MagicBoxReward(int ItemId, long Weight, int Count, bool Broadcast = false);
internal sealed record MagicBoxPool(ImmutableArray<MagicBoxReward> Rewards);
internal sealed record MagicBoxDefinition(ImmutableArray<MagicBoxPool> Pools, int MaterialItemId, int MaterialCount,
    string? PendingRule = null)
{
    public bool Available => PendingRule is null && Pools.Length != 0 &&
        (MaterialItemId == 0 && MaterialCount == 0 || MaterialItemId > 0 && MaterialCount > 0);
}

internal sealed class MagicBoxCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable)
{
    readonly ConcurrentDictionary<int, MagicBoxDefinition> definitions = new();

    public MagicBoxDefinition Get(int itemId) => definitions.GetOrAdd(itemId, id =>
    {
        // 赛丽亚幸运盒在部分资源索引中没有出现在 stackable.lst，但其脚本仍
        // 由客户端按该物品号直接使用；保留 PVF 中的真实脚本路径作为索引回退。
        if (!stackable.TryGetValue(id, out string? path) && id == 2682272)
            path = "ect/chn_random/chn_blessed_box.stk";
        return path is null ? new([], 0, 0, "非随机盒物品") : Parse(archive.ReadText("stackable/" + path));
    });

    internal static MagicBoxDefinition Parse(string text)
    {
        string type = Regex.Match(PvfScript.Section(text, "stackable type"), @"`\[([^\]]+)\]`").Groups[1].Value;
        // 官方资源还存在 random ss / random goblin 等前缀；它们仍沿用
        // 同一套随机盒奖池布局，不能因为标签前缀不同而落到“不支持”。
        if (!type.StartsWith("random ", StringComparison.OrdinalIgnoreCase) ||
            !type.EndsWith("upgradable legacy", StringComparison.OrdinalIgnoreCase))
            return new([], 0, 0, "其他开箱类型");
        var boxes = Regex.Matches(text,
            @"\[RANDOMBOX(?:_[A-Z0-9]+)?\](.*?)\[/RANDOMBOX(?:_[A-Z0-9]+)?\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (boxes.Count != 1)
            return new([], 0, 0, "随机盒定义缺失或重复");
        string body = boxes[0].Groups[1].Value;
        var pools = ImmutableArray.CreateBuilder<MagicBoxPool>();
        foreach (Match block in Regex.Matches(body, @"\[int data\](.*?)\[/int data\]", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            int[] values = PvfScript.Integers(block.Groups[1].Value);
            if (values.Length < 7 || (values.Length - 3) % 4 != 0)
                return new([], 0, 0, "随机盒奖池行宽无效");
            var rewards = ImmutableArray.CreateBuilder<MagicBoxReward>();
            for (int i = 3; i < values.Length; i += 4)
                if (values[i] <= 0 || values[i + 1] <= 0 || values[i + 2] <= 0)
                    return new([], 0, 0, "随机盒奖励字段无效");
                else
                    rewards.Add(new(values[i], values[i + 1], values[i + 2], values[i + 3] != 0));
            if (rewards.Count != 0)
                pools.Add(new(rewards.ToImmutable()));
        }
        int materialId = 0, materialCount = 0;
        string material = PvfScript.Block(body, "sealing removal item");
        int[] materialValues = PvfScript.Integers(material);
        if (materialValues.Length >= 3 && materialValues[1] > 0 && materialValues[2] > 0)
        {
            materialId = materialValues[1];
            materialCount = materialValues[2];
        }
        return new(pools.ToImmutable(), materialId, materialCount);
    }
}
