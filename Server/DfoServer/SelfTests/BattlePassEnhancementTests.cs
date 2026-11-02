using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassEnhancementTests
{
    public static void RunCore()
    {
        int before = Count;
        var at = new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.FromHours(8));
        var source = new InventoryItem(17, new(InventoryListType.Main, 121), new(3242, ItemKind.Material, 4));
        var target = new InventoryItem(31, new(InventoryListType.Main, 9), new(31440, ItemKind.Equipment, 0)
            { Attr = 0xE9, Marker16 = 3719 });
        var inventory = new InventorySnapshot([source, target], Gold: 1000);
        var result = new EnhancementReceipt(EnhancementMode.Reinforce, target.Position, 121, 3, 9, 10, true)
            { OutcomeCode = 0, InPlace = true, GoldCost = 100 };
        var fact = new EquipmentEnhancementFact(Guid.NewGuid(), at, TimeProvider.System,
            OrdinaryEnhancementSource.Portable, source, 1, target, result);
        var after = inventory with { Gold = 900, Items = [source with { Core = source.Core with { Value = 3 } },
            target with { Core = target.Core with { Attr = 0xEA } }] };
        var plan = new InventoryPlan(inventory, after, "core_ordinary") { EquipmentEnhancement = fact };
        That(fact.Matches(plan, after), "普通升级事实核完整UID、材料、金币和装备核心");
        That(ReferenceEquals(plan.WithAfter(after).EquipmentEnhancement, fact) && plan.WithAfter(after).EquipmentEnhancement!.Id == fact.Id,
            "同计划复制保持同一事实及GUID，不重新抽签或换幂等键");
        That(!fact.Matches(plan, after with { Gold = 901 }), "实际金币未准确扣费不能计来源");
        That(!fact.Matches(plan, after with { Items = after.Items.SetItem(0, after.Items[0] with { Uid = 18 }) }),
            "材料换UID即使数量相同也不能计来源");
        That(!fact.Matches(plan, after with { Items = after.Items.SetItem(1,
            after.Items[1] with { Core = after.Items[1].Core with { Marker16 = 0 } }) }), "目标完整扩展字段不能丢失");
        That(!(fact with { Id = Guid.Empty }).Matches(plan, after) &&
            !(fact with { Result = result with { NewLevel = 11 } }).Matches(plan, after), "空来源和券式越级结果拒绝");
        var exhausted = fact with { Consumed = 4, Result = result with { MaterialRemaining = 0 } };
        That(exhausted.Matches(plan, after with { Items = [after.Items[1]] }), "材料耗尽允许移除原UID");
        That(!exhausted.Matches(plan, after), "材料耗尽事实不得保留旧堆叠");
        var failure = fact with { Result = result with { Succeeded = false, OutcomeCode = 1, NewLevel = 9 } };
        That(failure.Matches(plan, after with { Items = after.Items.SetItem(1, target) }), "实际扣费失败保级仍是普通尝试");
        var destroyed = fact with { Result = result with { Succeeded = false, OutcomeCode = 3, NewLevel = 0 } };
        That(destroyed.Matches(plan, after with { Items = [after.Items[0]] }), "销毁要求原目标UID消失");

        var walletBefore = inventory with { Items = [target], AccountWallet = AccountWallet.Empty.Set(CurrencyKind.ClearCube, 1000) };
        var walletAfter = after with { Items = [after.Items[1]], AccountWallet = walletBefore.AccountWallet.Set(CurrencyKind.ClearCube, 990) };
        var walletResult = result with { MaterialSlot = 358, MaterialRemaining = 990 };
        var walletFact = fact with { SourceKind = OrdinaryEnhancementSource.Material, Source = null, Consumed = 10,
            Result = walletResult, WalletMaterial = CurrencyKind.ClearCube };
        var walletPlan = new InventoryPlan(walletBefore, walletAfter, "equipment_reinforcement_npc") { EquipmentEnhancement = walletFact };
        That(walletFact.Matches(walletPlan, walletAfter), "NPC普通强化以虚拟槽358核钱包无色扣料，不伪造实体UID");
        That(!walletFact.Matches(walletPlan, walletAfter with { AccountWallet = walletAfter.AccountWallet.Set(CurrencyKind.ClearCube, 991) }) &&
            !(walletFact with { Consumed = 9 }).Matches(walletPlan, walletAfter), "少扣钱包材料或伪改消耗量拒绝计次");
        That(!(walletFact with { Source = source }).Matches(walletPlan, walletAfter) &&
            !(walletFact with { WalletMaterial = null }).Matches(walletPlan, walletAfter) &&
            !(walletFact with { SourceKind = OrdinaryEnhancementSource.Portable }).Matches(walletPlan, walletAfter),
            "钱包与实物来源互斥，便携器不能冒充钱包扣料，缺来源不抛空引用");
        That(!(walletFact with { WalletMaterial = CurrencyKind.BlackCube }).Matches(walletPlan, walletAfter) &&
            !(walletFact with { Result = walletResult with { MaterialSlot = 357 } }).Matches(walletPlan, walletAfter),
            "钱包种类与原虚拟槽必须同时匹配");
        var walletDestroyed = walletFact with { Result = walletResult with { Succeeded = false, OutcomeCode = 3, NewLevel = 0,
            Refunds = [new(358, 3037, 2)] } };
        var refundAfter = walletAfter with { Items = [], AccountWallet = walletAfter.AccountWallet.Set(CurrencyKind.ClearCube, 992) };
        That(walletDestroyed.Matches(walletPlan, refundAfter) &&
            !walletDestroyed.Matches(walletPlan, refundAfter with { AccountWallet = refundAfter.AccountWallet.Set(CurrencyKind.ClearCube, 993) }),
            "销毁补偿的同种钱包材料按原回执精确返还，不把净扣料误当未扣料");

        var attempts = new[] { new BattlePassMission(17, 1, 100, 16, "", -1),
            new BattlePassMission(35, 3, 1000, 16, "", -1), new BattlePassMission(61, 30, 1600, 16, "", -1) };
        var amplifyAttempt = new BattlePassMission(62, 30, 1600, 38, "", -1);
        var reinforceReach = new BattlePassMission(63, 2, 1600, 39, "", 10);
        var amplifyReach = new BattlePassMission(64, 2, 1600, 40, "", 7);
        That(attempts.All(d => BattlePassEnhancementSource.Matches(d, fact) && BattlePassEnhancementSource.Matches(d, failure)) &&
            !BattlePassEnhancementSource.Matches(amplifyAttempt, fact), "六已核定义中强化次数包含真实成功和失败，不混增幅");
        That(BattlePassEnhancementSource.Matches(reinforceReach, fact) && !BattlePassEnhancementSource.Matches(reinforceReach, failure) &&
            !BattlePassEnhancementSource.Matches(reinforceReach, destroyed), "+10只计普通恰好成功，不计失败或销毁");
        var amplify = fact with { Result = result with { Mode = EnhancementMode.Amplify, OldLevel = 6, NewLevel = 7 } };
        That(BattlePassEnhancementSource.Matches(amplifyAttempt, amplify) && BattlePassEnhancementSource.Matches(amplifyReach, amplify) &&
            !BattlePassEnhancementSource.Matches(reinforceReach, amplify), "+7增幅和增幅尝试保持同族隔离");
        That(!BattlePassEnhancementSource.Matches(reinforceReach, fact with { Result = result with { OldLevel = 10, NewLevel = 11 } }) &&
            !BattlePassEnhancementSource.Matches(reinforceReach, fact with { Result = result with { OldLevel = 8 } }),
            "高于目标的成功和券赋级不冒充恰到+10");
        That(!BattlePassEnhancementSource.Matches(reinforceReach with { RawParameter = 11 }, fact) &&
            !BattlePassEnhancementSource.Matches(attempts[0] with { Id = 71 }, fact) &&
            !BattlePassEnhancementSource.Matches(amplifyReach with { RequiredCount = 3 }, amplify), "未知定义、目标和参数不泛化计量");
        That(!BattlePassEnhancementSource.Matches(attempts[0], fact with { Result = result with { Mode = EnhancementMode.LuckyAmplify } }),
            "幸运券模式不接普通来源");
        Console.WriteLine($"PASS BattlePassEnhancementTests core ({Count - before} checks)");
    }
}
