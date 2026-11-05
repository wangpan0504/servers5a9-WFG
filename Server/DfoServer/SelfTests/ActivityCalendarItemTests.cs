using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer;

internal static class ActivityCalendarItemTests
{
    public static async Task RunAsync(TcpTestResources resources)
    {
        int start = Count;
        const int coin = 490011740, other = 10308807;
        string root = Path.Combine(Path.GetTempPath(), "activity-calendar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var first = owners.Acquire("calendar-first", () => { });
            using var second = owners.Acquire("calendar-second", () => { });
            var a = repository.Create(first, new("CalendarA", resources.Towns.Room, resources.Towns.InitialReturn));
            var sibling = repository.Create(first, new("CalendarAlt", resources.Towns.Room, resources.Towns.InitialReturn));
            var b = repository.Create(second, new("CalendarB", resources.Towns.Room, resources.Towns.InitialReturn));
            repository.CommitInventory(first, a, new(a.Inventory, a.Inventory with
                { AccountWallet = a.Inventory.AccountWallet.SetMaterial(coin, new(500)).SetMaterial(other, new(7)) }, "fixture_calendar"));
            repository.CommitInventory(second, b, new(b.Inventory, b.Inventory with
                { AccountWallet = b.Inventory.AccountWallet.SetMaterial(coin, new(23)) }, "fixture_calendar"));
            var sessionA = new GameSession(repository, resources.Towns, first);
            var sessionB = new GameSession(repository, resources.Towns, second);
            sessionA.Select(a.Slot); sessionA.MarkReady(); sessionB.Select(b.Slot); sessionB.MarkReady();
            using var stop = new CancellationTokenSource();
            using var streamA = new MemoryStream(); using var streamB = new MemoryStream();
            List<(ushort Opcode, byte[] Body)> framesA = [], framesB = [];
            await using var senderA = new GameConnectionSender(first, streamA, stop, 0,
                (_, op, _, body, _) => framesA.Add((op, body.ToArray())));
            await using var senderB = new GameConnectionSender(second, streamB, stop, 0,
                (_, op, _, body, _) => framesB.Add((op, body.ToArray())));
            var contextA = new GameConnectionContext(new(1, IPAddress.Loopback, streamA, stop), first, sessionA, senderA);
            var contextB = new GameConnectionContext(new(2, IPAddress.Loopback, streamB, stop), second, sessionB, senderB);
            var town = new TownProjection(resources.Towns, new CharacterStatComputer(resources.Characters), null,
                resources.ItemResources.EpicPieces, items: resources.ItemResources.Items);
            var page = EndlessAdventurePackets.Page();
            EventInfoDesignPage[] pages = [page with { ShowItemIds = [coin, other, 3175] },
                page with { EventId = 30000, ShowItemIds = [coin] }];
            That(resources.ItemResources.Items.Get(coin).AccountMaterial is { CommonInventory: true } &&
                resources.ItemResources.Items.Get(other).AccountMaterial is { CommonInventory: false },
                "真实资源区分能量硬币的共通材料声明和不渝之咏的普通账号材料声明，原生商店分别读取45和44");
            await town.CalendarItemsAsync(contextA, pages);
            await town.CalendarItemsAsync(contextB, [page]);
            That(framesA is [{ Opcode: 13, Body: [44, ..], Body.Length: 126 }, { Opcode: 13, Body: [45, ..], Body.Length: 126 }] &&
                U16(framesA[0].Body, 1) == 1 && U32(framesA[0].Body, 5) == other && U32(framesA[0].Body, 9) == 7 &&
                U16(framesA[1].Body, 3) == 0 && U32(framesA[1].Body, 5) == coin && U32(framesA[1].Body, 9) == 500,
                "两个活动共享模板只占一格，按资源分入44/45各自槽序，普通实物不进入账号材料投影");
            That(framesB is [{ Opcode: 13, Body: [44, 0, 0] }, { Opcode: 13, Body: [45, ..], Body.Length: 126 }] &&
                U16(framesB[1].Body, 3) == 0 && U32(framesB[1].Body, 9) == 23,
                "同一个TownProjection服务第二连接仍独立初始化，余额和显示槽不串账号");
            framesA.Clear(); framesB.Clear();
            var change = sessionA.PlanAndCommitInventory(c => new(true, 0, new(c.Inventory, c.Inventory with
                { AccountWallet = c.Inventory.AccountWallet.SetMaterial(coin, new(507)).SetMaterial(other, new(0)) }, "fixture_calendar_change")));
            var receipt = change.Receipt!;
            await town.AccountMaterialsAsync(contextA, receipt.Character, change.Plan!.Before.AccountWallet,
                () => sessionA.CanPublishInventory(receipt));
            That(framesA.Select(row => row.Opcode).SequenceEqual(new ushort[] { 563, 1681, 14, 1460, 1461 }) && framesB.Count == 0 &&
                U16(framesA[1].Body, 3) == 0 && U32(framesA[1].Body, 5) == uint.MaxValue &&
                framesA[1].Body[0] == 44 && framesA[2].Body[0] == 45 &&
                U16(framesA[2].Body, 3) == 0 && U32(framesA[2].Body, 5) == coin && U32(framesA[2].Body, 9) == 507,
                "同事务消费另一材料及获得七币按原槽同步，563先到账、减少静默、14只播报增加，无502弹窗");
            framesA.Clear();
            await town.AccountMaterialsAsync(contextA, receipt.Character, receipt.Character.Inventory.AccountWallet);
            That(!framesA.Any(row => row.Opcode is 14 or 1681), "相同余额不再次播报获得");
            sessionA.ReturnToRoster(); sessionA.Select(sibling.Slot); sessionA.MarkReady(); framesA.Clear();
            await town.CalendarItemsAsync(contextA, pages);
            That(framesA is [{ Opcode: 13, Body: [44, 0, 0] }, { Opcode: 13, Body: [45, ..], Body.Length: 126 }] &&
                U16(framesA[1].Body, 3) == 0 && U32(framesA[1].Body, 9) == 507,
                "同账号换角按新代次全量恢复共享余额，零态前槽不挤占后一材料");
            framesA.Clear();
            await town.AccountMaterialsAsync(contextA, receipt.Character, change.Plan.Before.AccountWallet,
                () => sessionA.CanPublishInventory(receipt));
            That(framesA.Count == 0, "旧角色已提交回执无法把获得通知发给新角色");
            await town.CalendarItemsAsync(contextA, []);
            That(framesA is [{ Opcode: 13, Body: [44, 0, 0] }, { Opcode: 13, Body: [45, 0, 0] }] && framesB.Count == 0,
                "关闭最后一个活动清本连接完整显示，不触及另一连接的目录");
            framesA.Clear();
            var song = sessionA.PlanAndCommitInventory(c => new(true, 0, new(c.Inventory, c.Inventory with
                { AccountWallet = c.Inventory.AccountWallet.SetMaterial(other, new(100)) }, "fixture_song_shop")));
            contextA.ActivityCalendarItems = null;
            await town.CalendarItemsAsync(contextA, []);
            That(framesA is [{ Opcode: 13, Body: [44, ..], Body.Length: 126 }, { Opcode: 13, Body: [45, 0, 0] }] &&
                U16(framesA[0].Body, 1) == 1 && U32(framesA[0].Body, 5) == other && U32(framesA[0].Body, 9) == 100,
                "无活动登记时攻坚商店读取的list44仍投影不渝之咏持有量");
            framesA.Clear();
            var songMore = sessionA.PlanAndCommitInventory(c => new(true, 0, new(c.Inventory, c.Inventory with
                { AccountWallet = c.Inventory.AccountWallet.SetMaterial(other, new(200)) }, "fixture_song_shop_delta")));
            await town.AccountMaterialsAsync(contextA, songMore.Receipt!.Character, song.Plan!.After.AccountWallet,
                () => sessionA.CanPublishInventory(songMore.Receipt));
            That(framesA.Any(row => row.Opcode == 14 && row.Body is [44, ..] && row.Body.Length == 126 &&
                    U32(row.Body, 5) == other && U32(row.Body, 9) == 200) &&
                framesA.Any(row => row.Opcode == 1460) && framesA.Any(row => row.Opcode == 563) &&
                !framesA.Any(row => row.Body.Length > 0 && row.Body[0] == 45 && row.Opcode is 13 or 14 or 1681),
                "持有量从100增到200时14更新list44绝对余额，563与1460仍发，不把不渝之咏写入list45");
            await town.AccountMaterialsAsync(contextB, sessionB.Selected!);
            That(framesB.Any(row => row.Opcode == 13 && row.Body[0] == 45 && row.Body.Length == 126 && U32(row.Body, 9) == 23) &&
                !framesB.Any(row => row.Opcode is 14 or 502), "无before的权威恢复同步日历数量，保留另一账号23且不重放获得");
            That(repository.LoadCharacter(first, a.Slot).Inventory.Items.IsEmpty &&
                repository.LoadCharacter(second, b.Slot).Inventory.Items.IsEmpty,
                "所有显示投影均未创建普通背包实物或物理UID");
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
        Console.WriteLine($"PASS activity calendar publication {Count - start}");
    }
}
