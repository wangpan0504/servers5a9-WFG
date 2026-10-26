using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class AvatarAbilityChangeTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources;
        That(items.Items.Get(2660452).AvatarAbilityChange is { Grade: 3, IncludesLower: false } &&
            items.Items.Get(2660834).AvatarAbilityChange is { Grade: 2, IncludesLower: true } &&
            items.Items.Get(42605) is { Grade: 3, Rarity: 2 } && items.Items.Get(401520098).Grade == 2 &&
            new[] { 42605, 401520098 }.All(id => items.AvatarPackages.GetAvatar(id).Abilities.IsSupersetOf([0, 1])),
            "#801两套真实源、目标grade和可选属性来自匹配PVF，rarity不能代替grade");
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "avatar-ability-change"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            created = repo.Create(owner, new("AvatarOpt", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 0, Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([Tool(116, 2660452), Tool(115, 2660834), Avatar(42, 42605), Avatar(36, 401520098)], Gold: 1234)
            });
        var rare = created.Inventory.Find(new(InventoryListType.Avatar, 42))!;
        var ordinary = created.Inventory.Find(new(InventoryListType.Avatar, 36))!;
        await fixture.StartAsync();
        byte[] rareRequest = Convert.FromHexString("7400649828002A006DA6000000");
        byte[] ordinaryRequest = Convert.FromHexString("7300E29928002400E2B5EE1700");
        using (var peer = Open(fixture))
        {
            string before = Snapshot();
            peer.Send(459, rareRequest); peer.Expect(1, 459, [0, 0, 0]);
            That(before == Snapshot(), "未选角459不消费源或伪造成功");
            peer.Select(); peer.Barrier();
            foreach (byte[] request in new[] { rareRequest[..^1], rareRequest.Concat(new byte[1]).ToArray(),
                Request(116, 2660834, 42, 42605, 0), Request(115, 2660834, 42, 42605, 0), Request(116, 2660452, 42, 42605, 255) })
            {
                before = Snapshot();
                peer.Send(459, request); peer.Expect(1, 459, [0, 0, 0]); peer.Barrier();
                That(before == Snapshot(), "畸形包、错误源号、目标grade和越界属性拒绝且连接可继续");
            }
            Apply(peer, rareRequest, 116, 2660452, 1, 42, 0);
            That(Reload().Inventory.Find(rare.Position) == rare with { Core = rare.Core with { Durability = 0 } },
                "真实稀有克隆装扮仅改属性，UID、打造、孔位、颜色与期限完整保留");
            Apply(peer, Request(116, 2660452, 42, 42605, 1), 116, -1, 0, 42, 1);
            before = Snapshot();
            peer.Send(459, rareRequest); peer.Expect(1, 459, [0, 0, 0]); peer.Barrier();
            That(before == Snapshot(), "稀有调整器末件清槽后重放拒绝，不再改属性或revision");
            fixture.Sql("CREATE TRIGGER fail_avatar_ability AFTER INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='avatar_ability_change' BEGIN SELECT RAISE(ABORT,'ability rollback'); END");
            peer.Send(459, ordinaryRequest); peer.Closed();
            That(before == Snapshot(), "高级装扮转换晚期SQL失败整体回滚，未提前发送成功");
            fixture.Sql("DROP TRIGGER fail_avatar_ability");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            Apply(peer, ordinaryRequest, 115, 2660834, 1, 36, 0);
            That(Reload().Inventory.Find(ordinary.Position) == ordinary with { Core = ordinary.Core with { Durability = 0 } },
                "真实高级装扮与相应调整器沿同一事务正确消费并保留完整实例");
            Apply(peer, Request(115, 2660834, 36, 401520098, 1), 115, -1, 0, 36, 1);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            var full = peer.SpecialContainers.Single(packet => packet.Body[0] == 1).Body;
            That(full.Length == 5 + 165 * U16(full, 3), "重连装扮全量保留165字节实例行");
            foreach (short slot in new short[] { 36, 42 })
            {
                int at = 5 + 165 * Enumerable.Range(0, U16(full, 3)).Single(index => U16(full, 5 + 165 * index) == slot);
                That(U16(full, at + 11) == 1, "重连从持久核心恢复所选属性，不依赖窗口临时状态");
            }
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "装扮属性转换数据库与详情外键完整");

        InventoryItem Tool(short slot, int id) => new(0, new(InventoryListType.Main, slot), InventoryRewardGrantService.Create(items.Items.Get(id), 2));
        InventoryItem Avatar(short slot, int id)
        {
            var item = InventoryRewardGrantService.CreateItem(items.Items.Get(id), new(InventoryListType.Avatar, slot),
                InventoryRewardGrantService.Create(items.Items.Get(id)) with { Durability = 1, Attr = 7, Extension119 = 3 });
            return item with { Avatar = item.Avatar! with { Color1 = 7, ExpireTime = int.MaxValue, Socket0 = AvatarSocket.Empty(2) } };
        }
        Character Reload()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repo.LoadCharacter(owner, created.Slot);
        }
        string Snapshot() => fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") + fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
        static void Apply(TcpPeer peer, byte[] request, short sourceSlot, int sourceId, int sourceCount, short avatarSlot, byte ability)
        {
            peer.Send(459, request);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, sourceSlot, sourceId, sourceCount);
            peer.Expect(1, 459, [1, (byte)avatarSlot, (byte)(avatarSlot >> 8), ability]); peer.Barrier();
        }
    }

    static byte[] Request(short sourceSlot, int sourceId, short avatarSlot, int avatarId, byte ability) => Build(writer =>
    {
        writer.Write(sourceSlot); writer.Write(sourceId); writer.Write(avatarSlot); writer.Write(avatarId); writer.Write(ability);
    });
}
