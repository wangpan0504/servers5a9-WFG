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
    // 1) 未完成 [hell quest] 6080 → 拒绝 [0,0xAD,0,0]；2) 已完成但没有 3330 邀请函 → 拒绝 [0,0x11,0,0]，不落任何入场状态；
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
            SelectMap(peer);
            peer.Send(16,request);
            if (!qualified || tickets < 30)
            {
                peer.Expect(1,16,[0,(byte)(qualified ? 0x11 : 0xAD),0,0]); peer.Barrier();
                That(fixture.Query("SELECT count(*) FROM character_dungeon_state WHERE character_id=$p0 AND last_attempt>0",peer.Id) is [[0L]] &&
                    Tickets(created.Id) == tickets,
                    "深渊派对拒绝（" + name + "）：" + (qualified ? "票不够以 0x11 拒绝" : "未完成 6080 以 0xAD 拒绝") + "，不落入场状态，背包票数不变");
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
            fixture.Sql("UPDATE character_fatigue SET remaining=8,used=maximum-8 WHERE character_id=$p0", cosmoCharacter.Id);
            using (var peer = Open(fixture))
            {
                peer.Select(cosmoCharacter.Slot, prepared: true, resumeTown: true, room: false);
                SelectMap(peer);
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
                peer.Barrier();
                That(Tickets(peer.Id) == (usePass ? 40 : 0) &&
                    fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10305821", peer.Id) is var stones &&
                    Convert.ToInt64(stones[0][0]) == (usePass ? 1 : 0) &&
                    fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=490705255", peer.Id) is [[0L]] &&
                    fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0", peer.Id) is [[0L]] &&
                    fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[1L]],
                    "超星空真实TCP优先消耗1通行证，否则扣40邀请函和4灵石；均扣8疲劳，只建立一次尝试");
                Load(peer);
                peer.Expect(0, 36);
                peer.Barrier();
                string loaded = fixture.Snapshot();
                peer.Send(16, cosmoRequest);
                peer.Send(37, new byte[8]);
                peer.Barrier();
                That(fixture.Snapshot() == loaded, "超星空重复入场及加载不重复扣除或预留实例");
            }
        }
    }
}
