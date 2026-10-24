using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers;

internal readonly record struct AvatarPresetNameRequest(byte Slot, string Name);
internal readonly record struct AvatarPresetSlotRequest(byte Slot);
internal readonly record struct AvatarPresetExchangeRequest(byte From, byte To);

// 0x0639 = slot:u8 + nameLength:u32(字节数) + CP936名字，不带结尾NUL；0x063A = slot:u8；0x063B = from:u8 + to:u8。
// 客户端写入侧：FUN_0095ee80 -> FUN_03bdf8e0 -> FUN_03c783b0/FUN_03bdf850，先写 u32 字节长度再写 CP936 字节。
// 实机正文：00 03 00 00 00 31 31 31（slot 0，"111"）/ 00 0B 00 00 00 D0C2 D7B0 B0E7 D4A4 C9E8 33（"新装扮预设3"）。
internal static class AvatarPresetRequests
{
    // C2S 收到的是 CP936，最长按每字符 2 字节留出上限；同一名称还要能写进 S2C 记录，
    // 客户端名字读取器 FUN_03be25f0 只接受小于 0x100 的字节数，因此按 127 个字符（CP936 最多 254 字节）统一约束。
    // 长度 0 合法：客户端把空名换成自己的默认标签，玩家清空名字等价于恢复默认显示。
    public const int MaximumNameWchars = AvatarPresetState.MaximumNameLength;
    public const int MaximumNameBytes = MaximumNameWchars * 2;

    public static AvatarPresetNameRequest Name(ReadOnlySpan<byte> body)
    {
        if (body.Length < 5)
            throw new InvalidDataException("装扮预设名字长度无效");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(body[1..]);
        if (length > MaximumNameBytes || body.Length != 5 + (int)length)
            throw new InvalidDataException("装扮预设名字长度无效");
        string name;
        try { name = CharacterName.Encoding.GetString(body[5..]); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("装扮预设名字不是合法CP936", error); }
        // 客户端对话框自己会截断；服务端仍按长度与可见字符复核，避免控制字符进入持久化。
        if (name.Length > MaximumNameWchars || name.Any(char.IsControl))
            throw new InvalidDataException("装扮预设名字无效");
        return new(body[0], name);
    }

    public static AvatarPresetSlotRequest Select(ReadOnlySpan<byte> body) =>
        body.Length == 1 ? new(body[0]) : throw new InvalidDataException("选择装扮预设请求必须是单个槽位");

    public static AvatarPresetExchangeRequest Exchange(ReadOnlySpan<byte> body) =>
        body.Length == 2 ? new(body[0], body[1]) : throw new InvalidDataException("交换装扮预设请求必须是两个槽位");
}
