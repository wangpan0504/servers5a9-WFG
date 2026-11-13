using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Chat;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class ChatTcpTests
{
    // 与本组共用只读PVF及临时数据库，不再冷启动服务；隔离账号用于直接复核零提交与旧身份。
    static void OfficialMegaphoneSessions(TcpTestFixture fixture, TcpTestResources resources, DailyBoosterTestClock clock)
    {
        var owners = new AccountOwners();
        using var owner = owners.Acquire("30011", () => { });
        var repository = new CharacterRepository(fixture.Database);
        var core = InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1120), 1);
        var inventory = new InventorySnapshot([
            new(0, new(InventoryListType.Main, 67), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(36), 1)),
            new(0, new(InventoryListType.Main, 71), core),
            new(0, new(InventoryListType.Main, 72), core with { EquipmentLockId = 1 }),
            new(0, new(InventoryListType.Main, 73), core with { ExpireTime = checked((int)clock.GetUtcNow().ToUnixTimeSeconds() + 20) })]);
        new AuthenticationService(new AuthenticationRepository(fixture.Database)).Create("30011", "fixture-password");
        repository.Create(owner, new NewCharacter("GMRule", resources.Towns.Room, resources.Towns.InitialReturn) { Inventory = inventory });
        repository.InitializeAdventure(owner, resources.Adventure, clock.GetUtcNow());
        fixture.Sql("UPDATE account_adventures SET name=$p0,name_initialized=1 WHERE account='30011'", new string('测', 16));
        That(fixture.Query("SELECT account,name,name_initialized FROM account_adventures WHERE account='30011'")
            is [["30011", string initializedName, 1L]] && initializedName == new string('测', 16),
            "隔离账号已实际建号及初始化一行32B冒险团，不以零行UPDATE冒充准备完成");
        var session = new GameSession(repository, resources.Towns, owner, entry: new(new(2, 10), 22, "[granfloris]"),
            items: resources.ItemResources.Items, timeProvider: clock);
        try
        {
            That(!session.CanUseOfficialChatMegaphone(71), "未选角不能使用1120");
            session.Select(0);
            That(!session.CanUseOfficialChatMegaphone(71), "1120保留原喇叭入城就绪门禁");
            session.MarkReady();
            var original = session.Selected!;
            string before = fixture.Snapshot();
            string auditBefore = fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY rowid");
            string inventoryBefore = fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid");
            That(session.CanUseOfficialChatMegaphone(71) && session.CanUseOfficialChatMegaphone(71),
                "普通非GM账号只要真实持有1120及合法频道即可重复使用，不猜IsGm权限");
            foreach (ushort slot in new ushort[] { 67, 68, 72, 65535 })
                That(!session.CanUseOfficialChatMegaphone(slot), "1120错物品、空槽、锁定及越界槽拒绝：" + slot);
            That(session.CanUseOfficialChatMegaphone(73), "未来期限1120可用");
            clock.Advance(TimeSpan.FromSeconds(21));
            That(!session.CanUseOfficialChatMegaphone(73), "1120到期不能因无限使用而继续激活");
            That(ReferenceEquals(session.Selected, original) && fixture.Snapshot() == before &&
                fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY rowid") == auditBefore &&
                fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") == inventoryBefore,
                "1120成功及拒绝均不替换角色快照、不推进revision、不写资产审计");

            ChatNotice? queued = null;
            session.ChatNoticeArrived = notice => queued = notice;
            var origin = session.ChatOrigin(includeAdventureName: true)!;
            That(origin.AdventureName == new string('测', 16), "owner内账号长名快照与角色/频道同时取得");
            session.TryDeliverChat(_ => true, new(ChatKind.OfficialMegaphone, origin.CharacterId, origin.ChannelNumber,
                Megaphone: new(origin.Name, "queued")
                { Channel = origin.Scene.Channel, Actor = origin.CharacterId, AdventureName = origin.AdventureName })
                { Origin = origin, Guard = origin.IsCurrent });
            That(queued?.Guard?.Invoke() == true, "当前身份的GM喇叭出队资格有效");
            session.ReturnToRoster();
            That(!session.CanUseOfficialChatMegaphone(71) && queued!.Guard!() == false,
                "返回选角后不能继续激活1120或发布旧GM喇叭");
            session.Select(0);
            session.MarkReady();
            That(!queued!.Guard!() && session.CanUseOfficialChatMegaphone(71),
                "重选同角色仅新epoch有效，已排队GM喇叭不复活");
            using var replacement = owners.Acquire("30011", () => { });
            Reject<InvalidOperationException>(() => session.CanUseOfficialChatMegaphone(71), "旧owner不能复用1120只读资格");
            That(!queued.Guard!() && queued.Megaphone!.AdventureName == new string('测', 16),
                "账号接管后旧GM消息守卫拒绝，旧消息账号名快照仍独立不被换角改写");
        }
        finally { session.End(); }

        using var nextOwner = owners.Acquire("30011", () => { });
        var fallback = new GameSession(repository, resources.Towns, nextOwner, entry: new(new(2, 10), 25, "[test]"),
            items: resources.ItemResources.Items, timeProvider: clock);
        try
        {
            fallback.Select(0);
            fallback.MarkReady();
            That(!fallback.CanUseOfficialChatMegaphone(71), "raw25在LOGIN发0，真实持有1120仍不能绕过频道门禁");
        }
        finally { fallback.End(); }
    }

    static void OfficialMegaphoneTcp(TcpTestFixture fixture, TcpPeer alpha, TcpPeer beta, TcpPeer gamma, TcpPeer delta,
        Action nextWindow, byte[] quote, byte[] expectedText, byte[] equipment, byte[] stackable)
    {
        var peers = new[] { alpha, beta, gamma, delta };
        void BarrierAll() { foreach (var peer in peers) peer.Barrier(); }
        byte[] Quoted(uint source, params byte[][] records) => Build(writer =>
        {
            writer.Write((byte)14);
            writer.Write((ushort)9999);
            writer.Write(source);
            writer.Write((uint)quote.Length);
            writer.Write(quote);
            writer.Write((byte)records.Length);
            foreach (var record in records) writer.Write(record);
        });
        string before = fixture.Snapshot();
        string auditBefore = fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY rowid");
        string inventoryBefore = fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid");
        nextWindow();
        foreach (uint slot in new uint[] { 68, 67, 65536 })
            alpha.Send(CommandSendMessage, SendBody(14, 9999, slot, "GM拒绝"));
        alpha.Send(CommandItemHyperlink, Quoted(67, equipment, stackable));
        BarrierAll(); // 严格屏障会拒绝任何多余广播、ACK或库存差量，不以等待超时推断静默。

        nextWindow();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string text = "无限GM" + attempt;
            alpha.Send(CommandSendMessage, SendBody(14, 9999, 71, text));
            foreach (var peer in peers)
                Same(peer.Expect(0, NoticeMegaphone).Body, MegaphoneBody(14, "ChatA", text, 2, 10, alpha.Id, AlphaAdventureTarget),
                    "1120重复文字保持14与真实来源，投递同分组的其他频道");
            BarrierAll();
        }
        alpha.Send(CommandItemHyperlink, Quoted(71, equipment, stackable));
        foreach (var peer in peers)
            Same(peer.Expect(0, 372).Body, LinkedMegaphoneBody(14, alpha.Id, 10, "ChatA", expectedText, AlphaAdventureTarget, equipment, stackable),
                "1120引用使用原372头、真实发言人和完整148B记录，不扣源");
        BarrierAll();
        alpha.Send(CommandItemHyperlink, Quoted(71));
        foreach (var peer in peers)
            Same(peer.Expect(0, NoticeMegaphone).Body, MegaphoneBody(14, "ChatA", Text.GetString(expectedText), 2, 10, alpha.Id, AlphaAdventureTarget),
                "1120记录不足仅降级118/type14，不改普通喇叭或聊天");
        BarrierAll();

        nextWindow();
        for (int attempt = 0; attempt < 6; attempt++)
            alpha.Send(CommandSendMessage, SendBody(14, 9999, 71, "GM限频" + attempt));
        for (int attempt = 0; attempt < ChatRateLimiter.ShortWindowLimit; attempt++)
            foreach (var peer in peers)
                Same(peer.Expect(0, NoticeMegaphone).Body, MegaphoneBody(14, "ChatA", "GM限频" + attempt, 2, 10, alpha.Id, AlphaAdventureTarget),
                    "无限使用不绕过原每连接限频");
        BarrierAll();
        That(fixture.Snapshot() == before && fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY rowid") == auditBefore &&
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") == inventoryBefore,
            "GM14真实文字、引用、降级和拒绝不推进角色revision或资产审计");
        var row = fixture.Query("SELECT item_core FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) " +
            "WHERE s.list_type=0 AND s.slot_index=71 AND s.character_id=$p0", alpha.Id).Single();
        var core = System.Text.Json.JsonSerializer.Deserialize<ItemCore>((string)row[0])!;
        That(core.ItemId == 1120 && core.Count == 1, "1120成功重复发送后源槽仍精确保留一件");
    }
}
