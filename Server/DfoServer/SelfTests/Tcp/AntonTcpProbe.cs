using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Persistence;
using PvfLib;

namespace DfoServer.Testing;

// 连真实监听的安徒恩假客户端。默认不连接。进图与清图复用 TcpPeer 帧和正式副本的 39/45/117。
internal static partial class AntonTcpProbe
{
    // pcap-manual-1235：GAME RECV opcode 0x0010，25 字节，末尾 u32 = 0xFFFFFFFF。
    internal static readonly byte[] LiveSelect210 = Convert.FromHexString("D20000000200000000FFFF00000000000000000000FFFFFFFF");
    public const string Usage = """
        用法：DfoServer [运行参数] --anton-probe [探针参数]
          不带 --host 时只打印本说明，不连接、不监听。
          --host <IPv4>              目标地址。给出后才会连接
          --channel-port <端口>      频道端口。连接时必填
          --game-port <端口>         可选。必须与目录里频道 19 的入口一致；省略则用目录端口
          --account <账号>           连接或 --seed 时必填
          --digest <32位hex>         登录摘要，默认与自测 NativeLoginWire.Digest 相同
          --character <角色名>       默认 AntProbe
          --channel <号>             默认 19
          --server-log <server.jsonl>  跑完后核对该文件里的 210 / anton_phase_progress / 586 与本轮596/597
          --stop-after start|enter|phase   默认 phase
          --steps start,enter,phase,reward,card  按顺序选择步骤；自动执行前置步骤，与 --stop-after 互斥
                                      reward 检查重复清图；card 检查正式资源奖励、幂等回放及阶段1推进
          --seed                     只在 --data-dir 写入 85 级角色、两张 10096296 和 raid-start.json，不连接
        服务端进程的 --game-port 是频道 10。频道 19 的入口在频道目录里，不要把这两个端口当成同一个。
        隔离服示例（本轮不要执行）：
          DfoServer --pvf-path <Script.pvf> --anton-probe --host 127.0.0.1 --channel-port 23100 --account antonprobe --character AntProbe --server-log <隔离服 Logs/server.jsonl>
        """;

    public static bool IsUsageOnly(string[] args) => args.Length == 0 || args is ["--help"] or ["-h"];

