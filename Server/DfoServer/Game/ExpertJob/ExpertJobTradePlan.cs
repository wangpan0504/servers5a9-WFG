using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.ExpertJob;

// 顾客投入和产出归顾客；摊主只收服务费并更新副职业经验与耐久。
internal sealed record ExpertJobTradePlan(Character CustomerExpected, Character OwnerExpected,
    InventoryPlan CustomerInventory, InventoryPlan OwnerInventory, int OwnerExperience, int OwnerEndurance, uint Fee,
    uint SystemGoldCost = 0, uint CustomerGoldReward = 0);

internal sealed record ExpertJobTradeCommit(Character Customer, Character Owner);
