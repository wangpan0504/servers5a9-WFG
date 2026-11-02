using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassRewardResourceTests
{
    // 使用调用方共享的正式目录；不预填、覆盖模板，也不读取奖励后代用途。
    public static void RunResources(BattlePassCatalog catalog, ItemCatalog items,
        GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, DateTimeOffset now)
    {
        int before = Count;
        var service = new BattlePassRewardService(catalog, items, goldLimits, pieces, new FixedClock(now));
        Verify(4, 0, [(15, 1)]);
        Verify(4, 1, [(490705122, 1), (490705101, 3)]);
        Verify(80, 0, [(490705130, 1), (490705163, 1), (490705127, 1)]);
        Verify(80, 1, [(490705131, 1), (490705132, 1), (490705111, 1), (490705134, 1)]);
        Console.WriteLine($"PASS BattlePassRewardResourceTests ({Count - before} checks)");

        void Verify(int level, byte track, (int Id, int Count)[] expectedRows)
        {
            That(catalog.RewardsByLevel[level].Rewards.Where(row => row.RawTrackCode == track)
                .Select(row => (row.ItemId, row.Count)).SequenceEqual(expectedRows),
                $"正常资源等级{level}/轨{track}逐项原序符合已保存同源定义");
            var role = new Character(1, 0, "PassResource", 3, new(1, 0, 10, 10), null) { Level = 95 };
            var state = new BattlePassSeasonState("normal-resource-test-season", checked((long)catalog.TotalExperienceToReach(level)),
                track, ImmutableHashSet<BattlePassRewardKey>.Empty, 9);
            string input = JsonSerializer.Serialize(new { role, state });
            var expectedItems = new List<InventoryItem>();
            foreach (var row in expectedRows)
            {
                var template = items.Get(row.Id);
                // root实际10模板收据已确认全部为永久、无获取效果的普通消耗品。
                That(template.Id == row.Id && template.Kind == ItemKind.Consumable && row.Count <= template.StackLimit &&
                    !template.HasAcquisitionEffect && !template.IsPremiumService &&
                    template.Expiration is { AbsoluteTime: 0, Days: 0, DailyDelete: 0, WeeklyDeleteDay: null },
                    $"正常奖励模板{row.Id}沿真实分类与永久期限，不伪造模板或续期");
                var core = new ItemCore(row.Id, ItemKind.Consumable, row.Count)
                {
                    Durability = template.Durability,
                    SealFlag = template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0
                };
                expectedItems.Add(new(0, new(InventoryListType.Main, checked((short)(65 + expectedItems.Count))), core));
            }
            var result = service.Plan(role, state, 0, track, checked((uint)level));
            var plan = result.Plan ?? throw new InvalidOperationException($"Normal resource claim has no plan: level={level},track={track},error={result.Error},reason={result.RejectReason}");
            That(result.Accepted && result.Error == 0 && plan.Inventory.Before == role.Inventory && plan.Before == state &&
                plan.Inventory.Reason == "battle_pass_reward" && plan.Inventory.OverflowItems.IsEmpty,
                $"等级{level}/轨{track}真实完整发奖计划成功，不走邮件或临时替代奖");
            That(plan.Inventory.After.Items.SequenceEqual(expectedItems) &&
                JsonSerializer.Serialize(plan.Inventory.After with { Items = role.Inventory.Items }) == JsonSerializer.Serialize(role.Inventory),
                $"等级{level}/轨{track}原序全奖、规划UID为0、完整Core与详情正确，其他资产不变");
            plan.Inventory.After.Validate(allocating: true);
            byte[] expectedClaims = new byte[20];
            if (level == 4 && track == 0) expectedClaims[0] = 0x08;
            else if (level == 4) expectedClaims[10] = 0x08;
            else if (track == 0) expectedClaims[9] = 0x80;
            else expectedClaims[19] = 0x80;
            That(plan.After.Claims.SetEquals([new(track, level)]) && plan.After.ToClaimBytes().SequenceEqual(expectedClaims) &&
                (plan.After with { Claims = state.Claims }) == state,
                $"等级{level}/轨{track}只规划唯一领取位，经验资格赛季版本不提前提交");
            That(JsonSerializer.Serialize(new { role, state }) == input,
                $"等级{level}/轨{track}原角色与账号季输入完整不变");
        }
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
