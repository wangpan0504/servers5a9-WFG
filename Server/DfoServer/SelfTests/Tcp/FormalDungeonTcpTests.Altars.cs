using System.Buffers.Binary;
using System.Diagnostics;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using DfoServer.Network.Builders.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunAltarsAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        DungeonAltarRewardTests.Run(resources.Archive,resources.ItemResources);
        DungeonAltarSessionTests.Run(resources);
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var clock = new AltarTimers(20);
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"dungeon-altars"),resources:resources,clock:clock);
        await fixture.StartAsync();
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 11006));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(area.Default.Bounds.SelectMany(row=>row.Quests))
            .Concat(area.Default.DungeonGates.SelectMany(row=>row.Quests)).Distinct().ToArray();
        var bounds = area.Map(done.ToHashSet()).Bounds.First(row=>row.Conditions.Length==0 && row.Quests.All(done.Contains)).Bounds;
        var position = new DfoServer.GameWorld.Towns.Location(area.Town,area.Area,
            checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        var growth = SelfTestCatalogs.Growth(resources.Archive);
        DungeonSpecialEntryTests.Run(resources.Towns,resources.Characters,resources.Learning,growth,resources.Quests);
        foreach (uint id in new uint[] {11006,11007})
        {
            Action<WirePacket>? validateSpawn = null;
            var earlySpecial = new List<WirePacket>();
            int spawnedRound = -1, completedRounds = 0, trackerRoundCount = 0;
            uint nextRoomKey = 0;
            long trackerFirstDelay = 0;
            var runSpawnedKeys = new HashSet<ushort>();
            ushort questId = id == 11006 ? (ushort)5650 : (ushort)7340;
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{}))
            {
                var repo = new CharacterRepository(fixture.Database);
                var candidate = CharacterCreation.Prepare(id==11006?"Blood466":"Ultimate466",resources.Characters.Get(0),
                    position,position,resources.Learning.Get);
                created = repo.Create(owner,candidate with { Level=70,AcceptedStoryLevel=70,CompletedQuests=done.Append(id == 11006 ? 63 : 7339).Distinct().ToArray(),
                    InitialLearning=SkillPointLedger.Synchronize(0,0,70,candidate.InitialLearning with
                        { Experience=growth.GetLevelStartExperience(70) },resources.Learning) });
                created = repo.CommitInventory(owner,created,new(created.Inventory,created.Inventory with
                    { Gold=500000,ReviveCoins=3 },"altar_tcp_seed"));
            }
            using var peer = Open(fixture);
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            peer.Send(31, [31, 0, (byte)questId, (byte)(questId >> 8)]);
            That(peer.Expect(1, 31).Body[0] == 1, "生产31合法接取祭坛关联支线");
            RefreshedQuests(peer);
            // 新角色悲叹为第1层；塔类入口严格核对1536→1303→两包550→27。
            SelectTowerMap(peer,griefLayer:1);
            var request = Selection();
            BinaryPrimitives.WriteUInt32LittleEndian(request,id);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16),questId);
            if (id == 11007) BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(21),uint.MaxValue);
            peer.Send(16,request);
            var info = peer.Expect(0,212).Body;
            var map = peer.Expect(0,211).Body;
            int fee = id==11006?22000:0;
            if (fee>0)
                That(peer.Expect(0,14).Body.SequenceEqual(InventoryPackets.Delta(created.Inventory with { Gold = checked((uint)(500000-fee)) },
                    InventoryListType.Main, [new(InventoryListType.Main, 0)])),
                    "祭坛入场14仅投影已提交绝对金币余额，后续屏障拒绝重复金币通知");
            That(info.Length==12 && U32(info,0)==id && map.Length==15 && map[7]==1 &&
                fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id) is [[long gold]] && gold==500000-fee,
                "实际CMD16按212→211接入祭坛，金币只扣本次资源费用");
            That(U32(map,8) == U32(map,2) && (id != 11007 || U32(map,8) == 17030),
                "实际211创建字段为资源地图编号，极限使用17030而非随机种子");
            peer.Barrier();
            Load(peer);
            // 未准备UI前无波次；伪造普通255对象和空报文均不能越过此门禁。
            peer.Send(255,[1,255,255]); peer.Barrier();
            peer.Send(258);
            peer.Expect(0,215);
            var next = peer.Read();
            while (next is {Kind:0,Opcode:213}) { earlySpecial.Add(next); next=peer.Read(); }
            That(next is {Kind:0,Opcode:210} && next.Body.Length==4+U16(next.Body,0)*17 && U16(next.Body,0)>0,
                "UI准备后实际计时发布210，正文数量和17字节对象记录相符");
            StartTracker(info,map,next);
            // 两祭坛实机均有完整39，不能仅用255合成报告证明击杀链路。
            int count=U16(next.Body,0);
            byte[] killed=new byte[1+count*2]; killed[0]=checked((byte)count);
            for(int at=0;at<count;at++) next.Body.AsSpan(3+at*17,2).CopyTo(killed.AsSpan(1+at*2));
            ushort key = U16(next.Body,3);
            byte[] normalDeath = Death(key,peer.Id,next.Body[10]);
            byte[] zeroDeath = AltarZeroParticipantDeath(key,next.Body[10]);
            byte[] corrupt = (byte[])normalDeath.Clone();
            corrupt[^1] ^= 1;
            byte[] corruptZero = (byte[])zeroDeath.Clone();
            corruptZero[^1] ^= 1;
            var invalidDeathSpawnKeys = new HashSet<ushort>();
            ValidateTimedSpawn(next);
            peer.Send(39,corrupt);
            peer.Send(39,corruptZero);
            peer.Send(39,Death(ushort.MaxValue,peer.Id,next.Body[10]));
            peer.Send(39,AltarZeroParticipantDeath(ushort.MaxValue,next.Body[10]));
            // 无效死亡不能推进回合；合法计时刷怪同时核对完整资源批次与对象键，不吞死亡或奖励通知。
            peer.Barrier(packet =>
            {
                That(packet is { Kind: 0, Opcode: 210 or 213 }, "无效39屏障不能出现死亡确认、回合推进或奖励通知");
                validateSpawn!(packet);
                if (packet.Opcode == 210) ValidateTimedSpawn(packet);
            });
            foreach (byte[] death in new[] { zeroDeath, normalDeath, zeroDeath })
            {
                peer.Send(39,death);
                Bytes(ReadUntil(38).Body,[(byte)key,(byte)(key>>8),0,0,0,0,255,0,0],
                    "祭坛74B零参与者死亡及88B交叉重放均确认同一对象，不重复推进或发奖");
            }
            // 255与39共用本Run死亡事实，混合重报不重复推进。
            peer.Send(255,killed);
            if(id == 11007)
            {
                // 等待真实自动选难度和下一回合刷怪，确定覆盖慢调度，不靠快速死亡躲过计时通知。
                while(spawnedRound < 1) ReadUntil(210);
                That(spawnedRound == 1,"极限同实例自动选择后已严格校验第二回合210");
                Console.WriteLine("PASS 极限297自动选择→215第二回合→210资源批次，复活前后保留同实例tracker");
            }
            peer.Send(40,[0x74,0,4,1,0,0]);
            ReadUntil(32);
            peer.Send(41,[(byte)peer.Id,(byte)(peer.Id>>8)]);
            ExpectActive(1,41,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            ExpectActive(0,32,[(byte)peer.Id,(byte)(peer.Id>>8),1,0]); ExpectActive(0,14,CoinBalance(2));
            peer.Send(40,[0x74,0,4,1,0,0]); ReadUntil(32);
            peer.Send(255,killed);
            var ranking=ReadUntil(208).Body;
            That(ranking.Length==24 && U32(ranking,16)==(id==11006?15:7) &&
                fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts WHERE character_id=$p0",peer.Id) is [[1L]] &&
                fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0",peer.Id) is [[2L]],
                "第二次死亡截止后仅提交一次专用失败奖励，第一轮死亡回调与复活不重复结算");
            string committedItems = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
            ulong committedExperience = Experience(fixture,peer.Id);
            long committedGold = Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id)[0][0]);
            var rewards=peer.Expect(0,209).Body;
            That(rewards.Length>=6 && rewards[1]==(id==11006?15:7) && rewards[^3..].All(value=>value==0),
                "失败209保留四队员槽与真实完成回合，不替换成普通翻牌");
            if (id == 11007)
            {
                peer.Send(72,[1,2]);
                var exp = peer.Expect(0,37).Body;
                That(exp.Length==91 && BinaryPrimitives.ReadUInt64LittleEndian(exp.AsSpan(1))==committedExperience,
                    "点击回城先完整发布已提交的祭坛经验");
                var reply = peer.Read();
                int itemFrames = 0;
                while (reply is { Kind:0,Opcode:14 })
                {
                    That(++itemFrames<=2 && reply.Body.Length>=3 && reply.Body[0]==0 &&
                        reply.Body.Length==3+U16(reply.Body,1)*123,
                        "回城ACK前的祭坛库存通知保留完整条目且数量有界");
                    reply=peer.Read();
                }
                That(reply is { Kind:1,Opcode:72 },"手动回城不等待60秒兜底，完整奖励投影之后才回复ACK72");
                Bytes(reply.Body,[1,1,2],"祭坛手动回城ACK保持原请求选项");
            }
            var state=ReadUntil(3);
            That(state.Body is [1,_,_,0],"祭坛专用演出结束恢复本人城镇state0");
            peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$p0",peer.Id) is [[1L]] &&
                fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts WHERE character_id=$p0",peer.Id) is [[1L]] &&
                fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")==committedItems &&
                Experience(fixture,peer.Id)==committedExperience &&
                Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id)[0][0])==committedGold,
                "一次准入和一次失败持久事实保持独立，计时不重复收费或发奖");

            // 同一角色剩余两次分别覆盖加载前和加载后放弃，不另起资源或服务夹具。
            foreach (bool loaded in new[] { false, true })
            {
                validateSpawn = null;
                earlySpecial.Clear();
                SelectTowerMap(peer,griefLayer:1);
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), loaded ? questId : 0u);
                peer.Send(16,request);
                var returningInfo = peer.Expect(0,212).Body;
                var returningMap = peer.Expect(0,211).Body;
                That(U32(returningMap,8) == U32(returningMap,2), "再入场仍使用真实地图编号");
                if (fee > 0) peer.Expect(0,14);
                peer.Barrier();
                int remainingBatches = 0;
                if (loaded)
                {
                    // 极限覆盖258先于37；完整加载收尾前仍不允许启动回合。
                    if (id == 11007) { peer.Send(258); peer.Barrier(); }
                    Load(peer);
                    byte[] move = new byte[100];
                    if (id == 11006)
                    {
                        var definition = dungeons.Get(11006);
                        var maze = dungeons.ResolveOrdinaryMaze(definition,
                            definition.Mazes.Single(row => row.SourceOrdinal == returningInfo[5]), _ => 0);
                        var origin = new DfoServer.GameWorld.Dungeons.DungeonPoint(returningMap[0], returningMap[1]);
                        var target = maze.StaticTopology.Links!.SelectMany(link =>
                            new[] { (From: link.First, To: link.Second), (From: link.Second, To: link.First) })
                            .Single(edge => edge.From == origin).To;
                        That(maze.MapSpecification is { Count: >= 2 } route && route[0].Position == origin &&
                            route[1].Position == target && route[1].Kind == "map","无尽前两房按实际路线规划顺序分配对象键");
                        move[0] = checked((byte)target.X);
                        move[1] = checked((byte)target.Y);
                        peer.Send(45, move);
                        peer.Barrier();
                    }
                    if (id == 11006) peer.Send(258);
                    peer.Expect(0,215);
                    var spawned = ReadUntil(210);
                    That(U16(spawned.Body,0) > 0, "已启动波次的祭坛仍可主动退出");
                    int batchCount = StartTracker(returningInfo,returningMap,spawned);
                    if (id == 11006)
                    {
                        // 逐批消费实际210；末批interval=0来自资源计划，不用固定怪物数或等待时间模拟清房。
                        int batches = 0;
                        while (true)
                        {
                            That(++batches <= ushort.MaxValue && spawned.Body.Length == 4 + U16(spawned.Body,0) * 17,
                                "无尽清首房的波次数量有界且每个对象记录完整");
                            int actors = U16(spawned.Body,0);
                            bool last = U16(spawned.Body,spawned.Body.Length - 2) == 0;
                            if (last)
                            {
                                That(actors == 1, "无尽首房末批为实际资源声明的单个尾怪");
                                ushort tail = U16(spawned.Body,3);
                                peer.Send(39,Death(tail,peer.Id,spawned.Body[10]));
                                Bytes(ReadUntil(38).Body,[(byte)tail,(byte)(tail>>8),0,0,0,0,255,0,0],
                                    "无尽清首房尾怪39获得完整退场38后才能请求第二房");
                                break;
                            }
                            byte[] deaths = new byte[1 + actors * 2];
                            deaths[0] = checked((byte)actors);
                            for (int at = 0; at < actors; at++)
                                spawned.Body.AsSpan(3 + at * 17,2).CopyTo(deaths.AsSpan(1 + at * 2));
                            peer.Send(255,deaths);
                            spawned = ReadUntil(210);
                        }
                        peer.Send(45,move);
                        peer.Expect(1,45,[1]);
                        var nextMap = peer.Expect(0,211).Body;
                        That(nextMap.Length == 15 && nextMap[0] == move[0] && nextMap[1] == move[1] &&
                            nextMap[7] == 1 && U32(nextMap,8) == U32(nextMap,2),
                            "首房清完后原先被拒的45成功，ACK45后211携带第二房坐标及真实地图编号");
                        int mapId = checked((int)U32(nextMap,8));
                        That(dungeons.MapPath(mapId) is { } path && rooms.Get(new("map", new(move[0],move[1]),mapId,path))
                            .AltarWaves is { Ultimate: false }, "第二房211创建字段在当前PVF中对应实际无尽波次地图，不能是随机种子");
                        // 整条路线入场时顺序预分配；第二房从首房完整计划尾键继续，不向首个210学习键。
                        completedRounds += trackerRoundCount;
                        (validateSpawn,batchCount) = SpawnCheck(returningInfo,nextMap,checked((ushort)nextRoomKey),firstRoom:false);
                        returningMap = nextMap;
                        Load(peer);
                        peer.Send(258);
                        var round = peer.Expect(0,215).Body;
                        That(round.Length == 5 && round[0] == completedRounds+1 && round[0] == 6 && U32(round,1) == trackerFirstDelay && U16(ReadUntil(210).Body,0) > 0,
                            "第二房完成37及258后从第六回合继续实际刷怪，不重复入场或提前结算");
                    }
                    remainingBatches = batchCount-1;
                    if (remainingBatches > 0)
                        peer.WaitForPendingData(10000,"保留当前回合第二批210，确定覆盖放弃ACK前的积压通知");
                }
                peer.Send(42);
                if (!loaded) peer.Expect(1,42,[1]);
                else
                {
                    int pending = 0;
                    var reply = peer.Read();
                    while (reply is { Kind:0,Opcode:210 })
                    {
                        That(pending < remainingBatches,"放弃ACK前只允许当前回合资源内的剩余批次");
                        validateSpawn!(reply);
                        pending++;
                        reply = peer.Read();
                    }
                    That(remainingBatches == 0 || pending > 0,"多批次祭坛确实覆盖积压210，不依赖偶然计时顺序");
                    That(reply is { Kind:1,Opcode:42 },"合法前置210之后必须紧接放弃ACK，不吞其他通知");
                    Bytes(reply.Body,[1],"放弃ACK仍为成功正文1");
                    Console.WriteLine($"PASS 祭坛{id}实际map{U32(returningMap,2)}：ACK前校验{pending}批积压210（本回合剩余上限{remainingBatches}）");
                }
                peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
                peer.Expect(0,24); RefreshedQuests(peer);
                // 已退场的加载/UI尾包不能再次创建角色或启动波次。
                peer.Send(37,new byte[8]); peer.Send(258); peer.Barrier();
                peer.NoFrame(1000,"退出ACK后及迟到加载/UI请求不得再启动祭坛波次",(0,210),(0,213),(0,215));
            }
            That(fixture.Query("SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$p0",peer.Id) is [[3L]] &&
                fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts WHERE character_id=$p0",peer.Id) is [[1L]],
                "加载前后放弃各保留一次准入，不由迟到加载/UI请求重复扣次或结算");
            SelectTowerMap(peer,griefLayer:1);
            var exhaustedState = fixture.Snapshot("SELECT gold,revision FROM characters WHERE id=$p0",peer.Id);
            var exhaustedAttempt = fixture.Snapshot("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id);
            peer.Send(16,request); peer.Expect(1,16,[0,246,0,0]);
            var invalidRequest = (byte[])request.Clone(); invalidRequest[5] = 1;
            peer.Send(16,invalidRequest); peer.Expect(1,16,[0,9,0,0]);
            peer.Send(16,request); peer.Expect(1,16,[0,246,0,0]); peer.Barrier();
            That(fixture.Snapshot("SELECT gold,revision FROM characters WHERE id=$p0",peer.Id) == exhaustedState &&
                fixture.Snapshot("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) == exhaustedAttempt &&
                fixture.Query("SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$p0",peer.Id) is [[3L]],
                "两祭坛次数用尽回复246，不创建第四次入场；后续非法字段仍用通用9，不复用旧拒绝原因");
            peer.Send(132); peer.Expect(1,132,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            peer.Expect(0,23); peer.Expect(0,24); peer.Barrier();

            int StartTracker(byte[] entryInfo,byte[] entryMap,WirePacket first)
            {
                spawnedRound = -1;
                completedRounds = 0;
                runSpawnedKeys.Clear();
                (validateSpawn,int count) = SpawnCheck(entryInfo,entryMap,U16(first.Body,3));
                foreach(var special in earlySpecial) validateSpawn(special);
                earlySpecial.Clear();
                validateSpawn(first);
                return count;
            }

            (Action<WirePacket> Check,int Count) SpawnCheck(byte[] entryInfo,byte[] entryMap,ushort firstKey,bool firstRoom = true)
            {
                int roundOffset = completedRounds;
                bool specialSeen = false;
                int nextBatch = 0, currentRound = 0;
                byte? selectedDifficulty = null;
                bool choiceAnnounced = false;
                (uint Monster,byte Kind)? specialAnnounced = null;
                var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
                var dungeon = dungeons.Get((int)id);
                var maze = dungeon.Mazes.Single(row => row.SourceOrdinal == entryInfo[5]);
                int mapId = checked((int)U32(entryMap,2));
                // 无尽首图由同DGN地图池生成，不一定有显式map specification或CandidateIds。
                string mapPath = dungeons.MapPath(mapId) ?? throw new InvalidDataException("祭坛211地图缺少资源路径");
                var owned = DungeonMapPool.Parse(mapId,mapPath,resources.Archive.ReadTokens(mapPath),resources.Archive.ResolveString);
                var mapPosition = new DungeonPoint(entryMap[0],entryMap[1]);
                var boss = new DungeonPoint(entryInfo[6],entryInfo[7]);
                // 地图池优先同坐标/用途的DGN显式候选；只有未声明的首图才依赖MAP反向归属。
                string kind = mapPosition == boss ? "boss" : "map";
                var declared = (maze.MapSpecification ?? []).Concat(maze.MapAlternatives)
                    .Where(row => row.Position == mapPosition && row.Kind == kind).ToArray();
                bool explicitMatch = declared.Any(row => row.CandidateIds.Count == 0 ? row.Id == mapId : row.CandidateIds.Contains(mapId));
                var cell = maze.GreedGrid.Cells?.SingleOrDefault(row => row.SourceOrdinal == mapPosition.Y*maze.Width+mapPosition.X);
                bool pooled = owned is not null && owned.Dungeons.Contains((int)id) &&
                    owned.Kind == (kind == "boss" ? "[boss]" : "[normal]") && cell is not null &&
                    (owned.Layouts & (1 << cell.Index)) != 0;
                That((declared.Length != 0 ? explicitMatch : pooled) &&
                    (!firstRoom || maze.Starts.Contains(mapPosition)) && maze.Bosses.Contains(boss),
                    "当前211地图来自同坐标DGN候选或合法反向地图池，首房起点和Boss坐标匹配maze");
                var room = SelfTestCatalogs.Rooms(resources.Archive).Get(new DungeonMap(mapPosition == boss ? "boss" : "map",
                    mapPosition,mapId,mapPath),requirePassiveDefinitions:false);
                var definition = DungeonAltarDefinition.Read(room.AltarWaves!);
                bool allowsSpecial = !definition.Ultimate;
                // 资源首回合各有唯一分支：无尽0、极限A；随机增益不改变批次、对象分配和时序。
                var plan = DungeonAltarPlan.Create(definition,peer.Id,firstKey,
                    dungeon.BasisLevel!.Value,room.Map.Kind == "boss",bound => bound-1);
                nextRoomKey = plan.NextObjectKey;
                trackerRoundCount = definition.RoundCount;
                var batches = plan.Rounds.Single(row => row.Round == 0).Batches;
                trackerFirstDelay = batches[0].DelayMilliseconds;
                return (CheckSpawn,batches.Length);

                void CheckSpawn(WirePacket packet)
                {
                    That(packet.Kind == 0,"祭坛资源tracker只接收通知");
                    if(packet.Opcode == 297)
                    {
                        var choices = plan.Rounds.Where(row => row.Round == currentRound+1).ToArray();
                        That(definition.Ultimate && nextBatch == batches.Length && choices.Length > 1,
                            "仅当前回合完整发布后的极限多分支允许难度UI");
                        if(packet.Body is [0,_,_,_])
                        {
                            That(!choiceAnnounced && packet.Body[1] == currentRound+1 && U16(packet.Body,2) == 10,
                                "极限选择倒计时对应下一回合且只发布一次");
                            choiceAnnounced = true;
                        }
                        else
                        {
                            That(choiceAnnounced && selectedDifficulty is null && packet.Body.Length == 1 &&
                                choices.Any(row => row.Difficulty == packet.Body[0]),"自动选择难度必须在当前资源下一回合分支中");
                            selectedDifficulty = packet.Body[0];
                        }
                        return;
                    }
                    if(packet.Opcode == 215)
                    {
                        var choices = plan.Rounds.Where(row => row.Round == currentRound+1).ToArray();
                        byte difficulty = choices.Length == 1 ? choices[0].Difficulty : selectedDifficulty ?? byte.MaxValue;
                        var following = choices.SingleOrDefault(row => row.Difficulty == difficulty);
                        That(nextBatch == batches.Length && following is not null && packet.Body.Length == 5 &&
                            packet.Body[0] == roundOffset+currentRound+2 && U32(packet.Body,1) == following.Batches[0].DelayMilliseconds,
                            "下一回合215必须连续、前回合已完整发布且时长和所选分支来自资源");
                        batches = following!.Batches;
                        currentRound++; nextBatch = 0; selectedDifficulty = null; choiceAnnounced = false;
                        return;
                    }
                    if(packet.Opcode == 213)
                    {
                        That(allowsSpecial && !specialSeen && specialAnnounced is null && packet.Body.Length == 5 &&
                            packet.Body[4] < 19 && batches.SelectMany(row => row.Actors).Any(actor => actor.Phase.MonsterId == U32(packet.Body,0)),
                            "213只能宣布本房当前回合资源内唯一特殊演员");
                        specialAnnounced = (U32(packet.Body,0),packet.Body[4]);
                        return;
                    }
                    That(packet.Opcode == 210 && nextBatch < batches.Length,"只允许当前实例当前回合资源内的连续210，不吞其他通知");
                    var batch = batches[nextBatch++];
                    spawnedRound = roundOffset+currentRound;
                    That(packet is { Kind:0,Opcode:210 } && packet.Body.Length == 4+batch.Actors.Length*17 &&
                        U16(packet.Body,0) == batch.Actors.Length,"当前实例210数量和17B记录严格匹配当前回合资源批次");
                    for(int at=0;at<batch.Actors.Length;at++)
                    {
                        var actor = batch.Actors[at];
                        int offset = 2+at*17;
                        byte kind = packet.Body[offset];
                        That(kind <= 19 && (kind == 19 || allowsSpecial && !specialSeen),
                            "极限演员kind严格19，无尽每房最多一个合法随机增益演员");
                        if(kind < 19)
                        {
                            That(specialAnnounced == ((uint)actor.Phase.MonsterId,kind),"特殊210演员必须匹配先前213怪物和kind");
                            specialSeen = true;
                        }
                        That(U16(packet.Body,offset+1) == actor.ObjectKey && runSpawnedKeys.Add(actor.ObjectKey) &&
                            actor.ObjectKey != peer.Id,"当前实例对象键与资源分配顺序一致且不重复、不冒充本人");
                        That(U32(packet.Body,offset+3) == actor.Phase.MonsterId && packet.Body[offset+7] == actor.Rank &&
                            packet.Body[offset+8] == actor.Level && U16(packet.Body,offset+9) == actor.Phase.Scale &&
                            U16(packet.Body,offset+11) == actor.Phase.X && U16(packet.Body,offset+13) == actor.Phase.Y &&
                            U16(packet.Body,offset+15) == actor.Phase.Z,"积压210的怪物、等级、rank、scale和坐标来自当前地图回合");
                    }
                    That(U16(packet.Body,packet.Body.Length-2) == batch.Interval,"积压210间隔仍匹配原资源，不接纳任意生成包");
                }
            }

            void ExpectActive(byte kind,ushort opcode,byte[] body)
            {
                var packet = peer.Read();
                int notifications = 0;
                while(packet.Kind == 0 && packet.Opcode is 210 or 213 or 215 or 297)
                {
                    That(++notifications <= 200,"祭坛活动期前置通知数量有界");
                    validateSpawn!(packet); packet = peer.Read();
                }
                That(packet.Kind == kind && packet.Opcode == opcode,$"祭坛活动期间严格期待{kind}/{opcode}");
                Bytes(packet.Body,body,$"祭坛活动正文{kind}/{opcode}");
            }

            WirePacket ReadUntil(ushort opcode)
            {
                for(int messages=0;messages<200;messages++)
                {
                    var packet=peer.Read();
                    if(packet.Kind == 0 && packet.Opcode is 210 or 213 or 215 or 297)
                    {
                        if(validateSpawn is not null) validateSpawn(packet);
                        else if(packet.Opcode == 213) earlySpecial.Add(packet);
                        else That(packet.Opcode == 210 && opcode == 210,"tracker建立前只允许首批210和前置特殊提示");
                    }
                    if(packet.Kind==0 && packet.Opcode==opcode) return packet;
                    That(packet.Kind==0 && packet.Opcode is 2 or 13 or 14 or 19 or 21 or 36 or 37 or 210 or 213 or 215 or 297,
                        $"祭坛计时只允许专用波次、到账与成长投影，不混入普通结算；收到{packet.Kind}/{packet.Opcode}");
                }
                throw new InvalidOperationException("祭坛预期通知没有到达："+opcode);
            }

            void ValidateTimedSpawn(WirePacket packet)
            {
                That(packet is { Kind: 0, Opcode: 210 } && packet.Body.Length >= 4 && U16(packet.Body,0) > 0 &&
                    packet.Body.Length == 4 + U16(packet.Body,0) * 17,
                    "无效39屏障只允许完整祭坛刷怪通知，不能用死亡确认、奖励或推进通知替代");
                for (int at = 2; at < packet.Body.Length - 2; at += 17)
                    That(packet.Body[at] <= 19 && U32(packet.Body,at+3) > 0 && packet.Body[at+7] is 0 or 3 &&
                        packet.Body[at+8] == next.Body[10] && U16(packet.Body,at+1) != peer.Id && invalidDeathSpawnKeys.Add(U16(packet.Body,at+1)),
                        "定时刷怪记录保留合法类型、怪物、等级和互不重复的对象键");
            }
        }
        foreach (var (dungeonId, option) in new (uint, byte)[] { (11007,0), (11007,1), (11006,0), (11006,1) })
        {
            ushort questId = dungeonId == 11006 ? (ushort)5650 : (ushort)7340;
            int prerequisite = dungeonId == 11006 ? 63 : 7339;
            int fee = dungeonId == 11006 ? 22000 : 0;
            int retryFee = dungeonId == 11006 ? dungeons.AltarAdmission.BloodFee(70,1) : 0;
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{}))
            {
                var repo = new CharacterRepository(fixture.Database);
                var candidate = CharacterCreation.Prepare($"Altar{dungeonId}{option}", resources.Characters.Get(0),
                    position,position,resources.Learning.Get);
                created = repo.Create(owner,candidate with { Level=70, AcceptedStoryLevel=70, CompletedQuests=done.Append(prerequisite).Distinct().ToArray(),
                    InitialLearning=SkillPointLedger.Synchronize(0,0,70,candidate.InitialLearning with
                        { Experience=growth.GetLevelStartExperience(70) },resources.Learning) });
                created = repo.CommitInventory(owner,created,new(created.Inventory,created.Inventory with
                    { Gold=500000 },"altar_result_action_seed"));
            }
            using var peer = Open(fixture);
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            peer.Send(31,[31,0,(byte)questId,(byte)(questId>>8)]);
            That(peer.Expect(1,31).Body[0] == 1, "结果操作角色合法接取祭坛关联任务");
            RefreshedQuests(peer);
            SelectTowerMap(peer,griefLayer:1);
            var request = Selection();
            BinaryPrimitives.WriteUInt32LittleEndian(request,dungeonId);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16),questId);
            if (dungeonId == 11007) BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(21),uint.MaxValue);
            peer.Send(16,request);
            peer.Expect(0,212); peer.Expect(0,211); if (fee > 0) peer.Expect(0,14); peer.Barrier();
            Load(peer); peer.Send(258);
            peer.Send(40,[0x74,0,4,1,0,0]);
            ReadTo(208);
            var exp = Experience(fixture,peer.Id);
            long resultGold = Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id)[0][0]);
            var rewards = peer.Expect(0,209).Body;
            Bytes(rewards,[0,(byte)(dungeonId == 11006 ? 15 : 7),1,255,255,255,255,0,0,0,0,0,0,0],
                "零回合失败以空奖FFFFFFFF通知而不是伪造零金币");
            peer.Send(72,[2,option]);
            peer.Expect(1,72,[1,2,option]);
            peer.Send(72,[1,option]);
            var experience = peer.Expect(0,37).Body;
            That(experience.Length == 91 && BinaryPrimitives.ReadUInt64LittleEndian(experience.AsSpan(1)) == exp,
                "祭坛结果操作先发布已提交的经验");
            var reply = peer.Read();
            while (reply is { Kind:0, Opcode:14 }) reply = peer.Read();
            That(reply is { Kind:1, Opcode:72 }, "祭坛结果操作收到确认而不是等待超时");
            Bytes(reply.Body,[1,1,option],"祭坛结果操作确认保留选项");
            if (option == 0)
            {
                peer.Expect(0,27); peer.Expect(0,212); peer.Expect(0,211);
                if (fee > 0) peer.Expect(0,14);
                peer.Barrier();
                That(fixture.Query("SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$p0",peer.Id) is [[2L]] &&
                    Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id)[0][0]) == resultGold - retryFee,
                    "再次挑战建立新的祭坛实例并按原准入扣次和费用");
            }
            else
            {
                peer.Expect(1,15,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),1]);
                peer.Expect(0,27); peer.Barrier();
                That(fixture.Query("SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$p0",peer.Id) is [[1L]],
                    "选择其他地下城只退场并打开选图，不重复扣次");
            }
            That(fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts WHERE character_id=$p0",peer.Id) is [[1L]] &&
                Experience(fixture,peer.Id) == exp, "结果操作不重复结算或发放经验");
            if (option == 0 && dungeonId == 11007)
            {
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
                peer.Expect(0,24); RefreshedQuests(peer);
                SelectTowerMap(peer,griefLayer:1);
                peer.Send(16,request); peer.Expect(0,212); peer.Expect(0,211); peer.Barrier();
                Load(peer); peer.Send(258); peer.Send(40,[0x74,0,4,1,0,0]);
                ReadTo(208); peer.Expect(0,209);
                var lastExp = Experience(fixture,peer.Id);
                peer.Send(72,[1,0]);
                var lastNotice = peer.Expect(0,37).Body;
                That(BinaryPrimitives.ReadUInt64LittleEndian(lastNotice.AsSpan(1)) == lastExp,
                    "次数用尽时仍先完整发布已提交经验");
                var rejected = peer.Read();
                while (rejected is { Kind:0, Opcode:14 }) rejected = peer.Read();
                That(rejected is { Kind:1, Opcode:72 }, "次数用尽的再次挑战不能让结果页等待超时");
                Bytes(rejected.Body,[1,1,2],"次数用尽按回城选项确认");
                peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]); peer.Expect(0,24);
                RefreshedQuests(peer); peer.Barrier();
                That(fixture.Query("SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$p0",peer.Id) is [[3L]] &&
                    fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts WHERE character_id=$p0",peer.Id) is [[2L]] &&
                    Experience(fixture,peer.Id) == lastExp, "次数用尽不创建第四次入场、不重复发奖");
            }

            WirePacket ReadTo(ushort opcode)
            {
                for (int i = 0; i < 200; i++)
                {
                    var packet = peer.Read();
                    if (packet.Kind == 0 && packet.Opcode == opcode) return packet;
                    That(packet.Kind == 0 && packet.Opcode is 2 or 13 or 14 or 19 or 21 or 32 or 36 or 210 or 213 or 215 or 297 or 550 or 1303 or 1536,
                        $"祭坛结果操作的前置帧必须属于当前玩法：{packet.Kind}/{packet.Opcode}");
                }
                throw new InvalidOperationException("祭坛结果操作缺少通知："+opcode);
            }
        }
        {
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{}))
            {
                var repo = new CharacterRepository(fixture.Database);
                var candidate = CharacterCreation.Prepare("AltarPoor",resources.Characters.Get(0),position,position,resources.Learning.Get);
                created = repo.Create(owner,candidate with { Level=70,AcceptedStoryLevel=70,CompletedQuests=done.Append(63).Distinct().ToArray(),
                    InitialLearning=SkillPointLedger.Synchronize(0,0,70,candidate.InitialLearning with
                        { Experience=growth.GetLevelStartExperience(70) },resources.Learning) });
                created = repo.CommitInventory(owner,created,new(created.Inventory,created.Inventory with { Gold=21999 },"altar_poor_seed"));
            }
            using var peer = Open(fixture);
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            ushort questId = 5650;
            peer.Send(31,[31,0,(byte)questId,(byte)(questId>>8)]);
            That(peer.Expect(1,31).Body[0] == 1,"金币不足角色合法接取无尽关联任务");
            RefreshedQuests(peer); SelectTowerMap(peer,griefLayer:1);
            var request = Selection(); BinaryPrimitives.WriteUInt32LittleEndian(request,11006);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16),5650);
            var before = fixture.Snapshot("SELECT gold,revision FROM characters WHERE id=$p0",peer.Id);
            var attempt = fixture.Snapshot("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id);
            peer.Send(16,request); peer.Expect(1,16,[0,10,0,0]);
            var invalid = (byte[])request.Clone(); invalid[5] = 1;
            peer.Send(16,invalid); peer.Expect(1,16,[0,9,0,0]);
            peer.Send(16,request); peer.Expect(1,16,[0,10,0,0]); peer.Barrier();
            That(fixture.Snapshot("SELECT gold,revision FROM characters WHERE id=$p0",peer.Id) == before &&
                fixture.Snapshot("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) == attempt &&
                fixture.Query("SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "金币不足回复10且不扣费扣次或创建尝试，非法请求不继承旧金币错误");
            peer.Send(132); peer.Expect(1,132,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            peer.Expect(0,23); peer.Expect(0,24); peer.Barrier();
        }
        Console.WriteLine("PASS 两祭坛实际TCP准入、UI门禁、清首房换房、计时刷怪、255、复活与失败专用结算（非客户端验收）");
    }

    static byte[] AltarZeroParticipantDeath(ushort key, byte level)
    {
        byte[] full = Death(key,ushort.MaxValue,level);
        byte[] bytes = [..full[..31],..full[45..]];
        bytes[30] = 0;
        bytes[31] = 0x4D;
        bytes[41] = 1;
        SealDeath(bytes);
        return bytes;
    }

    sealed class AltarTimers(int factor) : TimeProvider
    {
        readonly long origin=Stopwatch.GetTimestamp();
        public override long GetTimestamp() => checked((Stopwatch.GetTimestamp()-origin)*factor);
        public override long TimestampFrequency => Stopwatch.Frequency;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period) =>
            base.CreateTimer(callback,state,Scale(dueTime),Scale(period));
        TimeSpan Scale(TimeSpan value) => value==Timeout.InfiniteTimeSpan || value<=TimeSpan.Zero?value:
            TimeSpan.FromMilliseconds(Math.Max(1,Math.Ceiling(value.TotalMilliseconds/factor)));
    }
}