    public static int Run(ServerOptions options, string[] args)
    {
        ProbeArgs parsed;
        try { parsed = ProbeArgs.Parse(args); }
        catch (InvalidDataException error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (parsed.Seed)
            return Seed(options, parsed);
        try
        {
            int gamePort = ResolveGamePort(parsed.Host!, parsed.ChannelPort!.Value, parsed.Channel);
            if (parsed.GamePort is { } pinned && pinned != gamePort)
                throw new InvalidOperationException($"目录里的频道 {parsed.Channel} 端口是 {gamePort}，与 --game-port {pinned} 不一致");
            Console.WriteLine($"channel {parsed.Channel} game port {gamePort}");
            using var peer = Login(parsed.Host!, gamePort, parsed.Account!, parsed.Digest, parsed.Channel, out byte[] roster);
            Select(peer, roster, parsed.Character!);
            using var archive = PvfArchive.Open(options.PvfPath);
            var catalog = EntryCatalog(archive);
            var report = Play(peer, catalog, parsed.StopAfter);
            if (parsed.ServerLog is { } log)
                AssertLog(log, report.StartedAt, report);
            Console.WriteLine(report.Summary());
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine("FAIL " + error.Message +
                "；检查本次隔离 Host 的频道目录、角色票/分队及 raid-start.json 开关，按对应 opcode 查询 server.jsonl。");
            return 1;
        }
    }

    internal static RaidDungeonCatalog EntryCatalog(PvfArchive archive)
    {
        var main = DungeonCatalog.Load(archive);
        var items = new ItemResources(archive);
        return RaidCatalog.Load(archive).PrepareResources(archive, main, items.Items,
            antonMapPath: AntonRaidMapPaths.Bind(main.MapPath)).Find(0)!.Resources!.EntryCatalog!;
    }

    internal static int ResolveGamePort(string host, int channelPort, byte channel)
    {
        using var client = new TcpClient();
        client.ConnectAsync(host, channelPort).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        var stream = client.GetStream();
        stream.ReadTimeout = 5000;
        WriteChannel(stream, 11, new byte[32]);
        _ = ReadChannel(stream, 12);
        WriteChannel(stream, 9, []);
        _ = ReadChannel(stream, 10);
        WriteChannel(stream, 17, new byte[24]);
        byte[] directory = DecodeChannel(ReadChannel(stream, 18));
        int count = BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(8));
        string wanted = "#ch." + channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < count; i++)
        {
            int at = 12 + i * 64;
            string name = Encoding.ASCII.GetString(directory, at, 20).TrimEnd('\0');
            int port = BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(at + 60));
            if (name == wanted)
            {
                Console.WriteLine($"channel directory {name} -> {Encoding.ASCII.GetString(directory, at + 28, 32).TrimEnd('\0')}:{port}");
                return port;
            }
        }
        throw new InvalidOperationException("频道目录没有 " + wanted);
    }

    internal static TcpPeer Login(string host, int gamePort, string account, string digest, byte channel, out byte[] roster)
    {
        roster = [];
        var peer = new TcpPeer(gamePort, channelNumber: channel, serverAddress: host);
        try
        {
            var hello = peer.Read();
            if (hello is not { Kind: 0, Opcode: 1 })
                throw new InvalidOperationException($"登录问候不是 NOTI 1，而是 {hello.Kind}/{hello.Opcode}");
            peer.Send(1562, NativeLoginWire.Precheck(account));
            var precheck = peer.Read();
            if (precheck is not { Kind: 1, Opcode: 1562, Body: [1] })
                throw new InvalidOperationException("1562 预检未被接受：" + Hex(precheck.Body));
            peer.Send(1, NativeLoginWire.Login(account, digest));
            _ = peer.Read();
            roster = peer.Read().Body;
            if (roster.Length < 3 || roster[0] != 2)
                throw new InvalidOperationException("登录后没有名册 NOTI 2");
            return peer;
        }
        catch
        {
            peer.Dispose();
            throw;
        }
    }

    internal static void Select(TcpPeer peer, byte[] roster, string character)
    {
        ushort slot = FindSlot(roster, character);
        peer.Send(4, TcpPeer.Build(w => w.Write(slot)));
        var rows = Flush(peer, "选角");
        var ack = rows.FirstOrDefault(p => p is { Kind: 1, Opcode: 4 });
        if (ack is null || ack.Body.Length < 11 || ack.Body[0] != 1)
            throw new InvalidOperationException("选角失败：" + (ack is null ? "没有 ACK4" : Hex(ack.Body)));
        peer.Id = TcpPeer.U16(ack.Body, 9);
        Console.WriteLine($"selected slot {slot} id {peer.Id}");
    }

    internal static AntonProbeReport Play(TcpPeer peer, RaidDungeonCatalog catalog, string stopAfter = "phase")
    {
        var report = new AntonProbeReport();
        var elapsed = Stopwatch.StartNew();
        peer.Send(833, Search(""));
        _ = Flush(peer, "833 寻找队");
        peer.Send(664, TcpPeer.Build(w => { w.Write((byte)0); w.Write(TcpPeer.Name("Probe")); w.Write(new byte[] { 0, 0, 0, 7 }); }));
        var created = Flush(peer, "664 建队");
        if (created.Any(p => p is { Kind: 1, Opcode: 664, Body: [0, ..] }))
            throw new InvalidOperationException("664 被拒绝：" + Hex(created.Last(p => p.Opcode == 664).Body));
        peer.Send(669, TcpPeer.Build(w => { w.Write(0u); w.Write((uint)peer.Id); w.Write(1u); }));
        var squad = Flush(peer, "669 分队");
        if (!squad.Any(p => p is { Kind: 1, Opcode: 669, Body: [1] }))
            throw new InvalidOperationException("669 未确认编入第一分队");
        peer.Send(666);
        var started = Flush(peer, "666 开战");
        var startAck = started.LastOrDefault(p => p is { Kind: 1, Opcode: 666 });
        if (startAck is not { Body: [1, ..] })
            throw new InvalidOperationException("666 未成功：" + (startAck is null ? "没有 ACK" : Hex(startAck.Body)));
        var place = started.LastOrDefault(p => p is { Kind: 0, Opcode: 23 } && p.Body.Length >= 4 && TcpPeer.U16(p.Body) == peer.Id);
        if (place is null || place.Body[2] != 19 || place.Body[3] != 2)
            throw new InvalidOperationException("666 后没有落在 19/2");
        // 开战已消费的583也归入同一报告，供初值回归读取实际接收包。
        Note(report, started);
        report.Started = true;
        FinishStep(report, elapsed, "start", "833/664/669/666 town=19/2");
        if (stopAfter == "start")
            return report;
        RejectTail(peer, 210);
        Enter(peer, catalog, 210, report, clear: stopAfter != "enter", requireClearNotice: stopAfter != "clear210");
        report.Entered210 = true;
        FinishStep(report, elapsed, "enter", "15/16/28/29 dungeon=210 Unknown21=FFFFFFFF");
        if (stopAfter == "enter")
            return report;
        if (stopAfter == "clear210")
        {
            ExpectClosed(peer, 212);
            ExpectClosed(peer, 216);
            if (report.Reward || report.Opcode586 != 0 || report.Opcode596 != 0 || report.Opcode597 != 0 || report.Opcode601 != 0)
                throw new InvalidOperationException("关闭阶段推进后清 210 仍推进了战况：" + report.Summary());
            return report;
        }
        for (int clear = 1; clear < 4; clear++)
            Enter(peer, catalog, 210, report, clear: true);
        ExpectClosed(peer, 211);
        foreach (int dungeon in new[] { 212, 214, 213, 215 })
            for (int clear = 0; clear < (dungeon is 213 or 215 ? 2 : 1); clear++)
                Enter(peer, catalog, dungeon, report, clear: true, leave: dungeon != 215 || clear == 0);
        if (!report.Reward)
            throw new InvalidOperationException("213/215 清完后没有见到 Reward 态 587 [3,0]");
        if (report.Opcode586 == 0)
            throw new InvalidOperationException("阶段推进没有 586");
        if (report.Opcode596 == 0 || report.Opcode597 == 0 || report.Opcode601 != 1)
            throw new InvalidOperationException($"不应发送 596/597/601，实际 596={report.Opcode596} 597={report.Opcode597} 601={report.Opcode601}");
        FinishStep(report, elapsed, "phase", "39/45/117/586/587 state=Reward phase=0");
        if (stopAfter is "reward" or "card")
        {
            StressReward(peer, report, report.LastClearCheck!);
            FinishStep(report, elapsed, "reward", "117 duplicate; 16 dungeon=216 rejected; no 600/601");
        }
        if (stopAfter == "card")
        {
            peer.Send(659, [1]); Note(report, Flush(peer, "影片完成"));
            // 未配置牌种和越界槽位不进入实际资产链。
            foreach (byte[] rejected in new byte[][] { [byte.MaxValue, 0], [1, byte.MaxValue] })
            {
                peer.Send(660, rejected);
                var packets = Flush(peer, "660 未配置牌面 " + Hex(rejected));
                Note(report, packets);
                if (packets.Any(packet => packet.Opcode is 600 or 601 or 660 or 587))
                    throw new InvalidOperationException("未配置的660不得发布结果、成功ACK或推进阶段");
                report.RejectedCards++;
            }
            // 原生1后0：团队1实际提交后服务器自动提交小队3，0只确认相同原牌背。
            bool? available = null;
            bool advanced = false;
            foreach (byte[] request in new byte[][] { [1, 0], [1, 0], [0, 0], [0, 0] })
            {
                peer.Send(660, request);
                var cards = Flush(peer, "660 正式翻牌 " + Hex(request));
                Note(report, cards);
                bool accepted = cards.Any(packet => packet is { Kind: 1, Opcode: 660, Body: [1] });
                available ??= accepted;
                if (accepted != available || !accepted && cards.Any(packet => packet.Opcode is 600 or 601 or 660))
                    throw new InvalidOperationException("660正式策略状态不一致，或未提交却返回了结果包");
                if (accepted && cards.Any(packet => packet.Kind == 1 && packet.Opcode == 660 && !packet.Body.SequenceEqual(new byte[] { 1 })))
                    throw new InvalidOperationException("ACK660成功应只有01，不能追加奖励业务体");
                if (cards.Any(packet => packet.Opcode == 601))
                    throw new InvalidOperationException("660领取不能重复发送阶段601");
                foreach (var display in cards.Where(packet => packet is { Kind: 0, Opcode: 600 }))
                {
                    if (display.Body is [0, 0] && request[0] == 0) continue;
                    if (display.Body.Length < 6 || display.Body[0] != request[0] && display.Body[0] != 3 ||
                        display.Body[1] != 1 || display.Body.Length != 6 + 16 * display.Body[5])
                        throw new InvalidOperationException("普通20人团600必须使用完整16B物品列，自动小队结果固定kind3");
                    if (!accepted || display.Body[0] != 3 && cards.IndexOf(display) >= cards.FindIndex(packet => packet is { Kind: 1, Opcode: 660 }))
                        throw new InvalidOperationException("手动600必须在资产提交后的ACK660之前写出，内部小队3随后发布");
                }
                if (advanced && cards.Any(packet => packet.Opcode == 601 || packet.Opcode == 600 && packet.Body.Length != 2))
                    throw new InvalidOperationException("旧阶段660回放不能向下一阶段窗口发布600/601");
                advanced |= cards.Any(packet => packet is { Kind: 0, Opcode: 587, Body: [5, 0] });
            }
            report.CardUnavailable = available != true;
            FinishStep(report, elapsed, "card", report.CardUnavailable
                ? "UNIMPLEMENTED: no configured offer; no success ACK/600/601; connection alive"
                : "660 (1,0)/(0,0) + duplicates committed/replayed; ACK=01; 600=frozen grants; 601=phase statistics");
        }
        peer.Send(42);
        Note(report, Flush(peer, "42 Reward 返回候战室"));
        return report;
    }

    static void FinishStep(AntonProbeReport report, Stopwatch elapsed, string step, string detail)
    {
        report.Steps.Add(step);
        Console.WriteLine($"STEP {step} elapsedMs={elapsed.Elapsed.TotalMilliseconds:F1} {detail}");
        elapsed.Restart();
    }

    internal static string StopAfterForSteps(string value)
    {
        string[] allowed = ["start", "enter", "phase", "reward", "card"];
        int last = -1;
        foreach (string step in value.Split(',', StringSplitOptions.TrimEntries))
        {
            int index = Array.IndexOf(allowed, step);
            if (index < 0 || index <= last)
                throw new InvalidDataException("--steps 只接受按 start,enter,phase,reward,card 顺序排列的不重复步骤");
            last = index;
        }
        return allowed[last];
    }

    static void RejectTail(TcpPeer peer, int dungeon)
    {
        peer.Send(15, DungeonId(dungeon));
        var opened = Flush(peer, "15 选图 " + dungeon);
        if (!opened.Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }))
            throw new InvalidOperationException("15 没有打开选图");
        byte[] bad = (byte[])LiveSelect210.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(21), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bad, (uint)dungeon);
        peer.Send(16, bad);
        var rejected = Flush(peer, "16 Unknown21=1");
        if (rejected.Any(p => p.Opcode is 28 or 29))
            throw new InvalidOperationException("Unknown21=1 仍发布了 28/29");
        var ack = rejected.LastOrDefault(p => p is { Kind: 1, Opcode: 16 });
        if (ack is not { Body: [0, 9, 0, 0] })
            throw new InvalidOperationException("Unknown21=1 的拒绝不是 [0,9,0,0]：" + (ack is null ? "没有 ACK16" : Hex(ack.Body)));
    }

    static void ExpectClosed(TcpPeer peer, int dungeon)
    {
        peer.Send(15, DungeonId(dungeon));
        var opened = Flush(peer, "15 关闭图 " + dungeon);
        if (opened.Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }))
            throw new InvalidOperationException(dungeon + " 仍是 open，15 被接受");
    }

    internal static void Enter(TcpPeer peer, RaidDungeonCatalog catalog, int dungeon, AntonProbeReport report, bool clear,
        bool leave = true, bool requireClearNotice = true, bool allowPhaseReward = false, Action<byte[]>? beforeRoomClear = null)
    {
        bool fiendwar = IsFiendwarDungeon(dungeon);
        if (!fiendwar) peer.Send(15, DungeonId(dungeon));
        List<WirePacket> opened = fiendwar ? [] : Flush(peer, "15 选图 " + dungeon);
        if (!fiendwar && !opened.Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }))
            throw new InvalidOperationException(dungeon + " 的 15 未被接受");
        byte[] request = (byte[])LiveSelect210.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)dungeon);
        if (!fiendwar) peer.Send(16, request);
        // 自然36的收包屏障已完成FWL1；不能补15/16把缺失入口伪装成通过。
        var entered = fiendwar ? TakeFiendwarEntry(peer, dungeon) : Flush(peer, "16 进图 " + dungeon);
        if (fiendwar && !entered.Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }))
            throw new InvalidOperationException(dungeon + " 的握手后15未被接受");
        Note(report, entered);
        var info = entered.LastOrDefault(p => p is { Kind: 0, Opcode: 28 } && p.Body.Length >= 10 && TcpPeer.U32(p.Body) == (uint)dungeon);
        var map = entered.LastOrDefault(p => p is { Kind: 0, Opcode: 29 } && p.Body.Length >= 19);
        if (info is null || map is null)
            throw new InvalidOperationException(dungeon + " 没有 28/29：" + string.Join(',', entered.Select(p => p.Kind + ":" + p.Opcode)));
        if (!clear)
            return;
        int confirmationsBefore = report.Opcode115, permissionsBefore = report.Opcode31;
        bool formalSettles = requireClearNotice &&
            (catalog.Type == 3 || catalog.Type == 0 && dungeon is >= 210 and <= 224);
        peer.Send(37, new byte[8]);
        Note(report, Flush(peer, "37 加载"));
        var path = Route(catalog, dungeon, info.Body[7], map.Body[0], map.Body[1], info.Body[8], info.Body[9]);
        beforeRoomClear?.Invoke(map.Body);
        ClearRoom(peer, map.Body, report, out ushort bossKey, out byte bossLevel, path[0].X == info.Body[8] && path[0].Y == info.Body[9],
            requireSettlementOrder: formalSettles);
        for (int i = 1; i < path.Length; i++)
        {
            byte[] move = new byte[100];
            move[0] = checked((byte)path[i].X);
            move[1] = checked((byte)path[i].Y);
            peer.Send(45, move);
            var moved = Flush(peer, $"45 -> {path[i].X},{path[i].Y}");
            Note(report, moved);
            map = moved.LastOrDefault(IsRoomMap)
                ?? throw new InvalidOperationException($"{dungeon} 移动到 {path[i].X},{path[i].Y} 没有 29");
            peer.Send(37, new byte[8]);
            Note(report, Flush(peer, "37 下一房"));
            beforeRoomClear?.Invoke(map.Body);
            ClearRoom(peer, map.Body, report, out ushort nextBoss, out byte nextLevel, i + 1 == path.Length,
                requireSettlementOrder: formalSettles);
            if (dungeon is 213 or 215 && i == 1)
            {
                // strongleg_lower/lower2.dgn 的第二房不是(0,0)终点；已死的有效房内怪也不能充当最终Boss。
                var early = new byte[39];
                BinaryPrimitives.WriteUInt16LittleEndian(early, peer.Id);
                BinaryPrimitives.WriteUInt16LittleEndian(early.AsSpan(2), TcpPeer.U16(map.Body, 25));
                BinaryPrimitives.WriteUInt32LittleEndian(early.AsSpan(13), map.Body[31]);
                BinaryPrimitives.WriteUInt64LittleEndian(early.AsSpan(17), 504);
                peer.Send(117, early);
                var premature = Flush(peer, "擎天第二房伪终末 " + dungeon);
                if (premature.Any(p => p is { Kind: 0, Opcode: 31 or 115 or 586 or 587 or 600 or 601 }))
                    throw new InvalidOperationException("擎天第二房117被误认为单图或阶段终末");
            }
            if (i + 1 == path.Length)
            {
                bossKey = nextBoss;
                bossLevel = nextLevel;
            }
        }
        byte[] check = new byte[39];
        BinaryPrimitives.WriteUInt16LittleEndian(check, peer.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(check.AsSpan(2), bossKey);
        BinaryPrimitives.WriteUInt32LittleEndian(check.AsSpan(13), bossLevel);
        BinaryPrimitives.WriteUInt64LittleEndian(check.AsSpan(17), 504);
        bool tremor = dungeon is 212 or 214;
        if (tremor)
        {
            bool passive = TremorUsesPassiveTerminal(catalog, dungeon, info.Body[7]);
            // 当前资源可显式声明15467；空条件走终点加载，有物件条件则由首次合法终末提交一次许可。
            peer.Send(39, QuestPassiveClearTcpTests.PassiveDeath(15467));
            var late = Flush(peer, "15467原资源终末分支");
            Note(report, late);
            if (late.Count(packet => packet is { Kind: 0, Opcode: 31 }) != (passive ? 1 : 0) ||
                late.Any(packet => packet is { Kind: 0, Opcode: 115 }))
                throw new InvalidOperationException("震颤首次15467许可须匹配真实ClearConditions且不伪造Boss确认");
            peer.Send(39, QuestPassiveClearTcpTests.PassiveDeath(15467));
            var duplicate = Flush(peer, "通关后重复15467");
            Note(report, duplicate);
            if (duplicate.Any(packet => packet is { Kind: 0, Opcode: 31 or 115 }))
                throw new InvalidOperationException("震颤通关后的15467不能再发31或伪造115");
        }
        if (formalSettles)
            RequireRunSettlement(report, confirmationsBefore, permissionsBefore, dungeon);
        // 震颤由全员终点加载结算，不补造key0的117。
        if (!tremor) peer.Send(117, check);
        int seen = report.Opcode586;
        var cleared = Flush(peer, (tremor ? "终点加载结算 " : "117 确认 ") + dungeon);
        Note(report, cleared);
        // 正式Run已随39发送许可，117仅协调重放；未启用正式Run的诊断保持原确认合同。
        if (cleared.Any(p => p is { Kind: 0, Opcode: 31 or 34 or 35 } or { Kind: 1, Opcode: 72 }) ||
            !allowPhaseReward && cleared.Any(p => p is { Kind: 0, Opcode: 600 or 601 }))
            throw new InvalidOperationException(dungeon + " 的117误发普通结算、阶段牌面或未请求的EPLP确认");
        if (formalSettles && cleared.Any(packet => packet is { Kind: 0, Opcode: 115 }))
            throw new InvalidOperationException(dungeon + " 的117重发了已提交Run的Boss确认");
        if (requireClearNotice && !formalSettles && report.Opcode586 == seen && !cleared.Any(p => p is { Kind: 0, Opcode: 115 or 586 }))
            throw new InvalidOperationException(dungeon + " 的清图没有 586，117 也没有确认");
        if (cleared.Any(p => p is { Kind: 0, Opcode: 587, Body: [3, 0] }))
            report.Reward = true;
        report.LastClearCheck = check;
        if (leave)
        {
            peer.Send(42);
            Note(report, Flush(peer, "42 返回候战室"));
        }
    }

    internal static void ClearRoom(TcpPeer peer, byte[] map, AntonProbeReport report, out ushort bossKey, out byte bossLevel, bool bossRoom,
        TcpPeer? witness = null, AntonProbeReport? witnessReport = null, bool requireSettlementOrder = false)
    {
        bossKey = 0;
        bossLevel = 0;
        // 已清起点回访使用16B缓存29，不含演员表；不能要求重生或再次上报死亡。
        if (map.Length == 16 && map[13] == 0) return;
        int count = map[18];
        if (map.Length < 19 + count * 22)
            throw new InvalidOperationException("29 演员表被截断");
        // 震颤终点由已提交双支路击杀和37加载完成；假客户端不能击杀场景外69308伪造通过。
        if (bossRoom && TcpPeer.U32(map, 14) is 32222 or 32242 && map[0] == 1 && map[1] == 0)
            return;
        // 部分圣地将Boss排在演员表首行；先确认其他敌人，再击杀会立即结束Run的Boss。
        foreach (int i in Enumerable.Range(0, count).OrderBy(index => map[19 + index * 22 + 13] == 3))
        {
            int offset = 19 + i * 22;
            if (map[offset + 21] == 1 || map[offset + 13] == 9)
                continue;
            ushort key = TcpPeer.U16(map, offset + 6);
            byte level = map[offset + 12];
            bool boss = map[offset + 13] == 3;
            peer.Send(39, FormalDungeonTcpTests.Death(key, peer.Id, level));
            var died = Flush(peer, "39 key " + key);
            Note(report, died);
            // R2现场2026-10-29 21:03:48、PVF 32230/32250：擎天(0,1)的58020是普通生成行，死亡不能发布结果菜单包。
            if (map[0] == 0 && map[1] == 1 && TcpPeer.U32(map, 14) is 32230 or 32250)
            {
                if (boss || died.Any(p => p.Opcode is 115 or 31 or 72))
                    throw new InvalidOperationException("擎天(0,1)被标为Boss或死亡触发了结算/退场包");
            }
            if (requireSettlementOrder) RequireSettlementOrder(died, key);
            if (witness is not null)
            {
                var witnessed = Barrier(witness, "39 队员 key " + key);
                Note(witnessReport!, witnessed);
                if (requireSettlementOrder) RequireSettlementOrder(witnessed, key);
            }
            if (!died.Any(p => p is { Kind: 0, Opcode: 38 }))
                throw new InvalidOperationException($"怪物 {key} 死亡未被接受");
            if (boss || bossRoom && bossKey == 0)
            {
                bossKey = key;
                bossLevel = level;
            }
        }
        if (bossRoom && bossKey == 0)
            throw new InvalidOperationException("Boss 房没有可击杀对象");
    }

    internal static DungeonPoint[] Route(RaidDungeonCatalog catalog, int dungeon, byte ordinal, byte x, byte y, byte bossX, byte bossY, bool avoidBossRooms = false)
    {
        var definition = catalog.Find(dungeon) ?? throw new InvalidOperationException("团本目录没有副本 " + dungeon);
        var maze = definition.Mazes.FirstOrDefault(row => row.SourceOrdinal == ordinal)
            ?? definition.Mazes.FirstOrDefault(row => row.QuestConnectionTokens.Count == 0)
            ?? throw new InvalidOperationException(dungeon + " 没有可用迷宫");
        maze = catalog.ResolveMaze(definition, maze, _ => 0);
        if (!maze.StaticTopology.Supported)
            throw new InvalidOperationException($"{dungeon} 静态路线不可用：{maze.StaticTopology.UnsupportedReason}");
        var topology = maze.StaticTopology;
        if (avoidBossRooms)
            topology = topology with { Links = topology.Links!.Where(link =>
                !maze.Bosses.Contains(link.First) && !maze.Bosses.Contains(link.Second)).ToArray() };
        if (!avoidBossRooms && catalog.Type == 0 && dungeon is 212 or 214 &&
            x == 1 && y == 1 && bossX == 1 && bossY == 0)
        {
            var route = new List<DungeonPoint> { new(x, y) };
            foreach (var target in new DungeonPoint[] { new(0, 1), new(2, 1), new(bossX, bossY) })
                route.AddRange(QuestStoryRouteTests.Path(topology, route[^1], target).Skip(1));
            return route.ToArray();
        }
        return QuestStoryRouteTests.Path(topology, new DungeonPoint(x, y), new DungeonPoint(bossX, bossY));
    }

    static List<WirePacket> Flush(TcpPeer peer, string why)
    {
        var elapsed = Stopwatch.StartNew();
        Console.WriteLine($"C2S 1278 barrier {why}");
        peer.Send(1278);
        var rows = new List<WirePacket>();
        bool continued = false;
        for (int i = 0; i < 500; i++)
        {
            WirePacket packet;
            try { packet = peer.Read(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw new InvalidOperationException($"{why} 读包失败：{error.Message}", error);
            }
            Console.WriteLine($"  {(packet.Kind == 1 ? "ACK" : "NOTI")} {packet.Opcode} {Name(packet)} {Describe(packet)}");
            if (packet is { Kind: 1, Opcode: 1278 })
            {
                if (packet.Body is not [1])
                    throw new InvalidOperationException(why + " 的 1278 应答不是 [1]");
                if (continued)
                {
                    continued = false;
                    peer.Send(1278);
                    continue;
                }
                Console.WriteLine($"  barrier complete elapsedMs={elapsed.Elapsed.TotalMilliseconds:F1}");
                RememberFiendwarEntry(peer, rows);
                return rows;
            }
            rows.Add(packet);
            if (ProcessFiendwarKeyNotice(peer, packet)) continued = true;
        }
        throw new InvalidOperationException(why + " 超过读取预算");
    }

    internal static List<WirePacket> Barrier(TcpPeer peer, string why) => Flush(peer, why);

    // Reward 态下再发一次 117，并选已隐藏的 216。只锁定现有回包：不崩、不发 601/596/597、不进图。
    static void StressReward(TcpPeer peer, AntonProbeReport report, byte[] check)
    {
        peer.Send(117, check);
        var again = Flush(peer, "117 Reward 重复");
        Note(report, again);
        if (again.Any(packet => packet.Opcode is 586 or 587 or 596 or 597 or 600 or 601) || again.Any(packet => packet is { Kind: 0, Opcode: 28 or 29 }))
            throw new InvalidOperationException("Reward 后重复 117 发出了进图或发奖包");
        byte[] closed = (byte[])LiveSelect210.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(closed, 216);
        peer.Send(16, closed);
        var rejected = Flush(peer, "16 Reward 态进 216");
        Note(report, rejected);
        if (rejected.Any(packet => packet.Opcode is 28 or 29 or 600 or 601 or 596 or 597))
            throw new InvalidOperationException("Reward 态进 216 仍发布了进图或发奖包");
        // 清图后仍在副本里，16 不在 MapSelect，当前实现不回 ACK16。连接必须还在。
        var ack = rejected.LastOrDefault(packet => packet is { Kind: 1, Opcode: 16 });
        if (ack is not null && ack.Body is not [0, 9, 0, 0])
            throw new InvalidOperationException("Reward 态进 216 的 ACK 不是拒绝：" + Hex(ack.Body));
    }

    internal static void RejectClosed(TcpPeer peer, TcpPeer? witness, int dungeon)
    {
        peer.Send(15, DungeonId(dungeon));
        var opened = Flush(peer, "15 关闭图 " + dungeon);
        if (witness is not null)
            _ = Barrier(witness, "15 关闭图队员 " + dungeon);
        if (opened.Any(packet => packet is { Kind: 1, Opcode: 15, Body: [1] } || packet.Opcode is 28 or 29))
            throw new InvalidOperationException(dungeon + " 在未开放时被 15 接受");
        var ack = opened.LastOrDefault(packet => packet is { Kind: 1, Opcode: 15 });
        if (ack is not { Body: [0, 1, 0] })
            throw new InvalidOperationException(dungeon + " 的 15 拒绝不是 [0,1,0]：" + (ack is null ? "没有 ACK15" : Hex(ack.Body)));
    }

    internal static void RejectSelect(TcpPeer peer, TcpPeer? witness, int dungeon, uint unknown21 = uint.MaxValue)
    {
        byte[] request = (byte[])LiveSelect210.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)dungeon);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(21), unknown21);
        peer.Send(16, request);
        var rejected = Flush(peer, "16 拒绝 " + dungeon);
        if (witness is not null)
            _ = Barrier(witness, "16 拒绝队员 " + dungeon);
        if (rejected.Any(packet => packet.Opcode is 28 or 29))
            throw new InvalidOperationException(dungeon + " 的拒绝仍发布了 28/29");
        var ack = rejected.LastOrDefault(packet => packet is { Kind: 1, Opcode: 16 });
        if (ack is not { Body: [0, 9, 0, 0] })
            throw new InvalidOperationException(dungeon + " 的 16 拒绝不是 [0,9,0,0]：" + (ack is null ? "没有 ACK16" : Hex(ack.Body)));
    }

    internal static void EnterSquad(TcpPeer leader, TcpPeer member, RaidDungeonCatalog catalog, int dungeon,
        AntonProbeReport leaderReport, AntonProbeReport memberReport, bool leave = true, Action? loaded = null,
        bool sendPlayResult = false)
    {
        int leaderConfirmations = leaderReport.Opcode115, leaderPermissions = leaderReport.Opcode31;
        int memberConfirmations = memberReport.Opcode115, memberPermissions = memberReport.Opcode31;
        leader.Send(15, DungeonId(dungeon));
        bool fiendwar = IsFiendwarDungeon(dungeon);
        List<WirePacket> selected = fiendwar ? [] : Flush(leader, "15 分队选择 " + dungeon);
        if (!fiendwar) _ = Barrier(member, "15 分队队员 " + dungeon);
        if (!fiendwar && !selected.Any(packet => packet is { Kind: 1, Opcode: 15, Body: [1] }))
            throw new InvalidOperationException(dungeon + " 分队不能打开选图");
        byte[] request = (byte[])LiveSelect210.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)dungeon);
        leader.Send(16, request);
        var entered = Flush(leader, "16 进图 " + dungeon);
        var witnessed = Barrier(member, "16 队员进图 " + dungeon);
        if (fiendwar)
        {
            CompleteFiendwarEntry([leader, member], [entered, witnessed], peer => Barrier(peer, "fiendwar entry continuation"));
            if (!entered.Any(packet => packet is { Kind: 1, Opcode: 15, Body: [1] }))
                throw new InvalidOperationException(dungeon + " 的握手后分队15未被接受");
        }
        Note(leaderReport, entered);
        Note(memberReport, witnessed);
        var info = entered.LastOrDefault(packet => packet is { Kind: 0, Opcode: 28 } && packet.Body.Length >= 10 && TcpPeer.U32(packet.Body) == (uint)dungeon);
        var map = entered.LastOrDefault(packet => packet is { Kind: 0, Opcode: 29 } && packet.Body.Length >= 19);
        var memberMap = witnessed.LastOrDefault(packet => packet is { Kind: 0, Opcode: 29 } && packet.Body.Length >= 19);
        if (info is null || map is null || memberMap is null)
            throw new InvalidOperationException(dungeon + " 分队没有双方 28/29");
        SameSquadMap(map, memberMap);
        leader.Send(37, new byte[8]);
        member.Send(37, new byte[8]);
        Note(leaderReport, Flush(leader, "37 加载"));
        Note(memberReport, Barrier(member, "37 队员加载"));
        loaded?.Invoke();
        int memberSymbolsBefore = memberReport.Symbols.Count, memberStatesBefore = memberReport.States.Count;
        var path = Route(catalog, dungeon, info.Body[7], map.Body[0], map.Body[1], info.Body[8], info.Body[9]);
        ClearRoom(leader, map.Body, leaderReport, out ushort bossKey, out byte bossLevel,
            path[0].X == info.Body[8] && path[0].Y == info.Body[9], member, memberReport, requireSettlementOrder: true);
        for (int i = 1; i < path.Length; i++)
        {
            byte[] move = new byte[100];
            move[0] = checked((byte)path[i].X);
            move[1] = checked((byte)path[i].Y);
            leader.Send(45, move);
            var moved = Flush(leader, $"45 -> {path[i].X},{path[i].Y}");
            var memberMoved = Barrier(member, $"45 队员 -> {path[i].X},{path[i].Y}");
            Note(leaderReport, moved);
            Note(memberReport, memberMoved);
            map = moved.LastOrDefault(IsRoomMap)
                ?? throw new InvalidOperationException($"{dungeon} 移动到 {path[i].X},{path[i].Y} 没有 29");
            SameSquadMap(map, memberMoved.LastOrDefault(packet => packet is { Kind: 0, Opcode: 29 }));
            leader.Send(37, new byte[8]);
            member.Send(37, new byte[8]);
            Note(leaderReport, Flush(leader, "37 下一房"));
            Note(memberReport, Barrier(member, "37 队员下一房"));
            ClearRoom(leader, map.Body, leaderReport, out ushort nextBoss, out byte nextLevel, i + 1 == path.Length,
                member, memberReport, requireSettlementOrder: true);
            if (i + 1 == path.Length)
            {
                bossKey = nextBoss;
                bossLevel = nextLevel;
            }
        }
        byte[] check = new byte[39];
        BinaryPrimitives.WriteUInt16LittleEndian(check, leader.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(check.AsSpan(2), bossKey);
        BinaryPrimitives.WriteUInt32LittleEndian(check.AsSpan(13), bossLevel);
        BinaryPrimitives.WriteUInt64LittleEndian(check.AsSpan(17), 504);
        if (dungeon is 212 or 214)
        {
            bool passive = TremorUsesPassiveTerminal(catalog, dungeon, info.Body[7]);
            leader.Send(39, QuestPassiveClearTcpTests.PassiveDeath(15467));
            var lateLeader = Flush(leader, "15467分队原资源终末");
            var lateMember = Barrier(member, "15467队员原资源终末");
            Note(leaderReport, lateLeader);
            Note(memberReport, lateMember);
            if (new[] { lateLeader, lateMember }.Any(rows =>
                rows.Count(packet => packet is { Kind: 0, Opcode: 31 }) != (passive ? 1 : 0) ||
                rows.Any(packet => packet is { Kind: 0, Opcode: 115 })))
                throw new InvalidOperationException("震颤两名成员首次15467许可须各自匹配真实资源条件");
            leader.Send(39, QuestPassiveClearTcpTests.PassiveDeath(15467));
            var duplicateLeader = Flush(leader, "通关后重复分队15467");
            var duplicateMember = Barrier(member, "通关后队员15467");
            Note(leaderReport, duplicateLeader);
            Note(memberReport, duplicateMember);
            if (duplicateLeader.Concat(duplicateMember).Any(packet => packet is { Kind: 0, Opcode: 31 or 115 }))
                throw new InvalidOperationException("双人震颤通关后的15467不能再发31或伪造115");
        }
        RequireRunSettlement(leaderReport, leaderConfirmations, leaderPermissions, dungeon);
        RequireRunSettlement(memberReport, memberConfirmations, memberPermissions, dungeon);
        foreach (var sender in dungeon is 212 or 214 ? Array.Empty<TcpPeer>() : new[] { leader, member, leader })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(check, sender.Id);
            sender.Send(117, check);
            var cleared = Flush(leader, "117 分队重放 " + dungeon);
            var memberCleared = Barrier(member, "117 队员重放 " + dungeon);
            Note(leaderReport, cleared);
            Note(memberReport, memberCleared);
            if (cleared.Concat(memberCleared).Any(packet => packet is { Kind: 0, Opcode: 31 or 115 or 34 or 35 }))
                throw new InvalidOperationException(dungeon + " 的117重放重复了分队结算许可或引入普通奖励");
        }
        if (memberReport.Symbols.Count == memberSymbolsBefore && memberReport.States.Count == memberStatesBefore)
            throw new InvalidOperationException("队员没有收到本次清图的583血量或586状态更新");
        if (sendPlayResult)
        {
            // 安徒恩每张正式图清图后客户端都会上传46；必须在仍处于结算阶段时验证两名成员均可安全消费。
            leader.Send(46, DungeonSettlementProtocolTests.Report((leader.Id, 99), (member.Id, 98)));
            member.Send(46, DungeonSettlementProtocolTests.Report((leader.Id, 99), (member.Id, 98)));
            _ = Barrier(leader, "46 安徒恩图 " + dungeon);
            _ = Barrier(member, "46 安徒恩图队员 " + dungeon);
        }
        if (!leave) return;
        leader.Send(42);
        member.Send(42);
        Note(leaderReport, Flush(leader, "42 返回候战室"));
        Note(memberReport, Barrier(member, "42 队员返回"));
    }

    static bool TremorUsesPassiveTerminal(RaidDungeonCatalog catalog, int dungeon, byte ordinal)
    {
        var definition = catalog.Find(dungeon) ?? throw new InvalidDataException("震颤正式定义缺失");
        var maze = definition.Mazes.FirstOrDefault(row => row.SourceOrdinal == ordinal)
            ?? throw new InvalidDataException("震颤原迷宫缺失");
        return maze.ClearConditions.Count == 0 ? false :
            maze.ClearConditions.SequenceEqual([new DungeonClearCondition("destroy object", 15467, 1)]) ? true :
                throw new InvalidDataException("震颤资源不是已验证的空条件或15467单物件条件");
    }

    static void SameSquadMap(WirePacket leader, WirePacket? member)
    {
        if (leader.Body.Length == 16 && leader.Body[13] == 0 && member is not null && member.Body.SequenceEqual(leader.Body)) return;
        if (member is null || member.Body.Length < 19 || leader.Body[0] != member.Body[0] || leader.Body[1] != member.Body[1] ||
            TcpPeer.U32(leader.Body, 14) != TcpPeer.U32(member.Body, 14))
            throw new InvalidOperationException("29 分队地图键或房间坐标不同步");
    }

    static bool IsRoomMap(WirePacket packet) => packet is { Kind: 0, Opcode: 29 } &&
        (packet.Body.Length >= 19 || packet.Body.Length == 16 && packet.Body[13] == 0);

    static void RequireRunSettlement(AntonProbeReport report, int confirmationsBefore, int permissionsBefore, int dungeon)
    {
        int confirmations = dungeon is 212 or 214 ? 0 : 1;
        if (report.Opcode115 != confirmationsBefore + confirmations || report.Opcode31 != permissionsBefore + 1)
            throw new InvalidOperationException($"{dungeon} 的正式Run应向该成员发送{confirmations}次115和一次31");
    }

    static void RequireSettlementOrder(List<WirePacket> packets, ushort key)
    {
        if (!packets.Any(packet => packet is { Kind: 0, Opcode: 115 or 31 })) return;
        int death = packets.FindIndex(packet => packet is { Kind: 0, Opcode: 38 } && TcpPeer.U16(packet.Body) == key);
        int boss = packets.FindIndex(packet => packet is { Kind: 0, Opcode: 115 });
        int clear = packets.FindIndex(packet => packet is { Kind: 0, Opcode: 31 });
        if (death < 0 || boss <= death || clear <= boss || TcpPeer.U16(packets[boss].Body, 2) != key ||
            packets.Count(packet => packet is { Kind: 0, Opcode: 115 }) != 1 ||
            packets.Count(packet => packet is { Kind: 0, Opcode: 31 }) != 1)
            throw new InvalidOperationException("正式死亡必须先38，再真实Boss键115，最后恰好一次空31");
    }

    internal static void Note(AntonProbeReport report, List<WirePacket> rows)
    {
        foreach (var packet in rows)
        {
            report.Observe?.Invoke(packet);
            if (packet is { Kind: 0, Opcode: 115 })
            {
                if (packet.Body is not [1, 1, _, _]) throw new InvalidOperationException("115必须是四字节真实Boss确认");
                report.Opcode115++;
            }
            if (packet is { Kind: 0, Opcode: 31 })
            {
                if (packet.Body.Length != 0) throw new InvalidOperationException("31必须严格空体");
                report.Opcode31++;
            }
            if (packet is { Kind: 0, Opcode: 587, Body: [var state, var phase] })
                report.Waiting = state == 5 && phase == 0;
            if (report.Waiting && packet is { Kind: 0, Opcode: 596 }) report.Wait596++;
            if (report.Waiting && packet is { Kind: 0, Opcode: 597 }) report.Wait597++;
            if (packet is { Kind: 0, Opcode: 586 })
            {
                report.Opcode586++;
                report.States.Add(packet.Body.ToArray());
            }
            else if (packet is { Kind: 0, Opcode: 585 }) report.DungeonSnapshots.Add(packet.Body.ToArray());
            else if (packet is { Kind: 0, Opcode: 583 }) report.Symbols.Add(packet.Body.ToArray());
            else if (packet.Opcode == 596) { report.Opcode596++; report.Timers.Add(packet.Body.ToArray()); }
            else if (packet.Opcode == 597) report.Opcode597++;
            else if (packet is { Kind: 0, Opcode: 600 }) report.Opcode600++;
            else if (packet.Opcode == 601)
            {
                if (packet.Body.Length != 10 || packet.Body[0] != 0 || packet.Body[1] > 1 ||
                    packet.Body[8] is < 2 or > 5 || packet.Body[9] > 1)
                    throw new InvalidOperationException("601必须为成功10B、合法阶段/rank及已封存任务标志");
                report.Opcode601++;
            }
            if (IsRoomMap(packet)) report.MapVisits++;
            if (packet is { Kind: 0, Opcode: 29 } && packet.Body.Length >= 18)
                report.Maps.Add(packet.Body.ToArray());
        }
        if (rows.Any(packet => packet is { Kind: 0, Opcode: 587, Body: [3, 0] }))
            report.Reward = true;
    }

    static byte[] Search(string name) => TcpPeer.Build(w =>
    {
        w.Write((byte)0); w.Write(0u); w.Write(0x100u); w.Write(0u); w.Write(TcpPeer.Name(name));
    });
    static byte[] DungeonId(int dungeon) => BitConverter.GetBytes((uint)dungeon);

    static ushort FindSlot(byte[] roster, string character)
    {
        int count = roster[1];
        int at = 15;
        for (int i = 0; i < count; i++)
        {
            ushort slot = TcpPeer.U16(roster, at);
            int width = (int)TcpPeer.U32(roster, at + 2);
            string name = TcpPeer.DecodeName(roster, at + 6, width);
            if (name == character)
                return slot;
            int equip = roster[at + 21 + width];
            at += 70 + width + equip * 25;
        }
        throw new InvalidOperationException("名册里没有角色 " + character);
    }

    static void AssertLog(string path, DateTimeOffset since, AntonProbeReport report)
    {
        string text = File.ReadAllText(path);
        bool entry = false, phase = false, send586 = false, send596 = false, send597 = false;
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = System.Text.Json.JsonDocument.Parse(line);
            if (!json.RootElement.TryGetProperty("Time", out var time) || time.GetDateTimeOffset() < since.AddSeconds(-2))
                continue;
            string? kind = json.RootElement.GetProperty("Kind").GetString();
            var detail = json.RootElement.GetProperty("Detail");
            if (kind == "formal_dungeon_entry" && detail.TryGetProperty("Dungeon", out var dungeon) && dungeon.GetInt32() == 210 &&
                detail.TryGetProperty("Accepted", out var accepted) && accepted.GetBoolean())
                entry = true;
            if (kind == "anton_phase_progress")
                phase = true;
            if (kind == "send" && detail.TryGetProperty("Opcode", out var opcode))
            {
                int value = opcode.GetInt32();
                if (value == 586) send586 = true;
                if (value == 596) send596 = true;
                if (value == 597) send597 = true;
            }
        }
        if (report.Entered210 && !entry || report.Reward && (!phase || !send586) || report.Opcode596 > 0 && !send596 || report.Opcode597 > 0 && !send597)
            throw new InvalidOperationException(
                $"server.jsonl 断言失败 entry210={entry} phase={phase} send586={send586} send596={send596} send597={send597}");
        Console.WriteLine($"server.jsonl: entry210={entry} phase={phase} send586={send586}, send596={send596}, send597={send597}");
    }

    static int Seed(ServerOptions options, ProbeArgs parsed)
    {
        Directory.CreateDirectory(options.DataDirectory);
        File.WriteAllText(Path.Combine(options.DataDirectory, "raid-start.json"),
            """{"StartMinimumOverride":1,"AntonFormalEntryEnabled":true,"AntonPhaseProgressEnabled":true}""");
        File.WriteAllText(Path.Combine(options.DataDirectory, "pc-room.json"), """{"enabled":false}""");
        using var locker = new FileStream(Path.Combine(options.DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var archive = PvfArchive.Open(options.PvfPath);
        var towns = TownCatalog.Load(archive);
        var items = new ItemResources(archive);
        var characters = CharacterCatalog.Load(archive, items.Items);
        var learning = SkillLearningCatalog.Load(archive, items.Contracts.ExpertSkillLevels);
        var quests = QuestCatalog.Load(archive).WithCreatures(items.Creatures);
        var growth = CharacterGrowthCatalog.Load(archive, learning);
        string database = Path.Combine(options.DataDirectory, "game.db");
        var repository = new CharacterRepository(database, quests, new GameWorld.Adventure.AdventureCatalog(archive), growth,
            items.GuildContents.DungeonMileage);
        var auth = new AuthenticationService(new AuthenticationRepository(database));
        try { auth.Create(parsed.Account!, parsed.Digest); }
        catch (InvalidOperationException) { Console.WriteLine("account exists"); }
        repository.OpenAdventureLedger(DateTimeOffset.UtcNow);
        var owners = new AccountOwners();
        using var owner = owners.Acquire(parsed.Account!, () => { });
        if (repository.List(owner).Any(row => row.Name == parsed.Character))
        {
            Console.WriteLine("character exists: " + parsed.Character);
            return 0;
        }
        var point = new Location(38, 0, 629, 196);
        var candidate = CharacterCreation.Prepare(parsed.Character!, characters.Get(0), point, point, learning.Get);
        var synced = SkillPointLedger.Synchronize(0, 0, 85, candidate.InitialLearning with
            { Experience = growth.GetLevelStartExperience(85) }, learning);
        var created = repository.Create(owner, candidate with
        {
            Level = 85, AcceptedStoryLevel = 85, CompletedQuests = [12829, 3779], InitialLearning = synced
        });
        var granted = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(created, [new(10096296, 2)]);
        if (!granted.Accepted)
            throw new InvalidOperationException("门票发放失败 " + granted.Error);
        InventoryService.Commit(repository, owner, created, granted);
        Console.WriteLine($"seeded {parsed.Account}/{parsed.Character} id={created.Id} tickets=2");
        return 0;
    }

    static void WriteChannel(Stream stream, byte opcode, byte[] body)
    {
        byte[] frame = new byte[11 + body.Length];
        frame[1] = opcode;
        frame[10] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(2), frame.Length);
        body.CopyTo(frame, 11);
        stream.Write(frame);
    }

    static byte[] ReadChannel(Stream stream, byte opcode)
    {
        byte[] header = new byte[11];
        stream.ReadExactly(header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2));
        if (header[0] != 0x7c || header[1] != opcode || length is < 11 or > 65536)
            throw new InvalidOperationException($"频道应答不是 {opcode}");
        byte[] body = new byte[length - 11];
        stream.ReadExactly(body);
        return body;
    }

    static byte[] DecodeChannel(byte[] body)
    {
        using var compressed = new MemoryStream(body);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var encrypted = new MemoryStream();
        zlib.CopyTo(encrypted);
        using var aes = Aes.Create();
        aes.Key = "EMU-LAB-KEY-0001"u8.ToArray();
        return aes.DecryptEcb(encrypted.ToArray(), PaddingMode.None);
    }

    static string Name(WirePacket packet) => packet.Kind == 1 ? ((CmdPacketType)packet.Opcode).ToString() : ((NotiPacketType)packet.Opcode).ToString();

    static string Describe(WirePacket packet)
    {
        byte[] body = packet.Body;
        if (packet is { Kind: 0, Opcode: 23 } && body.Length >= 8)
            return $"id={TcpPeer.U16(body)} town={body[2]}/{body[3]}";
        if (packet is { Kind: 0, Opcode: 28 } && body.Length >= 10)
            return $"dungeon={TcpPeer.U32(body)} boss={body[8]},{body[9]}";
        if (packet is { Kind: 0, Opcode: 29 } && body.Length >= 19)
            return $"room={body[0]},{body[1]} actors={body[18]}";
        if (packet is { Kind: 0, Opcode: 586 } && body.Length >= 6)
            return $"dungeon={TcpPeer.U32(body)} state={body[5]}";
        if (packet is { Kind: 0, Opcode: 587 } && body.Length >= 2)
            return $"state={body[0]} phase={body[1]}";
        return "len=" + body.Length + " " + Hex(body);
    }

    static string Hex(byte[] body)
    {
        int n = Math.Min(body.Length, 24);
        return Convert.ToHexString(body.AsSpan(0, n)) + (body.Length > n ? "..." : "");
    }

    internal sealed class AntonProbeReport
    {
        public Action<WirePacket>? Observe;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
        public bool Started;
        public bool Entered210;
        public bool Reward;
        public int Opcode115;
        public int Opcode31;
        public int Opcode586;
        public int Opcode596;
        public int Opcode597;
        // 按实际已接收587划分休息计时，不能与阶段/地图计时总数混用。
        public bool Waiting;
        public int Wait596;
        public int Wait597;
        public int Opcode600;
        public int Opcode601;
        public int RejectedCards;
        public bool CardUnavailable;
        public byte[]? LastClearCheck;
        public List<string> Steps { get; } = [];
        public List<byte[]> States { get; } = [];
        public List<byte[]> DungeonSnapshots { get; } = [];
        public List<byte[]> Symbols { get; } = [];
        public List<byte[]> Timers { get; } = [];
        public List<byte[]> Maps { get; } = [];
        public int MapVisits { get; set; }
        public string Summary() =>
            $"started={Started} entered210={Entered210} reward={Reward} noti586={Opcode586} 596={Opcode596} 597={Opcode597} 600={Opcode600} 601={Opcode601} rejectedCards={RejectedCards} cardUnavailable={CardUnavailable} steps={string.Join(',', Steps)}";
    }

    internal sealed record ProbeArgs(bool Seed, string? Host, int? ChannelPort, int? GamePort, string? Account, string Digest,
        string? Character, byte Channel, string? ServerLog, string StopAfter)
    {
        public static ProbeArgs Parse(string[] args)
        {
            string? host = null, account = null, digest = NativeLoginWire.Digest, character = "AntProbe", log = null, stop = "phase";
            int? channelPort = null, gamePort = null;
            byte channel = 19;
            bool seed = false;
            bool stopSpecified = false, stepsSpecified = false;
            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i];
                string Next() => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i] : throw new InvalidDataException(name + " 需要一个值");
                switch (name)
                {
                    case "--seed": seed = true; break;
                    case "--host": host = Next(); break;
                    case "--channel-port": channelPort = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--game-port": gamePort = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--account": account = Next(); break;
                    case "--digest": digest = Next(); break;
                    case "--character": character = Next(); break;
                    case "--channel": channel = byte.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--server-log": log = Next(); break;
                    case "--stop-after": stop = Next(); stopSpecified = true; break;
                    case "--steps": stop = StopAfterForSteps(Next()); stepsSpecified = true; break;
                    default: throw new InvalidDataException("未知探针参数：" + name);
                }
            }
            if (stopSpecified && stepsSpecified)
                throw new InvalidDataException("--steps 与 --stop-after 互斥");
            if (stop is not ("start" or "enter" or "phase" or "reward" or "card"))
                throw new InvalidDataException("步骤只能是 start、enter、phase、reward 或 card");
            if (!LoginCredentials.IsDigest(digest!))
                throw new InvalidDataException("--digest 必须是 32 位十六进制");
            if (seed)
            {
                if (string.IsNullOrWhiteSpace(account) || !LoginCredentials.IsAccount(account))
                    throw new InvalidDataException("--seed 需要合法 --account");
                if (host is not null)
                    throw new InvalidDataException("--seed 与 --host 分开执行");
            }
            else if (host is not null || channelPort is not null || gamePort is not null)
            {
                if (host is null || channelPort is null)
                    throw new InvalidDataException("连接需要 --host 和 --channel-port");
                if (channelPort is < 1 or > 65535 || gamePort is < 1 or > 65535)
                    throw new InvalidDataException("端口超出范围");
                if (string.IsNullOrWhiteSpace(account) || !LoginCredentials.IsAccount(account))
                    throw new InvalidDataException("连接需要合法 --account");
            }
            else if (args.Length != 0)
                throw new InvalidDataException("没有 --host 时不要带其它连接参数");
            return new(seed, host, channelPort, gamePort, account, digest!, character, channel, log, stop);
        }
    }
}
