using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ExperienceConsumableDefinition(ItemTemplate Item, bool Percent, int Amount,
    int CoolTime, int CoolGroup, bool VillageOnly,
    ImmutableArray<string> ExcludedJobs)
{
    public ImmutableArray<byte> FirstGrowTypes { get; init; } = [];
    public bool SeriaRoomOnly { get; init; }
    public ImmutableArray<string> Places { get; init; } = [];

    public static ExperienceConsumableDefinition? Parse(ExperienceConsumableFile file, ItemTemplate template)
    {
        var raw = file.Stackable;
        // 原生1034..1037、1231/1232没有数值标签；只对LST中ID与脚本身份同时匹配的旧经验书补默认值。
        var effect = file.Effect;
        if (effect is null && LegacyBookAmount(raw) is > 0 and var legacyAmount)
            effect = new("[expUp]", [legacyAmount]);
        if (file.Diagnostics.Length != 0 || raw.TemplateId != template.Id || template.Kind != ItemKind.Consumable || template.UseForbidden ||
            raw.TypeDeclaration is not { Label: "[etc]", Parameters: [0 or 1] } ||
            effect is not { Label: "[expUp]" or "[expPercentUp]", Parameters: [> 0] } ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            raw.HasCoolTime && raw.DeclaredCoolTime is null || raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 ||
            file.DeclaredFields.Contains("usable first growtype") && raw.DeclaredUsableFirstGrowTypes is not { Count: > 0 } ||
            raw.DeclaredUsableFirstGrowTypes?.Any(value => value is < 0 or > 5) == true ||
            file.DeclaredFields.Any(field => field is "action type" or "booster info" or "hp recovery" or "mp recovery" or
                "exp up level limit" or "dont change grow type"))
            return null;
        var excludedJobs = file.ExcludedJobs.Select(job => job.StartsWith('[') || job.EndsWith(']') ? job : "[" + job + "]").ToImmutableArray();
        if (excludedJobs.Any(job => job != "[all]" && !Enumerable.Range(0, 16).Any(index => ItemCatalog.JobTag((byte)index) == job)))
            return null;
        return new(template, effect.Label == "[expPercentUp]", effect.Parameters[0],
            raw.DeclaredCoolTime ?? 0, file.CoolGroup ?? -template.Id, StackableUsePlaces.TownOnly(raw.DeclaredActionUsablePlaces), excludedJobs)
        {
            FirstGrowTypes = (raw.DeclaredUsableFirstGrowTypes ?? []).Select(value => (byte)value).ToImmutableArray(),
            SeriaRoomOnly = StackableUsePlaces.SeriaRoomOnly(raw.DeclaredActionUsablePlaces),
            Places = raw.DeclaredActionUsablePlaces?.ToImmutableArray() ?? []
        };
    }

    public bool Allows(byte level, byte job, byte growType) => Item.AllowsUse(level, job) &&
        (FirstGrowTypes.IsEmpty || FirstGrowTypes.Contains((byte)(growType & 15))) &&
        !ExcludedJobs.Contains("[all]") && !ExcludedJobs.Contains(ItemCatalog.JobTag(job));

    static int LegacyBookAmount(StackableFile raw)
    {
        (string Path, int Amount)? book = raw.TemplateId switch
        {
            1034 => ("book_exp1.stk", 100), 1035 => ("book_exp2.stk", 1000),
            1036 => ("book_exp3.stk", 10000), 1037 => ("book_exp4.stk", 100000),
            1231 => ("book_exp5.stk", 1000000), 1232 => ("book_exp6.stk", 10000000),
            _ => null
        };
        return book is { } value && raw.ScriptPath.Equals("stackable/" + value.Path, StringComparison.OrdinalIgnoreCase)
            ? value.Amount : 0;
    }
}
