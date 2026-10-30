using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class KnightShieldPackets
{
    // NOTI580/023D0060只读五个i32，既没有数量前缀，也没有角色索引。
    public static byte[] Deck(KnightShieldDeck deck)
    {
        deck.Validate();
        byte[] body = new byte[20];
        for (int index = 0; index < KnightShieldDeck.Count; index++)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(index * 4), deck.Items[index]);
        return body;
    }

    public static byte[] Changed(InventoryResult result)
    {
        if (!result.Accepted) return [0, (byte)result.Error, (byte)(result.Error >> 8)];
        var committed = result.Receipt?.Character.Inventory.ShieldDeck ??
            throw new InvalidOperationException("盾牌成功应答缺少已提交配置");
        // ACK657/0233C393在公共success后读一个字节，再读五个i32。
        return [1, 0, .. Deck(committed)];
    }
}
