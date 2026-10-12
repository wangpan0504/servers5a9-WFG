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
    public int ChannelEntryPortBase { get; init; }
    // 0 表示关闭。监听固定在 127.0.0.1，不跟随 ListenAddress。
    public int GmPort { get; init; }
    public int PacketHttpPort { get; init; }
    public string PvfPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "Data", "Pvf", "Script.pvf");
    public string DataDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "Data");
    public string LogDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "Logs");
    public string? PacketCaptureDirectory { get; init; }
    public string DevelopmentAccount => LoginCredentials.DevelopmentAccount;
    public DuplicateLoginPolicy DuplicateLoginPolicy => DuplicateLoginPolicy.ReplaceExisting;
    public DungeonEntryProbeMode DevelopmentDungeonEntryProbe { get; init; }
    public int SessionTtlSeconds { get; init; } = 300;
    public string ProtocolVersion => "matched-client";

    public static ServerOptions Load(IReadOnlyDictionary<string, string>? arguments = null)
    {
        var value = new ServerOptions();
        if (arguments is not null)
        {
            if (arguments.TryGetValue("--server-ip", out string? address))
            {
                address = ResolveServerIp(address);
                value = value with { AdvertisedAddress = address, ListenAddress = IPAddress.IsLoopback(IPAddress.Parse(address)) ? address : "0.0.0.0" };
            }
            foreach (var (name, argument) in arguments)
            {
                value = name switch
                {
                    "--server-ip" => value,
                    "--listen-ip" => value with { ListenAddress = argument },
                    "--channel-port" => value with { ChannelPort = Number(argument, name) },
                    "--game-port" => value with { GamePort = Number(argument, name) },
                    "--channel-entry-port-base" => value with { ChannelEntryPortBase = Number(argument, name) },
                    "--gm-port" => value with { GmPort = Number(argument, name) },
                    "--packet-http-port" => value with { PacketHttpPort = Number(argument, name) },
                    "--pvf-path" => value with { PvfPath = StoragePath(argument, name) },
                    "--data-dir" => value with { DataDirectory = StoragePath(argument, name) },
                    "--session-ttl" => value with { SessionTtlSeconds = Number(argument, name) },
                    "--dungeon-entry-probe" => value with { DevelopmentDungeonEntryProbe = Mode<DungeonEntryProbeMode>(argument, name) },
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
            throw new InvalidDataException("--server-ip 必须为可达的 IPv4 或 auto");
        return parsed.ToString();
    }
}
