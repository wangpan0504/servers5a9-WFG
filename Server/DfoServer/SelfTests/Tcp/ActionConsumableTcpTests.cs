using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ActionConsumableTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new ConsumableUseSessionTests.FakeClock();
        var assets = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"action-consumables"),resources:resources,clock:clock);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        That(assets.Items.ActionConsumable(10002998) is { Label:"[rent point]", Parameters:[1] } &&
            assets.Items.ActionConsumable(10015551) is { Label:"[repair equipment]" } &&
            assets.Items.CreatureChange(2683091) is { Random:true, Candidates.Length:6 } &&
            assets.Items.ActionConsumable(10156153) is { EffectMilliseconds:600000 }, "实际PVF按动作而非开发样品ID注册消费");
        That(assets.Creatures.RenameCard(25) && !assets.Creatures.RenameCard(10005895),
            "宠物经验胶囊不能借同一stackable子型充当改名卡");
        That(assets.Items.ActionConsumable(10015484) is { Parameters: [0,1] } extension && extension.AllowedTargets.Contains(100360287),
            "当前延期券从动作、天数和真实白名单接入，不按道具ID补通用成功");
        long targetUid;
        ItemCore targetCore;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount,()=>{}))
        {
            var petTemplate = assets.Items.Get(63220);
            var pet = InventoryRewardGrantService.CreateItem(petTemplate,new(InventoryListType.Pet,0),
                InventoryRewardGrantService.Create(petTemplate,shopExpiration:true,utcNow:clock.GetUtcNow()));
            pet = pet with { Creature = pet.Creature! with { Name="Potential" } };
            var equipment = InventoryRewardGrantService.Create(assets.Items.Get(10400)) with { Durability=0 };
            var created = repo.Create(owner,new("ActionTcp",resources.Towns.Room,resources.Towns.InitialReturn)
            {
                Level=60,AcceptedStoryLevel=1,Skills=resources.Characters.Get(0).Skills,
                Inventory=new([new(0,new(InventoryListType.Main,9),equipment),
                    new(0,new(InventoryListType.Equipment,14),equipment),
                    Item(65,10002998),Item(66,8055),Item(67,10015551),Item(68,2683091),Item(69,10156153),
                    Item(70,10015484),new(0,new(InventoryListType.Main,10),InventoryRewardGrantService.Create(
                        assets.Items.Get(100360287),utcNow:clock.GetUtcNow())),pet],Gold:100000)
            });
            var target = created.Inventory.Find(new(InventoryListType.Main,10))!;
            targetUid=target.Uid; targetCore=target.Core;
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            Extend(peer,targetCore.ExpireTime+86400);
            That(Count(70)==1,"第一次延期扣一张券，不要求或消费STK商店兑换材料");
            Use(peer,65,106); Ack(peer,65,106);
            var balance = peer.Expect(0,988).Body;
            That(U32(balance,0)==1 && Count(65)==1 && fixture.Query("SELECT rental_points FROM account_wallets") is [[1L]],
                "ACK515消费一件，988投影同次提交的账号租赁点，金币不扣");
            peer.Barrier();
            Repair(peer,9,66); peer.Expect(1,23,Build(w=>{w.Write((byte)1);w.Write(100000);w.Write((byte)0);w.Write((short)9);w.Write((ushort)0);}));
            peer.Expect(0,13); peer.Expect(0,14); peer.Barrier();
            That(Count(66)==1 && fixture.Query("SELECT gold FROM characters") is [[100000L]],"单件修理券消费与耐久同事务且不扣金币");
            Repair(peer,-1,67); peer.Expect(1,23); peer.Expect(0,13); peer.Expect(0,14); peer.Expect(0,14);
            peer.Expect(0,2); peer.Expect(0,2); peer.Barrier();
            That(Count(67)==1 && fixture.Query("SELECT gold FROM characters") is [[long gold]] && gold<100000,
                "修复剂消费一件，整批修理仍正常按已有修理表扣金币");
            long petUid = (long)fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE list_type=7 AND slot_index=0")[0][0]!;
            Use(peer,68,87,7,0); Ack(peer,68,87); peer.Expect(0,14); peer.Expect(0,105); peer.Expect(0,14); peer.Barrier();
            That(Count(68)==1 && fixture.Query("SELECT json_extract(item_core,'$.ItemId') FROM character_inventory_items WHERE item_uid=$p0",petUid)
                is [[long id]] && id is >=2747871 and <=2747876 &&
                fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE list_type=7 AND slot_index=0") is [[var same]] && Equals(same,petUid),
                "潜能药只在PVF完整权重池抽取并保留原宠物UID");
            Use(peer,69,151); Ack(peer,69,151); peer.Barrier();
            That(Count(69)==1 && fixture.Query("SELECT count(*) FROM character_consumable_states WHERE item_id=10156153 AND kind=0") is [[1L]],
                "宠物效果源消费和六十万毫秒时限进入原持续账本");
            string current=Snapshot(); peer.Send(1241,[]); peer.Barrier();
            That(Snapshot()==current,"无源和版本的提前/迟到到期通知不能撤销仍有效的新效果");
            clock.Advance(TimeSpan.FromMilliseconds(600001));
            peer.Send(1241,[]); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_consumable_states WHERE item_id=10156153 AND kind=0") is [[0L]],
                "原生到期事件只清已到期资格，不重复扣源");
            fixture.Sql("CREATE TRIGGER fail_action_item BEFORE INSERT ON inventory_audit_log WHEN NEW.action_name='rental_point_item' BEGIN SELECT RAISE(ABORT,'action rollback'); END");
            string before=Snapshot(); Use(peer,65,106); peer.Closed();
            That(Snapshot()==before,"晚期SQL失败回滚源数量、账号租赁点、冷却、revision及审计，不先发成功");
            fixture.Sql("DROP TRIGGER fail_action_item");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();Use(peer,65,106);Ack(peer,65,106);peer.Expect(0,988);peer.Barrier();
            That(Count(65)==0 && fixture.Query("SELECT rental_points FROM account_wallets") is [[2L]],
                "重连只读取已提交状态，最后一件按真实成功清槽，不补发失败奖励");
        }
        fixture.Sql("CREATE TRIGGER fail_period_extension BEFORE INSERT ON inventory_audit_log WHEN NEW.action_name='item_period_extended' " +
            "BEGIN SELECT RAISE(ABORT,'extension rollback'); END");
        using (var peer=Open(fixture))
        {
            peer.Select();peer.Barrier();string before=Snapshot();Use(peer,70,69,0,10);peer.Closed();
            That(Snapshot()==before && Count(70)==1,"延期晚期SQL失败回滚源券、目标期限、revision和冷却，不发成功ACK");
        }
        fixture.Sql("DROP TRIGGER fail_period_extension");
        using (var peer=Open(fixture))
        {
            peer.Select();Extend(peer,targetCore.ExpireTime+2*86400);
            That(Count(70)==0,"最后一张延期券正常清槽，重连保留上次截止而非从当前时间重新计算");
            Use(peer,70,69,0,10);peer.Expect(1,515,[0,0,0,70,0,0,69]);peer.Barrier();
            That(Count(70)==0 && ReadTarget().ExpireTime==targetCore.ExpireTime+2*86400,"源已耗尽的重放不再次延期，也不重复发送扣源成功ACK");
        }
        PeriodChange(fixture, resources, clock, repo, owners);
        RidingConsumable(fixture, resources, clock, repo, owners);
        TitleChange(fixture, resources, clock, repo, owners);
        await fixture.StopAsync();

        ItemCore ReadTarget() => JsonSerializer.Deserialize<ItemCore>((string)fixture.Query(
            "SELECT item_core FROM character_inventory_items WHERE item_uid=$p0",targetUid)[0][0]!)!;
        void Extend(TcpPeer peer,int expires)
        {
            Use(peer,70,69,0,10);Ack(peer,70,69);
            var clear=peer.Expect(0,1681).Body;
            That(clear.Length==126 && clear[0]==0 && U16(clear,1)==1 && U16(clear,3)==10 && U32(clear,5)==uint.MaxValue,
                "原生69先消费ACK，再无播报清理目标缓存，不对源再次发数量差量");
            var update=peer.Expect(0,1681).Body;
            That(update.Length==126 && update[0]==0 && U16(update,3)==10 && U32(update,5)==100360287 && U32(update,59)==(uint)expires,
                "目标完整123字节行在独立期限偏移56同步实际截止，不追加私有ACK尾部");
            peer.Barrier();
            That(ReadTarget()==targetCore with { ExpireTime=expires },"延期保留目标UID、完整属性及原位置，只修改实际期限");
        }

        InventoryItem Item(short slot,int id) => new(0,new(InventoryListType.Main,slot),
            InventoryRewardGrantService.Create(assets.Items.Get(id),2,shopExpiration:true,utcNow:clock.GetUtcNow()));
        long Count(short slot) => fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid WHERE s.list_type=0 AND s.slot_index=$p0",slot)
            is [[long value]] ? value : 0;
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid"))+
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM account_wallets ORDER BY account"))+
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_consumable_states ORDER BY character_id,item_id,kind"))+
            fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id");
    }

    static void PeriodChange(TcpTestFixture fixture, TcpTestResources resources,
        ConsumableUseSessionTests.FakeClock clock, CharacterRepository repo, AccountOwners owners)
    {
        const int designId = 490010790;
        var items = resources.ItemResources.Items;
        var definition = items.EquipmentPeriodChange(designId)!;
        That(definition is not null, "真实制作图沿已注册action136解析成对升级表");
        var route = definition!.Upgrades.Single(row => row.Key == 100060659);
        That(route.Value == 100050802 && items.Get(route.Key).Durability == 33 && items.Get(route.Value).Durability == 28,
            "真实action136存在33到28的耐久上限下降，不把仅保留旧耐久视为合法换模板");
        var source = InventoryRewardGrantService.Create(items.Get(designId), utcNow: clock.GetUtcNow(), shopExpiration: true)
            with { ExpireTime = 0 };
        var weapon = InventoryRewardGrantService.Create(items.Get(route.Key), utcNow: clock.GetUtcNow(), shopExpiration: true)
            with { Attr = 11, GenuineUpgrade = 3, ExpireTime = 0 };
        int expires = checked((int)clock.GetUtcNow().ToUnixTimeSeconds() + 10);
        DfoServer.Game.Characters.Character role;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            role = repo.Create(owner, new("PeriodTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, new(InventoryListType.Main, 65), source),
                    new(0, new(InventoryListType.Main, 66), source),
                    new(0, new(InventoryListType.Main, 9), weapon),
                    new(0, new(InventoryListType.Main, 10), weapon with { ExpireTime = expires })])
            });
        using var peer = Open(fixture);
        peer.Select(role.Slot);
        Use(peer, 65, 136, 0, 9); Ack(peer, 65, 136);
        peer.Expect(0, 1681); peer.Expect(0, 1681); peer.Barrier();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.LoadCharacter(owner, role.Slot);
            That(current.Inventory.Find(new(InventoryListType.Main, 9)) ==
                role.Inventory.Find(new(InventoryListType.Main, 9))! with
                    { Core = weapon with { ItemId = route.Value, Durability = 28 } } &&
                current.Inventory.Find(new(InventoryListType.Main, 65)) is null &&
                current.Inventory.Find(new(InventoryListType.Main, 66))!.Core.Count == 1,
                "真实515制作图消费一件，产物沿用UID、位置与全部打造，不扣商店材料");
        }
        clock.Advance(TimeSpan.FromSeconds(10));
        int expiredNotices = 0;
        peer.Barrier(packet =>
        {
            That(packet is { Kind: 0, Opcode: 14 } && ++expiredNotices == 1,
                "请求前服务日投影只允许一次目标到期清包，不跳过其他业务通知");
            Bytes(packet.Body, Build(writer =>
            {
                writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)10);
                writer.Write(-1); writer.Write(new byte[117]);
            }), "在线到期只清除10槽的完整库存行，不清制作图源或9槽永久产物");
        });
        That(expiredNotices == 1 && fixture.Query("SELECT slot_index FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 ORDER BY slot_index", role.Id)
                .Select(row => (long)row[0]).SequenceEqual(new long[] { 9, 66 }) &&
            fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE character_id=$p0 AND action_name='item_expired'", role.Id) is [[1L]],
            "先核对新基线真实过期清包及审计，保留源和永久产物，再隔离验证制作请求拒绝");
        string before = fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT id,revision FROM characters ORDER BY id") + fixture.Snapshot("SELECT * FROM inventory_audit_log");
        Use(peer, 66, 136, 0, 10); peer.Expect(1, 515, [0, 0, 0, 66, 0, 0, 136]); peer.Barrier();
        That(before == fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT id,revision FROM characters ORDER BY id") + fixture.Snapshot("SELECT * FROM inventory_audit_log"),
            "目标恰到期的真实请求拒绝且连接可继续，不扣源、不写产物或审计");
    }

    static void RidingConsumable(TcpTestFixture fixture, TcpTestResources resources,
        ConsumableUseSessionTests.FakeClock clock, CharacterRepository repo, AccountOwners owners)
    {
        const int potionId = 490702204;
        var items = resources.ItemResources.Items;
        That(items.ActionConsumable(potionId) is { Label: "[riding]", Parameters: [107000902, 300000], CoolTime: 10000 } riding &&
            riding.AllowedDungeons is { } allowed && allowed.Contains(3),
            "真实PVF厄伽勒变身药水(490702204)按action100([riding] 107000902 300000)注册动作消费");
        var source = InventoryRewardGrantService.Create(items.Get(potionId), 2, utcNow: clock.GetUtcNow(), shopExpiration: true)
            with { ExpireTime = 0 };
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var candidate = DfoServer.Game.Characters.CharacterCreation.Prepare("RidingTcp", resources.Characters.Get(0),
            resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
        var learning = DfoServer.Game.Skills.SkillPointLedger.Synchronize(0, 0, 60,
            candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(60) }, resources.Learning);
        DfoServer.Game.Characters.Character role;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            role = repo.Create(owner, candidate with
            {
                Level = 60,
                InitialLearning = learning,
                Inventory = candidate.Inventory with
                {
                    Items = candidate.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65), source))
                }
            });

        long SlotCount() => fixture.Query(
            "SELECT json_extract(item_core,'$.Value') FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid WHERE s.character_id=$p0 AND s.list_type=0 AND s.slot_index=65",
            role.Id) is [[long value]] ? value : 0;

        using var peer = Open(fixture);
        peer.Select(role.Slot);
        Use(peer, 65, 100); peer.Expect(1, 515, [0, 0, 0, 65, 0, 0, 100]); peer.Barrier();
        That(SlotCount() == 2, "城镇阶段拒绝厄伽勒变身药水(action100)，不扣源或写入冷却");

        FormalDungeonTcpTests.EntryTown(peer);
        FormalDungeonTcpTests.Enter(peer);
        Use(peer, 65, 100); peer.Expect(1, 515, [0, 0, 0, 65, 0, 0, 100]); peer.Barrier();
        That(SlotCount() == 2, "副本Loading阶段拒绝厄伽勒变身药水(action100)，不扣源");

        FormalDungeonTcpTests.Load(peer);
        Use(peer, 65, 100); Ack(peer, 65, 100); peer.Barrier();
        That(SlotCount() == 1 &&
            fixture.Query("SELECT count(*) FROM character_consumable_states WHERE character_id=$p0 AND kind=1", role.Id) is [[1L]] &&
            fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE character_id=$p0 AND action_name='action_consumable_source'", role.Id) is [[1L]],
            "正式副本内厄伽勒变身药水经ACK515(action100)消费1瓶并写入10秒冷却账本，不重复发送源槽NOTI14");

        Use(peer, 65, 100); peer.Expect(1, 515, [0, 0, 0, 65, 0, 0, 100]); peer.Barrier();
        That(SlotCount() == 1, "10秒冷却期内重复请求被拒绝且不额外扣药");

        clock.Advance(TimeSpan.FromSeconds(10));
        Use(peer, 65, 100); Ack(peer, 65, 100); peer.Barrier();
        That(SlotCount() == 0 &&
            fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=65", role.Id) is [[0L]],
            "冷却结束后第二瓶厄伽勒变身药水正常消费并清空源槽");
    }

    static void TitleChange(TcpTestFixture fixture, TcpTestResources resources,
        ConsumableUseSessionTests.FakeClock clock, CharacterRepository repo, AccountOwners owners)
    {
        const ushort opcode = 970;
        const int threadId = 10007724;
        const int gorgeousThreadId = 10007725;
        const int initialTitleId = 400330031;
        var items = resources.ItemResources.Items;
        var sealedTitle = InventoryRewardGrantService.Create(items.Get(initialTitleId), utcNow: clock.GetUtcNow()) with
        {
            SealFlag = 1,
            Attr = 12,
            EnchantCardId = 5001,
            EmblemSocketCount = 2,
            Emblem1 = 300,
            Emblem2 = 301,
            Random0 = new(1, 2, 3),
            TradeRestriction = 1,
            ExpireTime = 0
        };
        var unsealedTitle = sealedTitle with { SealFlag = 0 };
        var normalThreads = InventoryRewardGrantService.Create(items.Get(threadId), 2, utcNow: clock.GetUtcNow()) with { ExpireTime = 0 };
        var gorgeousThreads = InventoryRewardGrantService.Create(items.Get(gorgeousThreadId), 2, utcNow: clock.GetUtcNow()) with { ExpireTime = 0 };
        DfoServer.Game.Characters.Character role;
        long titleUid;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            role = repo.Create(owner, new("TitleTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 85,
                AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(0).Skills,
                Inventory = new([
                    new(0, new(InventoryListType.Main, 9), sealedTitle),
                    new(0, new(InventoryListType.Main, 10), unsealedTitle),
                    new(0, new(InventoryListType.Main, 65), normalThreads),
                    new(0, new(InventoryListType.Main, 66), gorgeousThreads)
                ], Gold: 100_000)
            });
            titleUid = role.Inventory.Find(new(InventoryListType.Main, 9))!.Uid;
        }

        string StateSnapshot() =>
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index") +
            fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id");

        using (var peer = Open(fixture))
        {
            peer.Select(role.Slot);
            string baseline = StateSnapshot();
            foreach (var (body, error) in new (byte[], byte)[]
            {
                ([65, 0], 19),
                ([9, 0, 9, 0], 19),
                ([65, 0, 10, 0], 19),
                ([9, 0, 10, 0], 17)
            })
            {
                peer.Send(opcode, body);
                peer.Expect(1, opcode, [0, error, 0]);
            }
            peer.Barrier();
            That(StateSnapshot() == baseline, "CMD970畸形长度、同槽、未封装哥特称号与非变更道具源全部拒绝且不改库");

            peer.Send(opcode, [65, 0, 9, 0]);
            var ack1 = ExpectTitleChange(peer, opcode);
            int firstResultId = (int)U32(ack1, 2);
            That(ack1.Length == 10 && ack1[0] == 1 && ack1[1] == 1 &&
                firstResultId is 400330032 or 400330033 or 400330034 &&
                (int)U32(ack1, 6) == threadId,
                "绣线(10007724)经CMD970成功回包(isSuccess=1)并返回新哥特称号ID与消耗绣线ID");
            using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var saved = repo.LoadCharacter(owner, role.Slot);
                var updatedTitle = saved.Inventory.Find(new(InventoryListType.Main, 9))!;
                That(updatedTitle.Uid == titleUid &&
                    updatedTitle.Core == (sealedTitle with { ItemId = firstResultId }) &&
                    saved.Inventory.Find(new(InventoryListType.Main, 65))?.Core.Count == 1,
                    "绣线洗练保留原称号UID、封装状态、附魔与徽章，仅替换模板ID并扣1根绣线");
            }

            peer.Send(opcode, [66, 0, 9, 0]);
            var ack2 = ExpectTitleChange(peer, opcode);
            int expectedGorgeousId = firstResultId == 400330031 ? 400330033 :
                firstResultId is 400330032 or 400330033 ? 400330034 : 400330033;
            That(ack2.Length == 10 && ack2[0] == 1 && ack2[1] == 0 &&
                (int)U32(ack2, 2) == expectedGorgeousId && (int)U32(ack2, 6) == gorgeousThreadId,
                "华丽的绣线(10007725)经CMD970走0%成功率分支(isSuccess=0)定向洗出哥特白昼/黑夜称号");

            baseline = StateSnapshot();
            peer.Send(opcode, [65, 0, 9, 0]);
            peer.Expect(1, opcode, [0, 19, 0]);
            peer.Barrier();
            That(StateSnapshot() == baseline, "哥特白昼/黑夜毕业称号不能再被普通绣线(10007724)洗练，不扣剩余绣线");

            fixture.Sql("CREATE TRIGGER fail_title_change BEFORE INSERT ON inventory_audit_log WHEN NEW.action_name='title_change' BEGIN SELECT RAISE(ABORT,'title change rollback'); END");
            peer.Send(opcode, [66, 0, 9, 0]);
            peer.Closed();
            That(StateSnapshot() == baseline, "CMD970晚期SQL失败整体回滚称号ID、华丽的绣线数量与角色revision");
            fixture.Sql("DROP TRIGGER fail_title_change");
        }
    }

    static byte[] ExpectTitleChange(TcpPeer peer, ushort opcode)
    {
        int itemUpdates = 0;
        while (true)
        {
            var packet = peer.Read();
            if (packet is { Kind: 0, Opcode: 14 })
                itemUpdates++;
            else if (packet is { Kind: 1 } && packet.Opcode == opcode)
            {
                That(itemUpdates >= 2, "CMD970先通过NOTI14同步变更道具与目标称号槽位，再返回ACK970");
                peer.Barrier();
                return packet.Body;
            }
            else
                throw new InvalidOperationException($"Unexpected title change packet: kind={packet.Kind} opcode={packet.Opcode}");
        }
    }

    static void Use(TcpPeer peer,ushort slot,uint action,uint first=0,uint second=0) => peer.Send(515,Build(w=>
    { w.Write(slot);w.Write((byte)0);w.Write(0u);w.Write(action);w.Write(first);w.Write(second); }));
    static void Ack(TcpPeer peer,ushort slot,uint action) => peer.Expect(1,515,Build(w=>
    { w.Write((byte)1);w.Write(slot);w.Write((byte)0);w.Write(action); }));
    static void Repair(TcpPeer peer,short target,short source) => peer.Send(23,Build(w=>
    { w.Write((byte)0);w.Write(target);w.Write(source);w.Write((ushort)0);w.Write((byte)0); }));
}
