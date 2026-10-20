namespace DfoServer.Network.Builders;

// 保活包（客户端每 10 秒各发一次）：
//  - CHECK_USER_CONNECTION(0x04FE)：空 body，回同 opcode 成功 ack。
//    86 版参考实现是 Network/Handlers/LoginHandler.cs 的
//    Handle_ENUM_CMDPACKET_CHECK_USER_CONNECTION -> SendPacketAsync(Build(0x01, 0x04DD,
//    CommonPacketBodyBuilder.BuildSuccessAck()))，而 86 版 BuildSuccessAck() 的字节形状就是单字节 0x01。
//    本仓库没有通用 ack builder，因此直接沿用同一形状，不另造结构。
//  - PING(0x06BB)：17 字节客户端时钟/序号数据，86 版没有 handler；本服按“同 opcode 原样回显”处理，
//    不解析、不改写，长度上限与 GameFrame.Build 的 body 上限一致（超过则拒绝回显，不断线）。
internal static class KeepalivePackets
{
    // GameFrame.Build 的 body 上限；超出的回显会在发送泵里抛 InvalidDataException，必须在入队前拦掉。
    public const int MaxEchoLength = 65520;

    public static byte[] SuccessAck() => [1];

    public static byte[] Echo(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxEchoLength)
            throw new InvalidDataException("PING 回显超过单帧 body 上限");
        return body.ToArray();
    }
}
