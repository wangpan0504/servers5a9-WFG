using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EquipmentTransferSealTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var clock = TimeProvider.System;
        var assets = resources.ItemResources;
        var items = assets.Items;
        var catalog = assets.EquipmentTransports;
        await PointTitleStoneAsync(options, root, resources);
        var stackable = PvfScript.List(resources.Archive.ReadText("stackable/stackable.lst"));
        foreach (int id in new[] { 10000705, 10000706, 10014812, 10007991, 10305043, 10309718, 10311613, 10305482, 10305483,
            490704269, 490704533, 490705031, 490705033 })
            That(items.ActionConsumable(id) is { Parameters: [>= 0] }, "各族跨界石真实资源接通: " + id);
        foreach (int id in new[] { 14, 2660238, 10098656, 10157922, 10164812 })
            That(items.Get(id).ResealConsumable is not null, "蜜蜡真实资源与旧编号兼容: " + id);
        That(items.Get(10098656).ResealConsumable!.OneShot && items.Get(490002630).ResealConsumable is null,
            "客户端硬编码一次性蜜蜡接通，产出蜜蜡的礼包仍走礼包系统");
        bool recoveredWax=items.Get(490000345).ResealConsumable is { OneShot:true,MinimumLevel:1,MaximumLevel:95 };
        var equipment = PvfScript.List(resources.Archive.ReadText("equipment/equipment.lst"))
            .Where(row => row.Value.Contains("/weapon/",StringComparison.OrdinalIgnoreCase)).Select(row => items.Get(row.Key))
            .First(item => item.Kind == ItemKind.Equipment && item.Rarity is 2 or 3 && item.AttachType.Contains("[sealing]") &&
                catalog.Rules[0].StoneCost(item) > 0 && assets.EquipmentReseals.SoulCost(item,0) is not null);
        That(equipment.AttachType.Contains("[sealing]") && catalog.Rules[0].StoneCost(equipment) > 0 &&
            assets.EquipmentReseals.SoulCost(equipment, 0) is not null, "同一真实装备覆盖跨界、蜜蜡及NPC灵魂封装");
        var original = InventoryRewardGrantService.Create(equipment) with { SealFlag = 0, Attr = 17,
            EnchantCardId = 3175, Rune = 9, Extension97 = 0x12345678, GenuineUpgrade = 2 };
        var avatar = items.Get(resources.Characters.Get(3).DefaultAvatarEquipment[3]);
        var medal = items.Get(100380044);
        var emblem = stackable.Where(row => row.Value.Contains("emblem", StringComparison.OrdinalIgnoreCase)).Select(row => items.Get(row.Key))
            .First(item => item.Kind == ItemKind.Emblem && catalog.Rules[4039].StoneCost(item) > 0);
        bool medalEnabled = catalog.Rules.TryGetValue(9, out var medalRule);
        That(catalog.Rules[4036].StoneCost(avatar) > 0 && (!medalEnabled || medalRule!.StoneCost(medal) > 0),
            "装扮及已补表勋章夹具符合各自资源范围");
        InventoryItem Item(int id, short slot, int count = 1) => InventoryRewardGrantService.CreateItem(items.Get(id), Bag(slot),
            InventoryRewardGrantService.Create(items.Get(id), count, clock.GetUtcNow()) with { ExpireTime = 0 });
        await using var fixture = new TcpTestFixture(options, Path.Combine(root,"equipment-transfer-seal"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        ushort senderId, receiverId, npcId;
        InventoryItem target, avatarItem;
        var area = resources.Towns.Areas.First(row => row.Value.Map(new HashSet<int>()).Npcs.Any(npc => resources.Towns.EquipmentTransportNpcs.Contains(npc.Id)));
        var bounds = area.Value.Map(new HashSet<int>()).Bounds.First().Bounds;
        var npcPosition = new Location(area.Key.Item1,area.Key.Item2,checked((ushort)(bounds.X+1)),checked((ushort)(bounds.Y+1)));
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var sender = repository.Create(owner, new("TransferSrc",resources.Towns.Room,resources.Towns.InitialReturn)
            {
                Level=95,AcceptedStoryLevel=1,Skills=resources.Characters.Get(0).Skills,
                Inventory = new([new(0,Bag(9),original), new(0,Bag(10),original), new(0,Bag(11),original with { Attr = 7*32+17 }),
                    new(0,Bag(12),original with { Attr = 7*32+17 }),Item(490000345,88,2),
                    Item(10000705,82,100), Item(14,83,200), Item(10098656,84,2), Item(490704269,85,2), Item(10305043,86,2), Item(490705031,87,2),
                    InventoryRewardGrantService.CreateItem(avatar,new(InventoryListType.Avatar,0),InventoryRewardGrantService.Create(avatar)),
                    InventoryRewardGrantService.CreateItem(medal,new(InventoryListType.GuildMedal,0),InventoryRewardGrantService.Create(medal)),
                    Item(emblem.Id,289,3)])
            });
            sender = InventoryService.Commit(repository, owner, sender, new(true, 0,
                new(sender.Inventory, sender.Inventory with { AccountCargo = new(Capacity: 8) }, "fixture")));
            senderId=sender.Id; target=sender.Inventory.Find(Bag(9))!; avatarItem=sender.Inventory.Find(new(InventoryListType.Avatar,0))!;
            receiverId = repository.Create(owner,new("TransferDst",resources.Towns.Room,resources.Towns.InitialReturn)
                { Level=95,AcceptedStoryLevel=1,Skills=resources.Characters.Get(0).Skills }).Id;
            var npcRole = repository.Create(owner,new("SoulTransfer",npcPosition,resources.Towns.InitialReturn)
                { Level=95,AcceptedStoryLevel=1,Skills=resources.Characters.Get(0).Skills,Inventory=new([new(0,Bag(9),original),new(0,Bag(10),original)]) });
            npcId=npcRole.Id;
            var costs = catalog.Rules[7].NpcCosts(equipment,assets.EpicPieces)!.Value;
            var soulCost = assets.EquipmentReseals.SoulCost(equipment,0)!;
            var grant = InventoryRewardGrantService.TryGrantBatch(npcRole,
                costs.Append(soulCost).GroupBy(row => row.ItemId).Select(group => new InventoryReward(group.Key,group.Sum(row => row.Count)*5)).ToArray(),
                items,assets.GoldLimits,assets.EpicPieces,clock.GetUtcNow());
            InventoryService.Commit(repository,owner,npcRole,grant);
            var service = new EquipmentTransportService(items.Get,items.ActionConsumable,catalog,assets.EpicPieces,repository,clock);
            using var other = owners.Acquire(fixture.Options.DevelopmentAccount+"-other",()=>{});
            var foreign = repository.Create(other,new("ForeignDst",resources.Towns.Room,resources.Towns.InitialReturn));
            That(service.Plan(sender,CharacterRosterEntry.FromCharacter(foreign),new(85,0,7,0),"[seria room]",out var forged)==0,
                "目标账号归属由提交事务复核");
            string baseline=State();
            Reject<InvalidOperationException>(()=>service.Commit(owner,forged!),"跨账号邮件转移拒绝");
            That(State()==baseline,"跨账号拒绝不会扣道具或分配邮件资产");
        }
        await fixture.StartAsync();
        using (var peer=Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string unchanged = State();
            peer.Send(81, Seal(10,equipment.Id,0,1));peer.Expect(1,81,[0,19,0]);peer.Barrier();
            peer.Send(557,Transport(0,10,2,1));peer.Expect(1,557,[0,7,0]);peer.Barrier();
            That(State()==unchanged,"赛丽亚房间不能伪造NPC跨界和灵魂封装入口，资产不变");
            peer.Send(557,Transport(82,9,0,uint.MaxValue));
            int remaining=100-catalog.Rules[0].StoneCost(equipment);
            peer.Expect(1,557,Build(writer=>{writer.Write((byte)1);writer.Write((ushort)82);writer.Write((ushort)9);writer.Write((ushort)remaining);writer.Write((ushort)0);writer.Write((byte)0);}));
            peer.Barrier(CheckDelta);
            That(fixture.Query("SELECT list_type,slot_index FROM character_inventory_slots WHERE item_uid=$p0",target.Uid) is [[12L,0L]] && Read(target.Uid)==original,
                "557 mode0完整装备入账号金库，第四字段返回真实仓库槽");
            int wax=assets.EquipmentReseals.WaxCost(equipment,0);
            peer.Send(81,Seal(10,equipment.Id,83)); peer.Expect(0,14); peer.Expect(1,81,Build(writer=>{writer.Write((byte)1);writer.Write((short)83);writer.Write(200-wax);writer.Write((ushort)1);})); peer.Barrier();
            That(ReadSlot(senderId,0,10)==original with { SealFlag=1,Attr=49 },"普通蜜蜡扣实际费用、增加一次封装且保留其他核心");
            unchanged=State();peer.Send(81,Seal(10,equipment.Id,83));peer.Expect(1,81,[0,19,0]);peer.Barrier();
            That(State()==unchanged,"重复封装拒绝且不重复扣费");
            peer.Send(81,Seal(11,equipment.Id,84));peer.Expect(0,14);peer.Expect(1,81,[1,84,0,1,0,0,0,1,0]);peer.Barrier();
            That(ReadSlot(senderId,0,11)==original with { SealFlag=1,Attr=7*32+17 },"一次性蜜蜡越过满次数而不增加次数");
            unchanged=State();peer.Send(81,Seal(12,equipment.Id,88));
            if(recoveredWax)
            {
                peer.Expect(0,14);peer.Expect(1,81,[1,88,0,1,0,0,0,1,0]);peer.Barrier();
                That(ReadSlot(senderId,0,12)==original with { SealFlag=1,Attr=7*32+17 },"补标记的一口黄金蜜蜡实际封装且不增加次数");
            }
            else
            {
                peer.Expect(1,81,[0,19,0]);peer.Barrier();That(State()==unchanged,"缺少封装标记的正式资源明确拒绝且不消耗，补丁候选另验成功");
            }
            fixture.Sql("CREATE TRIGGER fail_equipment_transport AFTER INSERT ON mailbox_audit_log WHEN NEW.action_name='equipment_transport' BEGIN SELECT RAISE(ABORT,'transport rollback'); END");
            string rollback=State();peer.Send(557,Transport(85,0,7,1));peer.Expect(1,557,[0,7,0]);peer.Barrier();
            That(State()==rollback,"邮件审计末段失败整笔回滚源道具、装扮、详情、冷却、邮件及revision");
            fixture.Sql("DROP TRIGGER fail_equipment_transport");
            foreach (var input in new (ushort Stone,ushort Target,byte Mode)[] { (85,0,7), (86,0,100), (87,289,51) })
            {
                if(input.Mode==100 && !medalEnabled)
                {
                    unchanged=State();peer.Send(557,Transport(input.Stone,input.Target,input.Mode,1));peer.Expect(1,557,[0,7,0]);peer.Barrier();
                    That(State()==unchanged,"正式资源尚未补勋章规则时明确拒绝且不消耗，补表候选另验成功");
                    continue;
                }
                peer.Send(557,Transport(input.Stone,input.Target,input.Mode,1));
                peer.Expect(1,557,Build(writer=>{writer.Write((byte)1);writer.Write(input.Stone);writer.Write(input.Target);writer.Write((ushort)1);writer.Write((ushort)0);writer.Write(input.Mode);}));
                peer.Barrier(CheckDelta);
            }
            That(fixture.Query("SELECT character_id FROM character_inventory_items WHERE item_uid=$p0",avatarItem.Uid) is [[var ownerId]] && Convert.ToInt32(ownerId)==receiverId,
                "装扮邮件保持原UID和详情并转交正确同账号角色");
            That(ReadSlot(senderId,0,289)?.Count==2 && Convert.ToInt64(fixture.Query("SELECT COUNT(*) FROM mailbox_messages WHERE recipient_id=$p0",receiverId).Single()[0])==(medalEnabled?3:2),
                "徽章邮递只拆一枚，原堆叠剩余两枚，三族各生成一封邮件");
        }
        using (var peer=Open(fixture))
        {
            peer.Select(2,resumeTown:true,room:false);peer.Barrier();
            peer.Send(81,Seal(10,equipment.Id,0,1));peer.Expect(0,14);peer.Expect(1,81,[1,0,0,0,0,0,0,1,0]);peer.Barrier();
            That(ReadSlot(npcId,0,10)==original with { SealFlag=1,Attr=49 },"歌兰蒂斯灵魂封装接通真实81无材料槽请求");
            peer.Send(557,Transport(0,9,2,1));peer.Expect(1,557,[1,0,0,9,0,0,0,0,0,2]);peer.Barrier(CheckDelta);
            That(ReadSlot(npcId,0,9) is null && Convert.ToInt64(fixture.Query("SELECT COUNT(*) FROM mailbox_messages WHERE recipient_id=$p0",receiverId).Single()[0])==(medalEnabled?4:3),
                "歌兰蒂斯557 mode2实际扣灵魂与结晶并托管装备，源0不冒充道具");
        }
        using(var peer=Open(fixture))
        {
            peer.Select(1);peer.Barrier();
            var mails=fixture.Query("SELECT mail_id FROM mailbox_messages WHERE recipient_id=$p0 AND sender_name IN('TransferSrc','SoulTransfer') ORDER BY mail_id",receiverId)
                .Select(row=>Convert.ToUInt32(row[0])).ToArray();
            peer.Send(95,Build(writer=>{writer.Write(mails.Length);foreach(uint id in mails)writer.Write(id);}));
            var claimed=peer.Expect(1,95).Body;
            That(claimed.Length==5+12*mails.Length && claimed[0]==1 && U32(claimed,1)==mails.Length &&
                Enumerable.Range(0,mails.Length).All(index=>U32(claimed,5+12*index)==mails[index] && U32(claimed,13+12*index)==0),
                "跨界邮件批量领取使用真实95协议且每封实际成功");
            peer.Barrier(CheckDelta);
            That(fixture.Query("SELECT character_id,list_type FROM character_inventory_slots WHERE item_uid=$p0",avatarItem.Uid) is [[var recipient,var list]] &&
                Convert.ToInt32(recipient)==receiverId && Convert.ToInt32(list)==1,"收件角色重连后实际领取完整装扮，保持原UID");
        }
        await fixture.StopAsync();
        That(Read(target.Uid)==original,"停服重开存储读取仍保存完整跨界装备核心");
        ItemCore Read(long uid)=>JsonSerializer.Deserialize<ItemCore>((string)fixture.Query("SELECT item_core FROM character_inventory_items WHERE item_uid=$p0",uid).Single()[0]!)!;
        ItemCore? ReadSlot(ushort character,int list,int slot)
        {
            var rows=fixture.Query("SELECT item_core FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid WHERE s.character_id=$p0 AND s.list_type=$p1 AND s.slot_index=$p2",character,list,slot);
            return rows.Length==0?null:JsonSerializer.Deserialize<ItemCore>((string)rows.Single()[0]!);
        }
        string State()=>fixture.Snapshot()+fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid")+
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid")+fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id")+
            fixture.Snapshot("SELECT * FROM mailbox_attachments ORDER BY attachment_id")+fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid")+
            fixture.Snapshot("SELECT * FROM character_consumable_states ORDER BY character_id,item_id")+
            fixture.Snapshot("SELECT * FROM mailbox_audit_log ORDER BY audit_id");
        static void CheckDelta(WirePacket packet)=>That(packet.Opcode is 14 or 1681 && packet.Body.Length>=3 &&
            packet.Body.Length==3+U16(packet.Body,1)*(packet.Body[0]==1?165:123),
            $"跨界发布使用完整123B库存增量: opcode={packet.Opcode}, length={packet.Body.Length}, body={Convert.ToHexString(packet.Body.AsSpan(0,Math.Min(12,packet.Body.Length)))}");
    }
    static async Task PointTitleStoneAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var clock = TimeProvider.System;
        var assets = resources.ItemResources;
        var items = assets.Items;
        var catalog = assets.EquipmentTransports;
        That(items.Contains(490705481) && !items.ContainsEquipment(490705481) && items.ContainsEquipment(400330267),
            "积分称号石及至尊称号须存在于当前真实目录，不以保存路径或模板注入替代");
        var stone = items.Get(490705481);
        var title = items.Get(400330267);
        var action = items.ActionConsumable(490705481);
        That(action is { Label: "[cargo transport stone]", Parameters: [4041], CoolTime: 1000 } &&
            action.Places.SequenceEqual(new[] { "[seria room]" }) && !action.AllowsPlace("[village]") &&
            stone.Kind == ItemKind.Consumable && stone.StackLimit == int.MaxValue && stone.MinimumLevel == 1 &&
            stone.MaximumLevel == int.MaxValue && stone.RequiredSkill == 0 && stone.Jobs == ushort.MaxValue &&
            stone.AttachType.Trim('`', ' ', '\r', '\n') == "[account]" &&
            stone.Expiration == new ItemExpiration() && title.Kind == ItemKind.Equipment && title.EquipmentSlot == 13 &&
            title.MinimumLevel == 1 && title.Rarity == 6 && title.AttachType.Trim('`', ' ', '\r', '\n') == "[trade]" &&
            title.Expiration == new ItemExpiration(), "481动作4041、赛丽亚地点及267真实永久称号合同");
        That(catalog.Rules.TryGetValue(4041, out _), "真实跨界表必须有4041，缺规则不能跳过本例");
        var rule = catalog.Rules[4041];
        That(rule.Grades.SequenceEqual(new[] { 1, 95, 1 }) && rule.RarityWeights.Count == 3 &&
            rule.RarityWeights.GetValueOrDefault(2) == 1 && rule.RarityWeights.GetValueOrDefault(3) == 1 &&
            rule.RarityWeights.GetValueOrDefault(6) == 1 && rule.EquipmentSlots.SetEquals(new byte[] { 13 }) &&
            rule.StackableTypes.IsEmpty && rule.Included.Contains(400330267) && rule.Excluded.IsEmpty &&
            rule.ExcludedProperties.IsEmpty && rule.StoneCost(title) == 1 && rule.StoneCost(items.Get(26596)) == 0,
            "4041按实际白名单允许267、排除白名单外真实普通称号且只收1颗石头");
        var original = InventoryRewardGrantService.Create(title);
        var source = new InventoryItem(2, Bag(80), InventoryRewardGrantService.Create(stone, 2));
        var target = new InventoryItem(1, Bag(9), original);
        var role = new Character(1, 0, "TitlePlan", 0, resources.Towns.Room, null)
        { Level = 95, Inventory = new([target, source]) { AccountCargo = new(Capacity: 8) } };
        var service = new EquipmentTransportService(items.Get, items.ActionConsumable, catalog, assets.EpicPieces, null!, clock);
        That(service.Plan(role, null, new(80, 9, 0, uint.MaxValue), "[seria room]", out var planned) == 0 &&
            planned!.Transferred.Uid == target.Uid && planned.Transferred.Core == original &&
            planned.Inventory.After.Find(Bag(9)) is null && planned.Inventory.After.Find(Bag(80))!.Core.Count == 1 &&
            planned.Inventory.After.Find(new(InventoryListType.AccountCargo, 0)) == planned.Transferred,
            "真实481到267沿既有mode0计划保原UID及完整Core、扣1并迁入账号金库");
        That(service.Plan(role, null, new(80, 9, 0, 0), "[village]", out _) == 21,
            "真实481的赛丽亚场景限制不放宽到普通城镇");
        var expiredTarget = role with { Inventory = role.Inventory with
            { Items = role.Inventory.Items.Replace(target, target with { Core = original with { ExpireTime = 1 } }) } };
        var expiredSource = role with { Inventory = role.Inventory with
            { Items = role.Inventory.Items.Replace(source, source with { Core = source.Core with { ExpireTime = 1 } }) } };
        That(service.Plan(expiredTarget, null, new(80, 9, 0, 0), "[seria room]", out _) == 7 &&
            service.Plan(expiredSource, null, new(80, 9, 0, 0), "[seria room]", out _) == 21,
            "实例期限保护覆盖过期称号和过期源，永久模板不取消已有实例期限");

        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "point-title481"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        ushort characterId;
        long targetUid, sourceUid;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var created = repository.Create(owner, new("PointTitle", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, DungeonState = CharacterDungeonState.NewOrdinary,
                Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Bag(9), original), new(0, Bag(10), original), new(0, Bag(80), source.Core)], Gold: 1234)
            });
            created = InventoryService.Commit(repository, owner, created, new(true, 0,
                new(created.Inventory, created.Inventory with { AccountCargo = new(Capacity: 8),
                    Items = created.Inventory.Items.AddRange(Enumerable.Range(1, 7).Select(index =>
                        new InventoryItem(0, new(InventoryListType.AccountCargo, (short)index), original))) }, "fixture")));
            characterId = created.Id;
            targetUid = created.Inventory.Find(Bag(9))!.Uid;
            sourceUid = created.Inventory.Find(Bag(80))!.Uid;
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            fixture.Sql("CREATE TRIGGER fail_point_title481 AFTER INSERT ON inventory_audit_log WHEN NEW.action_name='equipment_transport' BEGIN SELECT RAISE(ABORT,'title transport rollback'); END");
            string before = Assets();
            peer.Send(557, Transport(80, 9, 0, uint.MaxValue)); peer.Expect(1, 557, [0, 7, 0]); peer.Barrier();
            That(Assets() == before, "267金库跨界晚期SQL失败整笔回滚称号、源石、冷却、revision和审计");
            fixture.Sql("DROP TRIGGER fail_point_title481");
            peer.Send(557, Transport(80, 9, 0, uint.MaxValue));
            peer.Expect(1, 557, Build(writer => { writer.Write((byte)1); writer.Write((ushort)80); writer.Write((ushort)9);
                writer.Write((ushort)1); writer.Write((ushort)0); writer.Write((byte)0); }));
            peer.Barrier(CheckTitleDelta);
            That(fixture.Query("SELECT list_type,slot_index FROM character_inventory_slots WHERE item_uid=$p0", targetUid) is [[12L, 0L]] &&
                Read(targetUid) == original && Read(sourceUid).Count == 1 &&
                fixture.Query("SELECT gold FROM characters WHERE id=$p0", characterId) is [[1234L]],
                "557真实成功返回余量1和目的格0，267保原UID及全部Core且不另扣金币");
            before = Assets();
            peer.Send(557, Transport(80, 9, 0, 0)); peer.Expect(1, 557, [0, 7, 0]); peer.Barrier();
            That(Assets() == before, "旧源槽请求重放不能再次扣石或再迁移267");
            await Task.Delay(1100); // 481真实cool time=1000，先等合法冷却结束再核满金库错误。
            before = Assets();
            peer.Send(557, Transport(80, 10, 0, 0)); peer.Expect(1, 557, [0, 19, 0]); peer.Barrier();
            That(Assets() == before, "账号金库已满拒绝第二件267，保留剩余石头、称号和完整资产");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            That(Read(targetUid) == original && Read(sourceUid).Count == 1 &&
                fixture.Query("SELECT list_type,slot_index FROM character_inventory_slots WHERE item_uid=$p0", targetUid) is [[12L, 0L]],
                "重连重读保持已提交267及剩余1石，不重复执行跨界");
        }
        await fixture.StopAsync();
        That(Read(targetUid) == original && Read(sourceUid).Count == 1, "停止测试服务后重新打开存储仍保留跨界完整结果");

        ItemCore Read(long uid) => JsonSerializer.Deserialize<ItemCore>((string)fixture.Query(
            "SELECT item_core FROM character_inventory_items WHERE item_uid=$p0", uid).Single()[0]!)!;
        string Assets() => fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") +
            fixture.Snapshot("SELECT * FROM character_consumable_states ORDER BY character_id,item_id") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id") +
            fixture.Snapshot("SELECT * FROM mailbox_attachments ORDER BY attachment_id") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
        static void CheckTitleDelta(WirePacket packet) => That(packet.Opcode is 14 or 1681 && packet.Body.Length >= 3 &&
            packet.Body.Length == 3 + U16(packet.Body, 1) * 123, "称号跨界沿既有完整123B库存投影");
    }


    static InventorySlot Bag(short slot)=>new(InventoryListType.Main,slot);
    static byte[] Transport(ushort source,ushort target,byte mode,uint recipient)=>Build(writer=>{writer.Write(source);writer.Write(target);writer.Write(mode);writer.Write(recipient);});
    static byte[] Seal(short target,int item,short source,ushort state=0)=>Build(writer=>{writer.Write(target);writer.Write(item);writer.Write(source);writer.Write(state);});
}
