using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.Creatures;

internal sealed record CreatureChangeCandidate(int ItemId, int Weight);

internal sealed record CreatureChangeDefinition(int ItemId, int SourceItemId, int MinimumLevel, int MaximumLevel,
    int CreatureMinimumLevel, ImmutableHashSet<int> AllowedItems, ImmutableArray<CreatureChangeCandidate> Candidates)
{
    public const uint Action = 64;
    public const uint RandomAction = 87;
    public int ConsumeCount { get; init; } = 1;
    public bool TownOnly { get; init; } = true;
    public bool Random { get; init; }
    public bool MakePermanent { get; init; }
    public long TotalWeight => Candidates.Sum(row => (long)row.Weight);

    public static CreatureChangeDefinition? Parse(StackableFile raw, ItemTemplate template)
    {
        var action = StackableActionCatalog.Arguments(raw);
        bool random = action?.Label == "[change creature random]";
        if (raw.TemplateId != template.Id || template.Kind != ItemKind.Consumable || template.UseForbidden ||
            raw.TypeDeclaration is not { Label: "[etc]" or "[waste]", Parameters: [0] } ||
            (random ? action!.Parameters.Count < 5 : action is not { Label: "[change creature]", Parameters.Count: 4 }) ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is not >= 1 || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.DeclaredMaximumLevel < (raw.DeclaredMinimumLevel ?? 1) ||
            raw.HasCreatureMinimumLevel && raw.DeclaredCreatureMinimumLevel is not (>= 1 and <= 255) ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 ||
            raw.HasActionUsablePlace && raw.DeclaredActionUsablePlaces is not { Count: > 0 } ||
            raw.DeclaredActionUsablePlaces is { } places &&
                (!places.Contains("[village]") || places.Any(place => place is not ("[village]" or "[seria room]"))) ||
            raw.DeclaredActionLimitIndexes is not { } limits || limits.Any(id => id <= 0) ||
            raw.DeclaredActionResultLimitIndexes is not { } results || results.Count % 3 != 0)
            return null;

        int source = action!.Parameters[0], target = action.Parameters[1];
        var candidates = ImmutableArray.CreateBuilder<CreatureChangeCandidate>();
        if (random)
        {
            int count = action.Parameters[3];
            if (source <= 0 || target <= 0 || action.Parameters[2] != 1 || count <= 0 ||
                count != action.Parameters.Count - 4 || (long)target + count - 1 > int.MaxValue || results.Count != 0 || limits.Count != 0)
                return null;
            for (int i = 0; i < count; i++)
            {
                int weight = action.Parameters[i + 4];
                if (weight < 0) return null;
                candidates.Add(new(target + i, weight));
            }
        }
        else if (source > 0 && target > 0 && action.Parameters[3] == -1 && results.Count == 0)
            candidates.Add(new(target, 1));
        else if (source == -1 && target == -1 && action.Parameters[3] == 0 && limits.Count > 0 && results.Count > 0)
        {
            // 原生解析器保存分组、模板和相对权重。只接入已核对的第0组，不能把权重当成功率。
            for (int i = 0; i < results.Count; i += 3)
            {
                if (results[i] != 0 || results[i + 1] <= 0 || results[i + 2] < 0) return null;
                candidates.Add(new(results[i + 1], results[i + 2]));
            }
        }
        else return null;
        if (candidates.Sum(row => (long)row.Weight) <= 0) return null;

        return new(template.Id, random ? -1 : source, raw.DeclaredMinimumLevel ?? 1, raw.DeclaredMaximumLevel ?? int.MaxValue,
            raw.DeclaredCreatureMinimumLevel ?? 1,
            random ? candidates.Select(row => row.ItemId).Append(source).ToImmutableHashSet() : limits.ToImmutableHashSet(), candidates.ToImmutable())
            { TownOnly = raw.DeclaredActionUsablePlaces is not null, Random = random,
                MakePermanent = !random && source > 0 && action.Parameters[3] == -1 };
    }

    public int Select(long roll)
    {
        if (roll < 0 || roll >= TotalWeight) throw new ArgumentOutOfRangeException(nameof(roll));
        foreach (var candidate in Candidates)
        {
            if (roll < candidate.Weight) return candidate.ItemId;
            roll -= candidate.Weight;
        }
        throw new InvalidOperationException("宠物转换权重与候选不一致");
    }
}
