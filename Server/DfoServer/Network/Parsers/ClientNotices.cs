using System.Buffers.Binary;
using DfoServer.Network.Parsers;

namespace DfoServer.Network;

internal readonly record struct ClientNotice(string Action, string Detail);

internal static class ClientReplies
{
    // category 1 先吃掉成功字节。02365910 在成功后读 u8 数量，数量 0 跳到 02365BB8，不再读 body。
    public static readonly byte[] EmptyBlacklist = [1, 0];
}

// 进房通知在 03BDF610 写完 body 后直接进入 03BE0FF0，发送函数不等待应答。
// C2S 34 的读取器是 02376BF0，成功 body 还没闭合，所以同样只记录、不回包。
// 服务端只核对已闭合的宽度并记录，不改角色或任务。对不上的宽度返回 false，连接保持。
internal static class ClientNotices
{
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, out ClientNotice notice)
    {
        notice = default;
        switch (opcode)
        {
            case (ushort)CmdPacketType.SET_QUEST_TRIGGER when TryQuestTrigger(body, out string trigger):
                // 02280690 在 selector 不是 10/20/40 时写 6 字节。清图达成走这条，不回 ACK33，也不改条件。
                notice = new("quest_trigger", trigger);
                return true;
            case (ushort)CmdPacketType.USE_SKILL when SkillRequests.TryParseUse(body, out var use):
                // 保留10B原码形状和14B实收形状的全部字节；只记录，不回包、不确认伤害或发奖。
                notice = new("use_skill", "bytes=" + Convert.ToHexString(body) + " checksum=" +
                    (use!.ChecksumMatches is null ? "absent" : use.ChecksumMatches.Value ? "matched" : "mismatched"));
                return true;
            case (ushort)CmdPacketType.FINISH_QUEST when TryFinish(body, out string finish):
                notice = new("finish_quest", finish);
                return true;
            case (ushort)CmdPacketType.REQUEST_BLACKLIST when body.IsEmpty:
                notice = new("blacklist_request", "empty");
                return true;
            case (ushort)CmdPacketType.MERCENARY_INFO when body.Length > 0 && body[0] == body.Length - 1:
                notice = new("mercenary_info", "count=" + body[0] + " bytes=" + Convert.ToHexString(body[1..]));
                return true;
            case (ushort)CmdPacketType.SECURITY_STATUS when TryReport(body, out string report):
                notice = new("security_status", report);
                return true;
            case (ushort)CmdPacketType.FPS_PERFORMANCE_STATISTIC when body.Length == 0x298:
                notice = new("fps_statistic", "bytes=664");
                return true;
            default:
                return false;
        }
    }

    static bool TryQuestTrigger(ReadOnlySpan<byte> body, out string detail)
    {
        detail = "";
        if (body.Length != 6 || BinaryPrimitives.ReadUInt16LittleEndian(body) != (ushort)CmdPacketType.SET_QUEST_TRIGGER) return false;
        detail = "quest=" + BinaryPrimitives.ReadUInt16LittleEndian(body[2..])
            + " value=" + body[4]
            + " tail=" + body[5];
        return true;
    }

    static bool TryFinish(ReadOnlySpan<byte> body, out string detail)
    {
        detail = "";
        if (body.Length != 10 || BinaryPrimitives.ReadUInt16LittleEndian(body) != (ushort)CmdPacketType.FINISH_QUEST) return false;
        detail = "quest=" + BinaryPrimitives.ReadUInt16LittleEndian(body[2..])
            + " reward=" + BinaryPrimitives.ReadUInt16LittleEndian(body[4..])
            + " multiplier=" + BinaryPrimitives.ReadUInt16LittleEndian(body[6..])
            + " reserved=" + BinaryPrimitives.ReadUInt16LittleEndian(body[8..]);
        return true;
    }

    static bool TryReport(ReadOnlySpan<byte> body, out string report)
    {
        report = "";
        if (body.Length is 8 or 12)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(body);
            uint second = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
            report = body.Length == 8
                ? "type=" + type + " value=" + second
                : "type=" + type + " value=" + second + " extra=" + BinaryPrimitives.ReadUInt32LittleEndian(body[8..]);
            return true;
        }
        if (body.Length < 8) return false;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        if (size != (uint)(body.Length - 8) || size > 1024) return false;
        uint kind = BinaryPrimitives.ReadUInt32LittleEndian(body);
        report = "type=" + kind + " text=" + Convert.ToHexString(body[8..]);
        return true;
    }
}
