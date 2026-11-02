using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
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
    internal static async Task ProductionAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await ProductionCoreAsync(source, root, resources, false);
        await ProductionCoreAsync(source, Path.Combine(root, "ineligible"), resources, false, ineligible: true);
    }
    internal static async Task RecoveryAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        foreach (int mode in new[] { 0, 1, 2 })
            await ProductionCoreAsync(source, Path.Combine(root, "recovery-mode-" + mode), resources, true, mode);
    }
    static async Task ProductionCoreAsync(ServerOptions source, string root, TcpTestResources resources, bool recovery, int recoveryMode = 0, bool battlePassDeath = false, bool ineligible = false)
    {
        static WirePacket Until(TcpPeer peer, byte kind, ushort opcode, List<WirePacket>? observed = null)
        {
            for (int index = 0; index < 256; index++)
            {
                var packet = peer.Read(); observed?.Add(packet);
                if (packet.Kind == kind && packet.Opcode == opcode) return packet;
            }
            throw new InvalidOperationException("production TCP expected packet not received");
        }
        var clock = new LukeRaidClockTests.Clock(DateTimeOffset.UtcNow);
        BattlePassMissionPolicy? deathPolicy = null;
        if (battlePassDeath)
        {
            var start = DateTimeOffset.Parse("2026-09-10T06:00:00+08:00");
            deathPolicy = new("luke-death-fixture", start, BattlePassCatalog.Load(resources.Archive));
            clock = new LukeRaidClockTests.Clock(start.AddDays(44).AddHours(2));
            That(new[] { 1, 2 }.All(id => deathPolicy.Active(clock.GetUtcNow())
                .Any(row => row.Key.Category == 0 && row.Key.Id == id)), "原调度同日激活日击杀与领主任务");
            source = source with { BattlePassSeasonKey = "luke-death-fixture", BattlePassSeasonStart = start };
        }
        await using var fixture = new TcpTestFixture(source, Path.Combine(root,
            battlePassDeath ? "battle-pass-luke-raid-death" : "luke-production"), resources: resources, clock: clock);
        await fixture.StartAsync();
        var definition = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive,
            SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items,
            SelfTestCatalogs.Rooms(resources.Archive), lukeResourceChoices: LukeSanctumRoute.ResourceChoices).Find(1)!;
        var catalog = definition.Resources!.EntryCatalog!;
        var repository = new CharacterRepository(fixture.Database); var owners = new AccountOwners();
        var port = ChannelTcpTests.Endpoints(fixture)[20];
        foreach (int count in recovery || battlePassDeath || ineligible ? new[] { 1 } : new[] { 1, 4, 8 })
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
                    var peer = Open(fixture, account, gamePort: port, channelNumber: 20);
                    if (deathPolicy is not null)
                    {
                        peer.ExpectedOpenEvents = [2532, 266, 2520];
                        peer.ExpectedBattlePassEnd = checked((uint)deathPolicy.End.ToUnixTimeSeconds());
                    }
                    peer.Select(); peers.Add(peer);
                    That(peer.LukeChannelCardsInfo is [.., 1], "尚未建团或开战，进入卢克频道已收到自动展示塔罗牌组");
                    Bytes(peer.LukeChannelCardsInfo, peers[0].LukeChannelCardsInfo, "同频道同一日成员进入时使用完全相同塔罗布局与真实怪物表");
                }
                var leader = peers[0];
                leader.Send(664, Build(writer => { writer.Write((byte)1); writer.Write(Name("LukeFull")); writer.Write(new byte[] { 0, 0, 0, 5 }); }));
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
                var leaderStart = new List<WirePacket>();
                Bytes(Until(leader, 1, 666, leaderStart).Body, [1], "默认Runtime直接启用完整卢克开战，无注入合成策略");
                foreach (var peer in peers)
                {
                    var started = Drain(peer);
                    if (peer == leader) started.InsertRange(0, leaderStart);
                    That(started.Count(packet => packet is { Kind: 0, Opcode: 1184 }) == 1,
                        "正式Runtime开战向每名成员发送三圣所塔罗牌，资源装配不能遗漏");
                    Validate(started.Single(packet => packet is { Kind: 0, Opcode: 1184 }));
                    var openingCards = started.Single(packet => packet is { Kind: 0, Opcode: 1184 }).Body;
                    That(openingCards[^1] == 0, "开战恢复牌组不重复触发入频道自动弹窗");
                    Bytes(openingCards.AsSpan(0, openingCards.Length - 1).ToArray(),
                        peer.LukeChannelCardsInfo.AsSpan(0, peer.LukeChannelCardsInfo.Length - 1).ToArray(),
                        "开战真实路线及塔罗逐字节继承入频道当天牌组，不重新抽取");
                    That(started.Any(packet => packet is { Kind: 0, Opcode: 776 }),
                        "正式Runtime开战同步牌组怪物存活状态，不能只验证收到时的包体");
                    That(started.Any(packet => packet is { Kind: 0, Opcode: 583 }), "生产开团确实收到符号快照");
                    foreach (var packet in started.Where(packet => packet is { Kind: 0, Opcode: 583 })) Validate(packet);
                    foreach (int locked in new[] { 5055, 5056 })
                        That(started.Any(packet => packet is { Kind: 0, Opcode: 586, Body.Length: 6 } &&
                            U32(packet.Body) == locked && packet.Body[5] != (byte)RaidDungeonState.Open) &&
                            !started.Any(packet => packet is { Kind: 0, Opcode: 586, Body.Length: 6 } &&
                                U32(packet.Body) == locked && packet.Body[5] == (byte)RaidDungeonState.Open),
                            "正式开团只开放诞生，蔓延和湮灭保留真实未开放状态");
                }
                if (deathPolicy is not null)
                {
                    VerifyBattlePassDeaths(fixture, leader, catalog, definition.Resources!, clock, deathPolicy);
                    return;
                }
                LukeRaidDisconnectCandidate? p1RejoinCandidate = null;
                long p1RejoinCandidateStamp = 0;
                var frozenTeamRows = new Dictionary<ushort, byte[]>();
                if (recovery)
                {
                    Enter([leader], leader, 5054); Load([leader]);
                    ushort original = leader.Id;
                    leader.Dispose();
                    await WaitAsync(() => fixture.LukeRejoinCandidate(original) is { OldOwnerReleased: true }, "真实图中断线完成候选捕获");
                    var candidate = fixture.LukeRejoinCandidate(original)!;
                    That(candidate is { Phase: 0, OldOwnerReleased: true }, "P1首次捕获即核实原阶段和旧owner释放");
                    p1RejoinCandidate = candidate;
                    p1RejoinCandidateStamp = clock.GetTimestamp();
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
                if (ineligible)
                {
                    // 隔离库已有两次阶段资格但未最终通关；真实新开战仍合法，领奖额度保持耗尽。
                    int week = new RaidWeeklyCycle(DayOfWeek.Sunday, new(6, 0), TimeSpan.FromHours(8)).Key(clock.GetUtcNow());
                    for (int previous = 0; previous < 2; previous++)
                    {
                        var old = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
                        string Receipt(int phase, Guid eventId) => System.Text.Json.JsonSerializer.Serialize(
                            new RaidPhaseEligibility(old, RaidKind.Luke, phase, eventId, 0, leader.Id,
                                false, true, "eligible", "fixture/previous", week, []));
                        fixture.Sql("INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state," +
                            "phase0_event,phase0_anchor,phase0_receipt,phase1_event,phase1_anchor,phase1_receipt) " +
                            "VALUES($p0,$p1,$p2,1,'fixture/previous',0,1,$p3,0,$p4,$p5,0,$p6)", leader.Id,
                            $"lkprod{count}_0", old.ToString("N"), first.ToString("N"), Receipt(0, first),
                            second.ToString("N"), Receipt(1, second));
                    }
                }
                var members = peers.Take(4).ToArray(); var others = peers.Skip(4).ToArray();
                if (!recovery) SelectionBoundaries(members, leader, 5054);
                int clearPhase = -1, phaseClears = 0;
                var facilityRules = LukeSanctumObjectRules.Create(catalog.Script);
                var facilityCounts = new Dictionary<int, int> { [117] = 0, [118] = 0, [119] = 0 };
                List<WirePacket>[]? retryEntry = null;
                void Movie(TcpPeer peer)
                {
                    peer.Send(659,[0]); var skip=Drain(peer,true);
                    That(skip.Count(packet=>packet is {Kind:0,Opcode:599})==1 &&
                        !skip.Any(packet=>packet.Opcode==600),"659零值只提示跳过，不提前标记已选或发奖");
                    peer.Send(659,[0]); var repeated=Drain(peer,true);
                    That(!repeated.Any(packet=>packet.Opcode is 599 or 600),"重复跳过不重播通知或发奖");
                    peer.Send(659,[1]); var cards=Drain(peer,true);
                    That(cards.Any(packet=>packet is {Kind:0,Opcode:587,Body:[4,1]}) &&
                        cards.Any(packet=>packet is {Kind:0,Opcode:600,Body:[0 or 1,0]}),
                        "演出完成推进原生翻牌显示，尚未选择时没有本人奖励结果");
                    peer.Send(659,[1]);
                    That(!Drain(peer,true).Any(packet=>packet.Opcode is 587 or 600),"重复完成不重建翻牌窗口");
                }
                void PhaseOneMovies()
                {
                    IEnumerable<TcpPeer> SenderFirst(TcpPeer sender) => new[] { sender }.Concat(peers.Where(peer => peer != sender));
                    void NoCards(TcpPeer sender, string reason)
                    {
                        foreach (var peer in SenderFirst(sender))
                            That(!Drain(peer, true).Any(packet => packet.Opcode is 587 or 600), reason);
                    }
                    void RejectPrematureReward(TcpPeer sender)
                    {
                        string before = fixture.Snapshot();
                        sender.Send(660, [1, 2]);
                        foreach (var peer in SenderFirst(sender))
                        {
                            var rejected = Drain(peer, true);
                            That(!rejected.Any(packet => packet.Opcode is 587 or 600) &&
                                rejected.Count(packet => packet is { Kind: 1, Opcode: 660 }) == (peer == sender ? 1 : 0) &&
                                (peer != sender || rejected.Any(packet => packet is { Kind: 1, Opcode: 660, Body: [0, 0x12, 0] })),
                                "P1全员演出未完成时660明确拒绝并清pending，不开牌、不发成功或结果");
                        }
                        That(fixture.Snapshot() == before, "P1演出等待屏障内提前660不增加收据或资产");
                    }
                    RejectPrematureReward(leader);
                    foreach (var sender in peers)
                    {
                        sender.Send(659, [0]);
                        foreach (var peer in SenderFirst(sender))
                        {
                            var skipped = Drain(peer, true);
                            That(skipped.Count(packet => packet is { Kind: 0, Opcode: 599 }) == (peer == sender ? 1 : 0) &&
                                !skipped.Any(packet => packet.Opcode is 587 or 600),
                                "P1各成员659零值仅本人收到一次599，不能作为演出完成或开放牌面");
                        }
                        sender.Send(659, [0]);
                        foreach (var peer in SenderFirst(sender))
                            That(!Drain(peer, true).Any(packet => packet.Opcode is 599 or 587 or 600),
                                "P1重复659零值不重播599、不开牌");
                    }
                    for (int index = 0; index < peers.Count - 1; index++)
                    {
                        var sender = peers[index];
                        sender.Send(659, [1]);
                        NoCards(sender, "P1先完成者仍等待全Present成员，待命分队未完成时也不能提前开牌");
                        sender.Send(659, [1]);
                        NoCards(sender, "P1同一成员重复659完成不能替其他成员确认或释放屏障");
                        RejectPrematureReward(sender);
                    }
                    peers[^1].Send(659, [1]);
                    foreach (var peer in SenderFirst(peers[^1]))
                    {
                        var cards = Drain(peer, true);
                        That(cards.Count(packet => packet is { Kind: 0, Opcode: 587, Body: [4, 0] }) == 1 &&
                            cards.Count(packet => packet is { Kind: 0, Opcode: 587 }) == 1 &&
                            cards.Count(packet => packet is { Kind: 0, Opcode: 600, Body: [1, 0] }) == 1 &&
                            cards.Count(packet => packet is { Kind: 0, Opcode: 600 }) == 1,
                            "P1最后一名Present成员659完成后全员各一次587翻牌和kind1空600，无提前已选结果");
                    }
                    foreach (var sender in peers)
                    {
                        sender.Send(659, [1]);
                        NoCards(sender, "P1屏障释放后任一成员重复659完成均不重建或广播翻牌窗口");
                    }
                }
                void Run(int dungeon, byte phase)
                {
                    var entered = retryEntry ?? Enter(members, leader, dungeon);
                    retryEntry = null;
                    Load(members);
                    var monitors = definition.Resources.LukeCardSet!.Monitors!.Find((uint)dungeon,
                        entered[0].Single(packet => packet.Opcode == 28).Body[7]);
                    That(!monitors.IsEmpty, "生产HP按buff当前地下城/迷宫解析有界原序监控项");
                    var killedMonitors = new HashSet<int>();
                    if(phase==0&&phaseClears==0)
                    {
                        leader.Send(72, [1, 0]);
                        foreach (var peer in members)
                            That(!Drain(peer, true).Any(packet => packet.Opcode is 72 or 28 or 29),
                                "未清图72不能触发成功回执或替换任一成员Run");
                        // 另一分队先消费真实入图快照，再独立核对随后695产生的634。
                        foreach(var peer in others) Drain(peer,true);
                        var initialValues = Enumerable.Repeat(100u, monitors.Length).ToArray();
                        initialValues[0] = 73;
                        if (initialValues.Length > 1) initialValues[1] = 41;
                        leader.Send(695,Build(writer=>{writer.Write(0x12345678u);writer.Write((byte)initialValues.Length);
                            foreach (uint value in initialValues) writer.Write(value);}));
                        byte[] expected=Build(writer=>
                        {
                            writer.Write((byte)1);writer.Write((ushort)1);writer.Write((byte)members.Length);
                            foreach(var member in members)writer.Write(member.Id);
                            writer.Write(0u);writer.Write(0x12345678u);writer.Write((byte)initialValues.Length);
                            foreach (uint value in initialValues) writer.Write(value);
                        });
                        foreach(var peer in peers)
                        {
                            var status=Drain(peer,true);
                            Bytes(status.Single(packet=>packet is {Kind:0,Opcode:634}).Body,expected,"695真实上报向全团投影同一634，不局限图内分队");
                            That(!status.Any(packet=>packet.Opcode is 586 or 601),"实时HP只刷新状态，不重发副本状态或奖励");
                        }
                    }
                    if (phase == 1 && dungeon == 5066)
                        ProductionLiveHp(peers.ToArray(), members, leader, monitors);
                    bool Timer(WirePacket packet, byte kind, uint seconds) => packet is { Kind: 0, Opcode: 596, Body.Length: 13 } &&
                        packet.Body[0] == kind && U32(packet.Body, 1) == 5066 && U32(packet.Body, 9) == seconds;
                    if (dungeon == 5061)
                        That(entered.All(frames => frames.Count(packet => Timer(packet, 1, 600)) == 1),
                            "首次进光王座向本队每人发送600秒储藏室增长计时，同代只写一次");
                    if (dungeon == 5066 || dungeon == 5062)
                        That(entered.All(frames => !frames.Any(packet => packet is { Kind: 0, Opcode: 596 } && U32(packet.Body, 1) == 5066)),
                            "光王座退出后进储藏室及暗王座不重启已有增长/压制进度条");
                    if (clearPhase != phase)
                    {
                        foreach (var peer in peers)
                        {
                            foreach (byte mode in new byte[] { 0, 2 })
                            {
                                Drain(peer, true);
                                peer.Send(1361, Build(writer => { writer.Write(mode); writer.Write(raidKey); }));
                                var panel = Until(peer, 1, 1361).Body;
                                That(panel[0] == 1 && panel[1] == mode && RaidPanelTests.ReadRecordState(panel, 6) == (2, phase),
                                    "首次进各阶段，图内与待命成员查询均使用真实阶段，不能回填开团P1");
                                That(!RaidPanelTests.ReadGuideFlag(panel, 6), "一二阶段图内及城镇查询始终保留普通模式");
                                That(RaidPanelTests.ReadRecruitRoles(panel, 6) == 5, "开战及阶段切换不清空或重置原招募对象");
                                if (mode == 2) RaidPanelTests.ReadClears(panel);
                                That(!Drain(peer, true).Any(packet => packet.Opcode is 587 or 601),
                                    "查询实时记录不重播阶段动画或结算");
                            }
                        }
                        leader.Send(658, [0]);
                        var costs = Until(leader, 0, 598).Body;
                        That(U32(costs) == count && Enumerable.Range(0, count).All(index => costs[6 + 5 * index] == 2),
                            "正式副本内费用查询仍返回全员已提交状态，不被城镇开战快照门槛清空");
                        Until(leader, 1, 658);
                    }
                    clock.Advance(7);
                    bool terminal=dungeon==5056&&phaseClears==8 || dungeon==5062;
                    if(terminal&&others.Length>0)
                    {
                        // 编队时第二队首名成员先入槽并成为队长；这里只请求选图，不安装Run。
                        others[0].Send(15,new byte[4]);
                        foreach(var peer in others)
                            That(Drain(peer,true).Count(packet=>packet is {Kind:0,Opcode:27})==1,
                                "另一分队真实停留选图场景，覆盖阶段末无Run成员自动退出");
                    }
                    var phaseResults=terminal?PhasePackets(peers):null;
                    List<WirePacket> combatTimers = [], updates = [];
                    // 生产原资源清图后隐藏普通目标，能源图进入Close恢复期；合成规则夹具仍检查Clear。
                    IReadOnlyDictionary<int,RaidDungeonState>? finalStates = phase==1
                        ? new Dictionary<int,RaidDungeonState>{{dungeon,dungeon is 5063 or 5066 ? RaidDungeonState.Close : RaidDungeonState.Hide}}
                        : dungeon==5056&&phaseClears==8
                            ? new Dictionary<int,RaidDungeonState>{{5054,RaidDungeonState.Hide},{5055,RaidDungeonState.Hide},{5056,RaidDungeonState.Hide}} : null;
                    Clear(fixture, members, others, leader, entered[0], catalog, phase,
                        passiveTarget: dungeon == 5066 ? 64060 : dungeon == 5063 ? 64062 : null,
                        observe: frames => { updates.AddRange(frames); combatTimers.AddRange(frames.Where(packet => packet.Opcode == 596)); },
                        expectedStates: finalStates, phaseResults: phaseResults,
                        roomCleared: room =>
                        {
                            if (phase == 0) ProductionFacility(peers.ToArray(), leader,
                                definition.Resources.LukeObjects!, facilityRules, dungeon,
                                entered[0].Single(packet => packet.Opcode == 28).Body[7], room, facilityCounts,
                                phaseClears % 3, catalog.Script);
                            else
                            {
                                foreach (var monster in SelfTestCatalogs.Rooms(resources.Archive).Get(room,
                                    requirePassiveDefinitions: false, allowUnresolvedSpecialMetadata: true).Monsters)
                                    if (monitors.Any(target => !target.Passive && target.Template == monster.MonsterId))
                                        killedMonitors.Add(monster.MonsterId);
                                ProductionMonitorRoomHp(peers.ToArray(), members, leader, monitors, killedMonitors);
                            }
                        },
                        waypoints: phase == 0 ? ProductionFacilityStops(catalog, definition.Resources.LukeObjects!,
                            dungeon, entered[0].Single(packet => packet.Opcode == 28).Body[7]) : null);
                    if (dungeon == 5066)
                        That(combatTimers.Count(packet => Timer(packet, 1, uint.MaxValue)) == members.Length &&
                            combatTimers.Count(packet => Timer(packet, 2, 300)) == members.Length,
                            "真实销毁能源对象后逐人结束增长旧条并开始300秒压制，不等待旧600秒到期");
                    bool canRetry = phase == 0 && phaseClears % 3 < 2;
                    if (phase == 0 && dungeon == 5054)
                        foreach (int locked in new[] { 5055, 5056 })
                            That(updates.Count(packet => packet is { Kind: 0, Opcode: 586, Body.Length: 6 } &&
                                U32(packet.Body) == locked && packet.Body[5] == (byte)RaidDungeonState.Open) ==
                                    (phaseClears == 0 ? members.Length : 0),
                                "首次诞生真实清图即向本队逐人开放蔓延/湮灭，后续通关不重播未变化状态");
                    That(terminal || updates.Count(packet => packet is { Kind: 0, Opcode: 261 } &&
                        packet.Body.SequenceEqual(new byte[] { canRetry ? (byte)9 : (byte)0 })) == members.Length,
                        "真实清图按各图剩余次数向本分队逐人发布一次再挑战资格，旁观队不获得清图按钮");
                    if (phase == 0 && phaseClears == 0)
                    {
                        byte[] report = DungeonSettlementProtocolTests.Report(members.Select(peer => (peer.Id, (byte)99)).ToArray());
                        leader.Send(46, report);
                        foreach (var peer in members)
                            That(!Drain(peer, true).Any(packet => packet.Opcode is 31 or 34 or 35 or 115),
                                "31后的原生46报告保持普通奖励拒绝且连接可继续，不重复许可或打开普通翻牌");
                    }
                    List<WirePacket> returned = [];
                    if (terminal)
                    {
                        returned=phaseResults![leader];
                        if (phase == 1 && !ineligible)
                        {
                            var frozen = members.OrderBy(peer => peer.Id).Select(peer =>
                                System.Text.Json.JsonSerializer.Deserialize<RaidPhaseEligibility>((string)fixture.Query(
                                    "SELECT phase1_receipt FROM character_raid_runs WHERE character_id=$p0", peer.Id)[0][0]!)!).ToArray();
                            var expectedTeam = Build(writer =>
                            {
                                writer.Write((byte)3); writer.Write(checked((byte)frozen.Length));
                                foreach (var row in frozen)
                                {
                                    That(row.Eligible, "二阶段权益页只取阶段事务真实签发的成员资格");
                                    var offer = row.Offers.Single(offer => offer.Kind == 0);
                                    var items = offer.LukeAdditionalResults.Insert(0, offer.LukeResult!);
                                    var itemBody = Build(itemsWriter =>
                                    {
                                        itemsWriter.Write(checked((byte)items.Length));
                                        foreach (var item in items)
                                        {
                                            itemsWriter.Write(item.RawCode!.Value); itemsWriter.Write(item.ItemId!.Value);
                                            itemsWriter.Write(item.DisplayNumber!.Value); itemsWriter.Write(item.RawFlag!.Value);
                                            itemsWriter.Write(item.RawTail1!.Value); itemsWriter.Write(item.EnchantUpgrade!.Value);
                                        }
                                    });
                                    frozenTeamRows.Add(row.Character, itemBody);
                                    writer.Write(row.Character); writer.Write(offer.Card); writer.Write(itemBody);
                                }
                            });
                            foreach (var peer in peers)
                            {
                                var frames = phaseResults[peer];
                                if (members.Contains(peer))
                                    Bytes(frames.Single(packet => packet is { Kind: 0, Opcode: 600, Body: [3, ..] }).Body,
                                        expectedTeam, "有资格成员在首次选牌及state4前收到完整冻结权益，包括尚未点击的晚领奖成员");
                                else That(!frames.Any(packet => packet.Opcode == 600) &&
                                    frames.Count(packet => packet is { Kind: 0, Opcode: 588 }) == 1,
                                    "无P2参与资格的待命成员只播放演出及原因，不安装不可选择的权益页");
                                That(!frames.Any(packet => packet is { Kind: 0, Opcode: 587, Body: [4, ..] }),
                                    "完整团队模型先于原生组合state4计时安装，不等待15秒后补空页");
                            }
                            That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE instance_id=$p0 AND phase=1", frozen[0].Instance.ToString("N")) is [[0L]],
                                "团队页只展示事务已密封权益，首次点击前未强发任何二阶段资产");
                        }
                        foreach(var peer in others)
                        {
                            var frames=phaseResults[peer];
                            That(frames.FindIndex(packet=>packet is {Kind:0,Opcode:24}) is var town&&town>=0&&
                                frames.FindIndex(packet=>packet is {Kind:0,Opcode:601})>town,
                                "选图待命成员也先24自动回城再601，不被阶段状态先行覆盖");
                        }
                        if (ineligible)
                        {
                            foreach (var frames in phaseResults.Values)
                                That(frames.Count(packet => packet is { Kind: 0, Opcode: 588 }) == 1 &&
                                    !frames.Any(packet => packet.Opcode == 600 || packet is { Kind: 0, Opcode: 587, Body: [4, ..] }),
                                    "真实阶段额度耗尽仍601及Reward演出并提示一次原因，不安装空团队页/四卡/八卡");
                        }
                        updates.AddRange(phaseResults.Values.SelectMany(frames=>frames));
                        That(phaseResults.Values.All(frames=>!frames.Any(packet=>packet is {Kind:0,Opcode:261,Body:[9]})),
                            "阶段全部结束没有再挑战资格，自动退场无42/72请求");
                    }
                    else if (phase == 0 && phaseClears % 3 < 2)
                    {
                        if (members.Length > 1)
                        {
                            members[1].Send(72, [1, 0]);
                            foreach (var peer in members)
                                That(!Drain(peer, true).Any(packet => packet.Opcode is 72 or 28 or 29),
                                    "非队长不能替全分队再次挑战或先回成功");
                        }
                        var attempts = fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", leader.Id);
                        if (dungeon == 5054)
                        {
                            leader.Send(72, [1, 1]);
                            foreach (var peer in members)
                            {
                                var selected = Drain(peer, true);
                                That(selected.Any(packet => packet is { Kind: 1, Opcode: 72, Body: [1, 1, 1] }) &&
                                    selected.Count(packet => packet is { Kind: 0, Opcode: 27 }) == 1 &&
                                    !selected.Any(packet => packet.Opcode is 28 or 29),
                                    "前两次诞生后真实72选择其他地下城释放旧Run并打开全队选图");
                            }
                            That(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", leader.Id)
                                is [[long unchangedAttempt]] && attempts is [[long oldAttempt]] && unchangedAttempt == oldAttempt,
                                "结果菜单选图本身不创建持久尝试或消费圣所进度");
                            retryEntry = Enter(members, leader, dungeon, sceneAlreadyOpen: true);
                        }
                        else retryEntry = Retry(members, leader, dungeon);
                        That(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", leader.Id)
                            is [[long nextAttempt]] && attempts is [[long lastAttempt]] && nextAttempt == lastAttempt + 1,
                            "结果菜单重进只新增一次真实持久尝试，不复用旧Run或重复扣圣所进度");
                    }
                    else if (phase == 0 && phaseClears == 2)
                    {
                        leader.Send(72, [1, 0]);
                        foreach (var peer in members)
                        {
                            var exhausted = Drain(peer, true);
                            That(exhausted.Any(packet => packet is { Kind: 1, Opcode: 72, Body: [1, 1, 2] }) &&
                                exhausted.Any(packet => packet is { Kind: 0, Opcode: 24 }) &&
                                !exhausted.Any(packet => packet.Opcode is 28 or 29),
                                "圣所次数耗尽后旧按钮请求全分队正常回城，不假成功重开封闭图");
                        }
                    }
                    else
                    {
                        foreach (var peer in members.Where(peer => peer != leader)) updates.AddRange(Return(members, peer, true, resultCommand: true));
                        returned = Return(members, leader, true, resultCommand: true); updates.AddRange(returned);
                    }
                    if(terminal)
                    {
                        int town=returned.FindIndex(packet=>packet is {Kind:0,Opcode:24});
                        int result=returned.FindIndex(packet=>packet is {Kind:0,Opcode:601});
                        That(result>town&&U32(returned[result].Body,2)>0,"先安装城镇再结果演出，601使用真实非零阶段耗时");
                    }
                    if(dungeon==5062)
                        That(updates.Where(packet=>packet is {Kind:0,Opcode:1446}).Any(packet=>
                            RaidPanelTests.ReadWeeklyClears(packet.Body)[1]==(1u,1u)),"最终暗王座提交后即时发布真实日周通关次数各一");
                    var rosterUpdates = terminal ? phaseResults!.Values.SelectMany(frames => frames).Concat(updates) : updates;
                    That(rosterUpdates.Any(packet => packet is { Kind: 1, Opcode: 1361, Body: [1, 2, ..] } &&
                        RaidPanelTests.ReadRecordState(packet.Body, 6).Phase == phase && !RaidPanelTests.ReadGuideFlag(packet.Body, 6) &&
                        RaidPanelTests.ReadRecruitRoles(packet.Body, 6) == 5),
                        "清图实时发布完整阶段及成员快照，不只更换计数留下旧阶段");
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
                That(facilityCounts.Values.All(value => value > 0),
                    "正常生产1/4/8人完整路线均实际报告三圣所设施，不以始终为零的快照冒充蓝格联通");
                string instance = (string)fixture.Query("SELECT instance_id FROM character_raid_runs WHERE character_id=$p0 AND state=0", leader.Id)[0][0]!;
                That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE instance_id=$p0 AND phase0_receipt IS NOT NULL", instance)
                    is [[long p1]] && p1 == count, "完整P1九次圣所签发逐人持久资格");
                void FinishIneligible(int phase)
                {
                    var peer = peers[0];
                    string before = fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid");
                    peer.Send(659, [0]); var skipped = Drain(peer, true);
                    That(skipped.Count(packet => packet is { Kind: 0, Opcode: 599 }) == 1 &&
                        !skipped.Any(packet => packet.Opcode is 600 or 588), "无资格演出仍接受真实跳过，只通知一次且不重发原因");
                    peer.Send(659, [1]); var completed = Drain(peer, true);
                    That(completed.Count(packet => packet is { Kind: 1, Opcode: 659, Body: [1] }) == 1 &&
                        !completed.Any(packet => packet.Opcode is 600 or 588 || packet is { Kind: 0, Opcode: 587, Body: [4, ..] }) &&
                        (phase == 1 || completed.Any(packet => packet is { Kind: 0, Opcode: 587, Body: [5, 0] })),
                        "真实659完成后P1进入待命、P2结束，不借Completed数字重开空翻牌");
                    peer.Send(660, [1, 2]); var rejected = Drain(peer, true);
                    That(rejected.Any(packet => packet is { Kind: 1, Opcode: 660, Body: [0, 0x12, 0] }) &&
                        !rejected.Any(packet => packet.Opcode is 600 or 588), "无资格660明确拒绝，不能由演出确认恢复奖励");
                    That(fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") == before &&
                        fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE instance_id=$p0", instance) is [[0L]],
                        "无资格演出和拒绝零资产变化、零领奖收据");
                }
                if (ineligible) FinishIneligible(0);
                else if (recovery)
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
                else
                {
                    PhaseOneMovies();
                    foreach (var peer in peers)
                    {
                        peer.Send(660, [1, 2]); var claimed = Drain(peer, true);
                        That(claimed.Any(packet => packet is { Kind: 1, Opcode: 660, Body: [1] }) && claimed.Any(packet => packet.Opcode == 600),
                            "P1全员演出完成后个人实际不渝结果与领取回执写出");
                        int personal = claimed.FindIndex(packet => packet is { Kind: 0, Opcode: 600, Body: [1, 1, ..] });
                        int skipped = claimed.FindIndex(packet => packet is { Kind: 0, Opcode: 600, Body: [3, 0] });
                        That(personal >= 0 && skipped > personal && skipped < claimed.FindIndex(packet => packet is { Kind: 1, Opcode: 660 }),
                            "P1实际本人结果后发送空wire3再ACK，原生跳过不存在的第二页，不新发奖励");
                        foreach (var other in peers.Where(other => other != peer)) Drain(other, true);
                    }
                }
                leader.Send(666); Bytes(Until(leader, 1, 666).Body, [1], "团长真实提前启动P2，不重复扣票");
                AwaitPhaseTwo(clock, peers.ToArray(), leader);
                foreach (int dungeon in new[] { 5311, 5313, 5057, 5058, 5059, 5060, 5064, 5065, 5061, 5066, 5062 }) Run(dungeon, 1);
                That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE instance_id=$p0 AND state=2", instance)
                    is [[long p2]] && p2 == count, "完整P2自然前置和光暗王座才记成功通关");
                if (ineligible)
                {
                    FinishIneligible(1);
                    Console.WriteLine("PASS Luke production phase limits preserve both movies without empty cards");
                    continue;
                }
                if (recovery)
                {
                    ushort original = leader.Id;
                    var previousCandidate = p1RejoinCandidate ?? throw new InvalidOperationException("缺少P1原重返候选");
                    That(previousCandidate is { Phase: 0, OldOwnerReleased: true } &&
                        previousCandidate.Instance.ToString("N") == instance &&
                        clock.GetElapsedTime(p1RejoinCandidateStamp) >= TimeSpan.FromSeconds(300) &&
                        fixture.LukeRejoinCandidate(original) == previousCandidate,
                        "P1已绑定在线候选跨原截止仍保持原阶段，不能把旧owner释放当作本轮P2断线完成");
                    leader.Dispose();
                    await WaitAsync(() => fixture.LukeRejoinCandidate(original) is { Phase: 1, OldOwnerReleased: true } current &&
                        current.Instance == previousCandidate.Instance && current.Key == previousCandidate.Key &&
                        current.RaidSlot == previousCandidate.RaidSlot && current.Squad == previousCandidate.Squad,
                        "领奖阶段同实例P2新候选真实离线完成清理");
                    if (recoveryMode != 0)
                    {
                        leader = Open(fixture, "lkprod1_0", gamePort: port, channelNumber: 20);
                        peers[0] = leader;
                        if (recoveryMode == 2) { leader.Select(1); That(leader.Id != original, "同账号改玩另一角色，不恢复原Battle"); }
                    }
                    clock.Advance(186);
                    var rewardWait = System.Diagnostics.Stopwatch.StartNew();
                    var lastStep = TimeSpan.Zero;
                    int retrySteps = 0;
                    Guid rewardInstance = Guid.ParseExact(instance, "N");
                    await WaitAsync(() =>
                    {
                        var receipts = fixture.Query("SELECT character_id,phase,kind FROM character_raid_reward_receipts WHERE instance_id=$p0 ORDER BY phase,kind", instance);
                        if (receipts.Length == 3) return true;
                        // 原10秒轮询中只驱动已登记的本实例一秒重排；ACK不代表门禁释放。
                        // 每次至少相隔一秒墙钟，不追赶补发，最多5步；这不是生产等待承诺。
                        long beforeStamp = clock.GetTimestamp();
                        if (receipts.Length < 3 && retrySteps < 5 && rewardWait.Elapsed - lastStep >= TimeSpan.FromSeconds(1) &&
                            clock.PendingDeadlines.Count(row => row.Instance == rewardInstance && row.Due == beforeStamp + 1000) == 1)
                        {
                            lastStep = rewardWait.Elapsed;
                            int beforeCallbacks = clock.Callbacks;
                            clock.Advance(1);
                            retrySteps++;
                            var after = fixture.Query("SELECT character_id,phase,kind FROM character_raid_reward_receipts WHERE instance_id=$p0 ORDER BY phase,kind", instance);
                            Console.WriteLine($"Luke recovery mode={recoveryMode} retry={retrySteps}/5 stamp={beforeStamp}->{clock.GetTimestamp()} callbacks={beforeCallbacks}->{clock.Callbacks} receipts={string.Join(";", after.Select(row => string.Join(",", row)))}");
                            return after.Length == 3;
                        }
                        return false;
                    }, "P2离线/在线选角/其它角色均超时结算原角色两类奖励");
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
                {
                    Movie(peer);
                    foreach (byte kind in new byte[] { 1, 0 })
                    {
                        peer.Send(660, [kind, 3]); var claimed = Drain(peer, true);
                        That(claimed.Count(packet => packet is { Kind: 1, Opcode: 660, Body: [1] }) == 1 &&
                            claimed.Count(packet => packet is { Kind: 0, Opcode: 600 }) == 1 &&
                            !claimed.Any(packet => packet is { Kind: 0, Opcode: 600, Body: [3, ..] }) &&
                            !claimed.Any(packet => packet is { Kind: 0, Opcode: 587 }),
                            "P2个人/团队实际产物各自提交与展示");
                        if (kind == 0)
                            Bytes(claimed.Single(packet => packet is { Kind: 0, Opcode: 600 }).Body.AsSpan(5).ToArray(),
                                frozenTeamRows[peer.Id], "晚领取成员最终实际产物逐字节等于预先密封的团队权益，未重抽或新增第三奖项");
                        foreach (var other in peers.Where(other => other != peer)) Drain(other, true);
                    }
                }
                That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE instance_id=$p0", instance)
                    is [[long receipts]] && receipts == count + 2 * members.Length, "冻结资格与真实参与领奖数对应，无重复箱内容");
                leader.Dispose();
                leader=Open(fixture,$"lkprod{count}_0",gamePort:port,channelNumber:20); peers[0]=leader; leader.Select();
                That(RaidPanelTests.ReadWeeklyClears(leader.RaidWeeklyClearInfo)[1]==(1u,1u),"完整通关后重登仍从账本恢复一次日周次数");
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
    static void AwaitPhaseTwo(LukeRaidClockTests.Clock clock, TcpPeer[] peers, TcpPeer leader)
    {
        // 1347只屏障连接，不代表全团时钟已取得命令门禁并提交P2；以真实587为完成事实。
        var ordered = new[] { leader }.Concat(peers.Where(peer => peer != leader)).ToArray();
        That(ordered.Length == peers.Length && ordered.Select(peer => peer.Id).Distinct().Count() == peers.Length,
            "P2屏障包含队长及全部唯一成员，不遗漏另一分队");
        var latest = new Dictionary<ushort, string>();
        var observed = new HashSet<ushort>();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        int virtualSeconds = 0;
        DrainStep(0, clock.Callbacks);
        // 额外五次只覆盖生产既有一秒重调度，是测试观察上限，不是新的阶段时长合同。
        for (int step = 1; step <= 6; step++)
        {
            CheckDeadline();
            int callbacksBefore = clock.Callbacks;
            int advance = step == 1 ? 3 : 1;
            clock.Advance(advance);
            virtualSeconds += advance;
            DrainStep(step, callbacksBefore);
            if (ordered.All(peer => observed.Contains(peer.Id) && latest.GetValueOrDefault(peer.Id) == "0201"))
            {
                That(true, "全部成员实际收到P2 Fighting的587后才发送选图，未用连接ACK代替阶段完成");
                return;
            }
        }
        That(false, "P2真实阶段屏障超出3+5秒虚拟上限：" + State());

        void DrainStep(int step, int callbacksBefore)
        {
            var phaseFrames = new Dictionary<ushort, string[]>();
            foreach (var peer in ordered)
            {
                CheckDeadline();
                var frames = Drain(peer, true);
                var phases = frames.Where(packet => packet is { Kind: 0, Opcode: 587 }).ToArray();
                foreach (var packet in phases)
                {
                    That(packet.Body.Length == 2, "P2屏障仅按完整两字节587读取阶段");
                    latest[peer.Id] = Convert.ToHexString(packet.Body);
                    if (packet.Body is [2, 1]) observed.Add(peer.Id);
                }
                phaseFrames[peer.Id] = phases.Select(packet => Convert.ToHexString(packet.Body)).ToArray();
                CheckDeadline();
            }
            Console.WriteLine("Luke P2 phase barrier: " + System.Text.Json.JsonSerializer.Serialize(new
            {
                Step = step, VirtualSeconds = virtualSeconds, CallbacksBefore = callbacksBefore,
                CallbacksAfter = clock.Callbacks, ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
                PhaseFrames = phaseFrames, Latest = latest,
                Missing = ordered.Where(peer => !observed.Contains(peer.Id) || latest.GetValueOrDefault(peer.Id) != "0201")
                    .Select(peer => peer.Id).ToArray()
            }));
        }
        string State() => System.Text.Json.JsonSerializer.Serialize(new
        {
            VirtualSeconds = virtualSeconds, Callbacks = clock.Callbacks, ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            Latest = latest, Missing = ordered.Where(peer => !observed.Contains(peer.Id) || latest.GetValueOrDefault(peer.Id) != "0201")
                .Select(peer => peer.Id).ToArray()
        });
        void CheckDeadline() => That(elapsed.Elapsed < TimeSpan.FromSeconds(10), "P2真实阶段屏障超出原10秒墙钟上限：" + State());
    }

}
