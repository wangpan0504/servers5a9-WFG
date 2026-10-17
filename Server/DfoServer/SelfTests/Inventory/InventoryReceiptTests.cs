using DfoServer.Infrastructure;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class InventoryReceiptTests
{
    public static async Task RunAsync(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = TestCheck.Count;
        var computer = new CharacterStatComputer(characters);
        string root = Path.Combine(Path.GetTempPath(), "S5A9-receipts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("receipts", () => { });
            var weapon = characters.Get(0).Equipment.Single(item => item.Slot == 12).Template;
            var character = repository.Create(owner, new("ReceiptCase", towns.Room, towns.InitialReturn));
            var session = new GameSession(repository, towns, owner);
            session.Select(character.Slot);
            session.MarkReady();
            InventoryResult Grant(Character current) => new(true, 0, new(current.Inventory,
                current.Inventory with { Items = current.Inventory.Items.Add(new(0, new(InventoryListType.Main, 9), InventoryRewardGrantService.Create(weapon))) }, "receipt_grant"));
            var result = session.PlanAndCommitInventory(Grant);
            var receipt = result.Receipt!;
            TestCheck.That(result.Plan!.After.Items.Single().Uid == 0 && receipt.Character.Inventory.Items.Single().Uid > 0,
                "发布回执使用真实 UID，保留规划临时 UID");
            TestCheck.That(ReferenceEquals(session.Selected, receipt.Character) && session.CanPublishInventory(receipt) &&
                receipt.Generation == owner.Generation && receipt.SelectionEpoch == session.SelectionEpoch, "回执绑定已安装快照与原会话");
            TestCheck.That(JsonSerializer.Serialize(receipt.Character) == JsonSerializer.Serialize(repository.List(owner).Single()), "回执与独立读库一致");
            TestCheck.That(session.MutateInventory(_ => InventoryResult.Reject()).Receipt is null, "拒绝无提交回执");

            var saved = session.Selected;
            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_receipt BEFORE UPDATE ON characters BEGIN SELECT RAISE(ABORT,'receipt failure'); END";
                command.ExecuteNonQuery();
                bool rejected = false;
                try { session.MutateInventory(c => new(true, 0, new(c.Inventory, c.Inventory with { Gold = 1 }, "fault"))); }
                catch (SqliteException) { rejected = true; }
                TestCheck.That(rejected && ReferenceEquals(saved, session.Selected) && repository.List(owner).Single().Inventory.Gold == 0,
                    "事务失败不安装快照、不返回成功回执");
                command.CommandText = "DROP TRIGGER fail_receipt";
                command.ExecuteNonQuery();
            }

            using var stop = new CancellationTokenSource();
            using var stream = new HeldOutput();
            var sent = new List<string>();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, action) => sent.Add(action));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            var projection = new InventoryProjection(computer, new TownProjection(towns, computer, null, itemResources.EpicPieces), itemResources.EpicPieces);
            // 首帧已经开始写，库存通知排队后换角；出队时必须再次验证原 epoch。
            Task first = sender.Send(1, 0, [], "held");
            await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
            Task publication = projection.RefreshAsync(context, result);
            try
            {
                session.ReturnToRoster();
                session.Select(character.Slot);
                session.MarkReady();
                TestCheck.That(!session.CanPublishInventory(receipt), "同角色重新选择也使旧回执过期");
            }
            finally { stream.Release(); }
            await Task.WhenAll(first, publication).WaitAsync(TimeSpan.FromSeconds(5));
            TestCheck.That(sent.SequenceEqual(["held"]), "排队期间过期的库存通知不发布");

            var next = session.MutateInventory(c => new(true, 0, new(c.Inventory, c.Inventory with { Gold = 2 }, "receipt_gold")));
            await projection.RefreshAsync(context, next);
            TestCheck.That(sent.Contains("inventory_committed_delta"), "当前提交正常发布差量");
            session.MutateInventory(c => new(true, 0, new(c.Inventory, c.Inventory with { Gold = 3 }, "receipt_newer")));
            TestCheck.That(!session.CanPublishInventory(next.Receipt!), "新 revision 使旧提交回执失效");
            var actual = session.Selected!;
            var currentReceipt = new InventoryCommitReceipt(actual, owner.Generation, session.SelectionEpoch);
            TestCheck.That(session.CanPublishInventory(currentReceipt), "当前完整快照可发布装备与面板");
            session.UpdateProgress(new(CharacterProgressField.Level, owner.Account, actual.Name, 2, 0));
            TestCheck.That(session.Selected!.Revision == actual.Revision && !ReferenceEquals(session.Selected, actual) &&
                !session.CanPublishInventory(currentReceipt), "同revision GM替换等级不能让旧穿戴Player属性覆盖新快照");
            using (var heldStop = new CancellationTokenSource())
            using (var heldStream = new HeldOutput())
            {
                var rejectionSent = new List<string>();
                await using var heldSender = new GameConnectionSender(owner, heldStream, heldStop, 0,
                    (_, _, _, _, action) => rejectionSent.Add(action));
                var heldContext = new GameConnectionContext(new(2, IPAddress.Loopback, heldStream, heldStop),
                    owner, session, heldSender);
                Task held = heldSender.Send(1, 0, [], "held_aura_rejection");
                await heldStream.Started.WaitAsync(TimeSpan.FromSeconds(5));
                var rejectionGuard = QuestTicketHandler.CaptureAuraRejectionGuard(heldContext);
                Task rejected = heldContext.Reply(CmdPacketType.USE_STACKABLE_ACTION, [0, 0, 0, 0, 0, 0, 0],
                    "aura_rejected", rejectionGuard);
                try
                {
                    var previous = session.Selected!;
                    session.UpdateProgress(new(CharacterProgressField.Level, owner.Account, previous.Name, 3, 0));
                    TestCheck.That(!ReferenceEquals(session.Selected, previous) && !rejectionGuard(),
                        "同owner GM替换Selected快照后，旧127拒绝的选择守卫失效");
                }
                finally { heldStream.Release(); }
                await Task.WhenAll(held, rejected).WaitAsync(TimeSpan.FromSeconds(5));
                TestCheck.That(rejectionSent.SequenceEqual(["held_aura_rejection"]),
                    "排队中的旧127失败ACK不发送给同owner新快照");
            }
            using var replacement = owners.Acquire(owner.Account, () => { });
            bool expired = false;
            try { session.CanPublishInventory(next.Receipt!); }
            catch (InvalidOperationException) { expired = true; }
            TestCheck.That(expired, "旧 generation 不能复用回执发布");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"PASS {TestCheck.Count - checks} inventory receipt/publication assertions");
    }

    sealed class HeldOutput : MemoryStream
    {
        readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => started.Task;
        public void Release() => release.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
