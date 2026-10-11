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
                log.Write("server_ready", new { Pid = 1 });
                log.Write("auth_rejected", new { Connection = 1, Failure = "InvalidCredentials" });
                log.Write("formal_dungeon_entry", new { Connection = 1, Accepted = false });
                log.Write("unimplemented", new { Connection = 1, Phase = "Town", Kind = 1, Opcode = 65535,
                    Name = "65535", Length = 3, Body = "10ABFF" });
                log.Write("unimplemented", new { Connection = 1, Phase = "Town", Kind = 1, Opcode = 65534,
                    Name = "65534", Length = 0, Body = "" });
                log.Write("unimplemented", new { Connection = 1, Phase = "Town", Kind = 1,
                    Opcode = (ushort)CmdPacketType.CLIENT_SPEC_STATISTIC, Name = "CLIENT_SPEC_STATISTIC", Length = 32, Body = "[login-redacted]" });
                log.Write("startup", new { Stage = "PVF archive", State = "starting" });
                log.Write("startup", new { Stage = "PVF archive", State = "complete", ElapsedMs = 12.5 });
                log.Write("listening", new { ChannelName = "#ch.10", Channel = "127.0.0.1:23100", Game = "127.0.0.1:23101",
                    AdvertisedGame = "127.0.0.1:23101", Gm = "disabled", PacketCapture = "disabled", ServiceLog = "Logs/with spaces/server.jsonl" });
                log.Write("travel", new { Connection = 1, Accepted = true, Position = new { Town = 2, X = 10 }, Tags = new[] { "first", "second" } });
                log.Write("character_deleted", new { Connection = 1, RosterIndex = 1, Character = 7, Slot = 2, Revision = 3, SelectionEpoch = 4 });
                log.Write("character_delete_rejected", new { Connection = 1, RosterIndex = 31, Reason = "index-absent" });
                log.Write("quest_completion", new { Connection = 1, Quest = 3156, Accepted = false, Failure = "ResourceUnsupported" });
                log.Write("resource_warning", new { MissingMaps = new[] { new { Path = "maps/with spaces/map.map", Error = "missing" } } });
                string text = output.ToString();
                TestCheck.That(text.Contains("[INFO] [server_ready] Server ready") && text.Contains("[WARN] [auth_rejected]") &&
                    text.Contains("[WARN] [formal_dungeon_entry]"), "English events expose readiness and rejection severity");
                TestCheck.That(text.Contains("[INFO] [character_deleted] Character deleted") &&
                    text.Contains("RosterIndex=1 Character=7 Slot=2 Revision=3 SelectionEpoch=4") &&
                    text.Contains("[WARN] [character_delete_rejected] Character deletion rejected") && text.Contains("Reason=index-absent"),
                    "删除日志区分提交身份、名册行索引和拒绝原因，使用英文INFO/WARN");
                TestCheck.That(text.Contains("[WARN] [quest_completion] Quest completion") &&
                    text.Contains("Quest=3156 Accepted=False Failure=ResourceUnsupported"),
                    "任务拒绝日志标为WARN并记录编号和结构化分类，不输出任意资源正文");
                TestCheck.That(text.Contains("Phase=Town Kind=0x01 Opcode=65535 (0xFFFF) Name=65535 Length=3 Body=10ABFF") &&
                    text.Contains("Length=0 Body=empty") && text.Contains("Body=[login-redacted]"), "unhandled packet fields include full body, empty marker, and redaction");
                TestCheck.That(text.All(character => character < 128), "console event templates and ASCII details remain English");
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
            TestCheck.That(fileLines.Length == 83,
                "console filtering preserves every structured file event and flushes the tail");
            using (var json = JsonDocument.Parse(fileLines[0]))
                TestCheck.That(json.RootElement.GetProperty("Kind").GetString() == "send" &&
                    json.RootElement.GetProperty("Detail").GetProperty("Body").GetString() == "hidden", "original JSON file schema stays intact");
            foreach (ushort opcode in new[] { (ushort)CmdPacketType.LOGIN, (ushort)CmdPacketType.LOGIN_PRECHECK,
                (ushort)CmdPacketType.CLIENT_SPEC_STATISTIC })
                TestCheck.That(PacketCapture.IsAuthentication(opcode), "capture and unhandled logs share credential redaction");
            TestCheck.That(!PacketCapture.IsAuthentication(65535), "ordinary unknown packets retain their body");

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
            bool rejected = false, ready = false;
            try
            {
                await new ServerHost(options, protocol, protocol, null, () => ready = true, failedLog).RunAsync(CancellationToken.None);
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
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS {TestCheck.Count - checks} server stdout log assertions");
    }

    sealed class EmptyProtocol : IConnectionProtocol
    {
        public Task RunAsync(ConnectionContext connection) => Task.CompletedTask;
    }
}
