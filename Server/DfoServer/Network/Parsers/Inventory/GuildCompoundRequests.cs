using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class GuildCompoundRequests
{
    // 本地客户端发包函数已确认的固定布局：13B 原生前缀（不解释）+ u32 件数 + u32 档位 + 4×u32 材料引用。
    public const int BodySize = 37;
    const int PrefixSize = 13;
    const int MaterialCount = 4;
    const int List38Slots = 98;
    const uint AbsentMaterial = 0xFFFFFFFF;

    public static GuildCompoundRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != BodySize)
            throw new InvalidDataException("公会合成请求正文长度无效");
        uint rawCount = BinaryPrimitives.ReadUInt32LittleEndian(body[PrefixSize..]);
        uint grade = BinaryPrimitives.ReadUInt32LittleEndian(body[(PrefixSize + 4)..]);
        if (rawCount is < 2 or > MaterialCount || grade > int.MaxValue)
            throw new InvalidDataException("公会合成请求材料件数或档位无效");
        int count = (int)rawCount;
        var slots = ImmutableArray.CreateBuilder<int>(count);
        for (int index = 0; index < MaterialCount; index++)
        {
            uint reference = BinaryPrimitives.ReadUInt32LittleEndian(body[(PrefixSize + 8 + index * 4)..]);
            if (index < count)
            {
                if (reference >= List38Slots)
                    throw new InvalidDataException("公会合成材料引用超出list38");
                slots.Add(checked((int)reference));
            }
            else if (reference != AbsentMaterial)
                throw new InvalidDataException("公会合成未使用材料位必须为0xFFFFFFFF");
        }
        return new(checked((int)grade), slots.MoveToImmutable());
    }
}
