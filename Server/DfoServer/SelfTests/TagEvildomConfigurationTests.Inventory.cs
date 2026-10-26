using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class TagEvildomConfigurationTests
{
    static void InventoryReadiness()
    {
        const int now = 1700000000;
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [1] = new(1, ItemKind.Equipment, 20, 1, 20, 1, 0, 0, 0, "[free]", 1, 0, "永久装备"),
            [2] = new ItemTemplate(2, ItemKind.Equipment, 20, 1, 20, 1, 0, 0, 0, "[free]", 1, 0, "限期装备") { Expiration = new(Days: 2) }
        });
        var worn = new InventoryItem(1, new(InventoryListType.Equipment, 20), new(1, ItemKind.Equipment, 1) { ExpireTime = now + 10 });
        foreach (var item in new[]
        {
            worn,
            worn with { Core = worn.Core with { IsRental = true } },
            worn with { Core = new(1, ItemKind.Avatar, 1), Avatar = new() { ExpireTime = now + 10 } },
            worn with { Core = new(1, ItemKind.Creature, 1), Creature = new() { ExpireTime = now + 10 } },
            worn with { Position = new(InventoryListType.Talisman, 210), Core = new(1, ItemKind.Rune, 1) { ExpireTime = now + 10 } }
        })
        {
            var inventory = new InventorySnapshot([item]);
            var character = new Character(1, 0, "TagExpiry", 1, new(62, 0, 1, 1), null) { Inventory = inventory };
            var stamp = TagEvildomAppearance.Capture(character, DateTimeOffset.FromUnixTimeSeconds(now));
            That(TagEvildomInventory.IsCurrent(inventory, catalog, now) && stamp.Matches(character, DateTimeOffset.FromUnixTimeSeconds(now + 9)),
                "真实穿戴期限未到允许，不按秒重建相对时间字段");
            That(!stamp.Matches(character, DateTimeOffset.FromUnixTimeSeconds(now + 10)) && !TagEvildomInventory.IsCurrent(inventory, catalog, now + 10),
                "普通装备、租赁、时装、宠物和已装Rune到期后重捕获仍拒旧资产，须普通登录维护");
            That(inventory.Items[0] == item, "就绪检查不删资产、不改期限");
        }
        var zero = worn with { Core = new(2, ItemKind.Equipment, 1) };
        That(!TagEvildomInventory.IsCurrent(new([zero]), catalog, now) &&
            TagEvildomInventory.IsCurrent(InventoryExpiryService.RepairZeroPeriod(new([zero]), catalog).Plan!.After, catalog, now),
            "存量限期模板零期限须真实选角修复；按同规则修复后恢复就绪");
        That(TagEvildomInventory.IsCurrent(new([worn with { Core = new(1, ItemKind.Equipment, 1) }]), catalog, now),
            "真正永久装备零期限合法，不扩大拒绝");
        foreach (var slot in new InventorySlot[] { new(InventoryListType.Main, 9), new(InventoryListType.Equipment, 30), new(InventoryListType.Talisman, 0) })
            That(TagEvildomInventory.IsCurrent(new([worn with { Position = slot, Core = worn.Core with { ExpireTime = now - 1 } }]), catalog, now),
                "无关背包/未装Rune不参与1394；NameTag自带到期过滤，不要求额外维护");
    }
}
