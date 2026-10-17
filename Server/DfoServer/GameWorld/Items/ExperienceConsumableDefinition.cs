using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ExperienceConsumableDefinition(ItemTemplate Item, bool Percent, int Amount,
    ImmutableArray<ItemMaterialCost> Materials, int CoolTime, int CoolGroup, bool VillageOnly,
    ImmutableArray<string> ExcludedJobs)
{
    public static ExperienceConsumableDefinition? Parse(ExperienceConsumableFile file, ItemTemplate template)
    {
        var raw = file.Stackable;
        if (file.Diagnostics.Length != 0 || raw.TemplateId != template.Id || template.Kind != ItemKind.Consumable || template.UseForbidden ||
            raw.TypeDeclaration is not { Label: "[etc]", Parameters: [0 or 1] } ||
            file.Effect is not { Label: "[expUp]" or "[expPercentUp]", Parameters: [> 0] } effect ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            raw.HasCoolTime && raw.DeclaredCoolTime is null || raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 ||
            raw.HasActionUsablePlace && raw.DeclaredActionUsablePlaces is not ["[village]"] ||
            raw.DeclaredActionUsableServerTypes is { Count: > 0 } ||
            raw.DeclaredNeededMaterials is not { } materials || materials.Count % 2 != 0 ||
            file.DeclaredFields.Any(field => field is "action type" or "booster info" or "hp recovery" or "mp recovery" or
                "exp up level limit" or "usable first growtype" or "dont change grow type"))
            return null;
        var costs = ImmutableArray.CreateBuilder<ItemMaterialCost>();
        for (int index = 0; index < materials.Count; index += 2)
        {
            if (materials[index] <= 0 || materials[index + 1] <= 0) return null;
            costs.Add(new(materials[index], materials[index + 1]));
        }
        var excludedJobs = file.ExcludedJobs.Select(job => job.StartsWith('[') || job.EndsWith(']') ? job : "[" + job + "]").ToImmutableArray();
        if (excludedJobs.Any(job => job != "[all]" && !Enumerable.Range(0, 16).Any(index => ItemCatalog.JobTag((byte)index) == job)))
            return null;
        return new(template, effect.Label == "[expPercentUp]", effect.Parameters[0], costs.ToImmutable(),
            raw.DeclaredCoolTime ?? 0, file.CoolGroup ?? -template.Id, raw.HasActionUsablePlace, excludedJobs);
    }

    public bool Allows(byte level, byte job) => Item.AllowsUse(level, job) &&
        !ExcludedJobs.Contains("[all]") && !ExcludedJobs.Contains(ItemCatalog.JobTag(job));
}
