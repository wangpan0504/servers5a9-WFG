using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class TitleChangeTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    const string SampleMain = """
        [title change item id]
        10007724
        [success rate]
        10000
        [resource title item]
        400330031 0 400330032 0
        [/resource title item]
        [final title item id]
        400330033 400330034
        [/final title item id]
        [/title change item id]

        [title change item id]
        10007725
        [success rate]
        0
        [resource title item]
        400330031 0 400330032 0 400330033 0 400330034 0
        [/resource title item]
        [final title item id]
        [/final title item id]
        [/title change item id]

        [title change item id]
        10015205
        [success rate]
        0
        [resource title item]
        2676151 5000
        [/resource title item]
        [final title item id]
        2676154
        [/final title item id]
        [/title change item id]
        """;

    const string SampleSub = """
        [resource title item id]
        400330031
        [success title item]
        400330032 98 400330033 1 400330034 1
        [/success title item]
        [fail title item]
        400330033 100
        [/fail title item]
        [/resource title item id]

        [resource title item id]
        400330032
        [success title item]
        400330031 98 400330033 1 400330034 1
        [/success title item]
        [fail title item]
        400330034 100
        [/fail title item]
        [/resource title item id]

        [resource title item id]
        400330033
        [success title item]
        400330034 100
        [/success title item]
        [fail title item]
        400330034 100
        [/fail title item]
        [/resource title item id]

        [resource title item id]
        400330034
        [success title item]
        400330034 100
        [/success title item]
        [fail title item]
        400330033 100
        [/fail title item]
        [/resource title item id]

        [resource title item id]
        2676151
        [success title item]
        2676152 100
        [/success title item]
        [fail title item]
        2676151 100
        [/fail title item]
        [/resource title item id]
        """;

    public static void RunCore()
    {
        That(TitleChangeRequests.Read([65, 0, 9, 0]) == new TitleChangeRequest(65, 9),
            "CMD970严格解析i16变更道具槽与i16目标称号槽");
        foreach (byte[] malformed in new byte[][] { [], [65, 0], [65, 0, 9, 0, 0], [0xFF, 0xFF, 9, 0], [65, 0, 0xFF, 0xFF], [9, 0, 9, 0] })
            Reject<InvalidDataException>(() => TitleChangeRequests.Read(malformed),
                "CMD970拒绝非法长度、负槽位与同槽请求");

        That(TitleChangePackets.Ack(InventoryResult.Reject(19), default).SequenceEqual(new byte[] { 0, 19, 0 }) &&
            TitleChangePackets.Ack(InventoryResult.Reject(0xD5), default).SequenceEqual(new byte[] { 0, 0xD5, 0 }) &&
            TitleChangePackets.Ack(new(true, 0), new(true, 400330033, 10007724)).SequenceEqual(
                new byte[] { 1, 1, 0x31, 0x8D, 0xDC, 0x17, 0xAC, 0xB4, 0x98, 0x00 }) &&
            TitleChangePackets.Ack(new(true, 0), new(false, 400330034, 10007725)).SequenceEqual(
                new byte[] { 1, 0, 0x32, 0x8D, 0xDC, 0x17, 0xAD, 0xB4, 0x98, 0x00 }),
            "ACK970失败回3字节错误码，成功回10字节(result+isSuccess+resultTitleId+usedThreadId)");

        var catalog = new TitleChangeCatalog(SampleMain, SampleSub);
        That(catalog.TryGetMain(10007724, out var normalThread) && normalThread.SuccessRate == 10_000 &&
            normalThread.FinalTitleItemIds.SetEquals([400330033, 400330034]) &&
            normalThread.AllowsSealState(1) && !normalThread.AllowsSealState(0) &&
            catalog.TryGetMain(10007725, out var gorgeousThread) && gorgeousThread.SuccessRate == 0 &&
            gorgeousThread.FinalTitleItemIds.Count == 0 &&
            catalog.TryGetMain(10015205, out var unsealedThread) && unsealedThread.AllowsSealState(0) && !unsealedThread.AllowsSealState(1),
            "称号变更表解析成功率、毕业列表与10015205未封装特例");
        foreach (var (name, main, sub) in new[]
        {
            ("空主表", "", SampleSub),
            ("空子表", SampleMain, ""),
            ("越界成功率", SampleMain.Replace("10000", "10001"), SampleSub),
            ("主表引用缺失子表", SampleMain.Replace("2676151 5000", "2676999 5000"), SampleSub),
            ("毕业与输入重叠", SampleMain.Replace("400330033 400330034", "400330031"), SampleSub)
        })
            Reject<InvalidDataException>(() => new TitleChangeCatalog(main, sub), "称号变更表拒绝" + name);

        var items = CreateCatalog();
        int fixedRoll = 98;
        var service = new InventoryTitleChangeService(items, catalog, nextRandom: _ => fixedRoll);
        var originalTitle = new InventoryItem(10, new(InventoryListType.Main, 9), new(400330031, ItemKind.Equipment, 999_999_998)
        {
            SealFlag = 1,
            Attr = 12,
            Durability = 30,
            EnchantCardId = 5001,
            EmblemSocketCount = 2,
            Emblem1 = 300,
            Emblem2 = 301,
            Random0 = new(1, 2, 3),
            TradeRestriction = 1
        });
        var threadStack = new InventoryItem(11, new(InventoryListType.Main, 65), new(10007724, ItemKind.Consumable, 2));
        var character = new Character(1, 0, "TitleTester", 1, new(1, 0, 1, 1), null)
        {
            Level = 85,
            Inventory = new([originalTitle, threadStack], Gold: 50_000)
        };

        var result = service.Apply(character, new(65, 9), out var outcome, Now);
        var after = result.Plan!.After;
        var changedTitle = after.Find(new(InventoryListType.Main, 9))!;
        That(result.Accepted && outcome == new TitleChangeOutcome(true, 400330033, 10007724) &&
            changedTitle.Uid == originalTitle.Uid && changedTitle.Core.ItemId == 400330033 &&
            changedTitle.Core with { ItemId = originalTitle.Core.ItemId } == originalTitle.Core &&
            after.Find(new(InventoryListType.Main, 65))!.Core.Count == 1 &&
            result.Plan.UsedItems is [{ ItemId: 10007724, Count: 1 }],
            "绣线按100%成功分支权重抽样替换哥特称号ID，保留UID、封装、附魔、徽章与品级并扣1根绣线");

        fixedRoll = 0;
        var swapped = service.Apply(character, new(65, 9), out var swapOutcome, Now);
        That(swapped.Accepted && swapOutcome == new TitleChangeOutcome(true, 400330032, 10007724) &&
            swapped.Plan!.After.Find(new(InventoryListType.Main, 9))!.Core.ItemId == 400330032,
            "绣线前98权重将哥特迷情洗为哥特绮梦");

        var afterFinal = character with { Inventory = after };
        That(!service.Apply(afterFinal, new(65, 9), out _, Now).Accepted,
            "普通绣线遇到final title item id毕业称号(400330033/400330034)时拒绝继续洗练");

        var gorgeousStack = new InventoryItem(12, new(InventoryListType.Main, 66), new(10007725, ItemKind.Consumable, 2));
        var withGorgeous = character with
        {
            Inventory = new([changedTitle, gorgeousStack], Gold: 50_000)
        };
        var gorgeousResult = service.Apply(withGorgeous, new(66, 9), out var gorgeousOutcome, Now);
        That(gorgeousResult.Accepted && gorgeousOutcome == new TitleChangeOutcome(false, 400330034, 10007725) &&
            gorgeousResult.Plan!.After.Find(new(InventoryListType.Main, 9))!.Core.ItemId == 400330034 &&
            gorgeousResult.Plan.After.Find(new(InventoryListType.Main, 66))!.Core.Count == 1,
            "华丽的绣线按0%成功率走fail分支，可将哥特迷情(白昼)400330033洗为哥特绮梦(黑夜)400330034");

        var unsealedCharacter = character with
        {
            Inventory = new([originalTitle with { Core = originalTitle.Core with { SealFlag = 0 } }, threadStack], Gold: 50_000)
        };
        That(service.Apply(unsealedCharacter, new(65, 9), out _, Now).Error == 19,
            "非10015205变更道具拒绝未封装称号");

        var lockedCharacter = character with
        {
            Inventory = new([originalTitle with { Core = originalTitle.Core with { EquipmentLockId = 1 } }, threadStack], Gold: 50_000)
        };
        That(service.Apply(lockedCharacter, new(65, 9), out _, Now).Error == 0xD5,
            "装备锁定的称号按0xD5拒绝");

        var unsealedTicket = new InventoryItem(13, new(InventoryListType.Main, 67), new(10015205, ItemKind.Consumable, 1));
        var unsealedTarget = originalTitle with { Core = originalTitle.Core with { ItemId = 2676151, SealFlag = 0 } };
        var poorCharacter = character with { Inventory = new([unsealedTarget, unsealedTicket], Gold: 4_999) };
        var richCharacter = character with { Inventory = new([unsealedTarget, unsealedTicket], Gold: 5_000) };
        That(service.Apply(poorCharacter, new(67, 9), out _, Now).Error == 4 &&
            service.Apply(richCharacter with { Inventory = new([unsealedTarget with { Core = unsealedTarget.Core with { SealFlag = 1 } }, unsealedTicket], Gold: 5_000) },
                new(67, 9), out _, Now).Error == 19 &&
            service.Apply(richCharacter, new(67, 9), out var paidOutcome, Now) is { Accepted: true, Plan.After.Gold: 0 } &&
            paidOutcome == new TitleChangeOutcome(false, 2676151, 10015205),
            "10015205仅允许未封装称号并校验金币费用，金币不足返回错误码4");
    }

    public static void RunResources(PvfArchive pvf, ItemResources resources)
    {
        var titles = resources.TitleChanges;
        That(titles.TryGetMain(10005036, out _) &&
            titles.TryGetMain(10007724, out var normal) && normal.SuccessRate == 10_000 &&
            normal.ResourceGoldCosts.ContainsKey(400330031) && normal.ResourceGoldCosts.ContainsKey(400330032) &&
            normal.FinalTitleItemIds.SetEquals([400330033, 400330034]) &&
            titles.TryGetMain(10007725, out var gorgeous) && gorgeous.SuccessRate == 0 &&
            gorgeous.ResourceGoldCosts.Keys.OrderBy(id => id).SequenceEqual([400330031, 400330032, 400330033, 400330034]) &&
            titles.TryGetSub(400330031, out var sub31) && sub31.FailPool is [{ TitleItemId: 400330033, Weight: 100 }] &&
            titles.TryGetSub(400330032, out var sub32) && sub32.FailPool is [{ TitleItemId: 400330034, Weight: 100 }],
            "真实PVF称号变更表包含绣线(10007724)、华丽的绣线(10007725)及哥特称号(400330031..400330034)映射");
        foreach (int id in new[] { 400330031, 400330032, 400330033, 400330034 })
            That(resources.Items.Get(id) is { Kind: ItemKind.Equipment, EquipmentSlot: 13 },
                "真实PVF哥特称号模板均为部位13称号装备: " + id);
        foreach (int id in new[] { 490702204, 490009242, 490004179, 490701640 })
        {
            var riding = resources.Items.ActionConsumable(id);
            That(riding is { Label: "[riding]", Parameters: [107000902, 300000], CoolTime: 10000 } &&
                riding.AllowedDungeons is { Count: > 0 },
                "真实PVF厄伽勒变身药水按[riding] 107000902 300000注册动作消费并保留可用副本集合: " + id);
        }
    }

    static ItemCatalog CreateCatalog()
    {
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [10007724] = new(10007724, ItemKind.Consumable, 0, 1, 0, 1000, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "绣线"),
            [10007725] = new(10007725, ItemKind.Consumable, 0, 1, 0, 1000, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "华丽的绣线"),
            [10015205] = new(10015205, ItemKind.Consumable, 0, 1, 0, 1000, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "称号变更券"),
            [400330031] = new(400330031, ItemKind.Equipment, 13, 1, 30, 1, 0, 2, 1, "[sealing]", ushort.MaxValue, 0, "哥特迷情"),
            [400330032] = new(400330032, ItemKind.Equipment, 13, 1, 30, 1, 0, 2, 1, "[sealing]", ushort.MaxValue, 0, "哥特绮梦"),
            [400330033] = new(400330033, ItemKind.Equipment, 13, 1, 30, 1, 0, 2, 1, "[sealing]", ushort.MaxValue, 0, "哥特迷情 (白昼)"),
            [400330034] = new(400330034, ItemKind.Equipment, 13, 1, 30, 1, 0, 2, 1, "[sealing]", ushort.MaxValue, 0, "哥特绮梦 (黑夜)"),
            [2676151] = new(2676151, ItemKind.Equipment, 13, 1, 30, 1, 0, 2, 1, "[free]", ushort.MaxValue, 0, "测试称号1"),
            [2676152] = new(2676152, ItemKind.Equipment, 13, 1, 30, 1, 0, 2, 1, "[free]", ushort.MaxValue, 0, "测试称号2"),
            [2676154] = new(2676154, ItemKind.Equipment, 13, 1, 30, 1, 0, 2, 1, "[free]", ushort.MaxValue, 0, "测试称号4")
        });
        return catalog;
    }
}
