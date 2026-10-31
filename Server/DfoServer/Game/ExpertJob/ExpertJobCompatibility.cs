using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.ExpertJob;

// 明确的本服兼容策略，不宣称这些缺少同版本服务端证据的参数是原版规则。
internal sealed record ExpertJobCompatibility
{
    public int DisjointEnduranceCost { get; init; } = 1;
    public int DisjointGoldCost { get; init; }
    public int DisjointRewardPermille { get; init; } = 1000;
    public int SelfServiceRewardPercent { get; init; } = 80;
    public int SelfServiceRewardItem { get; init; } = 2610045;
    public int SelfServiceRewardCount { get; init; } = 1;
    public int EnchantEnduranceCost { get; init; } = 1;
    public int EnchantGoldCost { get; init; }
    public int? EnchantSuccessPercent { get; init; }

    public ExpertJobCompatibility Validate()
    {
        if (DisjointEnduranceCost is < 1 or > 10000 || EnchantEnduranceCost is < 1 or > 10000 ||
            DisjointGoldCost is < 0 or > 100000000 || EnchantGoldCost is < 0 or > 100000000 ||
            DisjointRewardPermille is < 1 or > 10000 || SelfServiceRewardPercent is < 0 or > 100 ||
            SelfServiceRewardItem <= 0 || SelfServiceRewardCount is < 1 or > 1000 || EnchantSuccessPercent is < 0 or > 100)
            throw new InvalidDataException("副职业兼容参数超出安全范围");
        return this;
    }
}

internal sealed record ExpertJobActionPlan(Character Expected, InventoryPlan Inventory, int Experience, int Endurance,
    bool Succeeded = true, DisjointReceipt? Disjoint = null, byte? EnchantRarity = null,
    DfoServer.Game.Quests.QuestLifecyclePlan? Tutorial = null);
