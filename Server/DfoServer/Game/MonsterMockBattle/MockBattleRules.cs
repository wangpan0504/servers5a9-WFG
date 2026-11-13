using System.Collections.Immutable;
using DfoServer.GameWorld.MonsterMockBattle;

namespace DfoServer.Game.MonsterMockBattle;

internal sealed class MockBattleRejectedException(string message) : InvalidOperationException(message);

internal sealed record MockBattleSettings
{
    public DateOnly? SpecialCalendarStart { get; init; }
}

internal sealed record MockBattleAccount(long Revision, MockBattleSnapshot Snapshot, long HeartAt, string DisplayName)
{
    public const uint HeartLimit = 30;
    public const uint RecoverySeconds = 300;
    public const uint DailyCoins = 100;
    public bool SupplyInitialized { get; init; }
    public int CoinMailDay { get; init; }
    public uint Coins => Snapshot.Parameters6[0];
    public uint Hearts => Snapshot.Parameters6[1];
    public bool NeedsSupplyInitialization => Revision == 0 || !SupplyInitialized && Revision == 1 && Hearts == 0 &&
        Snapshot.Cards.All(card => card.IsEmpty) && Snapshot.Worlds.All(world => world == default) &&
        Snapshot.Achievements.All(progress => progress == default);
    public static MockBattleAccount Create(DateTimeOffset now, string name) =>
        new(0, MockBattleSnapshot.Empty, now.ToUnixTimeSeconds(), name);

    public MockBattleAccount WithCoins(uint coins) => this with
    { Snapshot = Snapshot with { Parameters6 = Snapshot.Parameters6.SetItem(0, coins) } };

    public MockBattleAccount Recover(DateTimeOffset now)
    {
        long seconds = now.ToUnixTimeSeconds();
        long elapsed = seconds <= HeartAt ? 0 : seconds - HeartAt;
        uint hearts = Hearts;
        long heartAt = HeartAt;
        uint remaining = 0;
        if (hearts < HeartLimit)
        {
            uint recovered = (uint)Math.Min(HeartLimit - hearts, elapsed / RecoverySeconds);
            hearts += recovered;
            heartAt += (long)recovered * RecoverySeconds;
            if (hearts < HeartLimit) remaining = RecoverySeconds - (uint)(elapsed % RecoverySeconds);
        }
        // 满额和超额都不积攒恢复时间；回拨时保留已经确认的时点。
        if (hearts >= HeartLimit) heartAt = Math.Max(HeartAt, seconds);
        if (hearts == Hearts && heartAt == HeartAt && Snapshot.Parameters6[4] == remaining) return this;
        return this with
        {
            HeartAt = heartAt,
            Snapshot = Snapshot with { Parameters6 = Snapshot.Parameters6.SetItem(1, hearts).SetItem(4, remaining) }
        };
    }
}

internal sealed record MockBattleMutation(MockBattleAccount After, ImmutableArray<byte> AcquiredSlots,
    int ConsumedItem = 0, MockBattleItemReward? Reward = null);

