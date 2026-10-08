using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Network.Builders;

internal readonly record struct DungeonPlayResultRow(ushort CharacterKey, uint Value, byte Flag);
internal readonly record struct DungeonCardDisplayItem(uint ItemId, uint Value);
internal sealed record DungeonCardSelectionRow(byte FreeOwner, byte PaidOwner,
    IReadOnlyList<DungeonCardDisplayItem> Items, bool AppearanceFlag);

internal static class DungeonSettlementPackets
{
    // 02381210没有直接reader，已解码普通辅助只改变本地统计/阶段或发送请求；不承载结算奖励。
    public static byte[] Clear() => [];

    // ACK69的0233D840没有附加reader；公共success1后普通窗口进入状态2。
    // 这里只编码响应，是否已完成统计/奖励由实例提交事实决定。
    public static byte[] ScoreScrollAccepted() => [1];

    // ACK70公共success后023405A0固定读取8个i16，literal明确为各成员疲劳度。
    // 调用方提供当前成员实际Fatigue和明确的空成员规则，codec不隐填未占用成员。
    public static byte[] CardRightsAccepted(IReadOnlyList<short> memberFatigue)
    {
        if (memberFatigue.Count != 8)
            throw new InvalidDataException("翻牌资格必须提供完整8成员疲劳度快照");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        foreach (short fatigue in memberFatigue)
            writer.Write(fatigue);
        return stream.ToArray();
    }

    // ACK71公共success后仍固定读取8卡行：免费owner/付费owner/count/pairs/外观bool。
    // FF为未选择，value保持原语义；这些显示行不等于物品增量或领取成功事实。
    public static byte[] CardSelectionAccepted(IReadOnlyList<DungeonCardSelectionRow> rows)
    {
        if (rows.Count != 8 || rows.Any(row =>
            (row.FreeOwner > 7 && row.FreeOwner != byte.MaxValue) ||
            (row.PaidOwner > 7 && row.PaidOwner != byte.MaxValue) || row.Items.Count > 127))
            throw new InvalidDataException("翻牌应答必须提供完整8卡行、合法成员owner和最多127个显示条目");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        foreach (var row in rows)
        {
            writer.Write(row.FreeOwner);
            writer.Write(row.PaidOwner);
            writer.Write((byte)row.Items.Count);
            foreach (var item in row.Items)
            {
                writer.Write(item.ItemId);
                writer.Write(item.Value);
            }
            writer.Write((byte)(row.AppearanceFlag ? 1 : 0));
        }
        return stream.ToArray();
    }

    // ACK72的02334F60在success后必读mode/operation，不能只回公共success。
    // 普通mode1：0再挑战、1选择其他副本、2回城；不构造未确认的失败码。
    public static byte[] OrdinaryExitAccepted(byte operation)
    {
        if (operation > 2)
            throw new ArgumentOutOfRangeException(nameof(operation));
        return [1, 1, operation];
    }

    // NOTI34的023816A0直接读宽：u8/u32/四个u8（最后为行数），每行u16/u32/u8。
    // 字段来源与奖励金额归业务；codec不从客户端统计推导等级、通关资格或奖励。
    public static byte[] PlayResult(byte firstFlag, uint value, byte secondFlag, byte thirdFlag, byte fourthFlag,
        IReadOnlyList<DungeonPlayResultRow> rows)
    {
        if (rows.Count > 8 || rows.Any(row => row.CharacterKey == 0) ||
            rows.Select(row => row.CharacterKey).Distinct().Count() != rows.Count)
            throw new InvalidDataException("结算结果必须使用完整且唯一的最多8个角色记录");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(firstFlag);
        writer.Write(value);
        writer.Write(secondFlag);
        writer.Write(thirdFlag);
        writer.Write(fourthFlag);
        writer.Write((byte)rows.Count);
        foreach (var row in rows)
        {
            writer.Write(row.CharacterKey);
            writer.Write(row.Value);
            writer.Write(row.Flag);
        }
        return stream.ToArray();
    }

    public static byte[] BossAccepted(ushort bossKey) => [1, 1, (byte)bossKey, (byte)(bossKey >> 8)];

    public static byte[] PlayResult(ushort actorId, DungeonSettlementPresentation presentation)
    {
        byte[] body = new byte[16];
        body[0] = presentation.RankGrade;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), presentation.ClearTimeMilliseconds);
        body[6] = presentation.RankPoint;
        body[7] = 1; // 正式ClearFact仅在本次完整路线清完后成立。
        body[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(9), actorId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(11), presentation.ClearTimeMilliseconds);
        return body;
    }

    public static byte[] EmptyCardReward(DungeonClearExperienceReward reward, uint monsterExperience)
    {
        // 所有动态名单和翻牌奖励为空。固定经验字段来自已经提交的同一次结算。
        // 本版固定尾部含八个独立bool，不能套用其他版本的210B正文。
        byte[] body = new byte[218];
        BinaryPrimitives.WriteUInt32LittleEndian(body, reward.BaseExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), reward.RankExperience);
        body[192] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(194), monsterExperience);
        return body;
    }

    public static byte[] CardRights()
    {
        byte[] body = new byte[17];
        body[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), 1);
        for (int index = 1; index < 8; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1 + index * 2), ushort.MaxValue);
        return body;
    }

    public static byte[] EmptyCardSelected()
    {
        byte[] body = new byte[33];
        body[0] = 1;
        for (int index = 0; index < 8; index++)
        {
            body[1 + index * 4] = byte.MaxValue;
            body[2 + index * 4] = byte.MaxValue;
        }
        body[1] = 0;
        body[2] = 0;
        return body;
    }

    public static byte[] ResultCommand(byte state, byte option) => [1, state, option];
}
