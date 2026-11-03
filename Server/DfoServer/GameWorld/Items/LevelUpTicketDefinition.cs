using PvfLib;
using DfoServer.GameWorld.Characters;
using System.Collections.Immutable;

namespace DfoServer.GameWorld.Items;

internal sealed record LevelUpAmountWeight(int Levels, int Weight);

internal sealed record LevelUpTicketDefinition(int TemplateId, int MinimumLevel, int MaximumLevel, ushort Jobs,
    bool VillageOnly, int CoolTime)
{
    public int Levels { get; init; } = 1;
    public int TargetMaximum { get; init; } = CharacterGrowthCatalog.MaximumSupportedLevel;
    public bool SeriaRoomOnly { get; init; }
    public ImmutableArray<string> Places { get; init; } = [];
    public ImmutableArray<LevelUpAmountWeight> Rolls { get; init; } = [];
    public byte TargetLevel(byte current, int? gainedLevels = null) => checked((byte)Math.Min(TargetMaximum, current + (gainedLevels ?? Levels)));
    public bool AllowsRoll(int levels) => Rolls.IsEmpty ? levels == Levels : Rolls.Any(row => row.Levels == levels && row.Weight > 0);
    public int Roll(Func<long,long> random)
    {
        if (Rolls.IsEmpty) return Levels;
        long total = Rolls.Sum(row => (long)row.Weight), value = random(total);
        if (value < 0 || value >= total) throw new InvalidDataException("升级券随机值超出原始权重区间");
        foreach (var row in Rolls) { if (value < row.Weight) return row.Levels; value -= row.Weight; }
        throw new InvalidDataException("升级券权重没有对应结果");
    }

    public static LevelUpTicketDefinition? Parse(StackableFile raw, ItemTemplate template)
    {
        int minimum = raw.DeclaredMinimumLevel ?? template.MinimumLevel;
        var action = StackableActionCatalog.Arguments(raw);
        if (raw.TemplateId != template.Id || template.Kind != ItemKind.Consumable || template.UseForbidden ||
            action is not { Label: "[level up ticket]" } ||
            action.Parameters is not ([] or [> 0 and <= CharacterGrowthCatalog.MaximumSupportedLevel]) ||
            raw.TypeDeclaration is not { Label: "[etc]", Parameters: [1] } || raw.DeclaredLevelUpRange is not (> 0 and <= CharacterGrowthCatalog.MaximumSupportedLevel) ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || minimum < 0 ||
            raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null || raw.DeclaredMaximumLevel < minimum ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 ||
            raw.HasCoolTime && raw.DeclaredCoolTime is null) return null;
        var rolls = ImmutableArray.CreateBuilder<LevelUpAmountWeight>();
        if (raw.HasBoosterInfo)
        {
            if (raw.DeclaredBoosterEtc is not { Count: >= 4 } entries ||
                entries[0] != 1 || (entries.Count - 1) % 3 != 0) return null;
            for (int i = 1; i < entries.Count; i += 3)
            {
                if (entries[i] != 1 || entries[i+1] < 0 || entries[i+2] < 1 || entries[i+2] > raw.DeclaredLevelUpRange) return null;
                rolls.Add(new(entries[i+2], entries[i+1]));
            }
            if (rolls.Sum(row => (long)row.Weight) <= 0) return null;
        }
        return new(template.Id, minimum, raw.DeclaredMaximumLevel ?? int.MaxValue, template.Jobs,
            StackableUsePlaces.TownOnly(raw.DeclaredActionUsablePlaces), raw.DeclaredCoolTime ?? 0)
        { Levels = raw.DeclaredLevelUpRange.Value, TargetMaximum = action.Parameters.Count == 0 ? CharacterGrowthCatalog.MaximumSupportedLevel : action.Parameters[0],
            SeriaRoomOnly = StackableUsePlaces.SeriaRoomOnly(raw.DeclaredActionUsablePlaces),
            Places = raw.DeclaredActionUsablePlaces?.ToImmutableArray() ?? [], Rolls = rolls.ToImmutable() };
    }

    public bool Allows(byte level, byte job) => level >= MinimumLevel && level <= MaximumLevel && job < 16 &&
        (Jobs & 1 << job) != 0 && TargetLevel(level) > level;
}
