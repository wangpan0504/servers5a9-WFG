using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record PartnerTeleportDefinition(ActionConsumableDefinition Source,
    ImmutableArray<ItemMaterialCost> PurchaseMaterials)
{
    public const uint Action = 93;

    public static PartnerTeleportDefinition? Parse(int id, string path, PvfToken[] tokens,
        Func<int, string> resolve, ItemTemplate item)
    {
        var file = RecoveryConsumableFile.Parse(id, path, tokens, resolve);
        var types = new PvfTokenDocument(tokens, resolve).Sections.Where(section => section.Name == "stackable type").ToArray();
        // 本版守护券重复声明相同的[waste] 0；只合并已逐项验证的同值声明，冲突值仍拒绝。
        if (types.Length > 1)
        {
            if (types.Any(section => section.Children.Length != 0 || section.Values.Length != 2 ||
                section.Values[0].Type != 6 || resolve(section.Values[0].Value) != "[waste]" ||
                section.Values[1].Type != 0 || section.Values[1].Value != 0)) return null;
            file = file with { Stackable = file.Stackable with { TypeDeclaration = new("[waste]", [0]) } };
        }
        return Parse(file, item);
    }

    public static PartnerTeleportDefinition? Parse(RecoveryConsumableFile file, ItemTemplate item)
    {
        var raw = file.Stackable;
        if (raw.TemplateId != item.Id || item.Kind != ItemKind.Consumable || item.UseForbidden ||
            file.StructureDiagnostics.Length != 0 ||
            raw.TypeDeclaration is not { Label: "[waste]", Parameters: [0] } ||
            StackableActionCatalog.Arguments(raw) is not { Label: "[teleport to partner]", Parameters: [1] } ||
            raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            raw.DeclaredActionUsablePlaces is not { Count: > 0 } places ||
            places.Any(place => place is not ("[village]" or "[seria room]")) ||
            file.DeclaredFields.Contains("cool time") && file.CoolTime is null or < 0 ||
            file.DeclaredFields.Contains("cooltime group") && file.CoolGroup is null or < 0 ||
            file.DeclaredFields.Contains("cooltime maintenance") && file.CooltimeMaintenance is null or > 7 ||
            file.DeclaredFields.Contains("total usable count") && file.TotalUsableCount is null or < 0 or > byte.MaxValue ||
            file.DeclaredFields.Any(field => field is "action usable server type" or "usable game server type" or
                "usable server type" or "booster info" or "increase status type" or "hp recovery" or "mp recovery" or
                "stat change" or "stat change duration" or "effect maintenance" or "total weekly usable count" or
                "total accumulated usable count" or "use count limit all server item") ||
            raw.DeclaredNeededMaterials is not { } materials || materials.Count % 2 != 0)
            return null;
        var purchase = ImmutableArray.CreateBuilder<ItemMaterialCost>();
        for (int index = 0; index < materials.Count; index += 2)
        {
            if (materials[index] <= 0 || materials[index + 1] <= 0) return null;
            purchase.Add(new(materials[index], materials[index + 1]));
        }
        // [need material] 是购买价格，仅保留来源；使用时只扣一张券。
        return new(new(item, "[teleport to partner]", [1], file.CoolTime ?? 0,
            file.CoolGroup ?? -item.Id, file.CooltimeMaintenance ?? 0,
            checked((byte)(file.TotalUsableCount ?? 0)), [.. places]), purchase.ToImmutable());
    }
}
