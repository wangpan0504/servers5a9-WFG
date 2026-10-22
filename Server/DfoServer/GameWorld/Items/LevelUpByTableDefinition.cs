using System.Collections.Immutable;
using DfoServer.GameWorld.Characters;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal readonly record struct LevelUpRange(int Minimum, int Maximum, byte Target);

internal sealed record LevelUpByTableDefinition(ItemTemplate Item, ImmutableArray<LevelUpRange> Ranges,
    ImmutableArray<byte> FirstGrowTypes, bool Jumping,
    bool TownOnly, bool SeriaRoomOnly, int CoolTime)
{
    public const uint JumpingAction = 193;

    public static LevelUpByTableDefinition? Parse(ExperienceConsumableFile file, ItemTemplate template)
    {
        var raw = file.Stackable;
        var action = StackableActionCatalog.Arguments(raw);
        if (file.Diagnostics.Length != 0 || raw.TemplateId != template.Id || template.Kind != ItemKind.Consumable || template.UseForbidden ||
            raw.TypeDeclaration is not { Label: "[etc]", Parameters: [0 or 1] } ||
            file.Effect is not { Label: "[levelUpByTable]", Parameters.Count: > 0 } effect || effect.Parameters.Count % 3 != 0 ||
            file.DeclaredFields.Contains("action type") && action is not { Label: "[jumping lv]", Parameters.Count: 0 } ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            raw.HasCoolTime && raw.DeclaredCoolTime is null || raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 ||
            raw.HasActionUsablePlace && (raw.DeclaredActionUsablePlaces is not { Count: > 0 } places ||
                places.Any(place => place is not ("[village]" or "[seria room]"))) ||
            raw.DeclaredActionUsableServerTypes is { Count: > 0 } || !file.ExcludedJobs.IsEmpty ||
            raw.DeclaredUsableFirstGrowTypes is not { } growTypes || growTypes.Any(value => value is < 0 or > 5) ||
            raw.DeclaredDontChangeGrowTypes is not { Count: 0 } ||
            file.DeclaredFields.Any(field => field is "booster info" or "hp recovery" or "mp recovery" or "exp up level limit"))
            return null;
        var ranges = ImmutableArray.CreateBuilder<LevelUpRange>();
        for (int i = 0; i < effect.Parameters.Count; i += 3)
        {
            int minimum = effect.Parameters[i], maximum = effect.Parameters[i + 1], target = effect.Parameters[i + 2];
            if (minimum < 1 || maximum < minimum || maximum > byte.MaxValue ||
                target is < 2 or > CharacterGrowthCatalog.MaximumSupportedLevel) return null;
            ranges.Add(new(minimum, maximum, (byte)target));
        }
        bool village = raw.DeclaredActionUsablePlaces?.Contains("[village]") == true;
        bool room = raw.DeclaredActionUsablePlaces?.Contains("[seria room]") == true;
        return new(template, ranges.ToImmutable(), growTypes.Select(value => (byte)value).ToImmutableArray(),
            action is not null, village || room, room && !village, raw.DeclaredCoolTime ?? 0);
    }

    // ARAD 按原序匹配第一个包含当前等级的三元组；不排序或合并区间。
    public byte TargetLevel(byte level)
    {
        foreach (var range in Ranges)
            if (level >= range.Minimum && level <= range.Maximum) return range.Target;
        return 0;
    }

    public bool Allows(byte level, byte job, byte growType) => Item.AllowsUse(level, job) &&
        (FirstGrowTypes.IsEmpty || FirstGrowTypes.Contains((byte)(growType & 15))) && TargetLevel(level) > level;
}
