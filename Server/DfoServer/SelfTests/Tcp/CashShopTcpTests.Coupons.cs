using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class CashShopTcpTests
{
    static ushort CreateMappedCouponRole(CharacterRepository repository, AccountOwners.Lease owner, TcpTestResources resources)
    {
        var now = DateTimeOffset.UtcNow;
        var template = resources.ItemResources.Items.Get(490703365);
        var core = InventoryRewardGrantService.Create(template, utcNow: now) with { ExpireTime = checked((int)now.AddDays(1).ToUnixTimeSeconds()) };
        return repository.Create(owner, new("CouponBuyTcp", resources.Towns.Room, resources.Towns.InitialReturn)
        {
            Job = 0, Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
            Inventory = new([new(0, new(InventoryListType.Main, 65), core),
                new(0, new(InventoryListType.Main, 66), core with { ExpireTime = 1 })])
        }).Slot;
    }

    static Task MappedCouponAsync(TcpTestFixture fixture, TcpTestResources resources, ushort slot)
    {
        const int commodity = 108628, itemId = 490701918;
        var template = resources.ItemResources.Items.Get(itemId);
        That(resources.ItemResources.CashShop.Bonus(commodity).Count == 0, "券代表商品无附赠，ACK金样固定24B");
        var owners = new AccountOwners();
        using (var peer = Open(fixture))
        {
            peer.Select(slot); peer.Barrier();
            foreach (var body in new[] { CouponBuy(0), CouponBuy(uint.MaxValue), CouponBuy(490703365, 66) })
            {
                string unchanged = PurchaseSnapshot(fixture);
                peer.Send(64, body); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
                That(PurchaseSnapshot(fixture) == unchanged, "真实19B券请求拒绝无券、uint越界及过期源，不改持久资产或断线");
            }
            string before = PurchaseSnapshot(fixture);
            fixture.Sql("CREATE TRIGGER fail_mapped_coupon_late BEFORE INSERT ON currency_audit_log " +
                "WHEN NEW.action_name='cash_shop_buy' BEGIN SELECT RAISE(ABORT,'mapped coupon TCP rollback'); END");
            peer.Send(64, CouponBuy(490703365)); peer.Closed();
            That(PurchaseSnapshot(fixture) == before, "券购买晚期SQL失败不发成功ACK，源删除、物品、点券、版本、UID序列和审计回滚");
            fixture.Sql("DROP TRIGGER fail_mapped_coupon_late");
        }
        InventoryItem reward;
        byte[] row;
        using (var peer = Open(fixture))
        {
            peer.Select(slot); peer.Barrier();
            var before = Read();
            var source = before.Inventory.Find(new(InventoryListType.Main, 65))!;
            That(source is { Uid: > 0, Core.ItemId: 490703365, Core.Count: 1 } && source.ExpireTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                "晚SQL失败重登恢复同一有效实体券");
            var started = DateTimeOffset.UtcNow;
            peer.Send(64, CouponBuy(490703365));
            Bought(peer, commodity);
            var saved = Read();
            reward = saved.Inventory.Items.Single(item => item.Core.ItemId == itemId);
            var ended = DateTimeOffset.UtcNow;
            var expected = new ItemCore(itemId, template.Kind, 1)
            {
                Durability = template.Durability,
                SealFlag = template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0
            };
            var range = ItemSlotBoundService.Range(template.Kind, saved.Inventory);
            That(saved.Revision == before.Revision + 1 && reward.Uid > 0 && reward.Uid != source.Uid &&
                saved.Inventory.Items.All(item => item.Uid != source.Uid) &&
                saved.Inventory.Find(new(InventoryListType.Main, 66)) == before.Inventory.Find(new(InventoryListType.Main, 66)) &&
                reward.Position.List == InventoryListType.Main &&
                reward.Position.Index >= range.Start && reward.Position.Index <= range.End &&
                (reward.Core with { ExpireTime = 0 }) == expected &&
                reward.ExpireTime >= template.Expiration.Resolve(started, ignoreAbsoluteExpiration: true) &&
                reward.ExpireTime <= template.Expiration.Resolve(ended, ignoreAbsoluteExpiration: true),
                "成功购买只消耗一次真实券并按真实分类创建完整新UID奖励");
            // 普通STK金样独立写基础14B，余109B为零，仅实例期限占+56，不调用生产Builder。
            row = new byte[123];
            BinaryPrimitives.WriteInt16LittleEndian(row, reward.Position.Index);
            BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(2), itemId);
            BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(6), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(11), template.Durability);
            row[13] = expected.SealFlag;
            BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(56), ItemExpiration.ForClient(reward.ExpireTime));
            if (reward.Position == source.Position)
            {
                peer.Expect(0, 14, Build(writer =>
                { writer.Write((byte)0); writer.Write((ushort)1); writer.Write((short)65); writer.Write(-1); writer.Write(new byte[117]); }));
                peer.Expect(0, 14, Build(writer => { writer.Write((byte)0); writer.Write((ushort)1); writer.Write(row); }));
            }
            else peer.Expect(0, 14, Build(writer =>
            { writer.Write((byte)0); writer.Write((ushort)2); writer.Write((short)65); writer.Write(-1); writer.Write(new byte[117]); writer.Write(row); }));
            peer.Expect(0, 53, Build(writer =>
            { writer.Write((byte)1); writer.Write(80100); writer.Write(0); writer.Write(0); }));
            That(saved.Inventory.AccountWallet.Get(DfoServer.Game.Currency.CurrencyKind.Cera) == 80100,
                "ACK后完整同步清源和新奖励行，随后按19900原价同步账号余额80100");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select(slot)[4].Body;
            int count = U16(full, 3);
            That(full[0] == 0 && full.Length == 5 + count * 123, "券购买重登恢复完整Main定长行");
            int at = Enumerable.Range(0, count).Select(index => 5 + index * 123).Single(index => U16(full, index) == reward.Position.Index);
            That(full[at..(at + 123)].SequenceEqual(row) && Read().Inventory.Find(reward.Position) == reward,
                "重登逐字节保留奖励完整核心、期限和正UID");
            string unchanged = PurchaseSnapshot(fixture);
            peer.Send(64, CouponBuy(490703365)); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            That(PurchaseSnapshot(fixture) == unchanged, "耗尽券重放不把源槽的新奖励当作券，也不再次扣款");
        }
        return Task.CompletedTask;

        Character Read()
        {
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            return new CharacterRepository(fixture.Database).LoadCharacter(owner, slot);
        }
    }

    static byte[] CouponBuy(uint related, ushort slot = 65)
    {
        byte[] body = new byte[19]; body[2] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7), 108628);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), related);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(17), related == 0 ? (ushort)0 : slot);
        return body;
    }
}
