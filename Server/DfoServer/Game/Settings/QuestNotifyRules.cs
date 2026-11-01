using System.Buffers.Binary;
using System.Collections.Immutable;

namespace DfoServer.Game.Settings;

// 原生选角固定四槽；保存请求仅传当前非空项，顺序不可重排。
internal static class QuestNotifyRules
{
    public const int Slots = 4;
    public const int Length = Slots * sizeof(uint);

    public static void Validate(ImmutableArray<byte> entries)
    {
        if (entries.IsDefault || entries.Length != Length)
            throw new InvalidDataException("任务提示书签必须为完整四槽");
        bool empty = false;
        Span<uint> seen = stackalloc uint[Slots];
        for (int slot = 0; slot < Slots; slot++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan()[(slot * sizeof(uint))..]);
            if (id == 0) { empty = true; continue; }
            if (empty || id >= 30000 || seen[..slot].Contains(id))
                throw new InvalidDataException("任务提示书签编号、顺序或重复项无效");
            seen[slot] = id;
        }
    }
}
