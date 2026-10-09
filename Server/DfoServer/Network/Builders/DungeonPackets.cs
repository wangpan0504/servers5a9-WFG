using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

internal sealed record DungeonDropPacketItem(uint ObjectKey, short RecordSlot, ItemCore Core, ushort X, ushort Y);

internal static class DungeonPackets
{
    // ACK15 成功标志为 1 时，02348EFD 不再读后续字段；这里只用于已有选图界面路径。
    public static byte[] SelectionOpened() => [1];

    // 公共 CMD 成功前缀，不混用同号 END_ASSAULT 通知。
    public static byte[] SelectionReturned() => [1];

    public static byte[] SelectionUnavailable() => [0, 1, 0];

    // NOTI27 在 023D8AC0 的空列表足迹：计数字段为 0 时不读列表项，共 42 字节。
    // 顺序是 u32、u32、u8、u32、u8、u8、u8、u32、u16、u16、u32、u8、u8、u32、u32、u8、u16、u8。
    public static byte[] EmptySelectNotice() => new byte[42];

    // ACK16 成功标志为 1 时，0233B36D 不再读后续字段。这只确认导航房间已建立，不代替地图或怪物初始化。
    public static byte[] SelectionAccepted() => [1];

    // ACK16 失败读取 success:u8、error:u16 和 memberSlot:u8；错误9静默恢复选图等待。
    public static byte[] SelectionRejected() => [0, 9, 0, 0];

    // NOTI28的匹配36B布局；调用方提供已审核的准入计划，构包不建立实例或确认加载。
    public static byte[] Information(DungeonEntryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        byte[] body = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(body, checked((uint)plan.DungeonId));
        body[4] = plan.Difficulty;
        body[7] = checked((byte)plan.Maze.SourceOrdinal);
        body[8] = checked((byte)plan.BossMap.Position.X);
        body[9] = checked((byte)plan.BossMap.Position.Y);
        body[10] = byte.MaxValue;
        body[11] = byte.MaxValue;
        // 未解释字段保持匹配形状：offset16的u16为0，offset20的FFFFFFFF选择跳过分支。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), uint.MaxValue);
        return body;
    }

    // NOTI29空名单为23B；seed由调用方显式提供，不能在Builder推导资源或实例随机规则。
    public static byte[] StartMap(DungeonEntryPlan plan, uint seed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        byte[] body = new byte[23];
        body[0] = checked((byte)plan.StartMap.Position.X);
        body[1] = checked((byte)plan.StartMap.Position.Y);
        body[2] = plan.StartMap.Kind == "layered" ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3), seed);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), 1);
        body[13] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(14), checked((uint)plan.StartMap.Id));
        // 三个空列表及fog字段保留0，FF跳过成员选择；不代替NOTI28的格记录初始化。
        body[22] = byte.MaxValue;
        return body;
    }

    // NOTI29每个已规划monster完整22B；等级和类型由领域确定，不在此解释资源行。
    public static byte[] StartMap(DungeonEntryPlan plan, DungeonMonsterPlan monsters, uint seed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(monsters);
        if (monsters.Room.Map != plan.StartMap || monsters.Monsters.Count > byte.MaxValue)
            throw new InvalidDataException("怪物计划与房间不匹配或超过通知数量范围");
        byte[] empty = StartMap(plan, seed);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(empty.AsSpan(0, 18));
        writer.Write(checked((byte)monsters.Monsters.Count));
        foreach (var monster in monsters.Monsters)
        {
            if (monster.CandidateLevel is < 1 or > byte.MaxValue)
                throw new NotSupportedException("怪物生成等级超出单字节范围");
            if (monster.SpawnType is not (<= 3 or >= 5 and <= 8))
                throw new NotSupportedException("演员生成类型不是已核验的怪物或APC类型");
            writer.Write((ushort)0);
            writer.Write(checked((uint)monster.SourceOrdinal));
            writer.Write(monster.ObjectKey);
            writer.Write(checked((uint)monster.MonsterId));
            writer.Write(checked((byte)monster.CandidateLevel));
            writer.Write(monster.SpawnType);
            writer.Write((byte)0);
            writer.Write((byte)0);
            // 行offset16为必读有符号创建组，FF对应-1，不能省略或挪作等级/类型。
            writer.Write(byte.MaxValue);
            writer.Write(0u);
            writer.Write((byte)0);
        }
        writer.Write(empty.AsSpan(19));
        return stream.ToArray();
    }

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
