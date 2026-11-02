using System.Buffers.Binary;
using DfoServer.Game.EpicWish;

namespace DfoServer.Network.Builders;

internal static class EpicWishPackets
{
    public static byte[] Info(EpicWishList wishes)
    {
        wishes.Validate();
        byte[] body = new byte[1 + wishes.Items.Length * sizeof(int)];
        body[0] = (byte)wishes.Items.Length;
        for (int index = 0; index < wishes.Items.Length; index++)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1 + index * sizeof(int)), wishes.Items[index]);
        return body;
    }

    // 010B5090只根据公共成功字节选择保存成功/失败提示，不读取业务尾部。
    public static byte[] Updated(bool accepted) => accepted ? [1] : [0, 0, 0];
}
