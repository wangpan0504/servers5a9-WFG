using System.Collections.Immutable;

namespace DfoServer.Game.MonsterMockBattle;

// 槽位是本活动100格卡册内的索引，与普通背包槽位及物品UID无关。
internal readonly record struct MockBattleCard(sbyte CardId, byte Stars, sbyte Position,
    byte RawFlags, uint Attributes)
{
    public static MockBattleCard Empty => new(-1, 0, -1, 0, 0);
    public bool IsEmpty => CardId == -1;
}

internal readonly record struct MockBattleWorldProgress(uint Value, byte Completed);
internal readonly record struct MockBattleAchievementProgress(byte Value, byte Claimed);

internal sealed record MockBattleSnapshot(ImmutableArray<uint> Parameters6, ImmutableArray<byte> Reserved9,
    ImmutableArray<MockBattleWorldProgress> Worlds, ImmutableArray<MockBattleCard> Cards,
    ImmutableArray<MockBattleAchievementProgress> Achievements)
{
    public static MockBattleSnapshot Empty => new([0, 0, 0, 0, 0, 0],
        ImmutableArray.CreateRange(Enumerable.Repeat((byte)255, 9)),
        ImmutableArray.CreateRange(Enumerable.Repeat(new MockBattleWorldProgress(), 21)),
        ImmutableArray.CreateRange(Enumerable.Repeat(MockBattleCard.Empty, 100)),
        ImmutableArray.CreateRange(Enumerable.Repeat(new MockBattleAchievementProgress(), 21)));

    public void Validate()
    {
        if (Parameters6.Length != 6 || Reserved9.Length != 9 || Worlds.Length != 21 || Cards.Length != 100 || Achievements.Length != 21)
            throw new InvalidDataException("怪物模拟战状态段数量与本版结构不符");
        if (Worlds.Any(value => value.Completed > 1) || Achievements.Any(value => value.Claimed > 1))
            throw new InvalidDataException("怪物模拟战领取状态无效");
        var positions = new HashSet<sbyte>();
        foreach (var card in Cards)
        {
            if (card.IsEmpty)
            {
                if (card != MockBattleCard.Empty) throw new InvalidDataException("怪物模拟战空卡槽携带属性");
                continue;
            }
            if (card.CardId < 0 || card.Stars is < 1 or > 5 || card.Position is < -1 or > 8 ||
                card.Position >= 0 && !positions.Add(card.Position))
                throw new InvalidDataException("怪物模拟战卡片星级、阵位或重复阵位无效");
        }
    }
}
