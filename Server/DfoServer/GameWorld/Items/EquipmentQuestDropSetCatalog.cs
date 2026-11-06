using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EquipmentQuestDropSetBonus(int SetId, int RequiredPieces, int Percent);

internal sealed class EquipmentQuestDropSetCatalog
{
    readonly Func<string, PvfTokenDocument> read;
    readonly Func<int, string> resolve;
    readonly Lazy<PvfTokenDocument> registry;
    readonly ConcurrentDictionary<int, Lazy<ImmutableArray<EquipmentQuestDropSetBonus>>> definitions = new();

    public EquipmentQuestDropSetCatalog(PvfArchive archive) : this(path =>
    {
        var tokens = archive.ReadTokens(path);
        // 不解释与本属性无关的旧套装条件块，导入仍需沿原链查找。
        if (path != "etc/equipmentpartset.etc" && !tokens.Any(token => token.Type == 3 &&
            archive.ResolveString(token.Value) is "[quest item drop rate bonus]" or "[import script]"))
            return new([], archive.ResolveString);
        return new(tokens, archive.ResolveString);
    }, archive.ResolveString) { }

    internal EquipmentQuestDropSetCatalog(Func<string, PvfTokenDocument> read, Func<int, string> resolve)
    {
        this.read = read;
        this.resolve = resolve;
        registry = new(() => read("etc/equipmentpartset.etc"));
    }

    public ImmutableArray<EquipmentQuestDropSetBonus> Get(int setId) => definitions.GetOrAdd(setId,
        id => new(() => Read(id))).Value;

    ImmutableArray<EquipmentQuestDropSetBonus> Read(int setId)
    {
        var entries = registry.Value.Sections.Where(row => row.Name == "equipment part set" &&
            !row.Values.IsEmpty && row.Values[0] is { Type: 0 } value && value.Value == setId).ToArray();
        // 套装号不一定注册；只有实际存在的本属性声明才能提供额外来源。
        if (entries.Length == 0) return [];
        if (setId <= 0 || entries.Any(entry => !entry.IsBlock || entry.Values.Length < 2 || entry.Values[1].Type != 6))
            throw new InvalidDataException("任务物品掉落套装目录无效：" + setId);
        ImmutableArray<EquipmentQuestDropSetBonus>? result = null;
        foreach (string source in entries.Select(entry => resolve(entry.Values[1].Value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = source.StartsWith("equipment/", StringComparison.OrdinalIgnoreCase) ? source : "equipment/" + source;
            ImmutableArray<EquipmentQuestDropSetBonus> bonuses = [];
            foreach (var document in AntiEvilUpgradeCatalog.ReadEquipmentChain(read, resolve, path))
            {
                if (!document.Sections.Any(row => row.Name == "piece set ability")) continue;
                bonuses = EquipmentQuestDropBonusFile.ReadSet(document)
                    .Select(tier => new EquipmentQuestDropSetBonus(setId, tier.RequiredPieces, tier.Percent)).ToImmutableArray();
                break;
            }
            // 原资源允许重复注册；无本属性或等价声明不增加新的装备门禁。
            if (result is { } previous && !previous.SequenceEqual(bonuses))
                throw new InvalidDataException("同一套装的任务物品掉落声明不一致：" + setId);
            result = bonuses;
        }
        return result ?? [];
    }
}
