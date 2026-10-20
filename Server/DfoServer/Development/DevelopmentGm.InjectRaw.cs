using DfoServer.Game.Session;
using DfoServer.Infrastructure;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    /// <summary>
    /// GM <c>inject-raw &lt;连接号&gt; &lt;opcode&gt; &lt;body-hex|-&gt;</c>：把命令行给出的字节原样作为一帧
    /// <c>Kind=0</c>（NOTI）投给指定在线连接，服务端不构造、不解析、不校验、不重试，也不写库。
    /// 用于客户端解析失败时逐个变体试长度字段、编码和结尾字节（例如 <c>NOTI 0x000C</c> 的「信息长度有误」）。
    /// <para>
    /// 投递复用 <c>chat-inject</c> 的同一条发送泵入口（<see cref="ChatProbeConnection.Probe"/>）与就绪守卫：
    /// 连接不存在或尚未进入世界时返回 <c>ERR</c>，不抛异常到 GM 连接。
    /// 回复里的 <c>hex</c> 是剔除空格后的规范大写十六进制，空 body 记为 <c>-</c>；日志只记
    /// <c>inject_raw</c>（连接号、opcode、正文），不产生其他事件。
    /// </para>
    /// </summary>
    static string InjectRaw(DevelopmentRequest request, Func<int, ChatProbeConnection?>? find, ServerLog? log)
    {
        if (find is null)
            throw new InvalidOperationException("原始包体注入仅支持在线服务");
        var target = find(request.Primary) ?? throw new InvalidDataException($"connection {request.Primary} not found");
        // 与 chat-inject 相同的就绪守卫：未进世界时发送泵会丢弃，先明确拒绝，避免回了 OK 却没发帧。
        if (!target.Session.Ready || target.Session.Selected is null)
            throw new InvalidDataException($"connection {request.Primary} not ready");
        // 正文与 opcode 都按命令行原样透传，只在回复与日志里做规范化展示。
        byte[] body = request.RawBody;
        ushort opcode = request.RawOpcode;
        string hex = Convert.ToHexString(body);
        target.Probe(opcode, body, "inject_raw");
        log?.Write("inject_raw", new
        {
            Connection = request.Primary,
            Opcode = $"0x{opcode:X4}",
            Body = hex
        });
        return $"OK inject-raw opcode=0x{opcode:X4} bytes={body.Length} hex={(body.Length == 0 ? "-" : hex)} connection={request.Primary}";
    }
}
