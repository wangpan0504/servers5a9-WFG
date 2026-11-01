using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static partial class InventoryReceiptTests
{
    static async Task VerifyItemSpaceAsync(GameSession session,
        InventoryProjection projection, ItemResources items, CharacterRepository repository, AccountOwners.Lease owner, string path)
    {
        var commands = new GameCommandRegistry();
        new InventoryHandler(new InventoryService(items.Items, items.GoldLimits, items.EpicPieces), projection).Register(commands);
        commands.Seal();
        using var stop = new CancellationTokenSource();
        using var stream = new MemoryStream();
        var sent = new List<(byte Kind, ushort Opcode, byte[] Body)>();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0,
            (kind, opcode, _, body, _) => sent.Add((kind, opcode, body.ToArray())));
        var current = new GameConnectionContext(new(3, IPAddress.Loopback, stream, stop), owner, session, sender);
        var before = session.Selected!;
        string database = JsonSerializer.Serialize(repository.List(owner));
        // 任何UPDATE都会失败，仍须完成只读恢复；不能借库存提交器写同值来构造成功。
        using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            db.Open();
            using var sql = db.CreateCommand();
            sql.CommandText = "CREATE TRIGGER sync_must_not_write BEFORE UPDATE ON characters BEGIN SELECT RAISE(ABORT,'sync write'); END";
            sql.ExecuteNonQuery();
            try
            {
                foreach (byte selector in new byte[] { 255, 0, 1, 15, 128 })
                {
                    sent.Clear();
                    TestCheck.That(await commands.DispatchAsync(current, new(1, 870, [selector])) == CommandResult.Handled,
                        "真实870注册处理所有单字节请求，不把尚未核证的选择字节猜成资产或容量");
                    TestCheck.That(sent.Select(row => (row.Kind, row.Opcode)).SequenceEqual(new (byte, ushort)[]
                        { (0, 105), (0, 13), (0, 13), (0, 13), (0, 13), (0, 1449), (1, 870) }) &&
                        sent.Where(row => row.Opcode == 13).Select(row => row.Body[0]).SequenceEqual(new byte[] { 0, 1, 7, 3 }) &&
                        sent[^1].Body.SequenceEqual(new byte[] { 1 }),
                        "先完整四容器/宠物详情/克隆来源，后原生一字节成功解除等待");
                }
            }
            finally { sql.CommandText = "DROP TRIGGER sync_must_not_write"; sql.ExecuteNonQuery(); }
        }
        TestCheck.That(ReferenceEquals(before, session.Selected) && database == JsonSerializer.Serialize(repository.List(owner)),
            "恢复不更改角色引用、revision、资产或数据库");
        sent.Clear();
        foreach (byte[] malformed in new byte[][] { [], [255, 0] })
            await commands.DispatchAsync(current, new(1, 870, malformed));
        TestCheck.That(sent.Count == 0, "畸形同步请求不造成功、不发送半套容器");

        using var heldStop = new CancellationTokenSource();
        using var heldStream = new HeldOutput();
        var heldSent = new List<string>();
        await using var heldSender = new GameConnectionSender(owner, heldStream, heldStop, 0,
            (_, _, _, _, action) => heldSent.Add(action));
        var heldContext = new GameConnectionContext(new(4, IPAddress.Loopback, heldStream, heldStop), owner, session, heldSender);
        Task first = heldSender.Send(1, 0, [], "held_sync");
        await heldStream.Started.WaitAsync(TimeSpan.FromSeconds(5));
        Task publication = projection.SynchronizeItemSpaceAsync(heldContext, session.ReadInventoryForSynchronization()!);
        try
        {
            var slot = session.Selected!.Slot;
            session.ReturnToRoster();
            TestCheck.That(session.ReadInventoryForSynchronization() is null, "退出选角后迟到同步不能读取旧角色");
            await commands.DispatchAsync(current, new(1, 870, [255]));
            TestCheck.That(sent.Count == 0, "未选择角色的870不发送旧库存或成功");
            session.Select(slot);
            session.MarkReady();
        }
        finally { heldStream.Release(); }
        await Task.WhenAll(first, publication).WaitAsync(TimeSpan.FromSeconds(5));
        TestCheck.That(heldSent.SequenceEqual(["held_sync"]), "排队同步遇同角色重选时整套通知及最终ACK均失效");
    }
}
