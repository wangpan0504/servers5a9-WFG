using System.Buffers.Binary;
using System.Diagnostics;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunAltarsAsync(ServerOptions source,string root,TcpTestResources resources)
    {
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
        foreach (uint id in new uint[] {11006,11007})
        {
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
            peer.Send(16,request);
            var info = peer.Expect(0,212).Body;
            var map = peer.Expect(0,211).Body;
            int fee = id==11006?22000:0;
            if (fee>0) peer.Expect(0,14);
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
            while (next is {Kind:0,Opcode:213}) next=peer.Read();
            That(next is {Kind:0,Opcode:210} && next.Body.Length==4+U16(next.Body,0)*17 && U16(next.Body,0)>0,
                "UI准备后实际计时发布210，正文数量和17字节对象记录相符");
            // 两祭坛实机均有完整39，不能仅用255合成报告证明击杀链路。
            int count=U16(next.Body,0);
            byte[] killed=new byte[1+count*2]; killed[0]=checked((byte)count);
            for(int at=0;at<count;at++) next.Body.AsSpan(3+at*17,2).CopyTo(killed.AsSpan(1+at*2));
            ushort key = U16(next.Body,3);
            byte[] normalDeath = Death(key,peer.Id,next.Body[10]);
            byte[] corrupt = (byte[])normalDeath.Clone();
            corrupt[^1] ^= 1;
            peer.Send(39,corrupt);
            peer.Send(39,Death(ushort.MaxValue,peer.Id,next.Body[10]));
            peer.Barrier();
            foreach (bool replay in new[] { false, true })
            {
                peer.Send(39,normalDeath);
                Bytes(ReadUntil(38).Body,[(byte)key,(byte)(key>>8),0,0,0,0,255,0,0],
                    replay ? "祭坛重复39补发相同对象退场，不重复推进或发奖" : "祭坛合法39先回完整38清除死亡对象");
            }
            // 255与39共用本Run死亡事实，混合重报不重复推进。
            peer.Send(255,killed);
            peer.Send(40,[0x74,0,4,1,0,0]);
            ReadUntil(32);
            peer.Send(41,[(byte)peer.Id,(byte)(peer.Id>>8)]);
            peer.Expect(1,41,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            peer.Expect(0,32,[(byte)peer.Id,(byte)(peer.Id>>8),1,0]); peer.Expect(0,14,CoinBalance(2));
            peer.Send(40,[0x74,0,4,1,0,0]); ReadUntil(32);
            peer.Send(255,killed);
            var ranking=ReadUntil(208).Body;
            That(ranking.Length==24 && U32(ranking,16)==(id==11006?15:7) &&
                fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts WHERE character_id=$p0",peer.Id) is [[1L]] &&
                fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0",peer.Id) is [[2L]],
                "第二次死亡截止后仅提交一次专用失败奖励，第一轮死亡回调与复活不重复结算");
            var rewards=peer.Expect(0,209).Body;
            That(rewards.Length>=6 && rewards[1]==(id==11006?15:7) && rewards[^3..].All(value=>value==0),
                "失败209保留四队员槽与真实完成回合，不替换成普通翻牌");
            var state=ReadUntil(3);
            That(state.Body is [1,_,_,0],"祭坛专用演出结束恢复本人城镇state0");
            peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$p0",peer.Id) is [[1L]] &&
                fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts WHERE character_id=$p0",peer.Id) is [[1L]],
                "一次准入和一次失败持久事实保持独立，计时不重复收费或发奖");

            // 同一角色剩余两次分别覆盖加载前和加载后放弃，不另起资源或服务夹具。
            foreach (bool loaded in new[] { false, true })
            {
                SelectTowerMap(peer,griefLayer:1);
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), loaded ? questId : 0u);
                peer.Send(16,request);
                var returningInfo = peer.Expect(0,212).Body;
                var returningMap = peer.Expect(0,211).Body;
                That(U32(returningMap,8) == U32(returningMap,2), "再入场仍使用真实地图编号");
                if (fee > 0) peer.Expect(0,14);
                peer.Barrier();
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
                        move[0] = checked((byte)target.X);
                        move[1] = checked((byte)target.Y);
                        peer.Send(45, move);
                        peer.Barrier();
                    }
                    if (id == 11006) peer.Send(258);
                    peer.Expect(0,215);
                    var spawned = ReadUntil(210);
                    That(U16(spawned.Body,0) > 0, "已启动波次的祭坛仍可主动退出");
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
                        Load(peer);
                        peer.Send(258);
                        var round = peer.Expect(0,215).Body;
                        That(round.Length == 5 && round[0] == 6 && U16(ReadUntil(210).Body,0) > 0,
                            "第二房完成37及258后从第六回合继续实际刷怪，不重复入场或提前结算");
                    }
                }
                peer.Send(42);
                peer.Expect(1,42,[1]);
                peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
                peer.Expect(0,24); RefreshedQuests(peer);
                // 已退场的加载/UI尾包不能再次创建角色或启动波次。
                peer.Send(37,new byte[8]); peer.Send(258); peer.Barrier();
            }
            That(fixture.Query("SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$p0",peer.Id) is [[3L]] &&
                fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts WHERE character_id=$p0",peer.Id) is [[1L]],
                "加载前后放弃各保留一次准入，不由迟到加载/UI请求重复扣次或结算");

            WirePacket ReadUntil(ushort opcode)
            {
                for(int messages=0;messages<200;messages++)
                {
                    var packet=peer.Read();
                    if(packet.Kind==0 && packet.Opcode==opcode) return packet;
                    That(packet.Kind==0 && packet.Opcode is 2 or 13 or 14 or 19 or 21 or 36 or 37 or 210 or 213 or 215 or 297,
                        $"祭坛计时只允许专用波次、到账与成长投影，不混入普通结算；收到{packet.Kind}/{packet.Opcode}");
                }
                throw new InvalidOperationException("祭坛预期通知没有到达："+opcode);
            }
        }
        Console.WriteLine("PASS 两祭坛实际TCP准入、UI门禁、清首房换房、计时刷怪、255、复活与失败专用结算（非客户端验收）");
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
