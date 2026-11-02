using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassUndefeatedSongBoosterResourceTests
{
    internal static void Run(TcpTestResources resources)
    {
        const int itemId = BattlePassUndefeatedSongSource.ItemId;
        var items = resources.ItemResources;
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(8));
        var material = items.Items.Get(itemId).AccountMaterial ?? throw new InvalidDataException("匹配资源缺少不渝之咏账号材料定义");
        var box = new InventoryItem(1, new(InventoryListType.Main, 65),
            InventoryRewardGrantService.Create(items.Items.Get(10306817), 1, now));
        var character = new Character(1, 0, "SongBox", 0, resources.Towns.Room, resources.Towns.InitialReturn)
            { Inventory = new([box]) };
        var service = new InventoryBoosterService(items.Items, items.Boosters, items.GoldLimits, items.EpicPieces);
        var opened = service.Open(character, new(65, 1), out var receipt, now);
        That(opened.Accepted && opened.Plan is { } && receipt!.Rewards.SequenceEqual([new BoosterGrantedItem(itemId, 150)]) &&
            opened.Plan.After.AccountWallet.Material(itemId).Balance == 150 &&
            opened.Plan.After.Items.All(item => item.Core.ItemId != itemId) &&
            BattlePassUndefeatedSongBoosterSource.Prepare(character.Id, character.Revision, opened.Plan) is { Amount: 150 },
            "真实150材料礼盒扣源并写入账号余额，任务来源以实际取得150计数");
        var full = character with { Inventory = character.Inventory with
            { AccountWallet = character.Inventory.AccountWallet.SetMaterial(itemId, new AccountMaterialBalance(material.HoldingLimit)) } };
        That(!service.Open(full, new(65, 1), out _, now).Accepted && full.Inventory.Find(box.Position) == box,
            "持有上限不足时原盒不消费、不生成任务来源");
    }
}
