using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class ShopTcpTests
{
    // ACK真实写出后立即重选同一角色；不靠睡眠或无包超时推断旧提示已被丢弃。
    static async Task VerifyGoldFeedbackGuardAsync(TcpTestResources resources, CharacterRepository repository,
        AccountOwners owners, string account, ushort slot, string root)
    {
        using var owner = owners.Acquire(account, () => { });
        var session = new GameSession(repository, resources.Towns, owner);
        session.Select(slot);
        session.MarkReady();
        var snapshot = session.ReadInventoryForSynchronization()!;
        using var stop = new CancellationTokenSource();
        using var output = new MemoryStream();
        using var log = new ServerLog(Path.Combine(root, "shop-gold-feedback-guard"));
        var sent = new List<(byte Kind, ushort Opcode, byte[] Body)>();
        await using var sender = new GameConnectionSender(owner, output, stop, 0, (kind, opcode, _, body, action) =>
        {
            sent.Add((kind, opcode, body.ToArray()));
            if (action != "npc_shop_sell_rejected") return;
            session.ReturnToRoster();
            session.Select(slot);
            session.MarkReady();
        });
        var context = new GameConnectionContext(new(1236, IPAddress.Loopback, output, stop), owner, session, sender);
        var items = resources.ItemResources;
        var commands = new GameCommandRegistry();
        new ShopHandler(new(items.Items, items.Shops, items.GoldLimits, items.EpicPieces), null!,
            new InventoryProjection(null!, null!, items.EpicPieces), log).Register(commands);
        commands.Seal();
        byte[] request = Build(w =>
        {
            w.Write((byte)0); w.Write((short)121); w.Write(1); w.Write(1); w.Write(2); w.Write(245);
        });
        That(await commands.DispatchAsync(context, new Packet(1, 22, request)) == CommandResult.Handled,
            "金币超限用真实19B出售请求进入唯一ShopHandler");
        await sender.DrainAsync();
        That(sent.Count == 1 && sent[0].Kind == 1 && sent[0].Opcode == 22 &&
            sent[0].Body.SequenceEqual(new byte[] { 0, 22, 0 }) &&
            output.Length > 0 &&
            session.Selected!.Id == snapshot.Character.Id && !session.CanPublishInventory(snapshot),
            "失败ACK后同角色重选使原快照失效，实际发送流不向新选角补发旧金币提示");
        session.End();
    }
}
