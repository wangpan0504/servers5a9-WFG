using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class FatigueConsumableTests
{
    internal const int Sample = 7518;
    internal static readonly InventorySlot Slot = new(InventoryListType.Main, 68), Second = new(InventoryListType.Main, 69);

    public static void RunCore()
    {
        int checks = Count;
        var template = new ItemTemplate(987654, ItemKind.Consumable, 0, 1, 0, 1000, 0, 2, 1, "[trade]", ushort.MaxValue, 0, "fixture");
        var raw = new StackableFile(template.Id, "stackable/custom/fatigue.stk", new("[etc]", [0]), 1000, null, null, 1, null, [])
        {
            ActionDeclaration = new("[add fatigue]", [5]), HasUseActionPacket = true, DeclaredUseActionPacket = 1,
            HasActionUsablePlace = true, DeclaredActionUsablePlaces = ["[village]", "[seria room]", "[dungeon]"]
        };
        var file = new RecoveryConsumableFile(template.Id, raw.ScriptPath, raw, 86400000, 7, null, null,
            ["cool time", "cooltime group", "cooltime maintenance"], []) { CooltimeMaintenance = 7 };
        That(FatigueConsumableDefinition.Parse(file, template) is { Amount: 5, CoolTime: 86400000, CooltimeMaintenance: 7 },
            "任意LST编号、目录和名称只按add fatigue标签自动识别，不需要旧编号恢复量表");
        foreach (var bad in new[] { file with { Stackable = raw with { ActionDeclaration = null } },
            file with { Stackable = raw with { ActionDeclaration = new("[add fatigue]", [0]) } },
            file with { Stackable = raw with { ActionDeclaration = new("[add fatigue]", [5, 30]) } },
            file with { Stackable = raw with { DeclaredUseActionPacket = 0 } },
            file with { StructureDiagnostics = ["bad"] }, file with { DeclaredFields = ["total usable count"] },
            file with { DeclaredFields = ["total usable count"], TotalUsableCount = 256 } })
            That(FatigueConsumableDefinition.Parse(bad, template) is null, "旧入口、复合动作和损坏的限次不伪装成成功");
        That(FatigueConsumableDefinition.Parse(file with { TotalUsableCount = 2, DeclaredFields = ["total usable count"],
            Diagnostics = ["重复恢复物品字段：attach type"] }, template) is { DailyLimit: 2 },
            "标签日次数与使用无关的重复交易元数据不阻断正确动作");
        var restricted = FatigueConsumableDefinition.Parse(file with { Stackable = raw with
            { DeclaredActionUsablePlaces = ["[dungeon]", "[dont use assult]"] } }, template)!;
        That(restricted.AllowsPlace("[dungeon]", false) && !restricted.AllowsPlace("[dungeon]", true) &&
            !restricted.AllowsPlace("[village]", false), "原生突击排除位只排除实际突击，不覆盖原使用地点");
        var before = new CharacterFatigueState(50, 156, 23, 106, 11, 9);
        That(FatigueConsumableService.Recover(before, 5) == before with { Used = 45, Remaining = 111 },
            "恢复量按标签5而非旧固定30，保留疲劳附属字段");
        foreach (int amount in new[] { 0, -1, 51 })
            Reject<InvalidDataException>(() => FatigueConsumableService.Recover(before, amount), "非法或无法完整恢复的量不截断");
        Console.WriteLine($"PASS {Count - checks} 疲劳标签识别、原生入口及完整恢复规则断言");
    }

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = SelfTestCatalogs.Growth(pvf, skills);
        var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
        var clock = new ConsumableUseSessionTests.FakeClock();
        var service = new FatigueConsumableService(items.Items, towns, clock);
        foreach (var (id, amount, daily) in new[] { (7456, 40, 1), (7518, 10, 0), (7644, 6, 0), (7645, 6, 0), (2682613, 1, 3),
            (8079, 30, 0), (8221, 20, 0), (2600026, 30, 1), (2600413, 30, 0),
            (490001834, 1, 0), (490004967, 50, 1), (490004968, 156, 1), (490009065, 30, 1) })
            That(items.Items.FatigueConsumable(id) is { } definition && definition.Amount == amount && definition.DailyLimit == daily,
                "不同目录、数量与日限次的疲劳药自动读取同一标签 " + id);
        That(items.Items.FatigueConsumable(Sample) is { Amount: 10, CoolTime: 86400000, CooltimeMaintenance: 7 } &&
            items.Items.FatigueConsumable(7519) is null && items.RecoveryConsumables.TryGet(7519, out _),
            "7518读取当前PVF的10点与24小时，7519保持普通恢复药，不变成旧升级券");
        using (var f = Make())
        {
            var before = f.Read();
            var used = f.Session.UseFatigueConsumable(Slot, service);
            var after = f.Read();
            That(used.Outcome == ConsumableUseOutcome.Committed && after.Fatigue == before.Fatigue! with { Used = 40, Remaining = 116 } &&
                after.Inventory.Find(Slot) is null && after.Inventory.Find(Second) is not null &&
                after.Inventory.ConsumableStates.Single(state => state.ItemId == Sample) is { Kind: ConsumableStateKind.Cooldown, Maintenance: 7 },
                "疲劳恢复、单件扣除和持久冷却同事务提交");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Session.UseFatigueConsumable(Second, service).Outcome == ConsumableUseOutcome.Rejected &&
                f.Read().Inventory.Find(Second) is not null, "重选不能绕过物品冷却");
            clock.Advance(TimeSpan.FromDays(1));
            That(f.Session.UseFatigueConsumable(Second, service).Outcome == ConsumableUseOutcome.Committed &&
                f.Read().Fatigue == before.Fatigue! with { Used = 30, Remaining = 126 }, "冷却到期后同一标签再次恢复完整10点");
        }
        using (var f = Make())
        {
            var before = f.Read(); var plan = service.Prepare(before, Slot);
            f.Sql("CREATE TRIGGER fatigue_potion_abort AFTER UPDATE ON character_fatigue BEGIN SELECT RAISE(ABORT,'fatigue rollback'); END");
            Reject<SqliteException>(() => f.Repository.CommitFatigueConsumable(f.Owner, plan, clock.GetUtcNow()), "疲劳更新末段SQL失败");
            That(QuestCompletionRepositoryTests.Same(before, f.Read()), "疲劳、道具、冷却、审计和revision完整回滚");
            f.Sql("DROP TRIGGER fatigue_potion_abort");
            f.Repository.CommitFatigueConsumable(f.Owner, plan, clock.GetUtcNow());
            Reject<InvalidOperationException>(() => f.Repository.CommitFatigueConsumable(f.Owner, plan, clock.GetUtcNow()),
                "旧revision计划不得再次扣药或增加疲劳");
        }
        using (var f = Make(lost: true))
        {
            f.Lost!.LoseFatigueConsumableResult = true;
            Reject<IOException>(() => f.Session.UseFatigueConsumable(Slot, service), "疲劳恢复提交后结果丢失");
            That(f.Read().Fatigue!.Used == 40 && f.Session.UseFatigueConsumable(Second, service).Outcome == ConsumableUseOutcome.CloseConnection,
                "未知提交结果不重新扣物或再次恢复疲劳");
        }
        using (var f = Make(used: 5))
            That(f.Session.UseFatigueConsumable(Slot, service).Outcome == ConsumableUseOutcome.Rejected && f.Read().Inventory.Find(Slot) is not null,
                "不足完整10点不吞道具，不采用旧126门槛");
        foreach (var (itemId, amount, level) in new[] { (7456, 40, (byte)1), (490004967, 50, (byte)50) })
        using (var f = Make(used: 120, itemId: itemId, level: level))
        {
            That(f.Session.UseFatigueConsumable(Slot, service).Outcome == ConsumableUseOutcome.Committed &&
                f.Read().Inventory.DailyBoosterUses.Single().Used == 1, "资源日限次和疲劳、冷却同库存提交");
            clock.Advance(TimeSpan.FromSeconds(11));
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            var before = f.Read();
            That(f.Session.UseFatigueConsumable(Second, service).Outcome == ConsumableUseOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(before, f.Read()), "冷却到期、换实例和重选不能绕过资源日限次");
            clock.Advance(TimeSpan.FromDays(1));
            That(f.Session.UseFatigueConsumable(Second, service).Outcome == ConsumableUseOutcome.Committed &&
                f.Read().Inventory.DailyBoosterUses.Single().Used == 1 && f.Read().Fatigue!.Used == 120 - 2 * amount,
                "新服务日恢复额度，按资源恢复量再次恢复，不重放前一日使用");
        }
        using (var f = Make(used: 120, itemId: 7456))
        {
            var before = f.Read(); var plan = service.Prepare(before, Slot);
            clock.Advance(DailyBoosterUsage.NextReset(clock.GetUtcNow()) - clock.GetUtcNow());
            Reject<InventoryQualificationExpiredException>(() => f.Repository.CommitFatigueConsumable(f.Owner, plan, clock.GetUtcNow()),
                "跨06:00的旧日次数计划不能提交");
            That(QuestCompletionRepositoryTests.Same(before, f.Read()), "日边界拒绝完整回滚道具、冷却、次数和疲劳");
        }
        using (var f = Make(used: 120, itemId: 2682613))
        {
            That(f.Session.UseFatigueConsumable(Slot, service).Outcome == ConsumableUseOutcome.Committed && f.Read().Fatigue!.Used == 119,
                "携带突击排除位的疲劳药在允许的城镇仍按标签恢复1点");
        }
        Console.WriteLine($"PASS {Count - checks} 主动疲劳恢复资源、冷却及真实事务断言");

        QuestOrdinaryCompletionTests.Fixture Make(bool lost = false, int used = 50, int itemId = Sample, byte level = 1)
        {
            var f = new QuestOrdinaryCompletionTests.Fixture(towns, characters, skills, growth, parameters, items, QuestEligibilityTests.Quest(100),
                materialCount: 0, loseResult: lost, active: [], configure: candidate => candidate with
                {
                    Level = level,
                    Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.AddRange(new[]
                    {
                        new InventoryItem(0, Slot, new(itemId, ItemKind.Consumable, 1) { ExpireTime = 2145852000 }),
                        new InventoryItem(0, Second, new(itemId, ItemKind.Consumable, 1) { ExpireTime = 2145852000 })
                    }) }
                });
            f.Sql($"UPDATE character_fatigue SET used={used},remaining=maximum-{used},used_maximum=23,context_auxiliary=11,selection_auxiliary=9");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            return f;
        }
    }
}
