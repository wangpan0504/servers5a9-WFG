using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class ConsumableUseTcpTests
{
    public static async Task RunEffectsAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var clock = new DailyBoosterTestClock(new(DateTimeOffset.UtcNow.Date, TimeSpan.Zero));
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "consumable-effects"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var dose = Create(owner, "TimedDose", 40, [(10087825,3),(2600020,2)]);
            repository.CommitInventory(owner, dose, new(dose.Inventory, dose.Inventory with
                { StaminaUntil = clock.GetUtcNow().ToUnixTimeSeconds()+600 }, "timed_weakness_fixture"));
            Create(owner, "GiftCaster", 70, [(7789,1)]);
        }
        using (var owner = owners.Acquire("beta", () => { })) Create(owner, "GiftTarget", 1, []);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            Use(peer,65,0,10087825); Accepted(peer,65,0,10087825); peer.Expect(0,4,[100]); peer.Barrier();
            That(fixture.Query("SELECT stamina_until FROM characters WHERE id=$p0",peer.Id) is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_consumable_states WHERE character_id=$p0",peer.Id) is [[2L]],
                "真实44先成功消费再恢复虚弱，效果与冷却在同一库中保存");
            string states = fixture.Snapshot("SELECT * FROM character_consumable_states ORDER BY character_id,kind,item_id");
            peer.Send(7); peer.Expect(1,7,[1]); peer.Expect(0,2); peer.Select();
            That(peer.RestoredConsumableStates.Select(packet => packet.Opcode).SequenceEqual(new ushort[] { 172,174 }) &&
                peer.RestoredConsumableStates.All(packet => packet.Body.Length == 9 && U32(packet.Body,1) == 10087825) &&
                fixture.Snapshot("SELECT * FROM character_consumable_states ORDER BY character_id,kind,item_id") == states,
                "同连接重新选角恢复172/174真实剩余时间，不重复续期或补发物品");
            Use(peer,65,1,10087825); Refused(peer,1,10087825); peer.Barrier();
            FormalDungeonTcpTests.EntryTown(peer); FormalDungeonTcpTests.Enter(peer); FormalDungeonTcpTests.Load(peer);
            long coins = Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0",peer.Id)[0][0]);
            Use(peer,66,0,2600020); Accepted(peer,66,0,2600020); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_consumable_states WHERE character_id=$p0 AND kind=0 AND item_id=2600020",peer.Id) is [[1L]],
                "神圣祝福药水实际扣一件并登记单次复活，不冒充普通HP效果");
            peer.Send(40,[0x74,0,4,1,0,0]);
            peer.Expect(0,32,[(byte)peer.Id,(byte)(peer.Id>>8),0,0]);
            peer.Expect(0,32,[(byte)peer.Id,(byte)(peer.Id>>8),2,0]); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_consumable_states WHERE character_id=$p0 AND kind=0 AND item_id=2600020",peer.Id) is [[0L]] &&
                Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0",peer.Id)[0][0]) == coins,
                "死亡先0后2恢复三分之一血蓝，仅消费祝福资格，不扣复活币、不伪造41应答");
            peer.Send(40,[0x74,0,4,1,0,0]);
            peer.Expect(0,32,[(byte)peer.Id,(byte)(peer.Id>>8),0,0]); peer.Barrier();
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(fixture.Query("SELECT stamina_until FROM characters WHERE id=$p0",peer.Id) is [[0L]],
                "实际死亡回城后有效防虚弱药剂立即恢复，第二次死亡不会重复复活");
            FormalDungeonTcpTests.Enter(peer); FormalDungeonTcpTests.Load(peer);
            Use(peer,66,1,2600020); Accepted(peer,66,1,2600020); peer.Barrier();
            string beforeTimeout = fixture.Snapshot("SELECT revision,revive_coins FROM characters WHERE id=" + peer.Id) +
                fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id=" + peer.Id);
            clock.Advance(TimeSpan.FromMilliseconds(1199999)); peer.Barrier();
            clock.Advance(TimeSpan.FromMilliseconds(1)); peer.Expect(0,228,[]); peer.Barrier(); peer.Barrier();
            That(beforeTimeout == fixture.Snapshot("SELECT revision,revive_coins FROM characters WHERE id=" + peer.Id) +
                fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id=" + peer.Id),
                "祝福到期独立发送一次空正文228，不提前清理、不发币应答或再次修改资产");
            peer.Send(40,[0x74,0,4,1,0,0]);
            peer.Expect(0,32,[(byte)peer.Id,(byte)(peer.Id>>8),0,0]); peer.Barrier();
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0",peer.Id)[0][0]) == coins,
                "祝福恰到期后死亡回到普通流程，不误用过期药效或扣复活币");
        }
        using (var source = Open(fixture))
        using (var target = Open(fixture,"beta"))
        {
            source.Select(1); target.Select();
            source.Send(1426); source.Expect(0,23); source.Expect(0,24); source.Barrier();
            target.Send(1426);
            target.Batch((0,23),(0,24),(0,2),(0,3),(0,23),(0,22)); target.Barrier();
            source.Batch((0,2),(0,3),(0,23),(0,22)); source.Barrier();
            string receiver = fixture.Snapshot("SELECT * FROM characters WHERE account='beta'") +
                fixture.Snapshot("SELECT * FROM character_consumable_states WHERE account='beta'");
            Use(source,65,0,7789); Accepted(source,65,0,7789);
            source.Expect(0,260,[(byte)source.Id,(byte)(source.Id>>8),0x74,0x1E,0,0]); source.Barrier();
            target.Expect(0,257,[1,0x74,0x1E,0,0,0x10,0x0E,0,0]);
            target.Expect(0,260,[(byte)source.Id,(byte)(source.Id>>8),0x74,0x1E,0,0]); target.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=65",source.Id) is [[1L]] &&
                fixture.Query("SELECT count(*) FROM character_consumable_states WHERE character_id=$p0 AND kind=1 AND item_id=7789",source.Id) is [[1L]] &&
                receiver == fixture.Snapshot("SELECT * FROM characters WHERE account='beta'") +
                    fixture.Snapshot("SELECT * FROM character_consumable_states WHERE account='beta'"),
                "无限使用礼物保留原物品、实际保存十二小时冷却，周边短期增益不变成收件人的物品或离线资产");
            Use(source,65,1,7789); Refused(source,1,7789); source.Barrier(); target.Barrier();
        }

        Character Create(AccountOwners.Lease owner, string name, byte level, (int Id,int Count)[] contents)
        {
            var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var growth = SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
            var learning = SkillPointLedger.Synchronize(0,0,level,candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(level) },resources.Learning);
            var items = candidate.Inventory.Items;
            for (int i=0;i<contents.Length;i++)
                items = items.Add(new(0,new(InventoryListType.Main,checked((short)(65+i))),
                    InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(contents[i].Id),contents[i].Count)));
            return repository.Create(owner,candidate with { Level=level,InitialLearning=learning,Inventory=candidate.Inventory with { Items=items } });
        }
    }
}
