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
            Create(owner, "EpicDose", 95, [(490000413,3),(490002458,2),(490004594,2),(10004688,2),(2600025,2),(10007343,2)]);
            var variant = Create(owner, "WeakVariant", 40, [(490003621,3)]);
            Create(owner, "StarDose", 95, [(490704393,3)]);
            Create(owner, "VillageDose", 1, [(490006630,3),(10306776,2)]);
            repository.CommitInventory(owner, variant, new(variant.Inventory, variant.Inventory with
                { StaminaUntil = clock.GetUtcNow().ToUnixTimeSeconds()+600 }, "variant_weakness_fixture"));
        }
        using (var owner = owners.Acquire("beta", () => { })) Create(owner, "GiftTarget", 1, []);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            Use(peer,65,0,490000413); Accepted(peer,65,0,490000413); peer.Barrier();
            long until = clock.GetUtcNow().ToUnixTimeMilliseconds() + 600000;
            That(fixture.Query("SELECT kind,until_ms FROM character_consumable_states WHERE character_id=$p0 ORDER BY kind",peer.Id)
                    .Select(row => (Convert.ToInt32(row[0]), Convert.ToInt64(row[1]))).SequenceEqual(new[] { (0,until),(1,until) }),
                "史诗Buff药剂真实44成功后同事务保存十分钟效果和冷却");
            string snapshot = fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id=" + peer.Id);
            Use(peer,65,1,490000413); Refused(peer,1,490000413); peer.Barrier();
            That(fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id=" + peer.Id) == snapshot,
                "史诗药新token冷却拒绝不重扣、不续期");
            peer.Send(7); peer.Expect(1,7,[1]); peer.Expect(0,2); peer.Select(2);
            That(peer.RestoredConsumableStates.Select(packet => packet.Opcode).SequenceEqual(new ushort[] { 172,174 }) &&
                fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id=" + peer.Id) == snapshot,
                "史诗药重选恢复实际剩余时限，不再扣源或重新起算");
            Use(peer,66,2,490002458); Accepted(peer,66,2,490002458);
            Replacement(peer,490000413,490002458); peer.Barrier();
            That(fixture.Query("SELECT item_id FROM character_consumable_states WHERE character_id=$p0 AND kind=0",peer.Id) is [[490002458L]],
                "同类固定史诗药替换旧效果，不叠加多份掉率");
            foreach (var (slot, id, duration) in new[] { (67,490004594,1800000), (68,10004688,180000) })
            {
                Use(peer,(ushort)slot,3,id); Accepted(peer,(ushort)slot,3,id); peer.Barrier();
                long end = clock.GetUtcNow().ToUnixTimeMilliseconds()+duration;
                That(fixture.Query("SELECT kind,until_ms FROM character_consumable_states WHERE character_id=$p0 AND item_id=$p1 ORDER BY kind",peer.Id,id)
                        .Select(row => (Convert.ToInt32(row[0]),Convert.ToInt64(row[1]))).SequenceEqual(new[] { (0,end),(1,end) }) &&
                    fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) " +
                        "WHERE s.character_id=$p0 AND list_type=0 AND slot_index=$p1",peer.Id,slot) is [[1L]],
                    "真实44强化或增幅药剂扣一瓶并保存各自三十分钟、三分钟效果与冷却："+id);
            }
            string upgrades = fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id="+peer.Id);
            peer.Send(7); peer.Expect(1,7,[1]); peer.Expect(0,2); peer.Select(2);
            foreach (var (id, seconds) in new[] { (490004594,1800), (10004688,180) })
                That(peer.RestoredConsumableStates.Select(packet => packet.Opcode).SequenceEqual(new ushort[] { 172,174 }) &&
                    peer.RestoredConsumableStates.All(packet => Enumerable.Range(0,packet.Body[0]).Any(index =>
                        U32(packet.Body,1+index*8) == (uint)id && U32(packet.Body,5+index*8) == (uint)seconds)),
                    "升级药剂重选沿原生172/174整表恢复各自真实剩余秒数："+id);
            That(fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id="+peer.Id) == upgrades,
                "真实双模式重选保持原截止和库存，不续期、不清另一模式或重扣");
            foreach (var (slot, id, duration, cooldown, previous) in new[]
                { (69,2600025,1800000,1800000,490004594), (70,10007343,30000,60000,2600025) })
            {
                Use(peer,(ushort)slot,4,id); Accepted(peer,(ushort)slot,4,id);
                Replacement(peer,previous,id); peer.Barrier();
                long at = clock.GetUtcNow().ToUnixTimeMilliseconds();
                That(fixture.Query("SELECT kind,until_ms FROM character_consumable_states WHERE character_id=$p0 AND item_id=$p1 ORDER BY kind",peer.Id,id)
                        .Select(row => (Convert.ToInt32(row[0]),Convert.ToInt64(row[1]))).SequenceEqual(new[] { (0,at+duration),(1,at+cooldown) }) &&
                    fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) " +
                        "WHERE s.character_id=$p0 AND list_type=0 AND slot_index=$p1",peer.Id,slot) is [[1L]],
                    "炼金强化药真实44替换同族效果、扣一瓶并分别保存自身药效和冷却："+id);
            }
            That(fixture.Query("SELECT item_id FROM character_consumable_states WHERE character_id=$p0 AND kind=0 ORDER BY item_id",peer.Id)
                    .Select(row => Convert.ToInt32(row[0])).SequenceEqual(new[] { 10004688,10007343,490002458 }),
                "水晶之光替换旧强化药，仍保留另一模式增幅及史诗药效");
            string alchemist = fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id="+peer.Id);
            peer.Send(7); peer.Expect(1,7,[1]); peer.Expect(0,2); peer.Select(2);
            foreach (var (opcode, seconds) in new[] { ((ushort)172,60), ((ushort)174,30) })
                That(peer.RestoredConsumableStates.Any(packet => packet.Opcode == opcode &&
                    Enumerable.Range(0,packet.Body[0]).Any(index => U32(packet.Body,1+index*8) == 10007343 &&
                        U32(packet.Body,5+index*8) == (uint)seconds)),
                    "水晶之光重选按原截止恢复30秒药效及60秒冷却："+opcode);
            That(fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id="+peer.Id) == alchemist,
                "炼金药重选不续期、不重扣，也不重新起算同族效果");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(4);
            Use(peer,65,0,490704393); Accepted(peer,65,0,490704393);
            long until = clock.GetUtcNow().ToUnixTimeMilliseconds() + 300000;
            ExpectBossBuff(peer,1271,checked((int)(until / 1000))); peer.Barrier();
            That(fixture.Query("SELECT kind,until_ms FROM character_consumable_states WHERE character_id=$p0 ORDER BY kind",peer.Id)
                    .Select(row => (Convert.ToInt32(row[0]), Convert.ToInt64(row[1]))).SequenceEqual(new[] { (0,until),(1,until) }),
                "截图星空裂缝史诗Buff药剂真实44成功后同事务保存五分钟效果和冷却");
            string snapshot = fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id=" + peer.Id);
            Use(peer,65,1,490704393); Refused(peer,1,490704393); peer.Barrier();
            That(fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id=" + peer.Id) == snapshot,
                "星空药新token冷却拒绝不重扣、不续期");
            peer.Send(7); peer.Expect(1,7,[1]); peer.Expect(0,2);
            peer.Select(4, hellLegendUntil: checked((int)(until / 1000)));
            That(peer.RestoredConsumableStates.Select(packet => packet.Opcode).SequenceEqual(new ushort[] { 172,174 }) &&
                fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id=" + peer.Id) == snapshot,
                "星空药重选恢复实际剩余时限，不再扣源或重新起算");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(5);
            Use(peer,65,0,490006630); Accepted(peer,65,0,490006630); peer.Barrier();
            long until = clock.GetUtcNow().ToUnixTimeMilliseconds() + 300000;
            That(fixture.Query("SELECT kind,until_ms FROM character_consumable_states WHERE character_id=$p0 ORDER BY kind",peer.Id)
                    .Select(row => (Convert.ToInt32(row[0]), Convert.ToInt64(row[1]))).SequenceEqual(new[] { (0,until),(1,until) }),
                "城镇移速药剂真实44成功后同事务保存五分钟效果和冷却");
            string snapshot = fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id=" + peer.Id);
            Use(peer,65,1,490006630); Refused(peer,1,490006630); peer.Barrier();
            That(fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id=" + peer.Id) == snapshot,
                "城镇移速药新token冷却拒绝不重扣、不续期");
            Use(peer,66,2,10306776); Accepted(peer,66,2,10306776);
            Replacement(peer,490006630,10306776); peer.Barrier();
            That(fixture.Query("SELECT item_id FROM character_consumable_states WHERE character_id=$p0 AND kind=0",peer.Id) is [[10306776L]] &&
                fixture.Query("SELECT until_ms FROM character_consumable_states WHERE character_id=$p0 AND kind=0",peer.Id) is [[var replaced]] &&
                Convert.ToInt64(replaced) == clock.GetUtcNow().ToUnixTimeMilliseconds() + 1200000,
                "不同冷却组的同类城镇移速药替换旧效果，不叠加走速");
            peer.Send(7); peer.Expect(1,7,[1]); peer.Expect(0,2); peer.Select(5);
            That(peer.RestoredConsumableStates.Select(packet => packet.Opcode).SequenceEqual(new ushort[] { 172,174 }) &&
                peer.RestoredConsumableStates.All(packet => Enumerable.Range(0,packet.Body[0]).Any(index =>
                    U32(packet.Body,1+index*8) == 10306776 && U32(packet.Body,5+index*8) == 1200)),
                "城镇移速药重选沿原生172/174恢复替换后的剩余秒数");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(3);
            Use(peer,65,0,490003621); Accepted(peer,65,0,490003621); peer.Expect(0,4,[100]); peer.Barrier();
            That(fixture.Query("SELECT stamina_until FROM characters WHERE id=$p0",peer.Id) is [[0L]] &&
                fixture.Query("SELECT until_ms FROM character_consumable_states WHERE character_id=$p0 AND kind=0",peer.Id)
                    is [[var end]] && Convert.ToInt64(end) == clock.GetUtcNow().ToUnixTimeMilliseconds()+600000,
                "十分钟活动变体真实44立即解除虚弱并保存自己的十分钟药效");
            FormalDungeonTcpTests.EntryTown(peer); FormalDungeonTcpTests.Enter(peer); FormalDungeonTcpTests.Load(peer);
            peer.Send(40,[0x74,0,4,1,0,0]); peer.Expect(0,32,[(byte)peer.Id,(byte)(peer.Id>>8),0,0]); peer.Barrier();
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(fixture.Query("SELECT stamina_until FROM characters WHERE id=$p0",peer.Id) is [[0L]],
                "活动变体实际TCP死亡返城继续免虚弱，不仅发送成功应答");
        }
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

        byte[] EffectTimes(params (int Id,int Seconds)[] rows) => Build(w =>
        { w.Write((byte)rows.Length); foreach(var row in rows) { w.Write(row.Id); w.Write(row.Seconds); } });

        void Replacement(TcpPeer peer,int oldId,int newId)
        {
            That(resources.ItemResources.RecoveryConsumables.TryGet(oldId,out var previous),"替换通知按真实旧资源维护位核对");
            if((previous.EffectMaintenance & 8)==0)
            {
                That(resources.ItemResources.RecoveryConsumables.TryGet(newId,out _),"新编号已由普通成功ACK按真实模板起算");
                peer.Expect(0,174,EffectTimes((oldId,0)));
            }
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
