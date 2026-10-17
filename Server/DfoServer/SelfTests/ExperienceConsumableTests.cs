using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class ExperienceConsumableTests
{
    internal const int PercentItem = 10006047;
    internal const int FixedItem = 10004881;
    static readonly InventorySlot Slot = new(InventoryListType.Main, 65);

    public static void RunCore()
    {
        int checks = Count;
        That(ExperienceConsumableService.Calculate(false, 200000, 1035) == 200000 &&
            ExperienceConsumableService.Calculate(true, 10, 1035) == 103 &&
            ExperienceConsumableService.Calculate(true, 150, 1035) == 1552,
            "固定经验取原值，百分比取当前完整等级区间并向下取整，不按累计或剩余经验计算");
        That(ExperienceConsumableService.Calculate(true, 10, ulong.MaxValue) == ulong.MaxValue / 10,
            "百分比中间乘法使用宽整数，不截断32位或先除后乘");
        That(ExperienceConsumableRequests.Read([65, 0]) == Slot && ExperienceConsumableRequests.Read([255, 255]).Index == -1,
            "CMD30严格保留u16源槽，非法槽交给业务拒绝");
        foreach (byte[] body in new byte[][] { [], [65], [65, 0, 0] })
            TestCheck.Reject<InvalidDataException>(() => ExperienceConsumableRequests.Read(body), "CMD30不接受44或418的正文");
        Bytes(ExperienceConsumablePackets.Rejected(), [0, 19, 0], "CMD30普通失败保留本版u16错误前缀");
        var template = new ItemTemplate(1, ItemKind.Consumable, 0, 1, 0, 99, 0, 1, 1, "[trade]", ushort.MaxValue, 0, "capsule");
        var raw = new StackableFile(1, "stackable/capsule.stk", new("[etc]", [0]), 99, null, null, 1, null, [])
        { DeclaredNeededMaterials = [], DeclaredMaximumLevel = 94, HasMaximumLevel = true };
        var file = new ExperienceConsumableFile(raw, new("[expPercentUp]", [10]), null, [], ["increase status type"], []);
        That(ExperienceConsumableDefinition.Parse(file, template) is { Percent: true, Amount: 10 }, "百分比和数值型声明按标签区分");
        foreach (var bad in new[] { file with { Effect = new("[expUpByPercent]", [10]) }, file with { Effect = new("[expUp]", [0]) },
            file with { Effect = new("[expUp]", [1, 2]) }, file with { Diagnostics = ["duplicate"] },
            file with { DeclaredFields = ["increase status type", "action type"] } })
            That(ExperienceConsumableDefinition.Parse(bad, template) is null, "不把其他动作、畸形或复合经验效果伪装成已支持胶囊");
        That(ExperienceConsumableDefinition.Parse(file with { ExcludedJobs = ["[mage]"] }, template) is { } excluded &&
            !excluded.Allows(10, 3) && excluded.Allows(10, 2), "排除职业不是可用职业的并集");
        That(ExperienceConsumableDefinition.Parse(file with { ExcludedJobs = ["mage"] }, template) is { } bare &&
            !bare.Allows(10, 3) && bare.Allows(10, 2) &&
            ExperienceConsumableDefinition.Parse(file with { ExcludedJobs = ["[mage"] }, template) is null,
            "排除职业接受资源中的裸名称，不把残缺括号修补成合法声明");
        Console.WriteLine($"PASS {Count - checks} 经验物品纯规则与CMD30边界断言");
    }

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = SelfTestCatalogs.Growth(pvf, skills);
        var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
        var clock = new ConsumableUseSessionTests.FakeClock();
        var service = new ExperienceConsumableService(items.Items, growth, items.EpicPieces, clock);
        That(items.Items.ExperienceConsumable(PercentItem) is { Percent: true, Amount: 10 } &&
            items.Items.ExperienceConsumable(FixedItem) is { Percent: false, Amount: 500000 } &&
            items.Items.ExperienceConsumable(10005107) is { Percent: true, Amount: 10, Materials: [{ ItemId: 10005066, Count: 6 }] } &&
            items.Items.ExperienceConsumable(LevelUpTicketTests.TicketId) is null,
            "真实LST覆盖固定、百分比、附加材料并与升级券分开");
        That(items.Items.ExperienceConsumable(10092765) is { Percent: false, Amount: 1000000 } bareJob &&
            bareJob.Allows(17, 3) && !bareJob.Allows(17, 12), "真实经验胶囊的裸knight排除只限制守护者，不误拒绝其他职业");
        using (var f = Make(49, PercentItem, nearLevel: true))
        {
            var before = f.Read();
            var prepared = service.Prepare(before, Slot);
            ulong gain = growth.GetLevelExperienceSpan(49) * 10 / 100;
            Bytes(ExperienceConsumablePackets.Used(prepared), TcpPeer.Build(w =>
            { w.Write((byte)1); w.Write((ushort)65); w.Write((byte)1); w.Write((uint)gain); w.Write((ushort)growth.GetSpDelta(49, 50)); w.Write((ushort)1); }),
                "成功30回kind1和实际经验/SP/TP差，不把资源enum3发给客户端");
            var result = f.Session.UseExperienceConsumable(Slot, service);
            That(result.Inventory?.Receipt is { Character.Level: 50 } receipt && Same(receipt.Character, f.Read()) &&
                f.Read().Learning.Experience == before.Learning.Experience + gain &&
                f.Read().Inventory.Find(Slot)!.Core.Count == 1 && f.Read().Revision == before.Revision + 1 &&
                f.Read().Learning.BonusSp == 17 && f.Read().Learning.BonusTp == 5 &&
                f.Read().CompletedQuests.SequenceEqual(before.CompletedQuests), "升50、扣一张和保留额外点数同事务，不自动跳过主线");
            var second = f.Session.UseExperienceConsumable(Slot, service);
            That(second.Experience!.Growth.GainedExperience == growth.GetLevelExperienceSpan(50) * 10 / 100 &&
                f.Read().Inventory.Find(Slot) is null, "无冷却胶囊可连续使用，第二次按新等级重新计算百分比");
            var empty = f.Read();
            That(f.Session.UseExperienceConsumable(Slot, service).Outcome == ConsumableUseOutcome.Rejected && Same(empty, f.Read()),
                "已耗尽源不会因重复请求再加经验");
        }
        using (var f = Make(60, FixedItem))
        {
            var before = f.Read(); var plan = service.Prepare(before, Slot);
            f.Sql("CREATE TRIGGER experience_abort AFTER UPDATE OF level ON characters BEGIN SELECT RAISE(ABORT,'experience rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Repository.CommitExperienceConsumable(f.Owner, plan, clock.GetUtcNow()), "扣物后的真实SQL失败");
            That(Same(before, f.Read()) && Same(before, f.Session.Selected!), "SQL回滚保留源、金币、经验、两树和revision");
            f.Sql("DROP TRIGGER experience_abort");
            var committed = f.Repository.CommitExperienceConsumable(f.Owner, plan, clock.GetUtcNow());
            That(committed.Learning.Experience == before.Learning.Experience + 500000 && committed.Inventory.Find(Slot)!.Core.Count == 1,
                "固定值不乘经验奖励倍率，明确回滚后原计划可提交");
            TestCheck.Reject<InvalidOperationException>(() => f.Repository.CommitExperienceConsumable(f.Owner, plan, clock.GetUtcNow()), "旧revision不重扣或重加经验");
        }
        using (var f = Make(60, FixedItem, lost: true))
        {
            f.Lost!.LoseExperienceResult = true;
            var before = f.Read();
            TestCheck.Reject<IOException>(() => f.Session.UseExperienceConsumable(Slot, service), "已提交但返回结果丢失");
            That(f.Read().Learning.Experience == before.Learning.Experience + 500000 && Same(before, f.Session.Selected!),
                "未知结果不安装旧内存或假报失败回滚");
            That(f.Session.UseExperienceConsumable(Slot, service).Outcome == ConsumableUseOutcome.CloseConnection,
                "未知结果保留未决状态，不能重发增量ACK");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(f.Read(), f.Session.Selected!), "重新选角加载实际已提交经验和库存，不补发奖励");
        }
        using (var f = Make(60, FixedItem))
        {
            var before = f.Read(); var source = before.Inventory.Find(Slot)!;
            foreach (var core in new[] { source.Core with { EquipmentLockId = 1 }, source.Core with { ExpireTime = 1 }, source.Core with { Value = 0 } })
                TestCheck.Reject<InvalidDataException>(() => service.Prepare(before with { Inventory = before.Inventory with
                { Items = before.Inventory.Items.Select(item => item.Position == Slot ? item with { Core = core } : item).ToImmutableArray() } }, Slot),
                    "锁定、过期或空数量拒绝");
            TestCheck.Reject<InvalidDataException>(() => service.Prepare(before with { Level = 1 }, Slot), "最低等级不符不能消费");
            var stale = service.Prepare(before, Slot);
            using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
            TestCheck.Reject<InvalidOperationException>(() => f.Repository.CommitExperienceConsumable(f.Owner, stale, clock.GetUtcNow()), "旧generation不能提交胶囊");
        }
        using (var f = Make(60, 10005107))
        {
            var before = f.Read();
            That(f.Session.UseExperienceConsumable(Slot, service).Outcome == ConsumableUseOutcome.Rejected && Same(before, f.Read()),
                "缺少胶囊父亲材料不能只扣胶囊加经验");
        }
        using (var f = Make(60, 10005107, materialCount: 7))
        {
            var before = f.Read();
            var result = f.Session.UseExperienceConsumable(Slot, service);
            That(result.Outcome == ConsumableUseOutcome.Committed && f.Read().Inventory.Find(Slot)!.Core.Count == 1 &&
                f.Read().Inventory.Items.Single(item => item.Core.ItemId == 10005066).Core.Count == 1 &&
                f.Read().Learning.Experience == before.Learning.Experience + growth.GetLevelExperienceSpan(60) * 10 / 100,
                "胶囊父亲所需六份材料与一张胶囊和百分比经验同事务提交");
        }
        using (var f = Make(60, FixedItem))
        {
            var expiryClock = new ConsumableUseSessionTests.FakeClock();
            var expiryService = new ExperienceConsumableService(items.Items, growth, items.EpicPieces, expiryClock);
            f.Session.MutateInventory(character => new(true, 0, new(character.Inventory, character.Inventory with
            {
                Items = character.Inventory.Items.Select(item => item.Position == Slot ? item with
                    { Core = item.Core with { ExpireTime = 1900000001 } } : item).ToImmutableArray()
            }, "capsule_fixture_expiry")));
            var before = f.Read();
            expiryClock.BeforeUtcRead = () => { if (expiryClock.Reads == 2) expiryClock.Advance(TimeSpan.FromSeconds(2)); };
            That(f.Session.UseExperienceConsumable(Slot, expiryService).Outcome == ConsumableUseOutcome.Rejected && Same(before, f.Read()),
                "锁外规划后到期不进入写事务，正常失败收尾并保留源和经验");
        }
        Console.WriteLine($"PASS {Count - checks} 经验物品实际资源、成长、事务和会话断言");

        QuestOrdinaryCompletionTests.Fixture Make(byte level, int id, bool nearLevel = false, bool lost = false, int materialCount = 0) =>
            new(towns, characters, skills, growth, parameters, items, QuestEligibilityTests.Quest(100), materialCount: 0, loseResult: lost,
                active: [], configure: candidate =>
                {
                    var inventory = candidate.Inventory.Items.Add(new(0, Slot, new(id, ItemKind.Consumable, 2)));
                    if (materialCount > 0)
                    {
                        var material = items.Items.Get(10005066);
                        short index = checked((short)(ItemSlotBoundService.Range(material.Kind, candidate.Inventory.Expansion).Start + 1));
                        inventory = inventory.Add(new(0, new(InventoryListType.Main, index), new(10005066, material.Kind, materialCount)));
                    }
                    return candidate with
                    {
                        Level = level, InitialLearning = SkillPointLedger.Synchronize(0, 0, level, candidate.InitialLearning with
                        { Experience = (nearLevel ? growth.GetCumulativeExperienceBoundary(level) - 1 : growth.GetLevelStartExperience(level) + 1), BonusSp = 17, BonusTp = 5 }, skills),
                        Inventory = candidate.Inventory with { Items = inventory }
                    };
                });
    }
}
