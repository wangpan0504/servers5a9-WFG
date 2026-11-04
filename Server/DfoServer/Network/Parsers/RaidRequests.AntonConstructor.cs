using System.Buffers.Binary;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Parsers;

internal static partial class RaidRequests
{
    // 01DC6180：内部SET 1000映射线上0，减法3映射线上2；数值1000不是操作码。
    internal static AntonConstructorLast? AntonConstructorLast(ReadOnlySpan<byte> body)
    {
        if (body.Length != 9) return null;
        uint symbol = BinaryPrimitives.ReadUInt32LittleEndian(body);
        uint operand = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        if (symbol == 110 && body[8] == 2)
            return operand switch { 1000 => new(AntonConstructorEffect.Energy), 500 => new(AntonConstructorEffect.InfectionEnergy), _ => null };
        return symbol is >= 20000 and <= 20003 && operand == 1 && body[8] == 0
            ? new(AntonConstructorEffect.VolcanoSignal, checked((int)symbol - 20000 + 221)) : null;
    }
}
