using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.AntonRaidTcpTests;

namespace DfoServer.Testing;

internal static partial class LukeRaidRunTcpTests
{
    internal static Task ProductionAsync(ServerOptions source, string root, TcpTestResources resources) => ProductionCoreAsync(source, root, resources, false);
    internal static async Task RecoveryAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        foreach (int mode in new[] { 0, 1, 2 })
            await ProductionCoreAsync(source, Path.Combine(root, "recovery-mode-" + mode), resources, true, mode);
    }
    static async Task ProductionCoreAsync(ServerOptions source, string root, TcpTestResources resources, bool recovery, int recoveryMode = 0)
    {
        static WirePacket Until(TcpPeer peer, byte kind, ushort opcode)
        {
            for (int index = 0; index < 256; index++) { var packet = peer.Read(); if (packet.Kind == kind && packet.Opcode == opcode) return packet; }
            throw new InvalidOperationException("production TCP expected packet not received");
        }
        var clock = new LukeRaidClockTests.Clock(DateTimeOffset.UtcNow);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "luke-production"), resources: resources, clock: clock);
        await fixture.StartAsync();
        var definition = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive,
            SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items).Find(1)!;
        var catalog = definition.Resources!.EntryCatalog!;
        var repository = new CharacterRepository(fixture.Database); var owners = new AccountOwners();
        var port = ChannelTcpTests.Endpoints(fixture)[20];
        foreach (int count in recovery ? new[] { 1 } : new[] { 1, 4, 8 })
        {
            var peers = new List<TcpPeer>();
            try
            {
                for (int index = 0; index < count; index++)
                {
                    string account = $"lkprod{count}_{index}";
                    using (var owner = owners.Acquire(account, () => { }))
                    {
                        var location = new Location(38, 0, 629, 196);
                        var candidate = CharacterCreation.Prepare($"Lp{count}n{index}", resources.Characters.Get(0), location, location, resources.Learning.Get);
                        var learning = SkillPointLedger.Synchronize(0, 0, 90, candidate.InitialLearning with
                            { Experience = resources.Growth.GetLevelStartExperience(90) }, resources.Learning);
                        var role = repository.Create(owner, candidate with { Level = 90, AcceptedStoryLevel = 90, CompletedQuests = [2903, 4872], InitialLearning = learning });
                        var inventory = new InventoryService(resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
                        InventoryService.Commit(repository, owner, role, inventory.Grant(role, [new(10162065, 1)]));
                        if (recovery && recoveryMode == 2)
                        {
                            var alternate = CharacterCreation.Prepare("LpAlt", resources.Characters.Get(0), location, location, resources.Learning.Get);
                            repository.Create(owner, alternate with { Level = 90, AcceptedStoryLevel = 90,
                                CompletedQuests = [2903, 4872], InitialLearning = learning });
                        }
                    }
                    var peer = Open(fixture, account, gamePort: port, channelNumber: 20); peer.Select(); peers.Add(peer);
                }
                var leader = peers[0];
                leader.Send(664, Build(writer => { writer.Write((byte)1); writer.Write(Name("LukeFull")); writer.Write(new byte[4]); }));
                uint raidKey = U32(Until(leader, 1, 664).Body, 1);
                for (int index = 1; index < count; index++)
                {
                    leader.Send(10, Peer(peers[index].Id, 0, true)); Until(leader, 1, 10); Until(peers[index], 0, 7);
                    peers[index].Send(11, Peer(leader.Id, 0, false)); Until(peers[index], 1, 11);
                    foreach (var peer in peers) Drain(peer);
                }
                for (int index = 0; index < count; index++)
                {
                    leader.Send(669, Build(writer => { writer.Write(0u); writer.Write((uint)peers[index].Id); writer.Write(index < 4 ? 1u : 2u); }));
                    Bytes(Until(leader, 1, 669).Body, [1], "完整生产流程分队");
                    foreach (var peer in peers) Drain(peer);
                }
                leader.Send(658, [0]);
                var unavailable = Until(leader, 0, 598).Body;
                That(U32(unavailable) == count && Enumerable.Range(0, count).All(index =>
                    unavailable[6 + 5 * index] == 0 && U16(unavailable, 7 + 5 * index) == 0),
                    "只持有门票盒时原生658明确未就绪，不以盒子替代门票");
                Until(leader, 1, 658);
                foreach (var peer in peers) Drain(peer);
                foreach (var peer in peers)
                {
                    peer.Send(160, Build(writer => { writer.Write((short)65); writer.Write(1); }));
                    var opened = Until(peer, 1, 160).Body;
                    That(opened.Length == 26 && opened[0] == 1 && opened[1] == 0 && U32(opened, 2) == 10162065 &&
                        U16(opened, 16) == 1 && U32(opened, 18) == 10159649 && U32(opened, 22) == 1,
                        "真实160开卢克门票盒，产物为一张黑白的守护");
                    var delta = Until(peer, 0, 14).Body;
                    int ticketAt = Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123)
                        .Single(at => U32(delta, at + 2) == 10159649);
                    int expectedExpiry = resources.ItemResources.Items.Get(10159649).Expiration.Resolve(clock.GetUtcNow());
                    That(U32(delta, ticketAt + 56) == expectedExpiry && expectedExpiry > clock.GetUtcNow().ToUnixTimeSeconds(),
                        "开盒差量123B写真实绝对截止，客户端能显示期限");
                    That(fixture.Query("SELECT json_extract(item_core,'$.ExpireTime') FROM character_inventory_items " +
                        "WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10159649", peer.Id) is [[long savedExpiry]] && savedExpiry == expectedExpiry,
                        "盒子产出票据期限持久化，不依靠GM零期限票据");
                    Drain(peer);
                }
                leader.Send(658, [1]);
                var ready = Until(leader, 0, 598).Body;
                That(U32(ready) == count && Enumerable.Range(0, count).All(index =>
                    ready[6 + 5 * index] == 1 && U16(ready, 7 + 5 * index) == 1),
                    "真实开盒后原生658自动检测查询返回全员门票就绪");
                Until(leader, 1, 658);
                foreach (var peer in peers) Drain(peer);
                leader.Send(666);
                Bytes(Until(leader, 1, 666).Body, [1], "默认Runtime直接启用完整卢克开战，无注入合成策略");
                foreach (var peer in peers) Drain(peer);
                if (recovery)
                {
                    Enter([leader], leader, 5054); Load([leader]);
                    ushort original = leader.Id;
                    leader.Dispose();
                    await WaitAsync(() => fixture.LukeRejoinCandidate(original) is { OldOwnerReleased: true }, "真实图中断线完成候选捕获");
                    var candidate = fixture.LukeRejoinCandidate(original)!;
                    leader = Open(fixture, "lkprod1_0", gamePort: port, channelNumber: 20); peers[0] = leader; leader.Select();
                    var notice = Until(leader, 0, 835);
                    That(notice.Body.Length == 7 && U32(notice.Body) == candidate.Key, "同频道835指向原团，不创建新实例");
                    leader.Send(668, Build(writer => writer.Write(candidate.Key)));
                    Bytes(Until(leader, 1, 668).Body, [1], "668恢复原团并返回候战，非仅内部绑定");
                    Drain(leader, true);
                    That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE account=$p0 AND raid_type=1", "lkprod1_0") is [[1L]],
                        "重返不增加账号入场计数");
                    That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE character_id=$p0", original) is [[1L]], "重返保留原实例账本，不重复扣票");
                }
                var members = peers.Take(4).ToArray(); var others = peers.Skip(4).ToArray();
                if (!recovery) SelectionBoundaries(members, leader, 5054);
                int clearPhase = -1, phaseClears = 0;
                void Run(int dungeon, byte phase)
                {
                    var entered = Enter(members, leader, dungeon); Load(members);
                    if (clearPhase != phase)
                    {
                        leader.Send(658, [0]);
                        var costs = Until(leader, 0, 598).Body;
                        That(U32(costs) == count && Enumerable.Range(0, count).All(index => costs[6 + 5 * index] == 2),
                            "正式副本内费用查询仍返回全员已提交状态，不被城镇开战快照门槛清空");
                        Until(leader, 1, 658);
                    }
                    Clear(fixture, members, others, leader, entered[0], catalog, phase,
                        passiveTarget: dungeon == 5066 ? 64060 : dungeon == 5063 ? 64062 : null);
                    foreach (var peer in members.Where(peer => peer != leader)) Return(members, peer, true, resultCommand: true);
                    Return(members, leader, true, resultCommand: true);
                    foreach (var peer in others) Drain(peer, true);
                    if (clearPhase != phase) { clearPhase = phase; phaseClears = 0; }
                    phaseClears++;
                    foreach (var peer in peers) Drain(peer, true);
                    leader.Send(1361, Build(writer => { writer.Write((byte)1); writer.Write(raidKey); }));
                    var counts = RaidPanelTests.ReadClears(Until(leader, 1, 1361).Body);
                    That(members.All(peer => counts[peer.Id] == phaseClears) && others.All(peer => counts[peer.Id] == 0),
                        "原生1361返回本阶段真实个人次数，旁观分队不冒领，阶段切换后重新计数");
                }
                foreach (int dungeon in new[] { 5054, 5055, 5056 }) for (int step = 0; step < 3; step++) Run(dungeon, 0);
                string instance = (string)fixture.Query("SELECT instance_id FROM character_raid_runs WHERE character_id=$p0", leader.Id)[0][0]!;
                That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE instance_id=$p0 AND phase0_receipt IS NOT NULL", instance)
                    is [[long p1]] && p1 == count, "完整P1九次圣所签发逐人持久资格");
                if (recovery)
                {
                    clock.Advance(186);
                    await WaitAsync(() => fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE instance_id=$p0", instance) is [[1L]], "P1在线超时自动领取真实奖励");
                    var automatic = Drain(leader, true);
                    if (!automatic.Any(packet => packet is { Kind: 0, Opcode: 587, Body: [5, 0] }))
                    {
                        for (int stateIndex = 0; stateIndex < 16; stateIndex++)
                        { if (Until(leader, 0, 587).Body is [5, 0]) break; That(stateIndex < 15, "超时自动领奖后进入休息阶段"); }
                    }
                }
                else foreach (var peer in members)
                {
                    peer.Send(660, [1, 0]); var claimed = Drain(peer, true);
                    That(claimed.Any(packet => packet is { Kind: 1, Opcode: 660, Body: [1] }) && claimed.Any(packet => packet.Opcode == 600),
                        "P1个人实际不渝结果与领取回执写出");
                    foreach (var other in peers.Where(other => other != peer)) Drain(other, true);
                }
                // 未参与的第二队按真实资格处理；P1原门槛为0，不能借漏领奖跳过休息屏障。
                foreach (var peer in others) { peer.Send(660, [1, 0]); Drain(peer, true); foreach (var other in peers.Where(other => other != peer)) Drain(other, true); }
                leader.Send(666); Bytes(Until(leader, 1, 666).Body, [1], "团长真实提前启动P2，不重复扣票");
                Drain(leader, true); clock.Advance(3);
                foreach (var peer in peers) Drain(peer, true);
                foreach (int dungeon in new[] { 5311, 5313, 5057, 5058, 5059, 5060, 5064, 5065, 5061, 5066, 5062 }) Run(dungeon, 1);
                That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE instance_id=$p0 AND state=2", instance)
                    is [[long p2]] && p2 == count, "完整P2自然前置和光暗王座才记成功通关");
                if (recovery)
                {
                    ushort original = leader.Id; leader.Dispose();
                    await WaitAsync(() => fixture.LukeRejoinCandidate(original) is { OldOwnerReleased: true }, "领奖阶段真实离线完成清理");
                    if (recoveryMode != 0)
                    {
                        leader = Open(fixture, "lkprod1_0", gamePort: port, channelNumber: 20);
                        peers[0] = leader;
                        if (recoveryMode == 2) { leader.Select(1); That(leader.Id != original, "同账号改玩另一角色，不恢复原Battle"); }
                    }
                    clock.Advance(186);
                    await WaitAsync(() => fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE instance_id=$p0", instance) is [[3L]],
                        "P2离线/在线选角/其它角色均超时结算原角色两类奖励");
                    clock.Advance(10);
                    That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE instance_id=$p0", instance) is [[3L]], "离线自动结算幂等，不重抽或重复发奖");
                    That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE instance_id=$p0 AND character_id<>$p1", instance, original)
                        is [[0L]], "自动恢复奖励不能发给当前另一角色");
                    if (recoveryMode != 0)
                    {
                        if (recoveryMode == 1)
                            leader.NoFrame(250, "原角色领奖结果不投影到选角窗口", (0, 600), (0, 601));
                        else
                        {
                            var delivered = Drain(leader, true);
                            That(!delivered.Any(packet => packet.Opcode is 600 or 601), "原角色领奖结果不投影到另一角色窗口");
                        }
                        if (recoveryMode == 2)
                        {
                            leader.Dispose();
                            leader = Open(fixture, "lkprod1_0", gamePort: port, channelNumber: 20); peers[0] = leader;
                            leader.Select(1);
                            That(leader.Id != original, "自动领奖后另一角色可重登且没有资产revision冲突");
                        }
                    }
                    Console.WriteLine($"PASS Luke production socket rejoin and automatic rewards mode={recoveryMode}");
                    continue;
                }
                foreach (var peer in members)
                    foreach (byte kind in new byte[] { 1, 0 })
                    {
                        peer.Send(660, [kind, 0]); var claimed = Drain(peer, true);
                        That(claimed.Any(packet => packet is { Kind: 1, Opcode: 660, Body: [1] }) && claimed.Any(packet => packet.Opcode == 600),
                            "P2个人/团队实际产物各自提交与展示");
                        foreach (var other in peers.Where(other => other != peer)) Drain(other, true);
                    }
                That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE instance_id=$p0", instance)
                    is [[long receipts]] && receipts == count + 2 * members.Length, "冻结资格与真实参与领奖数对应，无重复箱内容");
                Console.WriteLine($"PASS Luke production natural phases members={count}");
            }
            finally { foreach (var peer in peers) peer.Dispose(); }
        }
        static async Task WaitAsync(Func<bool> condition, string message)
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(10))
            { if (condition()) { That(true, message); return; } await Task.Delay(10); }
            That(false, message);
        }
    }
}
