using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class PermanentGrowthTests
{
    internal static readonly (int Id, string Path, PermanentGrowthKind Kind, int Amount)[] Cases =
    [
        (1031, "book_skill1.stk", PermanentGrowthKind.Sp, 5), (1038, "book_skill2.stk", PermanentGrowthKind.Sp, 20),
        (1039, "stone_str.stk", PermanentGrowthKind.Strength, 50), (1040, "stone_int.stk", PermanentGrowthKind.Intelligence, 50),
        (1041, "stone_helth.stk", PermanentGrowthKind.Vitality, 50), (1042, "stone_mind.stk", PermanentGrowthKind.Spirit, 50),
        (1043, "stone_hp.stk", PermanentGrowthKind.MaxHp, 250), (1044, "stone_mp.stk", PermanentGrowthKind.MaxMp, 250),
        (1045, "stone_speed.stk", PermanentGrowthKind.MoveSpeed, 10), (1046, "stone_allr.stk", PermanentGrowthKind.AllElementResistance, 10),
        (1204, "book_fskill1.stk", PermanentGrowthKind.Tp, 1), (1205, "book_fskill2.stk", PermanentGrowthKind.Tp, 5)
    ];
    static readonly InventorySlot Slot = new(InventoryListType.Main, 65);

    public static void RunCore()
    {
        int checks = Count;
        foreach (var (id, path, kind, amount) in Cases)
        {
            var template = new ItemTemplate(id, ItemKind.Consumable, 0, 1, 0, 1000, 0, 3, 1, "[trade]", ushort.MaxValue, 0, "fixture");
            var raw = new StackableFile(id, "stackable/" + path, new("[etc]", [1]), 1000, null, null, 1, null, []);
            var file = new ExperienceConsumableFile(raw, null, null, [], [], []);
            var definition = PermanentGrowthDefinition.Parse(file, template)!;
            That(definition is not null && definition.Kind == kind && definition.Amount == amount,
                "原生永久物品按注册ID和脚本双重识别，不依赖本地化说明 " + id);
            foreach (var bad in new[] { file with { Stackable = raw with { ScriptPath = "stackable/custom.stk" } },
                file with { Effect = new("[expUp]", [10]) }, file with { Diagnostics = ["duplicate"] },
                file with { DeclaredFields = ["action type"] } })
                That(PermanentGrowthDefinition.Parse(bad, template) is null, "错位、复合或畸形定义不能发永久奖励");
            var character = new Character(1, 0, "Book", 0, new(38, 1, 450, 234), null)
            {
                Learning = new(123, Array.AsReadOnly(new[] { new CharacterSkillTree(0, 7, 2, []), new CharacterSkillTree(1, 8, 3, []) }))
                    { BonusSp = 11, BonusTp = 13 }
            };
            var after = PermanentGrowthService.Apply(character, definition!);
            if (kind is PermanentGrowthKind.Sp or PermanentGrowthKind.Tp)
            {
                bool sp = kind == PermanentGrowthKind.Sp;
                That(after.Learning.Experience == 123 && after.Bonuses == PermanentStatBonuses.Empty &&
                    after.Learning.BonusSp == 11 + (sp ? amount : 0) && after.Learning.BonusTp == 13 + (sp ? 0 : amount) &&
                    after.Learning.Trees.Zip(character.Learning.Trees).All(pair =>
                        pair.First.Sp == pair.Second.Sp + (sp ? amount : 0) && pair.First.Tp == pair.Second.Tp + (sp ? 0 : amount)),
                    "书同时增加来源与两树余额，不改变原树技能、经验或其他点数");
                TestCheck.Reject<InvalidDataException>(() => PermanentGrowthService.Apply(character with
                    { Learning = character.Learning with { BonusSp = null, BonusTp = null } }, definition!), "未知来源不从余额猜奖励");
            }
            else
                That(ReferenceEquals(after.Learning, character.Learning) && after.Bonuses != PermanentStatBonuses.Empty,
                    "属性石只累加永久属性来源，不改学习树或经验");
            var source = new InventoryItem(9, Slot, new(id, ItemKind.Consumable, 1));
            var plan = new PermanentGrowthPlan(character, source, definition!, null!, after.Bonuses, after.Learning);
            Bytes(ExperienceConsumablePackets.Permanent(plan), TcpPeer.Build(w =>
                { w.Write((byte)1); w.Write((ushort)65); w.Write((byte)kind); w.Write((uint)amount); w.Write(0u); }),
                "CMD30种类、定点增量及两个零尾项使用独立字节合同");
        }
        That(!PermanentGrowthDefinition.IsNativeItem(80003) && !PermanentGrowthDefinition.IsNativeItem(1034),
            "不借相同脚本文本接入未证编号，也不接管已有经验书");
        var basis = new CharacterStats(100, 200, 10, 20, 30, 40, -10, 10, 20, 30, 1000, 50, 60, 70, 80, 90, 100, 110);
        var bonus = new PermanentStatBonuses(250, 250, 50, 50, 50, 50, 10, 10);
        var expected = basis with { MaxHp = 350, MaxMp = 450, PhysicalAttack = 60, PhysicalDefense = 70,
            MagicalAttack = 80, MagicalDefense = 90, MoveSpeed = 70, FireResistance = 0, WaterResistance = 20, DarkResistance = 30, LightResistance = 40 };
        That(bonus.Apply(basis) == expected && bonus.Apply(basis) == expected,
            "永久增量只与原始基础合成一次，所有抗性一起增长，重复计算不累计");
        TestCheck.Reject<OverflowException>(() => bonus.Apply(basis with { PhysicalAttack = short.MaxValue }), "属性溢出不截断或绕回负数");
        TestCheck.Reject<OverflowException>(() => new PermanentStatBonuses(MaxHp: uint.MaxValue).Add(PermanentGrowthKind.MaxHp, 250), "HP来源不绕回零");
        Console.WriteLine($"PASS {Count - checks} 永久道具映射、点数来源和定点投影断言");
    }

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = SelfTestCatalogs.Growth(pvf, skills);
        var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
        var clock = new ConsumableUseSessionTests.FakeClock();
        var stats = new CharacterStatComputer(characters);
        var service = new PermanentGrowthService(items.Items, stats, skills, clock);
        foreach (var row in Cases)
            That(items.Items.PermanentGrowth(row.Id) is { } definition && definition.Kind == row.Kind && definition.Amount == row.Amount,
                "当前PVF的LST及原生永久道具声明 " + row.Id);
        That(items.Items.PermanentGrowth(80003) is null && items.Items.PermanentGrowth(ExperienceConsumableTests.FixedItem) is null,
            "测试资源和经验胶囊不混入永久增长目录");
        using (var f = Make())
        {
            var before = f.Read();
            for (short index = 0; index < Cases.Length; index++)
                for (int use = 0; use < 2; use++)
                {
                    var slot = new InventorySlot(InventoryListType.Main, checked((short)(65 + index)));
                    var result = f.Session.UsePermanentGrowth(slot, service);
                    That(result.Outcome == ConsumableUseOutcome.Committed && result.PermanentGrowth?.Definition.Kind == Cases[index].Kind,
                        "每次实际消费一件并提交对应永久增量 " + Cases[index].Id);
                }
            var after = f.Read();
            That(after.PermanentStats == new PermanentStatBonuses(500, 500, 100, 100, 100, 100, 20, 20) &&
                after.Learning.BonusSp == before.Learning.BonusSp + 50 && after.Learning.BonusTp == before.Learning.BonusTp + 12 &&
                after.Learning.Experience == before.Learning.Experience && after.Level == before.Level &&
                after.Revision == before.Revision + 24 && after.Inventory.Gold == before.Inventory.Gold,
                "全部永久来源、SP/TP两树与库存同事务，使用不升级或改经验/金币");
            That(after.Learning.Trees.Zip(before.Learning.Trees).All(pair => pair.First.Sp == pair.Second.Sp + 50 && pair.First.Tp == pair.Second.Tp + 12),
                "主副两树均获得相同额外预算");
            var empty = f.Session.UsePermanentGrowth(Slot, service);
            That(empty.Outcome == ConsumableUseOutcome.Rejected && Same(after, f.Read()), "源已空不重复授予");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Session.Selected!.PermanentStats == after.PermanentStats && Same(f.Session.Selected!, after) &&
                new CharacterRepository(f.OpenPath).LoadCharacter(f.Owner, 0).PermanentStats == after.PermanentStats,
                "重新选角及仓储重开加载永久来源，不从派生面板覆盖");
            var synchronized = SkillPointLedger.Synchronize(after.Job, after.GrowType, after.Level, after.Learning, skills);
            That(synchronized.BonusSp == after.Learning.BonusSp && synchronized.BonusTp == after.Learning.BonusTp &&
                synchronized.Trees.Zip(after.Learning.Trees).All(pair => pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp),
                "重算不会纠正掉书获得的额外点数");
            foreach (byte context in new byte[] { 0, 1 })
            {
                var reset = SkillPointLedger.Reset(after.Job, after.GrowType, after.Level, after.Learning, context, skills, 0);
                That(reset.Success && reset.TotalSp == skills.TotalSp(after.Level) + after.Learning.BonusSp &&
                    reset.TotalTp == skills.TotalTp(after.Level) + after.Learning.BonusTp, "洗点总预算保留道具额外来源");
                That(f.Session.ResetSkills(context, 0, skills).Success && f.Read().PermanentStats == after.PermanentStats &&
                    f.Read().Learning.BonusSp == after.Learning.BonusSp && f.Read().Learning.BonusTp == after.Learning.BonusTp,
                    "真实洗点写回不丢属性表或额外技能预算");
            }
            var upgraded = CharacterGrowthPlan.Create(after, 61, growth.GetLevelStartExperience(61), growth).Plan!;
            That(upgraded.Learning.BonusSp == after.Learning.BonusSp && upgraded.Learning.BonusTp == after.Learning.BonusTp,
                "升级规划保留SP/TP书来源");
            foreach (byte grow in new byte[] { 0, 1, 0x11, 0x21 })
            {
                var profession = QuestProfessionReward.Prepare(after, grow, 61, upgraded.Learning, skills, clock.GetUtcNow());
                var changed = after with { GrowType = grow, Level = 61, Learning = profession };
                That(stats.Compute(changed) == after.PermanentStats.Apply(stats.ComputeLevelBaseStats(changed.Job, grow, 61)),
                    "升级、转职及觉醒的所有属性投影保留永久来源 " + grow);
                That(profession.BonusSp == after.Learning.BonusSp && profession.BonusTp == after.Learning.BonusTp,
                    "转职双树重建及觉醒赠技保留道具额外预算 " + grow);
            }
        }
        using (var f = Make())
        {
            var before = f.Read();
            var plan = service.Prepare(before, new(InventoryListType.Main, 67));
            f.Sql("CREATE TRIGGER permanent_abort AFTER INSERT ON character_permanent_stats BEGIN SELECT RAISE(ABORT,'permanent rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Repository.CommitPermanentGrowth(f.Owner, plan, clock.GetUtcNow()), "属性来源末段SQL失败");
            That(Same(before, f.Read()) && Same(before, f.Session.Selected!), "SQL回滚保留源物、属性、技能、金币和revision");
            f.Sql("DROP TRIGGER permanent_abort");
            var committed = f.Repository.CommitPermanentGrowth(f.Owner, plan, clock.GetUtcNow());
            That(committed.PermanentStats.Strength == 50 && committed.Inventory.Find(plan.Source.Position)!.Core.Count == 1,
                "明确回滚后的同一计划可提交真实来源");
            TestCheck.Reject<InvalidOperationException>(() => f.Repository.CommitPermanentGrowth(f.Owner, plan, clock.GetUtcNow()), "旧revision不重复扣物或增长");
            using var next = f.Owners.Acquire("ordinary-quest", () => { });
            TestCheck.Reject<InvalidOperationException>(() => f.Repository.CommitPermanentGrowth(f.Owner, plan, clock.GetUtcNow()), "旧generation不得提交");
        }
        using (var f = Make(lost: true))
        {
            f.Lost!.LosePermanentGrowthResult = true;
            var before = f.Read();
            TestCheck.Reject<IOException>(() => f.Session.UsePermanentGrowth(Slot, service), "永久成长已提交但返回丢失");
            That(f.Read().Learning.BonusSp == before.Learning.BonusSp + 5 &&
                f.Session.UsePermanentGrowth(Slot, service).Outcome == ConsumableUseOutcome.CloseConnection &&
                f.Read().Inventory.Find(Slot)!.Core.Count == 1, "提交不明关闭原连接，不重放奖励或再扣一件");
        }
        using (var f = Make())
        {
            var before = f.Read();
            var plan = service.Prepare(before, Slot);
            f.Sql("CREATE TRIGGER permanent_sp_abort AFTER UPDATE ON character_skill_point_sources BEGIN SELECT RAISE(ABORT,'SP source rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Repository.CommitPermanentGrowth(f.Owner, plan, clock.GetUtcNow()), "SP来源写入失败");
            That(Same(before, f.Read()), "SP书来源写入失败时库存、两树余额和revision全部回滚");
            f.Sql("DROP TRIGGER permanent_sp_abort");
            var locked = before with { Inventory = before.Inventory with { Items = before.Inventory.Items.Select(item =>
                item.Position == Slot ? item with { Core = item.Core with { EquipmentLockId = 1 } } : item).ToImmutableArray() } };
            TestCheck.Reject<InvalidDataException>(() => service.Prepare(locked, Slot), "安全锁不能绕过永久道具消费");
            var expired = before with { Inventory = before.Inventory with { Items = before.Inventory.Items.Select(item =>
                item.Position == Slot ? item with { Core = item.Core with { ExpireTime = 1 } } : item).ToImmutableArray() } };
            TestCheck.Reject<InvalidDataException>(() => service.Prepare(expired, Slot), "已到期书不增加来源");
            var overflow = before with { Learning = before.Learning with { BonusSp = 65535 } };
            TestCheck.Reject<InvalidDataException>(() => service.Prepare(overflow, Slot), "额外来源溢出不消费源物");
        }
        using (var f = Make(level: 49))
            That(f.Session.UsePermanentGrowth(new(InventoryListType.Main, 75), service).Outcome == ConsumableUseOutcome.Rejected,
                "TP书遵守本版PVF的最低50级，不仅按原生ID赠点");
        Console.WriteLine($"PASS {Count - checks} 永久成长资源、来源持久化和会话事务断言");

        QuestOrdinaryCompletionTests.Fixture Make(byte level = 60, bool lost = false) => new(towns, characters, skills, growth,
            parameters, items, QuestEligibilityTests.Quest(100), materialCount: 0, loseResult: lost, active: [], configure: candidate => candidate with
            {
                Level = level, InitialLearning = SkillPointLedger.Synchronize(0, 0, level, candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(level), BonusSp = 17, BonusTp = 5 }, skills),
                Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.AddRange(Cases.Select((row, index) =>
                    new InventoryItem(0, new(InventoryListType.Main, checked((short)(65 + index))), new(row.Id, ItemKind.Consumable, 2)))) }
            });
    }
}
