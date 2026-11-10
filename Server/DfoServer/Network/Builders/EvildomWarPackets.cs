using System.Buffers.Binary;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

internal static class EvildomWarPackets
{
    internal readonly record struct RewardRow(byte Type, int Value0, uint Value1);
    internal sealed record RewardGroup(byte Slot, RewardRow[] Rows);

    // 028F85F6读取成功u8，失败经028F8655读取公共u16；0080A920不使用该值，统一显示dstr 68129。
    // 没有可证的1652业务错误枚举，公共错误域置0；详细拒绝原因只写日志。
    public static byte[] Start(bool accepted) => accepted ? [1] : [0, 0, 0];

    // 0239F4E0按外层队伍槽位建表；00891B80再按每行Type选择该成员的奖励卡种。
    // 首u32为结果用时毫秒；0088CA00将显示上限夹到3600000，0088E1C0拆成分、秒及百分秒。
    // 调用方提供已提交的冻结值；本构包器不重新计时或产生资产。
    public static byte[] Rewards(uint elapsedMilliseconds, IReadOnlyList<RewardGroup> groups)
    {
        if (groups.Count > byte.MaxValue || groups.Any(group => group.Rows.Length > byte.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(groups), "魔界奖励组数及行数必须适配客户端u8");
        byte[] body = new byte[5 + groups.Sum(group => 2 + group.Rows.Length * 9)];
        BinaryPrimitives.WriteUInt32LittleEndian(body, elapsedMilliseconds);
        body[4] = (byte)groups.Count;
        int at = 5;
        foreach (var group in groups)
        {
            body[at++] = group.Slot;
            body[at++] = (byte)group.Rows.Length;
            foreach (var row in group.Rows)
            {
                body[at++] = row.Type;
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at), row.Value0);
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 4), row.Value1);
                at += 8;
            }
        }
        return body;
    }

    public static byte[] Info(byte mode, bool completed, int remainingSeconds, IReadOnlyList<EvildomWarDungeonState> rows)
    {
        byte[] body = new byte[10 + rows.Count * 7];
        // 0080C530的3为成功态；窗口沿0080AC70的原关闭队列收尾，不伪造剩余时间。
        body[0] = completed ? (byte)3 : (byte)2;
        body[1] = mode;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), Math.Max(0, remainingSeconds));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(6), rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            int at = 10 + i * 7;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at), rows[i].Id);
            // 0080D848..0080D88F读i32/u8/u8/u8；+8类型、+C状态的读者已确认。
            // 首个u8按PVF三元组中的普通图/王座标记解释，置信度中；客户端已查读者不依赖它准入。
            body[at + 4] = rows[i].Kind;
            body[at + 5] = rows[i].Attribute;
            body[at + 6] = rows[i].State; // 0090EC60..0090ECEC：0可进入，1战斗中，2已完成，其余不能进入。
        }
        return body;
    }
}
