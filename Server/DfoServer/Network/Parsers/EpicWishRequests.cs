using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.EpicWish;

namespace DfoServer.Network.Parsers;

internal static class EpicWishRequests
{
    public static EpicWishList Update(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0 || body[0] > EpicWishList.MaximumItems || body.Length != 1 + body[0] * sizeof(int))
            throw new InvalidDataException("史诗祈愿请求长度无效");
        var ids = ImmutableArray.CreateBuilder<int>(body[0]);
        for (int index = 0; index < body[0]; index++)
            ids.Add(BinaryPrimitives.ReadInt32LittleEndian(body[(1 + index * sizeof(int))..]));
        var wishes = new EpicWishList(ids.MoveToImmutable());
        wishes.Validate();
        return wishes;
    }
}
