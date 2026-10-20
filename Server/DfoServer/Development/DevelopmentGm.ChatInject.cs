using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    /// <summary>
    /// GM 聊天帧注入（诊断工具）：只给指定在线连接发一帧测试通知（action <c>gm_chat_inject</c>），
    /// 用于核对收发路径、帧到达与文本显示，不需要玩家在客户端打字。不进入正式聊天投递，也不改喇叭扣减。
    /// <para>
    /// 变体决定 opcode 与正文（见 <see cref="ChatProbes"/>）：L1/L2/L3 是 <c>NOTI 0x000C</c>，与正式投递共用
    /// 同一段正文编码（<c>[type][层 id][v][u32 CP936 字节数][CP936 文本]</c>），约定分别传 ASCII / 中文 / 超长文本，
    /// 用于核对文本显示与按字符边界截断；P3E 是 <c>NOTI 0x03E2</c> 制裁弹窗，AAS 是 <c>NOTI 0x06AD</c> 弹框
    /// （标题固定「系统公告」），AAS8/AASU 与 AAS 同布局，只把标题/正文的编码换成 UTF-8 / UTF-16LE（长度仍按字节数）。
    /// 这几个用于验证服务端能否让客户端显示任意文本。字节合同见 <c>Docs/协议/聊天.md</c> 第 2、4 节。
    /// </para>
    /// 层 id 只对 L1/L2/L3 有意义，优先取该连接最近一次 <c>CMD 0x0011</c> 上报值；没有就用 0，并在回复里标 <c>fallback</c>。
    /// AAS 系正文超过 600 字节、标题超过 24 字节，或 0x000C 正文字节数超过 255 时，按字符边界截断，
    /// 回复附 <c>(truncated)</c> 并写日志。
    /// 连接不存在或尚未进入世界时返回 <c>ERR</c>，不抛异常到 GM 连接。
    /// </summary>
    static string InjectChatProbe(DevelopmentRequest request, Func<int, ChatProbeConnection?>? find, ServerLog? log)
    {
        if (find is null)
            throw new InvalidOperationException("聊天帧注入仅支持在线服务");
        if (!ChatProbes.TryParseVariant(request.Variant, out var variant))
            throw new InvalidDataException("聊天帧注入变体必须是 L1/L2/L3/P3E/AAS/AAS8/AASU");
        var target = find(request.Primary) ?? throw new InvalidDataException($"connection {request.Primary} not found");
        // 已认证但未进世界时发送泵的就绪守卫会丢弃；这里先明确拒绝，避免回复 OK 却没发帧。
        if (!target.Session.Ready || target.Session.Selected is null)
            throw new InvalidDataException($"connection {request.Primary} not ready");
        ushort? known = target.Session.LastChatLayer;
        ushort layer = known ?? 0;
        byte[] body = ChatProbes.Body(variant, layer, request.Text, out bool truncated);
        ushort opcode = (ushort)ChatProbes.Noti(variant);
        target.Probe(opcode, body, "gm_chat_inject");
        log?.Write("chat_inject", new
        {
            Connection = request.Primary,
            Variant = variant.ToString(),
            Opcode = $"0x{opcode:X4}",
            Body = Convert.ToHexString(body),
            Truncated = truncated
        });
        // 层 id 只被 0x000C 正文使用；其余变体在回复里改为标出实际 opcode，避免误读。
        string routing = variant is ChatProbeVariant.P3E || ChatProbes.IsAas(variant)
            ? $" opcode=0x{opcode:X4}"
            : $" layer={layer}{(known is null ? "(fallback)" : "")}";
        return $"OK injected variant={variant} bytes={body.Length} body={Convert.ToHexString(body)} connection={request.Primary}{routing}{(truncated ? " (truncated)" : "")}";
    }
}
