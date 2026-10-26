using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunSpecialNormalAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        SpecialNormalDungeonRewardTests.Run(resources);
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"special-normal-entry"),gm:true,resources:resources);
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort,"infinite-fatigue off",out _),"普通副本回归关闭无限疲劳");
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var growth = SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
        int sequence = 0;
        foreach (var sample in new[] { (Id:9595,Tired:false,Ticket:false),(Id:9595,Tired:false,Ticket:true),(Id:5101,Tired:true,Ticket:true),
            (Id:5101,Tired:false,Ticket:true),(Id:400000016,Tired:false,Ticket:false),
            (Id:400000016,Tired:true,Ticket:true),(Id:400000016,Tired:false,Ticket:true) })
        {
            var definition = dungeons.Get(sample.Id);
            var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == sample.Id));
            var done = new HashSet<int>(area.Quests);
            done.UnionWith([3778,4872,8682]);
            if (sample.Id == 400000016) done.Add(8672); // 超时空频道准入任务，不能仅预置最后防线坐标绕过真实出门条件。
            if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
            var options = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.Where(option => option.DungeonId == sample.Id).ToArray();
            if (!options.Any(option => option.QuestId == -1)) done.Add(options.First(option => !option.InProgress).QuestId);
            done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
            var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town,area.Area,(ushort)(walk.X+walk.Width/2),(ushort)(walk.Y+walk.Height/2));
            // 最后防线独立账号不继承普通频道历史，避免把副本准入用例变成跨类型换频道测试。
            string account = sample.Id == 400000016 ? "entry608fiendwar" : fixture.Options.DevelopmentAccount;
            Character created;
            using (var owner = new AccountOwners().Acquire(account,() => { }))
            {
                var candidate = CharacterCreation.Prepare("Entry608"+(++sequence),
                    resources.Characters.Get(0),position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(95) },resources.Learning);
                var repository = new CharacterRepository(fixture.Database);
                created = repository.Create(owner,candidate with { Level=95,AcceptedStoryLevel=95,CompletedQuests=done.ToArray(),InitialLearning=learning });
                if (sample.Ticket && definition.OrdinaryEntryTicket is { } ticket)
                {
                    var items = resources.ItemResources;
                    var inventory = new InventoryService(items.Items,items.GoldLimits,items.EpicPieces);
                    created = InventoryService.Commit(repository,owner,created,inventory.Grant(created,[new(ticket.ItemId,ticket.Count * 2)]));
                }
                if (sample.Id == 9595 && sample.Ticket)
                {
                    var items = resources.ItemResources;
                    var inventory = new InventoryService(items.Items,items.GoldLimits,items.EpicPieces);
                    created = InventoryService.Commit(repository,owner,created,inventory.Grant(created,[new(1,2)]));
                }
            }
            int fatigue = sample.Tired ? definition.StartFatigue!.Value-1 : 100;
            fixture.Sql("UPDATE character_fatigue SET remaining=$p0 WHERE character_id=$p1",fatigue,created.Id);
            // 最后防线只经频道39进入；普通选角先到共享房间，再出门到115/0，不能把上一角色的普通频道历史当作团本快照。
            using var peer = sample.Id == 400000016 ? Open(fixture,account,gamePort:ChannelTcpTests.Endpoints(fixture)[39],channelNumber:39) : Open(fixture);
            if (sample.Id == 400000016)
            {
                var entry = peer.Select(created.Slot,prepared:done.Count != 0);
                That(entry.Single(packet => packet is { Kind:0,Opcode:24 }).Body.AsSpan(0,2).SequenceEqual(new byte[] { 38,1 }) &&
                    U32(entry.Single(packet => packet is { Kind:0,Opcode:1379 }).Body) == 115,
                    "超时空普通选角绑定共享房间，并以1379指定最后防线出门城镇");
                peer.Travel(115,0,1301,428,38,1,bottom:true);
                peer.Travel(position.Town,position.Area,position.X,position.Y,115,0);
            }
            else
                peer.Select(created.Slot,prepared:done.Count != 0,resumeTown:true,room:false);
            ushort questId = sample.Id == 9595 ? (ushort)2683 : sample.Id == 5101 ? (ushort)2902 : (ushort)8683;
            peer.Send(31,[(byte)31,0,(byte)questId,(byte)(questId >> 8)]);
            var accepted = peer.Expect(1,31).Body;
            That(accepted[0] == 1 && U16(accepted,1) == questId, "普通材料或累计通关任务经真实31接取");
            Drain();
            SelectMap(peer);
            if (sample.Id == 9595 && sample.Ticket)
            {
                // tutorial表中的正式团本索引不能借普通选图进入；拒绝后仍须能进入原普通副本。
                string EntryState() => string.Join("\n", new[] { "characters", "character_inventory_items", "character_fatigue",
                    "character_dungeon_state", "character_dungeon_growth_receipts", "character_dungeon_loot_receipts",
                    "character_dungeon_card_rewards" }.Select(table => fixture.Snapshot($"SELECT * FROM {table} ORDER BY 1,2")));
                string originalEntry = EntryState();
                foreach (uint raidDungeon in new uint[] { 210,218,224 })
                {
                    byte[] raidRequest = Selection(2);
                    BinaryPrimitives.WriteUInt32LittleEndian(raidRequest,raidDungeon);
                    raidRequest.AsSpan(16,4).Clear();
                    for (int repeat = 0; repeat < 2; repeat++)
                    {
                        peer.Send(16,raidRequest);
                        peer.Expect(1,16,[0,9,0,0]);
                        peer.Barrier();
                    }
                    peer.Send(37,new byte[8]);
                    peer.Barrier();
                    That(EntryState() == originalEntry,
                        $"团本{raidDungeon}不能从普通选图或重放加载建立实例、扣材料疲劳或写奖励回执");
                }
            }
            byte[] request = Selection(0);
            BinaryPrimitives.WriteUInt32LittleEndian(request,(uint)sample.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16),questId);
            string before = fixture.Snapshot();
            peer.Send(16,request); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
            That(fixture.Snapshot() == before,"非指定难度拒绝不扣票、疲劳或预留实例");
            request[4] = 2;
            peer.Send(16,request);
            if (sample.Tired || !sample.Ticket)
            {
                peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
                That(fixture.Snapshot() == before,"疲劳或普通门票不足时整次拒绝，已持有门票不被提前扣除");
                continue;
            }
            var info = peer.Expect(0,28).Body;
            var map = peer.Expect(0,29).Body;
            That(U32(info,0) == sample.Id && info[4] == 2 && U32(map,14) == (sample.Id switch
                { 9595 => 310353,5101 => 55916,_ => 400000000 }),"普通频道按28→29加载真实普通副本首图与指定难度");
            if (definition.OrdinaryEntryTicket is { } paidTicket)
            {
                peer.Expect(0,14);
                That(fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1",
                    peer.Id,paidTicket.ItemId) is [[long remainingTicket]] && remainingTicket == paidTicket.Count,
                    "持有两次所需材料，入场完整扣一次并持久化剩余数量");
            }
            if (sample.Id == 400000016)
            {
                // DungeonIncountInfo 限次：入场提交后按副本下发一条 NOTI 550（u32 副本 + u8 日剩余 + u8 周剩余），在加载应答前。
                var count = peer.Expect(0,550).Body;
                That(count.Length == 6 && U32(count,0) == 400000016,"超时空之战入场后同步本副本剩余进入次数");
            }
            Load(peer); peer.Expect(0,36); peer.Barrier();
            That(fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0",peer.Id) is [[long remaining]] &&
                remaining == fatigue-definition.StartFatigue &&
                fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) is [[1L]],
                "正式入场一次性扣原资源疲劳，加载不重复扣点或创建实例");
            string loaded = fixture.Snapshot();
            peer.Send(37,new byte[8]); peer.Barrier();
            That(fixture.Snapshot() == loaded,"重复加载不重复扣票、疲劳或修改实例身份");
            var maze = definition.Mazes.Single();
            var route = QuestStoryRouteTests.Path(maze.StaticTopology,maze.Starts.Single(),maze.Bosses.Single());
            foreach (var point in route)
            {
                for (int index = 0; index < map[18]; index++)
                {
                    int offset = 19 + index * 22;
                    if (map[offset + 13] > 3 || map[offset + 21] != 0) continue;
                    var report = Death(U16(map,offset + 6),peer.Id,map[offset + 12]);
                    peer.Send(39,report);
                    var packets = Drain();
                    foreach (var notice in packets.Where(packet => packet.Kind == 0 && packet.Opcode == 38))
                        PickupResourceDrops(fixture,peer,notice.Body,questId);
                    var state = fixture.Snapshot();
                    peer.Send(39,report);
                    Drain();
                    That(fixture.Snapshot() == state,"普通副本TCP重复死亡不增加任务进度、地面资产或经验");
                }
                if (point == maze.Bosses.Single()) break;
                var next = route[Array.IndexOf(route,point) + 1];
                byte[] move = new byte[100]; move[0] = (byte)next.X; move[1] = (byte)next.Y;
                peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                Load(peer); Drain();
            }
            if (sample.Id == 9595) ReviveAfterClear(1);
            peer.Send(46,DungeonSettlementProtocolTests.Report(peer.Id,99));
            That(Drain().Any(packet => packet.Kind == 0 && packet.Opcode == 35),"普通副本完整TCP通关返回奖励牌面");
            peer.Send(69); peer.Expect(1,69,[1]);
            peer.Send(70); peer.Expect(1,70);
            peer.Send(71,[0,0]); peer.Expect(1,71); Drain();
            var claimed = fixture.Snapshot();
            peer.Send(71,[0,0]); peer.Expect(1,71); Drain();
            That(fixture.Snapshot() == claimed,"普通副本TCP重复翻牌不增发奖励");
            if (sample.Id == 9595)
            {
                string settled = fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) +
                    fixture.Snapshot("SELECT * FROM character_dungeon_card_rewards WHERE character_id=$p0",peer.Id);
                ReviveAfterClear(0);
                That(settled == fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) +
                    fixture.Snapshot("SELECT * FROM character_dungeon_card_rewards WHERE character_id=$p0",peer.Id),
                    "结算菜单死亡复活不重开通关回执、不改牌面或领取状态");
            }
            That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,questId)
                is [[long scalar]] && scalar == (sample.Id == 400000016 ? 4 : 1),"真实通关减少一次计数，跨图收集任务保留未齐状态");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3); peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
            That(fixture.Query("SELECT town,area FROM characters WHERE id=$p0",peer.Id) is [[long town,long zone]] &&
                town == area.Town && zone == area.Area,"放弃普通副本返回实际入口地区");

            void ReviveAfterClear(int remaining)
            {
                byte[] key = [(byte)peer.Id,(byte)(peer.Id >> 8)];
                peer.Send(40,[0x74,0,4,1,0,0]);
                peer.Expect(0,32,[key[0],key[1],0,0]);
                peer.Send(41,key);
                peer.Expect(1,41,[1,key[0],key[1],0]);
                peer.Expect(0,32,[key[0],key[1],1,0]);
                peer.Expect(0,14,CoinBalance(remaining));
                Drain();
                string saved = fixture.Snapshot();
                peer.Send(41,key); peer.Barrier();
                That(fixture.Snapshot() == saved && fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0",peer.Id)
                    is [[long coins]] && coins == remaining,"770安徒恩清图后复活仍扣一币，重复请求不扣币");
            }

            List<WirePacket> Drain()
            {
                var packets = new List<WirePacket>();
                peer.SendBarrier();
                for (int count = 0; count < 200; count++)
                {
                    var packet = peer.Read();
                    if (packet is { Kind:1,Opcode:1347 })
                    {
                        That(packet.Body.SequenceEqual(new byte[] { 1 }),"专项网络屏障完整返回");
                        return packets;
                    }
                    if (packet is { Kind:0,Opcode:1760 })
                    {
                        peer.ReadCardMembershipState(packet);
                        continue;
                    }
                    if (packet is { Kind:0,Opcode:988 })
                    {
                        That(packet.Body.Length == 8 && U32(packet.Body,4) == 0 &&
                            U32(packet.Body,0) == Convert.ToInt64(fixture.Query(
                                "SELECT rental_points FROM account_wallets WHERE account=$p0",account).Single()[0]),
                            "结算幸运星通知对应实际账号余额");
                        continue;
                    }
                    That(packet.Kind == 0 && packet.Opcode is 3 or 4 or 13 or 14 or 21 or 34 or 35 or 36 or 37 or 38 or 31 or 115 or 356 or 574 or 1379,
                        $"专项链只接受对应业务通知：{packet.Kind}/{packet.Opcode}");
                    packets.Add(packet);
                }
                throw new InvalidDataException("专项网络通知超过有界数量");
            }
        }
        await fixture.StopAsync();
    }
}
