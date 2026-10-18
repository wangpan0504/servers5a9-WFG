using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class LimitCubeTcpTests
{
    const short TargetSlot = 95;
    const short TicketSlot = 85;

    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        var definition = itemResources.LimitCubes.Get(LimitCubeTests.TicketId) ??
            throw new InvalidDataException("现场PVF缺少暴戾搜捕团宝珠变更券规则");
        int requiredTarget = definition.RequiredTargetCount(LimitCubeTests.TargetId) ??
            throw new InvalidDataException("现场变更券不接受暴戾搜捕团宝珠");
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "limit-cube"),
            resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, new("LimitCubeTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95,
                AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(0).Skills,
                Inventory = BuildInventory()
            });
        await fixture.StartAsync();

        using (var peer = Open(fixture))
        {
            Send(peer);
            peer.Expect(1, 338, [0, 1, 0]);
            peer.Select();
            peer.Send(338, new byte[7]);
            peer.Expect(1, 338, [0, 1, 0]);
            peer.Barrier();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_limit_cube_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='use_limit_cube' BEGIN SELECT RAISE(ABORT,'limit cube rollback'); END");
            Send(peer);
            peer.Closed();
            That(Snapshot() == before,
                "CMD338审计末段失败关闭原连接并回滚目标、券、B材料、钱包、结果及UID");
            fixture.Sql("DROP TRIGGER fail_limit_cube_late");
        }

        int resultItemId;
        short resultSlot;
        byte[] resultRow;
        using (var peer = Open(fixture))
        {
            peer.Select();
            Send(peer);
            var deltas = new List<byte[]>();
            WirePacket ack = null!;
            for (int packets = 0; packets < 16; packets++)
            {
                var packet = peer.Read();
                if (packet is { Kind: 1, Opcode: 338 })
                {
                    ack = packet;
                    break;
                }
                That(packet.Kind == 0, "CMD338成功ACK之前只允许发布已提交通知");
                if (packet.Opcode == 14)
                    deltas.Add(packet.Body);
            }
            var response = ack ?? throw new InvalidOperationException("CMD338未返回成功ACK");
            That(response.Body.Length == 12 && response.Body[0] == 1 && response.Body[11] == 1,
                "CMD338成功ACK在全部投影后到达且保持完整12B");
            resultItemId = I32(response.Body, 1);
            ushort resultCount = U16(response.Body, 5);
            uint rawSlot = U32(response.Body, 7);
            That(rawSlot <= short.MaxValue && definition.Results.Any(row =>
                    row.ItemId == resultItemId && row.Count == resultCount),
                "成功ACK结果编号、数量和槽位来自现场PVF的实际抽取");
            resultSlot = (short)rawSlot;
            var matching = deltas.SelectMany(Rows)
                .Where(row => U16(row, 0) == resultSlot && I32(row, 2) == resultItemId).ToArray();
            That(deltas.Count != 0 && matching.Length == 1,
                "结果实体所在NOTI14严格先于ACK338，客户端查槽时已存在");
            resultRow = matching[0];
            peer.Barrier();

            string committed = Snapshot();
            Send(peer);
            peer.Expect(1, 338, [0, 1, 0]);
            peer.Barrier();
            That(Snapshot() == committed, "变更券耗尽后的相同请求不重复扣目标、材料或发放结果");
        }

        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            var main = entry.Single(packet => packet is { Kind: 0, Opcode: 13 } &&
                packet.Body.Length >= 5 && packet.Body[0] == (byte)InventoryListType.Main).Body;
            var restored = FullRows(main).Single(row => U16(row, 0) == resultSlot);
            Bytes(restored, resultRow, "重连从数据库恢复ACK引用槽的完整结果实例");
            peer.Barrier();
        }
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var saved = repository.List(owner).Single();
            That(saved.Inventory.Find(new(InventoryListType.Main, TicketSlot)) is null &&
                saved.Inventory.Find(new(InventoryListType.Main, resultSlot))?.Core.ItemId == resultItemId,
                "持久快照已消费唯一变更券并保存实际结果");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "变更券事务完成后数据库完整且无孤立实例");

        InventorySnapshot BuildInventory()
        {
            var rows = new List<InventoryItem>
            {
                new(0, new(InventoryListType.Main, TicketSlot),
                    InventoryRewardGrantService.Create(itemResources.Items.Get(LimitCubeTests.TicketId), 1,
                        shopExpiration: true)),
                new(0, new(InventoryListType.Main, TargetSlot),
                    InventoryRewardGrantService.Create(itemResources.Items.Get(LimitCubeTests.TargetId),
                        requiredTarget, shopExpiration: true))
            };
            var used = new HashSet<InventorySlot>(rows.Select(row => row.Position));
            AccountWallet wallet = AccountWallet.Empty;
            EpicPieceBook pieces = EpicPieceBook.Empty;
            int reviveCoins = 0;
            int winPoints = 0;
            foreach (var cost in definition.Materials)
            {
                if (CurrencyWallet.RewardKind(cost.ItemId) is { } currency)
                {
                    if (CurrencyWallet.IsAccount(currency))
                        wallet = wallet.Set(currency, cost.Count);
                    else if (currency == CurrencyKind.ReviveCoin)
                        reviveCoins = checked(reviveCoins + cost.Count);
                    else if (currency == CurrencyKind.WinPoint)
                        winPoints = checked(winPoints + cost.Count);
                    else
                        throw new InvalidDataException("变更券B条件不支持物品ID 0 的金币费用");
                    continue;
                }
                if (itemResources.EpicPieces.ByPiece.TryGetValue(cost.ItemId, out var piece))
                {
                    pieces = pieces.Set(itemResources.EpicPieces, piece.Index, cost.Count);
                    continue;
                }
                var template = itemResources.Items.Get(cost.ItemId);
                That(ItemSlotBoundService.List(template.Kind) == InventoryListType.Main,
                    "现场B条件实体材料属于Main容器");
                int remaining = cost.Count;
                var range = ItemSlotBoundService.Range(template.Kind, 24);
                for (short slot = range.Start; remaining > 0 && slot <= range.End; slot++)
                {
                    var position = new InventorySlot(InventoryListType.Main, slot);
                    if (used.Contains(position))
                        continue;
                    int count = Math.Min(remaining, template.StackLimit);
                    rows.Add(new(0, position, InventoryRewardGrantService.Create(template, count,
                        shopExpiration: true)));
                    used.Add(position);
                    remaining -= count;
                }
                That(remaining == 0, "TCP夹具完整准备现场B条件实体材料");
            }
            return new([.. rows])
            {
                ReviveCoins = reviveCoins,
                WinPoints = winPoints,
                AccountWallet = wallet,
                EpicPieces = pieces
            };
        }

        string Snapshot() => JsonSerializer.Serialize(fixture.Query(
                "SELECT id,revision,gold FROM characters ORDER BY id")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT item_uid,list_type,slot_index FROM character_inventory_slots ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT * FROM account_wallets ORDER BY account")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT * FROM account_currency_balances ORDER BY account,currency_kind")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT * FROM account_epic_books ORDER BY account")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT * FROM inventory_audit_log ORDER BY audit_id")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT * FROM currency_audit_log ORDER BY audit_id")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT * FROM epic_piece_audit_log ORDER BY audit_id")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }

    static void Send(TcpPeer peer) => peer.Send(338, Build(writer =>
    {
        writer.Write((ushort)TargetSlot);
        writer.Write(LimitCubeTests.TargetId);
        writer.Write((ushort)TicketSlot);
    }));

    static IEnumerable<byte[]> Rows(byte[] body)
    {
        That(body.Length >= 3 && body[0] == (byte)InventoryListType.Main,
            "CMD338库存差量属于Main容器");
        int count = U16(body, 1);
        That(body.Length == 3 + count * 123, "CMD338库存差量保持普通123B物品行");
        for (int index = 0; index < count; index++)
            yield return body.AsSpan(3 + index * 123, 123).ToArray();
    }

    static IEnumerable<byte[]> FullRows(byte[] body)
    {
        int count = U16(body, 3);
        That(body.Length == 5 + count * 123, "重连Main全量保持普通123B物品行");
        for (int index = 0; index < count; index++)
            yield return body.AsSpan(5 + index * 123, 123).ToArray();
    }

    static int I32(byte[] body, int at) =>
        BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at));
}
