using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ClassChangeTests
{
    internal const int Ticket = 10092795;

    public static void RunCore()
    {
        int checks = Count;
        That(ClassChangeRequests.Read([65, 0, 5, 1]) == new ClassChangeRequest(65, 5, true), "502完整请求保留槽、目标及确认位");
        That(ClassChangeRequests.Read([65, 0, 4]) == new ClassChangeRequest(65, 4, false, false), "固定目标券保留ARAD的3B变体");
        foreach (int length in new[] { 0, 1, 2, 5, 6 })
            Reject<InvalidDataException>(() => ClassChangeRequests.Read(new byte[length]), "502不吞缺字段或额外尾部");
        Reject<InvalidDataException>(() => ClassChangeRequests.Read([65, 0, 2, 2]), "确认位不是任意模式枚举");
        Bytes(ClassChangePackets.Result(0), [1], "ACK502成功无候选职业或技能尾部");
        Bytes(ClassChangePackets.Result(235), [0, 235, 0], "ACK502失败保持u8结果和完整u16错误码");
        var raw = new StackableFile(500, "stackable/class.stk", new("[waste]", [0]), 1, 0, 0, 15, null, [])
        {
            ActionDeclaration = new("[class change]", []), DeclaredMaximumLevel = 85, HasMaximumLevel = true,
            DeclaredUsableJobs = ["[swordman]"], DeclaredActionUsablePlaces = ["[seria room]", "[village]", "[dont use assult]"],
            DeclaredActionUsableServerTypes = ["[general server]", "[event server]"],
            DeclaredNeededMaterials = [1000, 2], DeclaredDontChangeGrowTypes = [0, 5], DeclaredUsableFirstGrowTypes = [1, 2]
        };
        var template = new ItemTemplate(500, ItemKind.Consumable, 0, 15, 0, 1, 0, 3, 1, "account", 1, 0, "Class", "waste");
        That(ClassChangeTicketDefinition.Parse(raw, template) is { Materials.Length: 1, FixedTarget: null } definition &&
            definition.Forbidden.Contains((0, 5)) && definition.SourceGrowTypes.SetEquals([1, 2]), "完整动作、等级、职业、材料及禁转声明共同解释");
        That(ClassChangeTicketDefinition.Parse(raw with { ActionDeclaration = new("[class change]", [4]) }, template)?.FixedTarget == 4,
            "固定目标参数不是未确认字段，不收窄已有回转券");
        foreach (var invalid in new[]
        {
            raw with { ActionDeclaration = new("[class change]", [1, 2]) }, raw with { ActionDeclaration = new("[quest clear]", []) },
            raw with { DeclaredNeededMaterials = [1000] }, raw with { DeclaredNeededMaterials = [1000, 0] },
            raw with { DeclaredDontChangeGrowTypes = [0] }, raw with { DeclaredDontChangeGrowTypes = [16, 1] },
            raw with { DeclaredUsableFirstGrowTypes = [6] }, raw with { DeclaredActionUsableServerTypes = null },
            raw with { DeclaredActionUsablePlaces = null }, raw with { HasMaximumLevel = true, DeclaredMaximumLevel = null }
        }) That(ClassChangeTicketDefinition.Parse(invalid, template) is null, "缺失、畸形或其他动作不能取得互转资格");
        Console.WriteLine($"PASS {Count - checks} 职业互转解析、完整回包及资源边界断言");
    }

    internal static NewCharacter CharacterFixture(TcpTestResources resources, byte grow = 0x11, byte level = 50)
    {
        var skills = resources.Learning;
        var growth = SelfTestCatalogs.Growth(resources.Archive, skills);
        // 此处验证合法存量券的使用，不验证按PVF活动截止日期发券；过期源另有独立拒绝用例。
        int expires = checked((int)DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
        ItemCore Book(int id) => InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(id)) with { ExpireTime = expires };
        var learning = SkillLearning.Initialize(0, skills.FreeSkills(0, grow), skills).State! with
        { Experience = growth.GetLevelStartExperience(level), BonusSp = 137, BonusTp = 11,
            ExpertContractExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() };
        learning = SkillPointLedger.Synchronize(0, grow, level, learning, skills, skills.ExpertContractLevels);
        foreach (byte context in new byte[] { 0, 1 })
        {
            var paid = SkillLearning.Plan(0, grow, level, learning, new(context, 0, [new(5, false, 1)]), skills);
            That(paid.Success, "互转fixture双树包含真实购买的SP技能");
            learning = paid.State!;
        }
        learning = learning with { Trees = learning.Trees.Select(tree => tree with
        { Skills = tree.Skills.Select(skill => skill.Id == 5 ? skill with { Command = "020304" } : skill).ToArray() }).ToArray() };
        return new("ClassHero", resources.Towns.Room, resources.Towns.InitialReturn)
        {
            Level = level, GrowType = grow, InitialLearning = learning, AcceptedStoryLevel = 1,
            Skills = learning.Trees[0].Skills.Select(skill => new CharacterSkill(skill.Id, skill.Level)).OrderBy(skill => skill.Id).ToArray(),
            DungeonState = CharacterDungeonState.NewOrdinary,
            Inventory = new(Enumerable.Range(65, 3).Select(slot => new InventoryItem(0, new(InventoryListType.Main, (short)slot),
                Book(Ticket)))
                .Append(new(0, new(InventoryListType.Main, 68), Book(10002876)))
                .ToImmutableArray())
        };
    }

    public static void RunResources(TcpTestResources resources)
    {
        int checks = Count;
        var now = DateTimeOffset.UtcNow;
        var items = resources.ItemResources;
        var service = new ClassChangeService(items.Items, resources.Characters, resources.Learning, resources.Quests, items.EpicPieces);
        var fixture = CharacterFixture(resources);
        var path = Path.Combine(Path.GetTempPath(), "class-change-" + Guid.NewGuid().ToString("N"), "game.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var repository = new CharacterRepository(path, resources.Quests);
            new AuthenticationService(new AuthenticationRepository(path), 100000).Create("classchange", NativeLoginWire.Digest);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("classchange", () => { });
            repository.Create(owner, fixture);
            Sql("INSERT INTO account_expert_contracts(account,expires_at) VALUES('classchange'," + fixture.InitialLearning.ExpertContractExpiresAt + ")");
            var before = repository.LoadCharacter(owner, 0);
            const int auraTemplateId = 99133911;
            var auraTemplate = new ItemTemplate(auraTemplateId, ItemKind.Avatar, 9, 100, 0, 1, 0, 0, 0,
                "[account]", 0, ushort.MaxValue, "转职外观资格回归");
            items.Items.PrefillTemplates(new Dictionary<int, ItemTemplate> { [auraTemplateId] = auraTemplate });
            var auraItem = new InventoryItem(0, new(InventoryListType.Equipment, 11),
                InventoryRewardGrantService.Create(auraTemplate)) { Avatar = new() };
            var auraFixture = fixture with { Name = "AuraClass", EquipmentSlotFlags = 8,
                Inventory = fixture.Inventory with { Items = fixture.Inventory.Items.Add(auraItem) } };
            var auraBefore = repository.Create(owner, auraFixture);
            var auraChange = service.Prepare(auraBefore, new(65, 2, false), now);
            That(auraChange.Plan is not null, "合法11外观模板即使声明目标职业不具备的技能，也不阻转职");
            var realAura = auraBefore with { Inventory = auraBefore.Inventory with { Items = auraBefore.Inventory.Items
                .Select(item => item.Position == auraItem.Position ? item with
                { Position = new(InventoryListType.Equipment, 9) } : item).ToImmutableArray() } };
            That(service.Prepare(realAura, new(65, 2, false), now).Reason == "class-dependent-equipment",
                "相同技能要求的真实9仍阻止不满足资格的转职");
            var auraAfter = repository.CommitClassChange(owner, auraChange.Plan!);
            That(auraAfter.GrowType == 0x12 && auraAfter.Inventory.Find(auraItem.Position)?.Core.ItemId == auraTemplateId &&
                repository.LoadCharacter(owner, auraAfter.Slot).Inventory.Find(auraItem.Position)?.Core.ItemId == auraTemplateId,
                "11外观转职事务提交并重读后仍保持原外观实例");
            That(before.Learning.ExpertContractExpiresAt == fixture.InitialLearning.ExpertContractExpiresAt &&
                before.Learning.ExpertContractExpiresAt > now.ToUnixTimeSeconds(), "互转使用真实账号契约来源，不只伪造角色镜像");
            foreach (var request in new[] { new ClassChangeRequest(65, 0, false), new(65, 6, false), new(65, 1, false),
                new(64, 2, false), new(65, 2, false, false) })
                That(service.Prepare(before, request, now).Plan is null, "零分支、未知分支、原分支、空源及普通券缺确认字段均拒绝");
            That(service.Prepare(before with { Job = 9 }, new(65, 2, false), now).Plan is null &&
                service.Prepare(before with { GrowType = 0 }, new(65, 2, false), now).Plan is null &&
                service.Prepare(before with { Level = 95 }, new(65, 2, false), now).Plan is null,
                "不可转职职业、普通券未转职源及原STK等级上限拒绝");
            var prepared = service.Prepare(before, new(65, 2, false), now);
            That(prepared.Plan is not null, "真实转职券、觉醒角色和任务资源可规划: " + prepared.Reason);
            var plan = prepared.Plan!;
            var endpoint = resources.Quests.All.First(quest => quest.RewardType == "[grow type]") with
            {
                Id = 21000, Rewards = [new(0,2,null)], Definition = new([]) { Job = "[all]", JobChange = 1,
                    TargetCharacters = [new("[swordman]",0,0)], PrerequisiteGroups = [[21001]] }
            };
            var shared = endpoint with { Id = 21001, RewardType = null, Rewards = [], Definition = new([]) { Job = "[all]" } };
            var unrelated = endpoint with { Id = 21002, Definition = endpoint.Definition! with
                { TargetCharacters = [new("[gunner]",0,0)] } };
            var scopedService = new ClassChangeService(items.Items, resources.Characters, resources.Learning,
                new(new[] { endpoint, shared, unrelated }.ToDictionary(quest => quest.Id)), items.EpicPieces);
            That(scopedService.Prepare(before, new(65, 2, false), now).Plan!.Completed.SequenceEqual([21000,21001]),
                "职业完成位按本版target character匹配基础职业，不替其他职业清任务");
            That(plan.GrowType == 0x12 && plan.Learning.Experience == before.Learning.Experience &&
                plan.Learning.ExpertContractExpiresAt == before.Learning.ExpertContractExpiresAt,
                "保留觉醒高位、实际经验和账号契约镜像");
            VerifyFree(before with { GrowType = plan.GrowType, Learning = plan.Learning }, resources.Learning);
            var source = before.Inventory.Find(new(InventoryListType.Main, 65))!;
            Character Source(ItemCore core) => before with { Inventory = before.Inventory with
                { Items = before.Inventory.Items.Select(item => item.Position == source.Position ? item with { Core = core } : item).ToImmutableArray() } };
            That(service.Prepare(Source(source.Core with { ExpireTime = 1 }), new(65, 2, false), now).Plan is null &&
                service.Prepare(Source(source.Core with { EquipmentLockId = 1 }), new(65, 2, false), now).Plan is null,
                "过期或安全锁定的原券不能消费");
            That(service.Prepare(Source(source.Core with { SortLockFlag = 1 }), new(65, 2, false), now).Plan is not null,
                "排序锁不被扩大为禁止使用");
            var paidTicket = items.Items.ClassChangeTicket(2660702)!;
            var costly = Source(InventoryRewardGrantService.Create(items.Items.Get(2660702)));
            That(paidTicket.Materials.Length > 0 && service.Prepare(costly, new(65, 2, false), now).Plan is null,
                "真实材料券材料不足时不消费源或改变职业");
            var used = costly.Inventory.Items.Select(item => item.Position).ToHashSet();
            var costs = paidTicket.Materials.Select((cost, index) =>
            {
                var template = items.Items.Get(cost.ItemId);
                var range = ItemSlotBoundService.Range(template.Kind, costly.Inventory.Expansion);
                var slot = Enumerable.Range(range.Start, range.End - range.Start + 1)
                    .Select(value => new InventorySlot(InventoryListType.Main, (short)value)).First(value => used.Add(value));
                return new InventoryItem(1000 + index, slot, InventoryRewardGrantService.Create(template, cost.Count));
            }).ToArray();
            costly = costly with { Inventory = costly.Inventory with { Items = costly.Inventory.Items.AddRange(costs) } };
            var withMaterials = service.Prepare(costly, new(65, 2, false), now);
            That(withMaterials.Plan is { } charged && charged.Inventory.After.Find(source.Position) is null &&
                costs.All(cost => charged.Inventory.After.Find(cost.Position) is null),
                "材料券保留STK全部材料费用，书与材料整批规划");
            Sql("CREATE TRIGGER class_change_late BEFORE INSERT ON character_skill_bindings BEGIN SELECT RAISE(ABORT,'class rollback'); END");
            Reject<SqliteException>(() => repository.CommitClassChange(owner, plan), "技能末段失败必须回滚已经规划的扣物");
            That(JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == JsonSerializer.Serialize(before),
                "失败回滚职业、双树、来源、物品UID、数量、任务完成位与revision");
            Sql("DROP TRIGGER class_change_late");
            var after = repository.CommitClassChange(owner, plan);
            That(after.GrowType == 0x12 && after.Revision == before.Revision + 1 && after.Inventory.Find(new(InventoryListType.Main, 65)) is null &&
                after.Inventory.Find(new(InventoryListType.Main, 66))!.Uid == before.Inventory.Find(new(InventoryListType.Main, 66))!.Uid,
                "真实事务只消费指定原实例，原位之外完整UID保留");
            Reject<InvalidOperationException>(() => repository.CommitClassChange(owner, plan), "旧revision不能再次扣券、更新职业或重建技能");
            That(service.Prepare(after, new(66, 2, false), now).Error == 3, "相同目标的重复请求不消耗下一张券");
            VerifyFree(after, resources.Learning);
            That(JsonSerializer.Serialize(new CharacterRepository(path, resources.Quests).LoadCharacter(owner, 0)) == JsonSerializer.Serialize(after),
                "仓储重建保留职业、双树、来源、任务与已扣道具");
            That(service.Prepare(after, new(68, 1, false, false), now).Plan?.GrowType == 0x11,
                "实际回归之书按固定目标接收3B并保留觉醒，不能截掉已有券形状");
            var career = resources.Quests.All.First(quest => quest.Definition?.JobChange == 1);
            var activeCareer = after with { ActiveMainQuests = [new(0, checked((ushort)career.Id), 0) { AcceptanceId = Guid.NewGuid() }] };
            That(service.Prepare(activeCareer, new(66, 3, false), now).Error == 22, "活动转职任务不因互转入口放开");
            var awakening = resources.Quests.All.First(quest => quest.Definition?.JobChange == 2);
            var activeAwakening = after with { ActiveMainQuests = [new(0, checked((ushort)awakening.Id), 0) { AcceptanceId = Guid.NewGuid() }] };
            That(service.Prepare(activeAwakening, new(66, 3, false), now).Plan is null, "活动觉醒任务不能被互转覆盖");
            Matrix(before, resources);
            var session = new GameSession(repository, resources.Towns, owner, questCatalog: resources.Quests);
            session.Select(0); session.MarkReady();
            var switched = session.ChangeClass(service, new(66, 3, false), now);
            That(switched.Error == 0 && session.CheckState(UserState.Town) && session.CanPublishClassChange(switched),
                "会话仅安装实际提交角色，本地弹窗及互转仍为原城镇状态");
            session.ReturnToRoster();
            That(!session.CanPublishClassChange(switched) && session.ChangeClass(service, new(67, 4, false), now).Error == 30,
                "返回选角后旧发布回执失效，不消费旧源或安装旧结果");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(Path.GetDirectoryName(path)!, true); }
        Console.WriteLine($"PASS {Count - checks} 职业互转、觉醒隔离及真实事务断言");

        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }

    static void Matrix(Character template, TcpTestResources resources)
    {
        var catalog = resources.Learning;
        foreach (byte stage in new byte[] { 0, 1, 2 })
        foreach (byte branch in new byte[] { 1, 2, 3, 4, 5 })
        {
            byte grow = (byte)(branch | stage << 4);
            var learning = SkillLearning.Initialize(0, catalog.FreeSkills(0, grow), catalog).State! with
            { BonusSp = 137, BonusTp = 11, Experience = template.Learning.Experience, ExpertContractExpiresAt = template.Learning.ExpertContractExpiresAt };
            learning = SkillPointLedger.Synchronize(0, grow, 95, learning, catalog, catalog.ExpertContractLevels);
            foreach (byte context in new byte[] { 0, 1 })
            {
                var paid = SkillLearning.Plan(0, grow, 95, learning, new(context, 0, [new(5, false, 1)]), catalog);
                That(paid.Success, "矩阵两树真实加点: " + grow);
                learning = paid.State!;
            }
            var before = template with { Level = 95, GrowType = grow, Learning = learning };
            foreach (byte target in new byte[] { 1, 2, 3, 4, 5 })
            {
                byte changed = (byte)(target | stage << 4);
                var after = QuestProfessionReward.Prepare(before, changed, 95, learning, catalog, DateTimeOffset.UtcNow);
                if (branch != target) VerifyFree(before with { GrowType = changed, Learning = after }, catalog);
                else That(after.Trees.Zip(learning.Trees).All(pair => pair.First.Skills.SequenceEqual(pair.Second.Skills) &&
                    pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp), "相同分支不意外洗点");
            }
            if (stage < 2)
            {
                var after = QuestProfessionReward.Prepare(before, (byte)(branch | (stage + 1) << 4), 95, learning, catalog, DateTimeOffset.UtcNow);
                That(learning.Trees.Zip(after.Trees).All(pair => pair.First.Skills.All(skill =>
                    pair.Second.Skills.Any(saved => saved.Id == skill.Id && saved.Level >= skill.Level && saved.Slot == skill.Slot))),
                    "同一分支一觉、二觉只叠加赠技，保留付费等级及槽");
            }
        }
    }

    static void VerifyFree(Character character, SkillLearningCatalog catalog)
    {
        var free = SkillPointLedger.FreeBaseline(character.Job, character.GrowType, character.Level, catalog,
            catalog.ActiveExpertContractLevels(character.Learning.ExpertContractExpiresAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        That(character.Learning.Trees.All(tree => tree.Sp == catalog.TotalSp(character.Level) + 137 && tree.Tp == catalog.TotalTp(character.Level) + 11 &&
            tree.Skills.Count == free.Count && tree.Skills.All(skill => free.GetValueOrDefault(skill.Id) == skill.Level && skill.Command.Length == 0) &&
            tree.Combos.Count == 0), "互转双树仅保留目标免费基线，完整返还已用SP/TP并清旧指令/连招");
    }
}
