using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record CollectBoxSlotDefinition(ushort PhysicalSlot, int ItemId, int Metadata);
internal sealed record CollectBoxExtensionDefinition(int ItemId, int RequiredCount);

internal sealed class CollectBoxEntry
{
    readonly ImmutableDictionary<int, CollectBoxSlotDefinition> slotsByItem;
    readonly ImmutableDictionary<int, CollectBoxExtensionDefinition> extensionsByItem;

    public byte Index { get; }
    public int Rows { get; }
    public int SlotsPerRow { get; }
    public long? BaseExpiration { get; }
    public long? MaximumExpiration { get; }
    public ImmutableArray<CollectBoxSlotDefinition> Slots { get; }
    public ImmutableArray<CollectBoxExtensionDefinition> Extensions { get; }

    public CollectBoxEntry(byte index, int rows, int slotsPerRow, long? baseExpiration, long? maximumExpiration,
        IEnumerable<CollectBoxSlotDefinition> slots, IEnumerable<CollectBoxExtensionDefinition> extensions)
    {
        Index = index;
        Rows = rows;
        SlotsPerRow = slotsPerRow;
        BaseExpiration = baseExpiration;
        MaximumExpiration = maximumExpiration;
        Slots = slots.ToImmutableArray();
        Extensions = extensions.ToImmutableArray();
        slotsByItem = Slots.Where(slot => slot.ItemId > 0).ToImmutableDictionary(slot => slot.ItemId);
        extensionsByItem = Extensions.ToImmutableDictionary(extension => extension.ItemId);
    }

    public bool TrySlot(int itemId, out CollectBoxSlotDefinition slot) => slotsByItem.TryGetValue(itemId, out slot!);
    public bool TryExtension(int itemId, out CollectBoxExtensionDefinition extension) =>
        extensionsByItem.TryGetValue(itemId, out extension!);

    public long? ResolveExpiration(long? saved)
    {
        long? resolved = saved ?? BaseExpiration ?? MaximumExpiration;
        return resolved is { } value && MaximumExpiration is { } maximum ? Math.Min(value, maximum) : resolved;
    }
}

internal sealed class CollectBoxCatalog
{
    const string Path = "localization/collectbox/collectboxinfo.etc";
    static readonly TimeSpan ChinaStandardTime = TimeSpan.FromHours(8);
    readonly ImmutableDictionary<byte, CollectBoxEntry> byIndex;

    public ImmutableArray<CollectBoxEntry> Entries { get; }

    public CollectBoxCatalog(PvfArchive archive, ItemCatalog items) : this(archive.ReadText(Path), items.Get) { }

