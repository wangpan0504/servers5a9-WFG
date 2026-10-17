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

internal static class CreatureGroupTests
{
    public static void RunCore()
    {
        int before = Count;
        var raw = CreatureChangeTests.Raw with { ActionDeclaration = new("[creature growth]", [0,1,1,2,3]),
            DeclaredActionUsablePlaces = null, HasActionUsablePlace = false };
        var book = CreatureGrowthItemDefinition.Parse(raw, CreatureChangeTests.Book)!;
        That(book is { ViewMode: 0, GroupId: 1, TownOnly: false } && book.Types.SequenceEqual(new[] { 1,2,3 }),
            "STK保留界面模式、分组与转换类型优先级，缺省地点不收窄为城镇");
        foreach (var action in new int[][] { [3,1], [5,1,4], [0,0,1], [0,1,0], [0,1,5] })
            That(CreatureGrowthItemDefinition.Parse(raw with { ActionDeclaration = new("[creature growth]", action) }, CreatureChangeTests.Book) is null,
                "缺失分组和未知界面或转换类型拒绝");
        var table = new CreatureGroupFile([new(1,
        [
            new(1,9000, [[30,7001,7002]]), new(2,9000, [[50,7001,7002,7001]]),
            new(3,9000, [[10,7001,7002,7001,2000,8000]]), new(4,9000, [[1,7001,7002]])
        ])]);
        var groups = new CreatureGroupCatalog(table);
        var fixedRule = groups.Resolve(book,7001,0)!;
        That(fixedRule.ConsumeCount == 30 && fixedRule.Select(0) == 7002 && groups.Resolve(book,7001,1) is null,
            "先匹配STK中的固定类型；成本30不是宠物等级，不因选择参数或材料不足回退后续类型");
        var selection = book with { ViewMode = 3, Types = [4] };
        That(groups.Resolve(selection,7001,2)?.Select(0) == 7002 && groups.Resolve(selection,7002,1)?.Select(0) == 7001 &&
            groups.Resolve(selection,7001,0) is null && groups.Resolve(selection,7001,3) is null && groups.Resolve(selection,9999,1) is null,
            "类型4在同一型号行内双向选择，使用1基选项，不接受其他行或越界选项");
        That(groups.Resolve(book with { Types = [2] },7001,1)?.Select(0) == 7002 &&
            groups.Resolve(book with { Types = [2] },7001,2) is null && groups.Resolve(book with { Types = [2] },7002,1) is null,
            "类型2源是首项、结果是后续项，不把结果当源，也不能选择当前模板");
        var random = book with { Types = [3] };
        var randomRule = groups.Resolve(random,7002,0)!;
        That(randomRule.Select(0) == 7002 && randomRule.Select(1999) == 7002 && randomRule.Select(2000) == 7001 &&
            randomRule.Select(9999) == 7001 && groups.Resolve(random,7002,1) is null,
            "类型3包含源及结果作为可转换源，保留万分权重边界，客户端不能指定随机结果");
        That(groups.Resolve(book with { GroupId = 2 },7001,0) is null &&
            groups.Resolve(book with { ItemId = 9001 },7001,0) is null &&
            new CreatureGroupCatalog(new CreatureGroupFile([])).Resolve(book,7001,0) is null,
            "分组、类型和实际道具共同匹配，可选表缺失不能借用别的配方");
        foreach (var row in new[]
        {
            new CreatureGroupConversion(1,9000,[[1,7001,7002,7003]]), new CreatureGroupConversion(3,9000,[[1,7001,7002,1]]),
            new CreatureGroupConversion(3,9000,[[1,7001,7002,7001,2000,7999]]), new CreatureGroupConversion(4,9000,[[0,7001,7002]]),
            new CreatureGroupConversion(4,9000,[[1,7001,-1]])
        }) Reject<InvalidDataException>(() => new CreatureGroupCatalog(new CreatureGroupFile([new(1,[row])])).Resolve(book,7001,0),
            "转换表畸形成本、源结果数量和概率拒绝，不能过滤后补默认规则");

        ItemTemplate Item(int id) => id == 9000 ? CreatureChangeTests.Book : new(id,ItemKind.Creature,26,1,0,1,0,0,0,"[trade]",65535,0,"pet") { CreatureSubType = 0 };
        var pet = new InventoryItem(71,new(InventoryListType.Pet,8),new(7001,ItemKind.Creature,71)
            { EnchantCardId = 1234, EnchantUpgrade = 2, Extension27 = 99 }) { Creature = new() { Level = 1, Name = "keep" } };
        var source = new InventoryItem(72,new(InventoryListType.Main,65),new(9000,ItemKind.Consumable,29));
        Character character = new(1,0,"Group",1,new(1,0,1,1),null) { Inventory = new([pet,source]) };
        var pieces = new EpicPieceCatalog("[piece list]\n1 2\n[/piece list]");
        var service = new CreatureChangeService(Item,_=>null,CreatureSystemTests.Synthetic(),pieces, random:_=>0,
            groups:groups,growthItems:_=>book);
        That(!service.PlanGroup(character,source.Position,pet.Position,0,true).Accepted, "29个不能被普通丢弃的数量归一规则当作足额30个");
        source = source with { Core = source.Core with { Value = 30 } };
        character = character with { Inventory = new([pet,source]) };
        var result = service.PlanGroup(character,source.Position,pet.Position,0,false);
        That(result.Accepted && result.Plan!.After.Find(source.Position) is null &&
            result.Plan.After.Find(pet.Position) == pet with { Core = pet.Core with { ItemId = 7002 } },
            "一级宠物也可按30个成本转换；默认地点允许正式副本，保留完整UID附魔及详情");
        var limited = new CreatureChangeService(Item,_=>null,CreatureSystemTests.Synthetic(),pieces, groups:groups,
            growthItems:_=>book with { TownOnly = true });
        That(!limited.PlanGroup(character,source.Position,pet.Position,0,false).Accepted,
            "资源显式仅城镇时仍按地点门禁拒绝");
        var same = new CreatureChangeService(Item,_=>null,CreatureSystemTests.Synthetic(),pieces, groups:groups,growthItems:_=>selection)
            .PlanGroup(character,source.Position,pet.Position,1,true);
        That(!same.Accepted && same.Plan is null && character.Inventory.Find(source.Position)!.Core.Count == 30,
            "原生选择窗排除当前属性，重复选择当前模板不消费源道具");
        That(QuestTicketRequests.TryUse(Convert.FromHexString("410000000000006B0000000800000002000000"),out var request) &&
            request == new QuestTicketUseRequest(65,0,0,107,8,2), "107请求是Pet槽及1基选项，不借用64的目标容器参数");
        Bytes(QuestTicketPackets.Used(request),[1,65,0,0,107,0,0,0],"107成功8B且无私有尾部，客户端不自行扣源");
        Console.WriteLine($"PASS {Count-before} 宠物转换组、资源优先级、消耗及选择协议断言");
    }
}
