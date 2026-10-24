using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class BoosterEpicExchangeMovePropagationTests
{
    public static void Run(Character current, ItemResources resources)
    {
        // 已有InventorySelfTests刚验证过的CHR实际穿戴实例，避开新增解锁槽位资格。
        var equipped = current.Inventory.Find(new(InventoryListType.Equipment, 12));
        That(equipped is { Core.Kind: ItemKind.Equipment }, "来源移动夹具使用已穿戴的真实12槽武器");
        var template = resources.Items.Get(equipped!.Core.ItemId);
        That(InventoryCargoService.AllowsInsert(equipped.Core, InventoryListType.PersonalCargo, template) &&
            InventoryCargoService.AllowsInsert(equipped.Core, InventoryListType.AccountCargo, template),
            "注入来源前明确校验原实例可进入个人及账号仓库，不放宽生产资格");
        var source = equipped with { Core = equipped.Core with { EpicExchangeOrigin = new(1, 490705251) } };
        var role = current with { Inventory = new([source]) { CargoCapacity = 8, AccountCargo = new(Capacity: 4) } };
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        InventorySlot[] stages = [new(InventoryListType.Main, 9), new(InventoryListType.Main, 10), equipped.Position,
            new(InventoryListType.Main, 9), new(InventoryListType.PersonalCargo, 0), new(InventoryListType.Main, 9),
            new(InventoryListType.AccountCargo, 0), new(InventoryListType.Main, 9)];
        foreach (var destination in stages)
        {
            var item = role.Inventory.Items.Single();
            string before = JsonSerializer.Serialize(role);
            var request = new InventoryMoveRequest(item.Position, item.Core.ItemId, 1, destination, 0, 0, -1, 0, 0);
            var moved = InventoryMoveService.Plan(role, request, resources.Items, now, resources.Contracts);
            That(moved.Accepted && moved.Plan is not null,
                "来源装备实际跨包/穿脱/个人及账号仓库移动仍合法 " + item.Position + "→" + destination);
            var result = moved.Plan!.After.Find(destination);
            That(result is not null && result.Uid == source.Uid && result.Core == source.Core &&
                result.Core.EpicExchangeOrigin == source.Core.EpicExchangeOrigin && moved.Plan.After.Items.Length == 1 &&
                moved.Plan.After.Find(item.Position) is null && JsonSerializer.Serialize(role) == before,
                "真实MoveService各路径保留同UID完整Core和来源，原位置仅移除且输入不变");
            role = role with { Inventory = moved.Plan.After };
        }
    }
}
