using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PreyTeamTcpTests
{
    static async Task CompletePhases(TcpTestFixture fixture, List<TcpPeer> participants, RaidResources resources, TownCatalog towns,
        uint key, LukeRaidClockTests.Clock clock, bool phaseOneOnly = false)
    {
        var peers = participants.ToArray();
        var catalog = resources.EntryCatalog!;
        var packets = peers.Select(_ => new List<WirePacket>()).ToArray();
        var reports = peers.Select((_, index) => new AntonTcpProbe.AntonProbeReport { Observe = packets[index].Add }).ToArray();
        int[] firstTargets = resources.Script.Phases[0].Dungeons.Where(row => Type(row) == "garden")
            .Concat(resources.Script.Phases[0].Dungeons.Where(row => Type(row) == "sanctuary")).Select(row => row.Id).ToArray();
        int gardenSymbol = resources.Script.Symbols["[clear garden]"];
        That(gardenSymbol == 224, "原普雷资源庭院攻击增益由真实符号224驱动");
        That(firstTargets.Length == 6 && firstTargets.Distinct().Count() == 6,
            "普通两阶段测试使用原资源两个庭院与四圣地");
        for (int index = 0; index < firstTargets.Length; index++)
        {
            int[] clearStarts = packets.Select(rows => rows.Count).ToArray();
            int owner = index % 2;
            bool phaseEnd = index + 1 == firstTargets.Length;
            Board(fixture, peers[owner], towns, 4);
            AntonTcpProbe.Enter(peers[owner], catalog, firstTargets[index], reports[owner], clear: true,
                leave: !phaseEnd, allowPhaseReward: phaseEnd);
            foreach (var peer in peers.Where(peer => peer != peers[owner]))
                AntonTcpProbe.Note(reports[Array.IndexOf(peers, peer)], Read(peer, "other raid member sanctuary state"));
            for (int member = 0; member < peers.Length; member++)
            {
                bool registered = false;
                foreach (var participation in packets[member].Skip(clearStarts[member]).Where(row => row is { Kind: 0, Opcode: 584 }))
                {
                    using var reader = new BinaryReader(new MemoryStream(participation.Body));
                    int count = reader.ReadByte();
                    for (int row = 0; row < count; row++)
                    {
                        uint dungeon = reader.ReadUInt32();
                        byte operation = reader.ReadByte(), members = reader.ReadByte();
                        reader.ReadBytes(members * 2);
                        That(operation is 0 or 2, "普通普雷584仅静默登记与移除位置，不另发通关系统消息");
                        registered |= dungeon == firstTargets[index] && operation == 2 && members > 0;
                    }
                    That(reader.BaseStream.Position == reader.BaseStream.Length, "静默584保留完整成员及全部参与位置行");
                }
                That(registered, "移除通关消息后仍发布本人及其他团员所需的真实Run参与位置");
            }
            if (index < 2)
                for (int member = 0; member < peers.Length; member++)
                {
                    var cleared = packets[member].Skip(clearStarts[member]).ToList();
                    int symbols = cleared.FindIndex(row => row is { Kind: 0, Opcode: 583 } && Symbols(row.Body)[gardenSymbol] == index + 1);
                    int state = cleared.FindIndex(row => row is { Kind: 0, Opcode: 586, Body.Length: 6 } &&
                        U32(row.Body) == firstTargets[index] && row.Body[5] == (byte)RaidDungeonState.Clear);
                    int animation = cleared.FindIndex(row => row is { Kind: 0, Opcode: 1595, Body.Length: 8 } &&
                        U32(row.Body) == 5 && U32(row.Body, 4) == firstTargets[index]);
                    That(symbols >= 0 && state > symbols && animation > state &&
                        Symbols(reports[member].Symbols.Last())[gardenSymbol] == index + 1,
                        "每座庭院真实清图后，全团先收224计数1/2的583，再586 Clear及完整u32 op5/DGN演出");
                    if (index == 0 && member == owner)
                        That(cleared.Take(symbols).Any(row => row is { Kind: 0, Opcode: 583 } && Symbols(row.Body)[gardenSymbol] == 0),
                            "首座庭院入图快照仍为224=0，真实死亡后才变为1");
                }
        }
        That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=3 AND phase0_event IS NOT NULL AND phase1_event IS NULL") is [[2L]],
            "四圣地真实清图后两名原成员共同保存首阶段资格，未提前完成第二阶段");
        AwaitAutomaticReward(0, FirstPhaseDelivered);
        That(FirstPhaseDelivered(), "一阶段资格和全部Run退场后自动交付四份收据并进入二阶段，不等待手工659/660或144秒兜底");
        for (int index = 0; index < peers.Length; index++)
        {
            var results = packets[index].Where(row => row is { Kind: 0, Opcode: 600 }).ToArray();
            That(results.Length == 2 && new byte[] { 0, 1 }.All(kind => results.Count(row => row.Body[0] == kind) == 1) &&
                packets[index].All(row => row.Opcode != 599 && row is not { Kind: 0, Opcode: 587, Body: [4, 0] }),
                "普通一阶段只发送各kind已提交的实际600，不预填未选牌、跳过电影或开启不存在的一阶段翻牌");
            foreach (var result in results) CheckActualReward(result, peers[index]);
            int phase = packets[index].FindIndex(row => row is { Kind: 0, Opcode: 587, Body: [2, 1] });
            That(results.All(result => packets[index].IndexOf(result) < phase),
                "本人一阶段真实600全部写出后才发布二阶段state2/phase1");
            int lastResult = results.Max(result => packets[index].IndexOf(result));
            int nextSymbols = packets[index].FindIndex(lastResult + 1, row => row is { Kind: 0, Opcode: 583 });
            That(nextSymbols > phase && phase >= 0,
                "二阶段587先安装新阶段，随后发送新符号快照，避免由旧阶段控件消费");
            That(Symbols(packets[index][nextSymbols].Body)[gardenSymbol] == 2,
                "进入二阶段的首份583立即继承两座庭院攻击增益计数2");
        }
        var finalPackets = packets[(firstTargets.Length - 1) % peers.Length];
        int finalEntry = finalPackets.FindLastIndex(row => row is { Kind: 0, Opcode: 28 });
        int finalReturn = finalPackets.FindLastIndex(row => row is { Kind: 0, Opcode: 24, Body: [59, 3, ..] });
        int rewardState = finalPackets.FindIndex(row => row is { Kind: 0, Opcode: 587, Body: [3, 0] });
        int firstResult = finalPackets.FindIndex(row => row is { Kind: 0, Opcode: 601, Body.Length: 10 } &&
            row.Body[0] == 0 && row.Body[1] == 0);
        int firstCard = finalPackets.FindIndex(row => row is { Kind: 0, Opcode: 600 });
        int nextPhase = finalPackets.FindIndex(row => row is { Kind: 0, Opcode: 587, Body: [2, 1] });
        That(finalEntry >= 0 && finalReturn > finalEntry && rewardState > finalReturn && firstResult > finalReturn &&
            firstCard > finalReturn && nextPhase > firstCard,
            "最后清图者最后24先于一阶段结果及真实600，二阶段只在已提交产物写出后开放");
        foreach (var peer in peers)
        {
            peer.Send(1361, Build(writer => { writer.Write((byte)0); writer.Write(key); }));
            byte[] body = Read(peer, "second phase opened information").Single(row =>
                row is { Kind: 1, Opcode: 1361 } && row.Body.Length >= 2 && row.Body[1] == 0).Body;
            That(body.Length >= 83 && body[0] == 1 && body[1] == 0 && U32(body, 2) == key && U32(body, 6) == key,
                "原1361 mode0保留成功前缀、查询团key与完整记录团key");
            int title = checked((int)U32(body, 10));
            That(title <= body.Length - 83, "团记录标题长度保留后续16字节字段与完整队长成员块");
            int at = 14 + title;
            That(body[at] == 3 && body[at + 1] == 2 && body[at + 2] == 1 && body[at + 7] == 0,
                "实际团记录为普通普雷type3、Fighting state2、第二阶段phase1，未误标引导或终局");
            at += 16;
            int name = checked((int)U32(body, at + 3));
            That(name <= body.Length - at - 53 && at + 53 + name == body.Length,
                "团记录独立消费完整队长成员名及53字节固定块，无截断或未解释尾部");
            peer.Send(1361, Build(writer => { writer.Write((byte)1); writer.Write(key); }));
            var memberInfo = Read(peer, "second phase member locations").Single(row =>
                row is { Kind: 1, Opcode: 1361, Body: [1, 1, ..] }).Body;
            var locations = RosterLocations(memberInfo);
            That(peers.All(member => locations.GetValueOrDefault(member.Id) == 3),
                "一阶段切至二阶段仍保留各成员真实甲板位置，不回退候战区显示");
        }
        if (phaseOneOnly)
        {
            That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3 AND phase=0") is [[4L]] &&
                fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3 AND phase=1") is [[0L]],
                "首阶段两人两类奖励各提交一份收据，未产生第二阶段奖励");
            That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=3 AND state=0 AND phase0_event IS NOT NULL AND phase1_event IS NULL") is [[2L]],
                "领取首阶段奖励后两名成员仍在同一未终局攻坚中，未提前记录二阶段通关");
            return;
        }
        int[] phaseOneStarts = packets.Select(rows => rows.Count).ToArray();
        var second = resources.Script.Phases[1];
        int departure = resources.Script.Symbols["[slenikon from area]"], destination = resources.Script.Symbols["[slenikon to area]"];
        int speed = second.Rules.SelectMany(rule => rule.Behavior).Single(action =>
            action.Tag == "[set]" && action.Symbol == "[slenikon speed]").RawIntegers[0];
        int preparation = second.Rules.SelectMany(rule => rule.Behavior).Single(action =>
            action.Tag == "[set timer]" && action.Timer == new RaidTimer(2, 1001)).RawIntegers[2];
        int[] route = [317, 320, 318, 320, 319, 320, 317];
        for (int stop = 0; stop < route.Length; stop++)
        {
            clock.Advance(stop == 0 ? preparation : speed);
            for (int member = 0; member < peers.Length; member++)
            {
                var flight = Read(peers[member], "second phase natural flight");
                AntonTcpProbe.Note(reports[member], flight);
                var symbols = Symbols(flight.Last(row => row is { Kind: 0, Opcode: 583 }).Body);
                That(symbols[departure] == route[stop] && symbols[destination] == (stop + 1 < route.Length ? route[stop + 1] : 320),
                    "原PVF二阶段自然时钟向全员同步外围与本体交替航线");
            }
        }
        int garden = second.Dungeons.Single(row => Type(row) == "big garden").Id;
        int boss = second.Dungeons.Single(row => Type(row) == "boss").Id;
        int[] support = second.Rules.SelectMany(rule => rule.Trigger)
            .Where(clause => clause.Tag == "[on clear dungeon]" && clause.DungeonId is { } id && id != garden && id != boss)
            .Select(clause => clause.DungeonId!.Value).Distinct().ToArray();
        That(support.Length == 2 && support.All(id => second.Dungeons.Any(row => row.Id == id)),
            "二阶段支援目标沿原清图规则定位，不借普通普雷每日图");
        Board(fixture, peers[1], towns, 4);
        AntonTcpProbe.Enter(peers[1], catalog, garden, reports[1], clear: true);

        Board(fixture, peers[0], towns, 5);
        int bossStart = packets[0].Count;
        AntonTcpProbe.Enter(peers[0], catalog, boss, reports[0], clear: false);
        byte[] info = packets[0].Skip(bossStart).Last(row => row is { Kind: 0, Opcode: 28 }).Body;
        byte[] map = packets[0].Skip(bossStart).Last(row => row is { Kind: 0, Opcode: 29 }).Body;
        peers[0].Send(37, new byte[8]);
        var loaded = Read(peers[0], "second phase boss initial load");
        AntonTcpProbe.Note(reports[0], loaded);
        Ack(loaded, 37, [1], "本体原37加载成功，原正式Run保持在场");
        That(loaded.Any(row => row is { Kind: 0, Opcode: 30 }), "本体实际房间完成加载释放");
        foreach (int dungeon in support.Reverse())
        {
            Board(fixture, peers[1], towns, 4);
            AntonTcpProbe.Enter(peers[1], catalog, dungeon, reports[1], clear: true);
        }
        AntonTcpProbe.Note(reports[0], Read(peers[0], "second phase support state"));
        That(reports[0].Symbols.Any(body => Symbols(body)[resources.Script.Symbols["[isys groggy]"]] == 1),
            "支援队真实通图使本体队收到资源虚弱状态");

        int roomHp = resources.Script.Symbols["[isys hp in dungeon]"];
        int worldHp = resources.Script.Symbols["[isys hp]"];
        int reducedHp = 0;
        int confirmationsBefore = reports[0].Opcode115, permissionsBefore = reports[0].Opcode31;
        FinishLoadedDungeon(peers[0], catalog, boss, info, map, reports[0],
            beforeBossClear: () => reducedHp = ReportBossHp(7500, reportZero: false), clearBoss: false);
        int groggySeconds = second.Rules.SelectMany(rule => rule.Behavior)
            .Single(action => action.Tag == "[set timer]" && action.Timer == new RaidTimer(2, 211)).RawIntegers[2];
        clock.Advance(groggySeconds);
        AwaitReturn(bossStart, "原PVF虚弱timer211到期后自动发24退鸟背，无需死亡或手动42");
        AntonTcpProbe.Note(reports[1], Read(peers[1], "second phase groggy expired"));
        var expired = Symbols(reports[0].Symbols.Last());
        That(expired[worldHp] == reducedHp && expired[resources.Script.Symbols["[isys groggy]"]] == 0 &&
            reports[0].Opcode115 == confirmationsBefore && reports[0].Opcode31 == permissionsBefore &&
            fixture.Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=3 AND phase1_event IS NOT NULL") is [[0L]],
            "虚弱到期保存已报告剩余HP且未签发死亡许可或二阶段通关");

        Board(fixture, peers[0], towns, 5);
        int retrySeconds = second.Dungeons.Single(row => row.Id == boss).Declarations
            .Single(row => row.Tag == "[member retry time]").Values.Single().Number;
        uint retryDeadline = RetryDeadline(peers[0], peers[0].Id);
        That(retrySeconds > 0 && retryDeadline == clock.GetUtcNow().ToUnixTimeSeconds() + retrySeconds &&
            RetryDeadline(peers[1], peers[1].Id) == 0,
            "强退者5003期限按原声明从退出起算，未参加本体的支援成员没有同一限制");
        await RejoinDuringRetry();
        RejectRetry("本体强退后立即重入");
        clock.AdvanceWithoutCallbacks(retrySeconds - 1);
        That(Position(fixture, peers[0]) is { Town: 59, Area: 5 } && RetryDeadline(peers[0], peers[0].Id) == retryDeadline,
            "等待期间本人位置及已签期限保持，查询和重试不重新起算");
        RejectRetry("本体重试期限前一秒");
        int retryStart = packets[0].Count;
        clock.AdvanceWithoutCallbacks(1);
        AntonTcpProbe.Enter(peers[0], catalog, boss, reports[0], clear: false);
        info = packets[0].Skip(retryStart).Last(row => row is { Kind: 0, Opcode: 28 }).Body;
        map = packets[0].Skip(retryStart).Last(row => row is { Kind: 0, Opcode: 29 }).Body;
        peers[0].Send(37, new byte[8]);
        var reloaded = Read(peers[0], "second phase boss retry load");
        AntonTcpProbe.Note(reports[0], reloaded);
        Ack(reloaded, 37, [1], "原成员重试期限恰好到达后正式进入本体并完成37加载");
        var restored = Symbols(packets[0].Skip(retryStart).Last(row => row is { Kind: 0, Opcode: 583 }).Body);
        That(reloaded.Any(row => row is { Kind: 0, Opcode: 30 }) && restored[roomHp] == reducedHp && restored[worldHp] == reducedHp,
            "本体再次挑战按上次虚弱到期的世界HP恢复房内HP，不重置为满血");
        int dungeonSeconds = catalog.Find(boss)!.LimitTimeSeconds!.Value;
        int timeoutStart = packets[0].Count;
        clock.Advance(dungeonSeconds - 1);
        var beforeTimeout = Read(peers[0], "second phase boss deadline before last second");
        AntonTcpProbe.Note(reports[0], beforeTimeout);
        That(beforeTimeout.All(row => row is not { Kind: 0, Opcode: 24 }) &&
            Position(fixture, peers[0]) is not { Town: 59, Area: 3 },
            "本体原DGN期限前一秒仍留在正式Run，不按支援图期限提前踢出");
        clock.Advance(1);
        AwaitReturn(timeoutStart, "本体原DGN期限恰好到达后自动发24退鸟背，不依赖死亡或手动42");
        AntonTcpProbe.Note(reports[1], Read(peers[1], "second phase boss timed out"));
        var timedOut = Symbols(reports[0].Symbols.Last());
        That(timedOut[worldHp] == reducedHp && reports[0].Opcode115 == confirmationsBefore &&
            reports[0].Opcode31 == permissionsBefore &&
            fixture.Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=3 AND phase1_event IS NOT NULL") is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3 AND phase=1") is [[0L]],
            "本体图超时保留已提交世界HP，不签发死亡、二阶段资格或奖励");
        Board(fixture, peers[0], towns, 5);
        RejectRetry("本体图超时后仍遵守原成员重试期限");
        clock.AdvanceWithoutCallbacks(retrySeconds);
        retryStart = packets[0].Count;
        AntonTcpProbe.Enter(peers[0], catalog, boss, reports[0], clear: false);
        info = packets[0].Skip(retryStart).Last(row => row is { Kind: 0, Opcode: 28 }).Body;
        map = packets[0].Skip(retryStart).Last(row => row is { Kind: 0, Opcode: 29 }).Body;
        peers[0].Send(37, new byte[8]);
        var deadlineRetry = Read(peers[0], "second phase boss retry after dungeon timeout");
        AntonTcpProbe.Note(reports[0], deadlineRetry);
        Ack(deadlineRetry, 37, [1], "单图失败及成员重试期限结束后可正常再次加载原本体");
        Board(fixture, peers[1], towns, 4);
        peers[1].Send(15, Build(writer => writer.Write((uint)support[0])));
        var supportSelection = Read(peers[1], "support member selecting at final clear");
        AntonTcpProbe.Note(reports[1], supportSelection);
        Ack(supportSelection, 15, [1], "支援成员在左侧只选图不入图，末阶段自动退场覆盖无Run的选图状态");
        int[] completionStarts = packets.Select(rows => rows.Count).ToArray();
        FinishLoadedDungeon(peers[0], catalog, boss, info, map, reports[0],
            beforeBossClear: () => ReportBossHp(5000), leave: false);
        AwaitReturn(retryStart, "本体真实39清图后由原阶段执行器直接发24回内侧备战区，不补手动42掩盖退场缺口", 2);
        AntonTcpProbe.Note(reports[1], Read(peers[1], "second phase final state"));
        That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=3 AND state=2 AND phase0_event IS NOT NULL AND phase1_event IS NOT NULL") is [[2L]],
            "本体真实死亡与退出提交两名成员最终通关，保留各自阶段资格");
        RevealFinalPhase();
        var finalStage = packets[0].Skip(retryStart).ToList();
        int lastReturn = finalStage.FindLastIndex(row => row is { Kind: 0, Opcode: 24 });
        That(finalStage.Count(row => row is { Kind: 0, Opcode: 24 }) == 1 &&
            finalStage[lastReturn] is { Body: [59, 2, ..] } &&
            finalStage.FindIndex(row => row is { Kind: 0, Opcode: 601, Body.Length: 10 } && row.Body[1] == 1) > lastReturn &&
            finalStage.FindIndex(row => row is { Kind: 0, Opcode: 600 }) > lastReturn,
            "二阶段只发一次24回内侧备战区，真实601结果及600牌面在该24后显示，不二次迁城清掉窗口");
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3") is [[8L]],
            "两人两阶段两类奖励各只有一个持久收据");
        That(peers.All(peer => fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[59L, 2L]]),
            "末阶段成功后本体与支援成员均回内侧备战区");
        for (int index = 0; index < peers.Length; index++)
        {
            var completed = packets[index].Skip(completionStarts[index]).ToList();
            int returned = completed.FindIndex(row => row is { Kind: 0, Opcode: 24, Body: [59, 2, ..] });
            That(completed.Count(row => row is { Kind: 0, Opcode: 24 }) == 1 && returned >= 0 &&
                completed.FindIndex(row => row is { Kind: 0, Opcode: 601, Body.Length: 10 } && row.Body[1] == 1) > returned &&
                completed.FindIndex(row => row is { Kind: 0, Opcode: 600 }) > returned,
                "本体正式Run与无Run选图者都只在24回备战区后显示最终结果与奖励");
        }

        void AwaitReturn(int entryStart, string detail, byte area = 3)
        {
            bool Returned() => packets[0].Skip(entryStart).Any(row => row is { Kind: 0, Opcode: 24 } &&
                row.Body.Length >= 2 && row.Body[0] == 59 && row.Body[1] == area);
            for (int attempt = 0; attempt < 100 && !Returned(); attempt++)
            {
                AntonTcpProbe.Note(reports[0], Read(peers[0], "second phase automatic return"));
                if (!Returned()) Task.Delay(10).GetAwaiter().GetResult();
            }
            That(Returned(), detail);
        }

        async Task RejoinDuringRetry()
        {
            ushort character = peers[0].Id;
            var originalRoster = ReadRoster(peers[0], key);
            var instance = Guid.Parse((string)fixture.Query(
                "SELECT DISTINCT instance_id FROM character_raid_runs WHERE raid_type=3").Single()[0]!);
            string assets = Ledger();
            int connection = fixture.Events().Last(row => row.GetProperty("Kind").GetString() == "selected" &&
                row.GetProperty("Detail").GetProperty("Character").GetUInt16() == character)
                .GetProperty("Detail").GetProperty("Connection").GetInt32();
            peers[0].Dispose();
            var elapsed = Stopwatch.StartNew();
            bool disconnected = false;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                disconnected = fixture.Events().Any(row => row.GetProperty("Kind").GetString() == "disconnected" &&
                    row.GetProperty("Detail").GetProperty("Connection").GetInt32() == connection);
                if (disconnected && fixture.LukeRejoinCandidate(character) is { OldOwnerReleased: true }) break;
                await Task.Delay(10);
            }
            var candidate = fixture.LukeRejoinCandidate(character);
            That(disconnected && candidate is { Phase: 1, RaidSlot: 0, Squad: 1, OldOwnerReleased: true } &&
                candidate.Instance == instance && candidate.Key == key && Ledger() == assets,
                "二阶段等待期真实断线释放原owner，保留原实例、标记和四份一阶段收据，不重复扣票");
            AntonTcpProbe.Note(reports[1], Read(peers[1], "second phase member disconnected"));
            int eventBoundary = fixture.Events().Length;
            var fresh = Open(fixture, "preyteam0", gamePort: ChannelTcpTests.Endpoints(fixture)[42], channelNumber: 42);
            peers[0] = participants[0] = fresh;
            var notices = fresh.Select().Concat(Read(fresh, "second phase rejoin candidate")).ToList();
            That(fresh.Id == character && notices.Count(row => row is { Kind: 0, Opcode: 835, Body.Length: 7 } &&
                    U32(row.Body) == key) == 1 && notices.All(row => row.Opcode is not (9 or 594 or 587)) && Ledger() == assets,
                "同账号新连接收到一次原团835，668前不自动绑定、组队、重播飞鸟或改变已提交资产");
            int freshConnection = fixture.Events().Skip(eventBoundary).Last(row => row.GetProperty("Kind").GetString() == "selected" &&
                row.GetProperty("Detail").GetProperty("Character").GetUInt16() == character)
                .GetProperty("Detail").GetProperty("Connection").GetInt32();
            fresh.Send(668, Build(writer => writer.Write(key)));
            var rejoined = Read(fresh, "second phase raid restored");
            AntonTcpProbe.Note(reports[0], rejoined);
            Ack(rejoined, 668, [1], "二阶段原668成功恢复同一攻坚实例");
            int received = 0, bound = 0;
            elapsed.Restart();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                // ACK确认网络顺序；异步日志仍须等该新连接的实际收包和绑定记录落盘。
                var freshEvents = fixture.Events().Skip(eventBoundary).Where(row =>
                    row.GetProperty("Detail").TryGetProperty("Connection", out var owner) && owner.GetInt32() == freshConnection).ToArray();
                received = freshEvents.Count(row => row.GetProperty("Kind").GetString() == "recv" &&
                    row.GetProperty("Detail").GetProperty("Opcode").GetUInt16() == 668);
                bound = freshEvents.Count(row => row.GetProperty("Kind").GetString() == "luke_raid_rejoin" &&
                    row.GetProperty("Detail").GetProperty("Self").GetUInt16() == character &&
                    row.GetProperty("Detail").GetProperty("Key").GetString() == key.ToString("X8") &&
                    row.GetProperty("Detail").GetProperty("Phase").GetInt32() == 1 &&
                    row.GetProperty("Detail").GetProperty("Accepted").GetBoolean() &&
                    row.GetProperty("Detail").GetProperty("Reason").GetString() == "bound-without-wire");
                if (received == 1 && bound == 1) break;
                await Task.Delay(10);
            }
            That(freshConnection != connection && received == 1 && bound == 1,
                $"重返确认属于新连接唯一668及原团二阶段绑定，不借用旧连接或历史日志：recv668={received}, bound={bound}");
            int returned = rejoined.FindIndex(row => row is { Kind: 0, Opcode: 24, Body: [59, 2, ..] });
            That(returned >= 0 && rejoined.Count(row => row is { Kind: 0, Opcode: 594, Body.Length: 0 }) == 1 &&
                rejoined.FindIndex(row => row is { Kind: 0, Opcode: 594 }) > returned &&
                rejoined.Any(row => row is { Kind: 0, Opcode: 587, Body: [1, 1] }) &&
                rejoined.All(row => row is not { Kind: 0, Opcode: 9 or 28 or 29 or 600 or 601 }) &&
                Position(fixture, fresh) is { Town: 59, Area: 2 } && Ledger() == assets,
                "二阶段重返24先回候战再发一次飞鸟，以phase1准备状态等待原36，不恢复副本或发新奖励");
            var roster = ReadRoster(fresh, key);
            That(roster.Count == originalRoster.Count && originalRoster.All(row => roster.GetValueOrDefault(row.Key) == row.Value) &&
                RetryDeadline(fresh, character) == retryDeadline,
                "重返保留原两人标记、阶段通关账本及本体绝对期限，不重锚90秒等待");
            Board(fixture, fresh, towns, 3, boardingPhase: 1);
            That(ReadDisplayedState(fresh, key) == 2 && Position(fixture, fresh) is { Town: 59, Area: 3 } &&
                RetryDeadline(fresh, character) == retryDeadline,
                "二阶段重返者沿原36登船后恢复战斗显示，原重试期限继续生效");
            Board(fixture, fresh, towns, 5);
            fresh.Send(1361, Build(writer => { writer.Write((byte)1); writer.Write(key); }));
            var located = Read(fresh, "second phase rejoin side location");
            AntonTcpProbe.Note(reports[0], located);
            That(RosterLocations(located.Single(row => row is { Kind: 1, Opcode: 1361, Body: [1, 1, ..] }).Body)
                    .GetValueOrDefault(character) == 5 && Ledger() == assets,
                "重返后右侧地点投影跟随真实位置，原门票、库存、生命周期和收据保持");
            AntonTcpProbe.Note(reports[1], Read(peers[1], "second phase member rejoined"));

            string Ledger() => fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid") +
                fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
                fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,instance_id") +
                fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY character_id,instance_id,phase,kind");
        }

        uint RetryDeadline(TcpPeer peer, ushort character)
        {
            peer.Send(1361, Build(writer => { writer.Write((byte)1); writer.Write(key); }));
            var rows = Read(peer, "second phase member retry information");
            AntonTcpProbe.Note(reports[Array.IndexOf(peers, peer)], rows);
            byte[] body = rows.Single(row => row is { Kind: 1, Opcode: 1361, Body: [1, 1, ..] }).Body;
            int at = 7;
            uint result = 0;
            for (int member = 0; member < body[6]; member++)
            {
                int width = RaidMemberWidth(body, at);
                int name = checked((int)U32(body, at + 3));
                int count = checked((int)U32(body, at + 40 + name));
                int entry = at + 44 + name;
                for (int index = 0; index < count; index++, entry += 8)
                    if (U16(body, at) == character && U32(body, entry) == boss) result = U32(body, entry + 4);
                at += width;
            }
            That(at == body.Length, "成员重试表保留所有1361成员及完整变长尾部");
            return result;
        }

        void RejectRetry(string detail)
        {
            string before = RewardAssets();
            peers[0].Send(15, Build(writer => writer.Write((uint)boss)));
            var rejected = Read(peers[0], detail);
            AntonTcpProbe.Note(reports[0], rejected);
            Ack(rejected, 15, [0, 1, 0], detail + "沿原15拒绝");
            That(rejected.All(row => row is not { Kind: 0, Opcode: 28 or 29 or 31 or 115 or 600 or 601 }) &&
                before == RewardAssets() && Position(fixture, peers[0]) is { Town: 59, Area: 5 },
                "成员重试门禁不建立新Run、迁移本人或修改原库存、门票和奖励收据");
        }

        int ReportBossHp(uint value, bool reportZero = true)
        {
            int beforeHp = Symbols(reports[0].Symbols.Last())[roomHp];
            Report(662, Build(writer => { writer.Write(212u); writer.Write(value); writer.Write((byte)0); }));
            int reduced = Symbols(reports[0].Symbols.Last())[roomHp];
            That(reduced > 0 && reduced < beforeHp, "已加载本体Boss房的原9B662把房内HP降至真实报告值");
            foreach (uint repeated in new uint[] { value, 9000 })
            {
                Report(662, Build(writer => { writer.Write(212u); writer.Write(repeated); writer.Write((byte)0); }));
                That(Symbols(reports[0].Symbols.Last())[roomHp] == reduced, "重复及更高的662报告不抬升已记录本体HP");
            }
            Report(1614, Build(writer => writer.Write(25u)));
            That(Symbols(reports[0].Symbols.Last())[resources.Script.Symbols["[hp alarm]"]] == 25,
                "原4B1614只更新本团本体HP报警阈值");
            if (!reportZero) return reduced;
            Report(1613, new byte[4]);
            var alive = Symbols(reports[0].Symbols.Last());
            That(alive[roomHp] >= 1 && alive[roomHp] <= reduced && alive[worldHp] > 0 &&
                fixture.Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=3 AND phase1_event IS NOT NULL") is [[0L]],
                "原4B1613的0百分比仍保留至少1点房内HP，世界HP未归零且不提前签发二阶段终局");
            return alive[roomHp];

            void Report(ushort opcode, byte[] body)
            {
                peers[0].Send(opcode, body);
                for (int index = 0; index < peers.Length; index++)
                    AntonTcpProbe.Note(reports[index], Read(peers[index], "second phase HP report " + opcode));
            }
        }

        bool FirstPhaseDelivered() =>
            fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3 AND phase=0") is [[4L]] &&
            peers.Select((peer, index) => new byte[] { 0, 1 }.All(kind => packets[index].Any(row =>
                row is { Kind: 0, Opcode: 600, Body.Length: >= 6 } && row.Body[0] == kind && row.Body[1] == 1 &&
                U16(row.Body, 2) == peer.Id)) && packets[index].Any(row => row is { Kind: 0, Opcode: 587, Body: [2, 1] })).All(done => done);

        static void CheckActualReward(WirePacket display, TcpPeer peer) =>
            That(display is { Kind: 0, Opcode: 600, Body.Length: >= 6 } && display.Body[1] == 1 &&
                U16(display.Body, 2) == peer.Id && display.Body[4] == 0 && display.Body[5] > 0 &&
                display.Body.Length == 6 + 16 * display.Body[5], "普雷真实600保留本人、卡位及全部16B实际产物");

        string RewardAssets() => fixture.Snapshot() +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY character_id,instance_id,phase,kind");

        void AwaitAutomaticReward(int phase, Func<bool> delivered)
        {
            long start = clock.GetTimestamp();
            for (int tick = 0; tick < 10 && !delivered(); tick++)
            {
                clock.Advance(1);
                // 留出本批原自动事务与写帧时间；每秒只收一次，门禁冲突由下一个原秒tick重试。
                Task.Delay(25).GetAwaiter().GetResult();
                for (int index = 0; index < peers.Length; index++)
                    AntonTcpProbe.Note(reports[index], Read(peers[index], "automatic reward phase " + phase));
            }
            double seconds = clock.GetElapsedTime(start).TotalSeconds;
            int fallback = checked(resources.Script.Headers.Single(row => row.Tag == "[movie end time]").Values.Max(row => row.Number) + 120);
            Console.WriteLine($"Prey automatic reward phase {phase}: fakeSeconds={seconds:F0}, maxSeconds=10, fallbackSeconds={fallback}");
            That(seconds <= 10 && seconds < fallback, "自动奖励测试只推进至多10个原秒tick，不依赖电影及120秒离线兜底");
        }

        void RevealFinalPhase()
        {
            for (int index = 0; index < peers.Length; index++)
            {
                var peer = peers[index];
                var initial = packets[index].Skip(phaseOneStarts[index]).ToList();
                int result = initial.FindIndex(row => row is { Kind: 0, Opcode: 601, Body.Length: 10 } && row.Body[1] == 1);
                int movie = initial.FindIndex(row => row is { Kind: 0, Opcode: 587, Body: [3, 1] });
                That(result >= 0 && movie > result && initial.All(row => row.Opcode is not (599 or 600) &&
                    row is not { Kind: 0, Opcode: 587, Body: [4, 1] }),
                    "二阶段初始真实601后以5873/1播放CG，不提前发送599、牌面或state4");
                string beforeMovie = RewardAssets();
                peer.Send(660, [1, 0]);
                var premature = Read(peer, "second phase reward before movie");
                AntonTcpProbe.Note(reports[index], premature);
                That(premature.All(row => row is not { Kind: 1, Opcode: 660, Body: [1, ..] } && row.Opcode != 600) &&
                    RewardAssets() == beforeMovie, "影片完成前提前660不能成功、显示结果或变更角色、资产、审计和收据");
                // 一人验证Esc确认，另一人不请求跳过，直接沿原生影片结束659=1继续。
                if (index != 0) continue;
                peer.Send(659, [0]);
                var skipped = Read(peer, "second phase movie skip");
                AntonTcpProbe.Note(reports[index], skipped);
                Ack(skipped, 659, [1], "真实6590回复原单字节成功");
                That(skipped.Count(row => row is { Kind: 0, Opcode: 599, Body.Length: 0 }) == 1 &&
                    skipped.FindIndex(row => row is { Kind: 0, Opcode: 599 }) > skipped.FindIndex(row => row is { Kind: 1, Opcode: 659 }) &&
                    skipped.All(row => row.Opcode != 600 && row is not { Kind: 0, Opcode: 587, Body: [4, 1] }) &&
                    RewardAssets() == beforeMovie,
                    "实际6590后才发送599跳过，不能提前开牌或完成影片");
            }
            string beforeCompletion = RewardAssets();
            peers[0].Send(659, [1]);
            var firstEnded = Read(peers[0], "first member movie ended");
            AntonTcpProbe.Note(reports[0], firstEnded);
            Ack(firstEnded, 659, [1], "真实6591记录首名成员电影完成");
            That(firstEnded.All(row => row.Opcode != 600 && row is not { Kind: 0, Opcode: 587, Body: [4, 1] }) &&
                RewardAssets() == beforeCompletion,
                "全团冻结结果未交付齐全时只确认6591，不预发空表或开启锁存结果窗口");
            AwaitAutomaticReward(1, () => AllDelivered() && Displayed(0));
            That(AllDelivered() && Displayed(0),
                "首名成员电影完成后沿原秒tick自动链交付全团真实权益，准备完整表后为本人开窗");
            That(packets[1].Skip(phaseOneStarts[1]).All(row => row.Opcode != 600 &&
                    row is not { Kind: 0, Opcode: 587, Body: [4, 1] }) && ReadDisplayedState(peers[0], key) == 3 &&
                ReadDisplayedState(peers[1], key) == 3,
                "另一成员资产已提交但CG未完时不发送其个人/团队结果或开窗，全团仍保持Reward而未终局");
            string committed = RewardAssets();
            peers[0].Send(659, [1]);
            var repeated = Read(peers[0], "first member movie ended replay");
            AntonTcpProbe.Note(reports[0], repeated);
            Ack(repeated, 659, [1], "全团未终局时重复6591回放本人完成确认");
            That(repeated.All(row => row.Opcode is not (599 or 600) && row is not { Kind: 0, Opcode: 587, Body: [4, 1] }) &&
                RewardAssets() == committed, "重复电影完成不重开本人窗口、不重复发送结果或提交资产收据");

            peers[1].Send(659, [1]);
            var secondEnded = Read(peers[1], "second member movie ended");
            AntonTcpProbe.Note(reports[1], secondEnded);
            Ack(secondEnded, 659, [1], "未请求跳过的第二名成员以原6591确认自然播放结束");
            That(Displayed(1) && RewardAssets() == committed,
                "第二名成员结束CG后立即接收本人真实结果和完整全团表再开窗，不再次发放资产");
            foreach (int index in Enumerable.Range(0, peers.Length))
            {
                var shown = packets[index].Skip(phaseOneStarts[index]).ToList();
                int opened = shown.FindIndex(row => row is { Kind: 0, Opcode: 587, Body: [4, 1] });
                var personal = shown.Where(row => row is { Kind: 0, Opcode: 600, Body.Length: >= 6 } && row.Body[0] is 0 or 1).ToArray();
                That(personal.Length == 1 && personal[0].Body[0] == 1 &&
                    shown.Where(row => row is { Kind: 0, Opcode: 600 }).All(row => shown.IndexOf(row) < opened),
                    "本人合计结果只用一份wire1并先于唯一5874/1，避免同owner/itemId被后包覆盖");
                CheckActualReward(personal[0], peers[index]);
                Bytes(personal[0].Body, Build(writer =>
                {
                    writer.Write((byte)1); writer.Write((byte)1);
                    writer.Write(ExpectedRow(peers[index].Id, personal: true));
                }), "个人wire1按SQL冻结全部offers合计同ID数量，其余16B列保留实际末项");
                var team = TeamResults(shown);
                foreach (int owner in Enumerable.Range(0, peers.Length))
                    Bytes(team[peers[owner].Id], ExpectedRow(peers[owner].Id, personal: false),
                        "wire3逐owner只展示SQL冻结Kind0原始完整行，不混入个人合计或固定奖");
            }
            string completed = RewardAssets();
            clock.Advance(1);
            Collect("second phase completed replay");
            for (int index = 0; index < peers.Length; index++)
            {
                peers[index].Send(1361, Build(writer => { writer.Write((byte)0); writer.Write(key); }));
                var final = Read(peers[index], "second phase completed information");
                AntonTcpProbe.Note(reports[index], final);
                That(RaidInformationState(final.Single(row => row is { Kind: 1, Opcode: 1361, Body: [1, 0, ..] }).Body, key) == 4,
                    "两人都实际收到完整结果并结束CG后，原团记录进入业务Completed终态");
            }
            That(peers.Select((_, index) => packets[index].Skip(phaseOneStarts[index]).Count(row =>
                row is { Kind: 0, Opcode: 587, Body: [4, 1] }) == 1).All(once => once) && RewardAssets() == completed &&
                packets.All(rows => rows.All(row => row is not { Kind: 1, Opcode: 660, Body: [1, ..] })),
                "自动交付后的业务Completed4不重开原生窗口或重复资产，正式揭示流程无需660");

            bool AllDelivered() => fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3") is [[8L]] &&
                peers.All(peer => fixture.Query(
                    "SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3 AND phase=1 AND character_id=$p0", peer.Id) is [[2L]]);
            bool Displayed(int index) =>
                packets[index].Skip(phaseOneStarts[index]).Any(row =>
                    row is { Kind: 0, Opcode: 600, Body.Length: >= 6 } && row.Body[0] == 1 && row.Body[1] == 1 &&
                    U16(row.Body, 2) == peers[index].Id) && packets[index].Skip(phaseOneStarts[index]).Any(row =>
                    row is { Kind: 0, Opcode: 587, Body: [4, 1] }) &&
                TeamResults(packets[index].Skip(phaseOneStarts[index])).Keys.ToHashSet().SetEquals(peers.Select(peer => peer.Id));
            void Collect(string action)
            {
                for (int index = 0; index < peers.Length; index++)
                    AntonTcpProbe.Note(reports[index], Read(peers[index], action));
            }
            byte[] ExpectedRow(ushort character, bool personal)
            {
                string json = (string)fixture.Query(
                    "SELECT phase1_receipt FROM character_raid_runs WHERE raid_type=3 AND character_id=$p0", character).Single()[0]!;
                using var document = JsonDocument.Parse(json);
                var offers = document.RootElement.GetProperty("Offers").EnumerateArray().ToArray();
                var source = personal ? offers : offers.Where(offer => offer.GetProperty("Kind").GetByte() == 0).ToArray();
                var items = new List<byte[]>();
                var itemPositions = new Dictionary<uint, int>();
                foreach (var item in source.SelectMany(offer => offer.GetProperty("PreyDisplay").EnumerateArray()))
                {
                    uint id = item.GetProperty("ItemId").GetUInt32();
                    byte[] encoded = Build(writer =>
                    {
                        writer.Write(item.GetProperty("RawCode").GetByte()); writer.Write(id);
                        writer.Write(item.GetProperty("DisplayNumber").GetUInt16());
                        writer.Write(item.GetProperty("RawFlag").GetByte());
                        writer.Write(item.GetProperty("RawTail1").GetUInt32());
                        writer.Write(item.GetProperty("EnchantUpgrade").GetUInt32());
                    });
                    if (personal && itemPositions.TryGetValue(id, out int position))
                    {
                        ushort count = checked((ushort)(U16(items[position], 5) + U16(encoded, 5)));
                        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(5), count);
                        items[position] = encoded;
                    }
                    else
                    {
                        itemPositions[id] = items.Count;
                        items.Add(encoded);
                    }
                }
                return Build(writer =>
                {
                    writer.Write(character);
                    writer.Write(personal ? (byte)0 : source.Single().GetProperty("Card").GetByte());
                    writer.Write(checked((byte)items.Count));
                    foreach (var item in items) writer.Write(item);
                });
            }
            static Dictionary<ushort, byte[]> TeamResults(IEnumerable<WirePacket> rows)
            {
                var results = new Dictionary<ushort, byte[]>();
                foreach (var row in rows.Where(row => row is { Kind: 0, Opcode: 600, Body.Length: >= 2 } && row.Body[0] == 3))
                {
                    That(row.Body[1] == 2, "wire3必须一次给完整两名已提交结果，不能部分表增量填充锁存窗口");
                    int at = 2;
                    for (int member = 0; member < row.Body[1]; member++)
                    {
                        That(at + 4 <= row.Body.Length, "wire3全团结果保留owner、卡位与产物数量");
                        int width = 4 + 16 * row.Body[at + 3];
                        That(at + width <= row.Body.Length, "wire3全团结果保留完整16B产物行");
                        results[U16(row.Body, at)] = row.Body.AsSpan(at, width).ToArray();
                        at += width;
                    }
                    That(at == row.Body.Length, "wire3完整消费全团结果，无截断或残余字段");
                }
                return results;
            }
        }

        static string? Type(RaidDungeon dungeon) => dungeon.Declarations.FirstOrDefault(field => field.Tag == "[type]")?.Values.FirstOrDefault()?.Text;
    }

    static void FinishLoadedDungeon(TcpPeer peer, RaidDungeonCatalog catalog, int dungeon, byte[] info, byte[] initialMap,
        AntonTcpProbe.AntonProbeReport report, Action? beforeBossClear = null, bool leave = true, bool clearBoss = true)
    {
        byte[] map = initialMap;
        var path = AntonTcpProbe.Route(catalog, dungeon, info[7], map[0], map[1], info[8], info[9]);
        ushort bossKey = 0; byte bossLevel = 0;
        int confirmations = report.Opcode115, permissions = report.Opcode31;
        for (int step = 0; step < path.Length; step++)
        {
            if (step != 0)
            {
                var move = new byte[100]; move[0] = checked((byte)path[step].X); move[1] = checked((byte)path[step].Y);
                peer.Send(45, move);
                var moved = Read(peer, "boss route move"); AntonTcpProbe.Note(report, moved);
                map = moved.Single(row => row is { Kind: 0, Opcode: 29 }).Body;
                peer.Send(37, new byte[8]);
                var loaded = Read(peer, "boss route load"); AntonTcpProbe.Note(report, loaded);
                Ack(loaded, 37, [1], "原路径房间实际加载");
            }
            if (step + 1 == path.Length)
            {
                beforeBossClear?.Invoke();
                if (!clearBoss) return;
            }
            AntonTcpProbe.ClearRoom(peer, map, report, out ushort key, out byte level, step + 1 == path.Length,
                requireSettlementOrder: true);
            if (step + 1 == path.Length) { bossKey = key; bossLevel = level; }
        }
        That(report.Opcode115 == confirmations + 1 && report.Opcode31 == permissions + 1,
            "当前正式Run真实39死亡恰好签发一次115与31，不从HP报告推定死亡");
        var confirmation = new byte[39];
        BinaryPrimitives.WriteUInt16LittleEndian(confirmation, peer.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(confirmation.AsSpan(2), bossKey);
        BinaryPrimitives.WriteUInt32LittleEndian(confirmation.AsSpan(13), bossLevel);
        BinaryPrimitives.WriteUInt64LittleEndian(confirmation.AsSpan(17), 504);
        peer.Send(117, confirmation);
        var replay = Read(peer, "boss confirmation replay"); AntonTcpProbe.Note(report, replay);
        That(replay.All(row => row is not { Kind: 0, Opcode: 115 or 31 }), "原117沿当前Run事实幂等回放，不重复签发死亡许可");
        if (leave)
        {
            peer.Send(46, DungeonSettlementProtocolTests.Report(peer.Id, 80));
            var returned = Read(peer, "cleared raid native result return");
            AntonTcpProbe.Note(report, returned);
            That(returned.Any(row => row is { Kind: 0, Opcode: 24, Body: [59, 3, ..] } && U16(row.Body, 4) == peer.Id) &&
                returned.All(row => row.Opcode is not (34 or 35 or 115 or 31 or 600 or 601)),
                "清图后原53B46直接回鸟背中间，不重发死亡许可或普通及阶段奖励");
        }
    }
}
