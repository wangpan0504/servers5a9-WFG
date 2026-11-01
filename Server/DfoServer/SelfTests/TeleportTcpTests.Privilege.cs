using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.PrivilegeServices;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class PrivilegeTeleportTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new Clock();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "privilege-teleport"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        Character entitled, ordinary;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, static () => { }))
        {
            var candidate = CharacterCreation.Prepare("PrivilegeTP", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, null) with { Level = 60, CompletedQuests = [3156] };
            entitled = repository.Create(owner, candidate);
            ordinary = repository.Create(owner, candidate with { Name = "NoPrivilege" });
            var product = resources.ItemResources.CashShop.Products.Values.Single(row => row.Section == "privilege service");
            var state = PrivilegeServicePolicy.Purchase(PrivilegeServiceState.Empty, product,
                resources.ItemResources.PrivilegeServices, clock.GetUtcNow());
            entitled = repository.CommitInventory(owner, entitled, new(entitled.Inventory,
                entitled.Inventory with { PrivilegeService = state }, "fixture_privilege_teleport"));
        }
        long duration = resources.ItemResources.PrivilegeServices.TeleportCooldownSeconds * 1000L;
        That(duration == 5000, "真实礼遇资源声明独立5秒传送冷却");
        await fixture.StartAsync();
        byte[] request = Request(38, 0, 1327, 234);
        long until;
        using (var peer = Open(fixture))
        {
            peer.Select(entitled.Slot, prepared: true); peer.Barrier();
            foreach (int width in new[] { 0, 18, 20, 21 })
            {
                string before = Facts(peer.Id);
                peer.Send(1641, new byte[width]);
                // 后续屏障确认派发完成；若畸形请求伪回成功或切图，严格屏障会直接失败。
                peer.Barrier();
                That(Facts(peer.Id) == before, "真实1641畸形正文不移动、不写冷却或角色资产：" + width);
            }
            RejectArea(peer, Request(57, 2, 10, 10));
            RejectArea(peer, Request(255, 255, 65535, 65535));
            var beforeSuccess = fixture.Query("SELECT revision,direction FROM characters WHERE id=$p0", peer.Id).Single();
            string qualification = fixture.Snapshot("SELECT * FROM character_privilege_service WHERE character_id=$p0", peer.Id);
            peer.Send(1641, request);
            peer.Expect(1, 1641, [1, 0, 0, 0, 0, 0]);
            Arrival(peer, Convert.ToByte(beforeSuccess[1]));
            until = clock.GetUtcNow().ToUnixTimeMilliseconds() + duration;
            That(fixture.Query("SELECT revision,town,area,x,y,direction FROM characters WHERE id=$p0", peer.Id)
                    is [[long revision, 38L, 0L, 1327L, 234L, long direction]] &&
                revision == (long)beforeSuccess[0] + 1 && direction == (long)beforeSuccess[1] &&
                fixture.Query("SELECT potion_until,aura_until,upgrade_aura_until,party_aura_until,privilege_until FROM character_teleport_cooldowns WHERE character_id=$p0", peer.Id)
                    is [[0L, 0L, 0L, 0L, long savedUntil]] && savedUntil == until &&
                fixture.Snapshot("SELECT * FROM character_privilege_service WHERE character_id=$p0", peer.Id) == qualification,
                "真实Handler先提交位置、原朝向、一次revision和第五冷却，资格不消耗，旧四类冷却不伪造");
            RejectCooldown(peer);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(entitled.Slot, prepared: true); peer.Barrier();
            That(fixture.Query("SELECT privilege_until FROM character_teleport_cooldowns WHERE character_id=$p0", peer.Id)
                is [[long restored]] && restored == until, "真实重连选角恢复礼遇截止，既有初始化不伪造光环378或NOTI669");
            RejectCooldown(peer);
            clock.Advance(duration - 1);
            RejectCooldown(peer);
            clock.Advance(1);
            byte direction = Convert.ToByte(fixture.Query("SELECT direction FROM characters WHERE id=$p0", peer.Id)[0][0]);
            var clean = (byte[])request.Clone(); Array.Clear(clean, 0, 13);
            peer.Send(1641, clean); peer.Expect(1, 1641, [1, 0, 0, 0, 0, 0]); Arrival(peer, direction);
            That(fixture.Query("SELECT privilege_until FROM character_teleport_cooldowns WHERE character_id=$p0", peer.Id)
                is [[long renewed]] && renewed == until + duration,
                "冷却截止前1毫秒拒绝，恰截止允许；清零前缀与首包脏栈使用同一真实末六字节合同");
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(ordinary.Slot, prepared: true); peer.Barrier();
            string before = Facts(peer.Id);
            peer.Send(1641, request); peer.Expect(1, 1641, [0, 1, 0, 0, 0, 0, 0, 0]); peer.Barrier();
            That(Facts(peer.Id) == before && fixture.Query("SELECT count(*) FROM character_teleport_cooldowns WHERE character_id=$p0", peer.Id)
                is [[0L]], "同账号无礼遇角色真实失败8B且不借其他角色资格，不生成冷却或旅行");
        }

        void RejectArea(TcpPeer peer, byte[] body)
        {
            string before = Facts(peer.Id);
            peer.Send(1641, body); peer.Expect(1, 1641, [0, 2, 0, 0, 0, 0, 0, 0]); peer.Barrier();
            That(Facts(peer.Id) == before, "团队专属城镇和非法落点走真实地区error2，位置、资产和冷却不变");
        }
        void RejectCooldown(TcpPeer peer)
        {
            string before = Facts(peer.Id);
            peer.Send(1641, request); peer.Expect(1, 1641, [0, 1, 0, 0, 0, 0, 0, 0]); peer.Barrier();
            That(Facts(peer.Id) == before, "冷却重复请求走真实8B非零错误，无旅行通知或截止延长");
        }
        static void Arrival(TcpPeer peer, byte direction)
        {
            byte[] area = peer.Expect(0, 23).Body;
            byte[] scene = peer.Expect(0, 24).Body;
            That(area.Length == 10 && U16(area, 0) == peer.Id && area[2] == 38 && area[3] == 0 &&
                U16(area, 4) == 1327 && U16(area, 6) == 234 && area[8] == direction &&
                scene.AsSpan(0, 2).SequenceEqual(new byte[] { 38, 0 }) && U16(scene, 2) == 1 && U16(scene, 4) == peer.Id &&
                U16(scene, 6) == 1327 && U16(scene, 8) == 234,
                "真实成功ACK后23/24沿原旅行投影发送目的地，不只回成功或伪造124");
            peer.Barrier();
        }
        string Facts(ushort id) => string.Join('\n',
            fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", id),
            fixture.Snapshot("SELECT * FROM character_teleport_cooldowns WHERE character_id=$p0", id),
            fixture.Snapshot("SELECT * FROM character_privilege_service WHERE character_id=$p0", id),
            fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", id),
            fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index", id),
            fixture.Snapshot("SELECT * FROM account_wallets WHERE account=(SELECT account FROM characters WHERE id=$p0)", id));
    }

    static byte[] Request(byte town, byte area, ushort x, ushort y)
    {
        var body = Enumerable.Repeat((byte)0xcd, 19).ToArray();
        body[13] = town; body[14] = area;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(15), x);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(17), y);
        return body;
    }
    sealed class Clock : TimeProvider
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(long milliseconds) => now = now.AddMilliseconds(milliseconds);
    }
}
