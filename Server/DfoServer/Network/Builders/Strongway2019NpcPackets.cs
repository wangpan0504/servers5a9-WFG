using System.Buffers.Binary;
using System.Text;

namespace DfoServer.Network.Builders;

internal static class Strongway2019NpcPackets
{
    // 当前地图没有原活动NPC声明；此处为兼容摆放坐标，沿原生639登记及交互，不改地图资源。
    public static byte[] Display(bool active)
    {
        byte[] text = Encoding.ASCII.GetBytes("[map] 38 1 [/map] [npc] 400001051 `[right]` 685 240 0 [/npc]");
        byte[] body = new byte[5 + text.Length];
        body[0] = active ? (byte)0 : (byte)2;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), checked((uint)text.Length));
        text.CopyTo(body, 5);
        return body;
    }
}
