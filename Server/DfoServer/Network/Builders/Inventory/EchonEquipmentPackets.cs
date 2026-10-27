using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Network.Builders.Inventory;

internal static class EchonEquipmentPackets
{
    public static byte[] Set(ushort character, EchonSet set)
    {
        set.Validate();
        byte[] body = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body, character);
        body[4] = set.Index; body[5] = set.Parts;
        body[6] = set.First; body[7] = set.Second; body[8] = set.Third; body[9] = set.Fourth;
        body[11] = set.Grade;
        return body;
    }
    public static byte[] Clear(ushort character, byte index)
    {
        byte[] body = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body, character);
        body[4] = index;
        return body;
    }
    public static IReadOnlyList<byte[]> Snapshot(Character character) =>
        InventoryEchonEquipmentService.Records(character.Inventory).Select(set => Set(character.Id, set)).ToArray();
}
