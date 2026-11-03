using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
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
        That(ExperienceConsumableDefinition.Parse(file with { Stackable = raw with { DeclaredNeededMaterials = [1000,6] } }, template)
            is { Percent: true, Amount: 10 }, "商店兑换材料不影响经验胶囊的使用解析");
        foreach (var (places, roomOnly) in new (string[], bool)[]
            { (["[village]"], false), (["[village]", "[seria room]"], false),
              (["[seria room]", "[village]"], false), (["[seria room]"], true) })
            That(ExperienceConsumableDefinition.Parse(file with { Stackable = raw with
                { HasActionUsablePlace = true, DeclaredActionUsablePlaces = places } }, template) is { VillageOnly: true } location &&
                location.SeriaRoomOnly == roomOnly, "经验物品地点按并集解释，单独赛丽亚房间仍保留限制");
        foreach (string[]? places in new string[]?[] { null, [], ["[village]", "[unknown]"], ["[dungeon]"] })
            That(ExperienceConsumableDefinition.Parse(file with { Stackable = raw with
                { HasActionUsablePlace = true, DeclaredActionUsablePlaces = places } }, template) is not null,
                "地点声明的类型和数量不改变经验物品身份，使用时按地点匹配");
        var restricted = file with { Stackable = raw with { DeclaredUsableFirstGrowTypes = [3, 4, 5] },
            DeclaredFields = ["increase status type", "usable first growtype"] };
        That(ExperienceConsumableDefinition.Parse(restricted, template) is { } branches &&
            branches.Allows(10, 3, 3) && branches.Allows(10, 3, 0x24) && branches.Allows(10, 3, 0x25) &&
            !branches.Allows(10, 3, 0) && !branches.Allows(10, 3, 2),
            "经验胶囊转职限制读取低四位，觉醒不取消资格，其他转职与未转职均拒绝");
        foreach (int[]? growValues in new int[]?[] { null, [], [-1], [6], [3, 256] })
            That(ExperienceConsumableDefinition.Parse(restricted with { Stackable = raw with
                { DeclaredUsableFirstGrowTypes = growValues } }, template) is null,
                "非法转职声明不忽略、不截断字节、不放宽为所有职业");
        foreach (var bad in new[] { file with { Effect = new("[expUpByPercent]", [10]) }, file with { Effect = new("[expUp]", [0]) },
            file with { Effect = new("[expUp]", [1, 2]) }, file with { Diagnostics = ["duplicate"] },
            file with { DeclaredFields = ["increase status type", "action type"] } })
            That(ExperienceConsumableDefinition.Parse(bad, template) is null, "不把其他动作、畸形或复合经验效果伪装成已支持胶囊");
        That(ExperienceConsumableDefinition.Parse(file with { ExcludedJobs = ["[mage]"] }, template) is { } excluded &&
            !excluded.Allows(10, 3, 0) && excluded.Allows(10, 2, 0), "排除职业不是可用职业的并集");
        That(ExperienceConsumableDefinition.Parse(file with { ExcludedJobs = ["mage"] }, template) is { } bare &&
            !bare.Allows(10, 3, 0) && bare.Allows(10, 2, 0) &&
            ExperienceConsumableDefinition.Parse(file with { ExcludedJobs = ["[mage"] }, template) is null,
            "排除职业接受资源中的裸名称，不把残缺括号修补成合法声明");
        foreach (var (id, amount) in new[] { (1034, 100), (1035, 1000), (1036, 10000), (1037, 100000), (1231, 1000000), (1232, 10000000) })
        {
            var nativeTemplate = template with { Id = id };
            var native = file with { Stackable = raw with { TemplateId = id, ScriptPath = "stackable/book_exp" + (id < 1200 ? id - 1033 : id - 1226) + ".stk" },
                Effect = null, DeclaredFields = [] };
            That(ExperienceConsumableDefinition.Parse(native, nativeTemplate) is { Percent: false } book && book.Amount == amount,
                "旧经验书ID与规范路径同时命中时补原生固定经验 " + id);
            That(ExperienceConsumableDefinition.Parse(native with { Stackable = native.Stackable with { ScriptPath = "stackable/custom.stk" } }, nativeTemplate) is null,
                "编号相同但脚本身份不同不套旧书经验");
            That(ExperienceConsumableDefinition.Parse(native with { Effect = new("[expUp]", [321]) }, nativeTemplate) is { Amount: 321 },
                "显式PVF经验标签优先于旧编号默认值");
            That(ExperienceConsumableDefinition.Parse(native with { Effect = new("[unknown]", [321]) }, nativeTemplate) is null,
                "未知显式效果不退回旧书默认经验");
        }
        Console.WriteLine($"PASS {Count - checks} 经验物品纯规则与CMD30边界断言");
    }

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = SelfTestCatalogs.Growth(pvf, skills);
        var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
        var clock = new ConsumableUseSessionTests.FakeClock();
        var service = new ExperienceConsumableService(items.Items, growth, clock);
        foreach (var (id, amount) in new[] { (1034, 100), (1035, 1000), (1036, 10000), (1037, 100000), (1231, 1000000), (1232, 10000000) })
        {
            That(items.Items.ExperienceConsumable(id) is { Percent: false } book && book.Amount == amount,
                "当前PVF的旧经验书无需效果标签即可查询 " + id);
            using var bookFixture = Make(60, id, nearLevel: true);
            var previous = bookFixture.Read();
            byte target = 60;
            while (previous.Learning.Experience + (uint)amount >= growth.GetCumulativeExperienceBoundary(target)) target++;
            var used = bookFixture.Session.UseExperienceConsumable(Slot, service);
            That(used.Outcome == ConsumableUseOutcome.Committed && bookFixture.Read().Level == target &&
                bookFixture.Read().Learning.Experience == previous.Learning.Experience + (uint)amount &&
                bookFixture.Read().Inventory.Find(Slot)!.Core.Count == 1 &&
                bookFixture.Read().Learning.BonusSp == previous.Learning.BonusSp && bookFixture.Read().Learning.BonusTp == previous.Learning.BonusTp,
                "旧书沿已有成长事务升级、扣一件并保留额外来源 " + id);
        }
        foreach (int id in new[] { 1231, 1232 })
        {
            using var capped = Make(94, id, nearLevel: true);
            var used = capped.Session.UseExperienceConsumable(Slot, service);
            That(used.Outcome == ConsumableUseOutcome.Committed && used.Experience!.Growth.GainedExperience == 1 &&
                capped.Read().Level == 95 && capped.Read().Learning.Experience == growth.GetLevelStartExperience(95) &&
                capped.Read().Inventory.Find(Slot)!.Core.Count == 1,
                "大额经验书跨满级只到账剩余额度并扣一件 " + id);
            var full = capped.Read();
            That(capped.Session.UseExperienceConsumable(Slot, service).Outcome == ConsumableUseOutcome.Rejected && Same(full, capped.Read()),
                "满级零到账不继续消耗经验书 " + id);
        }
        That(items.Items.ExperienceConsumable(PercentItem) is { Percent: true, Amount: 10 } &&
            items.Items.ExperienceConsumable(FixedItem) is { Percent: false, Amount: 500000 } &&
            items.Items.ExperienceConsumable(10005107) is { Percent: true, Amount: 10 } &&
            items.Items.ExperienceConsumable(LevelUpTicketTests.TicketId) is null,
            "真实LST覆盖固定、百分比和商店兑换胶囊并与升级券分开");
        That(items.Items.ExperienceConsumable(10092765) is { Percent: false, Amount: 1000000 } bareJob &&
            bareJob.Allows(17, 3, 0) && !bareJob.Allows(17, 12, 0), "真实经验胶囊的裸knight排除只限制守护者，不误拒绝其他职业");
        foreach (int id in new[] { 490002047, 490002048, 490003457, 490003458, 490004851, 490004852,
            490005337, 490006778, 490006779, 490008997, 490009024, 490009515, 490009516, 490009955,
            490009956, 490009957, 490010167, 490010192, 490010377, 490010752,
            10005340, 10005341, 10006651, 10006652 })
            That(items.Items.ExperienceConsumable(id) is { VillageOnly: true, SeriaRoomOnly: false },
                "真实城镇与赛丽亚并集胶囊不因两个合法地点被整体拒绝 " + id);
        foreach (int id in new[] { 490012478, 490012481 })
            That(items.Items.ExperienceConsumable(id) is null, "地点修复不放行重复限制字段的月饼 " + id);
        That(items.Items.ExperienceConsumable(490003969) is { } maleMage &&
            maleMage.Allows(60, 8, 0x23) && !maleMage.Allows(60, 8, 2) && !maleMage.Allows(60, 3, 3) &&
            items.Items.ExperienceConsumable(490005503) is { } knight &&
            knight.Allows(60, 12, 0x24) && !knight.Allows(60, 12, 1) && !knight.Allows(87, 12, 4),
            "实际男法三分支与守护者两分支胶囊同时保留职业、转职与最高等级限制");
        foreach (byte branch in new byte[] { 2, 0x23 })
        {
            using var f = Make(60, 490003969, job: 8, growType: branch);
            var before = f.Read();
            var result = f.Session.UseExperienceConsumable(Slot, service);
            if (branch == 2)
                That(result.Outcome == ConsumableUseOutcome.Rejected && Same(before, f.Read()),
                    "不符合首转分支的真实男法胶囊拒绝且库存与成长不变");
            else
            {
                That(result.Outcome == ConsumableUseOutcome.Committed &&
                    f.Read().Learning.Experience == before.Learning.Experience + growth.GetLevelExperienceSpan(60) / 10 &&
                    f.Read().Inventory.Find(Slot)!.Core.Count == 1 && f.Read().GrowType == branch,
                    "觉醒后的合法首转分支消费真实胶囊，不改变转职并只扣一件");
                var prepared = service.Prepare(f.Read(), Slot);
                TestCheck.Reject<InvalidDataException>(() => f.Repository.CommitExperienceConsumable(f.Owner,
                    prepared with { Definition = prepared.Definition with { FirstGrowTypes = [2] } }, clock.GetUtcNow()),
                    "事务入口独立复验首转分支，不只依赖服务预检");
                f.Sql("UPDATE characters SET grow_type=2");
                var changed = f.Read();
                TestCheck.Reject<InvalidOperationException>(() => f.Repository.CommitExperienceConsumable(f.Owner,
                    prepared, clock.GetUtcNow()), "首转变更后的旧经验计划不得提交");
                That(Same(changed, f.Read()), "持久化首转变化导致拒绝，未扣剩余源或追加经验");
            }
        }
        using (var f = Make(60, 10005340))
        {
            var before = f.Read();
            var result = f.Session.UseExperienceConsumable(Slot, service);
            That(result.Outcome == ConsumableUseOutcome.Committed &&
                f.Read().Learning.Experience == before.Learning.Experience + 100000 &&
                f.Read().Inventory.Find(Slot)!.Core.Count == 1,
                "合法地点并集的真实胶囊沿原事务发经验并只消费一件");
        }
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
        using (var f = Make(60, 1232))
        {
            var before = f.Read(); var plan = service.Prepare(before, Slot);
            f.Sql("CREATE TRIGGER experience_abort AFTER UPDATE OF level ON characters BEGIN SELECT RAISE(ABORT,'experience rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Repository.CommitExperienceConsumable(f.Owner, plan, clock.GetUtcNow()), "扣物后的真实SQL失败");
            That(Same(before, f.Read()) && Same(before, f.Session.Selected!), "SQL回滚保留源、金币、经验、两树和revision");
            f.Sql("DROP TRIGGER experience_abort");
            var committed = f.Repository.CommitExperienceConsumable(f.Owner, plan, clock.GetUtcNow());
            That(committed.Learning.Experience == before.Learning.Experience + 10000000 && committed.Inventory.Find(Slot)!.Core.Count == 1,
                "固定值不乘经验奖励倍率，明确回滚后原计划可提交");
            TestCheck.Reject<InvalidOperationException>(() => f.Repository.CommitExperienceConsumable(f.Owner, plan, clock.GetUtcNow()), "旧revision不重扣或重加经验");
        }
        using (var f = Make(60, 1231, lost: true))
        {
            f.Lost!.LoseExperienceResult = true;
            var before = f.Read();
            TestCheck.Reject<IOException>(() => f.Session.UseExperienceConsumable(Slot, service), "已提交但返回结果丢失");
            That(f.Read().Learning.Experience == before.Learning.Experience + 1000000 && Same(before, f.Session.Selected!),
                "未知结果不安装旧内存或假报失败回滚");
            That(f.Session.UseExperienceConsumable(Slot, service).Outcome == ConsumableUseOutcome.CloseConnection,
                "未知结果保留未决状态，不能重发增量ACK");
            var committed = f.Read();
            TestCheck.Reject<InvalidOperationException>(() => f.Session.SetCardMembership(before.Name, CardMembershipKind.BlackDiamond,
                clock.GetUtcNow().AddDays(1).ToUnixTimeSeconds()), "消耗提交结果未确认时不能借黑钻替换原角色快照");
            That(Same(committed, f.Read()) && Same(before, f.Session.Selected!), "拒绝黑钻修改不影响已提交经验与原未决内存");
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
            That(f.Session.UseExperienceConsumable(Slot, service).Outcome == ConsumableUseOutcome.Committed &&
                f.Read().Inventory.Find(Slot)!.Core.Count == 1 &&
                f.Read().Learning.Experience == before.Learning.Experience + growth.GetLevelExperienceSpan(60) * 10 / 100,
                "已获得的兑换胶囊不要求再次持有购买材料，只消费一件并发放经验");
        }
        using (var f = Make(60, 10005107, materialCount: 7))
        {
            var before = f.Read();
            var result = f.Session.UseExperienceConsumable(Slot, service);
            That(result.Outcome == ConsumableUseOutcome.Committed && f.Read().Inventory.Find(Slot)!.Core.Count == 1 &&
                f.Read().Inventory.Items.Single(item => item.Core.ItemId == 10005066).Core.Count == 7 &&
                f.Read().Learning.Experience == before.Learning.Experience + growth.GetLevelExperienceSpan(60) * 10 / 100,
                "胶囊使用不重复扣除商店兑换材料，已有材料余额不变");
        }
        using (var f = Make(60, FixedItem))
        {
            var expiryClock = new ConsumableUseSessionTests.FakeClock();
            var expiryService = new ExperienceConsumableService(items.Items, growth, expiryClock);
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

        QuestOrdinaryCompletionTests.Fixture Make(byte level, int id, bool nearLevel = false, bool lost = false, int materialCount = 0, byte job = 0, byte growType = 0) =>
            new(towns, characters, skills, growth, parameters, items, QuestEligibilityTests.Quest(100), materialCount: 0, loseResult: lost,
                active: [], configure: candidate =>
                {
                    if (job != 0)
                        candidate = CharacterCreation.Prepare(candidate.Name, characters.Get(job), towns.Room, towns.InitialReturn, skills.Get);
                    var inventory = candidate.Inventory.Items.Add(new(0, Slot, new(id, ItemKind.Consumable, 2)));
                    if (materialCount > 0)
                    {
                        var material = items.Items.Get(10005066);
                        short index = checked((short)(ItemSlotBoundService.Range(material.Kind, candidate.Inventory.Expansion).Start + 1));
                        inventory = inventory.Add(new(0, new(InventoryListType.Main, index), new(10005066, material.Kind, materialCount)));
                    }
                    return candidate with
                    {
                        Level = level, GrowType = growType, InitialLearning = SkillPointLedger.Synchronize(job, growType, level, candidate.InitialLearning with
                        { Experience = (nearLevel ? growth.GetCumulativeExperienceBoundary(level) - 1 : growth.GetLevelStartExperience(level) + 1), BonusSp = 17, BonusTp = 5 }, skills),
                        Inventory = candidate.Inventory with { Items = inventory }
                    };
                });
    }
}
