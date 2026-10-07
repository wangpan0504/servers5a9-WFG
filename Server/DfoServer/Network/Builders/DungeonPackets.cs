using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

internal sealed record DungeonDropPacketItem(uint ObjectKey, short RecordSlot, ItemCore Core, ushort X, ushort Y);

internal static class DungeonPackets
{
    // ACK15 成功标志为 1 时，02348EFD 不再读后续字段；这里只用于已有选图界面路径。
    public static byte[] SelectionOpened() => [1];

    public static byte[] SelectionUnavailable() => [0, 1, 0];

    // NOTI27 在 023D8AC0 的空列表足迹：计数字段为 0 时不读列表项，共 42 字节。
    // 顺序是 u32、u32、u8、u32、u8、u8、u8、u32、u16、u16、u32、u8、u8、u32、u32、u8、u16、u8。
    public static byte[] EmptySelectNotice() => new byte[42];

    // ACK16 成功标志为 1 时，0233B36D 不再读后续字段。这只确认导航房间已建立，不代替地图或怪物初始化。
    public static byte[] SelectionAccepted() => [1];

    // NOTI30 在 023842FC/0238434A 读取 u32 和 u8。当前普通路径是五个 0。
    public static byte[] LoadingNotice() => new byte[5];

    // NOTI3：count、角色 ID、state。state 1 才放行已确认的过房发送点。
    public static byte[] DungeonPlayerState(ushort characterId) => [1, (byte)characterId, (byte)(characterId >> 8), 1];

    public static byte[] MoveAccepted() => [1];

    // ACK42 成功标志为 1。回城位置仍由已经确认的 NOTI24 携带。
    public static byte[] GiveUpAccepted() => [1];

    // NOTI38普通路径：monster key/count，每条u32地面对象key、123B物品核心、两个u16位置，再接零掉落同款尾部。
    // 只投影调用方已经确定的地面身份和完整资产；这里不抽奖、不确认死亡、不创建物品或写库。
    public static byte[] MonsterDeath(ushort monsterKey, IReadOnlyList<DungeonDropPacketItem> drops)
    {
        ArgumentNullException.ThrowIfNull(drops);
        const int dropSize = 4 + ItemListProtocolWriter.EntrySize + 4;
        if (drops.Count > (65520 - 9) / dropSize)
            throw new InvalidDataException("死亡掉落通知超过本版帧容量");
        var keys = new HashSet<uint>();
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(monsterKey);
        writer.Write(checked((ushort)drops.Count));
        foreach (var drop in drops)
        {
            if (drop is null || drop.Core is null || !keys.Add(drop.ObjectKey))
                throw new InvalidDataException("死亡掉落缺失物品核心或地面对象key重复");
            // 时装和宠物另有RAW/detail消费者，不能用定长核心冒充完整通知。
            if (drop.Core.Kind is ItemKind.Avatar or ItemKind.Creature)
                throw new NotSupportedException("死亡掉落尚未接入时装或宠物附加记录");
            writer.Write(drop.ObjectKey);
            ItemListProtocolWriter.Write(writer, drop.RecordSlot, drop.Core);
            writer.Write(drop.X);
            writer.Write(drop.Y);
        }
        writer.Write(new byte[] { 0, 0, 255, 0, 0 });
        return bytes.ToArray();
    }
}
