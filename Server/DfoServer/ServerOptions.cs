using DfoServer.Development.Dungeons;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Development;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DfoServer;

internal sealed record ServerOptions
{
    public string ListenAddress { get; init; } = "127.0.0.1";
    public string AdvertisedAddress { get; init; } = "127.0.0.1";
    public int ChannelPort { get; init; } = 23100;
    public int GamePort { get; init; } = 23101;
    // Native 独立传输监听；0 由系统分配，实际端口通过 LOGIN 端点返回。
    public int NativePort { get; init; }
    public int ChannelEntryPortBase { get; init; }
    // 0 表示关闭。监听固定在 127.0.0.1，不跟随 ListenAddress。
    public int GmPort { get; init; }
    // 嵌入服务进程的 GM 网页站点；0 表示关闭。监听固定在 127.0.0.1。
    public int GmWebPort { get; init; }
    public int PacketHttpPort { get; init; }
    public string PvfPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "Data", "Pvf", "Script.pvf");
    public string DataDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "Data");
    public string LogDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "Logs");
    public string? PacketCaptureDirectory { get; init; }
    public string DevelopmentAccount => LoginCredentials.DevelopmentAccount;
    public DuplicateLoginPolicy DuplicateLoginPolicy => DuplicateLoginPolicy.ReplaceExisting;
    public DungeonEntryProbeMode DevelopmentDungeonEntryProbe { get; init; }
    // 拍卖行搜索应答的记录布局探针；null = 关闭（186/187 走真实挂牌数据）。见 Development/Auction/AuctionProbe.cs。
    public string? DevelopmentAuctionProbe { get; init; }
    // 开发用拍卖行种子条数；0 = 不种子（默认）。表内已有挂牌时不再写入。见 Game/Auction/AuctionService.cs。
    public int AuctionSeedCount { get; init; }
    // 瞬间移动药剂地下城入口限制；默认 Open 开放全部入口，Pvf 恢复药剂 `[int data]` 白名单。见 Game/Town/Teleport.cs。
    public Game.Town.TeleportPotionGateMode TeleportPotionGates { get; init; }
    // 每周地下城信息下发；默认 Slots（就绪后回读配置并下发721/720），Full 另发719，Off 回退旧行为。
    public Game.Adventure.AdventureWeeklyMode AdventureWeekly { get; init; } = Game.Adventure.AdventureWeeklyMode.Slots;
    public int SessionTtlSeconds { get; init; } = 300;
    // 自制累计赠礼页面协议独立开关，不改变活动购买资格。
    public bool EnablePromotionPage { get; init; }
    public string ProtocolVersion => "matched-client";

    public static ServerOptions Load(IReadOnlyDictionary<string, string>? arguments = null)
    {
        var value = new ServerOptions();
        if (arguments is not null)
        {
            if (arguments.TryGetValue("--server-ip", out string? address))
            {
                address = ResolveServerIp(address);
                value = value with { AdvertisedAddress = address,
                    ListenAddress = address == "connection" || !IPAddress.IsLoopback(IPAddress.Parse(address)) ? "0.0.0.0" : address };
            }
            foreach (var (name, argument) in arguments)
            {
                value = name switch
                {
                    "--server-ip" => value,
                    "--listen-ip" => value with { ListenAddress = argument },
                    "--channel-port" => value with { ChannelPort = Number(argument, name) },
                    "--game-port" => value with { GamePort = Number(argument, name) },
                    "--native-port" => value with { NativePort = Number(argument, name) },
                    "--channel-entry-port-base" => value with { ChannelEntryPortBase = Number(argument, name) },
                    "--gm-port" => value with { GmPort = Number(argument, name) },
                    "--gm-web-port" => value with { GmWebPort = Number(argument, name) },
                    "--packet-http-port" => value with { PacketHttpPort = Number(argument, name) },
                    "--pvf-path" => value with { PvfPath = StoragePath(argument, name) },
                    "--data-dir" => value with { DataDirectory = StoragePath(argument, name) },
                    "--session-ttl" => value with { SessionTtlSeconds = Number(argument, name) },
                    "--promotion-page" => value with { EnablePromotionPage = argument switch
                    {
                        "Off" => false,
                        "On" => true,
                        _ => throw new InvalidDataException(name + " 必须为 Off 或 On")
                    } },
                    "--dungeon-entry-probe" => value with { DevelopmentDungeonEntryProbe = Mode<DungeonEntryProbeMode>(argument, name) },
                    "--auction-probe" => value with { DevelopmentAuctionProbe = argument },
                    "--auction-seed" => value with { AuctionSeedCount = Number(argument, name) },
                    "--teleport-potion-gates" => value with { TeleportPotionGates = Mode<Game.Town.TeleportPotionGateMode>(argument, name) },
                    "--adventure-weekly" => value with { AdventureWeekly = Mode<Game.Adventure.AdventureWeeklyMode>(argument, name) },
                    _ => throw new InvalidDataException("未知运行参数：" + name)
                };
            }
        }
        if (!IPAddress.TryParse(value.ListenAddress, out var listen) || listen.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("Invalid server IPv4 address");
        if (value.ChannelPort is < 1 or > 65535 || value.GamePort is < 1 or > 65535 || value.GamePort == value.ChannelPort ||
            value.ChannelEntryPortBase is < 0 or > 65535 ||
            value.ChannelEntryPortBase != 0 && new[] { value.ChannelPort, value.GamePort, value.GmPort, value.PacketHttpPort }.Contains(value.ChannelEntryPortBase) ||
            value.GmPort is < 0 or > 65535 || value.GmPort != 0 && (value.GmPort == value.ChannelPort || value.GmPort == value.GamePort) ||
            value.PacketHttpPort is < 0 or > 65535 || value.PacketHttpPort != 0 &&
                new[] { value.ChannelPort, value.GamePort, value.GmPort }.Contains(value.PacketHttpPort))
            throw new InvalidDataException("Invalid endpoint configuration");
        if (value.GmWebPort is < 0 or > 65535 || value.GmWebPort != 0 &&
            new[] { value.ChannelPort, value.GamePort, value.GmPort, value.PacketHttpPort }.Contains(value.GmWebPort) ||
            value.GmWebPort != 0 && value.ChannelEntryPortBase != 0 && value.GmWebPort == value.ChannelEntryPortBase)
            throw new InvalidDataException("Invalid GM web port configuration");
        if (value.NativePort is < 0 or > 65535 || value.NativePort != 0 &&
            (new[] { value.ChannelPort, value.GamePort, value.GmPort, value.GmWebPort, value.PacketHttpPort }.Contains(value.NativePort) ||
             value.ChannelEntryPortBase != 0 && value.NativePort == value.ChannelEntryPortBase))
            throw new InvalidDataException("Invalid native port configuration");
        // 种子逐条写库并在启动时构造条目快照，必须有上限；0 表示不种子。
        if (value.AuctionSeedCount > 10_000)
            throw new InvalidDataException("--auction-seed 必须在 0..10000 之间");
        try
        {
            new AuthenticationSettings(value.DuplicateLoginPolicy, value.SessionTtlSeconds, value.ProtocolVersion).Validate();
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Invalid authentication configuration", error);
        }
        return value;
    }

    static int Number(string argument, string name) => int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out int result)
        ? result : throw new InvalidDataException(name + " 必须为非负整数");

    static string StoragePath(string argument, string name)
    {
        try { return Path.GetFullPath(argument, AppContext.BaseDirectory); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(name + " 路径无效", error);
        }
    }

    static T Mode<T>(string argument, string name) where T : struct, Enum =>
        Enum.GetNames<T>().Contains(argument, StringComparer.Ordinal) && Enum.TryParse<T>(argument, out var result)
            ? result : throw new InvalidDataException(name + " 模式无效");

    static string ResolveServerIp(string argument)
    {
        if (argument == "connection")
            return argument;
        if (argument == "auto")
        {
            argument = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .OrderByDescending(network => network.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))?.ToString()
                ?? throw new InvalidDataException("无法检测本机 LAN IPv4，请显式指定 --server-ip");
        }
        if (!IPAddress.TryParse(argument, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork || parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.Broadcast))
            throw new InvalidDataException("--server-ip 必须为可达的 IPv4、auto 或 connection");
        return parsed.ToString();
    }
}