internal static class MockBattleRules
{
    public static MockBattleMutation Apply(MonsterMockBattleCatalog catalog, MockBattleAccount before,
        uint operation, uint argument0, uint argument1,
        ReadOnlySpan<byte> payload, Func<int, int> draw)
    {
        before.Snapshot.Validate();
        if (payload.Length != 20) throw new MockBattleRejectedException("操作参数长度无效");
        var state = before.Snapshot;
        var cards = state.Cards.ToBuilder();
        var slots = ImmutableArray.CreateBuilder<byte>();
        var after = before;
        int consumedItem = 0;
        MockBattleItemReward? reward = null;
        MockBattleCard Card(uint slot)
        {
            if (slot >= cards.Count || cards[(int)slot].IsEmpty)
                throw new MockBattleRejectedException("卡片槽位不存在");
            return cards[(int)slot];
        }
        void Spend(uint amount)
        {
            if (after.Coins < amount) throw new MockBattleRejectedException("怪物硬币不足");
            after = after.WithCoins(after.Coins - amount);
        }
        void Add(MockBattleDraw card)
        {
            int slot = -1;
            for (int index = 0; index < cards.Count; index++)
                if (cards[index].IsEmpty) { slot = index; break; }
            if (slot < 0) throw new MockBattleRejectedException("卡册已满");
            cards[slot] = new(checked((sbyte)card.CardId), checked((byte)card.Stars), -1, 0, 0);
            slots.Add(checked((byte)slot));
        }
        MockBattleDraw Roll(ImmutableArray<MockBattleDraw> pool)
        {
            int total = checked((int)pool.Sum(row => (long)row.Weight));
            if (total <= 0) throw new MockBattleRejectedException("该卡券需要指定卡片");
            int value = draw(total);
            if (value < 0 || value >= total) throw new InvalidOperationException("抽卡随机值超出范围");
            foreach (var entry in pool)
            {
                if (value < entry.Weight) return entry;
                value -= entry.Weight;
            }
            throw new InvalidOperationException("抽卡权重计算错误");
        }

        switch (operation)
        {
            case 0:
                if (argument0 is not (1 or 5) || argument1 != 0)
                    throw new MockBattleRejectedException("普通召唤数量无效");
                long price = catalog.BuyCosts[0];
                if (argument0 == 5) price = price * 5 * (100 - catalog.BuyCosts[1]) / 100;
                Spend(checked((uint)price));
                for (int index = 0; index < argument0; index++) Add(Roll(catalog.CoinDraws));
                break;
            case 1:
                if (argument0 > int.MaxValue || !catalog.ItemDraws.TryGetValue((int)argument0, out var booster))
                    throw new MockBattleRejectedException("召唤卡券不存在");
                bool selectable = booster.Draws.All(row => row.Weight == 0);
                if (selectable && (argument1 == 0 || argument1 > booster.Draws.Length) || !selectable && argument1 != 0)
                    throw new MockBattleRejectedException("卡券选择无效");
                consumedItem = booster.ItemId;
                Add(selectable ? booster.Draws[(int)argument1 - 1] : Roll(booster.Draws));
                break;
            case 2:
                var released = Card(argument0);
                if (argument1 != 0 || payload.ContainsAnyExcept((byte)0) || released.RawFlags != 0 || released.Position != -1)
                    throw new MockBattleRejectedException("放归卡片参数、锁定或阵位无效");
                cards[(int)argument0] = MockBattleCard.Empty;
                break;
            case 3:
                var target = Card(argument0);
                var source = Card(argument1);
                if (argument0 == argument1 || source.CardId != target.CardId || source.Stars != target.Stars ||
                    target.Stars >= 5 || source.RawFlags != 0 || source.Position != -1)
                    throw new MockBattleRejectedException("合成卡片身份、星级、锁定或阵位无效");
                Spend(checked((uint)catalog.ComposeCosts[target.Stars - 1]));
                cards[(int)argument0] = target with { Stars = (byte)(target.Stars + 1) };
                cards[(int)argument1] = MockBattleCard.Empty;
                break;
            case 4:
                var locked = Card(argument0);
                if (argument1 > 1) throw new MockBattleRejectedException("锁定标志无效");
                cards[(int)argument0] = locked with { RawFlags = (byte)argument1 };
                break;
            case 5:
                var used = new HashSet<byte>();
                var positions = new HashSet<byte>();
                for (int index = 0; index < cards.Count; index++) cards[index] = cards[index] with { Position = -1 };
                // 00478328..00478354实际只复制17B；末个槽号为FF，随后3B由原生清零。
                for (int offset = 0; offset < 16; offset += 2)
                {
                    byte slot = payload[offset], position = payload[offset + 1];
                    if (slot == 255 && position == 255) continue;
                    _ = Card(slot);
                    if (position > 8 || !used.Add(slot) || !positions.Add(position) || used.Count > 5)
                        throw new MockBattleRejectedException("阵型包含无效或重复卡片、阵位，或超过5张卡片");
                    cards[slot] = cards[slot] with { Position = (sbyte)position };
                }
                if (payload[16] != 255 || payload[17] != 0 || payload[18] != 0 || payload[19] != 0)
                    throw new MockBattleRejectedException("阵型尾部无效");
                break;
            case 6:
                var attribute = Card(argument0);
                if (argument1 == 0 || argument1 > attribute.Stars || payload[0] > 1)
                    throw new MockBattleRejectedException("属性选择与星级不符");
                int shift = ((int)argument1 - 1) * 2;
                if ((attribute.Attributes & (3u << shift)) != 0)
                    throw new MockBattleRejectedException("该星级属性已经选择");
                cards[(int)argument0] = attribute with { Attributes = attribute.Attributes | (1u << (shift + payload[0])) };
                break;
            case 9:
                if (argument0 >= state.Achievements.Length)
                    throw new MockBattleRejectedException("成就不存在");
                var definition = catalog.Achievements.Single(row => row.Id == argument0);
                var progress = state.Achievements[(int)argument0];
                if (progress.Claimed != 0 || progress.Value < definition.Required)
                    throw new MockBattleRejectedException("成就未完成或已经领取");
                state = state with { Achievements = state.Achievements.SetItem((int)argument0, progress with { Claimed = 1 }) };
                reward = new(definition.ItemId, definition.Count);
                break;
            default:
                throw new MockBattleRejectedException("该操作不属于卡册变更");
        }
        state = state with { Parameters6 = after.Snapshot.Parameters6, Cards = cards.ToImmutable() };
        after = after with { Snapshot = state };
        // 0047BD60把raw2 bit1解释为教程已完成；只有真实获得卡片后才结束首次引导。
        if (operation is 0 or 1) after = after with { Snapshot = after.Snapshot with
        { Parameters6 = after.Snapshot.Parameters6.SetItem(2, after.Snapshot.Parameters6[2] | 2u) } };
        if (operation is 0 or 1) after = Advance(catalog, after, 1, 0, slots.Count);
        foreach (byte slot in slots)
        {
            var card = after.Snapshot.Cards[slot];
            after = Advance(catalog, after, 4, card.Stars, 1);
            if (catalog.Cards[card.CardId].RawClassification[1] == 1) after = Advance(catalog, after, 6, 0, 1);
        }
        if (operation == 3) after = Advance(catalog, after, 4, after.Snapshot.Cards[(int)argument0].Stars, 1);
        if (operation is 3 or 5 && after.Snapshot.Cards.Where(row => row.Position >= 0).Sum(row => row.Stars) >= 25)
            after = Advance(catalog, after, 5, 25, 1);
        after.Snapshot.Validate();
        return new(after, slots.ToImmutable(), consumedItem, reward);
    }

    public static MockBattleAccount Advance(MonsterMockBattleCatalog catalog, MockBattleAccount account,
        int type, int parameter, int amount)
    {
        var achievements = account.Snapshot.Achievements.ToBuilder();
        foreach (var row in catalog.Achievements)
        {
            if (row.Type != type || row.Parameter != parameter) continue;
            var previous = achievements[row.Id];
            achievements[row.Id] = previous with { Value = (byte)Math.Min(row.Required, (long)previous.Value + amount) };
        }
        int completed = catalog.Achievements.Count(row => row.Id != 20 && achievements[row.Id].Value >= row.Required);
        achievements[20] = achievements[20] with { Value = checked((byte)completed) };
        return account with { Snapshot = account.Snapshot with { Achievements = achievements.ToImmutable() } };
    }
}
