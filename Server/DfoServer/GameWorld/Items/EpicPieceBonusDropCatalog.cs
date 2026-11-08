using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EpicPieceBonusSource(ItemTemplate Template, int Count);

internal sealed class EpicPieceBonusDropCatalog
{
    public const string Path = "etc/chn_epicpiecebonusdropitemlist.etc";
    public bool Available { get; }
    public FrozenDictionary<int, EpicPieceBonusSource> Equipment { get; }
    public FrozenDictionary<int, EpicPieceBonusSource> Stackable { get; }
    public ImmutableArray<int> MissingItems { get; }
    public int MaximumBonus { get; }

    public EpicPieceBonusDropCatalog(PvfArchive archive, ItemCatalog items) : this(archive.Contains(Path)
        ? EpicPieceBonusDropFile.Read(new(archive.ReadTokens(Path), archive.ResolveString)) : null,
        id => items.TryGet(id, out var template) ? template : null) { }

    internal EpicPieceBonusDropCatalog(EpicPieceBonusDropData? data, Func<int, ItemTemplate?> resolve)
    {
        Available = data is not null;
        var missing = ImmutableArray.CreateBuilder<int>();
        Equipment = Bind(data?.Equipment, equipment: true);
        Stackable = Bind(data?.Stackable, equipment: false);
        MissingItems = missing.ToImmutable();
        // 一个属性部位最多穿一件；同种持有物不按堆叠份数重复计算，不把37把武器当成37个并发来源。
        long maximum = Equipment.Values.GroupBy(source => source.Template.EquipmentSlot).Sum(group => (long)group.Max(source => source.Count));
        MaximumBonus = checked((int)(maximum + Stackable.Values.Sum(source => (long)source.Count)));

        FrozenDictionary<int, EpicPieceBonusSource> Bind(IReadOnlyDictionary<int, int>? rows, bool equipment)
        {
            var bound = new Dictionary<int, EpicPieceBonusSource>();
            if (rows is null) return bound.ToFrozenDictionary();
            foreach (var (id, count) in rows)
            {
                var template = resolve(id);
                if (template is null) { missing.Add(id); continue; }
                bool wearable = template.Kind is ItemKind.Equipment or ItemKind.Avatar or ItemKind.Creature or
                    ItemKind.CreatureEquipment or ItemKind.GuildMedal or ItemKind.Talisman or ItemKind.Rune;
                if (wearable != equipment) throw new InvalidDataException("史诗碎片加成来源与装备/堆叠列表类别不符：" + id);
                bound.Add(id, new(template, count));
            }
            return bound.ToFrozenDictionary();
        }
    }
}
