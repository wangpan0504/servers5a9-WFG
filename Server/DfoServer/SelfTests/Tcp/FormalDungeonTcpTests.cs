using System.Text.Json;
using System.Buffers.Binary;
using System.Security.Cryptography;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.Game.Quests;
using DfoServer.Development.Dungeons;
using DfoServer.Network.Management;
using DfoServer.Game.Inventory.Models;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    // 本组验证穿脱、奖励与冷启动，经过时间由宠物专用用例推进；UTC仍用真实时间，保留期限与日期判断。
    sealed class FixedElapsedClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        FormalDungeonSessionTests.VerifyLoginWearWindow(resources.Towns, resources.Archive, resources.Characters);
        var growth = SelfTestCatalogs.Growth(resources.Archive);
        var kills = SelfTestCatalogs.KillExperience(resources.Archive);
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled }, Path.Combine(root, "formal-dungeon"), gm: true, resources: resources,
            clock: new FixedElapsedClock());
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort,"infinite-fatigue off",out var fatigueMode) &&
            fatigueMode == "OK {\"infiniteFatigue\":false}", "正式副本扣点回归显式关闭无限疲劳");
        byte[]? oldReport = null;
        ulong experience = 0;
        ushort nextKey;
        int picked = 0;
        int upgradedRoomMoves = 0;
        byte projectedLevel = 1;
        string completedOrdinaryState = "";
        long completedOrdinaryRevision = 0;
        int completedOrdinaryGoldLimit = 0, expectedOrdinaryGoldLimit = 0;
        bool expectContractOverview = false;
        using (var peer = Open(fixture))
        {
            peer.Create("FormalTcp");
            using (var owner = new DfoServer.Game.Accounts.AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var repository = new DfoServer.Persistence.CharacterRepository(fixture.Database);
                var character = repository.LoadCharacter(owner, 0);
                var items = resources.ItemResources;
                var grants = new DfoServer.Game.Inventory.Services.InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
                character = DfoServer.Game.Inventory.Services.InventoryService.Commit(repository, owner, character, grants.Grant(character, [new(1000, 5), new(1, 2)]));
                PrepareDelilahServices(repository,owner,character,resources);
            }
            fixture.Sql("UPDATE character_dungeon_state SET next_object_key=65536 WHERE character_id=$p0", peer.Id);
            peer.Select();
            EntryTown(peer);
            var map = Enter(peer);
            That(U16(map, 25) == 256 && fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[1L]],
                "耗尽旧角色经真实TCP恢复首图，沿完整击杀/拾取/结算流程仅预留一次尝试");
            RejectDelilahServices(fixture,peer,"Loading");
            RejectDungeonShop(fixture,peer);
            var target = new (byte X, byte Y)[] { (1, 1), (2, 1), (2, 0), (3, 0), (4, 0) };
            int[] ids = [76121, 76122, 76123, 76124, 76125, 76126];
            int movedSkill = -1;
            for (int room = 0; room < ids.Length; room++)
            {
                byte roomLevel = projectedLevel;
                That(U32(map, 14) == ids[room], "正式TCP依匹配3145六房顺序加载");
                Load(peer);
                if (room == 0)
                {
                    VerifyRevive(fixture, peer);
                    peer.Send(191, [0,0]);
                    That(peer.Expect(0,170).Body.SequenceEqual(new byte[] { (byte)peer.Id,(byte)(peer.Id >> 8),0,0 }),
                        "实收普通演出暂停请求只同步本人170");
                    peer.Send(191, [1,0]);
                    That(peer.Expect(0,170).Body.SequenceEqual(new byte[] { (byte)peer.Id,(byte)(peer.Id >> 8),1,0 }) &&
                        peer.Expect(0,3).Body.SequenceEqual(new byte[] { 1,(byte)peer.Id,(byte)(peer.Id >> 8),1 }),
                        "演出恢复后按170→3重新激活角色，随后继续原击杀与过房流程");
                    peer.Barrier();
                }
                if (room == 0)
                {
                    movedSkill = VerifyDungeonActions(fixture, peer);
                    RejectDelilahServices(fixture,peer,"Dungeon");
                    VerifyDungeonShop(fixture,peer,"Dungeon");
                }
                if (room == 0)
                {
                    peer.Send(39, Death(ushort.MaxValue,ushort.MaxValue,95));
                    peer.Barrier();
                    That(Experience(fixture,peer.Id) == experience,"未登记死亡目标只审计忽略，不使用包尾等级伪造奖励");
                }
                string beforeMovement = fixture.Snapshot();
                peer.Send(35, Move(1000, 249));
                peer.Barrier();
                That(fixture.Snapshot() == beforeMovement, "正式副本阻止城镇位置保存");
                if (oldReport is not null)
                {
                    peer.Send(39, oldReport);
                    peer.Barrier();
                    That(Experience(fixture, peer.Id) == experience, "新房旧key只审计忽略，不重复经验或死亡通知");
                }
                int count = map[18];
                for (int index = 0; index < count; index++)
                {
                    int offset = 19 + index * 22;
                    ushort key = U16(map, offset + 6);
                    byte level = map[offset + 12];
                    if (map[offset + 13] == 9)
                    {
                        That(room == 4 && count == 7 && U32(map, offset + 8) == 225 && level == 0 &&
                            map[offset + 14] == 0 && map[offset + 15] == 0 && map[offset + 16] == 255 && map[offset + 21] == 1,
                            "76125完整生成特殊父对象225，类型9不冒充可击杀的普通怪物");
                        continue;
                    }
                    That(level == (room == 4 && index == 4 ? 5 : 3), "TCP生成等级包含副本基准5加偏移0的哥布林");
                    uint expectedGain = level == 5 ? 137u : projectedLevel < 3 ? 104u : 93u;
                    byte[] report = Death(key, peer.Id, level);
                    if (room == ids.Length - 1 && U32(map, offset + 8) == 107000906)
                    {
                        That(U32(map,offset+2) == 1 && map[offset+21] == 1 && map[offset+14] == 0 &&
                            map[offset+15] == 0 && map[offset+16] == 255, "托比仅设置清房排除位，保留普通创建字段与原资源序号");
                        peer.Send(39, report); peer.Expect(0,38); peer.Barrier();
                        That(Experience(fixture, peer.Id) == experience &&
                            fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0 AND monster_key=$p1", peer.Id, key) is [[0L]],
                            "托比保留真实场景key，但友方死亡报告不能授予经验、掉落或阻塞已开启的结算");
                        continue;
                    }
                    if (room == 1 && index == 0)
                        report = Death(key, peer.Id, level, 696, 3, 2, 831);
                    else if (room == 1 && index == 2)
                        report = Death(key, peer.Id, level, 1528, 6, 5, 1728);
                    else if (room == 2 && index == 0)
                        report = Death(key, peer.Id, level, 400, 4, 3, 800);
                    if (oldReport is null)
                    {
                        byte[] altered = (byte[])report.Clone();
                        altered[84] ^= 1;
                        peer.Send(39, altered);
                        peer.Barrier();
                        That(Experience(fixture, peer.Id) == 0, "不匹配正文检查值不能发奖");
                        report = Death(key, peer.Id, 2, 0, 0, 0, 0);
                        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(4),ushort.MaxValue);
                        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(31),0x0211);
                        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(33),ushort.MaxValue);
                        SealDeath(report);
                    }
                    peer.Send(39, report);
                    byte[] deathNotice = peer.Expect(0, 38).Body;
                    int dropCount = U16(deathNotice, 2);
                    That(U16(deathNotice, 0) == key && dropCount <= 500 && deathNotice.Length == 9 + 131 * dropCount &&
                        deathNotice.AsSpan(deathNotice.Length - 5).SequenceEqual(new byte[] { 0, 0, 255, 0, 0 }),
                        "正式死亡接受FFFF报告者、0211来源及不同等级/零扣血统计，投影保留完整掉落行及尾部");
                    byte[] notice = ReadGrowth(room == ids.Length - 1 && map[offset + 13] == 3);
                    experience += expectedGain;
                    byte expectedLevel = 1;
                    while (experience >= growth.GetCumulativeExperienceBoundary(expectedLevel)) expectedLevel++;
                    That(notice.Length == 91 && notice[0] == expectedLevel && U64(notice, 1) == experience &&
                        U32(notice, 9) == 0 && U16(notice, 13) == growth.GetSpDelta(1, expectedLevel) &&
                        U16(notice, 15) == growth.GetSpDelta(1, expectedLevel), "完整NOTI37绝对经验等级及两树SP来自已提交角色");
                    if (room == ids.Length - 1 && map[offset + 13] == 3)
                    {
                        int bossOffset = 19 + Enumerable.Range(0, count).Single(row => map[19 + row * 22 + 13] == 3) * 22;
                        ushort bossKey = U16(map, bossOffset + 6);
                        peer.Expect(0, 115, [1, 1, (byte)bossKey, (byte)(bossKey >> 8)]);
                        peer.Expect(0, 31, []);
                        byte[] bossCheck = new byte[39];
                        BinaryPrimitives.WriteUInt16LittleEndian(bossCheck, peer.Id);
                        BinaryPrimitives.WriteUInt16LittleEndian(bossCheck.AsSpan(2), bossKey);
                        BinaryPrimitives.WriteUInt32LittleEndian(bossCheck.AsSpan(13), map[bossOffset + 12]);
                        BinaryPrimitives.WriteUInt64LittleEndian(bossCheck.AsSpan(17), U64(report, 60));
                        peer.Send(117, bossCheck);
                        peer.Barrier();
                        That(Experience(fixture, peer.Id) == experience, "117仅协调已死亡Boss，已开启结算不重复发31或经验");
                    }
                    picked += PickupResourceDrops(fixture, peer, deathNotice, 3145);
                    string growthBeforeReplay = fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts ORDER BY character_id,event_id");
                    string lootBeforeReplay = fixture.Snapshot("SELECT * FROM character_dungeon_loot_receipts ORDER BY character_id,event_id");
                    string inventoryBeforeReplay = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
                    peer.Send(39, report);
                    peer.Barrier();
                    byte[] changedReplay = (byte[])report.Clone();
                    BinaryPrimitives.WriteUInt64LittleEndian(changedReplay.AsSpan(18), U64(changedReplay, 18) + 1);
                    SealDeath(changedReplay);
                    peer.Send(39, changedReplay);
                    peer.Barrier();
                    That(Experience(fixture, peer.Id) == experience &&
                        fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts ORDER BY character_id,event_id") == growthBeforeReplay &&
                        fixture.Snapshot("SELECT * FROM character_dungeon_loot_receipts ORDER BY character_id,event_id") == lootBeforeReplay &&
                        fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") == inventoryBeforeReplay,
                        "同怪重复及统计变化只审计忽略，不追加通知、经验、掉落receipt或物品UID");
                    oldReport = report;
                }
                if (room < target.Length)
                {
                    byte[] move = new byte[100];
                    move[0] = target[room].X;
                    move[1] = target[room].Y;
                    peer.Send(45, move);
                    peer.Expect(1, 45, [1]);
                    map = peer.Expect(0, 29).Body;
                    if (projectedLevel > roomLevel) upgradedRoomMoves++;
                }
            }
            That(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", peer.Id) is [[28L]],
                "六房28个敌对对象各一条成长事实，友方托比没有伪造击杀或额外经验");
            That(experience == 2868 && projectedLevel == 3 && Experience(fixture, peer.Id) == 2868,
                "Lv1零经验起步完整六房得到2868经验，升级后逐次调整击杀倍率");
            That(upgradedRoomMoves > 0, "副本升级只发Player后仍能清完当前房间、成功换房并加载下一房");
            var clearCatalog = SelfTestCatalogs.ClearExperience(resources.Archive);
            RejectDelilahServices(fixture,peer,"Settlement before commit");
            // 修复后击杀XP为2868；最高评分456使首次通关跨过Lv3边界3315。
            var clearReward = clearCatalog.Calculate(SelfTestCatalogs.Dungeons(resources.Archive).Get(3), 0, 99);
            uint clearExperience = clearReward.TotalExperience;
            byte[] reportResult = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id, 99);
            byte[] corruptedResult = (byte[])reportResult.Clone(); corruptedResult[49] ^= 1;
            peer.Send(46, corruptedResult); peer.Barrier();
            That(Experience(fixture, peer.Id) == experience, "46检查值错误不触发结算或经验");
            string assetsBeforeClear = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
            peer.Send(46, reportResult);
            ReadClear();
            That(fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") == assetsBeforeClear &&
                fixture.Query("SELECT claimed FROM character_dungeon_card_rewards WHERE character_id=$p0",peer.Id) is [[0L]],
                "真实通关冻结牌面但不提前写背包或领取位");
            assetsBeforeClear = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
            experience += clearExperience;
            That(Experience(fixture, peer.Id) == experience && fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0", peer.Id) is [[1L]],
                "通关基础与评分经验仅一次提交，击杀经验不再累计");
            peer.Send(46, reportResult); ReadClear(duplicate: true);
            That(Experience(fixture, peer.Id) == experience, "同报告46回放投影不再加经验");
            byte[] changedResult = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id, 90);
            peer.Send(46, changedResult); peer.Barrier();
            That(Experience(fixture, peer.Id) == experience, "改变评分的46不能替换冻结报告");
            VerifyDelilahServices(fixture,peer,resources);
            VerifyDungeonShop(fixture,peer,"Settlement");
            assetsBeforeClear = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
            peer.Send(69); peer.Expect(1, 69, [1]);
            peer.Send(70); That(peer.Expect(1, 70).Body.Length == 17, "70普通八槽完整ACK");
            var cardRow = fixture.Query("SELECT reward_json FROM character_dungeon_card_rewards WHERE character_id=$p0 ORDER BY revision DESC LIMIT 1",peer.Id).Single();
            var cardReward = JsonSerializer.Deserialize<DfoServer.Game.Dungeons.DungeonCardReward>((string)cardRow[0])!;
            var automaticCard = peer.Expect(1,71).Body;
            That(automaticCard.Length == 33 && automaticCard[0] == 1 && automaticCard[1] == 0 && automaticCard[2] == 255,
                "70后真实计时自动选择本人免费席，提交成功后才揭示牌面");
            That(DrainCardInventory(peer) > 0 && fixture.Query("SELECT claimed FROM character_dungeon_card_rewards WHERE character_id=$p0",peer.Id) is [[1L]],
                "自动翻牌实际提交金币/物品和领取位，ACK后完整同步库存差量");
            if (cardReward.Item is { } cardItem)
            {
                var location = fixture.Query("SELECT s.list_type,s.slot_index FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid WHERE s.character_id=$p0 AND json_extract(i.item_core,'$.ItemId')=$p1 ORDER BY i.item_uid DESC LIMIT 1",peer.Id,cardItem.ItemId).Single();
                long goldBefore = Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id)[0][0]);
                peer.Send(22,Build(w => { w.Write(Convert.ToByte(location[0]));w.Write(Convert.ToInt16(location[1]));w.Write(1);w.Write(1000);w.Write(1000);w.Write(0u); }));
                That(peer.Expect(1,22).Body[0] == 1,"生产德利拉结算窗口19B实收形状可出售刚取得的免费牌物品");
                DrainCardInventory(peer);
                That(Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id)[0][0]) == goldBefore +
                    resources.ItemResources.Items.Get(cardItem.ItemId).ShopPrice.GetSellGold(cardItem.Durability,resources.ItemResources.Items.Get(cardItem.ItemId).Durability),
                    "德利拉出售按完整冻结物品耐久折算价实际增加金币");
                assetsBeforeClear = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
            }
            string cardState = fixture.Snapshot("SELECT * FROM character_dungeon_card_rewards ORDER BY character_id,instance_id");
            peer.Send(71, [0, 0]); peer.Expect(1,71,automaticCard);
            That(fixture.Snapshot("SELECT * FROM character_dungeon_card_rewards ORDER BY character_id,instance_id") == cardState,
                "自动翻牌后的手动重复只回放展示，不追加随机奖励回执");
            SelectPaid(false);
            assetsBeforeClear = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
            peer.Send(72, [2, 2]); peer.Expect(1, 72, [1, 2, 2]);
            peer.Send(72, [1, 2]); peer.Expect(1, 72, [1, 1, 2]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
            That(peer.Expect(0, 24).Body[11] == 3, "正式副本结果页回城保留转职特效显示位");
            That(QuestLifecycleTcpTests.List(peer.Expect(0, 21).Body, projectedLevel).Contains(3145),
                "结果页72回城在绑定场景后刷新当前任务，未领奖的任务仍保留");
            peer.Barrier();
            That(fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") == assetsBeforeClear &&
                Experience(fixture, peer.Id) == experience, "免费翻牌展示及回城保留已提交资产，不再发物品或经验");
            var fatigueBeforeBlack = fixture.Query("SELECT f.used,f.maximum,f.remaining,c.revision FROM character_fatigue f JOIN characters c ON c.id=f.character_id WHERE c.id=$p0",peer.Id).Single();
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort,"black-diamond FormalTcp 30",out string blackReply) && blackReply.StartsWith("OK ") &&
                DevelopmentGmClient.TryExecute(fixture.Options.GmPort,"gold-card FormalTcp 30",out string goldReply) && goldReply.StartsWith("OK "),
                "在线GM为当前账号保存黑钻及58期限，启用魔王服务0但不隐式启用双倍");
            peer.CheckBlackDiamondRefresh(active: true);
            expectContractOverview = true;
            // 主线已接入黑钻通关加成；重新入图后的到账按PVF比例加入基础经验加成，评分不参与。
            clearExperience = checked(clearReward.TotalExperience + (uint)decimal.Floor(clearReward.BaseExperience *
                ExperienceBonusCatalog.Load(resources.Archive).BlackDiamondPercent / 100m));
            var fatigueAfterBlack = fixture.Query("SELECT f.used,f.maximum,f.remaining,c.revision FROM character_fatigue f JOIN characters c ON c.id=f.character_id WHERE c.id=$p0",peer.Id).Single();
            That(Convert.ToInt64(fatigueAfterBlack[0]) == Convert.ToInt64(fatigueBeforeBlack[0]) &&
                Convert.ToInt64(fatigueAfterBlack[1]) == Convert.ToInt64(fatigueBeforeBlack[1])+32 &&
                Convert.ToInt64(fatigueAfterBlack[2]) == Convert.ToInt64(fatigueBeforeBlack[2])+32 &&
                Convert.ToInt64(fatigueAfterBlack[3]) == Convert.ToInt64(fatigueBeforeBlack[3])+2,
                "黑钻与58两次GM设置各按统一事务推进角色revision，黑钻容量只补32点一次");
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort,"event on 64",out string eventReply) && eventReply.StartsWith("OK "),
                "本机GM启用全服活动64，已有魔王资格与活动共同进入免费双倍牌，不依赖回归标志");
            ClearAgain(Enter(peer));
            peer.Send(46, reportResult); ReadClear(); experience += clearExperience;
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort,"event off 64",out string offReply) && offReply.StartsWith("OK "),
                "牌面冻结后关闭活动只影响下一份计划，本次仍免金币并按原x2发奖");
            peer.Send(69);peer.Expect(1,69,[1]);peer.Send(70);peer.Expect(1,70);
            var paidBeforeAuto = SelectPaid(true);
            var completeView = peer.Expect(1,71).Body;
            That(completeView.Length == paidBeforeAuto.Length && completeView[1] == 0 && completeView[2] == 0 &&
                completeView.AsSpan(3).SequenceEqual(paidBeforeAuto.AsSpan(3)),
                "付费或黄金牌先选后自动免费揭示仍保留同一付费奖励，计时不会覆盖已取得牌面");
            That(DrainCardInventory(peer) > 0,
                "付费先选后的自动免费与黑钻领取仍实际发布各自库存差量");
            peer.Send(72, [1, 0]); peer.Expect(1, 72, [1, 1, 0]); peer.Expect(0, 27, new byte[42]);
            peer.Expect(0, 28);
            byte[] retryMap = peer.Expect(0, 29).Body;
            That(U16(retryMap, 25) > U16(map, 25), "再挑战创建新Run、新尝试及新怪key");
            ClearAgain(retryMap);
            peer.Send(46, reportResult); ReadClear(); experience += clearExperience;
            var singleGold = JsonSerializer.Deserialize<DfoServer.Game.Dungeons.DungeonCardOptions>((string)fixture.Query(
                "SELECT options_json FROM character_dungeon_card_options WHERE character_id=$p0 ORDER BY rowid DESC LIMIT 1",peer.Id).Single()[0])!;
            That(singleGold is { UseGoldCard:true, Price:0, DoublePaidItems:false, LegacyReturningPlayer:false } && peer.DevilService0[13] == 1,
                "关闭活动后的新副本走单倍免费黄金牌，未领取不提前消耗第二次资格");
            peer.Send(72, [1, 1]); peer.Expect(1, 72, [1, 1, 2]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
            peer.Send(72, [1, 3]); peer.Barrier();
            That(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) is [[3L]],
                "剧情没有已接取且合法的后继任务时明确回城，不等待15或生成新副本");
            SelectMap(peer);
            peer.Send(132); peer.Expect(1, 132, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]); peer.Expect(0, 23); peer.Expect(0, 24);
            peer.Barrier();
            That(Experience(fixture, peer.Id) == experience && fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0", peer.Id) is [[3L]],
                "再挑战各一次结算，重选按真实15/132退出，旧报告不跨实例重复发奖");
            nextKey = checked((ushort)Convert.ToInt64(fixture.Query("SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0]));

            void ReadClear(bool duplicate = false)
            {
                byte[] result = peer.Expect(0, 34).Body;
                byte[] exp = ReadGrowth();
                ulong expectedExperience = experience + (duplicate ? 0 : clearExperience);
                byte expectedLevel = 1;
                while (expectedExperience >= growth.GetCumulativeExperienceBoundary(expectedLevel)) expectedLevel++;
                peer.ReadCardMembershipState();
                if (expectContractOverview)
                    That(peer.PremiumServices.GetValueOrDefault((byte)58) > 0 && U32(peer.DevilService0,9) > 0,
                        "有效type58契约期限与免费黄金牌服务0在结算35之前同步");
                byte[] clear = peer.Expect(0, 35).Body;
                That(result.Length == 16 && result[6] == 99 && U16(result, 9) == peer.Id && exp.Length == 91 &&
                    U64(exp, 1) == expectedExperience && exp[0] == expectedLevel &&
                    U32(exp, 9) == 0 && clear.Length >= 218 && U32(clear, 0) == clearReward.BaseExperience &&
                    U32(clear, 4) == clearReward.RankExperience,
                    "正式结算34→37→35匹配宽度，显示冻结评分及已提交奖励");
                ReadCommittedCard(fixture,peer,clear,duplicate);
            }

            byte[] SelectPaid(bool golden)
            {
                var optionsRow = fixture.Query("SELECT options_json FROM character_dungeon_card_options WHERE character_id=$p0 ORDER BY rowid DESC LIMIT 1",peer.Id).Single();
                var options = JsonSerializer.Deserialize<DfoServer.Game.Dungeons.DungeonCardOptions>((string)optionsRow[0])!;
                if (!golden)
                {
                    That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort,"gold FormalTcp 10000",out string answer) && answer.StartsWith("OK "),
                        "普通付费牌测试提供足够金币，既有窗口内更新不重抽牌面");
                }
                long before = Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id)[0][0]);
                peer.Send(71,[1,0]);
                byte[] selected = peer.Expect(1,71).Body;
                var rewardRow = fixture.Query("SELECT paid_reward_json,paid_cost FROM character_dungeon_card_options WHERE character_id=$p0 ORDER BY rowid DESC LIMIT 1",peer.Id).Single();
                var actual = JsonSerializer.Deserialize<DfoServer.Game.Dungeons.DungeonCardReward>((string)rewardRow[0])!;
                That(selected[0] == 1 && selected[2] == 0 && selected.Length == 33+selected[3]*8 &&
                    options.UseGoldCard == golden && (golden ? options.Price == 0 : options.Price > 0) &&
                    Convert.ToInt64(rewardRow[1]) == options.Price &&
                    Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id)[0][0]) == before-options.Price+actual.Gold,
                    "71实际费用、金币和付费条目按本次冻结及持久回执核对，不借客户端会员标志");
                peer.ReadCardMembershipState();
                That(golden ? peer.DevilService0[13] == 1 &&
                    fixture.Query("SELECT used FROM character_gold_card_uses WHERE character_id=$p0",peer.Id) is [[1L]] :
                    U32(peer.DevilService0,9) == 0 &&
                    fixture.Query("SELECT count(*) FROM character_gold_card_uses WHERE character_id=$p0",peer.Id) is [[0L]],
                    "魔王免费黄金牌成功后刷新已用1次；普通金币牌不创建免费次数");
                DrainCardInventory(peer);
                string saved = fixture.Snapshot();
                peer.Send(71,[1,0]);peer.Expect(1,71,selected);peer.ReadCardMembershipState();peer.Barrier();
                That(fixture.Snapshot() == saved,"真实网络重复付费请求不重复扣金币、消耗次数、写奖励或邮件");
                return selected;
            }

            void ClearAgain(byte[] startMap)
            {
                for (int room = 0; room < ids.Length; room++)
                {
                    Load(peer);
                    int count = startMap[18];
                    for (int index = 0; index < count; index++)
                    {
                        int offset = 19 + index * 22;
                        if (startMap[offset + 13] == 9 || U32(startMap, offset + 8) == 107000906) continue;
                        peer.Send(39, Death(U16(startMap, offset + 6), peer.Id, startMap[offset + 12]));
                        peer.Expect(0, 38);
                        experience = U64(ReadGrowth(), 1);
                    }
                    if (room == ids.Length - 1)
                    {
                        peer.Expect(0, 115); peer.Expect(0, 31, []);
                    }
                    else
                    {
                        byte[] move = new byte[100]; move[0] = target[room].X; move[1] = target[room].Y;
                        peer.Send(45, move); peer.Expect(1, 45, [1]); startMap = peer.Expect(0, 29).Body;
                    }
                }
            }

            byte[] ReadGrowth(bool cleared = false)
            {
                byte[] packet = peer.Expect(0, 37).Body;
                if (cleared)
                {
                    byte[] conditions = peer.Expect(0,574).Body;
                    That(conditions.Length == 10 && U32(conditions,0) == 1 && U16(conditions,4) == 3145 && U32(conditions,6) == 0,
                        "真实清图和猎杀同样投影已提交活动条件，不给3145省略通知");
                }
                if (packet[0] != projectedLevel)
                {
                    var skills = ExpectLevelRefresh(peer, packet[0], U64(packet, 1));
                    That(skills.All(tree => tree.Skills.Single(skill => skill.Id == 174).Level == packet[0]) &&
                        fixture.Query("SELECT slot FROM character_skill_bindings WHERE character_id=$p0 AND context=0 AND skill_id=$p1", peer.Id, movedSkill) is [[6L]],
                        "Player之后只同步变化技能，升级固定技能且不重置战斗换槽");
                }
                projectedLevel = packet[0];
                return packet;
            }
        }
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort,"black-diamond FormalTcp 0",out string clearBlackReply) &&
            clearBlackReply.StartsWith("OK "), "后续普通角色回归前只关闭黑钻，黄金牌、活动和回归资格保持独立");
        VerifyEntryRecoveryAndJobs(fixture, kills);
        VerifyOrdinaryEntry(fixture);
        VerifyDungeonEquipmentMoves(fixture, resources);
        VerifyOrdinaryRoutes(fixture, resources);
        VerifyDummyBossEntry(fixture,resources,growth);
        string persisted = fixture.Snapshot("SELECT level,experience FROM characters");
        string fatigue = fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id");
        string inventory = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
        string points = fixture.Snapshot("SELECT context,sp,tp FROM character_skill_points ORDER BY context");
        await fixture.StopAsync();
        await fixture.StartAsync("actual-restart", isolatedProcess: true);
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort,"infinite-fatigue off",out fatigueMode) &&
            fatigueMode == "OK {\"infiniteFatigue\":false}", "重启后的正常扣点回归重新关闭无限疲劳");
        That(fixture.Snapshot("SELECT level,experience FROM characters") == persisted &&
            fixture.Snapshot("SELECT context,sp,tp FROM character_skill_points ORDER BY context") == points &&
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") == inventory,
            "真实停服重启保留经验等级及两树SP");
        That(fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id") == fatigue, "普通首次房间费用在真实进程重启后保持");
        using (var peer = Open(fixture))
        {
            peer.Select();
            EntryTown(peer);
            var map = Enter(peer);
            That(U16(map, 25) >= nextKey, "重登及服务重启不复用旧实例怪key");
            Load(peer);
            peer.Send(39, oldReport!);
            peer.Barrier();
            That(Experience(fixture, peer.Id) == experience, "新实例上轮死亡key只审计忽略，不增加资产经验");
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0, 24); RefreshedQuests(peer); peer.Barrier();
            byte previousLevel = Convert.ToByte(fixture.Query("SELECT level FROM characters WHERE id=$p0", peer.Id)[0][0]);
            string afterClear = fixture.Snapshot();
            for (int replay = 0; replay < 2; replay++)
            {
                peer.Send(33, Convert.FromHexString("2100490C0000"));
                peer.Expect(1, 33, Convert.FromHexString("01490C00000000"));
                peer.Barrier();
                That(fixture.Snapshot() == afterClear, "本次权威清图ACK33精确7B重复只投影，不再次写库或发奖");
            }
            long beforeQuestRevision = Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id)[0][0]);
            uint beforeQuestGold = Convert.ToUInt32(fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id)[0][0]);
            uint questExperience;
            using (var owner = new DfoServer.Game.Accounts.AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
                questExperience = QuestExperiencePolicy.Prepare(new DfoServer.Persistence.CharacterRepository(fixture.Database).LoadCharacter(owner, 0),
                    SelfTestCatalogs.Quests(resources.Archive).Get(3145), SelfTestCatalogs.QuestRewardParameters(resources.Archive)).Amount;
            peer.Send(34, Convert.FromHexString("2200490CFFFF0100FFFF"));
            byte[] questAck = peer.Expect(1, 34).Body;
            int[] rewards = [10165055, 101010438, 10403, 12403, 14403, 16403, 18403];
            var rewardSlots = new Dictionary<ushort, int>();
            That(questAck.Length == 143 && questAck[0] == 1 && U16(questAck, 1) == 3145 && questAck[3] == 3 &&
                U32(questAck, 4) == questExperience && questAck[8] == 0 && questAck[9] == 7,
                "资源参数和实际等级计算的任务EXP与类型3/item模式0构成完整143B领奖ACK");
            for (int index = 0; index < rewards.Length; index++)
            {
                int at = 10 + 19 * index;
                ushort slot = U16(questAck, at);
                That(rewardSlots.TryAdd(slot, rewards[index]) && U32(questAck, at + 2) == rewards[index] &&
                    U32(questAck, at + 6) == (index == 0 ? 1u : 999999998u), "七项任务奖励原序/实际槽/数量或特殊品级来源");
                var row = fixture.Query("""
                    SELECT i.item_uid,i.item_core FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid
                    WHERE s.character_id=$p0 AND s.list_type=0 AND s.slot_index=$p1
                    """, peer.Id, slot).Single();
                using var core = JsonDocument.Parse((string)row[1]);
                That(Convert.ToInt64(row[0]) > 0 && core.RootElement.GetProperty("ItemId").GetInt32() == rewards[index] &&
                    core.RootElement.GetProperty("AmplifyType").GetByte() == questAck[at + 10] &&
                    core.RootElement.GetProperty("AmplifyValue").GetUInt16() == U16(questAck, at + 11) &&
                    core.RootElement.GetProperty("ExpireTime").GetInt32() == U32(questAck, at + 13) &&
                    core.RootElement.GetProperty("SortLockFlag").GetByte() == questAck[at + 17] &&
                    core.RootElement.GetProperty("RemainUseCount").GetByte() == questAck[at + 18],
                    "ACK奖励发布已持久UID/完整Core对应字段，不能拿规划临时UID充当提交");
            }
            byte[] rewardInventory = peer.Expect(0, 14).Body;
            That(rewardInventory.Length == 3 + 7 * 123 && rewardInventory[0] == 0 && U16(rewardInventory, 1) == 7,
                "领奖增量消费后完整123B Core校准七个目的槽，未混用奖励简表");
            for (int index = 0; index < 7; index++)
            {
                int at = 3 + index * 123;
                That(rewardSlots.TryGetValue(U16(rewardInventory, at), out int itemId) && U32(rewardInventory, at + 2) == itemId,
                    "完整库存通知与实际领奖七个目的槽一致");
            }
            experience += questExperience;
            byte rewardLevel = 1;
            while (experience >= growth.GetCumulativeExperienceBoundary(rewardLevel)) rewardLevel++;
            var questGrowth = peer.Expect(0, 37).Body;
            That(questGrowth.Length == 91 && questGrowth[0] == rewardLevel && U64(questGrowth, 1) == experience &&
                U32(questGrowth, 9) == 0 && U16(questGrowth, 13) == growth.GetSpDelta(1, rewardLevel) &&
                U16(questGrowth, 15) == growth.GetSpDelta(1, rewardLevel) &&
                fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id) is [[long questRevision]] && questRevision == beforeQuestRevision + 1 &&
                fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id) is [[long gold]] && gold == beforeQuestGold &&
                fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0", peer.Id) is [[0L]] &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0", peer.Id) is [[3145L]] &&
                fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0", peer.Id) is [[1L]],
                "经验自然跨级/SP/七资产/清槽/完成表/领奖receipt与唯一revision同事务，金币不猜补");
            if (rewardLevel != previousLevel)
                ExpectLevelRefresh(peer, rewardLevel, experience, town: true);
            peer.Expect(0, 21);
            byte[] completedQuests = peer.Expect(0, 356).Body;
            That(completedQuests.Length == 30004 && completedQuests[4 + 3145] == 1,
                "普通领奖ACK后立即同步完成位，任务面板不依赖重新选角");
            string afterAward = fixture.Snapshot();
            string awardedAssets = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
            peer.Send(34, Convert.FromHexString("2200490CFFFF0100FFFF"));
            peer.Expect(1, 34, [0, 19, 0]);
            peer.Barrier();
            That(fixture.Snapshot() == afterAward && fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") == awardedAssets,
                "同连接重复完成仅中立拒绝，不重发incremental物品或重复经验");
            VerifyCompletedQuestOrdinaryChallenge(fixture, peer, resources);
            completedOrdinaryState = OrdinaryPersistentState(fixture, peer.Id);
            completedOrdinaryRevision = Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id)[0][0]);
            var limit = fixture.Query("SELECT gold_carry_limit,level FROM characters WHERE id=$p0", peer.Id).Single();
            completedOrdinaryGoldLimit = Convert.ToInt32(limit[0]);
            expectedOrdinaryGoldLimit = Math.Max(completedOrdinaryGoldLimit, resources.ItemResources.GoldLimits.CarryLimit(Convert.ToInt32(limit[1])));
        }
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true);
            That(OrdinaryPersistentState(fixture, peer.Id) == completedOrdinaryState,
                "真实领奖后普通完整挑战与再挑战断线重登保持成长、疲劳、任务领奖与完整资产");
            That(fixture.Query("SELECT revision,gold_carry_limit FROM characters WHERE id=$p0", peer.Id) is [[long revision, long limit]] &&
                revision == completedOrdinaryRevision + 1 + (completedOrdinaryGoldLimit == expectedOrdinaryGoldLimit ? 0 : 1) && limit == expectedOrdinaryGoldLimit,
                "普通回城重登仅推进出生归一化及必要金币上限初始化的revision，不重放奖励或费用");
        }
        await VerifyQuestBossEntryAsync(source,root,resources,growth);
        VerifyStoryDifficultyEntry(fixture,resources,growth);
        await fixture.StopAsync();
        await VerifyMultipleStoryBlocksAsync(source,root,resources,growth);
        await VerifyHellPartyEntryAsync(source,root,resources,growth);
        Console.WriteLine($"PASS 正式TCP静默拒绝后回城重进、全部职业和转职加载、原生统计合成击杀、六房升级、真实随机拾取{picked}件、重复拒绝及服务重启（非Windows实机；零样本不作拾取覆盖）");
    }

    public static async Task RunDebugAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root,"gm-debug"),gm: true,resources: resources);
        await fixture.StartAsync();
        string Command(string line)
        {
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort,line,out var text),"GM调试端口返回 " + line);
            return text;
        }
        That(Command("infinite-fatigue") == "OK {\"infiniteFatigue\":true}" &&
            Command("cash-shop-expiration") == "OK {\"ignoreAbsoluteExpiration\":true}", "两个调试开关默认开启");
        using (var peer = Open(fixture))
        {
            peer.Create("InfiniteTcp");
            fixture.Sql("UPDATE character_fatigue SET used=maximum,remaining=0");
            string stored = fixture.Snapshot("SELECT * FROM character_fatigue");
            var selected = peer.Select().Single(row => row.Kind == 1 && row.Opcode == 4).Body;
            That(U16(selected,11) == 0 && U16(selected,228) == 156 && fixture.Snapshot("SELECT * FROM character_fatigue") == stored,
                "无限疲劳选角显示满值但不覆盖真实零疲劳");
            EntryTown(peer); SelectMap(peer);
            byte[] selection = Selection();
            BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),0);
            peer.Send(16,selection); peer.Expect(0,28); peer.Expect(0,29);
            long enteredRevision = Convert.ToInt64(fixture.Query("SELECT revision FROM characters")[0][0]);
            Load(peer); peer.Expect(0,36,[0,0,156,0,0,0,156,0,0,0]);
            peer.Send(37,new byte[8]); peer.Barrier();
            That(fixture.Snapshot("SELECT * FROM character_fatigue") == stored &&
                fixture.Query("SELECT count(*) FROM character_dungeon_fatigue_receipts") is [[0L]] &&
                fixture.Query("SELECT revision FROM characters") is [[long unchanged]] && unchanged == enteredRevision,
                "无限模式及重复加载不扣疲劳、不制造扣点账本或revision");
            That(Command("infinite-fatigue invalid").StartsWith("ERR ") &&
                Command("infinite-fatigue") == "OK {\"infiniteFatigue\":true}", "错误开关参数不改变默认状态");
            That(Command("infinite-fatigue off") == "OK {\"infiniteFatigue\":false}", "GM可关闭实际Session共享的无限疲劳");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
            SelectMap(peer); peer.Send(16,selection); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
            That(fixture.Snapshot("SELECT * FROM character_fatigue") == stored, "关闭后真实零疲劳拒绝重新入场");
            That(Command("infinite-fatigue on").StartsWith("OK "), "重新开启无限疲劳");
            peer.Send(16,selection); peer.Expect(0,28); peer.Expect(0,29);
            Load(peer); peer.Expect(0,36,[0,0,156,0,0,0,156,0,0,0]);
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
        }
        That(Command("infinite-fatigue off").StartsWith("OK "), "普通扣点场景显式关闭无限模式");
        using (var peer = Open(fixture))
        {
            peer.Create("FiniteTcp"); peer.Select(1); EntryTown(peer); SelectMap(peer);
            byte[] selection = Selection(); BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),0);
            peer.Send(16,selection); peer.Expect(0,28); peer.Expect(0,29);
            Load(peer); peer.Expect(0,36,[1,0,156,0,0,0,155,0,0,0]);
            peer.Send(37,new byte[8]); peer.Barrier();
            That(fixture.Query("SELECT used,remaining FROM character_fatigue WHERE character_id=$p0",peer.Id) is [[1L,155L]] &&
                fixture.Query("SELECT count(*) FROM character_dungeon_fatigue_receipts WHERE character_id=$p0",peer.Id) is [[1L]],
                "关闭开关恢复真实一次扣点及原去重账本");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        That(Command("infinite-fatigue") == "OK {\"infiniteFatigue\":true}" &&
            Command("cash-shop-expiration") == "OK {\"ignoreAbsoluteExpiration\":true}", "服务重建恢复两个开关默认开启");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "调试切换保留SQLite完整性");
        await fixture.StopAsync();
    }

    static SkillPacketsTests.WireTree[] ExpectLevelRefresh(TcpPeer peer, byte level, ulong experience, bool town = false, bool completedMainline = false)
    {
        if (town)
        {
            var actor = peer.Expect(0, 2).Body;
            int nameLength = BinaryPrimitives.ReadInt32LittleEndian(actor.AsSpan(123));
            That(actor[0] == 0 && U16(actor, 121) == peer.Id && actor[129 + nameLength] == level,
                "城镇升级仍同步Actor实际等级");
        }
        var player = peer.Expect(0, 2).Body;
        That(player[0] == 1 && U16(player, 22) == peer.Id && U64(player, 24) == experience && U32(player, 32) == 89 &&
            U32(player, 40) == 1400 + 250 * (level - 1) &&
            BinaryPrimitives.ReadInt16LittleEndian(player.AsSpan(44)) == 75 + 48 * (level - 1),
            "副本与结算升级只发Player，当前CHR基础/完整Core/双树来自实际提交角色");
        SkillPacketsTests.WireTree[] skills;
        if (town)
            skills = SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body);
        else
        {
            var alternate = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            var current = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(alternate.Context == 1 && current.Context == 0 && alternate.Mode == 0 && current.Mode == 0,
                "图内升级依次增量同步备用树和主树，不发送会清运行对象的完整19");
            skills = [new(current.Sp, current.Tp, current.Skills), new(alternate.Sp, alternate.Tp, alternate.Skills)];
        }
        if (!town)
        {
            That(QuestLifecycleTcpTests.List(peer.Expect(0, 21).Body, level).Contains(3145) != completedMainline,
                "副本和结算升级后按等级及实际完成位刷新任务，不省略通知或重显已完成任务");
            if (completedMainline)
                That(peer.Expect(0,356).Body[4+3145] == 1, "副本升级任务列表刷新后保留已完成主线历史");
        }
        return skills;
    }

    internal static void RefreshedQuests(TcpPeer peer)
    {
        byte[] history = peer.CompletedQuestHistory;
        peer.Expect(0,21);
        if (history.Skip(4).Any(value => value != 0))
            Bytes(peer.Expect(0,356).Body, history, "回城或接取刷新21后保留已知完成历史");
    }

    static int VerifyDungeonActions(TcpTestFixture fixture, TcpPeer peer)
    {
        int movedSkill = Convert.ToInt32(fixture.Query(
            "SELECT skill_id FROM character_skill_bindings WHERE character_id=$p0 AND context=0 AND slot=0", peer.Id).Single()[0]);
        string progress = fixture.Snapshot("SELECT experience,level FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") +
            fixture.Snapshot("SELECT * FROM character_skill_points ORDER BY character_id,context");
        peer.Send(28, [0, 0, 6, 255, 255, 255, 255, 0]);
        peer.Expect(1, 28, [1, 0, 0, 6]);
        peer.Send(29, [0, 1, 5, 0, 0, 1, 0]);
        peer.Expect(1, 29, [0, 0xD9, 0]);
        peer.Send(491, [0, 0]);
        peer.Expect(1, 491, [1, 0, 1]);
        peer.Expect(0, 19);
        var row = fixture.Query("SELECT s.slot_index FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid WHERE s.character_id=$p0 AND s.list_type=0 AND json_extract(i.item_core,'$.ItemId')=1000", peer.Id).Single();
        short from = Convert.ToInt16(row[0]);
        const short to = 3;
        peer.Send(19, Build(writer =>
        {
            writer.Write((byte)0); writer.Write(from); writer.Write(1000); writer.Write(0);
            writer.Write((byte)0); writer.Write(to); writer.Write(0); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
        }));
        peer.Expect(1, 19, Build(writer => { writer.Write((byte)1); writer.Write((byte)0); writer.Write(from); writer.Write(5); writer.Write((byte)0); writer.Write(to); }));
        peer.Send(713, [0, 3, 0]); peer.Expect(1, 713, [1, 0, 3, 0]);
        peer.Send(714, [0, 3, 0]); peer.Expect(1, 714, [1, 0, 3, 0]);
        peer.Barrier();
        That(progress == fixture.Snapshot("SELECT experience,level FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") +
            fixture.Snapshot("SELECT * FROM character_skill_points ORDER BY character_id,context"),
            "战斗中换槽、学习拒绝、洗点和背包移动/锁定不改run、经验或SP，后续击杀与结算继续");
        That(fixture.Query("SELECT slot FROM character_skill_bindings WHERE character_id=$p0 AND context=0 AND skill_id=$p1", peer.Id, movedSkill) is [[6L]],
            "战斗中的快捷栏换槽已持久，洗点保留免费技能的已有槽位");
        return movedSkill;
    }

    static void VerifyEntryRecoveryAndJobs(TcpTestFixture fixture, DungeonKillExperienceCatalog kills)
    {
        ushort slot = 1;
        foreach (byte job in new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15, 9, 10 })
        {
            using var peer = Open(fixture);
            peer.Create("FormalJ" + job, job);
            peer.Select(slot++, combos: job == 9 ? TcpPeer.StoredCombos(fixture, peer.Id) : null);
            EntryTown(peer);
            if (job == 14)
            {
                SelectMap(peer);
                string before = fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id");
                byte[] rejection = Selection(1);
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    peer.Send(16, rejection);
                    peer.Expect(1, 16, [0, 9, 0, 0]);
                    // 下一帧必须直接为屏障ACK；拒绝不附带弹窗、入场或自动回城通知。
                    peer.Barrier();
                    That(fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") == before,
                        $"职业{job}重复准入拒绝保留角色revision、attempt与key，不发成功通知");
                }
                peer.Send(132);
                peer.Expect(1, 132, [1]);
                peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
                byte[] area = peer.Expect(0, 23).Body;
                byte[] transition = peer.Expect(0, 24).Body;
                That(area[2] == 38 && area[3] == 2 && U16(area, 6) == 249 &&
                    transition[0] == 38 && transition[1] == 2 && U16(transition, 8) == 249 &&
                    fixture.Query("SELECT x,y FROM characters WHERE id=$p0", peer.Id) is [[var x, 249L]] && U16(area, 4) == Convert.ToUInt16(x),
                    "拒绝后的132返回沿原选图上下文，地区绑定与实际保存落点一致");
                peer.Barrier();
                peer.Send(35, Move(1085, 249));
                peer.Barrier();
            }
            byte[] map = Enter(peer);
            Load(peer);
            That(fixture.Query("SELECT job,last_attempt FROM characters JOIN character_dungeon_state ON character_id=id WHERE id=$p0", peer.Id)
                is [[var actualJob, 1L]] && Convert.ToByte(actualJob) == job,
                $"职业{job}真实TCP建角、旅行、选图、入场及加载建立一次正式尝试");
            if (job == 14)
            {
                ushort key = U16(map, 25);
                byte level = map[31];
                peer.Send(39, Death(key, peer.Id, level));
                That(U16(peer.Expect(0, 38).Body) == key, "女圣职者加载后死亡沿正式通道确认当前怪物key");
                byte[] growth = peer.Expect(0, 37).Body;
                That(U32(growth, 9) == 0 && Experience(fixture, peer.Id) == 104,
                    "女圣职者拒绝、132返回、15重进后可完成正式死亡成长");
            }
            peer.Send(42);
            peer.Expect(1, 42, [1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0, 24);
            RefreshedQuests(peer);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Create("PromotedTcp");
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "grow PromotedTcp 37", out string reply) && reply.StartsWith("OK "),
                "独立测试服务通过正式GM存储用例更新转职字节");
            var completed = fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id", peer.Id)
                .Select(row => Convert.ToInt32(row[0])).ToHashSet();
            That(completed.Count > 0 && completed.All(id => id is > 0 and < 30000), "GM转职已保存当前分支完整的合法任务历史");
            peer.Select(slot, prepared: true);
            var history = peer.CompletedQuestHistory;
            That(history.Length == 30004 && U32(history) == 30000 &&
                Enumerable.Range(0, 30000).All(id => history[4 + id] == (completed.Contains(id) ? 1 : 0)),
                "GM转职后选角严格恢复持久完成历史，逐项核对全部30000位");
            EntryTown(peer);
            Enter(peer);
            Load(peer);
            That(fixture.Query("SELECT grow_type,last_attempt FROM characters JOIN character_dungeon_state ON character_id=id WHERE id=$p0", peer.Id)
                is [[37L, 1L]], "转职角色真实TCP准入加载，不依赖普通职业成长资格");
            peer.Send(42);
            peer.Expect(1, 42, [1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0, 24);
            RefreshedQuests(peer);
            peer.Barrier();
        }
    }

    static bool ExecuteOfflineGm(TcpTestFixture fixture, string command, out string response)
    {
        string reply = "";
        bool delivered = false;
        // 客户端Dispose不等于服务端已释放owner；仅等待已确认未执行命令的在线保护拒绝。
        bool settled = SpinWait.SpinUntil(() =>
        {
            delivered = DevelopmentGmClient.TryExecute(fixture.Options.GmPort, command, out reply);
            return !delivered || reply != "ERR 账号正在使用，请重试";
        }, TimeSpan.FromSeconds(2));
        response = reply;
        return settled && delivered;
    }

    static void VerifyOrdinaryRoutes(TcpTestFixture fixture, TcpTestResources resources)
    {
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var clearRewards = SelfTestCatalogs.ClearExperience(resources.Archive);
        foreach (int dungeonId in new[] { 5, 6, 7, 8, 9, 1000 })
        {
            var definition = dungeons.Get(dungeonId);
            string name = "OrdMap" + dungeonId;
            ushort slot = checked((ushort)Convert.ToInt32(fixture.Query("SELECT count(*) FROM characters")[0][0]));
            using (var create = Open(fixture)) create.Create(name);
            That(ExecuteOfflineGm(fixture, $"level {name} {definition.MinimumLevel}", out var reply) && reply.StartsWith("OK "),
                "独立测试角色经完整成长用例达到对应普通图等级，保留合法初装与两树状态：" + reply);
            using var peer = Open(fixture);
            peer.Select(slot);
            EntryTown(peer);
            That(fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0", peer.Id) is [[0L]],
                "六图生产挑战使用无活动主线角色，不伪造任务资格");
            string equipmentQuery = "SELECT s.slot_index,i.item_uid,i.item_core FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid WHERE s.character_id=$p0 AND s.list_type=3 ORDER BY s.slot_index";
            That(fixture.Query(equipmentQuery, peer.Id).Length > 0, "普通挑战保留真实职业初始装备，不能用空资产证明保全");
            string equipment = fixture.Snapshot(equipmentQuery, peer.Id);
            byte currentLevel = Convert.ToByte(fixture.Query("SELECT level FROM characters WHERE id=$p0", peer.Id)[0][0]);
            ulong experience = Experience(fixture, peer.Id);
            SelectMap(peer);
            byte[] selection = Selection();
            BinaryPrimitives.WriteUInt32LittleEndian(selection, checked((uint)dungeonId));
            BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16), 0);
            selection[4] = 1;
            peer.Send(16, selection); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
            That(fixture.Query("SELECT last_attempt,remaining FROM character_dungeon_state JOIN character_fatigue USING(character_id,account) WHERE character_id=$p0", peer.Id)
                is [[0L, 156L]], "普通图非法难度完整拒绝不预留实例、不扣疲劳，连接和选图保留");
            selection[4] = 0;
            peer.Send(16, selection);
            var info = peer.Expect(0, 28).Body;
            var start = peer.Expect(0, 29).Body;
            var maze = definition.Mazes[0];
            var maps = maze.MapSpecification ?? throw new InvalidDataException("普通六图回归需要完整显式房间");
            var startMap = maps.Single(map => map.Position == maze.Starts.Single());
            That(U32(info, 0) == dungeonId && info[7] == 0 && U32(start, 14) == startMap.Id,
                "六图生产通知分别选择实际普通maze和起点，不复用剧情地图");
            byte[] mapBody = start;
            DungeonMap currentMap = startMap;
            var visited = new HashSet<int>();
            int deaths = 0, pickups = 0;
            VerifyRoom();
            var bossMap = maps.Single(map => map.Position == maze.Bosses.Single());
            foreach (var target in maps.Where(map => map != startMap && map != bossMap).Concat([bossMap]))
            {
                var queue = new Queue<DungeonPoint>();
                var from = new Dictionary<DungeonPoint, DungeonPoint?> { [currentMap.Position] = null };
                queue.Enqueue(currentMap.Position);
                while (queue.TryDequeue(out var point) && !from.ContainsKey(target.Position))
                foreach (var link in maze.StaticTopology.Links!)
                {
                    DungeonPoint? next = link.First == point ? link.Second : link.Second == point ? link.First : null;
                    if (next is not { } adjacent || from.ContainsKey(adjacent) || adjacent == bossMap.Position && adjacent != target.Position)
                        continue;
                    from.Add(adjacent, point);
                    queue.Enqueue(adjacent);
                }
                That(from.ContainsKey(target.Position), "普通图全部资源房间可沿真实连接抵达，最后进入Boss房");
                var path = new Stack<DungeonPoint>();
                for (var point = target.Position; from[point] is { } parent; point = parent)
                    path.Push(point);
                while (path.TryPop(out var next))
                {
                    byte[] move = new byte[100];
                    move[0] = checked((byte)next.X);
                    move[1] = checked((byte)next.Y);
                    peer.Send(45, move);
                    peer.Expect(1, 45, [1]);
                    mapBody = peer.Expect(0, 29).Body;
                    currentMap = maps.Single(map => map.Position == next);
                    VerifyRoom();
                }
            }
            That(visited.Count == maps.Count && fixture.Query(
                "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0 AND quest_id=0", peer.Id) is [[long storedDeaths]] && storedDeaths == deaths,
                "六图逐房全部静态敌人经正式死亡与资源掉落通道提交，机关子对象不混入奖励事实");
            peer.Send(46, DungeonSettlementProtocolTests.Report(peer.Id, 90));
            peer.Expect(0, 34);
            var clearReward = clearRewards.Calculate(definition, 0, 90);
            experience += clearReward.TotalExperience;
            ReadGrowth();
            peer.ReadCardMembershipState();
            ReadCommittedCard(fixture, peer, peer.Expect(0, 35).Body);
            peer.Send(69);
            peer.Expect(1, 69, [1]);
            peer.Send(70);
            That(peer.Expect(1, 70).Body.Length == 17, "普通六图沿完整八槽翻牌协议进入领奖");
            peer.Send(71, [0, 0]);
            var card = peer.Expect(1, 71).Body;
            That(card.Length == 33 && card[0] == 1 && card[1] == 0 && DrainCardInventory(peer) > 0 &&
                fixture.Query("SELECT claimed FROM character_dungeon_card_rewards WHERE character_id=$p0", peer.Id) is [[1L]],
                "普通六图免费牌实际发奖且领取位落库，不能仅凭35展示判定结算完成");
            if (dungeonId == 5)
            {
                string settled = OrdinaryPersistentState(fixture, peer.Id);
                peer.Send(72, [2, 1]); peer.Expect(1, 72, [1, 2, 1]);
                peer.Send(72, [1, 1]); peer.Expect(1, 72, [1, 1, 1]);
                peer.Expect(1, 15, [1]);
                peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
                peer.Expect(0, 27, new byte[42]);
                peer.Barrier();
                That(OrdinaryPersistentState(fixture, peer.Id) == settled,
                    "普通地图真实72序列直接返回选图，不借深渊资格、不附加扣费或奖励");
                peer.Send(72, [1, 1]); peer.Send(72, [1, 3]); peer.Barrier();
                That(OrdinaryPersistentState(fixture, peer.Id) == settled, "普通选图后旧结算确认重放不消费新上下文");
                byte[] chosen = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(chosen, (uint)dungeonId);
                peer.Send(15, chosen); peer.Expect(1, 15, [1]);
                peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]); peer.Barrier();
                peer.Send(16, selection);
            }
            else
            {
                peer.Send(72, [1, 0]);
                peer.Expect(1, 72, [1, 1, 0]);
                peer.Expect(0, 27, new byte[42]);
            }
            That(U32(peer.Expect(0, 28).Body, 0) == dungeonId && U32(peer.Expect(0, 29).Body, 14) == startMap.Id,
                "普通六图再挑战或重选后保持原资源路线，创建新的尝试");
            Load(peer);
            ExpectFatigue(visited.Count + 1);
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0, 24); RefreshedQuests(peer); peer.Barrier();
            That(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[2L]] &&
                fixture.Query("SELECT quest_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0", peer.Id) is [[0L]] &&
                fixture.Snapshot(equipmentQuery, peer.Id) == equipment && Experience(fixture, peer.Id) == experience,
                "普通六图再挑战及返回保留原完整装备和累计成长，不创建任务结算身份");
            Console.WriteLine($"PASS 普通图{dungeonId}完整生产TCP挑战：{visited.Count}房、{deaths}静态死亡、{pickups}拾取、实际翻牌与再挑战（合成战斗，非客户端验收）");

            void VerifyRoom()
            {
                bool revisit = !visited.Add(currentMap.Id);
                That(mapBody[0] == currentMap.Position.X && mapBody[1] == currentMap.Position.Y &&
                    (revisit ? mapBody.Length == 16 && mapBody[13] == 0 : mapBody[13] == 1 && U32(mapBody, 14) == currentMap.Id),
                    "普通六图首次按实际资源初始化，回访复用缓存而不重新生成演员");
                Load(peer);
                ExpectFatigue(visited.Count);
                if (revisit) return;
                var room = rooms.Get(currentMap, requirePassiveDefinitions: false);
                int expectedActors = room.Monsters.Count + room.AiCharacters.Count(row => !DungeonCompanion.IsCompanionRow(row.Id)) + room.SpecialObjects.Count +
                    room.SpecialObjects.Sum(parent => parent.Children.Count(child => child.Kind == "[monster]"));
                That(mapBody[18] == expectedActors, "普通六图通知包含完整资源怪物、APC和特殊父子对象，不用删行通过挑战");
                foreach (int index in Enumerable.Range(0, room.Monsters.Count).OrderBy(index => room.Monsters[index].RankKind == "[boss]"))
                {
                    int offset = 19 + index * 22;
                    var monster = room.Monsters[index];
                    That(U32(mapBody, offset + 2) == index && U32(mapBody, offset + 8) == monster.MonsterId && mapBody[offset + 14] == 0,
                        "普通六图每只静态怪物的原资源序号和模板完整进入生产通知");
                    if (room.MonsterTeams is { } teams && index < teams.Count && teams[index] == 0) continue;
                    ushort key = U16(mapBody, offset + 6);
                    peer.Send(39, Death(key, peer.Id, mapBody[offset + 12]));
                    var death = peer.Expect(0, 38).Body;
                    That(U16(death, 0) == key, "普通六图完整死亡通知绑定本房实际key");
                    var receipt = fixture.Query("SELECT amount,monster_level FROM character_dungeon_growth_receipts WHERE character_id=$p0 AND monster_key=$p1", peer.Id, key).Single();
                    That(Convert.ToByte(receipt[1]) == mapBody[offset + 12], "普通六图成长账本使用实际生成等级");
                    experience += Convert.ToUInt32(receipt[0]);
                    ReadGrowth();
                    deaths++;
                    if (currentMap.Kind == "boss" && monster.RankKind == "[boss]")
                    {
                        peer.Expect(0, 115, [1, 1, (byte)key, (byte)(key >> 8)]);
                        peer.Expect(0, 31, []);
                    }
                    pickups += PickupResourceDrops(fixture, peer, death, 0);
                }
            }

            void ReadGrowth()
            {
                var notice = peer.Expect(0, 37).Body;
                That(notice.Length == 91 && U64(notice, 1) == experience && Experience(fixture, peer.Id) == experience,
                    "普通六图逐怪和通关的完整成长投影对应实际持久提交");
                if (notice[0] == currentLevel) return;
                var player = peer.Expect(0, 2).Body;
                That(player[0] == 1 && U16(player, 22) == peer.Id && U64(player, 24) == experience,
                    "普通六图合法升级后同步本人Player及实际累计经验");
                var alternate = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
                var active = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
                That(alternate.Context == 1 && active.Context == 0 &&
                    alternate.Skills.Single(skill => skill.Id == 174).Level == notice[0] &&
                    active.Skills.Single(skill => skill.Id == 174).Level == notice[0],
                    "普通六图升级只更新真实变化技能，并恢复主树而不重建整个运行状态");
                RefreshedQuests(peer);
                currentLevel = notice[0];
            }

            void ExpectFatigue(int used) => peer.Expect(0, 36,
                [checked((byte)used), 0, 156, 0, 0, 0, checked((byte)(156 - used)), 0, 0, 0]);
        }
    }

    static void VerifyOrdinaryEntry(TcpTestFixture fixture)
    {
        using var peer = Open(fixture);
        ushort slot = checked((ushort)Convert.ToInt32(fixture.Query("SELECT count(*) FROM characters")[0][0]));
        peer.Create("NormalTcp");
        peer.Select(slot);
        EntryTown(peer);
        SelectMap(peer);
        byte[] selection = Selection();
        BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16), 0);
        peer.Send(16, selection);
        var information = peer.Expect(0, 28).Body;
        var map = peer.Expect(0, 29).Body;
        That(information[7] == 0 && U32(map, 14) == 58548 && map[18] == 4, "真实普通请求加载普通地图和全部资源怪物");
        Load(peer);
        byte[] expectedFatigue = [1, 0, 156, 0, 0, 0, 155, 0, 0, 0];
        peer.Expect(0, 36, expectedFatigue);
        peer.Send(37, new byte[8]); peer.Barrier();
        That(fixture.Query("SELECT used,remaining FROM character_fatigue WHERE character_id=$p0", peer.Id) is [[1L, 155L]],
            "普通重复加载不重复收费，实际NOTI36及SQLowner一致");
        peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0, 24); RefreshedQuests(peer); peer.Barrier();
        SelectMap(peer); peer.Send(16, selection);
        peer.Expect(0, 28); var current = peer.Expect(0, 29).Body; Load(peer);
        peer.Expect(0, 36, [2, 0, 156, 0, 0, 0, 154, 0, 0, 0]);
        byte currentLevel = checked((byte)Convert.ToInt32(fixture.Query("SELECT level FROM characters WHERE id=$p0", peer.Id)[0][0]));
        for (int room = 0; room < 2; room++)
        {
            for (int index = 0; index < current[18]; index++)
            {
                int offset = 19 + index * 22;
                peer.Send(39, Death(U16(current, offset + 6), peer.Id, current[offset + 12]));
                peer.Expect(0, 38);
                byte[] experience = peer.Expect(0, 37).Body;
                if (experience[0] != currentLevel)
                {
                    ExpectLevelRefresh(peer, experience[0], U64(experience, 1));
                    currentLevel = experience[0];
                }
            }
            byte[] move = new byte[100]; move[0] = 1; move[1] = room == 0 ? (byte)1 : (byte)0;
            peer.Send(45, move); peer.Expect(1, 45, [1]); current = peer.Expect(0, 29).Body; Load(peer);
            peer.Expect(0, 36, [checked((byte)(3 + room)), 0, 156, 0, 0, 0, checked((byte)(153 - room)), 0, 0, 0]);
        }
        That(U32(current, 14) == 58550 && current[18] == 8 && current[19 + 7 * 22 + 13] == 9,
            "普通实际过房生产NOTI29完整保留五只静态怪、两个机关子怪和原父对象");
        ulong beforeChild = Experience(fixture, peer.Id);
        int childOffset = 19 + 5 * 22;
        byte[] childReport = Death(U16(current, childOffset + 6), peer.Id, current[childOffset + 12]);
        peer.Send(39, childReport); peer.Expect(0, 38); peer.Barrier();
        peer.Send(39, childReport); peer.Barrier();
        That(Experience(fixture, peer.Id) == beforeChild && fixture.Query(
            "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0 AND monster_key=$p1", peer.Id, U16(current, childOffset + 6)) is [[0L]],
            "真实机关子报告只一次死亡收尾，不冒充普通经验或资产receipt");
        peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0, 24); RefreshedQuests(peer); peer.Barrier();
        That(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[2L]],
            "普通返回后再次挑战实际建立新尝试，没有重新接取或修改主线");
    }

    static void VerifyCompletedQuestOrdinaryChallenge(TcpTestFixture fixture, TcpPeer peer, TcpTestResources resources)
    {
        That(fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0", peer.Id) is [[3145L]] &&
            fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0", peer.Id) is [[0L]],
            "普通完整TCP挑战复用真实领奖角色，不伪造任务完成或清空活动任务");
        long[] originalRewardUids = fixture.Query("""
            SELECT item_uid FROM character_inventory_items WHERE character_id=$p0 AND
              json_extract(item_core,'$.ItemId') IN (10165055,101010438,10403,12403,14403,16403,18403) ORDER BY item_uid
            """, peer.Id).Select(row => Convert.ToInt64(row[0])).ToArray();
        That(originalRewardUids.Length >= 7, "普通挑战前确有完整七项领奖实例，不能用空查询证明资产保全");
        string originalRewardQuery = "SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 AND item_uid IN (" +
            string.Join(",", originalRewardUids) + ") ORDER BY item_uid";
        string questAssets = fixture.Snapshot(originalRewardQuery, peer.Id);
        long attempt = Convert.ToInt64(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0]);
        ulong experience = Experience(fixture, peer.Id);
        byte currentLevel = Convert.ToByte(fixture.Query("SELECT level FROM characters WHERE id=$p0", peer.Id)[0][0]);
        var clearReward = SelfTestCatalogs.ClearExperience(resources.Archive).Calculate(SelfTestCatalogs.Dungeons(resources.Archive).Get(3), 0, 90);
        byte[] selection = Selection();
        BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16), 0);
        SelectMap(peer);
        peer.Send(16, selection);
        That(peer.Expect(0, 28).Body[7] == 0, "已完成主线的生产NOTI28选择真实普通maze0");
        byte[] map = peer.Expect(0, 29).Body;
        var route = new (byte X, byte Y, uint Map)[] { (0, 1, 58548), (1, 1, 58549), (1, 0, 58550),
            (1, 1, 58549), (1, 0, 58550), (1, 1, 58549), (2, 1, 58551), (2, 0, 58552), (3, 0, 58553) };
        var visited = new HashSet<uint>();
        int ordinaryPicked = 0;
        byte[]? bossReport = null;
        for (int room = 0; room < route.Length; room++)
        {
            if (room != 0)
            {
                byte[] move = new byte[100]; move[0] = route[room].X; move[1] = route[room].Y;
                peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
            }
            bool revisit = visited.Contains(route[room].Map);
            That(map[0] == route[room].X && map[1] == route[room].Y && map[2] == 0 &&
                (revisit ? map.Length == 16 && map[13] == 0 && map[14] == 0 && map[15] == byte.MaxValue :
                    map[13] == 1 && U32(map, 14) == route[room].Map),
                "首次逐房初始化；普通及机关房回访按16B缓存正文恢复，不重建父子演员或地面对象");
            Load(peer);
            visited.Add(route[room].Map);
            ExpectFatigue(visited.Count);
            // 完整父子行仍由生产初始化消费；此处只对普通静态目标做合成战斗，不宣称机关奖励已验收。
            var rows = Enumerable.Range(0, revisit ? 0 : map[18]).Select(row => 19 + row * 22)
                .Where(offset => map[offset + 13] != 9 && map[offset + 14] == 0)
                .OrderBy(offset => map[offset + 13] == 3).ToArray();
            if (revisit) That(rows.Length == 0, "普通回访不重新生成已清静态怪物或机关父子演员");
            foreach (int offset in rows)
            {
                ushort key = U16(map, offset + 6);
                byte[] report = Death(key, peer.Id, map[offset + 12]);
                bool boss = map[offset + 13] == 3;
                if (boss)
                {
                    report[55] = 1;
                    BinaryPrimitives.WriteUInt64LittleEndian(report.AsSpan(18), 9000);
                    BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(26), 30);
                    SealDeath(report);
                    bossReport = report;
                }
                peer.Send(39, report);
                byte[] death = peer.Expect(0, 38).Body;
                That(U16(death, 0) == key, "普通静态死亡通过生产Parser/Handler并发布原对象key");
                var storedKill = fixture.Query("SELECT amount,monster_level FROM character_dungeon_growth_receipts WHERE character_id=$p0 AND monster_key=$p1", peer.Id, key).Single();
                That(Convert.ToInt32(storedKill[1]) == map[offset + 12], "普通成长账本保留实际生成等级，不借用客户端伤害统计作为奖励来源");
                ReadGrowth(Convert.ToUInt32(storedKill[0]));
                if (boss)
                {
                    peer.Expect(0, 115, [1, 1, (byte)key, (byte)(key >> 8)]);
                    peer.Expect(0, 31, []);
                }
                ordinaryPicked += PickupResourceDrops(fixture, peer, death, 0);
            }
        }
        That(bossReport is not null && fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0 AND quest_id=0", peer.Id)
            is [[32L]], "普通六房全部32个静态目标正式落库，机关子actor没有混入成长或清图事实");
        // 结算必须真实覆盖非零冒险团加成：把账号冒险团等级提到2级（该级资源声明通关经验+10%）。
        fixture.Sql("""
            INSERT INTO account_adventures(account,revision,name,created_date,level,experience,remainder,
              capsule_experience,brave_experience,points,month)
            VALUES($p0,0,'FixtureGroup',20261022,2,0,'0',0,0,zeroblob(20),202610)
            ON CONFLICT(account) DO UPDATE SET level=2
            """, fixture.Options.DevelopmentAccount);
        byte[] resultReport = DungeonSettlementProtocolTests.Report(peer.Id, 90);
        // 清图击杀经验已按账本推进账号冒险团等级；结算按提交前等级把资源声明的「通关经验值增加」计入整单奖励。
        int adventureLevel = Convert.ToInt32(fixture.Query(
            "SELECT coalesce((SELECT level FROM account_adventures WHERE account=$p0),1)",
            fixture.Options.DevelopmentAccount).Single()[0]);
        uint adventureBonus = DfoServer.Game.Adventure.AdventureBenefitPolicy.ClearExperienceBonus(
            clearReward.BaseExperience, adventureLevel, resources.Adventure);
        uint clearExperience = checked(clearReward.TotalExperience + adventureBonus);
        peer.Send(46, resultReport);
        That(peer.Expect(0, 34).Body[6] == 90, "普通真实Boss清图开启评分结果通知");
        ReadGrowth(clearExperience);
        peer.ReadCardMembershipState();
        byte[] clear = peer.Expect(0, 35).Body;
        ReadCommittedCard(fixture,peer,clear);
        // 35包没有独立冒险团字段，加成按基础通关经验合并展示（见 DungeonSettlementPackets.EmptyCardReward）。
        That(U32(clear, 0) == checked(clearReward.BaseExperience + adventureBonus) && U32(clear, 4) == clearReward.RankExperience &&
            fixture.Query("SELECT quest_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0 AND quest_id=0", peer.Id) is [[0L]],
            "普通生产结算使用本路线经验与零任务账本，不复用任务结算资格");
        peer.Send(69);
        peer.Expect(1, 69, [1]);
        peer.Send(70);
        That(peer.Expect(1, 70).Body.Length == 17, "领奖后普通幽暗密林进入完整翻牌阶段");
        peer.Send(71, [0, 0]);
        var selectedCard = peer.Expect(1, 71).Body;
        That(selectedCard.Length == 33 && selectedCard[0] == 1 && selectedCard[1] == 0 && DrainCardInventory(peer) > 0 &&
            fixture.Query("SELECT claimed FROM character_dungeon_card_rewards WHERE character_id=$p0 AND instance_id=(SELECT instance_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0 AND quest_id=0)", peer.Id) is [[1L]],
            "领奖后普通免费牌实际提交资产和领取位，不以结算候选展示代替发奖");
        peer.Send(72, [1, 0]); peer.Expect(1, 72, [1, 1, 0]); peer.Expect(0, 27, new byte[42]);
        That(peer.Expect(0, 28).Body[7] == 0 && U32(peer.Expect(0, 29).Body, 14) == 58548,
            "普通结算再挑战真实通知重新加载普通起点，保持任务字段0");
        Load(peer); ExpectFatigue(7);
        peer.Send(39, bossReport!); peer.Barrier();
        That(Experience(fixture, peer.Id) == experience, "再挑战拒绝旧普通Boss报告，不重复结算或发经验");
        peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0, 24); RefreshedQuests(peer); peer.Barrier();
        That(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[long last]] && last == attempt + 2 &&
            fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0", peer.Id) is [[1L]] &&
            fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0", peer.Id) is [[3145L]] &&
            fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0", peer.Id) is [[0L]] &&
            fixture.Snapshot(originalRewardQuery, peer.Id) == questAssets,
            "普通挑战与再挑战保留原领奖UID/Core、完成位和空活动主线；合法掉落可新增同模板实例");
        Console.WriteLine($"PASS 真实3145领奖后普通六房32个静态目标、Boss改写、结算及再挑战生产TCP；普通随机拾取{ordinaryPicked}件（合成战斗，非客户端验收）");

        void ReadGrowth(uint delta)
        {
            experience += delta;
            byte[] notice = peer.Expect(0, 37).Body;
            That(notice.Length == 91, $"普通经验通知长度应为91（实际{notice.Length}）");
            That(U64(notice, 1) == experience, $"普通经验通知累计值应与持久累计经验一致（通知{U64(notice, 1)}，期望{experience}）");
            That(U32(notice, 9) == 0, $"普通经验通知扩展字段应为0（实际{U32(notice, 9)}）");
            That(Experience(fixture, peer.Id) == experience,
                $"普通死亡和结算的实际完整经验通知与持久累计经验一致（库{Experience(fixture, peer.Id)}，期望{experience}）");
            if (notice[0] != currentLevel)
            {
                ExpectLevelRefresh(peer, notice[0], experience, completedMainline: true);
                currentLevel = notice[0];
            }
        }
        void ExpectFatigue(int used) => peer.Expect(0, 36, [checked((byte)used), 0, 156, 0, 0, 0, checked((byte)(156 - used)), 0, 0, 0]);
    }

    static string OrdinaryPersistentState(TcpTestFixture fixture, ushort id) =>
        fixture.Snapshot("""
            SELECT id,account,slot,name,job,grow_type,level,accepted_story_level,gold,experience,inventory_expansion,revive_coins,win_points
            FROM characters WHERE id=$p0
            """, id) +
        fixture.Snapshot("SELECT * FROM character_fatigue WHERE character_id=$p0", id) +
        fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", id) +
        fixture.Snapshot("SELECT * FROM character_skill_points WHERE character_id=$p0 ORDER BY context", id) +
        fixture.Snapshot("SELECT * FROM character_skill_bindings WHERE character_id=$p0 ORDER BY context,slot", id) +
        fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", id) +
        fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index", id) +
        fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") +
        fixture.Snapshot("SELECT * FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id", id) +
        fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot", id) +
        fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0 ORDER BY instance_id", id) +
        fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0", id);

    internal static void ReadCommittedCard(TcpTestFixture fixture, TcpPeer peer, byte[] body, bool duplicate = false)
    {
        var row = fixture.Query("SELECT reward_json FROM character_dungeon_card_rewards WHERE character_id=$p0 ORDER BY revision DESC LIMIT 1",peer.Id).Single();
        var card = JsonSerializer.Deserialize<DfoServer.Game.Dungeons.DungeonCardReward>((string)row[0])!;
        var expected = new List<(uint Item, uint Amount)>();
        if (card.Gold > 0) expected.Add((0,checked((uint)card.Gold)));
        if (card.Item is { } item) expected.Add((checked((uint)item.ItemId),checked((uint)item.Count)));
        var optionsRow = fixture.Query("SELECT options_json,black_diamond_reward_json FROM character_dungeon_card_options WHERE character_id=$p0 ORDER BY rowid DESC LIMIT 1",peer.Id).Single();
        var options = JsonSerializer.Deserialize<DfoServer.Game.Dungeons.DungeonCardOptions>((string)optionsRow[0])!;
        var bonus = options.BlackDiamond;
        var bonusPairs = new List<(uint Item,uint Amount)>();
        if (bonus?.Gold > 0) bonusPairs.Add((0,checked((uint)bonus.Gold)));
        if (bonus?.Item is { } bonusItem) bonusPairs.Add((checked((uint)bonusItem.ItemId),checked((uint)bonusItem.Count)));
        int totalPairs = expected.Count+bonusPairs.Count;
        // 杀怪对象明细是变长段：条数在151，条目从155起各8B，其后所有固定字段与卡牌区整体平移8N。
        int shift = checked((int)U32(body,151)*8);
        That(body.Length == 218 + shift + totalPairs * 8 && body[156+shift] == expected.Count && body[193 + shift + totalPairs * 8] == 0 &&
            body[^26] == (options.DoublePaidItems ? 1 : 0) && body[^27] == 0 && body[^8] == 0 &&
            body.AsSpan(body.Length-7).IndexOfAnyExcept((byte)0) == -1 && U32(body,164+shift+expected.Count*8) == options.Price,
            "35个人冻结候选、x2及回归标记按持久计划独立核对，不混为黑钻标志");
        for (int index = 0; index < expected.Count; index++)
            That(U32(body,157+shift+index*8) == expected[index].Item && U32(body,161+shift+index*8) == expected[index].Amount,
                "免费牌每项模板ID和本次数量对应实际金币或冻结物品");
        int bonusAt = 176+shift+expected.Count*8;
        That(body[bonusAt] == bonusPairs.Count,"35黑钻单独预载额外奖励组，非会员为空");
        for (int index = 0; index < bonusPairs.Count; index++)
            That(U32(body,bonusAt+1+index*8) == bonusPairs[index].Item && U32(body,bonusAt+5+index*8) == bonusPairs[index].Amount,
                "35黑钻条目精确对应同事务追加的实际金币及冻结核心数量");
        int inventoryNotices = DrainCardInventory(peer, fixture);
        That(inventoryNotices == 0,"首次和重复通关都不提前发翻牌库存差量，幸运星余额仍按既有链发布");
    }

    static int DrainCardInventory(TcpPeer peer, TcpTestFixture? fixture = null)
    {
        int inventoryNotices = 0;
        bool luckyStarSeen = false;
        peer.SendBarrier();
        while (true)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == 1347)
            {
                TestCheck.Bytes(packet.Body,[1],"翻牌库存更新后同步屏障");
                return inventoryNotices;
            }
            if (packet.Kind == 0 && packet.Opcode == 988 && fixture is not null)
            {
                That(!luckyStarSeen && inventoryNotices == 0 && packet.Body.Length == 8 && U32(packet.Body,4) == 0 &&
                    U32(packet.Body,0) == Convert.ToInt64(fixture.Query("SELECT rental_points FROM account_wallets WHERE account=$p0",
                        fixture.Options.DevelopmentAccount).Single()[0]),
                    "结算幸运星在库存差量前投影实际账号余额且只通知一次");
                luckyStarSeen = true;
                continue;
            }
            That(packet.Kind == 0 && packet.Opcode == 14,"翻牌/德利拉出售在提交后发送完整库存差量");
            inventoryNotices++;
        }
    }

    static ulong Experience(TcpTestFixture fixture, ushort id) => checked((ulong)Convert.ToInt64(fixture.Query("SELECT experience FROM characters WHERE id=$p0", id)[0][0]));
    static int PickupResourceDrops(TcpTestFixture fixture, TcpPeer peer, byte[] death, uint questId)
    {
        int count = U16(death, 2);
        That(death.Length == 9 + count * 131, "默认资源策略发送全部冻结行，数量与完整正文一致");
        var keys = new HashSet<uint>();
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            int offset = 4 + ordinal * 131;
            uint ground = U32(death, offset), itemId = U32(death, offset + 6), value = U32(death, offset + 10);
            That(keys.Add(ground) && ground != peer.Id && (itemId != 0 || value > 0), "各物品/金币保留独立key与合法核心");
            byte[] pickup = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(pickup, ground);
            peer.Send(43, pickup);
            byte[] got = peer.Expect(0, 39).Body;
            That(U32(got, 0) == ground && U16(got, 4) == peer.Id, "逐件拾取回报本次实际地面与本人身份");
            if (itemId == 0)
            {
                int cursor = 6;
                for (int member = 0; member < 8; member++)
                {
                    byte active = got[cursor++]; uint amount = U32(got, cursor); cursor += 4;
                    That(active == (member == 0 ? 1 : 0) && amount == (member == 0 ? value : 0), "金币八成员分支保留实际金额");
                    if (active != 0)
                    {
                        That(got[cursor++] == 1 && U32(got, cursor) == 0 && U32(got, cursor + 4) == 0,
                            "无附加金币也进入保留地面上下文的扩展表现分支，不伪造奖金或公会基金");
                        cursor += 8;
                    }
                }
                That(got.Length == 55 && cursor == got.Length, "金币独立55B格式完整消费并保留全部八成员");
            }
            else That(got.Length == 19 && U16(got, 14) == peer.Id, "普通物品与账号材料保留完整19B格式");
            peer.Expect(0, 14); peer.Expect(1, 43, [1]);
            var row = fixture.Query("""
                SELECT p.currency_kind,p.amount,p.destination_slot,p.item_uid,o.core,o.gold_amount,b.quest_id
                FROM character_dungeon_drop_pickups p JOIN character_dungeon_drop_objects o
                  ON o.character_id=p.character_id AND o.ground_key=p.ground_key
                JOIN character_dungeon_drop_batches b ON b.character_id=o.character_id AND b.event_id=o.event_id
                WHERE p.character_id=$p0 AND p.ground_key=$p1
                """, peer.Id, ground).Single();
            That(Convert.ToUInt32(row[6]) == questId, "集合拾取沿本次真实任务/普通死亡来源提交，不借旧实例身份");
            if (itemId == 0) That(row[4] is null or DBNull && Convert.ToUInt32(row[5]) == value && Convert.ToUInt32(row[1]) == value,
                "金币地面与真实到账金额一致，不制造背包UID");
            else
            {
                var core = JsonSerializer.Deserialize<ItemCore>((string)row[4]!)!;
                That(core.ItemId == itemId && core.Value == value && core.Count == Convert.ToInt32(row[1]),
                    "物品模板、冻结属性种子和数量沿同一集合持久化");
                if (row[0] is null or DBNull) That(Convert.ToInt64(row[3]) > 0 && Convert.ToUInt16(row[2]) == U16(got, 16),
                    "实体拾取使用已提交UID及目的槽");
            }
            string beforeDuplicate = fixture.Snapshot();
            peer.Send(43, pickup); peer.Expect(1, 43, [0, 19, 0]); peer.Barrier();
            That(fixture.Snapshot() == beforeDuplicate, "重复拾取不增加角色、账号钱包或物品资产");
        }
        return count;
    }

    static void VerifyRevive(TcpTestFixture fixture, TcpPeer peer)
    {
        byte[] key = [(byte)peer.Id, (byte)(peer.Id >> 8)];
        string Wallet() => JsonSerializer.Serialize(fixture.Query("SELECT revision,revive_coins FROM characters WHERE id=$p0", peer.Id));
        string before = Wallet();
        peer.Send(41, key);
        peer.Barrier();
        That(Wallet() == before, "活角色复活请求无扣币或成功包");
        for (int remaining = 1; remaining >= 0; remaining--)
        {
            peer.Send(40, [0x74, 0, 4, 1, 0, 0]);
            peer.Expect(0, 32, [key[0], key[1], 0, 0]);
            peer.Send(41, [0xFF, 0xFF]);
            peer.Barrier();
            That(Wallet() == before, "错误复活目标key无扣币");
            peer.Send(41, key);
            peer.Expect(1, 41, [1, key[0], key[1], 0]);
            peer.Expect(0, 32, [key[0], key[1], 1, 0]);
            byte[] balance = new byte[126];
            balance[1] = 1;
            balance[3] = 1;
            balance[5] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(balance.AsSpan(9), remaining);
            peer.Expect(0, 14, balance);
            before = Wallet();
            peer.Send(41, key);
            peer.Barrier();
            That(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", peer.Id) is [[var coins]] &&
                Convert.ToInt32(coins) == remaining && Wallet() == before, "完整ACK41→32→余额14及重复请求只消费一次");
        }
        // 两次复活后继续本组原有击杀、过房、结算与冷启动链。
    }

    static ulong U64(byte[] body, int at) => BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at));
    internal static void EntryTown(TcpPeer peer)
    {
        peer.Travel(38, 0, 622, 196, 38, 1, bottom: true);
        peer.Travel(38, 2, 1085, 249, 38, 0, kind: 5);
    }
    internal static byte[] Enter(TcpPeer peer, ushort questId = 3145, uint dungeonId = 3, Action<byte[]>? information = null,
        ushort? requestQuestId = null)
    {
        peer.Send(31,[31,0,(byte)questId,(byte)(questId >> 8)]);
        byte[] accepted = peer.Expect(1,31).Body;
        if (accepted[0] == 1) RefreshedQuests(peer);
        else Bytes(accepted,[0,18,0],"重复接取通过通用错误响应，不改变已有副本fixture");
        SelectMap(peer);
        var selection = Selection(); BinaryPrimitives.WriteUInt32LittleEndian(selection,dungeonId);
        BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),requestQuestId ?? questId);
        peer.Send(16, selection);
        var entered = peer.Expect(0, 28).Body;
        That(entered.Length == 36, "正式NOTI28完整36B"); information?.Invoke(entered);
        return peer.Expect(0, 29).Body;
    }
    static void VerifyDummyBossEntry(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth)
    {
        var area = resources.Towns.Areas.Values.First(area => area.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 51 && option.QuestId == -1));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).ToHashSet();
        done.UnionWith(area.Map(done).DungeonGates.Where(gate => gate.Conditions.Length == 0).SelectMany(gate => gate.Quests));
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new DfoServer.GameWorld.Towns.Location(area.Town,area.Area,
            checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        DfoServer.Game.Characters.Character created;
        using (var owner = new DfoServer.Game.Accounts.AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
        {
            var candidate = DfoServer.Game.Characters.CharacterCreation.Prepare("DummyMaze",resources.Characters.Get(0),position,position,resources.Learning.Get);
            created = new DfoServer.Persistence.CharacterRepository(fixture.Database).Create(owner,candidate with
            {
                Level = 75,AcceptedStoryLevel = 75,CompletedQuests = done.ToArray(),
                InitialLearning = DfoServer.Game.Skills.SkillPointLedger.Synchronize(0,0,75,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(75) },resources.Learning)
            });
        }
        using var peer = Open(fixture);
        peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
        SelectMap(peer);
        byte[] request = Selection();
        BinaryPrimitives.WriteUInt32LittleEndian(request,51);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16),0);
        peer.Send(16,request);
        byte[] information = peer.Expect(0,28).Body; byte[] map = peer.Expect(0,29).Body;
        That(information.Length == 36 && U32(information,0) == 51 && information[7] == 0 &&
            (information[8],information[9]) is (1,0) or (1,1) or (2,0) && U32(map,14) == 58901 && map[18] > 0,
            "D51原始普通请求不再ACK16错误9，按原生28/29进入起点并下发实际演员");
        var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(51).Mazes[information[7]];
        var goal = new DungeonPoint(information[8],information[9]);
        var current = new DungeonPoint(map[0],map[1]);
        var seen = new HashSet<ushort>();
        var cache = new Dictionary<DungeonPoint,uint>();
        var normalVisits = new HashSet<DungeonPoint>();
        var candidates = new Dictionary<DungeonPoint,(uint Normal,uint Dummy,uint Boss)>
        {
            [new(1,1)] = (58952,58908,58911),[new(2,0)] = (58953,58909,58912),[new(1,0)] = (58954,58910,58913)
        };
        int advances = 0; bool finished = false;
        for (int moves = 0; moves < 64 && !finished; moves++)
        {
            Load(peer); peer.Expect(0,36);
            if (map[13] == 0)
                That(cache.ContainsKey(current),"dummy路线普通回访只能安装已有房间缓存");
            else
            {
                cache[current] = U32(map,14);
                if (candidates.TryGetValue(current,out var expected))
                {
                    That(U32(map,14) == (current != goal ? expected.Normal : advances < 2 ? expected.Dummy : expected.Boss),
                        "D51按本位置普通/dummy/Boss候选下发真实ID，不提前使用dummy或抽取其他任务地图");
                    if (current != goal) normalVisits.Add(current);
                }
                var actors = Enumerable.Range(0,map[18]).Select(index => 19+22*index)
                    .Where(at => map[at+13] <= 3 && map[at+21] == 0).OrderBy(at => map[at+13] == 3).ToArray();
                foreach (int at in actors)
                {
                    ushort key = U16(map,at+6); uint template = U32(map,at+8);
                    if (current != goal)
                        That(template is not (1078 or 1077) && map[at+13] != 3,
                            "没有当前Boss标记的真实D51候选房不创建假Boss或真Boss，不仅是降低演员类型");
                    That(seen.Add(key),"阶段替换与普通房间使用独立对象key，不复用已死亡演员");
                    if (template == 1078) That(map[at+13] == 3,"真实D51的MAP级dummy演员下发Boss3，进入本版原生假Boss初始化");
                    var oldGoal = goal;
                    peer.Send(39,Death(key,peer.Id,map[at+12]));
                    byte[] death = peer.Expect(0,38).Body;
                    int tail = 4+U16(death,2)*131;
                    That(U16(death,0) == key,"dummy路线死亡通知绑定实际演员");
                    if (template == 1078)
                    {
                        That(death.Length == tail+7 && death.AsSpan(tail,4).SequenceEqual(new byte[] { 0,0,255,1 }) && death[^1] == 0,
                            "dummy死亡通过本版38完整掉落尾部同步唯一Boss目标");
                        goal = new(death[tail+4],death[tail+5]);
                        That(maze.Bosses.Contains(goal) && current == oldGoal && goal != oldGoal,
                            "只有当前目标格实际生成的dummy死亡才推进下一标记");
                        if (goal != oldGoal) advances++;
                    }
                    else Bytes(death.AsSpan(tail).ToArray(),[0,0,255,0,0],"普通死亡不混入Boss坐标更新");
                    peer.Expect(0,37);
                    if (template == 1077)
                    {
                        That(map[at+13] == 3 && current == goal && advances == 2,"两次dummy标记推进后才抵达唯一真Boss");
                        peer.Expect(0,115,[1,1,(byte)key,(byte)(key>>8)]); peer.Expect(0,31,[]);
                        finished = true;
                    }
                }
            }
            peer.Barrier();
            if (finished) break;
            // 有的候选在当前Boss后方，只提前访问无需穿过未清目标的普通格。
            var parents = Paths(false);
            var destination = maze.Bosses.Where(point => point != goal && !cache.ContainsKey(point) && parents.ContainsKey(point))
                .Select(point => (DungeonPoint?)point).FirstOrDefault() ?? goal;
            if (destination == goal) parents = Paths(true);
            That(parents.ContainsKey(destination) && current != destination,"先访问未标记普通候选，再按已公布目标推进可达路线");
            var target = destination;
            while (parents[target] is { } parent && parent != current) target = parent;
            byte[] move = new byte[100]; move[0] = checked((byte)target.X); move[1] = checked((byte)target.Y);
            peer.Send(45,move);
            peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body; current = target;
        }
        That(finished && advances == 2 && normalVisits.Count > 0,
            "真实D51生产TCP先验证可达未标记候选不生Boss，再完成两次dummy目标更新及真Boss许可");

        Dictionary<DungeonPoint,DungeonPoint?> Paths(bool includeGoal)
        {
            var parents = new Dictionary<DungeonPoint,DungeonPoint?> { [current] = null };
            var queue = new Queue<DungeonPoint>(); queue.Enqueue(current);
            while (queue.TryDequeue(out var point))
            foreach (var link in maze.StaticTopology.Links!)
            {
                DungeonPoint? next = link.First == point ? link.Second : link.Second == point ? link.First : null;
                if (next is { } adjacent && (includeGoal || adjacent != goal) && parents.TryAdd(adjacent,point)) queue.Enqueue(adjacent);
            }
            return parents;
        }
    }

    // 亡者峡谷世界地图同时含悲叹之塔入口：选图空列表末尾u16带当前悲叹楼层，且前面必须先发0x34通关状态。
    static void SelectMap(TcpPeer peer, ushort griefLayer = 0)
    {
        peer.Send(15, new byte[4]);
        peer.Expect(1, 15, [1]);
        peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
        byte[] notice = new byte[42];
        BinaryPrimitives.WriteUInt16LittleEndian(notice.AsSpan(39), griefLayer);
        peer.Expect(0, 27, notice);
        That(griefLayer == 0 ? peer.GriefTowerClearInfo.Length == 0 : peer.GriefTowerClearInfo is [0x34, _],
            "悲叹入口世界地图的选图通知与0x34通关状态成对，空列表只带当前楼层");
    }
    static byte[] Selection(byte difficulty = 0)
    {
        byte[] body = Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000");
        body[4] = difficulty;
        return body;
    }
    internal static void Load(TcpPeer peer)
    {
        peer.Send(37, new byte[8]);
        peer.Expect(1, 37, [1]);
        peer.Expect(0, 30, new byte[5]);
        peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
    }
    // 明确合成的网络统计。独立正文编码/MD5折叠，不调用服务Parser/Builder自证。
    internal static byte[] Death(ushort key, ushort reporter, byte level, ulong decrease = 600,
        uint totalCount = 1, ushort ownCount = 1, ulong? totalDecrease = null)
    {
        byte[] bytes = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, key);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), reporter);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(18), totalDecrease ?? decrease + 50);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(26), totalCount);
        bytes[30] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(31), 0x111);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(33), reporter);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(35), decrease);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(43), ownCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), level);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(60), 504);
        SealDeath(bytes);
        return bytes;
    }

    internal static void SealDeath(byte[] bytes)
    {
        int tail = bytes.Length - 4;
        byte[] digest = MD5.HashData(bytes.AsSpan(0, tail));
        bytes[tail] = (byte)(digest[0] ^ digest[13] ^ digest[8] ^ digest[10] ^ 0x81);
        bytes[tail + 1] = (byte)(digest[1] ^ digest[9] ^ digest[5] ^ digest[12] ^ 0x78);
        bytes[tail + 2] = (byte)(digest[2] ^ digest[6] ^ digest[14] ^ digest[4] ^ 0x1A);
        bytes[tail + 3] = (byte)(digest[3] ^ digest[15] ^ digest[11] ^ digest[7] ^ 0xBF);
    }
}
