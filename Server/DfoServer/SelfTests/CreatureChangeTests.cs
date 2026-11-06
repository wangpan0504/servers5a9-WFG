using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Creatures;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CreatureChangeTests
{
    internal static ItemTemplate Book { get; } = new(9000, ItemKind.Consumable, 0, 1, 0, 100, 0, 0, 0, "[trade]", 65535, 0, "book");
    internal static StackableFile Raw { get; } = new(9000, "stackable/book.stk", new("[etc]", [0]), 100, 0, 0, 1, null, [])
    {
        ActionDeclaration = new("[change creature]", [-1,-1,2,0]), DeclaredActionLimitIndexes = [7001],
        DeclaredActionResultLimitIndexes = [0,7002,10000], DeclaredNeededMaterials = [], DeclaredActionUsablePlaces = ["[village]"]
    };

    public static void RunCore()
    {
        var definition = CreatureChangeDefinition.Parse(Raw, Book)!;
        var potential = CreatureChangeDefinition.Parse(Raw with { ActionDeclaration = new("[change creature random]", [7001,7001,1,2,2,3]),
            DeclaredActionLimitIndexes = [], DeclaredActionResultLimitIndexes = [], DeclaredActionUsablePlaces = null }, Book)!;
        That(potential is { Random:true, TownOnly:false, TotalWeight:5 } && potential.Select(1) == 7001 && potential.Select(2) == 7002 &&
            potential.AllowedItems.SetEquals([7001,7002]), "潜能药读取连续产物和逐项权重，允许同族再次随机，不生成六种以外模板");
        That(CreatureChangeDefinition.Parse(Raw with { ActionDeclaration = new("[change creature random]", [7001,7001,1,3,2,3]),
            DeclaredActionLimitIndexes = [], DeclaredActionResultLimitIndexes = [], DeclaredActionUsablePlaces = null }, Book) is null,
            "潜能药候选数量必须与完整权重表一致");
        That(definition is { SourceItemId: -1, CreatureMinimumLevel: 1, TotalWeight: 10000 } &&
            definition.Select(0) == 7002 && definition.Select(9999) == 7002, "单个转换结果的10000是相对权重，不留失败概率");
        var weighted = CreatureChangeDefinition.Parse(Raw with { DeclaredActionResultLimitIndexes = [0,7002,2,0,7001,3,0,7002,1] }, Book)!;
        That(Enumerable.Range(0,6).Select(value => weighted.Select(value)).SequenceEqual(new[] { 7002,7002,7001,7001,7001,7002 }),
            "相对权重保留候选顺序与重复项，精确覆盖区间边界");
        var wide = weighted with { Candidates = [new(7002,int.MaxValue),new(7001,int.MaxValue)] };
        That(wide.TotalWeight == 2L * int.MaxValue && wide.Select(int.MaxValue) == 7001, "总权重及随机区间不截断为i32");
        var zero = CreatureChangeDefinition.Parse(Raw with { DeclaredActionResultLimitIndexes = [0,7001,0,0,7002,1] }, Book)!;
        That(zero.Candidates.Length == 2 && zero.Select(0) == 7002, "零权重保留原声明但不占随机区间，整个池须有正权重");
        Reject<ArgumentOutOfRangeException>(() => weighted.Select(-1), "负随机值拒绝");
        Reject<ArgumentOutOfRangeException>(() => weighted.Select(6), "权重上边界拒绝");
        foreach (var raw in new[]
        {
            Raw with { DeclaredActionLimitIndexes = [] }, Raw with { DeclaredActionLimitIndexes = null },
            Raw with { DeclaredActionResultLimitIndexes = [0,7002] }, Raw with { DeclaredActionResultLimitIndexes = [1,7002,1] },
            Raw with { DeclaredActionResultLimitIndexes = [0,7002,0] }, Raw with { DeclaredActionResultLimitIndexes = [0,7002,-1] },
            Raw with { ActionDeclaration = new("[change creature]", [-1,0,1,15]) },
            Raw with { ActionDeclaration = new("[change creature]", [-1,-1,2]) },
            Raw with { HasMaximumLevel = true }, Raw with { HasCreatureMinimumLevel = true },
            Raw with { HasUsableJobs = true }
        }) That(CreatureChangeDefinition.Parse(raw, Book) is null, "畸形声明和未确认参数变体不猜测成功语义");
        That(CreatureChangeDefinition.Parse(Raw with { HasActionUsablePlace = true, DeclaredActionUsablePlaces = [] }, Book) is { TownOnly: false } &&
            CreatureChangeDefinition.Parse(Raw with { DeclaredActionUsablePlaces = ["[unknown]"] }, Book) is { TownOnly: true },
            "空地点不限制，未知地点按参考端回退城镇");
        var fixedDefinition = CreatureChangeDefinition.Parse(Raw with { ActionDeclaration = new("[change creature]", [7001,7002,1,-1]),
            DeclaredActionLimitIndexes = [], DeclaredActionResultLimitIndexes = [] }, Book);
        That(fixedDefinition is { SourceItemId: 7001 } && fixedDefinition.Select(0) == 7002,
            "固定转换读取动作前两参数，与宠物最低等级独立");
        var unrestricted = CreatureChangeDefinition.Parse(Raw with { ActionDeclaration = new("[change creature]", [7001,7002,1,-1]),
            DeclaredActionLimitIndexes = [], DeclaredActionResultLimitIndexes = [], DeclaredActionUsablePlaces = null }, Book);
        That(unrestricted is { TownOnly: false, SourceItemId: 7001, MakePermanent: true },
            "固定转换未声明地点时不凭空要求城镇标签，末项-1保留永久转换意图");
        That(CreatureChangeDefinition.Parse(Raw with { DeclaredMinimumLevel = null }, Book) is { MinimumLevel: 1 } &&
            CreatureChangeDefinition.Parse(Raw with { HasMinimumLevel = true, DeclaredMinimumLevel = null }, Book) is null,
            "缺省最低等级按1级，显式畸形等级仍拒绝");
        That(CreatureChangeDefinition.Parse(Raw with { DeclaredActionUsablePlaces = ["[village]", "[seria room]"] }, Book)
            is { TownOnly: true }, "城镇并列赛丽亚房间仍按城镇限制，不误拒绝等价声明");
        var standardUse = new ActionConsumableDefinition(Book, "[change creature]", [-1,-1,2,0],0,-9000,0,0,["[village]"]);

        var pet = new InventoryItem(11, new(InventoryListType.Pet, 8), new(7001, ItemKind.Creature, 11)
        {
            EnchantCardId = 1234, EnchantUpgrade = 2, Extension27 = 41, Extension114 = new(1,12), SortLockFlag = 1
        }) { Creature = new() { Level = 2, Experience = 3, Name = "pet", Satiety = 78, ExpireTime = 2000000000 } };
        var book = new InventoryItem(12, new(InventoryListType.Main, 74), new(9000, ItemKind.Consumable, 2) { SortLockFlag = 1 });
        Character character = new(1,0,"Change",1,new(1,0,1,1),null) { Inventory = new([pet,book]) };
        ItemTemplate Item(int id) => id == 9000 ? Book : new(id, ItemKind.Creature, 26, 1, 0, 1, 0, 0, 0, "[trade]", 65535, 0, "pet") { CreatureSubType = 0 };
        int draws = 0;
        var service = new CreatureChangeService(Item, _ => definition, CreatureSystemTests.Synthetic(),
            new ConsumableUseSessionTests.FakeClock(), total => { draws++; return total-1; }, actionItems: _ => standardUse);
        var result = service.Plan(character, book.Position, pet.Position);
        var potentialUse = new ActionConsumableDefinition(Book, "[change creature random]", [7001,7001,1,2,2,3],1000,-9000,0,0,[]);
        var potentialService = new CreatureChangeService(Item, _ => potential, CreatureSystemTests.Synthetic(),
            new ConsumableUseSessionTests.FakeClock(), _ => 4, actionItems: _ => potentialUse);
        var potentialResult = potentialService.Plan(character, book.Position, pet.Position);
        That(potentialResult.Accepted && potentialResult.Plan!.After.Find(pet.Position) == pet with { Core = pet.Core with { ItemId = 7002 } } &&
            potentialResult.Plan.After.Find(book.Position)!.Core.Count == 1 &&
            !potentialService.Plan(character with { Inventory = potentialResult.Plan.After }, book.Position, pet.Position).Accepted,
            "潜能随机、扣源和冷却同一计划，UID、附魔、名字及原期限不丢失，连续请求不能重抽");
        That(result.Accepted && result.Plan!.After.Find(book.Position)!.Core.Count == 1 && draws == 1 &&
            result.Plan.After.Find(pet.Position) == pet with { Core = pet.Core with { ItemId = 7002 } } &&
            character.Inventory.Find(pet.Position) == pet && character.Inventory.Find(book.Position) == book,
            "转换只换原UID模板，完整附魔、扩展、详情及期限保留，源扣一本且不改输入快照");
        var last = character with { Inventory = character.Inventory with { Items = [pet,book with { Core = book.Core with { Value = 1 } }] } };
        That(service.Plan(last, book.Position, pet.Position).Plan!.After.Find(book.Position) is null, "最后一本转换书耗尽清槽");
        foreach (var invalid in new[]
        {
            pet with { Core = pet.Core with { EquipmentLockId = 1 } }, pet with { Core = pet.Core with { Value = 99 } },
            pet with { Creature = pet.Creature! with { ExpireTime = 1800000000 } },
            pet with { Core = pet.Core with { ItemId = 7002 } }
        }) That(!service.Plan(character with { Inventory = new([invalid,book]) }, book.Position, pet.Position).Accepted,
            "锁定、详情失配、过期或不在源白名单的目标拒绝且不消费");
        That(!service.Plan(character, book.Position, CreatureRules.EquippedSlot).Accepted &&
            !service.Plan(character, new(InventoryListType.Pet,74), pet.Position).Accepted, "不得借目标槽转换穿戴宠物或伪造源容器");
        foreach (var invalid in new[]
        {
            book with { Uid = 0 }, book with { Core = book.Core with { EquipmentLockId = 1 } },
            book with { Core = book.Core with { Value = 0 } }, book with { Core = book.Core with { ExpireTime = 1800000000 } }
        }) That(!service.Plan(character with { Inventory = new([pet,invalid]) }, book.Position, pet.Position).Accepted,
            "源书缺UID、锁定、数量无效或过期均拒绝");
        foreach (var restriction in new[] { definition with { MinimumLevel = 2 }, definition with { CreatureMinimumLevel = 3 } })
            That(!new CreatureChangeService(Item, _ => restriction, CreatureSystemTests.Synthetic())
                .Plan(character, book.Position, pet.Position).Accepted, "角色与宠物最低等级独立校验");
        That(new CreatureChangeService(id => id == 9000 ? Book with { Jobs = 2 } : Item(id), _ => definition,
            CreatureSystemTests.Synthetic(), new ConsumableUseSessionTests.FakeClock(),
            actionItems: _ => standardUse with { Item = Book with { Jobs = 2 } }).Plan(character, book.Position, pet.Position).Accepted,
            "宠物转换源STK不重复校验客户端usable job限制");
        foreach (var forbiddenBook in new[] { Book with { RequiredSkill = 123 }, Book with { UseForbidden = true } })
            That(!new CreatureChangeService(id => id == 9000 ? forbiddenBook : Item(id), _ => definition,
                CreatureSystemTests.Synthetic(), new ConsumableUseSessionTests.FakeClock(),
                actionItems: _ => standardUse with { Item = forbiddenBook }).Plan(character, book.Position, pet.Position).Accepted,
                "所需技能及使用禁令不能因进入515而绕过");
        That(!new CreatureChangeService(id => Item(id) with { CreatureSubType = 1 }, _ => definition,
            CreatureSystemTests.Synthetic()).Plan(character, book.Position, pet.Position).Accepted, "宠物蛋不参加原UID成宠转换");
        var missing = new CreatureChangeService(Item, _ => definition with { Candidates = [new(7002,1),new(9999,1)] },
            CreatureSystemTests.Synthetic(), random: _ => throw new Exception("不应抽取"));
        That(!missing.Plan(character, book.Position, pet.Position).Accepted, "完整候选核对先于随机，不删除缺失候选改变概率");
        var exchangeBook = CreatureChangeDefinition.Parse(Raw with { DeclaredNeededMaterials = [9000,1] }, Book)!;
        var fromShop = new CreatureChangeService(Item, _ => exchangeBook,
            CreatureSystemTests.Synthetic(), new ConsumableUseSessionTests.FakeClock(), _ => 0, actionItems: _ => standardUse);
        That(fromShop.Plan(character, book.Position, pet.Position).Plan!.After.Find(book.Position)!.Core.Count == 1 &&
            fromShop.Plan(last, book.Position, pet.Position).Plan!.After.Find(book.Position) is null,
            "兑换材料即使与源同ID也不重复扣除，最后一本仍可正常使用");
        var precondition = result.Plan!.CharacterPrecondition!;
        That(precondition.Matches(character,1999999999) && !precondition.Matches(character,2000000000) &&
            !precondition.Matches(character with { Revision = character.Revision + 1 },1900000000), "提交复验实际到期时刻与角色revision");
        var unchangedTarget = new CreatureChangeService(Item, _ => definition with { Candidates = [new(7001,1)] },
            CreatureSystemTests.Synthetic(), new ConsumableUseSessionTests.FakeClock(), _ => 0, actionItems: _ => standardUse)
            .Plan(character, book.Position, pet.Position);
        That(unchangedTarget.Accepted && !unchangedTarget.Plan!.CharacterPrecondition!.Matches(character,2000000000),
            "随机结果同原模板时仍消耗书，并复验没有变化的目标期限");
        var clock = new ConsumableUseSessionTests.FakeClock();
        int timedDraws = 0;
        var timed = new CreatureChangeService(Item, _ => unrestricted, CreatureSystemTests.Synthetic(), clock,
            _ => { timedDraws++; return 0; }, actionItems: _ => standardUse with { CoolTime = 1000, Places = [] });
        var timedResult = timed.Plan(character, book.Position, pet.Position, false);
        That(timedResult.Accepted && timedResult.Plan!.After.ConsumableStates is [{ Kind: ConsumableStateKind.Cooldown }],
            "缺省地点的固定转换允许合法图内规划，冷却与源扣耗同一快照");
        That(timedResult.Plan!.After.Find(pet.Position) == pet with { Core = pet.Core with { ItemId = 7002, ExpireTime = 0 },
            Creature = pet.Creature with { ExpireTime = 0 } },
            "永久转换清除核心及宠物详情期限，保留UID、名字、等级、经验与附魔");
        var nextPet = pet with { Uid = 13, Position = new(InventoryListType.Pet,9), Core = pet.Core with { Value = 13 } };
        var afterTimed = character with { Inventory = timedResult.Plan!.After with { Items = timedResult.Plan.After.Items.Add(nextPet) } };
        That(!timed.Plan(afterTimed, book.Position, nextPet.Position, false).Accepted && timedDraws == 1,
            "冷却期间对另一合法目标拒绝且不消耗源、不重抽");
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        That(timed.Plan(afterTimed, book.Position, nextPet.Position, false).Accepted && timedDraws == 2,
            "独立冷却到期后恢复转换，不能永久拦截源");
        That(service.Plan(character, book.Position, pet.Position, false).Accepted,
            "资产规划不再按旧TownOnly重复拦截，真实地点由统一Session门禁负责");
        That(StackableActionRequests.TryUse(Convert.FromHexString("4A000000000000400000000700000008000000"), out var request) &&
            request == new StackableActionUseRequest(74,0,0,64,7,8), "实收19B转换请求解析为源74、目标Pet槽8，不当作五个i32");
        Bytes(StackableActionPackets.Used(request), [1,74,0,0,64,0,0,0], "成功515仅8B，不追加私有目标字段");
        Bytes(StackableActionPackets.Rejected(request), [0,0,0,74,0,0,64], "失败515的动作只占一字节");
    }

    public static void RunResources(DfoServer.Infrastructure.ItemResources resources)
    {
        foreach (int id in new[] { 2683295,10004867,10005432,10006000,10088295,10093337,10093339,10093466,10093467,
            10098010,10098011,10098012,10098013,10098014,10098015,10146456,10151798,490000173,490004004 })
        {
            var definition = resources.Items.CreatureChange(id);
            That(definition is { SourceItemId: > 0, Random: false, TownOnly: false, Candidates.Length: 1 },
                $"真实固定转换{id}缺省地点定义完整接通");
            That(resources.Items.ActionConsumable(id) is { Label: "[change creature]" },
                $"真实固定转换{id}接入统一动作冷却消费定义");
        }
    }
}
