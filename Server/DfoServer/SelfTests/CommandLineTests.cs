using static DfoServer.Testing.TestCheck;
using DfoServer.Application;

namespace DfoServer;

internal static class CommandLineTests
{
    public static void Run()
    {
        int checks = Count;
        void Reject(string[] arguments)
        {
            bool rejected = false;
            try
            {
                var command = ServerCommand.Parse(arguments);
                ServerOptions.Load(command.Options);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            That(rejected, "invalid input must not reach listener: " + string.Join(' ', arguments));
        }

        That(ServerCommand.Parse([]) is { Kind: ServerCommandKind.Serve, PacketCapture: false }, "default entry needs no configuration");
        That(ServerCommand.Parse(["--help"]).Kind == ServerCommandKind.Help, "standalone help");
        That(ServerCommand.Parse(["--gm", "account", "--selftest", "list"]).Arguments.SequenceEqual(["account", "--selftest", "list"]), "command-like account name remains a GM argument");
        var launch = ServerCommand.Parse(["--server-ip", "auto", "--packet-http-port", "18081", "--packet-capture", "capture files"]);
        That(launch.PacketCapture && launch.Options["--server-ip"] == "auto" && launch.Options["--packet-http-port"] == "18081" &&
            launch.Arguments.SequenceEqual(["capture files"]), "runtime options and capture path remain separate");
        That(ServerCommand.Parse(["--packet-capture"]).Arguments.Length == 0, "capture defaults to fixed log directory");
        That(ServerOptions.Load(ServerCommand.Parse(["--channel-entry-port-base", "23103"]).Options).ChannelEntryPortBase == 23103,
            "分类入口端口范围使用正式CLI配置，启动脚本无需修改");
        (string[] Arguments, ServerCommandKind Kind)[] supported =
        [
            (["--inspect"], ServerCommandKind.InspectTowns),
            (["--inspect-dungeon", "0"], ServerCommandKind.InspectDungeon),
            (["--scan-dungeons"], ServerCommandKind.ScanDungeons),
            (["--scan-quests"], ServerCommandKind.ScanQuests),
            (["--inspect-jianying"], ServerCommandKind.InspectPreparation),
            (["--selftest-core"], ServerCommandKind.SelfTestCore),
            (["--selftest"], ServerCommandKind.SelfTest),
            (["--selftest-tcp"], ServerCommandKind.TcpTest),
            (["--gm"], ServerCommandKind.Gm),
            (["--prepare-jianying", "剑影", "audit"], ServerCommandKind.PrepareCharacter),
            (["--import-characters", "roles.txt"], ServerCommandKind.ImportCharacters),
            (["--pvf-list", "sample.pvf", "character/"], ServerCommandKind.PvfList),
            (["--pvf-text", "sample.pvf", "character/character.lst"], ServerCommandKind.PvfText)
        ];
        foreach (var test in supported)
            That(ServerCommand.Parse(test.Arguments).Kind == test.Kind, string.Join(' ', test.Arguments));
        That(ServerCommand.Parse(["--selftest-tcp", "material-exchange,booster"]).Arguments.SequenceEqual(["material-exchange,booster"]), "TCP group selection preserved");
        foreach (string group in Testing.TcpRegressionTests.GroupNames)
            That(ServerCommand.Parse(["--selftest-tcp", group]).Kind == ServerCommandKind.TcpTest, "known TCP group " + group);
        foreach (string selection in new[] { "", "booster,", "booster,booster", "booster,unknown", "Booster" })
            Reject(["--selftest-tcp", selection]);
        foreach (string name in new[] { "--pvf", "--log-dir", "--duplicate-login", "--client-ip" })
            Reject([name, "unused"]);
        string[][] invalid =
        [
            ["server.json"], ["--unknown"], ["--server-ip"], ["--server-ip", "--help"],
            ["--server-ip", "127.0.0.1", "--server-ip", "127.0.0.2"], ["--help", "extra"],
            ["--server-ip", "127.0.0.1", "--help"], ["--selftest", "extra"], ["--selftest-core", "extra"], ["--inspect", "extra"],
            ["--scan-dungeons", "extra"], ["--inspect-dungeon"], ["--inspect-dungeon", "-1"],
            ["--inspect-dungeon", "2147483648"], ["--inspect-dungeon", "+1"],
            ["--create-local-account"], ["--issue-local-ticket", "a"], ["--prepare-jianying", "Hero"],
            ["--import-characters"], ["--pvf-list", "sample.pvf"], ["--packet-capture", "--selftest"],
            ["--packet-capture", " "], ["--packet-capture", "capture", "extra"],
            ["--server-ip", "::1"], ["--server-ip", "0.0.0.0"], ["--listen-ip", "bad"],
            ["--channel-port", "0"], ["--game-port", "65536"], ["--channel-port", "23101"],
            ["--channel-entry-port-base", "-1"], ["--channel-entry-port-base", "65536"],
            ["--gm-port", "23100"], ["--session-ttl", "29"], ["--game-port", "-1"],
            ["--packet-http-port"], ["--packet-http-port", "-1"], ["--packet-http-port", "65536"],
            ["--packet-http-port", "23100"], ["--packet-http-port", "23101"],
            ["--gm-port", "23102", "--packet-http-port", "23102"],
            ["--channel-entry-port-base", "23103", "--packet-http-port", "23103"],
            ["--dungeon-entry-probe", "Unknown"]
        ];
        foreach (string[] arguments in invalid)
            Reject(arguments);
        Reject(["--pvf-path"]);
        Reject(["--data-dir", " "]);
        Reject(["--pvf-path", "one.pvf", "--pvf-path", "two.pvf"]);
        Reject(["--data-dir", "bad\0path"]);

        var defaults = ServerOptions.Load();
        That(defaults.PvfPath == Path.Combine(AppContext.BaseDirectory, "Data", "Pvf", "Script.pvf") &&
            defaults.DataDirectory == Path.Combine(AppContext.BaseDirectory, "Data") &&
            defaults.LogDirectory == Path.Combine(AppContext.BaseDirectory, "Logs"), "fixed paths use executable directory");
        That(defaults.GmPort == 0 && defaults.PacketHttpPort == 0 && defaults.DuplicateLoginPolicy == Game.Accounts.DuplicateLoginPolicy.ReplaceExisting, "management endpoints disabled and duplicate login always replaces old connection");
        var custom = ServerOptions.Load(ServerCommand.Parse(["--listen-ip", "127.0.0.1", "--server-ip", "192.168.0.1", "--channel-port", "25000", "--game-port", "25001", "--packet-http-port", "25002", "--session-ttl", "600"]).Options);
        That(custom.ListenAddress == "127.0.0.1" && custom.AdvertisedAddress == "192.168.0.1", "explicit listener wins independent of option order");
        That(custom.ChannelPort == 25000 && custom.GamePort == 25001 && custom.PacketHttpPort == 25002 && custom.SessionTtlSeconds == 600, "supported launch overrides");
        That(ServerOptions.Load(ServerCommand.Parse(["--server-ip", "192.168.0.1"]).Options).ListenAddress == "0.0.0.0", "LAN address enables external listener");
        string root = Path.Combine(Path.GetTempPath(), "S5A9-command-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string workingDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            File.WriteAllText(Path.Combine(root, "server.local.json"), "invalid JSON");
            That(ServerOptions.Load() == defaults, "working directory and local JSON cannot change fixed defaults");
            var paths = ServerOptions.Load(ServerCommand.Parse(["--pvf-path", "resources with spaces/Script.pvf", "--data-dir", "saved data"]).Options);
            That(paths.PvfPath == Path.GetFullPath("resources with spaces/Script.pvf", AppContext.BaseDirectory) &&
                paths.DataDirectory == Path.GetFullPath("saved data", AppContext.BaseDirectory), "relative resource and save paths use executable directory despite working directory changes");
            var absolute = ServerOptions.Load(ServerCommand.Parse(["--pvf-path", Path.Combine(root, "external.pvf"), "--data-dir", root]).Options);
            That(absolute.PvfPath == Path.Combine(root, "external.pvf") && absolute.DataDirectory == root &&
                absolute.LogDirectory == defaults.LogDirectory, "absolute overrides preserve the selected files and fixed log directory");
            That(!Directory.Exists(Path.Combine(root, "Data")), "option validation creates no player data");
        }
        finally
        {
            Environment.CurrentDirectory = workingDirectory;
            Directory.Delete(root, true);
        }
        Console.WriteLine($"PASS {Count - checks} command line assertions");
    }
}
