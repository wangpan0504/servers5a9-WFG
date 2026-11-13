using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.MonsterMockBattle;

namespace DfoServer.Network.Builders;

internal static class MonsterMockBattlePackets
{
    public const int InfoLength = 0x3d4;

    public static byte[] Info(MockBattleSnapshot snapshot)
    {
        snapshot.Validate();
        byte[] body = new byte[InfoLength];
        int offset = 0;
        foreach (uint value in snapshot.Parameters6)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), value);
            offset += 4;
        }
        snapshot.Reserved9.AsSpan().CopyTo(body.AsSpan(offset));
        offset += 9;
        foreach (var world in snapshot.Worlds)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), world.Value);
            body[offset + 4] = world.Completed;
            offset += 5;
        }
        foreach (var card in snapshot.Cards)
        {
            body[offset] = unchecked((byte)card.CardId);
            body[offset + 1] = card.Stars;
            body[offset + 2] = unchecked((byte)card.Position);
            body[offset + 3] = card.RawFlags;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset + 4), card.Attributes);
            offset += 8;
        }
        foreach (var achievement in snapshot.Achievements)
        {
            body[offset++] = achievement.Value;
            body[offset++] = achievement.Claimed;
        }
        return body;
    }

    public static byte[] Acknowledge(uint result, uint operation, ReadOnlySpan<byte> cardSlots5,
        ReadOnlySpan<byte> stars5)
    {
        if (cardSlots5.Length != 5 || stars5.Length != 5)
            throw new ArgumentException("怪物模拟战回执必须包含完整五项卡槽及对应星级");
        byte[] body = new byte[19];
        body[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), result);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), operation);
        cardSlots5.CopyTo(body.AsSpan(9));
        stars5.CopyTo(body.AsSpan(14));
        return body;
    }

    public static byte[] Matching(IReadOnlyList<MockBattleOpponent> opponents)
    {
        if (opponents.Count > 3) throw new ArgumentOutOfRangeException(nameof(opponents));
        byte[] body = new byte[174];
        for (int index = 0; index < opponents.Count; index++) Opponent(opponents[index]).CopyTo(body, index * 58);
        return body;
    }

    public static byte[] Opponent(MockBattleOpponent? opponent)
    {
        byte[] body = new byte[58];
        if (opponent is null) return body;
        var cards = opponent.State.Snapshot.Cards.Where(row => !row.IsEmpty && row.Position >= 0)
            .OrderBy(row => row.Position).ToArray();
        if (cards.Length is < 1 or > 5) throw new InvalidDataException("对手阵型数量无效");
        body[0] = checked((byte)cards.Sum(row => row.Stars));
        var encoder = Encoding.UTF8.GetEncoder();
        encoder.Convert(opponent.Name.AsSpan(), body.AsSpan(1, 16), true, out _, out _, out _);
        for (int index = 0; index < 5; index++)
        {
            var card = index < cards.Length ? cards[index] : MockBattleCard.Empty;
            int offset = 18 + index * 8;
            body[offset] = unchecked((byte)card.CardId);
            body[offset + 1] = card.Stars;
            body[offset + 2] = unchecked((byte)card.Position);
            body[offset + 3] = card.RawFlags;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset + 4), card.Attributes);
        }
        return body;
    }

    public static byte[] Spawns(IReadOnlyList<MockBattleSpawn> spawns)
    {
        if (spawns.Count > 10) throw new ArgumentOutOfRangeException(nameof(spawns));
        byte[] body = new byte[1 + spawns.Count * 18];
        body[0] = checked((byte)spawns.Count);
        for (int index = 0; index < spawns.Count; index++)
        {
            var spawn = spawns[index];
            if (spawn.SpawnType > 3) throw new InvalidDataException("活动怪物生成品级超出已确认范围");
            int offset = 1 + index * 18;
            // 00473A50按资源定义的MonsterId查找，卡片编号只用于1766卡册。
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset), spawn.MonsterId);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset + 4), spawn.ObjectKey);
            body[offset + 8] = spawn.RawFlag;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset + 9), spawn.Team);
            // 00479660固定等级95；此字段传给创建器的生成品级，不能再次写入等级。
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset + 13), spawn.SpawnType);
            body[offset + 17] = checked((byte)spawn.Card.Position);
        }
        return body;
    }
}
