using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

internal static class GuildCargoPackets
{
    // 028F82A0公共分派先读1B状态，失败再由03C85220读取u16错误码。
    public static byte[] Failure(ushort error) => [0, (byte)error, (byte)(error >> 8)];

    public static byte[] Transfer(GuildCargoRequest request, GuildCargoCommit commit)
    {
        if (!commit.Success || commit.Replayed) return Failure(commit.Replayed ? request.Deposit ? (ushort)0 : (ushort)8 : commit.Error);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        if (request.Move)
        {
            writer.Write(request.CargoSlot);
            writer.Write(request.TargetSlot);
        }
        else if (request.Deposit)
        {
            writer.Write((byte)request.List);
            writer.Write(request.InventorySlot);
            writer.Write(request.Count);
            writer.Write(request.CargoSlot);
        }
        else
        {
            writer.Write(request.CargoSlot);
            writer.Write((byte)request.List);
            writer.Write(commit.Destination);
            writer.Write(request.Count);
        }
        return stream.ToArray();
    }

    public static byte[] Snapshot(GuildCargoView view, bool success = true)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        if (success) writer.Write((byte)1);
        writer.Write(checked((uint)view.Capacity));
        writer.Write(checked((ushort)view.Items.Length));
        foreach (var item in view.Items.OrderBy(item => item.Position.Index))
            ItemListProtocolWriter.Write(writer, item.Position.Index, item.Core);
        return stream.ToArray();
    }

    public static byte[] History(GuildCargoView view)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(checked((uint)view.History.Length));
        foreach (var history in view.History)
        {
            writer.Write(checked((uint)history.Time));
            writer.Write(history.Kind);
            byte[] name = new byte[30];
            CharacterName.Encoding.GetEncoder().Convert(history.Name.AsSpan(), name.AsSpan(0, 29), true, out _, out _, out _);
            writer.Write(name);
            writer.Write(history.Core.ItemId);
            writer.Write(history.Count);
            using var itemStream = new MemoryStream();
            using var itemWriter = new BinaryWriter(itemStream);
            ItemListProtocolWriter.Write(itemWriter, (short)0, history.Core);
            byte[] item = itemStream.ToArray();
            byte[] random = new byte[14];
            int[] offsets = [0x48, 0x4b, 0x4e, 0x49, 0x4c, 0x4f, 0x4a, 0x4d, 0x50, 0x51, 0x54, 0x55, 0x56, 0x53];
            for (int i = 0; i < offsets.Length; i++) random[i] = item[offsets[i]];
            if (item[0x52] == 0xff) Array.Clear(random, 10, 3);
            writer.Write(random);
        }
        return stream.ToArray();
    }
}
