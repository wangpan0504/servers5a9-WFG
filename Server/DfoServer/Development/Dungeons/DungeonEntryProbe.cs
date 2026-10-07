using System.Buffers.Binary;
using DfoServer.Game.Dungeons;

namespace DfoServer.Development.Dungeons;

internal enum DungeonEntryProbeMode
{
    Disabled,
    Information,
    StartMap,
    FinishLoading,
    FinishLoadingMonsters
}

internal static class DungeonEntryProbe
{
    // 02395310 的零掉落、普通分支：key/count、可选表标志、mode、grade、附加标志、死亡标志。
    internal static byte[] BuildDeath(ushort key) => [(byte)key, (byte)(key >> 8), 0, 0, 0, 0, 255, 0, 0];
    // 02384210 在当前普通选图路径只读 u32 和 u8，再推进主游戏模块状态；仅供诊断。
    internal static byte[] BuildFinishLoading() => new byte[5];
    // 023D6DE5 按角色 ID 写本人成员状态；C2S45 在状态为零时不会发送。
    internal static byte[] BuildPlayerState(ushort characterId) => [1, (byte)characterId, (byte)(characterId >> 8), 1];
    // 这些包只用于显式启用的进图诊断；字段读宽已核对，候选值仍需客户端验证。
    // 收到成功应答或加载通知不证明进入副本、战斗可用或任务通关。
    internal static byte[] BuildInformation(DungeonEntryPlan plan)
    {
        byte[] body = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(body, checked((uint)plan.DungeonId));
        body[4] = plan.Difficulty;
        // 当前任务迷宫的资源顺序已与运行数组及实例索引对上，仍不外推未知迷宫结构。
        body[7] = checked((byte)plan.Maze.SourceOrdinal);
        body[8] = checked((byte)plan.BossMap.Position.X);
        body[9] = checked((byte)plan.BossMap.Position.Y);

        // FF/FF 在当前迷宫尺寸内不匹配任何合法格；这里只作为待验收的诊断候选。
        body[10] = byte.MaxValue;
        body[11] = byte.MaxValue;

        // 偏移 16 的 u16 沿用当前运行对象值 0；其含义尚未闭合，不套用其他版本的 12。
        // 偏移 20 的 FFFFFFFF 选择已确认的跳过分支；其他未知字段和列表计数保持候选值 0。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), uint.MaxValue);
        return body;
    }

    internal static byte[] BuildStartMap(DungeonEntryPlan plan, uint seed = 1)
    {
        byte[] body = new byte[23];
        body[0] = checked((byte)plan.StartMap.Position.X);
        body[1] = checked((byte)plan.StartMap.Position.Y);
        // 偏移 2 为普通格候选 0；种子默认 1 仅用于可重复诊断，不证明服务端合法默认值。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3), seed);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), 1);

        // 偏移 13 为 1 才消费本候选中的地图和列表段；这不替代 NOTI28 的格记录初始化。
        body[13] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(14), checked((uint)plan.StartMap.Id));
        // 偏移 18/19/21 三个列表计数为 0，偏移 20 为候选值 0，FF 跳过成员选择。
        body[22] = byte.MaxValue;
        return body;
    }

    internal static byte[] BuildStartMap(DungeonEntryPlan plan, DungeonMonsterPlan monsters, uint seed = 1)
    {
        if (monsters.Room.Map != plan.StartMap || monsters.Monsters.Count > byte.MaxValue)
            throw new InvalidDataException("怪物计划与房间不匹配或超过通知数量范围");
        byte[] empty = BuildStartMap(plan, seed);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(empty.AsSpan(0, 18));
        writer.Write(checked((byte)monsters.Monsters.Count));
        foreach (var monster in monsters.Monsters)
        {
            if (monster.CandidateLevel is < 1 or > byte.MaxValue)
                throw new NotSupportedException("怪物候选等级超出单字节范围");
            writer.Write((ushort)0);
            writer.Write(checked((uint)monster.SourceOrdinal));
            writer.Write(monster.ObjectKey);
            writer.Write(checked((uint)monster.MonsterId));
            writer.Write(checked((byte)monster.CandidateLevel));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            // Client 比参考布局多一个必读有符号字节；FF 保留初始创建组，不能省略。
            writer.Write(byte.MaxValue);
            writer.Write(0u);
            writer.Write((byte)0);
        }
        writer.Write(empty.AsSpan(19));
        return stream.ToArray();
    }
}
