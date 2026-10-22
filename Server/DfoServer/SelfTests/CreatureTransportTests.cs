using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CreatureTransportTests
{
    public static void RunCore()
    {
        That(CreatureTransportRequests.TryRead(Convert.FromHexString("520010000102000000"), out var request) &&
            request == new CreatureTransportRequest(82, 16, 1, 2), "557实收9B保留源、Pet目标、模式及原名册行号");
        That(!CreatureTransportRequests.TryRead(new byte[8], out _) && !CreatureTransportRequests.TryRead(new byte[10], out _),
            "跨界请求必须完整且不接受多余尾部");
        var book = CreatureChangeTests.Book;
        var raw = CreatureChangeTests.Raw with { ActionDeclaration = new("[creature transport stone]", [4012]),
            HasActionUsablePlace = true, DeclaredActionUsablePlaces = ["[seria room]"], DeclaredNeededMaterials = [999,9] };
        var file = new RecoveryConsumableFile(book.Id, "stackable/book.stk", raw, 1000, null, null, null, ["cool time"], []);
        var definition = ActionConsumableDefinition.Parse(file, book)!;
        That(definition is { Label: "[creature transport stone]", Parameters: [4012], CoolTime: 1000 } &&
            !definition.AllowsPlace("[village]"), "宠物跨界按动作解析，赛丽亚地点来自PVF，need material不成为消费成本");
        var clock = new ConsumableUseSessionTests.FakeClock();
        var source = new InventoryItem(77, new(InventoryListType.Main, 82), new(book.Id, ItemKind.Consumable, 2));
        var pet = new InventoryItem(78, new(InventoryListType.Pet, 16), new(7001, ItemKind.Creature, 78)
            { EnchantCardId = 1234, Extension27 = 99, TradeRestriction = 1 })
            { Creature = new() { Name = "Keep", Satiety = 41, ExpireTime = (int)clock.GetUtcNow().ToUnixTimeSeconds()+100 } };
        var character = new Character(1, 0, "Sender", 1, new(1,0,1,1), null) { Inventory = new([source, pet]) };
        var recipient = new CharacterRosterEntry(2, 7, "Receiver", 0, 0, 1, []);
        ItemTemplate Item(int id) => id == book.Id ? book : new(id, ItemKind.Creature, 26, 1, 0, 1, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "pet")
            { CreatureSubType = 0 };
        var service = new CreatureTransportService(Item, _ => definition, CreatureSystemTests.Synthetic(), null!, clock);
        That(service.Plan(character, recipient, request, "[seria room]", out var plan) == 0 &&
            plan!.Pet == pet && plan.Inventory.After.Find(pet.Position) is null &&
            plan.Inventory.After.Find(source.Position)!.Core.Count == 1 && plan.Inventory.After.ConsumableStates.Length == 1 &&
            character.Inventory.Find(pet.Position) == pet && !plan.Inventory.CharacterPrecondition!.Matches(character, pet.ExpireTime),
            "绑定及有限期成宠可专用跨界，规划保留原实例并同笔扣石头和冷却，最早到期时不可提交");
        That(service.Plan(character, recipient, request, "[village]", out _) == 21 &&
            service.Plan(character, recipient with { Id = character.Id }, request, "[seria room]", out _) == 7 &&
            service.Plan(character, recipient, request with { Mode = 0 }, "[seria room]", out _) == 7,
            "错误地点、自身及非宠物跨界模式不消费");
        foreach (var invalid in new[] { pet with { Position = new(InventoryListType.Equipment,26) },
            pet with { Core = pet.Core with { EquipmentLockId = 1 } }, pet with { Core = pet.Core with { IsRental = true } },
            pet with { Creature = pet.Creature! with { ExpireTime = (int)clock.GetUtcNow().ToUnixTimeSeconds() } } })
            That(service.Plan(character with { Inventory = new([source,invalid]) }, recipient, request, "[seria room]", out _) != 0,
                "穿戴、锁定、租赁和到期宠物不能被跨界请求取走");
        var committed = character with { Inventory = plan!.Inventory.After };
        Bytes(CreatureTransportPackets.Ack(request, new(true,0) { Receipt = new(committed,1,1) }),
            [1,82,0,16,0,1,0,0,0,1], "557成功完整10B，第三个u16为实际剩余量，末项mode1");
        Bytes(CreatureTransportPackets.Ack(request, InventoryResult.Reject(17)), [0,17,0], "557失败使用已确认的u16不存在错误码");
        Console.WriteLine("PASS 宠物跨界动作、资格、完整实例与557字节合同断言");
    }
}
