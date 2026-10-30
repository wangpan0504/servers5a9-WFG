using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Network.Builders;

// 自定义服务端私有包：class0/opcode0x7FF0，56字节固定体。客户端只在本次房间的原生29之后应用，
// 本包必须由同一连接先于该29入队；身份字段用于拒绝迟到或跨房间的包。
internal static class MonsterAiPackets
{
    public const ushort Opcode = 0x7FF0;
    public const int BodyLength = 56;
    public const int ExtendedBodyLength = 72;
    // "MAI1"
    const uint Magic = 0x3149414D;

    public static byte[] Encode(uint serial, uint dungeonId, uint mapId, uint roomSeed, DungeonPoint position, MonsterAiProfile profile)
    {
        if (serial == 0) throw new ArgumentOutOfRangeException(nameof(serial));
        if (dungeonId == 0) throw new ArgumentOutOfRangeException(nameof(dungeonId));
        if (mapId == 0) throw new ArgumentOutOfRangeException(nameof(mapId));
        bool extended = profile.HitPoints.Mode != MonsterAiMode.Native || profile.Defense.Mode != MonsterAiMode.Native;
        var body = new byte[extended ? ExtendedBodyLength : BodyLength];
        var span = body.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, extended ? 0x3249414D : Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], serial);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], dungeonId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], mapId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], roomSeed);
        span[20] = checked((byte)position.X);
        span[21] = checked((byte)position.Y);
        // [22..24]保留为0。
        Write(span[24..], profile.Warlike);
        Write(span[32..], profile.Sight);
        Write(span[40..], profile.MoveSpeed);
        Write(span[48..], profile.AttackSpeed);
        if (extended)
        {
            Write(span[56..], profile.HitPoints);
            Write(span[64..], profile.Defense);
        }
        return body;

        // 原生模式不携带数字，客户端据此跳过固定值/倍率改写。
        static void Write(Span<byte> destination, MonsterAiValue value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)value.Mode);
            BinaryPrimitives.WriteSingleLittleEndian(destination[4..], value.Mode == MonsterAiMode.Native ? 0 : value.Number);
        }
    }
}
