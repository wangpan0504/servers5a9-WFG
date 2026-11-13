using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunLostLandAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        LostLandSessionTests.Run(resources.Towns, resources.Archive, resources.Characters);
        int checks = Count;
        var clock = new Inventory.DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-27T12:00:00+08:00"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "lost-land"), resources: resources, clock: clock);
        await fixture.StartAsync();
        var repository = new CharacterRepository(fixture.Database, resources.Quests);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var position = new Location(40, 0, 845, 155);
            for (byte slot = 0; slot < 3; slot++)
            {
                var candidate = CharacterCreation.Prepare("LostLandTcp" + slot, resources.Characters.Get(4), position, position, resources.Learning.Get);
                repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [3156],
                    InitialLearning = SkillPointLedger.Synchronize(4, 0, 95, candidate.InitialLearning with
                        { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning) });
            }
        }
        for (byte slot = 0; slot < 3; slot++)
        {
            await clock.WaitForOneShotTimersAsync(0);
            using var peer = Open(fixture);
            peer.Select(slot, prepared: true, resumeTown: true, room: false);
            var initialMap = Enter(peer);
            if (slot == 0)
            {
                peer.Send(16, Request()); peer.Send(37, new byte[8]); peer.Barrier();
                That(Used() == 1, "真实TCP重复16/37不重扣次数或重发603");
            }
            if (slot == 2)
            {
                That(initialMap[18] > 0, "带怪过门使用真实首房演员，不发送合成击杀");
                var move = new byte[100]; move[0] = 8; move[1] = 6;
                peer.Send(45, move); peer.Expect(1, 45, [1]);
                var next = peer.Expect(0, 29).Body;
                That(next[0] == 8 && next[1] == 6 && next[13] == 1 && U32(next, 14) == 11251,
                    "真实TCP未清怪过左门依次收到ACK45和目标房完整29");
                Load(peer); peer.Barrier();
                // 实收的第二房传送正文，保留客户端其余统计字段，仅目标格驱动原请求路径。
                var warp = Convert.FromHexString("010758060000F7000000007FA6050000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000C1010000000000000300000000000000000000000300080023600000000000003E410000000000");
                peer.Send(45,warp); peer.Expect(1,45,[1]);
                var destination = peer.Expect(0,29).Body;
                That(destination[0] == 1 && destination[1] == 7 && destination[13] == 1 && U32(destination,14) == 11206,
                    "实收CMD45从第二房跨区域到11206，沿原包序完成加载");
                Load(peer); peer.Barrier();
                peer.Send(45,move); peer.Expect(1,45,[1]);
                var secondCached = peer.Expect(0,29).Body;
                That(secondCached[0] == 8 && secondCached[1] == 6 && secondCached.Length == 16 && secondCached[13] == 0,
                    "传送返回第二房使用同一缓存，后续普通移动仍可执行");
                Load(peer); peer.Barrier();
                move[1] = 7;
                peer.Send(45,move); peer.Expect(1,45,[1]);
                var below = peer.Expect(0,29).Body;
                That(below[0] == 8 && below[1] == 7 && below[13] == 1 && U32(below,14) == 11202,
                    "传送后第二房向下普通出口仍完成完整45/29/37流程");
                Load(peer); peer.Barrier();
                move[1] = 6;
                peer.Send(45,move); peer.Expect(1,45,[1]); peer.Expect(0,29); Load(peer); peer.Barrier();
                move[0] = 9;
                peer.Send(45, move); peer.Expect(1, 45, [1]);
                var cached = peer.Expect(0, 29).Body;
                That(cached[0] == 9 && cached[1] == 6 && cached.Length == 16 && cached[13] == 0,
                    "真实TCP未清怪回访只使用起点缓存正文");
                Load(peer); peer.Barrier();
                // 水旋风实收正文同样走kind0；统计尾部不当作位置、高度或事件完成证明。
                var water = Convert.FromHexString("070947010000DD00000000926B010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000FD000800000000001E0003000200F9FFFFFF0900230002009B5B0000000000003E410000BFFF00");
                peer.Send(45,water); peer.Expect(1,45,[1]);
                var sea = peer.Expect(0,29).Body;
                That(sea[0] == 7 && sea[1] == 9 && sea[13] == 1 && U32(sea,14) == 11214,
                    "实收水旋风请求沿完整资源链进入海域，保留正式ACK45和29包序");
                Load(peer); peer.Barrier();
                peer.Send(45,water); peer.Expect(1,45,[0,19,0]); peer.Barrier();
                var wrongSource = new byte[100]; wrongSource[0] = 8; wrongSource[1] = 6;
                peer.Send(45,wrongSource); peer.Expect(1,45,[0,19,0]); peer.Barrier();
                var returnWater = new byte[100]; returnWater[0] = 9; returnWater[1] = 6;
                peer.Send(45,returnWater); peer.Expect(1,45,[1]);
                var port = peer.Expect(0,29).Body;
                That(port[0] == 9 && port[1] == 6 && port.Length == 16 && port[13] == 0,
                    "同格及错误源房拒绝后，海域水旋风仍可返回原港口缓存");
                Load(peer); peer.Barrier();
                // 实收公主返程100B正文；只提供目标格，不把统计尾部解释为救援完成事实。
                var rescue = Convert.FromHexString("09061F030000C500000000066B0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000006C0100000000000003000000000000000000000004000400DD4D000000000000A6370000000000");
                foreach (var (x,y) in new (byte,byte)[] { (8,6),(8,7),(0,9),(0,8),(1,8),(1,9) })
                {
                    move[0] = x; move[1] = y;
                    peer.Send(45,move); peer.Expect(1,45,[1]);
                    var step = peer.Expect(0,29).Body;
                    That(step[0] == x && step[1] == y,"真实TCP沿普通门及已有传送抵达公主房");
                    Load(peer); peer.Barrier();
                    if (x == 1 && y == 8) { peer.Send(45,rescue); peer.Expect(1,45,[0,19,0]); peer.Barrier(); }
                }
                peer.Send(45,rescue); peer.Expect(1,45,[1]);
                var rescued = peer.Expect(0,29).Body;
                That(rescued[0] == 9 && rescued[1] == 6 && rescued.Length == 16 && rescued[13] == 0 && Used() == slot + 1,
                    "错误源房拒绝后，公主房的实收返程仍沿ACK45及缓存29回起点，不重扣次数");
                Load(peer); peer.Barrier();
            }
            if (slot < 2)
            {
                // 精确等待本实例1800秒到期，不依赖活动边界等其他一次性定时器的总数。
                await clock.WaitForOneShotTimerAsync(clock.GetUtcNow().AddSeconds(LostLandAdmission.InitialSeconds));
                clock.Advance(TimeSpan.FromSeconds(LostLandAdmission.InitialSeconds - 1)); peer.Barrier();
                clock.Advance(TimeSpan.FromSeconds(1)); peer.Expect(0, 31, []);
            }
            if (slot == 0)
            {
                peer.Send(46, new byte[1]); peer.Barrier();
                That(Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts").Single().Single()) == 0,
                    "到期后普通46不授予普通结算或翻牌");
            }
            if (slot == 1)
            {
                // 周签到也有30秒等待；必须确认本角色、本实例的回城宽限已注册，不能借用同期限的活动计时器。
                That(SpinWait.SpinUntil(() =>
                {
                    var events = fixture.Events();
                    return events.Where(row => row.GetProperty("Kind").GetString() == "formal_dungeon_entry" &&
                            row.GetProperty("Detail").GetProperty("Accepted").GetBoolean() &&
                            row.GetProperty("Detail").GetProperty("CharacterId").GetUInt16() == peer.Id)
                        .Any(entry => events.Any(row => row.GetProperty("Kind").GetString() == "formal_lost_land_timer" &&
                            row.GetProperty("Detail").GetProperty("State").GetString() == "return_grace_started" &&
                            row.GetProperty("Detail").GetProperty("Connection").GetInt64() ==
                                entry.GetProperty("Detail").GetProperty("Connection").GetInt64() &&
                            row.GetProperty("Detail").GetProperty("InstanceId").GetGuid() ==
                                entry.GetProperty("Detail").GetProperty("Identity").GetProperty("InstanceId").GetGuid()));
                }, TimeSpan.FromSeconds(5)), "无42回城只推进当前实例已注册的30秒宽限");
                clock.Advance(TimeSpan.FromSeconds(30));
            }
            if (slot != 1) { peer.Send(42); peer.Expect(1, 42, [1]); }
            Town(peer);
            peer.Send(42); peer.Barrier();
            That(Used() == slot + 1, "自然结束、无42回城或主动退出均只消耗各角色一次，重复42与后续屏障正常");
        }
        Console.WriteLine($"PASS {Count - checks} 遗忘之地真实TCP带怪换房、603、到期31、42退出和无响应回城断言（非客户端验收）");

        byte[] Enter(TcpPeer peer)
        {
            SelectMap(peer);
            peer.Send(16, Request());
            var information = peer.Expect(0, 28).Body;
            That(U32(information, 0) == 4000, "正式入场发送真实遗忘之地信息");
            var map = peer.Expect(0, 29).Body;
            That(U32(map, 14) == 11200, "正式起始房使用匹配PVF地图11200");
            peer.Expect(0, 550);
            Load(peer);
            peer.Expect(0, 603, [0x08, 0x07, 0, 0, 0xB0, 0x04, 0, 0]);
            return map;
        }
        void Town(TcpPeer peer)
        {
            peer.Expect(0, 3);
            var transition = peer.Expect(0, 24).Body;
            That(transition[0] == 40 && transition[1] == 0, "退出落回乔安所在的原城镇");
            RefreshedQuests(peer); peer.Barrier();
        }
        long Used() => Convert.ToInt64(fixture.Query("SELECT sum(daily_used) FROM character_dungeon_incount WHERE dungeon=4000").Single().Single());
        static byte[] Request()
        {
            var body = Convert.FromHexString("A00F00000000000000FFFF0000000000000000000000000000");
            BinaryPrimitives.WriteUInt32LittleEndian(body, 4000);
            return body;
        }
    }
}
