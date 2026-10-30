using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Network.Builders.Inventory;

internal static class SkinStoragePackets
{
    public static byte[] Emoticon(ushort memberId, int skinId)
    {
        var body = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(body, memberId);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), skinId);
        return body;
    }

    public static void WriteUserInfo(BinaryWriter writer, SkinStorageState state, long? now = null)
    {
        // 0267AC80：类别数及每类u8类型/u8数量/u32编号；过期字体回退原生默认1。
        writer.Write((byte)((state.Selected == 0 ? 0 : 1) + (state.SelectedDamageFont == 0 ? 0 : 1) +
            (state.SelectedCutscenes.IsEmpty ? 0 : 1) + (state.SelectedPartyFrame == 0 ? 0 : 1) +
            (state.SelectedEmoticons.IsEmpty ? 0 : 1)));
        if (state.SelectedPartyFrame != 0)
        {
            writer.Write(InventorySkinStorageService.PartyFrameType);
            writer.Write((byte)1);
            writer.Write(state.PartyFrameSelection(now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }
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
        if (!state.SelectedEmoticons.IsEmpty)
        {
            var selected = state.EmoticonSelection(now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            writer.Write(InventorySkinStorageService.EmoticonType);
            writer.Write(checked((byte)selected.Length));
            foreach (int id in selected) writer.Write(id);
        }
        if (state.Selected != 0)
        {
            writer.Write(InventorySkinStorageService.Type);
            writer.Write((byte)1);
            writer.Write(state.Selected);
        }
    }

    public static byte[] Owned(SkinStorageState state, SkinStorageState? account = null)
    {
        state.Validate(account);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(InventorySkinStorageService.Type);
        writer.Write(checked((ushort)state.Owned.Length));
        foreach (int id in state.Owned) writer.Write(id);
        return stream.ToArray();
    }

    public static byte[] DamageFonts(SkinStorageState state, SkinStorageState account)
    {
        state.Validate(account);
        account.Validate();
        return TimedOwned(InventorySkinStorageService.DamageFontType, 1, state.DamageFonts, account.DamageFonts);
    }

    public static byte[] Cutscenes(SkinStorageState state, SkinStorageState account)
    {
        state.Validate(account);
        account.Validate();
        return TimedOwned(InventorySkinStorageService.CutsceneType, 30000, state.Cutscenes, account.Cutscenes);
    }

    public static byte[] PartyFrames(SkinStorageState state, SkinStorageState account)
    {
        state.Validate(account);
        account.Validate();
        return TimedOwned(InventorySkinStorageService.PartyFrameType, 20000, state.PartyFrames, account.PartyFrames);
    }

    public static byte[] Emoticons(SkinStorageState state, SkinStorageState account)
    {
        state.Validate(account);
        account.Validate();
        return TimedOwned(InventorySkinStorageService.EmoticonType, 0, state.Emoticons, account.Emoticons);
    }

    // 即时表情没有原生默认皮肤，defaultId为0时不补默认条目。
    static byte[] TimedOwned(byte type, int defaultId, IReadOnlyList<SkinOwnership> owned, IReadOnlyList<SkinOwnership> account)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(type);
        writer.Write(checked((ushort)(owned.Count + (defaultId == 0 ? 0 : 1))));
        if (defaultId != 0) { writer.Write(defaultId); writer.Write(0); }
        foreach (var row in owned)
        {
            writer.Write(row.SkinId);
            writer.Write(row.ExpiresAt);
        }
        // 1764130：非武器类别随后读取第二份账号皮肤仓期限列表。
        writer.Write(checked((ushort)account.Count));
        foreach (var row in account)
        {
            writer.Write(row.SkinId);
            writer.Write(row.ExpiresAt);
        }
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

    // 1766550 case 3：即时表情固定读取4个u32，没有数量字段。
    public static byte[] EmoticonSelection(IReadOnlyList<int> selected)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(InventorySkinStorageService.EmoticonType);
        for (int at = 0; at < InventorySkinStorageService.EmoticonSlots; at++)
            writer.Write(at < selected.Count ? selected[at] : 0);
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

    public static byte[] Recent(IReadOnlyList<SkinStorageKey> keys)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(checked((byte)keys.Count));
        foreach (var key in keys) { writer.Write(key.Type); writer.Write(key.SkinId); }
        return stream.ToArray();
    }
}
