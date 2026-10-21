using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Network.Builders.Inventory;

internal static class SkinStoragePackets
{
    public static void WriteUserInfo(BinaryWriter writer, SkinStorageState state, long? now = null)
    {
        // 0267AC80：类别数及每类u8类型/u8数量/u32编号；过期字体回退原生默认1。
        writer.Write((byte)((state.Selected == 0 ? 0 : 1) + (state.SelectedDamageFont == 0 ? 0 : 1) +
            (state.SelectedCutscenes.IsEmpty ? 0 : 1)));
        if (!state.SelectedCutscenes.IsEmpty)
        {
            var selected = state.CutsceneSelection(now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            writer.Write(InventorySkinStorageService.CutsceneType);
            writer.Write(checked((byte)selected.Length));
            foreach (int id in selected) writer.Write(id);
        }
        if (state.SelectedDamageFont != 0)
        {
            writer.Write(InventorySkinStorageService.DamageFontType);
            writer.Write((byte)1);
            writer.Write(state.DamageFontSelection(now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }
        if (state.Selected != 0)
        {
            writer.Write(InventorySkinStorageService.Type);
            writer.Write((byte)1);
            writer.Write(state.Selected);
        }
    }

    public static byte[] Owned(SkinStorageState state)
    {
        state.Validate();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(InventorySkinStorageService.Type);
        writer.Write(checked((ushort)state.Owned.Length));
        foreach (int id in state.Owned) writer.Write(id);
        return stream.ToArray();
    }

    public static byte[] DamageFonts(SkinStorageState state)
    {
        state.Validate();
        return TimedOwned(InventorySkinStorageService.DamageFontType, 1, state.DamageFonts);
    }

    public static byte[] Cutscenes(SkinStorageState state)
    {
        state.Validate();
        return TimedOwned(InventorySkinStorageService.CutsceneType, 30000, state.Cutscenes);
    }

    static byte[] TimedOwned(byte type, int defaultId, IReadOnlyList<SkinOwnership> owned)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(type);
        writer.Write(checked((ushort)(owned.Count + 1)));
        writer.Write(defaultId); writer.Write(0);
        foreach (var row in owned)
        {
            writer.Write(row.SkinId);
            writer.Write(row.ExpiresAt);
        }
        writer.Write((ushort)0); // 1764130：非武器类别还要读取第二份账号期限列表。
        return stream.ToArray();
    }

    public static byte[] Selection(int id, byte type = InventorySkinStorageService.Type)
    {
        var body = new byte[5];
        body[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), id);
        return body;
    }

    public static byte[] SelectedAck(InventoryResult result, int id, int type = InventorySkinStorageService.Type)
    {
        if (!result.Accepted) return CargoPackets.Ack(result);
        var body = new byte[45];
        body[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), type);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5), id);
        return body;
    }

    public static byte[] SelectedAck(InventoryResult result, SelectSkinRequest request)
    {
        if (!result.Accepted) return CargoPackets.Ack(result);
        var body = new byte[45];
        body[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), request.Type);
        int at = 5;
        foreach (int id in request.Ids)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at), id);
            at += 4;
        }
        return body;
    }

    public static byte[] CutsceneSelection(IReadOnlyList<int> selected)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(InventorySkinStorageService.CutsceneType);
        writer.Write(checked((ushort)selected.Count));
        foreach (int id in selected) writer.Write(id);
        return stream.ToArray();
    }

    public static byte[] Recent(int id, byte type = InventorySkinStorageService.Type)
    {
        var body = new byte[6];
        body[0] = 1;
        body[1] = type;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), id);
        return body;
    }
}
