using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Network.Management;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    // 实验性（#256，未在95客户端实证）：深渊派对入场的真实 TCP，op16 请求 [7]=1。
    // 1) 未完成 [hell quest] 6080 → NOTI27禁用按钮，强制请求拒绝 [0,9,0,0]；2) 已完成但没有 3330 邀请函 → 拒绝 [0,0x11,0,0]，不落任何入场状态；
    // 3) 已完成且有 30 张 3330 → 入场成功，DUNGEON_INFO/START_MAP 带深渊字段，入场提交时扣 30 张 3330（NOTI 14 背包增量 + 库内数量归零 + 入场状态落库）；
    // 4) 只有 29 张 → 同样 0x11，背包原样。
    static async Task VerifyHellPartyEntryAsync(ServerOptions source,string root,TcpTestResources resources,CharacterGrowthCatalog growth)
    {
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"hell-party-entry"),resources:resources,gm:true);
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _),
            "深渊入场回归关闭开发无限疲劳，验证真实扣费");
        var baseDone = new HashSet<int>();
        var area = resources.Towns.Areas.Values.First(row => row.Level <= 95 && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 291100308));
        baseDone.UnionWith(area.Quests);
        if (area.AnyQuests.Length != 0) baseDone.Add(area.AnyQuests[0]);
        var walk = area.Map(baseDone).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done => baseDone.Contains(done))).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(walk.X+walk.Width/2)),checked((ushort)(walk.Y+walk.Height/2)));
        // 实测请求：副本 291100308，[4]=4，[7]=1，[16:20]=6114。
        byte[] request = Convert.FromHexString("94D6591104000001" + "00FFFF" + "0000000000" + "E2170000" + "00" + "00000000");
        long Tickets(long characterId) => (long)fixture.Query("SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=3330",characterId)[0][0];
        // 哈林、地轨中心等世界地图与黎明裂缝530共用时，选图会先补发1545/550；与本组其他入口用例一致，
        // 按角色所在世界地图显式给出本轮日剩余，不能交给27的兼容读取静默跳过。
        byte? DaybreakRemaining(TownArea row) => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 530) ? (byte)2 : null;
        foreach (var (name,qualified,tickets) in new[] { ("HellNoQuest",false,0), ("HellNoTicket",true,0), ("HellShort",true,29), ("HellYes",true,30) })
        {
            var done = new HashSet<int>(baseDone);
            if (qualified) done.Add(6080);
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
            {
                var candidate = CharacterCreation.Prepare(name,resources.Characters.Get(0),position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with
                    { Experience=growth.GetLevelStartExperience(95) },resources.Learning);
                var repository = new CharacterRepository(fixture.Database);
                created = repository.Create(owner,candidate with { Level=95,AcceptedStoryLevel=95,
                    CompletedQuests=done.ToArray(),InitialLearning=learning });
                if (tickets > 0)
                    created = repository.CommitInventory(owner,created,new(created.Inventory,created.Inventory with
                        { Items = created.Inventory.Items.Add(new(0,new(Game.Inventory.Models.InventoryListType.Main,121),new(3330,ItemKind.Material,tickets))) },"test_hell_ticket"));
            }
            using var peer = Open(fixture);
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            string beforeSelection = fixture.Snapshot();
            SelectMap(peer, incompleteHellQuest: !qualified, daybreakRemaining: DaybreakRemaining(area));
            peer.Barrier();
            That(fixture.Snapshot() == beforeSelection, "选图资格投影不改写任务、门票、疲劳或revision");
            peer.Send(16,request);
            if (!qualified || tickets < 30)
            {
                peer.Expect(1,16,[0,(byte)(qualified ? 0x11 : 9),0,0]); peer.Barrier();
                That(fixture.Query("SELECT count(*) FROM character_dungeon_state WHERE character_id=$p0 AND last_attempt>0",peer.Id) is [[0L]] &&
                    Tickets(created.Id) == tickets,
                    "深渊派对拒绝（" + name + "）：" + (qualified ? "票不够以 0x11 拒绝" : "未完成 6080 以错误9恢复") + "，不落入场状态，背包票数不变");
                if (!qualified)
                {
                    SelectMap(peer, incompleteHellQuest: true, daybreakRemaining: DaybreakRemaining(area));
                    peer.Send(132); peer.Expect(1, 132, [1]);
                    peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
                    peer.Expect(0, 23); peer.Expect(0, 24);
                    peer.Barrier();
                    That(fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=6080", peer.Id) is [[0L]],
                        "缺区域任务的失败后同连接可重开选图并返回城镇，不补完成位绕过门禁");
                }
                continue;
            }
            var information = peer.Expect(0,28).Body;
            var map = peer.Expect(0,29).Body;
            That(information.Length == 36 && BinaryPrimitives.ReadUInt32LittleEndian(information) == 291100308 && information[4] == 4 &&
                information[10] == 2 && information[11] == 2 && BinaryPrimitives.ReadUInt16LittleEndian(information.AsSpan(14)) == 1 &&
                information[18] == 0x0B && map[7] == 1,
                "深渊派对：持票且完成 6080 放行，DUNGEON_INFO 带深渊房间坐标/hellPartyEnabled/Value2，START_MAP 模式字节=1");
            // 实验性：起点房间不是深渊房间，START_MAP 只含普通 actor 行（无波次模板行），也不发 NOTI 679。
            That(map.Length == 23 + map[18] * 22, "深渊派对起点房间 START_MAP：actor 行数与长度一致（无波次模板行）");
            byte[] delta = peer.Expect(0,14).Body;
            That(delta[0] == 0 && U16(delta,3) == 121 && Tickets(created.Id) == 0 &&
                fixture.Query("SELECT count(*) FROM character_dungeon_state WHERE character_id=$p0 AND last_attempt>0",peer.Id) is [[1L]],
                "入场提交时扣掉 30 张 3330（NOTI 14 删除第 121 格、库内数量归零、入场状态落库）");
            Load(peer); peer.Expect(0,36); peer.Barrier();
        }
        // 时空裂缝（[inferno dungeon]）：地轨中心 7539，op16 [4]=2、[7]=1。整条路线是深渊场地，没有密门房间：
        // DUNGEON_INFO 的 [10,11] 保持 FF，入场一次扣 30 张 3330 与 6 点疲劳。
        // 只准备区域 [hell quest] 2900，不写入 10110；选图与入场按 InfernoQuestUnlock 视为已满足。
        var riftArea = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 7539));
        var riftDone = new HashSet<int>(riftArea.Quests) { 2900 };
        var riftWalk = riftArea.Map(riftDone).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(riftDone.Contains)).Bounds;
        var riftPosition = new Location(riftArea.Town, riftArea.Area,
            checked((ushort)(riftWalk.X + riftWalk.Width / 2)), checked((ushort)(riftWalk.Y + riftWalk.Height / 2)));
        Character riftCharacter;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("RiftTcp", resources.Characters.Get(0), riftPosition, riftPosition, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(95) }, resources.Learning);
            var repository = new CharacterRepository(fixture.Database);
            riftCharacter = repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95,
                CompletedQuests = riftDone.ToArray(), InitialLearning = learning });
            var riftItems = resources.ItemResources;
            var riftInventory = new InventoryService(riftItems.Items, riftItems.GoldLimits, riftItems.EpicPieces);
            riftCharacter = InventoryService.Commit(repository, owner, riftCharacter, riftInventory.Grant(riftCharacter, [new(3330, 30)]));
        }
        fixture.Sql("UPDATE character_fatigue SET remaining=8,used=maximum-8 WHERE character_id=$p0", riftCharacter.Id);
        using (var peer = Open(fixture))
        {
            peer.Select(riftCharacter.Slot, prepared: true, resumeTown: true, room: false);
            SelectMap(peer, daybreakRemaining: DaybreakRemaining(riftArea));
            byte[] riftRequest = new byte[25];
            BinaryPrimitives.WriteUInt32LittleEndian(riftRequest, 7539);
            riftRequest[4] = 2;
            riftRequest[7] = 1;
            riftRequest[9] = riftRequest[10] = byte.MaxValue;
            peer.Send(16, riftRequest);
            var information = peer.Expect(0, 28).Body;
            var map = peer.Expect(0, 29).Body;
            That(information.Length == 36 && U32(information, 0) == 7539 && information[4] == 2 &&
                information[10] == 255 && information[11] == 255 && U16(information, 14) == 1 &&
                information[18] == 0x0B && map[7] == 1,
                "时空裂缝TCP：7539 放行，DUNGEON_INFO 带深渊标志但没有密门坐标，START_MAP 模式字节=1");
            peer.Expect(0, 14);
            peer.Barrier();
            That(Tickets(peer.Id) == 0 &&
                fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0", peer.Id) is [[2L]] &&
                fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[1L]],
                "时空裂缝TCP：入场一次扣 30 张 3330 与 6 点疲劳，只建立一个尝试");
            Load(peer);
            peer.Expect(0, 36);
            peer.Barrier();
        }
        // #616：同一连接先拒绝省略模式的请求，再验证合法超星空完整入场和两种材料落库。
        var cosmoArea = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 7191));
        var cosmoDone = new HashSet<int>(cosmoArea.Quests) { 6080, 10112 };
        if (cosmoArea.AnyQuests.Length != 0) cosmoDone.Add(cosmoArea.AnyQuests[0]);
        var cosmoWalk = cosmoArea.Map(cosmoDone).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(cosmoDone.Contains)).Bounds;
        var cosmoPosition = new Location(cosmoArea.Town, cosmoArea.Area, checked((ushort)(cosmoWalk.X + cosmoWalk.Width / 2)),
            checked((ushort)(cosmoWalk.Y + cosmoWalk.Height / 2)));
        foreach (bool usePass in new[] { false, true })
        {
            Character cosmoCharacter;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var candidate = CharacterCreation.Prepare(usePass ? "CosmoPass" : "CosmoTcp", resources.Characters.Get(0), cosmoPosition, cosmoPosition, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(95) }, resources.Learning);
                var repository = new CharacterRepository(fixture.Database);
                cosmoCharacter = repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95,
                    CompletedQuests = cosmoDone.ToArray(), InitialLearning = learning });
                var items = resources.ItemResources;
                var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
                cosmoCharacter = InventoryService.Commit(repository, owner, cosmoCharacter,
                    inventory.Grant(cosmoCharacter, usePass ? [new(3330, 40), new(10305821, 4), new(490705255, 1)] : [new(3330, 40), new(10305821, 4)]));
            }
            int bossBuffId = usePass ? 1275 : 300;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var repository = new CharacterRepository(fixture.Database);
                var current = repository.LoadCharacter(owner, cosmoCharacter.Slot);
                That(resources.ItemResources.RecoveryConsumables.TryGet(usePass ? 490012682 : 490705256, out var potion) &&
                    potion.BossSelectionBuff is not null, "7191投影使用真实药型资源");
                var after = current.Inventory with { BossSelectionBuff = new(usePass ? 490012682 : 490705256,
                    usePass ? 2 : 1, DailyBoosterUsage.NextReset(DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(), potion.BossSelectionBuff!) };
                repository.CommitInventory(owner, current, new(current.Inventory, after, "test_boss_projection"));
            }
            fixture.Sql("UPDATE character_fatigue SET remaining=8,used=maximum-8 WHERE character_id=$p0", cosmoCharacter.Id);
            using (var peer = Open(fixture))
            {
                peer.Select(cosmoCharacter.Slot, prepared: true, resumeTown: true, room: false, bossBuff: (bossBuffId, usePass ? 2 : 1));
                SelectMap(peer, daybreakRemaining: DaybreakRemaining(cosmoArea));
                byte[] cosmoRequest = new byte[25];
                BinaryPrimitives.WriteUInt32LittleEndian(cosmoRequest, 7191);
                cosmoRequest[4] = 4;
                cosmoRequest[9] = cosmoRequest[10] = byte.MaxValue;
                string before = fixture.Snapshot();
                peer.Send(16, cosmoRequest);
                peer.Expect(1, 16, [0, 9, 0, 0]);
                peer.Barrier();
                That(fixture.Snapshot() == before, "超星空省略深渊标志拒绝，保持任务、材料、疲劳及尝试号");
                cosmoRequest[7] = 1;
                peer.Send(16, cosmoRequest);
                var information = peer.Expect(0, 28).Body;
                var map = peer.Expect(0, 29).Body;
                That(information.Length == 36 && U32(information, 0) == 7191 && information[4] == 4 &&
                    information[10] == 255 && information[11] == 255 && U16(information, 14) == 1 &&
                    information[18] == 0x0B && map[7] == 1,
                    "合法超星空16经正式分派发送28和29，保留独立深渊模式且不伪造柱子坐标");
                peer.Expect(0, 14);
                if (usePass)
                    peer.Expect(0, 487, Build(writer =>
                    { writer.Write((byte)1); writer.Write(bossBuffId); writer.Write(1); writer.Write(0); writer.Write(0); }));
                else
                    peer.Expect(0, 488, Build(writer => { writer.Write((byte)1); writer.Write(bossBuffId); }));
                peer.Barrier();
                That(Tickets(peer.Id) == (usePass ? 40 : 0) &&
                    fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10305821", peer.Id) is var stones &&
                    Convert.ToInt64(stones[0][0]) == (usePass ? 1 : 0) &&
                    fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=490705255", peer.Id) is [[0L]] &&
                    fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0", peer.Id) is [[0L]] &&
                    fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[1L]],
                    "超星空真实TCP优先消耗1通行证，否则扣40邀请函和4灵石；均扣8疲劳，只建立一次尝试");
                Load(peer);
                if (usePass)
                    peer.Expect(0, 487, Build(writer =>
                    { writer.Write((byte)1); writer.Write(bossBuffId); writer.Write(1); writer.Write(0); writer.Write(0); }));
                peer.Expect(0, 36);
                peer.Barrier();
                string loaded = fixture.Snapshot();
                peer.Send(16, cosmoRequest);
                peer.Send(37, new byte[8]);
                peer.Barrier();
                That(fixture.Snapshot() == loaded, "超星空重复入场及加载不重复扣除或预留实例");
                VerifyCosmoActualRoute(fixture, peer, resources, information, map, usePass);
            }
        }
    }
    // 真实socket沿本次随机终点验收，不通过反复抽取或替换正式资源伪造白斯命中。
    // 三种指定领主和拾取SQL回滚另由受控Session/Handler资源用例完整覆盖。
    static void VerifyCosmoActualRoute(TcpTestFixture fixture, TcpPeer peer, TcpTestResources resources, byte[] information, byte[] map, bool strongBuff)
    {
        var definition = SelfTestCatalogs.Dungeons(resources.Archive).Get(7191);
        var maze = definition.Mazes.Single(row => row.SourceOrdinal == information[7]);
        var route = QuestStoryRouteTests.Path(maze.StaticTopology, new(map[0], map[1]), new(information[8], information[9]));
        int bossId = 0, picked = 0;
        for (int room = 0; room < route.Length; room++)
        {
            if (room != 0)
            {
                byte[] move = new byte[100]; move[0] = (byte)route[room].X; move[1] = (byte)route[room].Y;
                peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
                Load(peer);
                if (strongBuff)
                    peer.Expect(0, 487, Build(writer =>
                    { writer.Write((byte)1); writer.Write(1275); writer.Write(1); writer.Write(0); writer.Write(0); }));
                peer.Expect(0, 36);
            }
            That(map[0] == route[room].X && map[1] == route[room].Y, "超星空TCP沿真实四房拓扑前进");
            foreach (int offset in Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                .Where(offset => map[offset + 13] != 9 && map[offset + 14] == 0).OrderBy(offset => map[offset + 13] == 3))
            {
                ushort key = U16(map, offset + 6);
                bool boss = map[offset + 13] == 3;
                byte[] report = Death(key, peer.Id, map[offset + 12]);
                ulong previousExperience = Experience(fixture, peer.Id);
                string previousProficiency = fixture.Snapshot("SELECT proficiency_level,proficiency_experience FROM character_dungeon_state WHERE character_id=$p0", peer.Id);
                peer.Send(39, report);
                byte[] death = peer.Expect(0, 38).Body;
                That(U16(death, 0) == key, "超星空TCP死亡通知绑定实际场景key");
                // 满级入口沿现有合同不发布NOTI37；奖励转入真实熟练度账本，而非等待不存在的经验包。
                That(Experience(fixture, peer.Id) == previousExperience &&
                    fixture.Snapshot("SELECT proficiency_level,proficiency_experience FROM character_dungeon_state WHERE character_id=$p0", peer.Id) != previousProficiency &&
                    fixture.Query("SELECT amount,monster_level FROM character_dungeon_growth_receipts WHERE character_id=$p0 AND monster_key=$p1", peer.Id, key)
                        is [[long amount, long monsterLevel]] && amount > 0 && monsterLevel == map[offset + 12],
                    "超星空满级死亡不增加角色XP但真实熟练度与死亡来源正确落库");
                if (boss)
                {
                    bossId = checked((int)U32(map, offset + 8));
                    peer.Expect(0, 115, [1, 1, (byte)key, (byte)(key >> 8)]); peer.Expect(0, 31, []);
                    peer.Expect(0, 261, [0]);
                    var ids = Enumerable.Range(0, U16(death, 2)).Select(index => checked((int)U32(death, 4 + index * 131 + 6))).ToArray();
                    That(ids.Count(id => id == HarlemMaterialDropPolicy.FragmentId) >= 30 &&
                        ids.Count(id => id == HarlemMaterialDropPolicy.SkyWishId) >= 10,
                        "白斯增量不能覆盖超星空原有30碎片与10天空之愿");
                    VerifyWhiteBossMaterials(ids, bossId);
                    if (bossId == 109007097)
                    {
                        var tayberrs = DungeonHellEpicCatalog.Load(resources.Archive).Pool(54, 4, resources.ItemResources.Items.Get)
                            .Entries.Select(row => row.Template.Id).ToHashSet();
                        var epicIds = ids.Where(id => id != 0 && resources.ItemResources.Items.Get(id) is { Kind: ItemKind.Equipment, Rarity: 4 }).ToArray();
                        That(epicIds.All(tayberrs.Contains), "白斯socket所有已触发的史诗属于泰波尔斯池，不仅检查追加掉落");
                        Console.WriteLine($"Cosmo white boss socket epic hits: {epicIds.Length}");
                    }
                }
                picked += PickupResourceDrops(fixture, peer, death, 0);
                string saved = fixture.Snapshot();
                peer.Send(39, report); peer.Barrier();
                That(fixture.Snapshot() == saved, "超星空TCP重复死亡不重抽装备或材料、不给重复经验");
            }
        }
        That(bossId is 65587 or 107013805 or 109007097 && picked >= 40, "超星空TCP实际击杀三候选之一并完成地面拾取");
        Console.WriteLine($"PASS 超星空真实socket终点{bossId}：{picked}项掉落与拾取；指定白斯覆盖见受控资源用例");
    }

    static void VerifyWhiteBossMaterials(int[] ids, int bossId)
    {
        That(ids.Count(id => id == 10306750) == (bossId == 109007097 ? 2 : 0) && !ids.Contains(10309041),
            "白斯固定掉落2个普通反物质粒子，其他Boss不额外掉落且不误发账号绑定版本");
    }

}
