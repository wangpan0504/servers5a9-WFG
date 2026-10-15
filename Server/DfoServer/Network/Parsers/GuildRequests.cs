using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Parsers;

/// <summary>
/// 创建公会窗口的查重请求：正文 = u32 字节长度 + CP936 文本（与角色名同一编码口径）。
/// 已实测样本：`07 00 00 00 42 55 47 B9 AB BB E1` = 「BUG公会」。
/// </summary>
internal static class GuildRequests
{
    public static string Text(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4) throw new InvalidDataException("公会名称长度缺失");
        int length = BinaryPrimitives.ReadInt32LittleEndian(body);
        if (length < 0 || body.Length != 4 + length) throw new InvalidDataException("公会名称正文宽度错误");
        try
        {
            return CharacterName.Encoding.GetString(body[4..]);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Invalid CP936 guild text", ex);
        }
    }

    /// <summary>
    /// 认证邀请（CMD 0x2E5）：正文 = `u8 大区序号 + u32 名称长度 + CP936 角色名`。
    /// 已实测样本：`02 05 00 00 00 61 64 6D 69 6E` = 大区 2 +「admin」。
    /// 大区序号与窗口下拉一致，服务端原样回送给被邀请方。
    /// </summary>
    public static (byte Region, string Name) Permit(ReadOnlySpan<byte> body)
    {
        if (body.Length < 5) throw new InvalidDataException("认证邀请正文过短");
        byte region = body[0];
        int length = BinaryPrimitives.ReadInt32LittleEndian(body[1..]);
        if (length < 1 || body.Length != 5 + length) throw new InvalidDataException("认证邀请正文宽度错误");
        try
        {
            return (region, CharacterName.Encoding.GetString(body[5..]));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Invalid CP936 invitee name", ex);
        }
    }

    /// <summary>被邀请方答复（CMD 0x2E6）：正文恰好 1 字节，1=接受、0=拒绝/关闭。</summary>
    public static bool PermitReply(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1) throw new InvalidDataException("认证答复正文宽度错误");
        return body[0] != 0;
    }
}
