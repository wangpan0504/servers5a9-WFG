using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CreatureChangeTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var definition = resources.ItemResources.Items.CreatureChange(490705419);
        That(definition is { SourceItemId: -1, TotalWeight: 10000 } && definition.Select(0) == 400990451 &&
            definition.AllowedItems.SetEquals([400990236]), "真实PVF进化书的白名单及唯一结果均来自正式资源解析");
        var species = resources.ItemResources.Creatures;
        That(species.Get(400990236) is { Species: 10200, Egg: false } && species.Get(400990451) is { Species: 10410, Egg: false },
            "真实源和目标EQU关联不同种类，均是成宠而非宠物蛋");
        That(resources.Archive.ReadText("creature/chn_2019_NewYear/fox_hidden/creature.cre") ==
            resources.Archive.ReadText("creature/chn_2020_NewYear/fox_hidden/creature.cre"),
            "这组转换源目标CRE内容一致，换模板不更换原宠物外观资源");
        var clock = new ConsumableUseSessionTests.FakeClock();
        await using var fixture = new TcpTestFixture(options, Path.Combine(root,"creature-change"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("PetChange", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            InventoryItem Pet(short slot) => new(0, new(InventoryListType.Pet,slot), new(400990236, ItemKind.Creature,0)
                { EnchantCardId = 12345, EnchantUpgrade = 2, Extension27 = 87, SortLockFlag = 1, Extension114 = new(1,777) })
                { Creature = new() { Name = "old", Level = 3, Experience = 6, Satiety = 77, ExpireTime = 2000000000, SatietyFraction = 1200 } };
            repository.Create(owner, candidate with { Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                .Add(new(0, new(InventoryListType.Main,74), new(490705419,ItemKind.Consumable,2)))
                .Add(new(0, new(InventoryListType.Main,75), new(490705419,ItemKind.Consumable,1)))
                .Add(Pet(8)).Add(Pet(9)).Add(Pet(10))
                .Add(new(0,CreatureRules.EquippedSlot,new(63002,ItemKind.Creature,0)) { Creature = new() { Name = "keep" } }) } });
        }
        await fixture.StartAsync();
        var original = Read();
        using (var peer = Open(fixture))
        {
            Select(peer);
            string before = State();
            peer.Send(515, Request(74,8,reserved:1)); peer.Expect(1,515,[0,0,0,74,0,0,64]); peer.Barrier();
            peer.Send(515, Request(74,26,list:3)); peer.Expect(1,515,[0,0,0,74,0,0,64]); peer.Barrier();
            That(State() == before, "真实515错误参数和穿戴目标拒绝，不改当前宠物、书或revision，连接可继续");
            peer.Send(515, Request(74,8)); ExpectConverted(peer,74,8);
            var saved = Read();
            var oldPet = original.Inventory.Find(new(InventoryListType.Pet,8))!;
            That(saved.Inventory.Find(new(InventoryListType.Pet,8)) == oldPet with { Core = oldPet.Core with { ItemId = 400990451 } } &&
                saved.Inventory.Find(new(InventoryListType.Main,74))!.Core.Count == 1 &&
                saved.Inventory.Find(CreatureRules.EquippedSlot) == original.Inventory.Find(CreatureRules.EquippedSlot),
                "TCP原UID转换、完整附魔与详情保存，书仅扣一本且当前穿戴宠物不变");
            before = State();
            peer.Send(515, Request(74,8)); peer.Expect(1,515,[0,0,0,74,0,0,64]); peer.Barrier();
            That(State() == before, "重复请求已转换目标不再符合白名单，不能再次扣书");
            peer.Send(515, Request(74,9)); ExpectConverted(peer,74,9);
            That(Read().Inventory.Find(new(InventoryListType.Main,74)) is null, "最后一本ACK自行清源，不补源差量重复消费");
        }
        using (var peer = Open(fixture))
        {
            Select(peer); peer.Barrier();
            That(Read().Inventory.Find(new(InventoryListType.Pet,8))!.Core.ItemId == 400990451, "重新连接恢复原UID转换结果，不重新发放或消费");
            string before = State();
            fixture.Sql("CREATE TRIGGER fail_creature_change AFTER INSERT ON inventory_audit_log WHEN NEW.action_name='creature_change' " +
                "BEGIN SELECT RAISE(ABORT,'creature change rollback'); END");
            peer.Send(515, Request(75,10)); peer.Closed();
            That(State() == before, "目标模板、源扣除及详情已写后审计失败，整笔SQLite回滚且无成功ACK");
            fixture.Sql("DROP TRIGGER fail_creature_change");
        }
        using (var peer = Open(fixture))
        {
            Select(peer); peer.Send(515,Request(75,10)); ExpectConverted(peer,75,10);
            That(Read().Inventory.Find(new(InventoryListType.Main,75)) is null, "回滚后重连可合法再转换，不遗留占用");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restore");
        using (var peer = Open(fixture))
        {
            Select(peer); peer.Barrier();
            var restored = Read();
            That(new short[] { 8,9,10 }.All(slot => restored.Inventory.Find(new(InventoryListType.Pet,slot))!.Core.ItemId == 400990451),
                "重建服务恢复全部已提交转换，不把目标退回源模板");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "转换与失败恢复后数据库完整");

        Character Read()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repository.LoadCharacter(owner,0);
        }
        string State() => fixture.Snapshot("SELECT * FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_creature_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY rowid");
    }

    static byte[] Request(ushort source, uint target, uint list = 7, uint reserved = 0) => Build(writer =>
    { writer.Write(source); writer.Write((byte)0); writer.Write(reserved); writer.Write(64u); writer.Write(list); writer.Write(target); });

    static void Select(TcpPeer peer) => peer.Select(inventoryNotices: [(1,713),(1,713),(1,713)]);

    static void ExpectConverted(TcpPeer peer, ushort source, ushort target)
    {
        peer.Expect(1,515,[1,(byte)source,(byte)(source>>8),0,64,0,0,0]);
        byte[] empty = new byte[126];
        empty[0] = 7; empty[1] = 1; empty[3] = (byte)target; empty[4] = (byte)(target >> 8);
        Array.Fill(empty, (byte)255, 5, 4);
        peer.Expect(0,14,empty);
        peer.Expect(0,105);
        var changed = peer.Expect(0,14).Body;
        That(changed.Length == 3+123 && changed[0] == 7 && U16(changed,3) == target && U32(changed,5) == 400990451,
            "ACK后只清目标Pet槽、发105详情及完整123B核心，没有106或私有ACK尾部");
        peer.Expect(1,713);
        peer.Barrier();
    }
}
