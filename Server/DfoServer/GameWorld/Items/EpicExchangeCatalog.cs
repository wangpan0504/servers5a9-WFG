using PvfLib;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld.Items;

internal sealed record EpicExchangeGroup(int Index, ImmutableArray<int> RewardItemIds, ItemMaterialCost ExchangeItem);

// 休亚原生史诗交换（etc/epicitemexchange.etc）。费用系数与交换次数上限来自客户端反汇编：
// 源装备的交换次数取自实例位 bits1..5，费用 = 基础金币 × 系数[次数]。
// 三组 [reward epic item]/[exchange item] 属于 190307_preyraid 的史诗交换窗，本命令（道具转换窗）
// 只用顶层费用段；奖励并集仍完整解析，作为该 etc 的其余内容保留。
internal sealed class EpicExchangeCatalog
{
    // 客户端物品信息只在 count < 4 时显示交换行，count 来自 bits1..5。
    public const int CountLimit = 4;
    // 休亚：用材料兑换泰波尔斯史诗的唯一商人。商店直购的装备与在售礼盒的产物都按这个来源记资格。
    public const int MerchantNpcId = 486;
    const int CostUnit = 1_000_000;

    public EpicExchangeCatalog(PvfArchive pvf) : this(pvf.ReadText("etc/epicitemexchange.etc")) { }

    internal EpicExchangeCatalog(string text)
    {
        int[] required = PvfScript.Integers(PvfScript.Section(text, "required item"));
        if (required.Length != 2 || required[0] <= 0 || required[1] <= 0)
            throw new InvalidDataException("PVF 史诗交换必需材料不完整");
        RequiredItem = new ItemMaterialCost(required[0], required[1]);

        int[] gold = PvfScript.Integers(PvfScript.Section(text, "require default gold"));
        if (gold.Length != 1 || gold[0] <= 0)
            throw new InvalidDataException("PVF 史诗交换基础金币无效");
        BaseGold = gold[0];

        var scale = ImmutableArray.CreateBuilder<float>();
        foreach (Match match in Regex.Matches(PvfScript.Block(text, "cost scale"),
                     @"(\d+)\s+([-+]?\d+(?:\.\d+)?)", RegexOptions.CultureInvariant))
        {
            int index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            float value = float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (index != scale.Count + 1 || !float.IsFinite(value) || value <= 0)
                throw new InvalidDataException("PVF 史诗交换费用系数不连续");
            scale.Add(value);
        }
        if (scale.Count == 0)
            throw new InvalidDataException("PVF 史诗交换费用系数缺失");
        Scale = scale.ToImmutable();

        var rewards = ParseBlocks(text, "reward epic item");
        var exchange = ParseBlocks(text, "exchange item");
        if (rewards.Length == 0 || rewards.Length != exchange.Length)
            throw new InvalidDataException("PVF 史诗交换组不完整");
        var groups = ImmutableArray.CreateBuilder<EpicExchangeGroup>();
        for (int index = 0; index < rewards.Length; index++)
        {
            var rewardIds = rewards[index];
            var exchangeRow = exchange[index];
            if (rewardIds.Any(id => id <= 0) || exchangeRow[0] <= 0 || exchangeRow[1] <= 0)
                throw new InvalidDataException("PVF 史诗交换组内容无效");
            groups.Add(new(index + 1, rewardIds, new(exchangeRow[0], exchangeRow[1])));
        }
        Groups = groups.ToImmutable();
        Rewards = Groups.SelectMany(group => group.RewardItemIds).ToFrozenSet();
    }

    public ItemMaterialCost RequiredItem { get; }
    public int BaseGold { get; }
    public ImmutableArray<float> Scale { get; }
    public ImmutableArray<EpicExchangeGroup> Groups { get; }
    public FrozenSet<int> Rewards { get; }
    public bool IsReward(int itemId) => Rewards.Contains(itemId);

    // 反汇编证明的运算顺序：float 乘系数 → CVTTSS2SI 截断 → 有符号整除 1,000,000 → 乘回。
    // 因此 5,000,000×1.50 先截成 7,500,000，再整除丢余数，实扣 7,000,000。
    public bool TryCost(int count, out int cost)
    {
        cost = 0;
        if (count < 0 || count >= Scale.Length)
            return false;
        float product = BaseGold * Scale[count];
        if (!float.IsFinite(product) || product < 0 || product >= int.MaxValue)
            return false;
        cost = (int)product / CostUnit * CostUnit;
        return true;
    }

    // 同一标签在 etc 内重复出现三组；组号必须从 1 连续，只取首个整数做组号。
    static ImmutableArray<ImmutableArray<int>> ParseBlocks(string text, string tag)
    {
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<int>>();
        foreach (Match match in Regex.Matches(text, @"\[" + Regex.Escape(tag) + @"\](.*?)\[/" + Regex.Escape(tag) + @"\]",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            int[] values = PvfScript.Integers(match.Groups[1].Value);
            if (values.Length < 2 || values[0] != rows.Count + 1)
                throw new InvalidDataException("PVF 史诗交换组号不连续: " + tag);
            rows.Add([.. values.Skip(1)]);
        }
        return rows.ToImmutable();
    }
}