    internal CollectBoxCatalog(string text, Func<int, ItemTemplate>? item = null)
    {
        var entries = ImmutableArray.CreateBuilder<CollectBoxEntry>();
        foreach (Match match in Regex.Matches(text,
                     @"\[collect box info\](.*?)\[/collect box info\]",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            entries.Add(ParseEntry(match.Groups[1].Value));
        if (entries.Count == 0)
            throw new InvalidDataException("PVF宝珠收集箱没有任何定义");
        if (entries.Select(entry => entry.Index).Distinct().Count() != entries.Count)
            throw new InvalidDataException("PVF宝珠收集箱编号重复");
        Entries = entries.OrderBy(entry => entry.Index).ToImmutableArray();
        byIndex = Entries.ToImmutableDictionary(entry => entry.Index);
        if (item is not null)
            ValidateTemplates(item);
    }

    public bool TryGet(byte index, out CollectBoxEntry entry) => byIndex.TryGetValue(index, out entry!);
    public CollectBoxEntry Get(byte index) => TryGet(index, out var entry)
        ? entry : throw new InvalidDataException("宝珠收集箱编号未配置");

    CollectBoxEntry ParseEntry(string block)
    {
        int index = Scalar(block, "index");
        int[] position = PvfScript.Integers(PvfScript.Section(block, "item position info"));
        if (index is < 1 or > byte.MaxValue || position.Length < 2 || position[0] <= 0 || position[1] <= 0)
            throw new InvalidDataException("PVF宝珠收集箱编号或槽位几何无效");
        int count;
        try
        {
            count = checked(position[0] * position[1]);
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("PVF宝珠收集箱槽位数量溢出", error);
        }
        if (count > byte.MaxValue || position.Length < 2 + count * 2)
            throw new InvalidDataException("PVF宝珠收集箱槽位表不完整或超过客户端u8计数");

        var slots = ImmutableArray.CreateBuilder<CollectBoxSlotDefinition>(count);
        for (int at = 0; at < count; at++)
            slots.Add(new(checked((ushort)at), position[2 + at * 2], position[3 + at * 2]));
        var configured = slots.Where(slot => slot.ItemId > 0).Select(slot => slot.ItemId).ToArray();
        if (configured.Distinct().Count() != configured.Length)
            throw new InvalidDataException("PVF同一宝珠收集箱包含重复物品");

        int[] extensionValues = PvfScript.Integers(PvfScript.Section(block, "extend item"));
        if (extensionValues.Length % 2 != 0)
            throw new InvalidDataException("PVF宝珠收集箱续期表必须为物品与数量对");
        var extensions = ImmutableArray.CreateBuilder<CollectBoxExtensionDefinition>(extensionValues.Length / 2);
        for (int at = 0; at < extensionValues.Length; at += 2)
        {
            int itemId = extensionValues[at];
            if (itemId <= 0)
                throw new InvalidDataException("PVF宝珠收集箱续期物品无效");
            extensions.Add(new(itemId, Math.Max(1, extensionValues[at + 1])));
        }
        if (extensions.Select(extension => extension.ItemId).Distinct().Count() != extensions.Count)
            throw new InvalidDataException("PVF同一宝珠收集箱续期物品重复");

        long? baseExpiration = Date(block, "base expiration date");
        long? maximumExpiration = Date(block, "max expiration date");
        if (baseExpiration is { } baseline && maximumExpiration is { } maximum && baseline > maximum)
            throw new InvalidDataException("PVF宝珠收集箱基础期限晚于上限");
        if (extensions.Count > 0 && baseExpiration is null && maximumExpiration is null)
            throw new InvalidDataException("PVF可续期宝珠收集箱缺少期限");
        return new(checked((byte)index), position[0], position[1], baseExpiration, maximumExpiration, slots, extensions);
    }

    void ValidateTemplates(Func<int, ItemTemplate> item)
    {
        foreach (int itemId in Entries.SelectMany(entry => entry.Slots).Where(slot => slot.ItemId > 0).Select(slot => slot.ItemId)
                     .Concat(Entries.SelectMany(entry => entry.Extensions).Select(extension => extension.ItemId)).Distinct())
        {
            var template = item(itemId);
            if (template.Id != itemId || template.Kind is not (ItemKind.Consumable or ItemKind.Material or ItemKind.Quest or
                    ItemKind.Profession or ItemKind.Emblem) ||
                template.StackLimit <= 0 || template.IsPremiumService || template.AcquisitionContract is not null)
                throw new InvalidDataException("PVF宝珠收集箱只允许普通堆叠模板: " + itemId);
        }
        foreach (var extension in Entries.SelectMany(entry => entry.Extensions))
            if (extension.RequiredCount > item(extension.ItemId).StackLimit)
                throw new InvalidDataException("PVF宝珠收集箱续期数量超过模板堆叠上限: " + extension.ItemId);
    }

    static int Scalar(string block, string tag)
    {
        int[] values = PvfScript.Integers(PvfScript.Section(block, tag));
        return values.Length == 1 ? values[0] : throw new InvalidDataException("PVF宝珠收集箱字段必须为单个整数: " + tag);
    }

    static long? Date(string block, string tag)
    {
        string value = PvfScript.Section(block, tag).Trim().Trim('`', '\'', '"').Trim();
        if (value.Length == 0)
            return null;
        value = value.Replace('/', '-').Replace('.', '-');
        string[] formats = ["yyyyMMdd", "yyyyMMddHHmmss", "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"];
        if (!DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new InvalidDataException("PVF宝珠收集箱日期无效: " + tag);
        long unix = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), ChinaStandardTime).ToUnixTimeSeconds();
        return unix > 0 ? unix : throw new InvalidDataException("PVF宝珠收集箱日期超出范围: " + tag);
    }
}
