using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

// NOTI 550 DUNGEON_ENTER_COUNT_INFO（客户端 023CD590 注册 0x226 → 023A0200）：读 u32 副本、u8、u8。
// 副本不等于 0xDC4 且已选角时，第一个 u8 写角色 +0x6C68[副本]（02BBD520），第二个 u8 写 +0x6C7C[副本]（02BBD560）。
// 世界地图 018EC320 取 +0x6C68 作 48010“每日进入次数 %d/%d”的第一个数，上限取 DungeonIncountInfo（03FB9D50）；
// 该行第二值大于 0 时再取 +0x6C7C（018EC378），两者取小。两表默认 0，所以不下发时显示 0/上限。
internal static class DungeonEnterCountPackets
{
    public static byte[] Info(uint dungeon, byte remaining, byte second)
    {
        var body = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(body, dungeon);
        body[4] = remaining;
        body[5] = second;
        return body;
    }
}
