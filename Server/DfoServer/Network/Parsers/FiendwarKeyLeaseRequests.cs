using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Parsers;

internal static class FiendwarKeyLeaseRequests
{
    internal const ushort Opcode = 0x7FF3;
    internal const uint Magic = 0x314C5746;
    internal const int HeaderLength = 80;
    internal static FiendwarKeyLeaseMessage Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length < HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(body) != Magic ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[76..]) != 0)
            throw new InvalidDataException("漩涡对象键握手必须为完整FWL1");
        var kind = (FiendwarKeyLeaseKind)body[4];
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body[6..]);
        if (kind is not (FiendwarKeyLeaseKind.Candidates or FiendwarKeyLeaseKind.Ready or FiendwarKeyLeaseKind.Cancel or FiendwarKeyLeaseKind.Refill) ||
            count > FiendwarKeyLeaseMessage.BatchCapacity || body.Length != HeaderLength + count * 2 ||
            kind == FiendwarKeyLeaseKind.Candidates && count == 0 || kind != FiendwarKeyLeaseKind.Candidates && count != 0 ||
            kind != FiendwarKeyLeaseKind.Cancel && body[5] != 0)
            throw new InvalidDataException("漩涡对象键握手动作或候选容量非法");
        var keys = ImmutableArray.CreateBuilder<ushort>(count);
        for (int index = 0; index < count; index++) keys.Add(BinaryPrimitives.ReadUInt16LittleEndian(body[(HeaderLength + index * 2)..]));
        if (keys.Any(key => key is 0 or ushort.MaxValue) || keys.Distinct().Count() != count)
            throw new InvalidDataException("漩涡对象键候选重复或无效");
        var scene = new FiendwarKeyLeaseScene(new(body[24..40]), BinaryPrimitives.ReadUInt32LittleEndian(body[48..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[52..]), BinaryPrimitives.ReadUInt32LittleEndian(body[56..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[60..]), BinaryPrimitives.ReadUInt32LittleEndian(body[64..]),
            BinaryPrimitives.ReadInt64LittleEndian(body[68..]));
        var handshake = new Guid(body[8..24]);
        ulong lease = BinaryPrimitives.ReadUInt64LittleEndian(body[40..]);
        if (scene.Run == Guid.Empty || handshake == Guid.Empty || lease == 0 || scene.Dungeon == 0 || scene.Map == 0 ||
            scene.Position > ushort.MaxValue || scene.Epoch < 0)
            throw new InvalidDataException("漩涡对象键握手身份非法");
        return new(kind, body[5], handshake, scene, lease, keys.ToImmutable());
    }
}
