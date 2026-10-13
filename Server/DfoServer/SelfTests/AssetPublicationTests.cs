using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using PvfLib;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static class AssetPublicationTests
{
    public static async Task RunAsync(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int before = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-asset-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CommittedFailureAsync(towns, characters, itemResources, root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine($"PASS {TestCheck.Count - before} asset commit and publication assertions");
    }

    private static async Task CommittedFailureAsync(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources, string root)
    {
        string path = Path.Combine(root, "game.db");
        var repository = new CharacterRepository(path);
        var owners = new AccountOwners();
        using var owner = owners.Acquire("publication", () => { });
        var commands = new GameCommandRegistry();
        var projection = new TownProjection(towns, new CharacterStatComputer(characters), null, itemResources.EpicPieces);
        using var log = new ServerLog(root);
        new CharacterHandler(characters, projection, log, itemResources.GoldLimits).Register(commands);
        commands.Seal();
        const string name = "PubFailure";
        byte[] nameBytes = CharacterName.Encoding.GetBytes(name);
        byte[] create = new byte[17 + nameBytes.Length];
        create[0] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(create.AsSpan(1), nameBytes.Length);
        nameBytes.CopyTo(create, 5);
        create[11 + nameBytes.Length] = 255;
        var request = new Packet(1, (ushort)CmdPacketType.CREATE_CHARACTER, create);
        int publications = 0;
        using var failedStop = new CancellationTokenSource();
        using var failedStream = new FailedOutput(() =>
        {
            // 在实际 ACK 写入时打开另一条 SQLite 连接，证明提交先于发送。
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = "SELECT count(*) FROM character_inventory_items";
            TestCheck.That((long)query.ExecuteScalar()! == 5, "ACK write observes five already committed assets");
        });
        await using var failedSender = new GameConnectionSender(owner, failedStream, failedStop, 0,
            (_, _, _, _, _) => publications++);
        var failedSession = new GameSession(repository, towns, owner);
        var failedContext = new GameConnectionContext(new(1, IPAddress.Loopback, failedStream, failedStop), owner, failedSession, failedSender);
        await RejectAsync<IOException>(() => commands.DispatchAsync(failedContext, request), "committed create propagates ACK write failure");
        TestCheck.That(failedStream.Writes == 1 && publications == 0 && failedStop.IsCancellationRequested,
            "failed ACK cancels connection without success publication or roster send");
        Character committed = new CharacterRepository(path).List(owner).Single();
        DfoServer.Game.Inventory.Models.InventoryItem[] instances = committed.Inventory.Items.ToArray();
        TestCheck.That(instances.Length == 5 && instances.Select(item => item.Uid).Distinct().Count() == 5,
            "failed send retains five distinct durable instance identities");

        // 使用未取消的旧发送泵，独立验证 generation 拦截，而非依赖失败连接的取消状态。
        using var staleStop = new CancellationTokenSource();
        using var staleBytes = new MemoryStream();
        await using var staleSender = new GameConnectionSender(owner, staleBytes, staleStop, 0,
            (_, _, _, _, _) => publications++);
        using var fresh = owners.Acquire("publication", () => { });
        var staleContext = new GameConnectionContext(new(1, IPAddress.Loopback, staleBytes, staleStop), owner, failedSession, staleSender);
        await RejectAsync<InvalidOperationException>(() => commands.DispatchAsync(staleContext, request),
            "old owner cannot retry committed creation");
        await RejectAsync<InvalidOperationException>(() => staleSender.Send(0, (ushort)NotiPacketType.USERINFO, [2], "stale_assets"),
            "replaced owner cannot publish asset result");
        TestCheck.That(staleBytes.Length == 0 && publications == 0, "stale generation rejected before stream write");
        owner.Dispose();
        var reopened = new CharacterRepository(path);
        TestCheck.That(reopened.List(fresh).Single().Inventory.Items.SequenceEqual(instances),
            "fresh owner and reopened repository recover identical instances and fields");

        using var freshStop = new CancellationTokenSource();
        using var freshBytes = new MemoryStream();
        var sent = new List<Packet>();
        await using var freshSender = new GameConnectionSender(fresh, freshBytes, freshStop, 0,
            (kind, opcode, _, body, _) => sent.Add(new(kind, opcode, body.ToArray())));
        var session = new GameSession(reopened, towns, fresh);
        var context = new GameConnectionContext(new(2, IPAddress.Loopback, freshBytes, freshStop), fresh, session, freshSender);
        var rosterRequest = new Packet(1, (ushort)CmdPacketType.GET_USERINFO, [255, 255, 2]);
        await commands.DispatchAsync(context, rosterRequest);
        await commands.DispatchAsync(context, rosterRequest);
        TestCheck.That(sent.Count == 1 && sent[0].Kind == 0 && sent[0].Opcode == (ushort)NotiPacketType.USERINFO,
            "new connection publishes recovered roster via actual registry handshake");
        AssetWireResult rosterEquipment = AssetWireChecks.ActorEntries(sent[0].Body, 15 + 21 + nameBytes.Length);
        TestCheck.That(rosterEquipment.ConsumedBytes == 126 && rosterEquipment.Entries.Length == 5,
            "recovered roster fully consumes five Actor equipment entries");
        TestCheck.That(rosterEquipment.Entries.All(entry => instances.Any(item => item.Position.Index == entry.Slot && item.Core.ItemId == entry.TemplateId)),
            "recovered roster templates and slots match committed database assets");
        await RejectAsync<SqliteException>(() => commands.DispatchAsync(context, request), "same-name retry rejected by committed uniqueness");
        TestCheck.That(sent.Count == 1 && reopened.List(fresh).Single().Inventory.Items.SequenceEqual(instances),
            "same-name retry sends no success and grants no second asset set");
        session.Select(committed.Slot);
        await projection.EnterAsync(context);
        Packet player = sent.Single(packet => packet.Kind == 0 && packet.Opcode == (ushort)NotiPacketType.USERINFO && packet.Body[0] == 1);
        AssetWireResult playerEquipment = AssetWireChecks.PlayerEntries(player.Body, 126);
        TestCheck.That(playerEquipment.ConsumedBytes == 461 && playerEquipment.Entries.Length == 5,
            "fresh selected Player projection consumes complete equipment and public tail");
        TestCheck.That(playerEquipment.Entries.All(entry => instances.Any(item => item.Position.Index == entry.Slot && item.Core.ItemId == entry.TemplateId
            && item.Core.Value == entry.Data && item.Core.Durability == entry.Durability)),
            "fresh Player projection matches committed random Data and current durability");
        TestCheck.That(reopened.List(fresh).Single().Inventory.Items.SequenceEqual(instances), "projection does not mutate or regrant assets");
    }

    private static async Task RejectAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            TestCheck.That(true, name);
            return;
        }

        throw new InvalidOperationException("FAIL expected " + typeof(T).Name + ": " + name);
    }

    private sealed class FailedOutput(Action beforeFailure) : MemoryStream
    {
        public int Writes { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            beforeFailure();
            return ValueTask.FromException(new IOException("fixture ACK output failure"));
        }
    }
}
