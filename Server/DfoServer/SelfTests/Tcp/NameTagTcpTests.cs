using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class NameTagTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "name-tag"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var character = repository.Create(owner, new("NameTagTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0))
            });
            InventoryService.Commit(repository, owner, character,
                new CurrencyService(resources.ItemResources.GoldLimits).Grant(character, CurrencyKind.Cera, 1000));
        }
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var character = repository.LoadCharacter(owner, 0);
            var r = resources.ItemResources;
            var service = new InventoryCashShopService(r.Items, r.CashShop, r.GoldLimits, r.EpicPieces, contracts: r.Contracts);
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var tagRequest = new CashShopPurchaseRequest(0, [new(100615, 0)]);
            var tagged = service.Buy(character, tagRequest, out _, now);
            var tagSlot = new InventorySlot(InventoryListType.Equipment, 30);
            var firstTag = tagged.Plan!.After.Find(tagSlot)!;
            That(tagged.Accepted && firstTag.Core.ItemId == 2681313 && firstTag.ExpireTime == now.AddDays(7).ToUnixTimeSeconds() &&
                tagged.Plan.After.AccountWallet.Get(CurrencyKind.Cera) == 700, "名称装饰按7天商品期限直接生效，不发到背包");
            var renewed = service.Buy(character with { Inventory = tagged.Plan.After }, tagRequest, out _, now);
            That(renewed.Accepted && renewed.Plan!.After.Find(tagSlot)!.ExpireTime == now.AddDays(14).ToUnixTimeSeconds(), "同款名称装饰从现有期限续期");
            var replaced = service.Buy(character with { Inventory = renewed.Plan!.After }, new(0, [new(100616, 0)]), out _, now);
            That(replaced.Accepted && replaced.Plan!.After.Find(tagSlot) is { Core.ItemId: 2681314 } replacement &&
                replacement.ExpireTime == now.AddDays(7).ToUnixTimeSeconds(), "换款覆盖旧款及期限，不继承旧款额外天数");
            var expiredTag = tagged.Plan.After with { Items = tagged.Plan.After.Items.Select(item => item.Position == tagSlot ?
                item with { Core = item.Core with { ExpireTime = (int)now.ToUnixTimeSeconds() } } : item).ToImmutableArray() };
            var restarted = service.Buy(character with { Inventory = expiredTag }, tagRequest, out _, now);
            That(restarted.Accepted && restarted.Plan!.After.Find(tagSlot)!.ExpireTime == now.AddDays(7).ToUnixTimeSeconds(), "过期同款从当前时间重新计期");
            var cappedTag = tagged.Plan.After with { Items = tagged.Plan.After.Items.Select(item => item.Position == tagSlot ?
                item with { Core = item.Core with { ExpireTime = int.MaxValue } } : item).ToImmutableArray() };
            That(!service.Buy(character with { Inventory = cappedTag }, tagRequest, out _, now).Accepted &&
                !service.Buy(character, new(0, [new(100615, 0), new(100616, 0)]), out _, now).Accepted,
                "期限溢出及同订单多款覆盖拒绝扣费");
            using (var tagStream = new MemoryStream())
            using (var tagWriter = new BinaryWriter(tagStream))
            {
                EquipmentProtocolWriter.NameTag(tagWriter, tagged.Plan.After, now.ToUnixTimeSeconds());
                That(BinaryPrimitives.ReadInt32LittleEndian(tagStream.ToArray()) == 2681313 && tagStream.Length == 8,
                    "名称装饰独立字段保持当前客户端8B布局");
                tagStream.SetLength(0);
                EquipmentProtocolWriter.NameTag(tagWriter, expiredTag, now.ToUnixTimeSeconds());
                That(tagStream.ToArray().All(value => value == 0), "到期独立字段清零，不等待后台删除才隐藏效果");
            }
        }
        await fixture.StartAsync();
        uint expiry;
        using (var peer = Open(fixture))
        {
            peer.Select();
            expiry = Purchase(peer, 100615, 2681313, 700);
            uint renewed = Purchase(peer, 100615, 2681313, 400);
            That(renewed == expiry + 7 * 86400, "同款第二次购买续7天，权威Actor字段立即更新");
            expiry = Purchase(peer, 100616, 2681314, 100);
            That(expiry < renewed, "换款回到新商品7天期限，不继承旧款剩余14天");
            peer.Send(64, Request(100615)); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            That(Tag(entry[2].Body) == (2681314u, expiry), "完整重启和选角后恢复实际购买款式与期限");
            peer.Barrier();
        }
        await fixture.StopAsync();

        static uint Purchase(TcpPeer peer, int commodity, uint item, uint balance)
        {
            peer.Send(64, Request(commodity));
            That(peer.Expect(1, 64).Body[0] == 1, "名称装饰购买成功");
            var tag = Tag(peer.Expect(0, 2).Body);
            That(tag.Item == item && tag.Expiry > DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Actor携带有效名称装饰");
            That(peer.Expect(0, 2).Body[0] == 1, "本人Player同步名称装饰和派生属性");
            That(U32(peer.Expect(0, 53).Body, 1) == balance, "名称装饰扣取实际商品价格");
            return tag.Expiry;
        }
        static byte[] Request(int commodity)
        {
            byte[] body = new byte[19]; body[2] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7), commodity);
            return body;
        }
        static (uint Item, uint Expiry) Tag(byte[] actor)
        {
            // 独立按当前Actor字段读取，不能调用服务端Builder自证。
            int at = 123;
            at += 4 + checked((int)U32(actor, at)) + 6;
            int count = actor[at++];
            for (int i = 0; i < count; i++)
            {
                byte slot = actor[at++]; at += 4;
                int colors = checked((int)U32(actor, at)); at += 4 + colors + 3;
                uint clear = U32(actor, at); at += 4;
                if (slot == 9 && clear > 0) at += 4 + checked((int)U32(actor, at));
                at += 5;
                That(slot != 30, "名称装饰不重复进入普通装备外观数组");
            }
            at += 8;
            return (U32(actor, at), U32(actor, at + 4));
        }
    }
}
