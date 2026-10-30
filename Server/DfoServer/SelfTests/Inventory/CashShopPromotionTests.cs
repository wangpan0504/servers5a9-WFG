using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.CashShopPromotions;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class CashShopPromotionTests
{
    public static void RunCore()
    {
        int checks = Count;
        var rules = PilotRules();
        foreach (var (before, units, expected) in new (long, int, PromotionAward[])[]
        {
            (0, 1, []),
            (1, 1, [new(2, 490705441, 1)]),
            (9, 1, [new(10, 490705449, 1)]),
            (10, 2, [new(12, 490705441, 1)]),
            (20, 2, [new(22, 490705441, 1)]),
            (29, 2, [new(30, 490705449, 1), new(31, 490705415, 1)])
        })
            That(CashShopPromotionRules.Settle(rules, before, units).SequenceEqual(expected),
                $"累计完整结算({before},{before + units}]的真实外盒");
        var all = CashShopPromotionRules.Settle(rules, 0, 64);
        That(all.Length == 61 && all.Count(award => award.ItemId == 490705415) == 34 &&
            all.Count(award => award.ItemId != 490705415) == 27 &&
            all.All(award => award.Count == 1 && award.PurchaseNumber is not (1 or 11 or 21)),
            "64套恰27阶段及34溢出，无首档和遗漏、截断");
        That(CashShopPromotionRules.Settle(rules, 4999, 1).SequenceEqual([new PromotionAward(5000, 490705415, 1)]),
            "累计上限最后一套仍完整发奖");
        var maximumBatch = CashShopPromotionRules.Settle(rules, 30, 64);
        That(maximumBatch.Length == 64 && maximumBatch.All(award => award.Count == 1) &&
            (maximumBatch.Length + 9) / 10 == 7, "首期最坏单车64完整外盒，按10附件分7封邮件有界");
        foreach (var (before, units) in new (long, int)[] { (-1, 1), (0, 0), (0, -1), (0, 65), (5000, 1), (4999, 2), (long.MaxValue, 1) })
            Reject<ArgumentOutOfRangeException>(() => CashShopPromotionRules.Settle(rules, before, units), "非法购买范围整段拒绝");
        foreach (var invalid in new[]
        {
            rules with { CycleLength = 0 }, rules with { Cycles = 0 },
            rules with { CycleLength = int.MaxValue, Cycles = int.MaxValue },
            rules with { OverflowFrom = 30 }, rules with { OverflowFrom = 32 },
            rules with { OverflowItemId = 0 }, rules with { OverflowCount = 2 },
            rules with { MaximumPurchases = 5001 }, rules with { MaximumPurchases = 0 },
            rules with { Products = [] }, rules with { Products = default },
            rules with { Products = [new(1, 1, 0)] }, rules with { Products = [new(1, 1, 2)] },
            rules with { Products = [new(0, 1, 1)] }, rules with { Products = [new(1, -1, 1)] },
            rules with { Products = [new(1, 1, 1), new(1, 2, 1)] },
            rules with { Tiers = [new(2, 1, 1), new(2, 2, 1)] },
            rules with { Tiers = [new(0, 1, 1)] }, rules with { Tiers = [new(11, 1, 1)] },
            rules with { Tiers = [new(2, 0, 1)] }, rules with { Tiers = [new(2, 1, 0)] },
            rules with { Tiers = [new(2, 1, 2)] }, rules with { Tiers = default },
            rules with { Scope = rules.Scope with { Campaign = "UPPER" } },
            rules with { Scope = rules.Scope with { Period = "2020/01" } },
            rules with { Scope = rules.Scope with { RulesVersion = "规则" } },
            rules with { Scope = rules.Scope with { Realm = "" } },
            rules with { Scope = rules.Scope with { Group = new string('a', 33) } }
        })
            Reject<InvalidDataException>(() => CashShopPromotionRules.Settle(invalid, 0, 1), "非法配置不得结算或静默少送");
        var reversed = rules with { Tiers = rules.Tiers.Reverse().ToImmutableArray() };
        That(CashShopPromotionRules.Settle(reversed, 29, 2).SequenceEqual(
            [new PromotionAward(30, 490705449, 1), new PromotionAward(31, 490705415, 1)]), "配置顺序不改变发奖顺序");
        Catalog(rules);
        RetiredPermanentConfiguration();
        Console.WriteLine($"Cash shop promotions: {Count - checks} checks passed.");
    }

    static void Catalog(PromotionRules rules)
    {
        var shop = CashShopCatalog.Parse("[item]\n105681 490705385 1 0 0 100 0 0 0\n" +
            "108726 490705385 1 0 0 100 0 0 0\n108727 490705385 1 0 0 100 0 0 0\n105684 490705483 1 0 0 100 0 0 0\n");
        var disabled = new PromotionConfiguration(rules);
        string Json(params PromotionConfiguration[] activities) => JsonSerializer.Serialize(activities);
        CashShopPromotionCatalog Read(params PromotionConfiguration[] activities) =>
            CashShopPromotionCatalog.Parse(Json(activities), shop, Template, _ => null);
        var off = Read(disabled);
        That(off.Activities.Length == 1 && off.Product(105681)?.Availability == PromotionAvailability.Disabled &&
            off.Product(105681)?.Configuration.DisabledReason.Length > 0, "未启用活动保留映射和禁用原因");
        That(off.Product(105684) is null && off.Product(123456) is null, "同名非参加商品和真正非活动商品均无活动");
        var on = Read(disabled with { Enabled = true });
        That(on.Product(105681)?.Availability == PromotionAvailability.Active &&
            on.IsCurrent(on.Revision, rules.Scope), "可信资源门槛通过的启用活动可捕获");
        var historicalRules = rules with { Scope = rules.Scope with { Period = "archive-2020" } };
        var historical = Read(disabled with { Enabled = true, Rules = historicalRules });
        That(historical.IsCurrent(historical.Revision, historicalRules.Scope) &&
            CashShopPromotionRules.Settle(historical.Product(105681)!.Configuration.Rules, 30, 1)
                .SequenceEqual([new PromotionAward(31, 490705415, 1)]),
            "历史批次身份在销售日期之外仍可按完整规则购买，不从时钟更新身份");
        var stopped = Read(disabled);
        var restarted = Read(disabled with { Enabled = true });
        That(!stopped.IsCurrent(on.Revision, rules.Scope) && !restarted.IsCurrent(on.Revision, rules.Scope) &&
            restarted.Revision > stopped.Revision && stopped.Revision > on.Revision,
            "停用及同身份重新启用均拒绝旧规划，运行revision防ABA");
        var progress = new PromotionProgress(rules.Scope, 30, 4);
        That(CashShopPromotionRules.Settle(restarted.Product(105681)!.Configuration.Rules, progress.Count, 1)
            .SequenceEqual([new PromotionAward(31, 490705415, 1)]) && progress.Count == 30,
            "同身份重启沿用调用方原进度，不从头重复发旧档");
        var changed = Read(disabled with { Enabled = true, Rules = rules with { OverflowItemId = 490705416 } });
        That(!changed.IsCurrent(on.Revision, rules.Scope), "规则改变拒绝旧规划");
        var invalid = Read(disabled with { Enabled = true, Rules = rules with { OverflowFrom = 30 } });
        That(invalid.Product(105681)?.Availability == PromotionAvailability.Invalid &&
            !string.IsNullOrEmpty(invalid.Product(105681)?.Error) && invalid.Product(105684) is null &&
            !invalid.IsCurrent(invalid.Revision, rules.Scope), "启用后规则损坏仅阻止所映射商品，不回落普通购买");
        var missingTemplate = CashShopPromotionCatalog.Parse(Json(disabled with { Enabled = true }), shop,
            id => id == 490705449 ? throw new KeyNotFoundException("缺奖励模板") : Template(id), _ => null);
        That(missingTemplate.Product(108726)?.Availability == PromotionAvailability.Invalid &&
            missingTemplate.Product(105684) is null, "奖励资源损坏保留全部活动商品拒绝范围");
        var notVerified = CashShopPromotionCatalog.Parse(Json(disabled with { Enabled = true }), shop, Template);
        var unknownConsumer = CashShopPromotionCatalog.Parse(Json(disabled with { Enabled = true }), shop, Template,
            _ => "消费者尚未核实");
        That(notVerified.Product(105681)?.Availability == PromotionAvailability.Invalid &&
            unknownConsumer.Product(105681)?.Availability == PromotionAvailability.Invalid,
            "运营Enabled本身不能充当消费者验证证据");
        var wrongShop = CashShopCatalog.Parse("[item]\n105681 490705483 1 0 0 100 0 0 0\n");
        var mismatch = CashShopPromotionCatalog.Parse(Json(disabled with { Enabled = true }), wrongShop, Template, _ => null);
        That(mismatch.Product(105681)?.Availability == PromotionAvailability.Invalid, "实际商品到源物品映射不符拒绝");
        var zeroStack = CashShopPromotionCatalog.Parse(Json(disabled with { Enabled = true }), shop,
            id => Template(id) with { StackLimit = 0 }, _ => null);
        That(zeroStack.Product(105681)?.Availability == PromotionAvailability.Invalid,
            "奖励实际堆叠能力无效时禁止规划，不靠截断附件");
        string snapshot = CashShopPromotionCatalog.RuleSnapshot(rules);
        That(snapshot.Length > 0 && snapshot == CashShopPromotionCatalog.RuleSnapshot(rules with
            { Products = rules.Products.Reverse().ToImmutableArray(), Tiers = rules.Tiers.Reverse().ToImmutableArray() }) &&
            !snapshot.Contains("Enabled", StringComparison.Ordinal) && !snapshot.Contains("DisabledReason", StringComparison.Ordinal),
            "持久规则快照按商品/档位规范排序且不含运行开关");
        That(snapshot != CashShopPromotionCatalog.RuleSnapshot(rules with { OverflowItemId = 490705416 }),
            "同身份改变发奖规则会改变规范快照");
        foreach (string invalidJson in new[]
        {
            "null", "{}", "[null]", "[{}]", "[] trailing", Json(disabled).Replace("\"Rules\":", "\"Unknown\":1,\"Rules\":"),
            Json(disabled).Replace("\"Enabled\":false", "\"Enabled\":false,\"Enabled\":true"),
            Json(disabled).Replace("\"Enabled\":false", "\"Enabled\":\"false\""),
            Json(disabled).Replace("\"Enabled\":false", "\"StartsAt\":\"2020-01-01\",\"Enabled\":false")
        })
            Reject<InvalidDataException>(() => CashShopPromotionCatalog.Parse(invalidJson, shop, Template), "严格配置拒绝未知、重复、残缺和日期窗字段");
        Reject<InvalidDataException>(() => Read(disabled, disabled), "重复身份和跨活动重复商品不得任取一个");
        Reject<InvalidDataException>(() => Read(disabled, disabled with
            { Rules = rules with { Scope = rules.Scope with { Campaign = "another" } } }), "不同活动不得争用同一商品");
        That(Read(Enumerable.Range(0, 16).Select(index => disabled with
            { Rules = rules with { Scope = rules.Scope with { Campaign = "c" + index }, Products = [new(1000 + index, 490705385, 1)] } }).ToArray())
            .Activities.Length == 16, "16个独立关闭候选允许装载，不将数量上限误作无限奖池许可");
        Reject<InvalidDataException>(() => Read(Enumerable.Range(0, 17).Select(index => disabled with
            { Rules = rules with { Scope = rules.Scope with { Campaign = "c" + index }, Products = [new(1000 + index, 490705385, 1)] } }).ToArray()),
            "活动数超过16拒绝整个配置");
        var omittedEnabled = CashShopPromotionCatalog.Parse(Json(disabled).Replace("\"Enabled\":false,", ""), shop, Template);
        That(omittedEnabled.Product(105681)?.Availability == PromotionAvailability.Disabled, "未声明Enabled默认关闭");
        That(typeof(DfoServer.Infrastructure.ServerRuntime).Assembly
                .GetManifestResourceStream("DfoServer.Resources.cash-shop-promotions.json") is not null,
            "促销配置随程序集打包，单文件发行包不依赖外置文件");
        var shipped = CashShopPromotionCatalog.Parse(
            DfoServer.Infrastructure.ServerRuntime.ReadShippedPromotionConfig(), shop, Template);
        var shippedEntry = shipped.Activities[0];
        That(shipped.Activities.Length == 1 && shippedEntry.Error is null &&
            shippedEntry.Policy is { IsPermanent: false } shippedPolicy && shippedPolicy.CounterScope == rules.Scope &&
            shippedEntry.Configuration is { Enabled: false, RuleKind: "pilot", CarryForward: null } &&
            new[] { 105681, 108726, 108727 }.All(id => ReferenceEquals(shipped.Product(id), shippedEntry)) &&
            shippedEntry.Availability == PromotionAvailability.Disabled && shipped.Product(105684) is null &&
            CashShopPromotionCatalog.RuleSnapshot(shippedEntry.Configuration.Rules, shippedPolicy) == snapshot &&
            shippedEntry.Configuration.Rules.MaximumPurchases == 5000,
            "发布规则保持原v1与5000套上限、三商品及九档完整快照，仍保持关闭");
        string path = Path.Combine(Path.GetTempPath(), "dfo-promotion-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, Json(disabled));
            That(CashShopPromotionCatalog.Load(path, shop, Template).Product(105681)?.Availability == PromotionAvailability.Disabled,
                "目录读取独立类型化配置文件，关闭候选不加载未证消费者");
        }
        finally { File.Delete(path); }
    }

    static void RetiredPermanentConfiguration()
    {
        var shop = CashShopCatalog.Parse("[item]\n105681 490705385 1 0 0 100 0 0 0\n" +
            "108726 490705385 1 0 0 100 0 0 0\n108727 490705385 1 0 0 100 0 0 0\n105684 490705483 1 0 0 100 0 0 0\n");
        const string json = """
[
  {
    "Enabled": true,
    "DisabledReason": "仅内存语义测试夹具；发布资源仍Enabled=false",
    "Rules": {
      "Scope": {
        "Campaign": "spring-2020",
        "Period": "replica-permanent-1",
        "RulesVersion": "v2",
        "Realm": "default",
        "Group": "package"
      },
      "CycleLength": 10,
      "Cycles": 3,
      "OverflowFrom": 31,
      "OverflowItemId": 490705415,
      "OverflowCount": 1,
      "MaximumPurchases": 5001,
      "Products": [
        {
          "CommodityId": 105681,
          "ItemId": 490705385,
          "Units": 1
        },
        {
          "CommodityId": 108726,
          "ItemId": 490705385,
          "Units": 1
        },
        {
          "CommodityId": 108727,
          "ItemId": 490705385,
          "Units": 1
        }
      ],
      "Tiers": [
        {
          "Sequence": 2,
          "ItemId": 490705441,
          "Count": 1
        },
        {
          "Sequence": 3,
          "ItemId": 490705442,
          "Count": 1
        },
        {
          "Sequence": 4,
          "ItemId": 490705443,
          "Count": 1
        },
        {
          "Sequence": 5,
          "ItemId": 490705444,
          "Count": 1
        },
        {
          "Sequence": 6,
          "ItemId": 490705445,
          "Count": 1
        },
        {
          "Sequence": 7,
          "ItemId": 490705446,
          "Count": 1
        },
        {
          "Sequence": 8,
          "ItemId": 490705447,
          "Count": 1
        },
        {
          "Sequence": 9,
          "ItemId": 490705448,
          "Count": 1
        },
        {
          "Sequence": 10,
          "ItemId": 490705449,
          "Count": 1
        }
      ]
    },
    "RuleKind": "approved-permanent",
    "CarryForward": {
      "FromScope": {
        "Campaign": "spring-2020",
        "Period": "replica-1",
        "RulesVersion": "v1",
        "Realm": "default",
        "Group": "package"
      },
      "FromRulesSnapshotSha256": "B2C325D85D49C20050DB49718F6DFF676BAD03C244B5ADEED237DCBD9C499E28"
    }
  }
]
""";
        var configured = CashShopPromotionCatalog.Parse(json, shop, Template, _ => null);
        foreach (int commodity in new[] { 105681, 108726, 108727 })
            That(configured.Product(commodity)?.Availability == PromotionAvailability.Invalid &&
                configured.Product(commodity)!.Policy is null,
                "旧常驻配置即使原前驱完整也不能获得5000以上的新购授权");
        Reject<InvalidDataException>(() => CashShopPromotionService.Select(configured, new(0, [new(105681, 0)])),
            "已撤回常驻配置不能回落普通购买或绕过累计上限");
        That(configured.Product(105684) is null, "未证同名商品仍无活动资格");
    }

    static ItemTemplate Template(int id) => new(id, ItemKind.Consumable, 0, 1, 0, 1, 0, 0, 0, "[free]", ushort.MaxValue, 0, "促销外盒夹具");

    internal static PromotionRules PilotRules() => new(new("spring-2020", "replica-1", "v1", "default", "package"),
        10, 3, 31, 490705415, 1, 5000,
        [new(105681, 490705385, 1), new(108726, 490705385, 1), new(108727, 490705385, 1)],
        [new(2, 490705441, 1), new(3, 490705442, 1), new(4, 490705443, 1),
         new(5, 490705444, 1), new(6, 490705445, 1), new(7, 490705446, 1),
         new(8, 490705447, 1), new(9, 490705448, 1), new(10, 490705449, 1)]);
}
