using System.Text.Json;
using System.Buffers.Binary;
using System.Security.Cryptography;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.Game.Quests;
using DfoServer.Development.Dungeons;
using DfoServer.Network.Management;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class FormalDungeonTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var growth = CharacterGrowthCatalog.Load(resources.Archive);
        var kills = DungeonKillExperienceCatalog.Load(resources.Archive);
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled }, Path.Combine(root, "formal-dungeon"), gm: true, resources: resources);
        await fixture.StartAsync();
        byte[]? oldReport = null;
        ulong experience = 0;
        ushort nextKey;
        int picked = 0;
        int upgradedRoomMoves = 0;
        byte projectedLevel = 1;
        byte automaticLevel = resources.Characters.Get(0).Skills.Single(skill => skill.Id == 174).Level;
        using (var peer = Open(fixture))
        {
            peer.Create("FormalTcp");
            using (var owner = new DfoServer.Game.Accounts.AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var repository = new DfoServer.Persistence.CharacterRepository(fixture.Database);
                var character = repository.LoadCharacter(owner, 0);
                var items = resources.ItemResources;
                var grants = new DfoServer.Game.Inventory.Services.InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
                DfoServer.Game.Inventory.Services.InventoryService.Commit(repository, owner, character, grants.Grant(character, [new(1000, 5)]));
            }
            peer.Select();
            EntryTown(peer);
            var map = Enter(peer);
            var target = new (byte X, byte Y)[] { (1, 1), (2, 1), (2, 0), (3, 0), (4, 0) };
            int[] ids = [76121, 76122, 76123, 76124, 76125, 76126];
            for (int room = 0; room < ids.Length; room++)
            {
                byte roomLevel = projectedLevel;
                That(U32(map, 14) == ids[room], "正式TCP依匹配3145六房顺序加载");
                Load(peer);
                if (room == 0) VerifyDungeonActions(fixture, peer);
                string beforeMovement = fixture.Snapshot();
                peer.Send(35, Move(1000, 249));
                peer.Barrier();
                That(fixture.Snapshot() == beforeMovement, "正式副本阻止城镇位置保存");
                if (oldReport is not null)
                {
                    peer.Send(39, oldReport);
                    peer.Barrier();
                    That(Experience(fixture, peer.Id) == experience, "新房旧key没有经验或死亡通知");
                }
                int count = map[18];
                for (int index = 0; index < count; index++)
                {
                    int offset = 19 + index * 22;
                    ushort key = U16(map, offset + 6);
                    byte level = map[offset + 12];
                    That(level == (room == 4 && index == 4 ? 5 : 3), "TCP生成等级包含副本基准5加偏移0的哥布林");
                    uint expectedGain = level == 5 ? 137u : projectedLevel < 3 ? 104u : 93u;
                    byte[] report = Death(key, peer.Id, level);
                    if (room == ids.Length - 1 && U32(map, offset + 8) == 107000906)
                    {
                        peer.Send(39, report); peer.Barrier();
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
                    }
                    peer.Send(39, report);
                    byte[] deathNotice = peer.Expect(0, 38).Body;
                    int dropCount = U16(deathNotice, 2);
                    That(U16(deathNotice, 0) == key && dropCount is 0 or 1 && deathNotice.Length == 9 + 131 * dropCount &&
                        deathNotice.AsSpan(deathNotice.Length - 5).SequenceEqual(new byte[] { 0, 0, 255, 0, 0 }),
                        "正式死亡接受不同报告等级和零扣血统计，投影保留完整可变掉落行及尾部");
                    byte[] notice = ReadGrowth(room == ids.Length - 1 && map[offset + 13] == 3);
                    experience += expectedGain;
                    byte expectedLevel = 1;
                    while (experience >= growth.GetCumulativeExperienceBoundary(expectedLevel)) expectedLevel++;
                    That(notice.Length == 91 && notice[0] == expectedLevel && U64(notice, 1) == experience &&
                        U32(notice, 9) == expectedGain && U16(notice, 13) == growth.GetSpDelta(1, expectedLevel) &&
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
                    if (dropCount != 0)
                    {
                        uint ground = U32(deathNotice, 4);
                        int itemId = checked((int)U32(deathNotice, 10));
                        That(itemId is 1000 or 1047 or 1004 && U32(deathNotice, 14) == 1,
                            "本服正式随机仅生成获授权的一件普通物品");
                        byte[] getItem = new byte[20];
                        BinaryPrimitives.WriteUInt32LittleEndian(getItem, ground);
                        peer.Send(43, getItem);
                        byte[] got = peer.Expect(0, 39).Body;
                        That(got.Length == 19 && U32(got, 0) == ground && U16(got, 4) == peer.Id && U16(got, 14) == peer.Id,
                            "实际普通20B拾取获得完整19B NOTI39，不追加空42");
                        ushort slot = U16(got, 16);
                        peer.Expect(0, 14);
                        peer.Expect(1, 43, [1]);
                        That(fixture.Query("SELECT destination_slot,item_uid FROM character_dungeon_pickup_receipts WHERE character_id=$p0 AND ground_key=$p1", peer.Id, ground)
                            is [{ Length: 2 } row] && Convert.ToUInt16(row[0]) == slot && Convert.ToInt64(row[1]) > 0,
                            "拾取通知使用同事务实际UID与目的槽");
                        string assets = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
                        peer.Send(43, getItem);
                        peer.Expect(1, 43, [0, 22, 0]);
                        peer.Barrier();
                        That(fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") == assets,
                            "重复拾取明确拒绝并恢复等待，库存不再增加");
                        picked++;
                    }
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
                        "精确重复及相容统计变化的同怪重复均不通知、追加经验或掉落receipt、生成物品UID");
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
            var clearCatalog = DungeonClearExperienceCatalog.Load(resources.Archive);
            // 修复后击杀XP为2868；最高评分456使首次通关跨过Lv3边界3315。
            var clearReward = clearCatalog.Calculate(DungeonCatalog.Load(resources.Archive).Get(3), 0, 99);
            byte[] reportResult = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id, 99);
            byte[] corruptedResult = (byte[])reportResult.Clone(); corruptedResult[49] ^= 1;
            peer.Send(46, corruptedResult); peer.Barrier();
            That(Experience(fixture, peer.Id) == experience, "46检查值错误不触发结算或经验");
            string assetsBeforeClear = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
            peer.Send(46, reportResult);
            ReadClear();
            experience += clearReward.TotalExperience;
            That(Experience(fixture, peer.Id) == experience && fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0", peer.Id) is [[1L]],
                "通关基础与评分经验仅一次提交，击杀经验不再累计");
            peer.Send(46, reportResult); ReadClear(duplicate: true);
            That(Experience(fixture, peer.Id) == experience, "同报告46回放投影不再加经验");
            byte[] changedResult = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id, 90);
            peer.Send(46, changedResult); peer.Barrier();
            That(Experience(fixture, peer.Id) == experience, "改变评分的46不能替换冻结报告");
            peer.Send(69); peer.Expect(1, 69, [1]);
            peer.Send(70); That(peer.Expect(1, 70).Body.Length == 17, "70普通八槽完整ACK");
            peer.Send(71, [0, 0]); That(peer.Expect(1, 71).Body.Length == 33, "71空道具结果保留八槽完整ACK");
            peer.Send(72, [2, 2]); peer.Expect(1, 72, [1, 2, 2]);
            peer.Send(72, [1, 2]); peer.Expect(1, 72, [1, 1, 2]);
            peer.Expect(0, 24);
            That(QuestLifecycleTcpTests.List(peer.Expect(0, 21).Body, projectedLevel).Contains(3145),
                "结果页72回城在绑定场景后刷新当前任务，未领奖的任务仍保留");
            peer.Barrier();
            That(fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") == assetsBeforeClear &&
                Experience(fixture, peer.Id) == experience, "空翻牌及结果页回城不生成道具或重复经验");
            ClearAgain(Enter(peer));
            peer.Send(46, reportResult); ReadClear(); experience += clearReward.TotalExperience;
            peer.Send(72, [1, 0]); peer.Expect(1, 72, [1, 1, 0]); peer.Expect(0, 261, [9]);
            peer.Expect(0, 28);
            byte[] retryMap = peer.Expect(0, 29).Body;
            That(U16(retryMap, 25) > U16(map, 25), "再挑战创建新Run、新尝试及新怪key");
            ClearAgain(retryMap);
            peer.Send(46, reportResult); ReadClear(); experience += clearReward.TotalExperience;
            peer.Send(72, [1, 1]); peer.Expect(1, 72, [1, 1, 1]); peer.Expect(1, 15, [1]);
            peer.Send(72, [1, 3]); peer.Expect(1, 72, [1, 1, 3]);
            peer.Expect(0,24); peer.Expect(0,21); peer.Barrier();
            peer.Send(15,new byte[4]); peer.Expect(1,15,[1]); peer.Barrier();
            That(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) is [[3L]],
                "剧情没有已接取且合法的后继任务时回城，真实15只结束旧场景，不生成新副本");
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
                ulong expectedExperience = experience + (duplicate ? 0 : clearReward.TotalExperience);
                byte expectedLevel = 1;
                while (expectedExperience >= growth.GetCumulativeExperienceBoundary(expectedLevel)) expectedLevel++;
                byte[] clear = peer.Expect(0, 35).Body;
                That(result.Length == 16 && result[6] == 99 && U16(result, 9) == peer.Id && exp.Length == 91 &&
                    U64(exp, 1) == expectedExperience && exp[0] == expectedLevel &&
                    U32(exp, 9) == clearReward.TotalExperience && clear.Length == 218 && U32(clear, 0) == clearReward.BaseExperience &&
                    U32(clear, 4) == clearReward.RankExperience && clear[192] == 1,
                    "正式结算34→37→35匹配宽度，显示冻结评分及已提交奖励");
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
                        if (U32(startMap, offset + 8) == 107000906) continue;
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
                if (packet[0] > automaticLevel)
                {
                    var skills = SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body);
                    That(skills.All(tree => tree.Skills.Single(skill => skill.Id == 174).Level == packet[0]) &&
                        skills[0].Skills.Single(skill => skill.Id == 5).Slot == 6,
                        "升级固定技能时跟随完整技能同步，战斗换槽保持稳定");
                    automaticLevel = packet[0];
                }
                if (packet[0] != projectedLevel)
                    ExpectLevelRefresh(peer, packet[0], U64(packet, 1));
                projectedLevel = packet[0];
                return packet;
            }
        }
        VerifyEntryRecoveryAndJobs(fixture, kills);
        string persisted = fixture.Snapshot("SELECT level,experience FROM characters");
        string inventory = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
        string points = fixture.Snapshot("SELECT context,sp,tp FROM character_skill_points ORDER BY context");
        await fixture.StopAsync();
        await fixture.StartAsync("actual-restart", isolatedProcess: true);
        That(fixture.Snapshot("SELECT level,experience FROM characters") == persisted &&
            fixture.Snapshot("SELECT context,sp,tp FROM character_skill_points ORDER BY context") == points &&
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") == inventory,
            "真实停服重启保留经验等级及两树SP");
        using (var peer = Open(fixture))
        {
            peer.Select();
            EntryTown(peer);
            var map = Enter(peer);
            That(U16(map, 25) >= nextKey, "重登及服务重启不复用旧实例怪key");
            Load(peer);
            peer.Send(39, oldReport!);
            peer.Barrier();
            That(Experience(fixture, peer.Id) == experience, "新实例拒绝上轮死亡请求，不增加资产经验");
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0, 24); peer.Expect(0, 21); peer.Barrier();
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
                    QuestCatalog.Load(resources.Archive).Get(3145), QuestRewardParameterCatalog.Load(resources.Archive)).Amount;
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
            if (rewardLevel > previousLevel) peer.Expect(0, 19);
            That(questGrowth.Length == 91 && questGrowth[0] == rewardLevel && U64(questGrowth, 1) == experience &&
                U32(questGrowth, 9) == questExperience && U16(questGrowth, 13) == growth.GetSpDelta(1, rewardLevel) &&
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

        }
        await fixture.StopAsync();
        Console.WriteLine($"PASS 正式TCP静默拒绝后回城重进、全部职业和转职加载、原生统计合成击杀、六房升级、真实随机拾取{picked}件、重复拒绝及服务重启（非Windows实机；零样本不作拾取覆盖）");
    }

    static void ExpectLevelRefresh(TcpPeer peer, byte level, ulong experience, bool town = false)
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
        if (!town)
            That(QuestLifecycleTcpTests.List(peer.Expect(0, 21).Body, level).Contains(3145),
                "副本和结算升级后按当前等级刷新任务，不以场景门禁省略");
    }

    static void VerifyDungeonActions(TcpTestFixture fixture, TcpPeer peer)
    {
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
        var row = fixture.Query("SELECT slot_index FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0", peer.Id).Single();
        short from = Convert.ToInt16(row[0]);
        const short to = 3;
        peer.Send(19, Build(writer =>
        {
            writer.Write((byte)0); writer.Write(from); writer.Write(1000); writer.Write(0);
            writer.Write((byte)0); writer.Write(to); writer.Write(0); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
        }));
        peer.Expect(1, 19, Build(writer => { writer.Write((byte)1); writer.Write((byte)0); writer.Write(from); writer.Write(5); writer.Write((byte)0); writer.Write(to); }));
        peer.Expect(0, 14);
        peer.Send(713, [0, 3, 0]); peer.Expect(1, 713, [1, 0, 3, 0]);
        peer.Send(714, [0, 3, 0]); peer.Expect(1, 714, [1, 0, 3, 0]);
        peer.Barrier();
        That(progress == fixture.Snapshot("SELECT experience,level FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") +
            fixture.Snapshot("SELECT * FROM character_skill_points ORDER BY character_id,context"),
            "战斗中换槽、学习拒绝、洗点和背包移动/锁定不改run、经验或SP，后续击杀与结算继续");
        That(fixture.Query("SELECT slot FROM character_skill_bindings WHERE character_id=$p0 AND context=0 AND skill_id=5", peer.Id) is [[6L]],
            "战斗中的快捷栏换槽已持久，洗点保留免费技能的已有槽位");
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
                That(U32(growth, 9) == 104 && Experience(fixture, peer.Id) == 104,
                    "女圣职者拒绝、132返回、15重进后可完成正式死亡成长");
            }
            peer.Send(42);
            peer.Expect(1, 42, [1]);
            peer.Expect(0, 24);
            peer.Expect(0, 21);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Create("PromotedTcp");
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "grow PromotedTcp 37", out string reply) && reply.StartsWith("OK "),
                "独立测试服务通过正式GM存储用例更新转职字节");
            peer.Select(slot);
            EntryTown(peer);
            Enter(peer);
            Load(peer);
            That(fixture.Query("SELECT grow_type,last_attempt FROM characters JOIN character_dungeon_state ON character_id=id WHERE id=$p0", peer.Id)
                is [[37L, 1L]], "转职角色真实TCP准入加载，不依赖普通职业成长资格");
            peer.Send(42);
            peer.Expect(1, 42, [1]);
            peer.Expect(0, 24);
            peer.Expect(0, 21);
            peer.Barrier();
        }
    }

    static ulong Experience(TcpTestFixture fixture, ushort id) => checked((ulong)Convert.ToInt64(fixture.Query("SELECT experience FROM characters WHERE id=$p0", id)[0][0]));
    static ulong U64(byte[] body, int at) => BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at));
    internal static void EntryTown(TcpPeer peer)
    {
        peer.Travel(38, 0, 622, 196, 38, 1, bottom: true);
        peer.Travel(38, 2, 1085, 249, 38, 0, kind: 5);
    }
    internal static byte[] Enter(TcpPeer peer, ushort questId = 3145, uint dungeonId = 3, Action<byte[]>? information = null)
    {
        peer.Send(31,[31,0,(byte)questId,(byte)(questId >> 8)]);
        byte[] accepted = peer.Expect(1,31).Body;
        if (accepted[0] == 1) peer.Expect(0,21);
        else Bytes(accepted,[0,18,0],"重复接取通过通用错误响应，不改变已有副本fixture");
        SelectMap(peer);
        var selection = Selection(); BinaryPrimitives.WriteUInt32LittleEndian(selection,dungeonId);
        BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),questId);
        peer.Send(16, selection);
        var entered = peer.Expect(0, 28).Body;
        That(entered.Length == 36, "正式NOTI28完整36B"); information?.Invoke(entered);
        return peer.Expect(0, 29).Body;
    }
    static void SelectMap(TcpPeer peer)
    {
        peer.Send(15, new byte[4]);
        peer.Expect(1, 15, [1]);
        peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
        peer.Expect(0, 27, new byte[42]);
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
