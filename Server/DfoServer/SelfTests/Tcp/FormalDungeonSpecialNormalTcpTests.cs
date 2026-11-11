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
        BossRushTests.Run(resources.Archive);
        SpecialNormalDungeonRewardTests.Run(resources);
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"special-normal-entry"),gm:true,resources:resources);
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort,"infinite-fatigue off",out _),"普通副本回归关闭无限疲劳");
        MooncreekDungeonTcpTests.Verify(fixture,resources);
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort,"support-companions wangfugui,guangbing",out _),
            "灵魂之冢回归先开启两类支援兵，避免默认关闭掩盖遗漏");
        try { VerifyBossRushEntry(fixture,resources); }
        finally
        {
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort,"support-companions off",out _),
                "灵魂之冢回归结束恢复支援兵关闭");
        }
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var growth = SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
        int sequence = 0;
        int preyDay = DfoServer.Game.Quests.QuestCycle.Day(DateTimeOffset.UtcNow);
        int preyBranch = PreyNormalCatalog.BranchForDay(preyDay);
        var preyRoute = dungeons.PreyNormal.Route(preyBranch);
        foreach (var sample in new[] { (Id:9595,Tired:false,Ticket:false),(Id:9595,Tired:false,Ticket:true),(Id:5101,Tired:true,Ticket:true),
            (Id:5101,Tired:false,Ticket:true),(Id:400000016,Tired:false,Ticket:false),
            (Id:400000016,Tired:true,Ticket:true),(Id:400000016,Tired:false,Ticket:true) }.Select(row => (row.Id,row.Tired,row.Ticket,Explicit:true,RetryWithoutTicket:false))
            .Concat(Enumerable.Range(5102,5).Select(id => (Id:id,Tired:false,Ticket:true,Explicit:true,RetryWithoutTicket:false)))
            .Concat(Enumerable.Range(5101,6).Select(id => (Id:id,Tired:false,Ticket:true,Explicit:false,RetryWithoutTicket:false)))
            .Concat([(Id:9595,Tired:false,Ticket:true,Explicit:true,RetryWithoutTicket:true)])
            .Concat(new[] {9596,9597,9599,9600,9601,9602,9603,9604,9605,9606,9607}
                .Select(id => (Id:id,Tired:false,Ticket:true,Explicit:false,RetryWithoutTicket:false)))
            .Concat(preyRoute.Select(id => (Id:id,Tired:false,Ticket:true,Explicit:true,RetryWithoutTicket:false))))
        {
            var definition = dungeons.Get(sample.Id);
            bool luke = definition.IsOrdinaryLuke;
            bool prey = definition.IsOrdinaryPrey;
            bool anton = definition.IsOrdinaryAnton;
            int antonBranch = sample.Id is >= 9599 and <= 9602 ? sample.Id - 9598 : sample.Id is >= 9603 and <= 9606 ? sample.Id - 9602 : 1;
            int antonIndex = anton ? dungeons.AntonNormalSequence.Routes[antonBranch - 1].IndexOf(sample.Id) : 0;
            var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == sample.Id));
            var done = new HashSet<int>(area.Quests);
            done.UnionWith([3778,4872,8682]);
            if (prey) done.Add(8925);
            if (sample.Id == 400000016) done.Add(8672); // 超时空频道准入任务，不能仅预置最后防线坐标绕过真实出门条件。
            if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
            var options = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.Where(option => option.DungeonId == sample.Id).ToArray();
            if (!options.Any(option => option.QuestId == -1)) done.Add(options.First(option => !option.InProgress).QuestId);
            done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
            var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town,area.Area,(ushort)(walk.X+walk.Width/2),(ushort)(walk.Y+walk.Height/2));
            // 最后防线独立账号不继承普通频道历史，避免把副本准入用例变成跨类型换频道测试。
            string account = sample.Id == 400000016 ? "entry608fiendwar" : prey ? "entryprey"+sequence : luke ? "entryluke"+sequence :
                anton && sample.Id != 9595 ? "entryanton"+sequence : fixture.Options.DevelopmentAccount;
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
                    created = InventoryService.Commit(repository,owner,created,inventory.Grant(created,
                        [new(ticket.ItemId,ticket.Count * (sample.RetryWithoutTicket ? 1 : 2))]));
                }
                if (sample.Id == 9595 && sample.Ticket || luke && sample.Explicit && !sample.Tired)
                {
                    var items = resources.ItemResources;
                    var inventory = new InventoryService(items.Items,items.GoldLimits,items.EpicPieces);
                    created = InventoryService.Commit(repository,owner,created,inventory.Grant(created,[new(1,luke ? 14 : 2)]));
                }
            }
            // 此组独立验证各图wire/奖励，显式已有前序；完整顺序由共享资源Session链验证。
            if (luke)
                fixture.Sql("INSERT INTO character_luke_normal_progress(character_id,account,day,progress) VALUES($p0,$p1,$p2,$p3)",
                    created.Id, account, DfoServer.Game.Quests.QuestCycle.Day(DateTimeOffset.UtcNow), sample.Id - 5101);
            // 本夹具逐图测试奖励及协议，明确建立前序；零进度自然六图链另有Session回归。
            if (anton)
                fixture.Sql("INSERT INTO character_anton_normal_progress(character_id,account,day,branch,progress) VALUES($p0,$p1,$p2,$p3,$p4)",
                    created.Id, account, DfoServer.Game.Quests.QuestCycle.Day(DateTimeOffset.UtcNow), antonBranch, antonIndex);
            if (prey)
                fixture.Sql("INSERT INTO character_prey_normal_progress(character_id,account,day,branch,progress) VALUES($p0,$p1,$p2,$p3,$p4)",
                    created.Id,account,preyDay,preyBranch,preyRoute.IndexOf(sample.Id));
            int fatigue = sample.Tired ? definition.StartFatigue!.Value-1 : luke && !sample.Explicit ? 8 : 100;
            // 疲劳通知保持已用+剩余=上限，夹具改余额时同步回填已用量。
            fixture.Sql("UPDATE character_fatigue SET used=maximum-$p0,remaining=$p0 WHERE character_id=$p1",fatigue,created.Id);
            // 普通超时空115沿普通频道；39/type45已归漩涡57，不能借团队落点验证普通副本。
            using var peer = Open(fixture,account);
            peer.Select(created.Slot,prepared:done.Count != 0,resumeTown:true,room:false);
            ushort questId = prey ? (ushort)8926 : anton ? (ushort)2683 : luke ? (ushort)2902 : (ushort)8683;
            peer.Send(31,[(byte)31,0,(byte)questId,(byte)(questId >> 8)]);
            var accepted = peer.Expect(1,31).Body;
            That(accepted[0] == 1 && U16(accepted,1) == questId, "普通材料或累计通关任务经真实31接取");
            string questAcceptance = fixture.Snapshot("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,questId);
            Drain();
            SelectMap(peer, incompleteHellQuest: HasIncompleteHellQuest(resources, created));
            if (luke)
            {
                peer.Send(871, [42,0,0,0]);
                byte[] sequenceBody = peer.Expect(0,602).Body;
                That(sequenceBody.Length == 16 && U32(sequenceBody) == 42 && sequenceBody[4] == sample.Id - 5101 &&
                    U32(sequenceBody,5) == (1u << (sample.Id - 5101)) - 1 && sequenceBody.AsSpan(9).SequenceEqual(new byte[7]),
                    "普通卢克各图查询602复述真实当日前置，机械王座返回阶段5/低五位");
                peer.Barrier();
            }
            if (prey)
            {
                peer.Send(871,[203,0,0,0]);
                byte[] body = peer.Expect(0,602).Body;
                int completed = preyRoute.IndexOf(sample.Id);
                That(body.Length == 16 && U32(body) == 203 && body[4] == completed && U32(body,5) == (1u << completed)-1 &&
                    U32(body,9) == preyBranch && body.AsSpan(13).SequenceEqual(new byte[3]),"203查询返回当前真实分支与前序");
                peer.Barrier();
            }
            if (anton)
            {
                peer.Send(871,[28,0,0,0]);
                var body = peer.Expect(0,602).Body;
                That(body.Length == 16 && U32(body) == 28 && body[4] == antonIndex && U32(body,5) == (1u << antonIndex)-1 &&
                    U32(body,9) == antonBranch && body[13] == 1 && body.AsSpan(14).SequenceEqual(new byte[2]),
                    "序列28查询反映实际当日前序及分支，未完成讨伐保留卡片资格");
                peer.Barrier();
            }
            if (sample.Id == 9595 && sample.Ticket)
            {
                // tutorial表中的正式团本索引不能借普通选图进入；拒绝后仍须能进入原普通副本。
                string EntryState() => string.Join("\n", new[] { "characters", "character_inventory_items", "character_fatigue",
                    "character_dungeon_state", "character_dungeon_growth_receipts", "character_dungeon_loot_receipts",
                    "character_dungeon_card_rewards", "character_dungeon_settlement_receipts", "character_active_main_quests"
                    }.Select(table => fixture.Snapshot($"SELECT * FROM {table} ORDER BY 1,2")));
                string originalEntry = EntryState();
                foreach (uint raidDungeon in new uint[] { 210,211,212,213,214,215,216,218,219,220,221,222,223,224 })
                {
                    byte[] raidRequest = Selection(2);
                    BinaryPrimitives.WriteUInt32LittleEndian(raidRequest,raidDungeon);
                    // 练习分支同样不能把备用索引当生产目录；offset8不是tutorial表切换授权。
                    foreach (var entry in new[] { (Mode:(byte)0,Quest:0u), (Mode:(byte)1,Quest:0u), (Mode:(byte)1,Quest:(uint)questId) })
                    {
                        raidRequest[4] = entry.Quest == 0 ? (byte)2 : (byte)0;
                        raidRequest[8] = entry.Mode;
                        BinaryPrimitives.WriteUInt32LittleEndian(raidRequest.AsSpan(16),entry.Quest);
                        for (int repeat = 0; repeat < 2; repeat++)
                        {
                            peer.Send(16,raidRequest);
                            peer.Expect(1,16,[0,9,0,0]);
                            peer.Barrier();
                        }
                        peer.Send(37,new byte[8]);
                        peer.Barrier();
                        That(EntryState() == originalEntry,
                            $"团本{raidDungeon}模式{entry.Mode}任务{entry.Quest}不能从选图或重放加载建立实例、扣材料疲劳、改任务或写奖励回执");
                    }
                }
            }
            byte[] request = Selection(0);
            BinaryPrimitives.WriteUInt32LittleEndian(request,(uint)sample.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16),sample.Explicit && (!prey || sample.Id == PreyNormalCatalog.FinalDungeon) ? questId : 0u);
            string before = fixture.Snapshot();
            foreach (byte wrong in luke ? new byte[] { 0,1,3,4 } : new byte[] { 0 })
            {
                request[4] = wrong;
                peer.Send(16,request); peer.Expect(1,16,[0,sample.Ticket ? (byte)9 : (byte)17,0,0]); peer.Barrier();
                That(fixture.Snapshot() == before,"先检查缺票、再检查指定难度；拒绝不扣票、疲劳或预留实例");
            }
            if (luke)
            {
                request[4] = 2;
                request[8] = 1;
                peer.Send(16,request); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
                That(fixture.Snapshot() == before,"卢克禁止练习，不能借免费模式绕过费用或任务身份");
                request[8] = 0;
            }
            request[4] = 2;
            if (luke && sample.Id > 5101)
            {
                fixture.Sql("UPDATE character_luke_normal_progress SET progress=0 WHERE character_id=$p0", created.Id);
                string locked = fixture.Snapshot();
                peer.Send(16,request); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
                That(fixture.Snapshot() == locked, "真实TCP未完成前序拒绝，无扣费/疲劳/尝试号变化");
                fixture.Sql("UPDATE character_luke_normal_progress SET progress=$p0 WHERE character_id=$p1", sample.Id - 5101, created.Id);
            }
            peer.Send(16,request);
            if (sample.Tired || !sample.Ticket)
            {
                peer.Expect(1,16,[0,sample.Tired ? (byte)22 : (byte)17,0,0]); peer.Barrier();
                That(fixture.Snapshot() == before,"疲劳或普通门票不足时整次拒绝，已持有门票不被提前扣除");
                continue;
            }
            var info = peer.Expect(0,28).Body;
            if (luke)
            {
                byte[] sequenceBody = peer.Expect(0,602).Body;
                That(sequenceBody[4] == sample.Id - 5101 && U32(sequenceBody,5) == (1u << (sample.Id - 5101)) - 1,
                    "单人28之后、29之前刷新当前角色进度，不依赖选图缓存");
            }
            if (prey)
            {
                byte[] body = peer.Expect(0,602).Body;
                That(U32(body) == 203 && body[4] == preyRoute.IndexOf(sample.Id) && U32(body,9) == preyBranch,
                    "普通普雷28与29之间投影当前路线");
            }
            if (anton)
            {
                var body = peer.Expect(0,602).Body;
                That(body.Length == 16 && U32(body) == 28 && body[4] == antonIndex && U32(body,9) == antonBranch,
                    "安徒恩28之后29之前同步个人门禁进度");
            }
            var map = peer.Expect(0,29).Body;
            That(U32(info,0) == sample.Id && info[4] == 2 && U32(map,14) == (sample.Id switch
                { 9595 => 310353,5101 => 55916,5102 => 55924,5103 => 55935,5104 => 55940,5105 => 55950,5106 => 55954,_ => prey || anton ? (uint)definition.Mazes.Single().MapSpecification!.Single(m => m.Position == definition.Mazes.Single().Starts.Single()).Id : 400000000 }),"普通频道按28→29加载真实普通副本首图与指定难度");
            if (definition.OrdinaryEntryTicket is { } paidTicket)
            {
                peer.Expect(0,14);
                That(fixture.Query("SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1",
                    peer.Id,paidTicket.ItemId) is [[long remainingTicket]] && remainingTicket == (sample.RetryWithoutTicket ? 0 : paidTicket.Count),
                    "入场完整扣除一次材料并持久化剩余数量");
            }
            if (sample.Id == 400000016)
            {
                // DungeonIncountInfo 限次：入场提交后按副本下发一条 NOTI 550（u32 副本 + u8 日剩余 + u8 周剩余），在加载应答前。
                var count = peer.Expect(0,550).Body;
                That(count.Length == 6 && U32(count,0) == 400000016,"超时空之战入场后同步本副本剩余进入次数");
            }
            Load(peer, fatigue: false); peer.Expect(0,36); peer.Barrier();
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
                    byte[]? expectedCondition = sample.Explicit && point == maze.Bosses.Single()
                        ? (sample.Id,U32(map,14),U32(map,offset+8)) switch
                        {
                            (400000016,400000015,110994) => [1,235,33,4,0,0,0],
                            (400000046,400000102,109007083) => [1,222,34,0,0,0,0],
                            _ => null
                        } : null;
                    peer.Send(39,report);
                    var packets = Drain(expectedCondition);
                    foreach (var notice in packets.Where(packet => packet.Kind == 0 && packet.Opcode == 38))
                        PickupResourceDrops(fixture,peer,notice.Body,sample.Explicit && (!prey || sample.Id == PreyNormalCatalog.FinalDungeon) ? questId : 0u,resources);
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
            if (luke && sample.Explicit)
            {
                for (int coinsLeft = 13; coinsLeft >= 2; coinsLeft--) ReviveAfterClear(coinsLeft);
                byte[] key = [(byte)peer.Id,(byte)(peer.Id >> 8)];
                peer.Send(40,[0x74,0,4,1,0,0]);
                peer.Expect(0,32,[key[0],key[1],0,0]);
                string beforeRejected = fixture.Snapshot();
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    peer.Send(41,key);
                    peer.Expect(1,41,[0,1,0]);
                    peer.Barrier();
                    That(fixture.Snapshot() == beforeRejected && fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0",peer.Id)
                        is [[2L]],"六白图TCP第十三次复活及重放均拒绝，无扣币或部分写入");
                }
            }
            That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,questId)
                is [[long scalar]] && scalar == (sample.Id == 400000016 ? 4 : prey && sample.Id == PreyNormalCatalog.FinalDungeon ? 0 : 1),
                "真实终点才减少目标图任务计数，跨图收集任务保留未齐状态");
            if (sample.RetryWithoutTicket)
            {
                string attempts = fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", peer.Id);
                string fatigueAfterClear = fixture.Snapshot("SELECT * FROM character_fatigue WHERE character_id=$p0", peer.Id);
                string assets = fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id);
                peer.Send(72, [1, 0]);
                var returned = PartyDungeonTcpTests.Drain(peer);
                int ack = Array.FindIndex(returned, packet => packet is { Kind: 1, Opcode: 72, Body: [1, 1, 2] });
                int state = Array.FindIndex(returned, packet => packet is { Kind: 0, Opcode: 3 });
                int townNotice = Array.FindIndex(returned, packet => packet is { Kind: 0, Opcode: 24 });
                That(ack >= 0 && state > ack && townNotice > state && returned.All(packet => packet.Opcode is not (28 or 29)) &&
                    fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", peer.Id) == attempts &&
                    fixture.Snapshot("SELECT * FROM character_fatigue WHERE character_id=$p0", peer.Id) == fatigueAfterClear &&
                    fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id) == assets,
                    "普通副本重试缺入场材料时按ACK72、状态3、24回城，不再扣费或创建实例");
                That(fixture.Events().Any(row => row.GetProperty("Kind").GetString() == "formal_dungeon_return" &&
                    row.GetProperty("Detail").TryGetProperty("Reason", out var reason) && reason.GetString() == "ordinary-retry-rejected" &&
                    row.GetProperty("Detail").TryGetProperty("RejectReason", out var rejection) &&
                    rejection.GetString()?.StartsWith("dungeon-required-item:", StringComparison.Ordinal) == true),
                    "失败原因确为入场材料耗尽，不由其它资格拒绝替代");
                continue;
            }
            if (prey || luke || anton)
            {
                uint? expectedNext = prey ? preyRoute.IndexOf(sample.Id) < 4 ? (uint)preyRoute[preyRoute.IndexOf(sample.Id)+1] : null :
                    luke ? sample.Id < 5106 ? (uint)sample.Id + 1 : null : sample.Id switch
                    {9595 => 9596u,9596 => 9597u,9597 => 9599u, >= 9599 and <= 9602 => (uint)sample.Id+4,
                        >= 9603 and <= 9606 => 9607u, _ => null};
                peer.Send(72,[1,0]);
                var continued = PartyDungeonTcpTests.Drain(peer).ToList();
                if (luke && !sample.Explicit && expectedNext is not null)
                {
                    That(continued.Any(packet => packet is {Kind:1,Opcode:72,Body:[1,1,2]}) &&
                        continued.Any(packet => packet is {Kind:0,Opcode:24}) &&
                        continued.All(packet => packet.Opcode is not (28 or 29)) &&
                        fixture.Events().Any(row => row.GetProperty("Kind").GetString() == "formal_dungeon_return" &&
                            row.GetProperty("Detail").TryGetProperty("RejectReason",out var reason) && reason.GetString() == "fatigue-exhausted"),
                        "单次八疲劳已耗尽时拒绝下一图并回城，不能免费续图");
                }
                else if (expectedNext is { } nextDungeon)
                {
                    int ack = continued.FindIndex(packet => packet is { Kind:1,Opcode:72,Body:[1,1,0] });
                    int transition = continued.FindIndex(packet => packet is { Kind:0,Opcode:27 });
                    int dungeonInfo = continued.FindIndex(packet => packet is { Kind:0,Opcode:28 });
                    int startMap = continued.FindIndex(packet => packet is { Kind:0,Opcode:29 });
                    That(ack >= 0 && transition > ack && dungeonInfo > transition && startMap > dungeonInfo &&
                        U32(continued[dungeonInfo].Body) == nextDungeon && continued.Count(packet => packet is {Kind:0,Opcode:29}) == 1,
                        "真实F10按72、27、28、29进入顺序下一图，未重复当前图");
                    string entered = fixture.Snapshot();
                    peer.Send(72,[1,0]); PartyDungeonTcpTests.Drain(peer);
                    That(fixture.Snapshot() == entered,"加载中重复旧结算续图不跳图、不重复扣费或预留实例");
                    peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3); peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
                }
                else
                {
                    That(continued.Any(packet => packet is { Kind:1,Opcode:72,Body:[1,1,2] }) &&
                        continued.Any(packet => packet is {Kind:0,Opcode:24}) &&
                        continued.All(packet => packet.Opcode is not (28 or 29)),"普通序列终点F10回城，不重进终图或循环到首图");
                }
            }
            else
            {
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3); peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
            }
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
                    is [[long coins]] && coins == remaining,"清图后合法复活仍扣一币，重复请求不扣币");
            }

            List<WirePacket> Drain(byte[]? expectedCondition = null)
            {
                var packets = new List<WirePacket>();
                bool conditionSeen = false;
                peer.SendBarrier();
                for (int count = 0; count < 200; count++)
                {
                    var packet = peer.Read();
                    if (packet is { Kind:1,Opcode:1347 })
                    {
                        That(packet.Body.SequenceEqual(new byte[] { 1 }),"专项网络屏障完整返回");
                        That(expectedCondition is null || conditionSeen,"本次明确终点死亡在屏障前发布一次任务条件");
                        return packets;
                    }
                    if (packet is { Kind:1,Opcode:33 })
                    {
                        That(expectedCondition is not null && !conditionSeen &&
                            packets.Any(row => row is { Kind:0,Opcode:38 }) &&
                            packets.All(row => row.Kind != 0 || row.Opcode is not (31 or 115)),
                            "只接受本次明确终点死亡38后、通关115/31前唯一条件33，不吞其它任务或重复ACK");
                        Bytes(packet.Body,expectedCondition!,"普通超时空8683/scalar4或普雷终点8926/scalar0完整条件正文");
                        That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,questId)
                                is [[long state]] && state == U32(expectedCondition!,3) &&
                            fixture.Snapshot("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,questId) == questAcceptance,
                            "条件33对应原任务接取身份和真实提交状态，不用通知制造进度");
                        conditionSeen = true;
                        packets.Add(packet);
                        continue;
                    }
                    if (packet is { Kind:0,Opcode:1760 })
                    {
                        peer.ReadCardMembershipState(packet);
                        continue;
                    }
                    if (packet is { Kind:0,Opcode:1349 })
                    {
                        AssertAdventureProgress(fixture, resources, peer, packet);
                        packets.Add(packet);
                        continue;
                    }
                    if (packet is { Kind:1,Opcode:1002 })
                    {
                        That(packet.Body.Length == 13 && packet.Body[0] == 1 && U64(packet.Body,1) == 1 &&
                            U32(packet.Body,9) == Convert.ToInt64(fixture.Query(
                                "SELECT rental_points FROM account_wallets WHERE account=$p0",account).Single()[0]),
                            "结算幸运星获得/上限应答对应实际账号余额");
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
                    if (packet is { Kind:0,Opcode:602 })
                    {
                        if (anton)
                        {
                            var antonProgress = fixture.Query("SELECT progress,branch FROM character_anton_normal_progress WHERE character_id=$p0",peer.Id);
                            That(antonProgress is [[long antonCompleted, long savedBranch]] && packet.Body.Length == 16 && U32(packet.Body) == 28 &&
                                packet.Body[4] == antonCompleted && U32(packet.Body,5) == (1u << (int)antonCompleted)-1 && U32(packet.Body,9) == savedBranch &&
                                packet.Body[13] == (antonCompleted < 6 ? 1 : 0) && packet.Body.AsSpan(14).SequenceEqual(new byte[2]),
                                "安徒恩每次602与本角色已提交进度及剩余讨伐资格完全一致");
                            packets.Add(packet); continue;
                        }
                        if (prey)
                        {
                            var preyProgress = fixture.Query("SELECT progress FROM character_prey_normal_progress WHERE character_id=$p0",peer.Id);
                            That(preyProgress is [[long preyCompleted]] && packet.Body.Length == 16 && U32(packet.Body) == 203 &&
                                packet.Body[4] == preyCompleted && U32(packet.Body,5) == (1u << (int)preyCompleted)-1 && U32(packet.Body,9) == preyBranch,
                                "结算203刷新对应实际持久化进度");
                            packets.Add(packet); continue;
                        }
                        var saved = fixture.Query("SELECT day,progress FROM character_luke_normal_progress WHERE character_id=$p0", peer.Id);
                        int completed = saved is [[long savedDay, long progress]] && savedDay == DfoServer.Game.Quests.QuestCycle.Day(DateTimeOffset.UtcNow)
                            ? checked((int)progress) : 0;
                        That(luke && packet.Body.Length == 16 && U32(packet.Body) == 42 && packet.Body[4] == completed &&
                            U32(packet.Body,5) == (1u << completed) - 1 && packet.Body.AsSpan(9).SequenceEqual(new byte[7]),
                            "结算602逐次对应本角色真实当日提交进度，拒绝其它图或伪造前置");
                        packets.Add(packet);
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
        var soulEntries = fixture.Events().Where(row => row.GetProperty("Kind").GetString() == "formal_dungeon_entry")
            .Select(row => row.GetProperty("Detail")).Where(row => row.GetProperty("Request").GetProperty("DungeonIndex").GetUInt32() == 337).ToArray();
        That(soulEntries.Length == 1 && soulEntries[0].GetProperty("Accepted").GetBoolean() &&
            soulEntries[0].GetProperty("Identity").ValueKind != System.Text.Json.JsonValueKind.Null,
            "337经真实选图进入正式实例并记录入场身份");
    }

    static void VerifyBossRushEntry(TcpTestFixture fixture,TcpTestResources resources)
    {
        var catalog = SelfTestCatalogs.Dungeons(resources.Archive);
        var bosses = catalog.BossRush.Bosses;
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 337));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).ToHashSet();
        var option = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.First(row => row.DungeonId == 337);
        if (option.QuestId > 0 && !option.InProgress) done.Add(option.QuestId);
        done.UnionWith(area.Map(done).DungeonGates.Where(gate => gate.Conditions.Length == 0).SelectMany(gate => gate.Quests));
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        using var peer = Open(fixture);
        peer.Create("Soul337");
        fixture.Sql("UPDATE characters SET level=95,experience=$p0,town=$p1,area=$p2,x=$p3,y=$p4 WHERE id=$p5",
            SelfTestCatalogs.Growth(resources.Archive).GetLevelStartExperience(95),area.Town,area.Area,
            bounds.X+bounds.Width/2,bounds.Y+bounds.Height/2,peer.Id);
        foreach (int quest in done)
            fixture.Sql("INSERT OR IGNORE INTO character_completed_quests(character_id,quest_id) VALUES($p0,$p1)",peer.Id,quest);
        peer.Select(prepared:true,resumeTown:true,room:false);
        SelectMap(peer,griefLayer:1);
        byte[] request = Selection();
        BinaryPrimitives.WriteUInt32LittleEndian(request,337);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16),0);
        peer.Send(16,request);
        peer.Expect(0,28);
        var startMap = peer.Expect(0,29).Body;
        That(startMap.Length == 23 && startMap[18] == 0,
            "灵魂之冢即使开启王富贵和光兵，起点真实29仍无额外AI演员");
        byte[] info = peer.Expect(0,1488).Body;
        byte[] data = peer.Expect(0,1487).Body;
        That(info.Length == 4 + 9 * 25 && BinaryPrimitives.ReadInt32LittleEndian(info) == 25 &&
            info.AsSpan(4 + 12 * 9, 9).ToArray().All(value => value == 0),
            "1488 包含25格并保留空起点，避免后半名单坐标错位");
        That(data.Length == 9 && data[0] == 0 && BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(1)) == 0 &&
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(5)) is >= 100000 and <= 120000,
            "1487 入场 rank/score 为 0，剩余毫秒取自 120 秒时限");
        // 屏障证明入场未提前发1486，不能用29已发送冒充客户端地图已安装。
        peer.Barrier();
        peer.Send(37,new byte[8]);
        peer.Expect(1,37,[1]);
        peer.Expect(0,30,new byte[5]);
        byte[] timeout = peer.Expect(0,1486).Body;
        That(timeout.Length == 8 && U32(timeout,0) is >= 100 and <= 120 && U32(timeout,4) == 1 &&
            U32(timeout,0) * 1000 <= U32(data,5) + 999,
            "1486在首次真实加载完成后启动，仍取同实例权威剩余秒数，不补回加载耗时");
        peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),1]);
        peer.ExpectOptionalFatigue();
        peer.Barrier();
        peer.Send(37,new byte[8]);
        peer.Barrier();
        int score = 0;
        var seen = new HashSet<int>();
        foreach (var point in new[] { (X:1,Y:2), (X:0,Y:2) })
        {
            byte[] move = new byte[100];
            move[0] = (byte)point.X;
            move[1] = (byte)point.Y;
            peer.Send(45, move);
            peer.Expect(1, 45, [1]);
            byte[] battleMap = peer.Expect(0, 29).Body;
            That(battleMap.Length >= 41 && battleMap[18] == 1 &&
                bosses.Contains(checked((int)U32(battleMap, 27))),
                $"337战斗房必须生成专表内可计分怪物，实际模板={U32(battleMap, 27)}");
            int id = checked((int)U32(battleMap,27));
            int gridOffset = 4 + (point.Y * 5 + point.X) * 9;
            That(U32(info,gridOffset) == id && info[gridOffset+4] <= 2 &&
                catalog.BossRush.SoulPoints[info[gridOffset+4]] == catalog.BossRush.SoulFor(bosses.IndexOf(id)),
                "真实生成怪物与原生按房间坐标查表的ID和灵魂类别一致");
            That(seen.Add(id),"不同战斗房不重复分配同一计分怪");
            Load(peer);
            // 普通Load严格期待30后直接3；过房不得再次插入1486重启原生计时。
            QuestLifecycleTcpTests.DrainNotifications(peer);
            var report = Death(U16(battleMap,25),peer.Id,battleMap[31]);
            peer.Send(39,report);
            var notices = new List<WirePacket>();
            QuestLifecycleTcpTests.DrainNotifications(peer,notices.Add);
            score += catalog.BossRush.SoulFor(bosses.IndexOf(id));
            var hud = notices.Single(packet => packet.Opcode == 1487).Body;
            That(hud.Length == 9 && hud[0] == catalog.BossRush.RankOf(score) &&
                U32(hud,1) == score,"真实击杀刷新1487分数与对应段位");
            peer.Send(39,report);
            notices.Clear();
            QuestLifecycleTcpTests.DrainNotifications(peer,notices.Add);
            That(notices.All(packet => packet.Opcode != 1487),"重复死亡报告不再次计分或刷新段位");
        }
        That(score >= 20 && catalog.BossRush.RankOf(score) > 0,"沿自然房间击杀能够跨过首档段位阈值");
        peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3); peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
    }
}
