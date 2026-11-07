using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CreatureSealTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources.Items;
        That(items.CreatureCapsule(2683241, 63023) == 10002402 && items.CreatureCapsule(2683241, 63022) == 10002402 &&
            items.CreatureCapsule(2683241, 63144) == 10002395 && items.CreatureCapsule(2683241, 400990451) == 0,
            "真实普通封印表覆盖波比、玛巴斯蛋和成宠，排除活动宠物");
        That(items.Get(10002402) is { Kind: ItemKind.Consumable, StackLimit: 1, AccountCargoForbidden: true },
            "真实产物是不可入账号仓库的单件礼包");
        That(!InventoryCargoService.AllowsInsert(new(10002402, ItemKind.Consumable, 1) { Attr = 32 },
            InventoryListType.AccountCargo, items.Get(10002402)), "尚有交易次数的胶囊也不能进入账号仓库");
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "creature-seal"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            InventoryItem Pet(short slot, int id) => new(0, new(InventoryListType.Pet, slot), new(id, ItemKind.Creature, 0))
                { Creature = new() { Name = "old" } };
            repository.Create(owner, new("SealTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 25, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Bag(65), new(2683241, ItemKind.Consumable, 1)),
                    new(0, Bag(66), new(2683241, ItemKind.Consumable, 1)), Pet(0, 63023), Pet(1, 63144), Pet(2, 400990451)]) });
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string before = State();
            foreach (byte[] invalid in new[] { new byte[8], Request(0, 63022, 65), Request(2, 400990451, 65) })
            { peer.Send(1014, invalid); peer.Expect(1, 1014, [0,19,0]); peer.Barrier(); }
            That(State() == before, "畸形、错误模板和非名单宠物拒绝后连接可继续，资产不动");
            fixture.Sql("CREATE TRIGGER fail_seal AFTER INSERT ON inventory_audit_log WHEN NEW.action_name='creature_seal' " +
                "BEGIN SELECT RAISE(ABORT,'seal rollback'); END");
            peer.Send(1014, Request(0, 63023, 65)); peer.Closed();
            That(State() == before, "扣工具、删宠物、插入礼包后SQL失败整体回滚，没有成功ACK");
            fixture.Sql("DROP TRIGGER fail_seal");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            foreach (var (slot, id, source) in new[] { ((short)0, 63023, (short)65), ((short)1, 63144, (short)66) })
            {
                peer.Send(1014, Request(slot, id, source));
                byte[] expected = [1,0,0,0,0,0,0,(byte)slot,0,(byte)source,0,0,0,0,0];
                peer.Expect(1, 1014, expected);
                bool outputSeen = false;
                peer.Barrier(packet =>
                {
                    if (packet is { Kind: 0, Opcode: 14 } && packet.Body[0] == 0)
                        for (int at = 3; at < packet.Body.Length; at += 123)
                            if (U32(packet.Body, at + 2) == (slot == 0 ? 10002402 : 10002395)) outputSeen = true;
                });
                That(outputSeen, "ACK解锁后收到权威胶囊库存行");
                var saved = Read();
                That(saved.Inventory.Find(new(InventoryListType.Pet, slot)) is null && saved.Inventory.Items.Any(item =>
                    item.Core.ItemId == (slot == 0 ? 10002402 : 10002395) && item.Core.Attr == 32), "波比成宠与玛巴斯蛋真实转换为一次交易胶囊");
                string before = State();
                peer.Send(1014, Request(slot, id, source)); peer.Expect(1, 1014, [0,19,0]); peer.Barrier();
                That(State() == before, "重复1014不再扣道具或发产物");
            }
        }
        await fixture.StopAsync(); await fixture.StartAsync("restore");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            var saved = Read();
            That(saved.Inventory.Items.Count(item => item.Core.ItemId is 10002402 or 10002395) == 2 &&
                saved.Inventory.Find(new(InventoryListType.Pet, 2)) is { Core.ItemId: 400990451 }, "服务重建恢复两枚胶囊和未参与宠物");
            short capsuleSlot = saved.Inventory.Items.Single(item => item.Core.ItemId == 10002402).Position.Index;
            peer.Send(518, [(byte)capsuleSlot, (byte)(capsuleSlot >> 8), 0]);
            peer.Expect(1, 518, [1, (byte)capsuleSlot, (byte)(capsuleSlot >> 8)]);
            bool eggSeen = false;
            peer.Barrier(packet =>
            {
                That(packet.Kind == 0 && packet.Opcode is 14 or 105, "开封仅发布库存及宠物详情");
                if (packet.Opcode == 14 && packet.Body[0] == 7)
                    for (int at = 3; at < packet.Body.Length; at += 123)
                        if (U32(packet.Body, at + 2) == 63022) eggSeen = true;
            });
            That(eggSeen && Read().Inventory.Items.Any(item => item.Core.ItemId == 63022 && item.Creature is not null) &&
                Read().Inventory.Items.All(item => item.Core.ItemId != 10002402), "实际518消费胶囊并创建资源指定波比蛋，详情与UID合法");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "封印成功及回滚后数据库完整");
        Character Read() { using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }); return repository.LoadCharacter(owner, 0); }
        string State() => fixture.Snapshot("SELECT * FROM characters ORDER BY id") + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_creature_details ORDER BY item_uid") + fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
        static byte[] Request(short slot, int id, short source)
        {
            var bytes = Enumerable.Repeat((byte)0xCD, 21).ToArray();
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(13), slot);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(15), id);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(19), source);
            return bytes;
        }
    }
}
