using System.Collections.Immutable;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

internal sealed record EpicPieceBook(long Revision, string OrderHash, ImmutableArray<int> Counts)
{
    public static EpicPieceBook Empty { get; } = new(0, "", []);
    public int Get(int index) => Counts.IsEmpty ? 0 : Counts[index];
    public bool Same(EpicPieceBook other) => ReferenceEquals(this, other) ||
        Revision == other.Revision && OrderHash == other.OrderHash && (Counts == other.Counts || Counts.SequenceEqual(other.Counts));

    public void Validate()
    {
        if (Revision < 0 || Counts.IsDefault || Counts.Any(count => count < 0) ||
            (Counts.IsEmpty ? OrderHash.Length != 0 || Revision != 0 : OrderHash.Length != 64 || OrderHash.Any(value => !Uri.IsHexDigit(value))))
            throw new InvalidDataException("无效史诗碎片图鉴");
    }

    public void Validate(EpicPieceCatalog catalog)
    {
        Validate();
        if (!Counts.IsEmpty && (OrderHash != catalog.OrderHash || Counts.Length != catalog.Entries.Length))
            throw new InvalidDataException("史诗碎片图鉴与当前PVF顺序不一致，需要迁移");
    }

    public EpicPieceBook Set(EpicPieceCatalog catalog, int index, int count)
    {
        Validate(catalog);
        if (count < 0 || (uint)index >= catalog.Entries.Length)
            throw new InvalidDataException("无效史诗碎片余额或索引");
        var counts = Counts.IsEmpty ? ImmutableArray.Create(new int[catalog.Entries.Length]) : Counts;
        return this with { OrderHash = catalog.OrderHash, Counts = counts.SetItem(index, count) };
    }

    public int[] ChangedIndexes(EpicPieceBook after) => Same(after) ? [] :
        Enumerable.Range(0, Math.Max(Counts.Length, after.Counts.Length))
            .Where(index => Get(index) != after.Get(index)).ToArray();
}
