using System.Text.Json;
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
        That(ServerOptions.Load().NativePort == 0 &&
            ServerOptions.Load(ServerCommand.Parse(["--native-port", "25003"]).Options).NativePort == 25003,
            "Native 默认由系统分配独立端口，也接受显式端口");
        foreach (string port in new[] { "-1", "65536", "23100", "23101" }) Reject(["--native-port", port]);
        Reject(["--native-port", "25003", "--gm-port", "25003"]);
        Reject(["--native-port", "25003", "--gm-web-port", "25003"]);
        Reject(["--native-port", "25003", "--packet-http-port", "25003"]);
        Reject(["--native-port", "25003", "--channel-entry-port-base", "25003"]);
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
            (["--selftest-tcp-list-json"], ServerCommandKind.TcpTestList),
            (["--gm"], ServerCommandKind.Gm),
            (["--prepare-jianying", "剑影", "audit"], ServerCommandKind.PrepareCharacter),
            (["--import-characters", "roles.txt"], ServerCommandKind.ImportCharacters),
            (["--anton-probe"], ServerCommandKind.AntonProbe),
            (["--pvf-list", "sample.pvf", "character/"], ServerCommandKind.PvfList),
            (["--pvf-text", "sample.pvf", "character/character.lst"], ServerCommandKind.PvfText)
        ];
        foreach (var test in supported)
            That(ServerCommand.Parse(test.Arguments).Kind == test.Kind, string.Join(' ', test.Arguments));
        Reject(["--selftest-tcp-list-json", "extra"]);
        Reject(["--pvf-path", "missing.pvf", "--selftest-tcp-list-json"]);
        Reject(["--data-dir", "unused", "--selftest-tcp-list-json"]);
        using (var registry = JsonDocument.Parse(Testing.TcpRegressionTests.RegistryJson()))
        {
            var data = registry.RootElement;
            That(data.GetProperty("SchemaVersion").GetInt32() == 1 && data.GetProperty("Execution").GetString() == "not-run",
                "测试组导出只是注册表不是执行或覆盖证明");
            string[] Names(string property) => data.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();
            string[] ordered = data.GetProperty("Groups").EnumerateArray().Select(value => value.GetProperty("Name").GetString()!).ToArray();
            That(ordered.Distinct(StringComparer.Ordinal).Count() == ordered.Length, "机器清单的注册组不重复");
            That(Names("DefaultGroups").SequenceEqual(Testing.TcpRegressionTests.DefaultGroupNames), "默认执行与导出顺序相同");
            That(Names("DefaultGroups").Concat(Names("TargetedGroups")).Concat(Names("SurveyGroups")).ToHashSet(StringComparer.Ordinal).SetEquals(ordered),
                "默认/定向/普查分类完整覆盖注册组");
            That(Names("DefaultGroups").Length + Names("TargetedGroups").Length + Names("SurveyGroups").Length == ordered.Length,
                "注册分类之间不重叠");
            foreach (var alias in data.GetProperty("Aliases").EnumerateObject())
                That(alias.Value.EnumerateArray().Select(value => value.GetString()!).SequenceEqual(Testing.TcpRegressionTests.Expand([alias.Name])),
                    "别名导出与实际展开保持相同顺序：" + alias.Name);
            That(ordered.Concat(data.GetProperty("Aliases").EnumerateObject().Select(alias => alias.Name)).SequenceEqual(Testing.TcpRegressionTests.GroupNames),
                "实际接受的全部名称与机器清单相符");
        }
        That(ServerCommand.Parse(["--selftest-tcp", "material-exchange,booster"]).Arguments.SequenceEqual(["material-exchange,booster"]), "TCP group selection preserved");
        That(ServerCommand.Parse(["--anton-probe", "--host", "127.0.0.1", "--channel-port", "24100", "--account", "antonprobe"]).Arguments.SequenceEqual(
            ["--host", "127.0.0.1", "--channel-port", "24100", "--account", "antonprobe"]), "anton probe keeps connection arguments");
        That(Testing.AntonTcpProbe.IsUsageOnly([]) && Testing.AntonTcpProbe.IsUsageOnly(["--help"]) &&
            !Testing.AntonTcpProbe.IsUsageOnly(["--host", "127.0.0.1"]), "anton probe without a host does not connect");
        foreach (string step in new[] { "start", "enter", "phase", "reward", "card" })
            That(Testing.AntonTcpProbe.StopAfterForSteps(step) == step, "探针单步自动执行前置步骤：" + step);
        That(Testing.AntonTcpProbe.StopAfterForSteps("start,enter,phase,reward,card") == "card", "探针完整步骤包含翻牌边界");
        foreach (string steps in new[] { "", "start,", "card,start", "phase,phase", "unknown" })
            Testing.TestCheck.Reject<InvalidDataException>(() => Testing.AntonTcpProbe.StopAfterForSteps(steps), "探针拒绝非法步骤：" + steps);
        That(Testing.AntonTcpProbe.ProbeArgs.Parse(["--host", "127.0.0.1", "--channel-port", "24100", "--account", "antonprobe",
            "--steps", "start,enter,phase,reward,card"]).StopAfter == "card", "探针CLI解析完整步骤");
        Testing.TestCheck.Reject<InvalidDataException>(() => Testing.AntonTcpProbe.ProbeArgs.Parse(
            ["--steps", "card", "--stop-after", "start"]), "探针两种步骤选项互斥，不能静默覆盖");
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
            ["--dungeon-entry-probe", "Unknown"], ["--teleport-potion-gates", "open"], ["--teleport-potion-gates", "Strict"],
            ["--adventure-weekly", "slots"], ["--adventure-weekly", "On"]
        ];
        foreach (string[] arguments in invalid)
            Reject(arguments);
        Reject(["--pvf-path"]);
        That(ServerOptions.Load().TeleportPotionGates == Game.Town.TeleportPotionGateMode.Open &&
            ServerOptions.Load(ServerCommand.Parse(["--teleport-potion-gates", "Pvf"]).Options).TeleportPotionGates == Game.Town.TeleportPotionGateMode.Pvf,
            "瞬间移动药剂入口默认全部开放，--teleport-potion-gates Pvf 恢复白名单");
        That(ServerOptions.Load().AdventureWeekly == Game.Adventure.AdventureWeeklyMode.Slots &&
            ServerOptions.Load(ServerCommand.Parse(["--adventure-weekly", "Full"]).Options).AdventureWeekly == Game.Adventure.AdventureWeeklyMode.Full &&
            ServerOptions.Load(ServerCommand.Parse(["--adventure-weekly", "Off"]).Options).AdventureWeekly == Game.Adventure.AdventureWeeklyMode.Off,
            "每周地下城默认只下发721/720，Full启用719，Off回退旧行为");
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
        var connection = ServerOptions.Load(ServerCommand.Parse(["--server-ip", "connection"]).Options);
        That(connection.AdvertisedAddress == "connection" && connection.ListenAddress == "0.0.0.0",
            "按连接选址模式保留运行策略，默认监听所有 IPv4 接口");
        That(ServerOptions.Load(ServerCommand.Parse(["--listen-ip", "127.0.0.1", "--server-ip", "connection"]).Options).ListenAddress == "127.0.0.1",
            "按连接选址仍尊重显式监听地址");
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
