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
            Raw with { HasUsableJobs = true }, Raw with { DeclaredNeededMaterials = [100,0] },
            Raw with { DeclaredNeededMaterials = null }, Raw with { DeclaredActionUsablePlaces = null }
        }) That(CreatureChangeDefinition.Parse(raw, Book) is null, "畸形声明和未确认参数变体不猜测成功语义");
        var fixedDefinition = CreatureChangeDefinition.Parse(Raw with { ActionDeclaration = new("[change creature]", [7001,7002,1,-1]),
            DeclaredActionLimitIndexes = [], DeclaredActionResultLimitIndexes = [] }, Book);
        That(fixedDefinition is { SourceItemId: 7001 } && fixedDefinition.Select(0) == 7002,
            "固定转换读取动作前两参数，与宠物最低等级独立");

        var pet = new InventoryItem(11, new(InventoryListType.Pet, 8), new(7001, ItemKind.Creature, 11)
        {
            EnchantCardId = 1234, EnchantUpgrade = 2, Extension27 = 41, Extension114 = new(1,12), SortLockFlag = 1
        }) { Creature = new() { Level = 2, Experience = 3, Name = "pet", Satiety = 78, ExpireTime = 2000000000 } };
        var book = new InventoryItem(12, new(InventoryListType.Main, 74), new(9000, ItemKind.Consumable, 2) { SortLockFlag = 1 });
        Character character = new(1,0,"Change",1,new(1,0,1,1),null) { Inventory = new([pet,book]) };
        var pieces = new EpicPieceCatalog("[piece list]\n1 2\n[/piece list]");
        ItemTemplate Item(int id) => id == 9000 ? Book : new(id, ItemKind.Creature, 26, 1, 0, 1, 0, 0, 0, "[trade]", 65535, 0, "pet") { CreatureSubType = 0 };
        int draws = 0;
        var service = new CreatureChangeService(Item, _ => definition, CreatureSystemTests.Synthetic(), pieces,
            new ConsumableUseSessionTests.FakeClock(), total => { draws++; return total-1; });
        var result = service.Plan(character, book.Position, pet.Position);
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
            That(!new CreatureChangeService(Item, _ => restriction, CreatureSystemTests.Synthetic(), pieces)
                .Plan(character, book.Position, pet.Position).Accepted, "角色与宠物最低等级独立校验");
        foreach (var forbiddenBook in new[] { Book with { Jobs = 2 }, Book with { RequiredSkill = 123 }, Book with { UseForbidden = true } })
            That(!new CreatureChangeService(id => id == 9000 ? forbiddenBook : Item(id), _ => definition,
                CreatureSystemTests.Synthetic(), pieces).Plan(character, book.Position, pet.Position).Accepted,
                "职业、所需技能及使用禁令不能因进入515而绕过");
        That(!new CreatureChangeService(id => Item(id) with { CreatureSubType = 1 }, _ => definition,
            CreatureSystemTests.Synthetic(), pieces).Plan(character, book.Position, pet.Position).Accepted, "宠物蛋不参加原UID成宠转换");
        var missing = new CreatureChangeService(Item, _ => definition with { Candidates = [new(7002,1),new(9999,1)] },
            CreatureSystemTests.Synthetic(), pieces, random: _ => throw new Exception("不应抽取"));
        That(!missing.Plan(character, book.Position, pet.Position).Accepted, "完整候选核对先于随机，不删除缺失候选改变概率");
        var withMaterials = new CreatureChangeService(Item, _ => definition with { Materials = [new(9000,1)] },
            CreatureSystemTests.Synthetic(), pieces, new ConsumableUseSessionTests.FakeClock(), _ => 0);
        That(withMaterials.Plan(character, book.Position, pet.Position).Plan!.After.Find(book.Position) is null &&
            !withMaterials.Plan(last, book.Position, pet.Position).Accepted, "源与材料同ID先扣书再足额扣材料，缺材料整笔拒绝");
        var precondition = result.Plan!.CharacterPrecondition!;
        That(precondition.Matches(character,1999999999) && !precondition.Matches(character,2000000000) &&
            !precondition.Matches(character with { Revision = character.Revision + 1 },1900000000), "提交复验实际到期时刻与角色revision");
        var unchangedTarget = new CreatureChangeService(Item, _ => definition with { Candidates = [new(7001,1)] },
            CreatureSystemTests.Synthetic(), pieces, new ConsumableUseSessionTests.FakeClock(), _ => 0)
            .Plan(character, book.Position, pet.Position);
        That(unchangedTarget.Accepted && !unchangedTarget.Plan!.CharacterPrecondition!.Matches(character,2000000000),
            "随机结果同原模板时仍消耗书，并复验没有变化的目标期限");
        That(QuestTicketRequests.TryUse(Convert.FromHexString("4A000000000000400000000700000008000000"), out var request) &&
            request == new QuestTicketUseRequest(74,0,0,64,7,8), "实收19B转换请求解析为源74、目标Pet槽8，不当作五个i32");
        Bytes(QuestTicketPackets.Used(request), [1,74,0,0,64,0,0,0], "成功515仅8B，不追加私有目标字段");
        Bytes(QuestTicketPackets.Rejected(request), [0,0,0,74,0,0,64], "失败515的动作只占一字节");
    }
}
