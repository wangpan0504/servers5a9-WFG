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
    ScanQuests,
    InspectPreparation,
    SelfTest,
    SelfTestCore,
    TcpTest,
    Gm,
    PrepareCharacter,
    PrepareBookCharacter,
    ImportCharacters
}

internal sealed record ServerCommand(ServerCommandKind Kind, string[] Arguments, bool PacketCapture = false)
{
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    public const string Usage = """
        用法：DfoServer [运行参数] [命令及参数]
          --server-ip <IPv4|auto|connection> 返回服务端地址；connection 按连接选址，默认 127.0.0.1
          --listen-ip <IPv4>         监听地址；指定非回环 server-ip 时默认 0.0.0.0
          --channel-port <端口>      频道端口，默认 23100
          --game-port <端口>         游戏端口，默认 23101
          --native-port <端口>       Native 独立传输端口，默认 0（自动分配）
          --channel-entry-port-base <端口> 分类入口连续端口起点，默认 0（自动分配）
          --gm-port <端口>           本机 GM 端口，默认 0（关闭）
          --gm-web-port <端口>       嵌入服务进程的 GM 网页，仅监听 127.0.0.1，默认 0（关闭）
          --packet-http-port <端口>  本机只读包记录 HTTP 端口，默认 0（关闭）
          --pvf-path <文件>          资源文件，默认 Data/Pvf/Script.pvf
          --data-dir <目录>          存档目录，默认 Data，数据库名为 game.db
          --session-ttl <秒>         认证元数据有效期，默认 300
          --dungeon-entry-probe <模式>  独立开发诊断，默认 Disabled
          --auction-probe <参数>     拍卖行应答记录布局探针，默认关闭（关闭时走真实挂牌数据）
          --auction-seed <条数>      开发用拍卖行种子条数，默认 0（不种子）；表内已有挂牌时不写入
          --teleport-potion-gates <Open|Pvf> 瞬间移动药剂地下城入口限制，默认 Open（全部开放）
          --adventure-weekly <Off|Slots|Full> 每周地下城信息下发，默认 Slots（721/720），Full 另发719
          --packet-capture [目录]    启动并记录应用层帧
          --selftest-core            基础自测，无需 PVF
          --selftest                 完整服务端自测
          --selftest-tcp [测试组]    隔离 TCP 回归
          --scan-quests              只读任务覆盖与依赖检查，输出JSON
          --gm [命令]               开发管理命令
          --prepare-book <职业> <分支> <角色名> <审计目录> [账号]  开发用：创建指定职业分支的95级二觉角色
          --help                    显示帮助，不读取资源或创建数据库
        默认及相对路径以服务端程序所在目录为基准；运行参数放在命令之前。
        日志固定在程序目录的 Logs；切换存档目录使用该目录的 game.db，不自动搬迁数据。
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
            "--channel-port" or "--game-port" or "--native-port" or "--channel-entry-port-base" or "--gm-port" or "--gm-web-port" or "--packet-http-port" or "--session-ttl" or
            "--dungeon-entry-probe" or "--auction-probe" or "--auction-seed" or "--teleport-potion-gates" or "--adventure-weekly" or "--pvf-path" or "--data-dir")
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
            "--scan-quests" => (ServerCommandKind.ScanQuests, 0),
            "--inspect-jianying" => (ServerCommandKind.InspectPreparation, 0),
            "--selftest-core" => (ServerCommandKind.SelfTestCore, 0),
            "--selftest" => (ServerCommandKind.SelfTest, 0),
            "--selftest-tcp" => (ServerCommandKind.TcpTest, -1),
            "--gm" => (ServerCommandKind.Gm, -1),
            "--prepare-jianying" => (ServerCommandKind.PrepareCharacter, 2),
            "--prepare-book" => (ServerCommandKind.PrepareBookCharacter, -1),
            "--import-characters" => (ServerCommandKind.ImportCharacters, 1),
            _ => throw new InvalidDataException("未知命令：" + action)
        };
        if (argumentCount >= 0 && arguments.Length != argumentCount)
            throw new InvalidDataException($"{action} 需要 {argumentCount} 个参数");
        // 账号是可选尾部参数：4 个参数沿用开发账号，5 个参数建到指定账号。
        if (kind == ServerCommandKind.PrepareBookCharacter && arguments.Length is not (4 or 5))
            throw new InvalidDataException("--prepare-book 需要 4 或 5 个参数");
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
