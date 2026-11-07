using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Testing;

namespace DfoServer;

internal static class ServerLogTests
{
    public static async Task RunAsync()
    {
        int checks = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "stdout-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var output = new StringWriter();
            using (var log = new ServerLog(root, output))
            {
                foreach (string kind in new[] { "send", "recv", "position", "client_notice", "formal_dungeon_death" })
                    log.Write(kind, new { Body = "hidden", Connection = 1 });
                TestCheck.That(output.ToString().Length == 0, "routine packet and hot-path events stay out of stdout");
                TestCheck.That(ServerBuildInfo.Version.Length > 0 &&
                    (ServerBuildInfo.Commit.Length == 0 || ServerBuildInfo.Commit.Length == 40),
                    "构建标识只取自程序集：版本非空，提交号要么为空要么是完整 git 提交");
                log.Write("server_ready", new { Pid = 1 });
                log.Write("battle_pass_default_unavailable", new { Error = "ambiguous legacy seasons" });
                TestCheck.That(output.ToString().Contains("[WARN] [battle_pass_default_unavailable] Default battle pass unavailable; server continues without battle pass: Error=ambiguous legacy seasons"),
                    "默认赛季降级在关闭文件日志时也能通过英文WARN定位，保留原始原因");
                log.Write("fatigue_daily_reset", new { Connection = 1, Character = 7, Day = 739916, Remaining = 156, Maximum = 156, Revision = 3 });
                log.Write("fatigue_daily_reset_failed", new { Connection = 1, Epoch = 2, Error = "fixture" });
                TestCheck.That(output.ToString().Contains("[INFO] [fatigue_daily_reset] Daily fatigue restored:") &&
                    output.ToString().Contains("[WARN] [fatigue_daily_reset_failed] Daily fatigue restore failed:"),
                    "疲劳日切提交与失败使用现有英文纯文本日志，区分INFO和WARN");
                log.Write("auth_rejected", new { Connection = 1, Failure = "InvalidCredentials" });
                log.Write("formal_dungeon_entry", new { Connection = 1, Accepted = false });
                log.Write("formal_quest_continuation_selection", new { Connection = 1, DungeonIndex = 6, Opened = true, SelectionId = 2 });
                log.Write("unimplemented", new { Connection = 1, Phase = "Town", Kind = 1, Opcode = 65535,
                    Name = "65535", Length = 3, Body = "10ABFF" });
                log.Write("unimplemented", new { Connection = 1, Phase = "Town", Kind = 1, Opcode = 65534,
                    Name = "65534", Length = 0, Body = "" });
                log.Write("unimplemented", new { Connection = 1, Phase = "Town", Kind = 1,
                    Opcode = (ushort)CmdPacketType.CLIENT_SPEC_STATISTIC, Name = "CLIENT_SPEC_STATISTIC", Length = 32, Body = "[login-redacted]" });
                log.Write("startup", new { Stage = "PVF archive", State = "starting" });
                log.Write("startup", new { Stage = "PVF archive", State = "complete", ElapsedMs = 12.5 });
                log.Write("listening", new { ChannelName = "#ch.10", Channel = "127.0.0.1:23100", Game = "127.0.0.1:23101",
                    AdvertisedGame = "127.0.0.1:23101", Gm = "disabled", GmWeb = "127.0.0.1:23132",
                    GmPacketQueries = "http://127.0.0.1:23132/api/packets", PacketHttp = "disabled", PacketCapture = "disabled", ServiceLog = "Logs/with spaces/server.jsonl" });
                log.Write("travel", new { Connection = 1, Accepted = true, Position = new { Town = 2, X = 10 }, Tags = new[] { "first", "second" } });
                log.Write("character_deleted", new { Connection = 1, RosterIndex = 1, Character = 7, Slot = 2, Revision = 3, SelectionEpoch = 4 });
                log.Write("character_delete_rejected", new { Connection = 1, RosterIndex = 31, Reason = "index-absent" });
                log.Write("quest_completion", new { Connection = 1, Quest = 3156, Accepted = false, Failure = "ResourceUnsupported" });
                log.Write("quest_lifecycle", new { Connection = 1, questId = 13601, action = 0, Outcome = 0,
                    Reason = "level-mismatch", Accepted = false, Phase = 1, CharacterId = 7, Revision = 3, Level = 24, Job = 0, GrowType = 0, ActiveQuestCount = 0 });
                log.Write("quest_lifecycle", new { Connection = 1, questId = 13601, action = 0, Outcome = 1,
                    Reason = "committed", Accepted = true, Phase = 1, CharacterId = 7, Revision = 4, Level = 25, Job = 0, GrowType = 0, ActiveQuestCount = 1 });
                log.Write("consumable_use", new { Connection = 1, Item = 1108, Slot = 65, Accepted = true, Reason = "committed", SourceUid = 7 });
                log.Write("consumable_use", new { Connection = 1, Item = 1108, Slot = 65, Accepted = false, Reason = "cooldown" });
                log.Write("enhancement", new { Connection = 1, Accepted = true, OldLevel = 4, NewLevel = 5,
                    BaseSuccessPercent = 80m, SuccessPercent = 88m });
                log.Write("random_option_change", new { Connection = 1, Slot = 13, OptionIndex = 1, Accepted = true, Result = "committed", Revision = 2 });
                log.Write("random_option_change", new { Connection = 1, Slot = 13, OptionIndex = 0, Accepted = false, Error = 4 });
                log.Write("cash_shop_buy", new { Connection = 1, Count = 1, Commodities = "270623", Accepted = true, Result = "committed", Revision = 3 });
                log.Write("cash_shop_buy", new { Connection = 1, Count = 1, Commodities = "270623", Accepted = false, Error = 17 });
                log.Write("avatar_dye", new { Connection = 1, SourceSlot = 74, AvatarSlot = 0, Accepted = true, Reason = "committed", Revision = 4, Color1 = 1, Color2 = 0 });
                log.Write("avatar_dye", new { Connection = 1, SourceSlot = 74, AvatarSlot = 0, Accepted = false, Reason = "source-qualification" });
                log.Write("avatar_socket", new { Connection = 1, TargetSlot = 0, TargetItemId = 101550535, MaterialSlot = 74,
                    Accepted = true, Reason = "committed", Revision = 5 });
                log.Write("avatar_socket", new { Connection = 1, TargetSlot = 0, TargetItemId = 101550535, MaterialSlot = 74,
                    Accepted = false, Reason = "already-open" });
                log.Write("resource_warning", new { MissingMaps = new[] { new { Path = "maps/with spaces/map.map", Error = "missing" } } });
                log.Write("db_busy", new { Connection = 1, Phase = "Town", Kind = 1, Opcode = 0, Name = "0", Stage = "dispatch", Attempts = 5,
                    Message = "SQLite Error 5: 'database is locked'." });
                log.Write("db_slow", new { Stage = "write_transaction", Action = "CharacterRepository.Save", ElapsedMs = 1523,
                    SqlHead = "UPDATE characters SET revision=revision+1", Connection = 7 });
                string text = output.ToString();
                TestCheck.That(text.Contains("[INFO] [enhancement] Equipment enhancement:") &&
                    text.Contains("BaseSuccessPercent=80 SuccessPercent=88"),
                    "强化提交日志按英文纯文本显示实际基础及最终成功百分比");
                TestCheck.That(text.Contains("[INFO] [server_ready] Server ready") && text.Contains("[WARN] [auth_rejected]") &&
                    text.Contains("[WARN] [formal_dungeon_entry]"), "English events expose readiness and rejection severity");
                TestCheck.That(text.Contains("[WARN] [db_busy] Database busy") && text.Contains("Stage=dispatch Attempts=5") &&
                    text.Contains("Message=SQLite Error 5: 'database is locked'."),
                    "忙锁耗尽降级为 WARN 诊断，保留阶段、尝试次数与原始消息");
                TestCheck.That(text.Contains("[WARN] [db_slow] Database slow") &&
                    text.Contains("Stage=write_transaction Action=CharacterRepository.Save ElapsedMs=1523") &&
                    text.Contains("SqlHead=UPDATE characters SET revision=revision+1 Connection=7"),
                    "慢数据库操作按 WARN 输出实际阶段、动作、耗时、SQL 头与连接");
                TestCheck.That(text.Contains("[INFO] [formal_quest_continuation_selection] Quest continuation selection") &&
                    text.Contains("DungeonIndex=6 Opened=True SelectionId=2"),
                    "连图选择日志保留英文阶段及选择身份，不将选图准备标为副本入场成功");
                TestCheck.That(text.Contains("[INFO] [character_deleted] Character deleted") &&
                    text.Contains("RosterIndex=1 Character=7 Slot=2 Revision=3 SelectionEpoch=4") &&
                    text.Contains("[WARN] [character_delete_rejected] Character deletion rejected") && text.Contains("Reason=index-absent"),
                    "删除日志区分提交身份、名册行索引和拒绝原因，使用英文INFO/WARN");
                TestCheck.That(text.Contains("[WARN] [quest_completion] Quest completion") &&
                    text.Contains("Quest=3156 Accepted=False Failure=ResourceUnsupported"),
                    "任务拒绝日志标为WARN并记录编号和结构化分类，不输出任意资源正文");
                TestCheck.That(text.Contains("[WARN] [quest_lifecycle] Quest update") && text.Contains("[INFO] [quest_lifecycle] Quest update") &&
                    text.Contains("Outcome=0 Reason=level-mismatch Accepted=False Phase=1 CharacterId=7 Revision=3 Level=24 Job=0 GrowType=0 ActiveQuestCount=0") &&
                    text.Contains("Outcome=1 Reason=committed Accepted=True Phase=1 CharacterId=7 Revision=4 Level=25 Job=0 GrowType=0 ActiveQuestCount=1"),
                    "接取业务回执沿统一英文纯文本区分拒绝WARN与提交INFO，保留最小资格快照");
                TestCheck.That(text.Contains("[INFO] [consumable_use] Consumable use") && text.Contains("[WARN] [consumable_use] Consumable use") &&
                    text.Contains("Item=1108 Slot=65") && text.Contains("Reason=cooldown"),
                    "道具使用日志以实际接受/拒绝标INFO/WARN并保留源身份和英文原因");
                TestCheck.That(text.Contains("Phase=Town Kind=0x01 Opcode=65535 (0xFFFF) Name=65535 Length=3 Body=10ABFF") &&
                    text.Contains("Length=0 Body=empty") && text.Contains("Body=[login-redacted]"), "unhandled packet fields include full body, empty marker, and redaction");
                TestCheck.That(text.All(character => character < 128), "console event templates and ASCII details remain English");
                TestCheck.That(text.Contains("[INFO] [random_option_change] Equipment random option change") &&
                    text.Contains("[WARN] [random_option_change]") && text.Contains("Slot=13 OptionIndex=1 Accepted=True Result=committed Revision=2"),
                    "属性变换日志区分实际提交与拒绝，保留目标、候选索引和提交revision");
                TestCheck.That(text.Contains("[INFO] [cash_shop_buy] Cash shop purchase") && text.Contains("[WARN] [cash_shop_buy]") &&
                    text.Contains("Commodities=270623 Accepted=True Result=committed Revision=3"), "商城日志保留真实商品、拒绝和提交版本");
                TestCheck.That(text.Contains("[INFO] [avatar_dye] Avatar dye") && text.Contains("[WARN] [avatar_dye]") &&
                    text.Contains("SourceSlot=74 AvatarSlot=0 Accepted=True Reason=committed Revision=4 Color1=1 Color2=0"),
                    "漂染日志保留两个槽位、拒绝原因与实际提交颜色");
                TestCheck.That(text.Contains("[INFO] [avatar_socket] Avatar socket opening") && text.Contains("[WARN] [avatar_socket]") &&
                    text.Contains("TargetSlot=0 TargetItemId=101550535 MaterialSlot=74 Accepted=True Reason=committed Revision=5") &&
                    text.Contains("Reason=already-open"), "开孔日志保留目标、材料、实际提交版本及拒绝原因");
                TestCheck.That(text.Contains("Loading Script.pvf...") && text.Contains("Script.pvf: OK (12.5 ms).") &&
                    text.Contains("Channel #ch.10 listening on TCP 127.0.0.1:23100") && text.Contains("GM control: disabled"),
                    "startup uses S4A21-style English text instead of serialized fields");
                TestCheck.That(!text.Contains('{') && !text.Contains('}') && !text.Contains('"') &&
                    text.Contains("Position.Town=2") && text.Contains("Tags[1]=second") &&
                    text.Contains("MissingMaps[0].Path=maps/with spaces/map.map"), "nested details and paths are readable plain text");

                await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
                {
                    for (int i = 0; i < 16; i++) log.Write("selected", new { Connection = worker, Character = i });
                })));
                string[] selected = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.Contains("[selected]")).ToArray();
                var seen = new HashSet<(int, int)>();
                foreach (string line in selected)
                {
                    var connection = Regex.Match(line, @"\bConnection=(\d+)\b");
                    var character = Regex.Match(line, @"\bCharacter=(\d+)\b");
                    TestCheck.That(connection.Success && character.Success &&
                        seen.Add((int.Parse(connection.Groups[1].Value), int.Parse(character.Groups[1].Value))),
                        "concurrent plain-text console records are complete and unique");
                }
                TestCheck.That(seen.Count == 64, "all concurrent console events are delivered");
            }
            string[] fileLines = File.ReadAllLines(Path.Combine(root, "server.jsonl"));
            TestCheck.That(fileLines.Length == 102 && fileLines.Count(line => line.Contains("\"Kind\":\"fatigue_daily_reset", StringComparison.Ordinal)) == 2,
                "console filtering preserves every structured file event and flushes the tail");
            using (var json = JsonDocument.Parse(fileLines[0]))
                TestCheck.That(json.RootElement.GetProperty("Kind").GetString() == "send" &&
                    json.RootElement.GetProperty("Detail").GetProperty("Body").GetString() == "hidden", "original JSON file schema stays intact");
            foreach (ushort opcode in new[] { (ushort)CmdPacketType.LOGIN, (ushort)CmdPacketType.LOGIN_PRECHECK,
                (ushort)CmdPacketType.CLIENT_SPEC_STATISTIC })
                TestCheck.That(PacketCapture.IsAuthentication(opcode), "capture and unhandled logs share credential redaction");
            TestCheck.That(!PacketCapture.IsAuthentication(65535), "ordinary unknown packets retain their body");
            foreach (ushort opcode in new[] { (ushort)CmdPacketType.MAKE_PVP_ROOM, (ushort)CmdPacketType.ENTER_PVP_ROOM })
                TestCheck.That(PacketCapture.IsSensitiveCommand(opcode) &&
                    PacketCapture.RedactedCommandBody(opcode) == "[pvp-password-redacted]",
                    "房间收包与未处理日志使用同一密码脱敏合同");

            // 真实占用游戏端口，确认前一监听被释放且不发布虚假的就绪。
            var occupied = new TcpListener(IPAddress.Loopback, 0);
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            occupied.Start();
            reservation.Start();
            int channelPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            int gamePort = ((IPEndPoint)occupied.LocalEndpoint).Port;
            reservation.Stop();
            string failedRoot = Path.Combine(root, "occupied");
            using var failureOutput = new StringWriter();
            using var failedLog = new ServerLog(failedRoot, failureOutput);
            var protocol = new EmptyProtocol();
            var options = new ServerOptions { ChannelPort = channelPort, GamePort = gamePort, LogDirectory = failedRoot };
            var catalog = DfoServer.GameWorld.Channels.ChannelCatalog.Parse(
                "[server]\n2\n10 `<chn_channel_info_028>` 22 `[granfloris]` 5 0 0 0 0 0 0 0 0 0 0 ``\n[/server]\n");
            var listeners = new GameChannelListeners(options, catalog);
            bool rejected = false, ready = false;
            try
            {
                await new ServerHost(options, protocol, protocol, null, static () => [], () => ready = true, failedLog, listeners).RunAsync(CancellationToken.None);
            }
            catch (SocketException) { rejected = true; }
            finally { occupied.Stop(); }
            TestCheck.That(rejected && !ready && !File.Exists(Path.Combine(failedRoot, "ready.json")) &&
                failureOutput.ToString().Contains("Game listener") && !failureOutput.ToString().Contains("Server ready"),
                "occupied listener reports its stage without publishing readiness");
            try
            {
                reservation.Start();
                TestCheck.That(reservation.Server.IsBound, "startup failure releases the earlier channel listener");
            }
            finally { reservation.Stop(); }

            await CheckListenerCancellationAsync(options, root, catalog);
        }
        finally { if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {TestCheck.Count - checks} server stdout log assertions");
    }

    sealed class EmptyProtocol : IConnectionProtocol
    {
        public Task RunAsync(ConnectionContext connection) => Task.CompletedTask;
    }

    // 探针值而不是真实 schema 版本：证明 ready.json 写的是宿主收到的参数，不是别处抄来的常量。
    const int ProbeSaveVersion = 99;

    static async Task CheckListenerCancellationAsync(ServerOptions ports, string root, DfoServer.GameWorld.Channels.ChannelCatalog catalog)
    {
        foreach (bool reading in new[] { false, true })
        {
            string directory = Path.Combine(root, reading ? "cancel-reading" : "cancel-accept");
            using var output = new StringWriter();
            using var log = new ServerLog(directory, output);
            using var stop = new CancellationTokenSource();
            var channelProtocol = new WaitingProtocol();
            var gameProtocol = new WaitingProtocol();
            var options = ports with { LogDirectory = directory };
            var host = new ServerHost(options, channelProtocol, gameProtocol, null, static () => [], () => { }, log, new GameChannelListeners(options, catalog),
                saveVersion: ProbeSaveVersion);
            Task running = host.RunAsync(stop.Token);
            using var channel = new TcpClient();
            using var game = new TcpClient();
            try
            {
                if (reading)
                {
                    await channel.ConnectAsync(IPAddress.Loopback, ports.ChannelPort).WaitAsync(TimeSpan.FromSeconds(3));
                    await game.ConnectAsync(IPAddress.Loopback, ports.GamePort).WaitAsync(TimeSpan.FromSeconds(3));
                    await Task.WhenAll(channelProtocol.Started.Task, gameProtocol.Started.Task).WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
            finally
            {
                stop.Cancel();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(3));
            }
            TestCheck.That(running.IsCompletedSuccessfully && output.ToString().Contains("[stopped]") &&
                !output.ToString().Contains("[listener_failed]"), "待接入或连接读取期间取消宿主正常结束，不记录监听失败");
            // 构建标识随就绪文件与启动日志一起给出，启动器按它显示「版本 · 存档 · 提交号」。
            using (var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "ready.json"))))
            {
                var marker = ready.RootElement;
                TestCheck.That(marker.TryGetProperty("ServerVersion", out var build) && build.ValueKind == JsonValueKind.String
                    && marker.TryGetProperty("Commit", out var built) && built.ValueKind == JsonValueKind.String
                    && marker.TryGetProperty("SaveVersion", out var save) && save.GetInt32() == ProbeSaveVersion
                    && output.ToString().Contains($"SaveVersion=v{ProbeSaveVersion}"),
                    "就绪文件与启动日志带程序版本、构建提交号与存档 schema 版本");
            }
            if (reading)
            {
                TestCheck.That(channelProtocol.Released && gameProtocol.Released, "宿主退出前等待频道与游戏连接清理");
                foreach (TcpClient client in new[] { channel, game })
                    TestCheck.That(await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) == 0,
                        "宿主停止释放正在读取的客户端连接");
            }
        }

        // 管理任务的真实错误不在 Accept 的取消边界内，即使同时请求停止也不能吞掉。
        foreach (bool canceled in new[] { false, true })
        {
            string directory = Path.Combine(root, "management-failure-" + canceled);
            using var output = new StringWriter();
            using var log = new ServerLog(directory, output);
            using var stop = new CancellationTokenSource();
            var management = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var expected = new SocketException((int)(canceled ? SocketError.ConnectionReset : SocketError.OperationAborted));
            var protocol = new EmptyProtocol();
            var options = ports with { LogDirectory = directory };
            Task running = new ServerHost(options, protocol, protocol,
                (_, _) => management.Task, static () => [], () => { }, log, new GameChannelListeners(options, catalog)).RunAsync(stop.Token);
            bool propagated = false;
            try
            {
                if (canceled) stop.Cancel();
                management.SetException(expected);
                await running.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (SocketException error) when (ReferenceEquals(error, expected)) { propagated = true; }
            finally
            {
                stop.Cancel();
                management.TrySetException(expected);
                try { await running.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (SocketException error) when (ReferenceEquals(error, expected)) { }
            }
            TestCheck.That(propagated, "未取消995及取消时其他真实套接字错误仍传播");
        }
    }

    sealed class WaitingProtocol : IConnectionProtocol
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Released { get; private set; }

        public async Task RunAsync(ConnectionContext connection)
        {
            ValueTask<int> reading = connection.Stream.ReadAsync(new byte[1], connection.Stop);
            Started.TrySetResult();
            try { await reading; }
            finally { Released = true; }
        }
    }
}
