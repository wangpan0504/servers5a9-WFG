using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class ConsumableUseSessionTests
{
    // 本服自定义合同：028的%100按十万分母解释为相对成功权重+0.1%，不声称原版公式。
    public static void RunDisguiseUpgradeLuck(TownCatalog towns, CharacterCatalog characters, ItemResources r)
    {
        const int disguiseId = 490013028, formId = 56673, gearId = 31440, couponId = 490705402;
        That(r.RecoveryConsumables.TryGet(disguiseId, out var disguise) && disguise.Disguise is
            { Type: 2, Seconds: 300 } definition && definition.Allows(2, formId), "028强化幸运回归使用真实资源形态池和期限");
        var clock = new FakeClock();
        clock.Advance(TimeSpan.FromMilliseconds(123));
        long until = clock.GetUtcNow().ToUnixTimeMilliseconds() + 300000;
        var rng = new AmplifyUpgradeTests.FixedRandom(6000);
        var target = new InventoryItem(1, new(InventoryListType.Main, 9),
            InventoryRewardGrantService.Create(r.Items.Get(gearId)) with { Attr = 0xE0, AmplifyType = 0, AmplifyValue = 0, ExpireTime = 0 });
        var coupon = new InventoryItem(2, new(InventoryListType.Main, 67),
            InventoryRewardGrantService.Create(r.Items.Get(couponId), 2) with { ExpireTime = 0 });
        var actor = new Character(1, 0, "DisguiseLuck", 0, towns.Room, null)
        { Level = 95, Inventory = new([target, coupon]), Disguise = new(Guid.NewGuid(), 2, formId, disguiseId, until) };
        var request = new EnhancementRequest(EnhancementMode.Reinforce, target.Position, gearId, 67, -1,
            ReadOnlyMemory<byte>.Empty, -1, -1);
        InventoryEnhancementService Tickets(string script) => new(r.Items, new EnhancementCatalog(
            path => path.StartsWith("stackable/", StringComparison.Ordinal) ? script : "",
            new Dictionary<int, string> { [couponId] = "disguise-luck-ticket.stk" },
            new Dictionary<int, string> { [gearId] = "disguise-luck-target.equ" }), r.MedalUpgrades,
            rng, clock, consumables: r.RecoveryConsumables);
        var tickets = Tickets("[equipment reinforcement ticket]\n1 60 `additional` -1\n");
        int calls = rng.Calls;
        That(tickets.Plan(actor with { Disguise = null }, request, out var plain).Accepted && plain is { Succeeded: false } &&
            tickets.Plan(actor, request, out var boosted).Accepted && boosted is { Succeeded: true, NewLevel: 1 } &&
            rng.Calls == calls + 2 && actor.Inventory.Items.All(item => item.Core.ItemId != disguiseId),
            "本服028相对+0.1%使逐级券6000抽样从失败变成功，各抽一次；激活后无需继续持有COS源");
        rng.Value = 6006;
        That(tickets.Plan(actor, request, out var exact).Accepted && exact is { Succeeded: false },
            "逐级券60%乘1.001为60.06%，6006等号失败，不是翻倍/加100个百分点/加1个百分点");
        rng.Value = 6000;
        foreach (var invalid in new ConsumableDisguiseAppearance?[] { null,
            actor.Disguise! with { UntilMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds() },
            actor.Disguise! with { FormId = -1 }, actor.Disguise! with { Type = 1 }, actor.Disguise! with { SourceItemId = -1 } })
            That(tickets.Plan(actor with { Disguise = invalid }, request, out var excluded).Accepted && excluded is { Succeeded: false },
                "取消、原到期等号、池外形态、错类型和未知资源来源不产生028加成");
        var potion = actor with { Inventory = actor.Inventory with { ConsumableStates =
            [new(ConsumableStateKind.Effect, 490004594, until, 7, -490004594)] } };
        rng.Value = 6060;
        That(tickets.Plan(potion, request, out var maximum).Accepted && maximum is { Succeeded: false },
            "强化药剂1000与COS100按同模式取Max，6060等号失败，不能额外复乘COS");
        long winnerStarted = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var equalWinners = actor with { Inventory = actor.Inventory with { ConsumableStates =
            [new(ConsumableStateKind.Effect, 490004594, winnerStarted + 1000, 7, -490004594),
             new(ConsumableStateKind.Effect, 2600025, winnerStarted + 3000, 7, -2600025)] } };
        rng.Value = 6059;
        var winningPlan = tickets.Plan(equalWinners, request, out var winningReceipt);
        That(winningPlan.Accepted && winningReceipt is { Succeeded: true }, "同值真实药剂1000合并仍按单一最大加成");
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        That(winningPlan.Plan!.CharacterPrecondition?.IsExpired() == false,
            "同值最大加成的短效来源到期不误拒，另一同值长效来源仍足以支持本次概率");
        clock.Advance(TimeSpan.FromMilliseconds(2000));
        That(winningPlan.Plan!.CharacterPrecondition?.IsExpired() == true,
            "所有最大值1000来源到期等号整拒，较低值COS100长效不能延长已抽出的1000概率");
        rng.Value = 6000;
        That(Tickets("[equipment reinforcement ticket]\n11 60 `fixed` -1\n").Plan(actor, request, out var fixedLevel).Accepted &&
            fixedLevel is { Succeeded: false }, "固定等级券保留自身池，不接受028强化幸运");
        var randomTickets = Tickets("[enchant random]\n[er_grade]\n0 1 2 3 4 5 6\n[er_slot]\n0\n[er_seal]\n0\n[er_enchant]\n2 7 6000 12 4000\n[/er_enchant]\n[/enchant random]\n");
        var randomRequest = request with { Mode = EnhancementMode.RandomReinforce };
        var randomPlain = randomTickets.Plan(actor with { Disguise = null }, randomRequest, out var randomPlainReceipt);
        var randomForm = randomTickets.Plan(actor, randomRequest, out var randomFormReceipt);
        That(randomPlain.Accepted && randomForm.Accepted && randomFormReceipt == randomPlainReceipt &&
            randomForm.Plan!.After.Find(target.Position) == randomPlain.Plan!.After.Find(target.Position),
            "随机等级券的原分段池与完整装备结果不受028影响");

        var portable = new InventoryItem(3, coupon.Position,
            InventoryRewardGrantService.Create(r.Items.Get(10005312), 2) with { ExpireTime = 0 });
        var normal = actor with { Inventory = new([target with { Core = target.Core with { Attr = 0xE9 } }, portable]) };
        var normalService = new InventoryAmplifyUpgradeService(r.Items, r.AmplifyUpgrades, r.Enhancements,
            r.Disjoint, r.GoldLimits, r.EpicPieces, rng, clock, r.RecoveryConsumables);
        int failure = r.AmplifyUpgrades.ReinforceRules.Steps[9].FailureWeight;
        That(failure is > 0 and < 100000, "普通强化选择真实normal表成功失败两区间");
        rng.Value = failure - 1;
        calls = rng.Calls;
        That(normalService.Plan(normal with { Disguise = null }, request, out var normalPlain).Accepted &&
            normalPlain is { Succeeded: false } && normalService.Plan(normal, request, out var normalBoost).Accepted &&
            normalBoost is { Succeeded: true, NewLevel: 10 } && rng.Calls == calls + 2,
            "正常Reinforce表消费者同样读取028原状态，保留单次随机和原费用材料合同");
        var amplifyActor = actor with { Inventory = new([
            target with { Core = target.Core with { Attr = 0xE9, AmplifyType = 3, AmplifyValue = 6 } },
            portable with { Position = new(InventoryListType.Main, 121),
                Core = InventoryRewardGrantService.Create(r.Items.Get(3242), 1000) with { ExpireTime = 0 } }], Gold: 100_000_000) };
        rng.Value = r.AmplifyUpgrades.Rules.Steps[9].FailureWeight - 1;
        That(normalService.Plan(amplifyActor, request with { Mode = EnhancementMode.Amplify, MaterialSlot = 121 }, out var amplifyReceipt).Accepted &&
            amplifyReceipt is { Succeeded: false }, "028强化幸运不扩大到普通增幅通路");
        var luckyActor = actor with { Inventory = new([
            target with { Core = target.Core with { Attr = 0xEE } },
            portable with { Core = InventoryRewardGrantService.Create(r.Items.Get(10007406), 2) with { ExpireTime = 0 } }]) };
        rng.Value = 84999;
        That(normalService.Plan(luckyActor, request, out var luckyReceipt).Accepted &&
            luckyReceipt is { Path: 4, Succeeded: false, NewLevel: 13 }, "幸运强化券保留自身三段池，不接受028成功加成");

        foreach (string scenario in new[] { "before", "expired", "cancel", "replace", "potion" })
        {
            using var f = new Fixture(towns, characters, hp: disguise, consumables: r.RecoveryConsumables, level: 95);
            f.Clock.Advance(TimeSpan.FromMilliseconds(123));
            var seed = f.Read();
            var items = seed.Inventory.Items.Add(target with { Uid = 0 }).Add(coupon with { Uid = 0 });
            var effects = scenario == "potion" ?
                seed.Inventory.ConsumableStates.Add(new(ConsumableStateKind.Effect, 490004594,
                    f.Clock.GetUtcNow().ToUnixTimeMilliseconds() + 600000, 7, -490004594)) : seed.Inventory.ConsumableStates;
            f.Repository.CommitInventory(f.Owner, seed, new(seed.Inventory, seed.Inventory with { Items = items, ConsumableStates = effects },
                "disguise_luck_seed", new(seed)));
            f.Select();
            That(f.Session.ApplyUnlimitedDisguise(2, formId, disguiseId, f.Service) is not null,
                "会话概率回归经真实直接211激活028，不预置在线Disguise");
            var appearance = f.Session.CurrentConsumableDisguise()!.Value.Appearance;
            f.Clock.Advance(TimeSpan.FromSeconds(60));
            var sessionRandom = new AmplifyUpgradeTests.FixedRandom(scenario == "potion" ? 6059 : 6000);
            var sessionTickets = new InventoryEnhancementService(r.Items, new EnhancementCatalog(
                path => path.StartsWith("stackable/", StringComparison.Ordinal) ? "[equipment reinforcement ticket]\n1 60 `additional` -1\n" : "",
                new Dictionary<int, string> { [couponId] = "disguise-luck-ticket.stk" },
                new Dictionary<int, string> { [gearId] = "disguise-luck-target.equ" }), r.MedalUpgrades,
                sessionRandom, f.Clock, consumables: r.RecoveryConsumables);
            var before = JsonSerializer.Serialize(f.Read());
            var result = f.Session.PlanAndCommitInventory(character =>
            {
                var planned = sessionTickets.Plan(character, request, out _);
                That(planned.Accepted, "会话概率计划在当前形态和期限内合法");
                if (scenario == "cancel") f.Session.CancelConsumableDisguise();
                else if (scenario == "replace")
                {
                    f.Session.CancelConsumableDisguise();
                    That(f.Session.ApplyUnlimitedDisguise(2, 58026, disguiseId, f.Service) is not null,
                        "规划后重新激活池内另一形态，制造真实快照替换");
                }
                else f.Clock.Advance(TimeSpan.FromMilliseconds(scenario == "before" ? 239999 : 240000));
                return planned;
            });
            bool accepted = scenario is "before" or "potion";
            That(result.Accepted == accepted && sessionRandom.Calls == 1 &&
                (accepted ? result.Receipt is not null && f.Read().Inventory.Find(coupon.Position)!.Core.Count == 1 :
                    result.Receipt is null && JsonSerializer.Serialize(f.Read()) == before),
                "规划后原ms截止前1ms可提交；等号到期/取消/换形态整拒无扣物无重抽；强药剂胜出不加COS到期门");
            if (scenario == "before")
                That(ReferenceEquals(f.Session.Selected!.Disguise, appearance) && appearance.UntilMilliseconds ==
                    f.Clock.GetUtcNow().ToUnixTimeMilliseconds() + 1, "成功提交仍保原形态对象和原截止，不续期");
        }
    }
}
