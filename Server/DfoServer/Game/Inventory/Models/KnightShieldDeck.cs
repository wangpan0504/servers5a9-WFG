using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

// 图鉴提供使用资格，五个配置位只保存模板编号；主盾的真实核心归穿戴槽24。
internal sealed record KnightShieldDeck(ImmutableArray<int> Items)
{
    public const int Count = 5;
    public static KnightShieldDeck Empty { get; } = new([0, 0, 0, 0, 0]);
    public void Validate()
    {
        if (Items.IsDefault || Items.Length != Count || Items.Any(id => id < 0) ||
            Items.Where(id => id != 0).Distinct().Count() != Items.Count(id => id != 0))
            throw new InvalidDataException("守护者盾牌配置必须是五个不重复的有效模板或空位");
    }
    public bool Same(KnightShieldDeck other) => ReferenceEquals(this, other) || Items == other.Items || Items.SequenceEqual(other.Items);
}
