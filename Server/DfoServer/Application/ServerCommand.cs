using System.Globalization;
using DfoServer.Testing;

namespace DfoServer.Application;

internal enum ServerCommandKind
{
    Serve,
    Help,
    PvfList,
    PvfText,
    InspectTowns,
    InspectDungeon,
    ScanDungeons,
    InspectPreparation,
    SelfTest,
    SelfTestCore,
    TcpTest,
    Gm,
    PrepareCharacter,
    ImportCharacters
}

internal sealed record ServerCommand(ServerCommandKind Kind, string[] Arguments, bool PacketCapture = false)
{
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    public const string Usage = """
        用法：DfoServer [运行参数] [命令及参数]
          --server-ip <IPv4|auto>    返回给客户端的服务端地址，默认 127.0.0.1
          --listen-ip <IPv4>         监听地址；指定非回环 server-ip 时默认 0.0.0.0
          --channel-port <端口>      频道端口，默认 23100
          --game-port <端口>         游戏端口，默认 23101
          --gm-port <端口>           本机 GM 端口，默认 0（关闭）
          --session-ttl <秒>         认证元数据有效期，默认 300
          --dungeon-entry-probe <模式>  独立开发诊断，默认 Disabled
          --packet-capture [目录]    启动并记录应用层帧
          --selftest-core            基础自测，无需 PVF
          --selftest                 完整服务端自测
          --selftest-tcp [测试组]    隔离 TCP 回归
          --gm [命令]               开发管理命令
          --help                    显示帮助，不读取资源或创建数据库
        默认路径以服务端程序所在目录为基准；运行参数放在命令之前。
        PVF 固定为 Data/Pvf/Script.pvf，数据库固定在 Data，日志固定在 Logs。
        重复登录固定替换旧连接，不限制客户端来源 IP，不读取 JSON 运行配置。
        """;

    public static ServerCommand Parse(string[] args)
    {
        if (args.Length != 0 && args[0] is "--pvf-list" or "--pvf-text")
        {
            if (args.Length != 3)
                throw new InvalidDataException("用法：DfoServer --pvf-list/--pvf-text <PVF文件> <路径或关键词>");
            return new(args[0] == "--pvf-list" ? ServerCommandKind.PvfList : ServerCommandKind.PvfText, args[1..]);
        }

        int index = 0;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        while (index < args.Length && args[index] is "--server-ip" or "--listen-ip" or
            "--channel-port" or "--game-port" or "--gm-port" or "--session-ttl" or "--dungeon-entry-probe")
        {
            string name = args[index++];
            if (index == args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new InvalidDataException(name + " 需要一个值");
            if (!options.TryAdd(name, args[index++]))
                throw new InvalidDataException("重复运行参数：" + name);
        }
        if (index == args.Length)
            return new(ServerCommandKind.Serve, []) { Options = options };
        string action = args[index++];
        string[] arguments = args[index..];
        if (action == "--packet-capture")
        {
            if (arguments.Length > 1 || arguments.Length == 1 && (string.IsNullOrWhiteSpace(arguments[0]) || arguments[0].StartsWith("--", StringComparison.Ordinal)))
                throw new InvalidDataException("用法：DfoServer [运行参数] --packet-capture [输出目录]");
            return new(ServerCommandKind.Serve, arguments, PacketCapture: true) { Options = options };
        }
        var (kind, argumentCount) = action switch
        {
            "--help" or "-h" => (ServerCommandKind.Help, 0),
            "--inspect" => (ServerCommandKind.InspectTowns, 0),
            "--inspect-dungeon" => (ServerCommandKind.InspectDungeon, 1),
            "--scan-dungeons" => (ServerCommandKind.ScanDungeons, 0),
            "--inspect-jianying" => (ServerCommandKind.InspectPreparation, 0),
            "--selftest-core" => (ServerCommandKind.SelfTestCore, 0),
            "--selftest" => (ServerCommandKind.SelfTest, 0),
            "--selftest-tcp" => (ServerCommandKind.TcpTest, -1),
            "--gm" => (ServerCommandKind.Gm, -1),
            "--prepare-jianying" => (ServerCommandKind.PrepareCharacter, 2),
            "--import-characters" => (ServerCommandKind.ImportCharacters, 1),
            _ => throw new InvalidDataException("未知命令：" + action)
        };
        if (argumentCount >= 0 && arguments.Length != argumentCount)
            throw new InvalidDataException($"{action} 需要 {argumentCount} 个参数");
        if (kind == ServerCommandKind.TcpTest &&
            (arguments.Length > 1 || arguments.Length == 1 && !TcpRegressionTests.IsValidSelection(arguments[0])))
            throw new InvalidDataException("TCP 测试组必须是以下名称（多个组用逗号分隔，不得重复）：" + string.Join(',', TcpRegressionTests.GroupNames));
        if (kind == ServerCommandKind.InspectDungeon && !int.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException("副本列表索引必须为非负整数");
        if (kind == ServerCommandKind.Help && options.Count != 0)
            throw new InvalidDataException("--help 单独使用");
        return new(kind, arguments) { Options = options };
    }
}
