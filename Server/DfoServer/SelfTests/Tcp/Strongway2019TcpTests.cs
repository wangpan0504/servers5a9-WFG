using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Infrastructure;
using DfoServer.Development.Dungeons;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Network;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.PartyDungeonTcpTests;

namespace DfoServer.Testing;

internal static partial class Strongway2019TcpTests
{
    // 固定真实模板，不扫描装备正文，也不向Session注入抗魔代理。
    static readonly int[] EquipmentIds = [2747476, 400050102, 400150086, 400100094, 400250092,
        400200086, 400300070, 400310070, 400320074, 400340233];

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source with
        { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "strongway2019"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var sets = new AntiEvilSetCatalog(resources.Archive);
        var provider = new CharacterAntiEvil(resources.ItemResources.Items, new(resources.Archive),
            resources.ItemResources.RecoveryConsumables, contracts: resources.ItemResources.Contracts,
            sets: sets);
        var high = Prepare("swtcpfull", "SWFull", equipped: true);
        var low = Prepare("swtcplow", "SWLow", equipped: false);
        var questCharacter = Prepare("swtcpquest", "SWQuest", equipped: false);
        var hero = PrepareSoloHero(fixture, resources, provider);
        // 仅隔离测试资产：通过正式Grant准备真实兑换材料，不预种8698任务完成位。
        using (var shopOwner = new AccountOwners().Acquire("swtcpquest", () => { }))
        {
            var inventory = new InventoryService(resources.ItemResources.Items, resources.ItemResources.GoldLimits,
                resources.ItemResources.EpicPieces);
            questCharacter = InventoryService.Commit(repository, shopOwner, questCharacter,
                inventory.Grant(questCharacter, [new(490012698, 10)]));
        }

        var qualifications = high.Inventory.Items.Select(item =>
        {
            var template = resources.ItemResources.Items.Get(item.Core.ItemId);
            bool allows = InventoryMoveService.Allows(high, item.Position, item.Core, resources.ItemResources.Items,
                fixture.UtcNow.ToUnixTimeSeconds(), resources.ItemResources.Contracts);
            Console.WriteLine($"Strongway equipment id={item.Core.ItemId} slot={item.Position.List}/{item.Position.Index}" +
                $" templateSlot={template.EquipmentSlot} kind={item.Core.Kind}/{template.Kind} allows={allows}" +
                $" level={template.MinimumLevel} jobs={template.Jobs} skill={template.RequiredSkill}" +
                $" durability={item.Core.Durability}/{template.Durability} expiry={item.ExpireTime}" +
                $" antiEvil={template.AntiEvil} sets={string.Join(',', template.PartSetIds)}");
            return (Item: item, Template: template, Allows: allows);
        }).ToArray();
        foreach (var group in qualifications.SelectMany(row => row.Template.PartSetIds.Where(id => id > 0).Distinct()
            .Select(setId => (SetId: setId, row.Allows))).GroupBy(row => row.SetId))
            Console.WriteLine($"Strongway set id={group.Key} pieces={group.Count()} allowed={group.Count(row => row.Allows)}" +
                $" tiers={string.Join(',', sets.Get(group.Key).Select(tier => $"{tier.RequiredPieces}:{tier.AntiEvil}"))}");
        int highAntiEvil = provider.Compute(high), lowAntiEvil = provider.Compute(low);
        Console.WriteLine($"Strongway qualification items={high.Inventory.Items.Length} level={high.Level} job={high.Job}" +
            $" grow={high.GrowType} equipmentFlags={high.EquipmentSlotFlags} high={highAntiEvil} low={lowAntiEvil}");
        foreach (var row in qualifications)
            That(row.Allows, "TCP每件穿戴满足真实部位、等级、职业、技能及槽解锁资格：" + row.Item.Core.ItemId);
        That(high.Inventory.Items.Length == 10 && highAntiEvil == 4127 && lowAntiEvil < 4000,
            $"TCP资格取真实十件装备与30125三件套共4127，低抗魔角色不具准入：high={highAntiEvil}, low={lowAntiEvil}");
        var events = new ServerEventService(new ServerEventRepository(fixture.Database));
        events.Override(ServerEventService.Strongway2019Id, true);
        await fixture.StartAsync();
        var entry = ContentEntry(fixture);
        VerifyNativeQuest();
        VerifyDynamicEntryShop();

        using (var peer = Open(fixture, "swtcplow", gamePort: entry.Port, channelNumber: entry.Number))
        {
            Select(peer, low.Slot);
            BeginSelection(peer);
            RejectEntry(peer, "活动开放但服务端真实抗魔不足");
            peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
        }

        events.Override(ServerEventService.Strongway2019Id, false);
        byte[] history;
        byte[] accountRewards = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(accountRewards.AsSpan(12), 1);
        using (var peer = Open(fixture, "swtcpfull", gamePort: entry.Port, channelNumber: entry.Number))
        {
            Select(peer, high.Slot);
            BeginSelection(peer);
            RejectEntry(peer, "真实抗魔达标但活动关闭");
            events.Override(ServerEventService.Strongway2019Id, true);
            byte[] directEntry = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(directEntry, 5359);
            peer.Send(15, directEntry);
            var directSelection = Drain(peer);
            int directAck = Array.FindIndex(directSelection.ToArray(), packet => packet is { Kind: 1, Opcode: 15, Body: [1] });
            int directState = Array.FindIndex(directSelection.ToArray(), packet =>
                packet is { Kind: 0, Opcode: 3, Body.Length: 4 } && packet.Body[0] == 1);
            int directScene = Array.FindIndex(directSelection.ToArray(), packet => packet is { Kind: 0, Opcode: 27, Body.Length: 42 });
            That(directAck >= 0 && directState > directAck && directScene > directState &&
                directSelection.Count(packet => packet is { Kind: 1, Opcode: 15, Body: [1] }) == 1 &&
                directSelection.Count(packet => packet is { Kind: 0, Opcode: 27, Body.Length: 42 }) == 1,
                "首期原生面板从城镇指定直入5359时先用27建立场景，ACK15和state1不能代替");
            peer.Send(15, directEntry);
            That(!Drain(peer).Any(packet => packet is { Kind: 0, Opcode: 27 }),
                "首期已在选图阶段的指定15重放不重新打开原生选择场景");
            peer.Send(16, EntryRequest());
            var entered = Drain(peer);
            var information = entered.Single(packet => packet is { Kind: 0, Opcode: 28 }).Body;
            byte[] map = entered.Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
            That(information.Length == 36 && U32(information, 0) == 5359 && information[4] == 2 &&
                !entered.Any(packet => packet is { Kind: 1, Opcode: 16 }) &&
                fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[1L]],
                "打开312后同一真实选图请求进入5359指定难度2，只提交一次原Run尝试");
            string experience = Experience(peer.Id);
            That(fixture.Query("SELECT count(*) FROM account_strongway2019_rewards") is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_strongway2019_statistics") is [[0L]],
                "入场不是通关事实，不提前发邮件或记录角色成绩");
            var dungeon = SelfTestCatalogs.Dungeons(resources.Archive).Get(5359);
            var maze = dungeon.Mazes[information[7]];
            var path = Route(maze, new(map[0], map[1]), new(information[8], information[9]));
            byte[]? finalDeath = null;
            byte[]? clearHistory = null;
            int rooms = 0;
            while (true)
            {
                That(++rooms <= 64 && map.Length >= 19 + map[18] * 22,
                    "真实START_MAP的完整演员行与房间数有界");
                FormalDungeonTcpTests.Load(peer);
                var afterLoad = Drain(peer);
                That(afterLoad.All(packet => packet is { Kind: 0, Opcode: 36 or 4 }),
                    "原Run加载后只接受疲劳及虚弱通知，不伪造战斗或结算");
                foreach (int at in Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                    .Where(at => map[at + 13] <= 3 && map[at + 21] == 0).OrderBy(at => map[at + 13] == 3))
                {
                    ushort key = U16(map, at + 6);
                    byte[] report = FormalDungeonTcpTests.Death(key, peer.Id, map[at + 12]);
                    peer.Send(39, report);
                    var deaths = Drain(peer);
                    var ownDeath = deaths.Where(packet => packet is { Kind: 0, Opcode: 38 } && U16(packet.Body, 0) == key).ToArray();
                    That(ownDeath.Length == 1 && ownDeath[0].Body.Length >= 9 &&
                        ownDeath[0].Body.Length == 9 + 131 * U16(ownDeath[0].Body, 2),
                        "39报告经过真实分派后仅退场当前29已公布的演员：" + key);
                    That(!deaths.Any(packet => packet is { Kind: 0, Opcode: 37 }) && Experience(peer.Id) == experience,
                        "首期disable exp不增加角色经验或熟练度，也不发送虚构经验通知");
                    if (deaths.FirstOrDefault(packet => packet is { Kind: 0, Opcode: 1491 }) is { } profile)
                    {
                        clearHistory = profile.Body;
                        Bytes(deaths.Single(packet => packet is { Kind: 0, Opcode: 1753 }).Body, accountRewards,
                            "真实通关刷新1753账号普通周次数1，匹配次数和四个原生默认槽保持0");
                        finalDeath = report;
                    }
                }
                if (!path.TryPop(out var target)) break;
                byte[] move = new byte[100];
                move[0] = checked((byte)target.X); move[1] = checked((byte)target.Y);
                peer.Send(45, move);
                var moved = Drain(peer);
                That(moved.Count(packet => packet is { Kind: 1, Opcode: 45, Body: [1] }) == 1,
                    "真实清房后沿45确认向资源相邻房间前进");
                map = moved.Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
                That(map[0] == target.X && map[1] == target.Y, "原Run移动通知绑定请求的真实相邻房间");
            }
            That(clearHistory is { Length: 16 } && finalDeath is not null,
                "真实终点向TCP连接刷新1491角色历史，不能只凭SQL变更判包已发送");
            long elapsed = (long)fixture.Query("SELECT elapsed_ms FROM character_strongway2019_statistics WHERE character_id=$p0", peer.Id).Single()[0];
            history = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(history.AsSpan(12), checked((uint)elapsed));
            Bytes(clearHistory!, history, "简单成绩的前三个u32为0，末字段为原Run真实毫秒，不加匹配5秒");
            That(fixture.Query("SELECT dungeon_id,mode FROM character_strongway2019_statistics WHERE character_id=$p0", peer.Id) is [[5359L, 0L]] &&
                fixture.Query("SELECT dungeon_id,bucket FROM account_strongway2019_rewards WHERE character_id=$p0", peer.Id) is [[5359L, 0L]] &&
                fixture.Query("SELECT json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value') " +
                    "FROM mailbox_messages m JOIN mailbox_attachments a USING(mail_id) JOIN character_inventory_items i USING(item_uid) " +
                    "WHERE m.recipient_id=$p0 AND a.claimed=0", peer.Id) is [[490012735L, 1L]],
                "真实通关只记录一次角色成绩，并将735完整礼盒邮件与账号额度共同提交");
            string completed = Completed();
            peer.Send(39, finalDeath!);
            Drain(peer);
            That(Completed() == completed, "同一真实终点死亡重放不重复成绩、礼盒邮件或额度");
            peer.Send(42);
            var returned = Drain(peer);
            That(returned.Any(packet => packet is { Kind: 1, Opcode: 42, Body: [1] }) &&
                returned.Any(packet => packet is { Kind: 0, Opcode: 1491 } && packet.Body.SequenceEqual(history)) &&
                fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[65L, 1L]],
                "通关后经正式返城恢复65/1与同一角色历史");
            ClaimAndOpenReward(fixture, peer, resources.ItemResources, 490012735, [(490012698, 20), (490012699, 5)]);
            VerifyRewardShop(peer);
            peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
        }
        using (var peer = Open(fixture, "swtcpfull", gamePort: entry.Port, channelNumber: entry.Number))
        {
            var restored = peer.Select(high.Slot, prepared: true, resumeTown: true, room: false, strongwayHistory: history,
                strongwayAccountRewards: accountRewards);
            Bytes(peer.StrongwayHistory, history, "重连由持久化真实成绩恢复1491，不使用连接局部缓存");
            Bytes(peer.StrongwayAccountRewards, accountRewards, "重连由账号奖励账本恢复1753周次数");
            foreach (var (id, quantity) in new[] { (490012698, 15), (490012699, 5), (10305786, 200) })
                BoosterTcpTests.Entry(restored[4].Body, MainItemSlot(fixture, peer, id), id, quantity, full: true);
            That(MainItemCount(fixture, peer, 490012735) == 0 && MainItemCount(fixture, peer, 490013018) == 0,
                "重连恢复兑换和自选后的真实余额，已领取奖励盒与已开自选盒不复活");
            peer.Barrier();
        }
        VerifySoloHero(fixture, resources, entry, hero);
        Console.WriteLine($"PASS {Count - before} 强者之路真实TCP准入、活动开关、原Run通关与历史恢复");

        void VerifyRewardShop(TcpPeer peer)
        {
            var box = resources.ItemResources.Boosters.Get(490013018);
            That(box.Available && box.Selection is { SelectionCount: 1 } selection &&
                selection.Categories is [{ Category: 0, Subcategory: 0 } category] &&
                category.Rewards.Select(row => (row.ItemId, row.Count)).SequenceEqual(new[]
                    { (10305786, 200), (10305892, 150), (10305616, 50) }),
                "真实018保留三项完整材料选项，不以通用自选盒通过代替本资源");
            peer.Send(21, Buy());
            var ack = peer.Expect(1, 21).Body;
            That(ack.Length == 149 && ack[0] == 1 && U32(ack, 19) == 490013018 && U32(ack, 23) == 1 &&
                ack[140] == 1 && U32(ack, 141) == 490012698 && U32(ack, 145) == 15,
                "真实通关735的二十个证明在返回店086兑换018，仅扣五个");
            short slot = MainItemSlot(fixture, peer, 490013018);
            var bought = peer.Expect(0, 14).Body;
            BoosterTcpTests.Entry(bought, slot, 490013018, 1);
            BoosterTcpTests.Entry(bought, MainItemSlot(fixture, peer, 490012698), 490012698, 15);
            peer.Barrier();
            BoosterSelectionTcpTests.SendSelect(peer, slot, [10305786]);
            BoosterTcpTests.ExpectAck(peer, 490013018, slot, [(10305786, 200)]);
            var opened = peer.Expect(0, 14).Body;
            BoosterTcpTests.Entry(opened, slot, -1, 0);
            BoosterTcpTests.Entry(opened, MainItemSlot(fixture, peer, 10305786), 10305786, 200);
            peer.Barrier();
            That(MainItemCount(fixture, peer, 490013018) == 0 && MainItemCount(fixture, peer, 490012698) == 15 &&
                MainItemCount(fixture, peer, 490012699) == 5 && MainItemCount(fixture, peer, 10305786) == 200 &&
                MainItemCount(fixture, peer, 10305892) == 0 && MainItemCount(fixture, peer, 10305616) == 0,
                "真实CMD160仅给完整所选二百材料，不重复扣兑换成本或额外发未选两项");
            string committed = fixture.Snapshot();
            BoosterSelectionTcpTests.SendSelect(peer, slot, [10305786]);
            peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
            peer.Send(21, Buy()); peer.Expect(1, 21, [0, 0x85, 0]); peer.Barrier();
            That(fixture.Snapshot() == committed && MainItemCount(fixture, peer, 10305786) == 200 &&
                MainItemCount(fixture, peer, 490012698) == 15 &&
                fixture.Query("SELECT character_id,count FROM item_purchase_counts WHERE account='swtcpfull' AND item_id=490013018")
                    is [[0L, 1L]],
                "耗尽自选源重放及本账号已用周限购重买均拒绝，不多发材料、不再扣款或占额");
            byte[] Buy() => Build(writer =>
            { writer.Write(490013018); writer.Write(1); writer.Write(400000086); writer.Write(400001052); });
        }

        void VerifyNativeQuest()
        {
            events.Override(ServerEventService.Strongway2019Id, false);
            using (var peer = Open(fixture, "swtcpquest"))
            {
                peer.Select(questCharacter.Slot, prepared: true);
                That(!QuestLifecycleTcpTests.List(peer.AvailableQuests, 95).Contains(8698),
                    "普通频道活动关闭时21不投影8698，不能由客户端索引单独开放活动");
                peer.Send(31, [31, 0, 250, 33]);
                peer.Expect(1, 31, [0, 18, 0]); peer.Barrier();
                That(fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=8698", peer.Id) is [[0L]],
                    "关闭312的真实31不预写前置任务");
                peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
            }
            events.Override(ServerEventService.Strongway2019Id, true);
            using (var peer = Open(fixture, "swtcpquest"))
            {
                peer.Select(questCharacter.Slot, prepared: true);
                That(QuestLifecycleTcpTests.List(peer.AvailableQuests, 95).Contains(8698) &&
                    peer.CompletedQuestHistory[4 + 8698] == 0,
                    "普通频道312启用后21提供自然8698接取，356仍未完成");
                long gold = (long)fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id).Single()[0];
                peer.Send(31, [31, 0, 250, 33]);
                var accepted = Drain(peer);
                Bytes(accepted.Single(packet => packet is { Kind: 1, Opcode: 31 }).Body,
                    [1, 250, 33, 0, 0, 0, 0, 0], "真实31返回sub1零scalar及无接取赠品");
                That(accepted.Single(packet => packet is { Kind: 0, Opcode: 356 }).Body[4 + 8698] == 0 &&
                    fixture.Query("SELECT condition_state,length(acceptance_id) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=8698", peer.Id) is [[0L, 32L]],
                    "31只保存本次接取身份，不伪造完成频道解锁");
                events.Override(ServerEventService.Strongway2019Id, false);
                peer.Send(34, [34, 0, 250, 33, 255, 255, 1, 0, 255, 255]);
                peer.Expect(1, 34, [0, 19, 0]); peer.Barrier();
                That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id) is [[var unchanged]] && (long)unchanged == gold,
                    "接取后关闭活动拒绝真实34并保留金币");
                events.Override(ServerEventService.Strongway2019Id, true);
                peer.Send(34, [34, 0, 250, 33, 255, 255, 1, 0, 255, 255]);
                var finished = Drain(peer);
                var ack = finished.Single(packet => packet is { Kind: 1, Opcode: 34 }).Body;
                That(ack is [1, 250, 33, 4, ..] &&
                    finished.Last(packet => packet is { Kind: 0, Opcode: 356 }).Body[4 + 8698] == 1 &&
                    fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id) is [[var rewarded]] && (long)rewarded == gold + 1 &&
                    fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=8698", peer.Id) is [[0L]],
                    "真实34交付1金币、关闭原接取，最后356持久完成8698解锁频道");
                string after = fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=8698", peer.Id);
                peer.Send(34, [34, 0, 250, 33, 255, 255, 1, 0, 255, 255]);
                peer.Expect(1, 34, [0, 19, 0]); peer.Barrier();
                That(after == fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=8698", peer.Id) &&
                    fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id) is [[var duplicate]] && (long)duplicate == gold + 1,
                    "重复34不重发金币或改写完成收据");
                peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
            }
            using (var peer = Open(fixture, "swtcpquest"))
            {
                peer.Select(questCharacter.Slot, prepared: true);
                That(peer.CompletedQuestHistory[4 + 8698] == 1 &&
                    !QuestLifecycleTcpTests.List(peer.AvailableQuests, 95).Contains(8698),
                    "重连由原持久完成表恢复8698，不靠旧连接或GM补位");
                peer.Barrier();
            }
        }

        void VerifyDynamicEntryShop()
        {
            var price = resources.ItemResources.Items.Get(490013018).ShopPrice;
            That(price.Gold == 0 && price.Materials is [{ ItemId: 490012698, Count: 5 }],
                "TCP使用真实084商品及五个强者的证明成本");
            using var peer = Open(fixture, "swtcpquest");
            peer.Select(questCharacter.Slot, prepared: true);
            That(peer.CompletedQuestHistory[4 + 8698] == 1 &&
                fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[38L, 1L]],
                "真实31/34自然完成8698后仍在普通38/1，未GM补任务位");
            peer.Barrier();
            long material = ItemCount(490012698), product = ItemCount(490013018);
            That(material == 10 && product == 0, "真实Grant仅准备十材料，没有预种兑换奖励");
            events.Override(ServerEventService.Strongway2019Id, false);
            SendBuy(400000084, 400001051);
            var closed = ReadBuyReply(active: false, inventoryExpected: false);
            That(closed.Count(packet => packet is { Kind: 1, Opcode: 21, Body: [0, 17, 0] }) == 1 &&
                ItemCount(490012698) == material && ItemCount(490013018) == product,
                "关闭312时真实CMD21拒绝动态入口商店，材料与奖励不变");
            events.Override(ServerEventService.Strongway2019Id, true);
            SendBuy(400000086, 400001052);
            var wrong = ReadBuyReply(active: true, inventoryExpected: false);
            That(wrong.Count(packet => packet is { Kind: 1, Opcode: 21, Body: [0, 17, 0] }) == 1 &&
                ItemCount(490012698) == material && ItemCount(490013018) == product,
                "38/1不能购买65/1的返回店，不能以活动开放全局绕过商人在场");
            SendBuy(400000084, 400001051);
            var bought = ReadBuyReply(active: true, inventoryExpected: true);
            var ack = bought.Single(packet => packet is { Kind: 1, Opcode: 21 }).Body;
            That(ack.Length == 149 && ack[0] == 1 && U32(ack, 19) == 490013018 && U32(ack, 23) == 1 &&
                ack[140] == 1 && U32(ack, 141) == 490012698 && U32(ack, 145) == material - 5 &&
                bought.Count(packet => packet is { Kind: 0, Opcode: 14 }) == 1 &&
                ItemCount(490012698) == material - 5 && ItemCount(490013018) == product + 1,
                "真实CMD21经动态051/084扣五材料发一完整商品，ACK与库存差量及SQL一致");
            peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();

            long ItemCount(int item) => Convert.ToInt64(fixture.Query(
                "SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items " +
                "WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1", peer.Id, item).Single()[0]);
            void SendBuy(int shop, int npc) => peer.Send(21, Build(writer =>
            { writer.Write(490013018); writer.Write(1); writer.Write(shop); writer.Write(npc); }));

            WirePacket[] ReadBuyReply(bool active, bool inventoryExpected)
            {
                var packets = new List<WirePacket>();
                for (int at = 0; at < 8; at++)
                {
                    var packet = peer.Read();
                    packets.Add(packet);
                    if (packet is { Kind: 1, Opcode: 21 })
                    {
                        // ACK先于库存差量；屏障逐包校验随后的正式提交投影。
                        peer.Barrier(notification =>
                        {
                            ValidateNotification(notification, inventoryExpected);
                            packets.Add(notification);
                        });
                        return packets.ToArray();
                    }
                    ValidateNotification(packet, allowInventory: false);
                }
                throw new InvalidOperationException("FAIL 动态入口购买ACK21超过有界通知预算");

                void ValidateNotification(WirePacket packet, bool allowInventory)
                {
                    That(packet.Kind == 0, $"购买等待只接ACK21或已知通知，实际{packet.Kind}/{packet.Opcode}");
                    switch (packet.Opcode)
                    {
                        case 108:
                            That(packet.Protected, "活动目录仍沿保护压缩108发布");
                            using (var input = new MemoryStream(packet.Body))
                            using (var compressed = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress))
                            {
                                byte[] body = new byte[peer.EventInfoPayload.Length];
                                compressed.ReadExactly(body);
                                That(compressed.ReadByte() == -1, "312显隐不扩充或截断既有活动目录");
                                Bytes(body, peer.EventInfoPayload, "312开关保留选角时完整活动目录及原生设计页");
                            }
                            break;
                        case 706:
                            Bytes(packet.Body, peer.MileageLimits, "312开关保留既有完整积分限购通知");
                            break;
                        case 639:
                            That(!packet.Protected, "NPC显隐保持普通原生帧");
                            Strongway2019NpcTests.AssertDisplay(packet.Body, active);
                            break;
                        case 14:
                            var delta = packet.Body;
                            That(allowInventory && delta.Length == 249 && delta[0] == 0 && U16(delta, 1) == 2,
                                "仅成功ACK后发布材料与商品两格完整123B差量");
                            int[] offsets = [3, 126];
                            That(offsets.Count(offset => U32(delta, offset + 2) == 490012698 && U32(delta, offset + 6) == material - 5) == 1 &&
                                offsets.Count(offset => U32(delta, offset + 2) == 490013018 && U32(delta, offset + 6) == product + 1) == 1,
                                "库存差量逐项核对真实扣五材料及发一商品");
                            break;
                        default:
                            throw new InvalidOperationException($"FAIL 动态入口购买未登记通知{packet.Kind}/{packet.Opcode}");
                    }
                }
            }
        }

        Character Prepare(string account, string name, bool equipped)
        {
            using var owner = new AccountOwners().Acquire(account, () => { });
            var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), new(65, 1, 450, 234),
                resources.Towns.InitialReturn, resources.Learning.Get);
            var equipment = EquipmentIds.Select(id =>
            {
                var template = resources.ItemResources.Items.Get(id);
                return new InventoryItem(0, new(InventoryListType.Equipment, template.EquipmentSlot),
                    new(id, template.Kind, 0) { Durability = template.Durability });
            }).ToImmutableArray();
            var requested = candidate with
            {
                Level = 95, AcceptedStoryLevel = 95, EquipmentSlotFlags = 1,
                Inventory = candidate.Inventory with { Items = equipped ? equipment : [] },
                InitialLearning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning)
            };
            var created = repository.Create(owner, requested);
            var persisted = repository.List(owner).Single(row => row.Id == created.Id);
            That(created.EquipmentSlotFlags == requested.EquipmentSlotFlags &&
                persisted.EquipmentSlotFlags == requested.EquipmentSlotFlags,
                $"新建角色返回与持久化重读均保留真实装备槽解锁：requested={requested.EquipmentSlotFlags}," +
                $" returned={created.EquipmentSlotFlags}, persisted={persisted.EquipmentSlotFlags}");
            return created;
        }

        void Select(TcpPeer peer, ushort slot)
        {
            That(peer.LoginEndpoint[3] == 55, "LOGIN实际绑定内容频道type55，不用普通频道冒充");
            peer.Select(slot, prepared: true, resumeTown: true, room: false);
            Bytes(peer.StrongwayHistory, new byte[16], "新角色入城1491初始化四字段全零，不沿用其他角色");
            That(fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[65L, 1L]],
                "内容频道真实恢复65/1入口场景");
            peer.Barrier();
        }

        void RejectEntry(TcpPeer peer, string reason, uint dungeon = 5359)
        {
            string previous = Assets(peer.Id);
            var request = EntryRequest();
            BinaryPrimitives.WriteUInt32LittleEndian(request, dungeon);
            peer.Send(16, request);
            var rejected = Drain(peer);
            That(rejected.Count(packet => packet is { Kind: 1, Opcode: 16, Body.Length: 4 } && packet.Body[0] == 0 && packet.Body[3] == 0) == 1 &&
                !rejected.Any(packet => packet.Kind == 0 && packet.Opcode is 28 or 29) && Assets(peer.Id) == previous,
                reason + "时按ACK16结束等待，不预留attempt、不扣资产、不创建原Run");
            int ack = Array.FindIndex(rejected.ToArray(), packet => packet is { Kind: 1, Opcode: 16 });
            int returned = Array.FindIndex(rejected.ToArray(), packet => packet is { Kind: 1, Opcode: 132, Body: [1] });
            int area = Array.FindIndex(rejected.ToArray(), packet => packet is { Kind: 0, Opcode: 24 });
            int notice = Array.FindIndex(rejected.ToArray(), packet => packet.Kind == 0 && packet.Opcode == (ushort)NotiPacketType.AAS_TIPS);
            That(returned > ack && area > returned && notice > area,
                reason + "失败后自动返回准备区并显示原因，不能要求玩家在空选图页手动退出");
        }

        string Experience(ushort id) => fixture.Snapshot("SELECT experience FROM characters WHERE id=$p0", id) +
            fixture.Snapshot("SELECT proficiency_level,proficiency_experience FROM character_dungeon_state WHERE character_id=$p0", id);
        string Assets(ushort id) => fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", id) +
            fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", id) +
            fixture.Snapshot("SELECT * FROM character_fatigue WHERE character_id=$p0", id) + Completed();
        string Completed() => fixture.Snapshot("SELECT * FROM character_strongway2019_statistics ORDER BY character_id,instance_id") +
            fixture.Snapshot("SELECT * FROM account_strongway2019_rewards ORDER BY account,instance_id") +
            fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id") +
            fixture.Snapshot("SELECT * FROM mailbox_attachments ORDER BY attachment_id");
    }

    // 复用两组已真实通关的邮件；不注入奖励或创建另一套TCP fixture。
    internal static void ClaimAndOpenReward(TcpTestFixture fixture, TcpPeer peer, ItemResources resources,
        int boxId, (int Id, int Count)[] rewards)
    {
        var definition = resources.Boosters.Get(boxId);
        That(definition.Available && definition.Selection is null && definition.Pools.Length == rewards.Length &&
            definition.Pools.Select((pool, index) => pool.Draws == 1 && pool.Rewards is [{ } row] &&
                row.ItemId == rewards[index].Id && row.Count == rewards[index].Count && row.Weight == 1000).All(value => value),
            "真实奖励礼盒完整固定池与独立数量合同一致：" + boxId);
        That(MainItemCount(fixture, peer, boxId) == 0 && rewards.All(row => MainItemCount(fixture, peer, row.Id) == 0),
            "奖励链开始时盒仍在邮件，Main没有预种材料或礼盒");
        string quota = fixture.Snapshot("SELECT * FROM account_strongway2019_rewards WHERE character_id=$p0 ORDER BY instance_id", peer.Id);
        peer.Send(96); peer.Expect(1, 96, [1, 0, 0]);
        var mail = MailboxTcpTests.ReadList(peer.Expect(0, 97).Body).Summaries.Single(row => row.ItemId == boxId);
        peer.Barrier();
        long uid = Convert.ToInt64(fixture.Query("SELECT item_uid FROM mailbox_attachments WHERE mail_id=$p0 AND claimed=0", mail.MailId).Single()[0]);
        byte[] claim = Build(writer => { writer.Write(1); writer.Write(mail.ClaimId); });
        peer.Send(95, claim);
        peer.Expect(1, 95, Build(writer =>
        { writer.Write((byte)1); writer.Write(1); writer.Write(mail.MailId); writer.Write(mail.ClaimId); writer.Write(0); }));
        var claimed = peer.Expect(0, 14).Body;
        short slot = MainItemSlot(fixture, peer, boxId);
        BoosterTcpTests.Entry(claimed, slot, boxId, 1); peer.Barrier();
        That(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=$p1", peer.Id, slot)
                is [[long stored]] && stored == uid &&
            fixture.Query("SELECT claimed,item_uid FROM mailbox_attachments WHERE mail_id=$p0", mail.MailId) is [[1L, DBNull]],
            "CMD95一次转移原邮件UID并原子清掉附件资格：" + boxId);
        string claimedState = State();
        peer.Send(95, claim); peer.Expect(1, 95, [0, 21, 0]); peer.Barrier();
        That(State() == claimedState, "同一附件领取重放不改变库存、UID、revision或审计");
        byte[] open = Build(writer => { writer.Write(slot); writer.Write(1); });
        peer.Send(160, open); BoosterTcpTests.ExpectAck(peer, boxId, slot, rewards);
        var opened = peer.Expect(0, 14).Body;
        BoosterTcpTests.Entry(opened, slot, -1, 0);
        foreach (var row in rewards)
        {
            That(MainItemCount(fixture, peer, row.Id) == row.Count, "正式奖励盒完整发放真实材料：" + row.Id);
            BoosterTcpTests.Entry(opened, MainItemSlot(fixture, peer, row.Id), row.Id, row.Count);
        }
        peer.Barrier();
        That(MainItemCount(fixture, peer, boxId) == 0, "奖励礼盒仅消费一件且源耗尽：" + boxId);
        string openedState = State();
        peer.Send(160, open); peer.Expect(1, 160, [0, 19, 0]); peer.Barrier();
        That(State() == openedState &&
            fixture.Snapshot("SELECT * FROM account_strongway2019_rewards WHERE character_id=$p0 ORDER BY instance_id", peer.Id) == quota,
            "耗尽奖励盒重放不多发材料，领取与开盒均不改变已提交账号奖励额度");

        string State() => fixture.Snapshot("SELECT id,revision,gold FROM characters WHERE id=$p0", peer.Id) +
            fixture.Snapshot("SELECT i.item_uid,i.item_core,s.list_type,s.slot_index FROM character_inventory_items i " +
                "LEFT JOIN character_inventory_slots s USING(item_uid) WHERE i.character_id=$p0 ORDER BY i.item_uid", peer.Id) +
            fixture.Snapshot("SELECT * FROM mailbox_attachments WHERE mail_id=$p0", mail.MailId) +
            fixture.Snapshot("SELECT count(*) FROM inventory_audit_log WHERE character_id=$p0", peer.Id);
    }

    static long MainItemCount(TcpTestFixture fixture, TcpPeer peer, int itemId) => Convert.ToInt64(fixture.Query(
        "SELECT coalesce(sum(json_extract(i.item_core,'$.Value')),0) FROM character_inventory_items i " +
        "JOIN character_inventory_slots s USING(item_uid) WHERE i.character_id=$p0 AND s.list_type=0 " +
        "AND json_extract(i.item_core,'$.ItemId')=$p1", peer.Id, itemId).Single()[0]);

    static short MainItemSlot(TcpTestFixture fixture, TcpPeer peer, int itemId) => Convert.ToInt16(fixture.Query(
        "SELECT s.slot_index FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) " +
        "WHERE i.character_id=$p0 AND s.list_type=0 AND json_extract(i.item_core,'$.ItemId')=$p1", peer.Id, itemId).Single()[0]);

    static (byte Number, int Port) ContentEntry(TcpTestFixture fixture)
    {
        using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "ready.json")));
        var entries = ready.RootElement.GetProperty("ChannelEntries").EnumerateArray()
            .Where(row => row.GetProperty("Entry").GetProperty("Type").GetInt32() == 55).ToArray();
        That(entries.Length == 1, "正式频道配置必须提供唯一type55入口，缺失不能当作TCP已验证或悄然跳过");
        var entry = entries.Single();
        return (entry.GetProperty("Entry").GetProperty("Identity").GetProperty("Number").GetByte(), entry.GetProperty("Port").GetInt32());
    }

    static void BeginSelection(TcpPeer peer)
    {
        peer.Send(15, new byte[4]);
        var selected = Drain(peer);
        That(selected.Count(packet => packet is { Kind: 1, Opcode: 15, Body: [1] }) == 1 &&
            selected.Count(packet => packet is { Kind: 0, Opcode: 27 }) == 1,
            "CMD15沿真实内容频道打开原生副本选择，不直接调用Session改变阶段");
    }

    static byte[] EntryRequest()
    {
        var request = new byte[25];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 5359);
        request[4] = 2; request[9] = request[10] = 255;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(21), uint.MaxValue);
        return request;
    }

    static Stack<DungeonPoint> Route(DungeonMaze maze, DungeonPoint start, DungeonPoint goal)
    {
        var links = maze.StaticTopology.Links ?? throw new InvalidDataException("首期TCP需要真实静态路线");
        var parents = new Dictionary<DungeonPoint, DungeonPoint?> { [start] = null };
        var queue = new Queue<DungeonPoint>(); queue.Enqueue(start);
        while (queue.TryDequeue(out var point) && !parents.ContainsKey(goal))
        foreach (var link in links)
        {
            DungeonPoint? adjacent = link.First == point ? link.Second : link.Second == point ? link.First : null;
            if (adjacent is { } next && parents.TryAdd(next, point)) queue.Enqueue(next);
        }
        That(parents.ContainsKey(goal), "NOTI28公布的真实终点在本次资源拓扑中可达");
        var path = new Stack<DungeonPoint>();
        for (var point = goal; parents[point] is { } parent; point = parent) path.Push(point);
        return path;
    }
}
