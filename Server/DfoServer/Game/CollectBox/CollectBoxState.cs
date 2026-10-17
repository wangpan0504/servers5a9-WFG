using System.Collections.Immutable;

namespace DfoServer.Game.CollectBox;

internal readonly record struct CollectedBoxItem(byte Box, ushort PhysicalSlot, int ItemId);
internal readonly record struct CollectBoxExpiration(byte Box, long ExpiresAt);

internal sealed record CollectBoxState(byte SelectedBox, ImmutableArray<CollectedBoxItem> Items,
    ImmutableArray<CollectBoxExpiration> Expirations)
{
    public static CollectBoxState Empty { get; } = new(0, [], []);

    public void Validate()
    {
        if (Items.IsDefault || Expirations.IsDefault ||
            Items.Any(item => item.Box == 0 || item.ItemId <= 0) ||
            Items.Select(item => (item.Box, item.PhysicalSlot)).Distinct().Count() != Items.Length ||
            Items.Select(item => (item.Box, item.ItemId)).Distinct().Count() != Items.Length ||
            Expirations.Any(expiration => expiration.Box == 0 || expiration.ExpiresAt <= 0) ||
            Expirations.Select(expiration => expiration.Box).Distinct().Count() != Expirations.Length)
            throw new InvalidDataException("无效宝珠收集箱状态");
    }

    public bool Same(CollectBoxState other) => SelectedBox == other.SelectedBox &&
        Items.SequenceEqual(other.Items) && Expirations.SequenceEqual(other.Expirations);

    public bool Contains(byte box, int itemId) => Items.Any(item => item.Box == box && item.ItemId == itemId);

    public long? Expiration(byte box)
    {
        var value = Expirations.SingleOrDefault(expiration => expiration.Box == box);
        return value.Box == 0 ? null : value.ExpiresAt;
    }

    public CollectBoxState Select(byte box) => box == SelectedBox ? this : this with { SelectedBox = box };

    public CollectBoxState Add(byte box, ushort physicalSlot, int itemId)
    {
        var next = this with
        {
            Items = Items.Add(new(box, physicalSlot, itemId))
                .OrderBy(item => item.Box).ThenBy(item => item.PhysicalSlot).ToImmutableArray()
        };
        next.Validate();
        return next;
    }

    public CollectBoxState Remove(byte box, ushort physicalSlot, int itemId)
    {
        var target = new CollectedBoxItem(box, physicalSlot, itemId);
        if (!Items.Contains(target))
            throw new InvalidOperationException("宝珠收集箱规划缺少目标物品");
        return this with { Items = Items.Remove(target) };
    }

    public CollectBoxState Extend(byte box, long expiresAt)
    {
        if (expiresAt <= 0)
            throw new InvalidDataException("宝珠收集箱期限必须为正数");
        var previous = Expirations.SingleOrDefault(expiration => expiration.Box == box);
        var values = previous.Box == 0 ? Expirations.Add(new(box, expiresAt)) :
            Expirations.Replace(previous, new(box, expiresAt));
        var next = this with { Expirations = values.OrderBy(expiration => expiration.Box).ToImmutableArray() };
        next.Validate();
        return next;
    }
}
