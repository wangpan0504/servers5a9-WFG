using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class InventoryTcpTests
{
    static async Task VerifyRentalAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, root, resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var character = new GameSession(repository, resources.Towns, owner).CreateCharacter("RentLv1", resources.Characters.Get(14));
            repository.CommitInventory(owner, character, new(character.Inventory, character.Inventory with
            {
                AccountWallet = character.Inventory.AccountWallet.Set(CurrencyKind.RentalPoint, 3)
            }, "fixture_rental_points"));
        }
        await fixture.StartAsync();
        long rentedUid;
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            Bytes(entry[5].Body, [3, 0, 0, 0, 0, 0, 0, 0], "租赁初始化只发送余额与已租数量");
            byte[] request = Convert.FromHexString("F5733BD78025074D18EB476300470B52180E000000");
            byte[] mismatched = (byte[])request.Clone();
            mismatched[13] = 0x4E; // 408030030存在于服务端PVF第7档，不能按客户端上报的第0档领取。
            string before = Snapshot(fixture);
            peer.Send(1001, mismatched);
            peer.Expect(1, 1001, [0, 1, 0, 0, 0]);
            peer.Barrier();
            That(Snapshot(fixture) == before && fixture.Query("SELECT rental_points FROM account_wallets") is [[3L]],
                "客户端商品与服务端PVF档位不一致时拒绝，余额和库存保持");
            long sentAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            peer.Send(1001, request);
            peer.Expect(1, 1001, [1, 0, 0, 0, 0]);
            var state = peer.Expect(0, 988).Body;
            That(state.Length == 16 && U32(state, 0) == 0 && U32(state, 4) == 1 && U32(state, 8) == 408030023 &&
                U32(state, 12) >= sentAt + 86400 && U32(state, 12) <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400,
                "1级真实请求租到10级模板，扣3星并发布24小时到期状态");
            var delta = peer.Expect(0, 14).Body;
            That(delta.Length == 126 && U16(delta, 3) == 9 && U32(delta, 5) == 408030023 &&
                U32(delta, 3 + 0x38) is >= 86390 and <= 86400, "低等级租赁发布原Main单槽完整差量与剩余租期");
            peer.Barrier();
            That(fixture.Query("SELECT level FROM characters WHERE id=$p0", peer.Id) is [[1L]] &&
                fixture.Query("SELECT rental_points FROM account_wallets") is [[0L]], "租赁不升级角色，余额已提交");
            rentedUid = (long)fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=9", peer.Id)[0][0]!;
            string committed = Snapshot(fixture);
            peer.Send(1001, request);
            peer.Expect(1, 1001, [0, 1, 0, 0, 0]);
            peer.Barrier();
            That(Snapshot(fixture) == committed && fixture.Query("SELECT rental_points FROM account_wallets") is [[0L]],
                "余额不足拒绝不二扣、不新增租赁实例且连接保持");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            That(U32(entry[5].Body, 0) == 0 && U32(entry[5].Body, 4) == 1 && U32(entry[5].Body, 8) == 408030023 &&
                fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=9", peer.Id) is [[var uid]] &&
                Equals(uid, rentedUid), "重连恢复同一低等级租赁实例、余额和期限");
        }
        await fixture.StopAsync();
    }
}
