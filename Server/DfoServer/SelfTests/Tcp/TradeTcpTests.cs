using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;

namespace DfoServer.Testing;

internal static class TradeTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "trade"), resources: resources);
        await fixture.StartAsync(startupTimeout: TimeSpan.FromMinutes(3));
        using var inviter = Open(fixture, "tradea");
        using var invitee = Open(fixture, "tradeb");
        inviter.Create("TradeA");
        invitee.Create("TradeB");
        fixture.Sql("UPDATE characters SET gold=1000 WHERE id=$p0", inviter.Id);
        fixture.Sql("UPDATE characters SET gold=1000 WHERE id=$p0", invitee.Id);
        int materialId = MailboxTests.TradableMaterial(resources.Archive, resources.ItemResources).Id;
        var firstItem = SeedItem(fixture, resources, "tradea", materialId, 5);
        var secondItem = SeedItem(fixture, resources, "tradeb", 10400, 1);
        var card = SeedItem(fixture, resources, "tradea", 490007851, 5);
        inviter.Select();
        invitee.Select();
        inviter.Barrier();
        invitee.Barrier();
        const uint token = 0x12345678;
        inviter.Send(10, Build(writer =>
        {
            writer.Write(invitee.Id);
            writer.Write((byte)1);
            writer.Write(token);
            writer.Write(new byte[4]);
        }));
        inviter.Expect(1, 10, [1]);
        var notice = invitee.Expect(0, 7).Body;
        That(notice.Length == 11 && BitConverter.ToUInt16(notice, 0) == inviter.Id && notice[2] == 1 &&
            BitConverter.ToUInt32(notice, 3) == token, "交易邀请保留邀请者和客户端令牌");
        invitee.Send(11, Build(writer =>
        {
            writer.Write(inviter.Id);
            writer.Write((byte)1);
            writer.Write(0u);
            writer.Write((ushort)0x85);
        }));
        inviter.Expect(0, 16);
        invitee.Barrier();
        inviter.Send(10, Build(writer =>
        {
            writer.Write(invitee.Id);
            writer.Write((byte)1);
            writer.Write(token);
            writer.Write(new byte[4]);
        }));
        inviter.Expect(1, 10, [1]);
        invitee.Expect(0, 7);
        invitee.Send(11, Build(writer =>
        {
            writer.Write(inviter.Id);
            writer.Write((byte)1);
            writer.Write(0u);
        }));
        invitee.Expect(1, 11, [1, (byte)inviter.Id, (byte)(inviter.Id >> 8), 1, 0]);
        var accepted = inviter.Expect(0, 8).Body;
        That(accepted.Length == 12 && BitConverter.ToUInt16(accepted, 0) == invitee.Id && accepted[2] == 1 &&
            BitConverter.ToUInt32(accepted, 3) == token, "交易接受使用邀请方令牌");
        inviter.Send(24, [0]);
        inviter.Expect(1, 24, [1]);
        inviter.Expect(0, 16);
        invitee.Expect(0, 16);
        inviter.Barrier();
        invitee.Barrier();
        GoldTrade(fixture, inviter, invitee, token, firstItem, secondItem);
        InsufficientGold(fixture, inviter, invitee, token);
        WriteFailure(fixture, inviter, invitee, token, secondItem.Uid);
        MovedItem(fixture, inviter, invitee, token, secondItem.Uid);
        EmptyTrade(inviter, invitee, token);
        LimitedCardTrade(fixture, inviter, invitee, token, card);
        inviter.Send(10, Build(writer =>
        {
            writer.Write(invitee.Id);
            writer.Write((byte)0);
            writer.Write(0u);
            writer.Write(new byte[6]);
        }));
        inviter.Expect(1, 10, [1]);
        invitee.Expect(0, 2);
        var partyNotice = invitee.Expect(0, 7).Body;
        That(partyNotice.Length == 17 && BitConverter.ToUInt16(partyNotice, 0) == inviter.Id && partyNotice[2] == 0,
            "组队邀请保留原有分支和包格式");
        Disconnect(fixture);
        ReturnToSelection(fixture);
        ChangeArea(fixture);
        SwitchCharacter(fixture);
    }

    private static void EmptyTrade(TcpPeer inviter, TcpPeer invitee, uint token)
    {
        inviter.Send(10, Build(writer =>
        {
            writer.Write(invitee.Id);
            writer.Write((byte)1);
            writer.Write(token);
            writer.Write(new byte[4]);
        }));
        inviter.Expect(1, 10, [1]);
        invitee.Expect(0, 7);
        invitee.Send(11, Build(writer =>
        {
            writer.Write(inviter.Id);
            writer.Write((byte)1);
            writer.Write(0u);
        }));
        invitee.Expect(1, 11);
        inviter.Expect(0, 8);
        inviter.Send(24, [5]);
        inviter.Expect(1, 24, [1]);
        invitee.Expect(0, 17);
        invitee.Send(24, [5]);
        invitee.Expect(1, 24, [1]);
        inviter.Expect(0, 17);
        inviter.Expect(0, 655, []);
        invitee.Expect(0, 655, []);
        inviter.Send(24, [1]);
        inviter.Expect(1, 24, [1]);
        invitee.Expect(0, 17);
        invitee.Send(24, [1]);
        invitee.Expect(1, 24, [1]);
        inviter.Expect(0, 17);
        inviter.Expect(0, 18, [0, 0]);
        invitee.Expect(0, 18, [0, 0]);
        inviter.Expect(0, 1681);
        invitee.Expect(0, 1681);
        inviter.Barrier();
        invitee.Barrier();
    }

    private static void WriteFailure(TcpTestFixture fixture, TcpPeer inviter, TcpPeer invitee, uint token, long itemUid)
    {
        var repository = new CharacterRepository(fixture.Database);
        using var owner = new AccountOwners().Acquire("tradea", () => { });
        var item = repository.List(owner).Single().Inventory.Items.Single(row => row.Uid == itemUid);
        string before = fixture.Snapshot("SELECT id,gold,revision FROM characters WHERE id IN ($p0,$p1) ORDER BY id", inviter.Id, invitee.Id);
        string itemsBefore = fixture.Snapshot("SELECT character_id,item_uid,item_core FROM character_inventory_items WHERE character_id IN ($p0,$p1) ORDER BY character_id,item_uid", inviter.Id, invitee.Id);
        string slotsBefore = fixture.Snapshot("SELECT character_id,list_type,slot_index,item_uid FROM character_inventory_slots WHERE character_id IN ($p0,$p1) ORDER BY character_id,list_type,slot_index", inviter.Id, invitee.Id);
        inviter.Send(10, Build(writer =>
        {
            writer.Write(invitee.Id);
            writer.Write((byte)1);
            writer.Write(token);
            writer.Write(new byte[4]);
        }));
        inviter.Expect(1, 10, [1]);
        invitee.Expect(0, 7);
        invitee.Send(11, Build(writer =>
        {
            writer.Write(inviter.Id);
            writer.Write((byte)1);
            writer.Write(0u);
        }));
        invitee.Expect(1, 11);
        inviter.Expect(0, 8);
        MoveGold(inviter, invitee, 0, 4, 30, 30);
        MoveGold(invitee, inviter, 0, 4, 20, 20);
        MoveItem(inviter, invitee, item, false);
        inviter.Send(24, [5]);
        inviter.Expect(1, 24, [1]);
        invitee.Expect(0, 17);
        invitee.Send(24, [5]);
        invitee.Expect(1, 24, [1]);
        inviter.Expect(0, 17);
        inviter.Expect(0, 655, []);
        invitee.Expect(0, 655, []);
        inviter.Send(24, [1]);
        inviter.Expect(1, 24, [1]);
        invitee.Expect(0, 17);
        fixture.Sql($"CREATE TRIGGER fail_trade_write BEFORE UPDATE ON characters WHEN NEW.id={invitee.Id} BEGIN SELECT RAISE(ABORT, 'trade test failure'); END");
        try
        {
            invitee.Send(24, [1]);
            invitee.Expect(1, 24, [1]);
            inviter.Expect(0, 17);
            inviter.Expect(0, 16);
            invitee.Expect(0, 16);
            inviter.Expect(0, 1681);
            invitee.Expect(0, 1681);
            That(fixture.Snapshot("SELECT id,gold,revision FROM characters WHERE id IN ($p0,$p1) ORDER BY id", inviter.Id, invitee.Id) == before,
                "一方数据库写入失败时双方金币和版本保持不变");
            That(fixture.Snapshot("SELECT character_id,item_uid,item_core FROM character_inventory_items WHERE character_id IN ($p0,$p1) ORDER BY character_id,item_uid", inviter.Id, invitee.Id) == itemsBefore &&
                fixture.Snapshot("SELECT character_id,list_type,slot_index,item_uid FROM character_inventory_slots WHERE character_id IN ($p0,$p1) ORDER BY character_id,list_type,slot_index", inviter.Id, invitee.Id) == slotsBefore,
                "一方数据库写入失败时双方物品实例和槽位保持不变");
        }
        finally
        {
            fixture.Sql("DROP TRIGGER fail_trade_write");
        }
    }

    private static void InsufficientGold(TcpTestFixture fixture, TcpPeer inviter, TcpPeer invitee, uint token)
    {
        string before = fixture.Snapshot("SELECT id,gold,revision FROM characters WHERE id IN ($p0,$p1) ORDER BY id", inviter.Id, invitee.Id);
        inviter.Send(10, Build(writer =>
        {
            writer.Write(invitee.Id);
            writer.Write((byte)1);
            writer.Write(token);
            writer.Write(new byte[4]);
        }));
        inviter.Expect(1, 10, [1]);
        invitee.Expect(0, 7);
        invitee.Send(11, Build(writer =>
        {
            writer.Write(inviter.Id);
            writer.Write((byte)1);
            writer.Write(0u);
        }));
        invitee.Expect(1, 11);
        inviter.Expect(0, 8);
        MoveGold(inviter, invitee, 0, 4, 2000, 2000);
        inviter.Send(24, [5]);
        inviter.Expect(1, 24, [1]);
        invitee.Expect(0, 17);
        invitee.Send(24, [5]);
        invitee.Expect(1, 24, [1]);
        inviter.Expect(0, 17);
        inviter.Expect(0, 655, []);
        invitee.Expect(0, 655, []);
        inviter.Send(24, [1]);
        inviter.Expect(1, 24, [1]);
        invitee.Expect(0, 17);
        invitee.Send(24, [1]);
        invitee.Expect(1, 24, [1]);
        inviter.Expect(0, 17);
        inviter.Expect(0, 16);
        invitee.Expect(0, 16);
        inviter.Expect(0, 1681);
        That(fixture.Snapshot("SELECT id,gold,revision FROM characters WHERE id IN ($p0,$p1) ORDER BY id", inviter.Id, invitee.Id) == before,
            "金币不足时双方资产及版本均不提交");
    }

    private static void MovedItem(TcpTestFixture fixture, TcpPeer inviter, TcpPeer invitee, uint token, long itemUid)
    {
        var repository = new CharacterRepository(fixture.Database);
        using var owner = new AccountOwners().Acquire("tradea", () => { });
        var inventory = repository.List(owner).Single().Inventory;
        var item = inventory.Items.Single(row => row.Uid == itemUid);
        var (start, end) = ItemSlotBoundService.Range(item.Core.Kind, inventory);
        short destination = checked((short)Enumerable.Range(start, end - start + 1)
            .First(index => inventory.Find(new(item.Position.List, (short)index)) is null));
        inviter.Send(10, Build(writer => { writer.Write(invitee.Id); writer.Write((byte)1); writer.Write(token); writer.Write(new byte[4]); }));
        inviter.Expect(1, 10, [1]);
        invitee.Expect(0, 7);
        invitee.Send(11, Build(writer => { writer.Write(inviter.Id); writer.Write((byte)1); writer.Write(0u); }));
        invitee.Expect(1, 11);
        inviter.Expect(0, 8);
        MoveItem(inviter, invitee, item, false);
        inviter.Send(19, Build(writer =>
        {
            writer.Write((byte)item.Position.List); writer.Write(item.Position.Index); writer.Write(item.Core.ItemId); writer.Write(item.Core.Count);
            writer.Write((byte)item.Position.List); writer.Write(destination); writer.Write(0); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
        }));
        That(inviter.Expect(1, 19).Body[0] == 1, "交易报价后移动物品成功");
        string before = fixture.Snapshot("SELECT character_id,item_uid,item_core FROM character_inventory_items WHERE character_id IN ($p0,$p1) ORDER BY character_id,item_uid", inviter.Id, invitee.Id);
        inviter.Send(24, [5]); inviter.Expect(1, 24, [1]); invitee.Expect(0, 17);
        invitee.Send(24, [5]); invitee.Expect(1, 24, [1]); inviter.Expect(0, 17);
        inviter.Expect(0, 655); invitee.Expect(0, 655);
        inviter.Send(24, [1]); inviter.Expect(1, 24, [1]); invitee.Expect(0, 17);
        invitee.Send(24, [1]); invitee.Expect(1, 24, [1]); inviter.Expect(0, 17);
        inviter.Expect(0, 16); invitee.Expect(0, 16);
        inviter.Expect(0, 1681);
        That(fixture.Snapshot("SELECT character_id,item_uid,item_core FROM character_inventory_items WHERE character_id IN ($p0,$p1) ORDER BY character_id,item_uid", inviter.Id, invitee.Id) == before,
            "报价物品被移动后成交取消且双方归属不变");
        inviter.Barrier();
        invitee.Barrier();
    }

    private static InventoryItem SeedItem(TcpTestFixture fixture, TcpTestResources resources, string account, int itemId, int count)
    {
        using var owner = new AccountOwners().Acquire(account, () => { });
        var repository = new CharacterRepository(fixture.Database);
        var character = repository.List(owner).Single();
        var existing = character.Inventory.Items.Select(item => item.Uid).ToHashSet();
        var itemResources = resources.ItemResources;
        var grant = InventoryRewardGrantService.TryGrantBatch(character, [new(itemId, count)],
            itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
        That(grant.Accepted, "交易测试准备可交换物品");
        var saved = repository.CommitInventory(owner, character, grant.Plan!);
        return saved.Inventory.Items.Single(item => item.Core.ItemId == itemId && !existing.Contains(item.Uid));
    }

    private static void GoldTrade(TcpTestFixture fixture, TcpPeer inviter, TcpPeer invitee, uint token,
        InventoryItem firstItem, InventoryItem secondItem)
    {
        inviter.Send(10, Build(writer =>
        {
            writer.Write(invitee.Id);
            writer.Write((byte)1);
            writer.Write(token);
            writer.Write(new byte[4]);
        }));
        inviter.Expect(1, 10, [1]);
        invitee.Expect(0, 7);
        invitee.Send(11, Build(writer =>
        {
            writer.Write(inviter.Id);
            writer.Write((byte)1);
            writer.Write(0u);
        }));
        invitee.Expect(1, 11);
        inviter.Expect(0, 8);

        MoveGold(inviter, invitee, 0, 4, 100, 100);
        MoveGold(inviter, invitee, 4, 0, 25, 75);
        MoveGold(invitee, inviter, 0, 4, 50, 50);
        MoveGold(invitee, inviter, 4, 0, 10, 40);
        MoveGold(invitee, inviter, 0, 4, 10, 50);
        MoveItem(inviter, invitee, firstItem, false);
        MoveItem(inviter, invitee, firstItem, true);
        MoveItem(inviter, invitee, firstItem, false);
        MoveItem(invitee, inviter, secondItem, false);
        MoveItem(invitee, inviter, secondItem, true);
        MoveItem(invitee, inviter, secondItem, false);

        inviter.Send(24, [5]);
        inviter.Expect(1, 24, [1]);
        invitee.Expect(0, 17, [ (byte)inviter.Id, (byte)(inviter.Id >> 8), 5 ]);
        invitee.Send(24, [5]);
        invitee.Expect(1, 24, [1]);
        inviter.Expect(0, 17, [ (byte)invitee.Id, (byte)(invitee.Id >> 8), 5 ]);
        inviter.Expect(0, 655, []);
        invitee.Expect(0, 655, []);
        inviter.Send(24, [1]);
        inviter.Expect(1, 24, [1]);
        invitee.Expect(0, 17, [ (byte)inviter.Id, (byte)(inviter.Id >> 8), 1 ]);
        invitee.Send(24, [1]);
        invitee.Expect(1, 24, [1]);
        inviter.Expect(0, 17, [ (byte)invitee.Id, (byte)(invitee.Id >> 8), 1 ]);
        inviter.Expect(0, 18);
        invitee.Expect(0, 18);
        inviter.Expect(0, 1681);
        invitee.Expect(0, 1681);
        inviter.Barrier();
        invitee.Barrier();
        inviter.NoFrame(150, "成交后不能发送取消通知", (0, 16));
        invitee.NoFrame(150, "成交后不能发送取消通知", (0, 16));
        That(Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0", inviter.Id)[0][0]) == 975 &&
            Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0", invitee.Id)[0][0]) == 1025,
            "交易提交双方金币并保留取回的金额");
        var repository = new CharacterRepository(fixture.Database);
        using var firstOwner = new AccountOwners().Acquire("tradea", () => { });
        using var secondOwner = new AccountOwners().Acquire("tradeb", () => { });
        var first = repository.List(firstOwner).Single();
        var second = repository.List(secondOwner).Single();
        That(first.Inventory.Items.All(item => item.Uid != firstItem.Uid) &&
            first.Inventory.Items.Any(item => item.Uid == secondItem.Uid && item.Core.Count == secondItem.Core.Count) &&
            second.Inventory.Items.All(item => item.Uid != secondItem.Uid) &&
            second.Inventory.Items.Any(item => item.Uid == firstItem.Uid && item.Core.Count == firstItem.Core.Count),
            "成交后双方物品与数量交换且来源消失");
    }

    private static void LimitedCardTrade(TcpTestFixture fixture, TcpPeer first, TcpPeer second, uint token, InventoryItem source)
    {
        first.Send(10, Build(writer => { writer.Write(second.Id); writer.Write((byte)1); writer.Write(token); writer.Write(new byte[4]); }));
        first.Expect(1, 10, [1]);
        second.Expect(0, 7);
        second.Send(11, Build(writer => { writer.Write(first.Id); writer.Write((byte)1); writer.Write(0u); }));
        second.Expect(1, 11);
        first.Expect(0, 8);
        MoveItem(first, second, source, false, 2);
        MoveItem(first, second, source, true, 2);
        MoveItem(first, second, source, false, 2);
        first.Send(24, [5]); first.Expect(1, 24, [1]); second.Expect(0, 17);
        second.Send(24, [5]); second.Expect(1, 24, [1]); first.Expect(0, 17);
        first.Expect(0, 655, []); second.Expect(0, 655, []);
        first.Send(24, [1]); first.Expect(1, 24, [1]); second.Expect(0, 17);
        second.Send(24, [1]); second.Expect(1, 24, [1]); first.Expect(0, 17);
        first.Expect(0, 18); second.Expect(0, 18);
        var firstDelta = first.Expect(0, 1681).Body;
        var secondDelta = second.Expect(0, 1681).Body;
        var repository = new CharacterRepository(fixture.Database);
        using var firstOwner = new AccountOwners().Acquire("tradea", () => { });
        using var secondOwner = new AccountOwners().Acquire("tradeb", () => { });
        var remaining = repository.List(firstOwner).Single().Inventory.Items.Single(item => item.Uid == source.Uid);
        var received = repository.List(secondOwner).Single().Inventory.Items.Single(item => item.Core.ItemId == source.Core.ItemId);
        That(remaining.Core == source.Core with { Value = 3 } && received.Core == source.Core with { Value = 2, Extension120 = 0x51 },
            "真实交易请求拆堆仅绑定转出卡片，取回重放不消耗次数");
        That(firstDelta.SequenceEqual(DfoServer.Network.Builders.Inventory.InventoryPackets.Delta(repository.List(firstOwner).Single().Inventory,
                source.Position.List, [remaining.Position])) &&
            secondDelta.SequenceEqual(DfoServer.Network.Builders.Inventory.InventoryPackets.Delta(repository.List(secondOwner).Single().Inventory,
                received.Position.List, [received.Position])), "成交1681真实发布源余量和offset120绑定状态");
        first.Barrier(); second.Barrier();
        first.Send(10, Build(writer => { writer.Write(second.Id); writer.Write((byte)1); writer.Write(token); writer.Write(new byte[4]); }));
        first.Expect(1, 10, [1]); second.Expect(0, 7);
        second.Send(11, Build(writer => { writer.Write(first.Id); writer.Write((byte)1); writer.Write(0u); }));
        second.Expect(1, 11); first.Expect(0, 8);
        second.Send(19, Build(writer =>
        {
            writer.Write((byte)received.Position.List); writer.Write((ushort)received.Position.Index); writer.Write(received.Core.ItemId);
            writer.Write(1u); writer.Write((byte)4); writer.Write((ushort)3); writer.Write(0u); writer.Write(0u); writer.Write(-1); writer.Write((ushort)0);
        }));
        second.Expect(1, 19, [0, 1, 0]);
        first.Send(24, [0]); first.Expect(1, 24, [1]); first.Expect(0, 16); second.Expect(0, 16);
        first.Barrier(); second.Barrier();
        That(repository.List(secondOwner).Single().Inventory.Items.Single(item => item.Uid == received.Uid) == received,
            "绑定卡片再次上交易栏拒绝且实例不变");
    }

    private static void MoveItem(TcpPeer actor, TcpPeer other, InventoryItem item, bool take, int? transferCount = null)
    {
        byte source = take ? (byte)4 : (byte)item.Position.List;
        byte destination = take ? (byte)item.Position.List : (byte)4;
        ushort sourceSlot = take ? (ushort)3 : checked((ushort)item.Position.Index);
        ushort destinationSlot = take ? checked((ushort)item.Position.Index) : (ushort)3;
        uint count = checked((uint)(transferCount ?? item.Core.Count));
        actor.Send(19, Build(writer =>
        {
            writer.Write(source);
            writer.Write(sourceSlot);
            writer.Write(item.Core.ItemId);
            writer.Write(count);
            writer.Write(destination);
            writer.Write(destinationSlot);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(-1);
            writer.Write((ushort)0);
        }));
        actor.Expect(1, 19, Build(writer =>
        {
            writer.Write((byte)1);
            writer.Write(source);
            writer.Write(sourceSlot);
            writer.Write(count);
            writer.Write(destination);
            writer.Write(destinationSlot);
        }));
        var update = other.Expect(0, 15).Body;
        That(update.Length == 123 && BitConverter.ToUInt16(update, 0) == 3 &&
            BitConverter.ToUInt32(update, 2) == (take ? uint.MaxValue : (uint)item.Core.ItemId),
            "交易物品放入和取回同步对方行号与模板");
    }

    private static void MoveGold(TcpPeer actor, TcpPeer other, byte source, byte destination, uint amount, uint offered)
    {
        actor.Send(19, Build(writer =>
        {
            writer.Write(source);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write(amount);
            writer.Write(destination);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(-1);
            writer.Write((ushort)0);
        }));
        actor.Expect(1, 19, Build(writer =>
        {
            writer.Write((byte)1);
            writer.Write(source);
            writer.Write((ushort)0);
            writer.Write(amount);
            writer.Write(destination);
            writer.Write((ushort)0);
        }));
        var update = other.Expect(0, 15).Body;
        That(update.Length == 123 && BitConverter.ToUInt16(update, 0) == 0 &&
            BitConverter.ToUInt32(update, 2) == 0 && BitConverter.ToUInt32(update, 6) == offered,
            "对方交易窗口显示金币累计报价");
    }

    private static void Disconnect(TcpTestFixture fixture)
    {
        using var inviter = Open(fixture, "tradec");
        using var invitee = Open(fixture, "traded");
        inviter.Create("TradeC");
        invitee.Create("TradeD");
        inviter.Select();
        invitee.Select();
        inviter.Barrier();
        invitee.Barrier();
        inviter.Send(10, Build(writer =>
        {
            writer.Write(invitee.Id);
            writer.Write((byte)1);
            writer.Write(0x12345678u);
            writer.Write(new byte[4]);
        }));
        inviter.Expect(1, 10, [1]);
        invitee.Expect(0, 7);
        invitee.Send(11, Build(writer =>
        {
            writer.Write(inviter.Id);
            writer.Write((byte)1);
            writer.Write(0u);
        }));
        invitee.Expect(1, 11);
        inviter.Expect(0, 8);
        inviter.Dispose();
        invitee.Expect(0, 16);
    }

    private static void ReturnToSelection(TcpTestFixture fixture)
    {
        using var inviter = Open(fixture, "tradee");
        using var invitee = Open(fixture, "tradef");
        inviter.Create("TradeE");
        invitee.Create("TradeF");
        inviter.Select();
        invitee.Select();
        inviter.Barrier();
        invitee.Barrier();
        inviter.Send(10, Build(writer =>
        {
            writer.Write(invitee.Id);
            writer.Write((byte)1);
            writer.Write(0x12345678u);
            writer.Write(new byte[4]);
        }));
        inviter.Expect(1, 10, [1]);
        invitee.Expect(0, 7);
        invitee.Send(11, Build(writer =>
        {
            writer.Write(inviter.Id);
            writer.Write((byte)1);
            writer.Write(0u);
        }));
        invitee.Expect(1, 11);
        inviter.Expect(0, 8);
        inviter.Send(7);
        invitee.Expect(0, 16);
    }

    private static void ChangeArea(TcpTestFixture fixture)
    {
        using var inviter = Open(fixture, "tradeg");
        using var invitee = Open(fixture, "tradeh");
        inviter.Create("TradeG");
        invitee.Create("TradeH");
        inviter.Select();
        invitee.Select();
        inviter.Barrier();
        invitee.Barrier();
        inviter.Send(10, Build(writer =>
        {
            writer.Write(invitee.Id);
            writer.Write((byte)1);
            writer.Write(0x12345678u);
            writer.Write(new byte[4]);
        }));
        inviter.Expect(1, 10, [1]);
        invitee.Expect(0, 7);
        invitee.Send(11, Build(writer =>
        {
            writer.Write(inviter.Id);
            writer.Write((byte)1);
            writer.Write(0u);
        }));
        invitee.Expect(1, 11);
        inviter.Expect(0, 8);
        inviter.Send(1426);
        invitee.Expect(0, 16);
    }

    private static void SwitchCharacter(TcpTestFixture fixture)
    {
        using var inviter = Open(fixture, "tradei");
        using var invitee = Open(fixture, "tradej");
        inviter.Create("TradeI");
        inviter.Create("TradeAlt");
        invitee.Create("TradeJ");
        inviter.Select(0);
        invitee.Select();
        inviter.Barrier();
        invitee.Barrier();
        inviter.Send(10, Build(writer =>
        {
            writer.Write(invitee.Id);
            writer.Write((byte)1);
            writer.Write(0x12345678u);
            writer.Write(new byte[4]);
        }));
        inviter.Expect(1, 10, [1]);
        invitee.Expect(0, 7);
        invitee.Send(11, Build(writer =>
        {
            writer.Write(inviter.Id);
            writer.Write((byte)1);
            writer.Write(0u);
        }));
        invitee.Expect(1, 11);
        inviter.Expect(0, 8);
        inviter.Send(7);
        invitee.Expect(0, 16);
        inviter.Expect(1, 7, [1]);
        inviter.Expect(0, 2);
        inviter.Select(1);
        inviter.Barrier();
        invitee.Barrier();
    }
}
