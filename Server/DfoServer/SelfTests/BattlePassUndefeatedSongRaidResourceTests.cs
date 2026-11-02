using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Items;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassUndefeatedSongRaidResourceTests
{
    internal static void Run(TcpTestResources resources)
    {
        const int itemId = BattlePassUndefeatedSongSource.ItemId;
        var items = resources.ItemResources;
        var template = items.Items.Get(itemId);
        if (template is not { Kind: ItemKind.Material, AccountMaterial: { } definition } || definition.HoldingLimit < 1)
            throw new InvalidDataException("匹配资源缺少不渝之咏账号材料额度");
        var now = DateTimeOffset.UtcNow;
        var prepared = new PreparedRaidReward(Guid.Parse("00000001-0002-0003-0004-000000000005"), 0, 1, 0, 1,
            LukeResourceRewards.Source, [new(itemId, 25)], false, 1) { RaidType = RaidKind.Luke };
        foreach (int oldBalance in new[] { 0, Math.Max(0, definition.HoldingLimit - 7), definition.HoldingLimit }.Distinct())
        {
            var inventory = InventorySnapshot.Empty with
                { AccountWallet = InventorySnapshot.Empty.AccountWallet.SetMaterial(itemId, new AccountMaterialBalance(oldBalance)) };
            var character = new Character(1, 0, "RaidSong", 0, resources.Towns.Room, resources.Towns.InitialReturn)
                { Inventory = inventory };
            var grant = InventoryRewardGrantService.TryGrantBatch(character, prepared.Grants, items.Items, items.GoldLimits,
                items.EpicPieces, utcNow: now, allowOverflowMail: true, raidReward: true);
            if (!grant.Accepted || grant.Plan is not { } planned)
                throw new InvalidDataException("真实攻坚账号材料规划失败");
            var raidPlan = new InventoryPlan(planned.Before, planned.After, "raid_reward", new(character), planned.OverflowItems)
            {
                AccountMaterialRaidWeek = planned.AccountMaterialRaidWeek,
                AccountMaterialRaidDefinitions = planned.AccountMaterialRaidDefinitions
            };
            int expected = Math.Min(25, definition.HoldingLimit - oldBalance);
            if (definition.WeeklyAccountLimit != 0) expected = Math.Min(expected, definition.WeeklyAccountLimit);
            if (definition.WeeklyCharacterLimit != 0) expected = Math.Min(expected, definition.WeeklyCharacterLimit);
            That(planned.After.AccountWallet.Material(itemId).Balance - oldBalance == expected &&
                BattlePassUndefeatedSongSource.DirectAmount(prepared, raidPlan, now) == expected &&
                planned.After.Items.All(item => item.Core.ItemId != itemId),
                "真实资源攻坚发奖按持有/周额度入账，任务只计实际入账量");
        }
    }
}
