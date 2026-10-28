using System.Collections.Immutable;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Pvp;

internal readonly record struct PvpMatchingExclusion(byte Job, byte Grow)
{
    public bool IsEmpty => Job == byte.MaxValue && Grow == byte.MaxValue;
    public bool IsValid => IsEmpty || Job <= 15 && Grow <= 5;
    public bool Contains(Character character) => !IsEmpty && character.Job == Job && (character.GrowType & 15) == Grow;
}

// 两个开关按客户端原值划分候选，不把未证名称解释成疲劳扣除或额外奖励授权。
internal sealed record PvpMatchingPreferences(byte Option, byte Final, bool ExclusionsEnabled,
    ImmutableArray<PvpMatchingExclusion> Exclusions)
{
    public static PvpMatchingPreferences Default { get; } = new(0, 0, false,
        [new(255, 255), new(255, 255), new(255, 255), new(255, 255)]);
    public bool IsValid => Option <= 1 && Final <= 1 && !Exclusions.IsDefault && Exclusions.Length == 4 &&
        Exclusions.All(row => row.IsValid);
    public bool SameChoice(PvpMatchingPreferences other) => Option == other.Option && Final == other.Final &&
        ExclusionsEnabled == other.ExclusionsEnabled && Exclusions.SequenceEqual(other.Exclusions);
    public bool Compatible(PvpMatchingPreferences other) => Option == other.Option && Final == other.Final;
    public bool Excludes(Character character) => ExclusionsEnabled && Exclusions.Any(row => row.Contains(character));
}
