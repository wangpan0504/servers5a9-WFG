using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using PvfLib;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CreatureEggTests
{
    public static void RunCore()
    {
        foreach (byte slot in new byte[] { 0, 1, 139 })
            That(CreatureEggRequests.Read([7, slot, 0]) == new InventorySlot(InventoryListType.Pet, slot),
                "102双槽抓包按list:u8和slot:u16LE读取，限定合法宠物页范围");
        foreach (byte[] body in new byte[][] { [], [7, 0], [7, 0, 0, 0], [0, 0, 0], [3, 26, 0], [7, 140, 0], [7, 0, 1] })
            Reject<InvalidDataException>(() => CreatureEggRequests.Read(body), "孵化正文宽度、容器和槽位无效时完整拒绝");
        Bytes(CreatureEggPackets.Ack(new(true, 0)), [1], "ACK102成功没有源槽、模板或其它未读取字段");
        Bytes(CreatureEggPackets.Ack(InventoryResult.Reject(0x1234)), [0, 0x34, 0x12], "ACK102失败保留公共u16宽度，不宣称细分文案");
        var egg = new ItemTemplate(7001, ItemKind.Creature, 26, 1, 0, 1, 0, 1, 1, "[sealing trade]", ushort.MaxValue, 0, "蛋");
        var output = egg with { Id = 7002, Name = "宠物", AttachType = "[trade]" };
        const string eggText = "[equipment type]\n`[creature]` 0\n[sub type]\n1\n[creature species]\n20\n[output index]\n7002";
        const string outputText = "[equipment type]\n`[creature]` 0\n[sub type]\n0\n[creature species]\n20";
        var definition = CreatureEggCatalog.Read(new(eggText), new(outputText), egg, output);
        That(definition == new CreatureEggDefinition(7001, 7002, 20) { DefaultName = "宠物" }, "孵化定义来自蛋subtype1与单一output index，目标为单一output index指定的subtype0");
        foreach (string invalid in new[] { eggText.Replace("7002", "7001"), eggText.Replace("\n1\n", "\n0\n"),
            eggText + "\n[output index]\n7002", eggText.Replace("7002", "7002 7003"), eggText.Replace("`[creature]` 0", "`[creature]` 1") })
            That(CreatureEggCatalog.Read(new(invalid), new(outputText), egg, output) is null,
                "自指、非蛋、重复或多产物及未知类型参数不能被普通孵化默默补齐");
        var crossSpecies = CreatureEggCatalog.Read(new(eggText), new(outputText.Replace("\n20", "\n21")), egg, output);
        That(crossSpecies == definition, "显式产物允许源与目标各自合法species不同，关系仍保存源species");
        foreach (string invalid in new[] { outputText.Replace("\n20", "\n-1"), outputText + "\n[creature species]\n20",
            outputText.Replace("\n20", "\n20 21"), outputText.Replace("[creature species]", "[unknown species]") })
            That(CreatureEggCatalog.Read(new(eggText), new(invalid), egg, output) is null,
                "产物species必须存在且为单一非负整数，不因允许跨species而放行无效产物");
        That(CreatureEggCatalog.Read(new(eggText.Replace("\n20", "\n-1")), new(outputText), egg, output) is null &&
            CreatureEggCatalog.Read(new(eggText), new(outputText), egg, output with { Kind = ItemKind.Equipment }) is null,
            "源负species及非Creature产物拒绝，不生成裸普通核心");
        var position = new InventorySlot(InventoryListType.Pet, 0);
        var detail = new CreatureDetail { Name = "试验", Satiety = 73, Experience = 55, Level = 2, TailFlag = 1, ExpireTime = 2000 };
        var core = new ItemCore(7001, ItemKind.Creature, 63) { SealFlag = 1, Attr = 3, Marker16 = 77, SortLockFlag = 1, Extension27 = 123 };
        Character character = new(1, 0, "Egg", 5, new(1, 0, 10, 10), null)
        { Inventory = new([new InventoryItem(63, position, core) { Creature = detail }]) };
        var result = InventoryCreatureEggService.Plan(character, position, 1000, definition!, egg);
        var hatched = result.Plan!.After.Find(position)!;
        var source = character.Inventory.Find(position)!;
        var unnamed = source with { Creature = source.Creature! with { Name = "" } };
        var namedHatch = InventoryCreatureEggService.Plan(character with { Inventory = new([unnamed]) }, position, 1000, definition!, egg).Plan!.After.Find(position)!;
        That(namedHatch.Creature!.Name == "宠物" && namedHatch.Uid == source.Uid,
            "旧空名宠物蛋孵化补目标默认名，不要求额外使用改名卡");
        That(result.Accepted && result.Plan.CharacterPrecondition!.Matches(character, 1000) && result.Plan.ChangedSlots.SequenceEqual(new[] { position }) &&
            hatched == new InventoryItem(63, position, core with { ItemId = 7002, SealFlag = 0 }) { Creature = detail } &&
            character.Inventory.Find(position)!.Core.ItemId == 7001,
            "孵化只改变同一完整实例的模板和目标普通宠物类型，不分配临时UID、扣数量或改源快照");
        That(hatched.Creature == detail && hatched.ExpireTime == 2000 && hatched.Core.Value == 63 && hatched.Core.SortLockFlag == 1,
            "完整名字、经验、等级、饱食、UID、排序锁及原实例期限均保留，不从无期限目标重置永久");
        That(result.Plan.CharacterPrecondition!.Matches(character, 1999) && !result.Plan.CharacterPrecondition.Matches(character, 2000),
            "提交时间复验绑定detail实际截止，不因Core期限0漏掉跨到期的旧规划");
        var growingDefinition = CreatureEggCatalog.Read(new(eggText), new(outputText), egg, output with { CreatureInitialMode = 1 });
        var growing = InventoryCreatureEggService.Plan(character, position, 1000, growingDefinition!, egg).Plan!.After.Find(position)!;
        That(growing.Creature == detail with { Mode = 1, Mode1Field0 = 10, Mode1Field1 = 5 } && growing.Uid == hatched.Uid && growing.Core == hatched.Core,
            "道具成长产物在孵化时即初始化模式和亲密度，保留其它详情，不等穿戴后再修正");
        That(InventoryCreatureEggService.Plan(character, position, 1000, crossSpecies!, egg).Plan!.After.Find(position) == hatched,
            "精确跨species关系按相同产物转换，完整实例与同species孵化保持一致");
        That(!InventoryCreatureEggService.Plan(character with { Inventory = result.Plan.After }, position, 1000, definition!, egg).Accepted,
            "相同请求再次检查真实源；已孵化目标不能作为蛋重复转换");
        foreach (var invalid in new[] { character with { Inventory = InventorySnapshot.Empty },
            character with { Inventory = new([new InventoryItem(0, position, core) { Creature = detail }]) },
            character with { Inventory = new([new InventoryItem(63, position, core with { Value = 64 }) { Creature = detail }]) },
            character with { Inventory = new([new InventoryItem(63, position, core with { EquipmentLockId = 1 }) { Creature = detail }]) },
            character with { Inventory = new([new InventoryItem(63, position, core) { Creature = detail with { ExpireTime = 1000 } }]) },
            character with { Inventory = new([new InventoryItem(63, position, core)]) } })
            That(!InventoryCreatureEggService.Plan(invalid, position, 1000, definition!, egg).Accepted,
                "空槽、临时UID、UID核心不符、安全锁、精确到期及缺详情不取得孵化资格");
        foreach (var invalid in new[] { egg with { Id = 7003 }, egg with { UseForbidden = true } })
            That(!InventoryCreatureEggService.Plan(character, position, 1000, definition!, invalid).Accepted,
                "蛋原模板身份及禁用资格不能在转换模板时被绕过");
        That(!InventoryCreatureEggService.Plan(character, new(InventoryListType.Main, 0), 1000, definition!, egg).Accepted &&
            !result.Plan.CharacterPrecondition!.Matches(character with { Revision = 6 }), "错误容器及旧角色revision拒绝，不能重绑旧计划");
    }

    public static void RunResources(CreatureEggCatalog eggs, ItemCatalog items)
    {
        var definition = eggs.Get(63022);
        That(definition is { EggItemId: 63022, OutputItemId: 63023, Species: 20, DefaultName.Length: > 0 } &&
            definition.DefaultName == items.Get(63023).CreatureName && eggs.Get(63023) is null,
            "匹配波比蛋63022单向输出63023，目标subtype0不可再次孵化");
        var crossSpecies = eggs.Get(2681358);
        That(crossSpecies is { EggItemId: 2681358, OutputItemId: 2681359, Species: 87, DefaultName.Length: > 0 } &&
            crossSpecies.DefaultName == items.Get(2681359).CreatureName && eggs.Get(2681359) is null,
            "黑炭头蛋按明确output index孵化为成年宠物，源87与目标88不必相同");
        That(items.Get(2681358) is { Kind: ItemKind.Creature, EquipmentSlot: 26 } &&
            items.Get(2681359) is { Kind: ItemKind.Creature, EquipmentSlot: 26 }, "跨species样本双方仍须为合法Creature模板");
        That(items.Get(63022) is { Kind: ItemKind.Creature, EquipmentSlot: 26 } &&
            items.Get(63023) is { Kind: ItemKind.Creature, EquipmentSlot: 26 }, "样本来自equipment Creature而非宽泛STK消耗品");
    }
}
