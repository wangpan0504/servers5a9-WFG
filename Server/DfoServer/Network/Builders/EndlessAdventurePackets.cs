using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Events;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Network.Builders;

internal static class EndlessAdventurePackets
{
    // V31 0239DE00连读五个DWORD及128B数据串；2257分支将首值交给018489F0并夹到200。
    public static byte[] Progress(int earned)
    {
        if (earned is < 0 or > EndlessAdventurePolicy.DailyLimit) throw new InvalidDataException("活动进度超出200");
        var body = new byte[152];
        BinaryPrimitives.WriteInt32LittleEndian(body, EndlessAdventurePolicy.EventId);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), earned);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(20), 128);
        return body;
    }

    public static EventInfoDesignPage Page() => new(EndlessAdventurePolicy.EventId, 1, "", "", 2, 4,
        "无尽大冒险", "无尽大冒险", "", 0, 0, "fatigue shop event/0/0",
        "通关推荐地下城获得能量硬币，账号每日最多200枚。", true)
        { ShowItemIds = [EndlessAdventurePolicy.CoinId] };

    // 复用原生639动态NPC登记，补当前资源缺少的摆放；不修改玩家客户端或PVF。
    public static byte[] Npc(bool active, TownCatalog towns)
    {
        // V31 的639消费者按单个地图登记解析；疲劳值商店只放在赛丽亚房间，沿已验证的38/1格式发送。
        byte[] bytes = Encoding.ASCII.GetBytes(
            "[map] 38 1 [/map] [npc] 400000023 `[right]` 599 240 0 [/npc]");
        byte[] body = new byte[5 + bytes.Length];
        body[0] = active ? (byte)0 : (byte)2;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), bytes.Length);
        bytes.CopyTo(body, 5);
        return body;
    }
}
