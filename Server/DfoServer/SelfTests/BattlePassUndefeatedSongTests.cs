using System.Collections.Immutable;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassUndefeatedSongTests
{
    public static void RunCore()
    {
        int before = Count;
        var definition = new BattlePassMission(48, 300, 1000, 43, "获得不渝之咏", -1);
        That(BattlePassUndefeatedSongSource.Matches(1, definition) && !BattlePassUndefeatedSongSource.Matches(0, definition), "只匹配原周48的raw43/300/1000定义");
        That(BattlePassUndefeatedSongSource.NextCount(definition, 0, 25) == 25 &&
            BattlePassUndefeatedSongSource.NextCount(definition, 25, 75) == 100 &&
            BattlePassUndefeatedSongSource.NextCount(definition, 299, int.MaxValue) == 300,
            "按P1/P2真实材料金额累计且封顶，不把两次领取当两点或溢发");
        foreach (var other in new[] { definition with { Id = 47 }, definition with { RawTypeCode = 42 },
            definition with { RawParameter = 10308807 }, definition with { RequiredCount = 301 }, definition with { Experience = 1001 } })
        {
            That(!BattlePassUndefeatedSongSource.Matches(1, other), "变异任务定义不借原材料来源");
            Reject<InvalidDataException>(() => BattlePassUndefeatedSongSource.NextCount(other, 0, 25), "变异定义不能累计");
        }
        foreach (int amount in new[] { 0, -1 })
            Reject<InvalidDataException>(() => BattlePassUndefeatedSongSource.NextCount(definition, 0, amount), "非正取得量不累计");
        foreach (long current in new[] { -1L, 301L })
            Reject<InvalidDataException>(() => BattlePassUndefeatedSongSource.NextCount(definition, current, 25), "损坏旧进度不被静默修正");
        var prepared = new PreparedRaidReward(Guid.Parse("00000001-0002-0003-0004-000000000005"), 0, 1, 0, 1,
            LukeResourceRewards.Source, [new(10308807, 25)], false, 1) { RaidType = RaidKind.Luke };
        var existing = Song(8, 121, 10); var gained = existing with { Core = existing.Core with { Value = 35 } };
        var plan = new InventoryPlan(new([existing]), new([gained]), "raid_reward");
        That(BattlePassUndefeatedSongSource.DirectAmount(prepared, plan) == 25, "合堆只按本次真实增加25，不按旧余额35");
        var raidAt = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var accountBefore = InventorySnapshot.Empty with
            { AccountWallet = InventorySnapshot.Empty.AccountWallet.SetMaterial(10308807, new AccountMaterialBalance(10)) };
        foreach (var (limit, expected) in new[] { (100, 25), (15, 15), (0, 0) })
        {
            var definitionLimit = new AccountMaterialDefinition(limit == 0 ? 10 : 100, limit == 15 ? 15 : 0, 0);
            var granted = AccountMaterialService.Grant(accountBefore, 1, 10308807, 25, definitionLimit, raidAt, raidReward: true);
            var accountPlan = new InventoryPlan(accountBefore, granted.Plan!.After, "raid_reward")
            {
                AccountMaterialRaidWeek = definitionLimit.WeeklyAccountLimit == 0 ? null : AccountMaterialService.RaidWeek(raidAt),
                AccountMaterialRaidDefinitions = ImmutableDictionary<int, AccountMaterialDefinition>.Empty.Add(10308807, definitionLimit)
            };
            That(BattlePassUndefeatedSongSource.DirectAmount(prepared, accountPlan, raidAt) == expected,
                "账号材料按原持有/周额度合法裁剪，任务只记本次实际取得");
            That(accountPlan.WithAfter(accountPlan.After).AccountMaterialRaidDefinitions == accountPlan.AccountMaterialRaidDefinitions,
                "库存计划复制保留攻坚材料额度事实");
            if (expected == 25)
            {
                var forged = accountPlan.After with { AccountWallet = accountPlan.After.AccountWallet.SetMaterial(10308807,
                    accountPlan.After.AccountWallet.Material(10308807) with { Balance = 34 }) };
                Reject<InvalidDataException>(() => BattlePassUndefeatedSongSource.DirectAmount(prepared, accountPlan.WithAfter(forged), raidAt),
                    "伪造少发不能冒合法额度裁剪");
            }
        }
        That(BattlePassUndefeatedSongSource.DirectAmount(prepared,
            new(plan.Before, new([existing with { Core = existing.Core with { Value = 20 } }]), "raid_reward", overflowItems: [Song(0, 0, 15)])) == 10,
            "部分投邮只计实际入包10，不能把邮件15提前计入");
        That(BattlePassUndefeatedSongSource.DirectAmount(prepared,
            new(plan.Before, plan.Before, "raid_reward", overflowItems: [Song(0, 0, 25)])) == 0,
            "全部投邮本笔尚无物理取得，不注册正来源");
        That(BattlePassUndefeatedSongSource.DirectAmount(prepared with { Grants = [new(10308804, 1)] },
            new(InventorySnapshot.Empty, InventorySnapshot.Empty, "raid_reward")) == 0,
            "领取原箱或其它物品不是取得10308807材料");
        foreach (var invalid in new[] { new InventoryPlan(plan.Before, plan.Before, "raid_reward"),
            new InventoryPlan(plan.Before, plan.After, "reward_grant"),
            new InventoryPlan(plan.Before, plan.After, "raid_reward", overflowItems: [Song(0, 0, 25)]) })
            Reject<InvalidDataException>(() => BattlePassUndefeatedSongSource.DirectAmount(prepared, invalid), "授权量与物理增量或原事务不符拒绝");
        string key = BattlePassUndefeatedSongSource.SourceId(prepared);
        That(key.Length == 32 && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            key == BattlePassUndefeatedSongSource.SourceId(prepared), "完整原身份确定性32hex满足真实来源表，不截断业务键");
        foreach (var other in new[] { prepared with { Instance = Guid.NewGuid() }, prepared with { Character = 2 },
            prepared with { Phase = 1 }, prepared with { Kind = 0 }, prepared with { Card = 1 },
            prepared with { Source = prepared.Source + ";different" }, prepared with { RaidType = RaidKind.Prey } })
            That(key != BattlePassUndefeatedSongSource.SourceId(other), "团本及实例角色阶段牌种卡片来源各维身份不串用");
        Reject<InvalidDataException>(() => BattlePassUndefeatedSongSource.SourceId(prepared with { Instance = Guid.Empty }), "空实例不能注册来源");
        Console.WriteLine($"PASS {Count - before} BattlePassUndefeatedSongTests core");
    }
    static InventoryItem Song(long uid, short slot, int count) => new(uid, new(InventoryListType.Main, slot), new(10308807, ItemKind.Material, count));
}
