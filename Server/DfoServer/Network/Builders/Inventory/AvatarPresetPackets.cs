using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

// 0x063D 整表：activeSlot:u8 + count:u8 + 记录；0x063F 单条：同一条记录。
// 客户端按首字段恢复已应用槽与当前浏览槽，按记录条数绘制格子，所以整表按 0..capacity-1 补齐：
// 已保存槽发真实记录，空槽发空名、零部位占位；占位只在投影层合成，永不落库。
// 记录 = slot:u8 + nameLength:u32 + name:CP936 + partCount:u8 + 部位；
// 部位记录与客户端读取顺序一致：partSlot:u8, itemId:u32, clearAvatarId:u32(0客户端归一成-1), color1:u16, color2:u16, 5×(type:u16, emblemId:i32)。
internal static class AvatarPresetPackets
{
    public static byte[] List(AvatarPresetState state)
    {
        state.Validate();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(state.ActiveSlot ?? (byte)0);
        writer.Write(state.Capacity);
        for (byte slot = 0; slot < state.Capacity; slot++)
        {
            if (state.Find(slot) is { } preset) WritePreset(writer, preset);
            else WritePlaceholder(writer, slot);
        }
        return stream.ToArray();
    }

    public static byte[] Update(AvatarPreset preset)
    {
        preset.Validate();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WritePreset(writer, preset);
        return stream.ToArray();
    }

    // 空槽占位：名字长度写0，客户端按空名回退到自己的默认标签；零部位表示尚未保存。
    static void WritePlaceholder(BinaryWriter writer, byte slot)
    {
        writer.Write(slot);
        writer.Write(0u);
        writer.Write((byte)0);
    }

    // 名字必须先写u32字节数再写CP936原始字节，不带结尾NUL：客户端FUN_03be25f0按u32字节数原样复制，
    // 显示侧按CP936解码（实机把UTF-16LE字节显示成GBK乱码）；长度0时客户端换成自己的默认标签。
    // 长度≥0x100会让客户端不消费名字字节，后续部位整条错位，写包前必须拒绝。
    static void WritePreset(BinaryWriter writer, AvatarPreset preset)
    {
        var name = preset.Name.Length == 0 ? [] : CharacterName.Encoding.GetBytes(preset.Name);
        writer.Write(preset.Slot);
        writer.Write((uint)name.Length);
        writer.Write(name);
        writer.Write(checked((byte)preset.Parts.Length));
        foreach (var part in preset.Parts)
        {
            writer.Write(part.Slot);
            writer.Write(part.ItemId);
            writer.Write(part.ClearAvatarId);
            writer.Write(part.Color1);
            writer.Write(part.Color2);
            foreach (var socket in part.Sockets)
            {
                writer.Write(socket.Type);
                writer.Write(socket.EmblemId);
            }
        }
    }
}
