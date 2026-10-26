using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record AvatarSocketOpenerDefinition(ItemTemplate Item, int Mode)
{
    public ImmutableHashSet<int> AllowedTargets { get; init; } = [];

    public static AvatarSocketOpenerDefinition? Parse(int id, string path, PvfToken[] tokens,
        Func<int, string> resolve, ItemTemplate item)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var limits = document.Sections.Where(section => section.Name == "add socket limited avatar").ToArray();
        ImmutableHashSet<int>? targets = null;
        if (limits.Length != 0)
        {
            if (limits is not [{ Children.Length: 0, Values.Length: > 0 } section] ||
                section.Values.Any(value => value.Type != 0 || value.Value <= 0)) return null;
            targets = section.Values.Select(value => value.Value).ToImmutableHashSet();
        }
        var grades = document.Sections.Where(section => section.Name == "grade").ToArray();
        if (grades is not [{ Children.Length: 0, Values: [{ Type: 0 } grade] }] || grade.Value != item.Grade)
            return null;
        return Parse(RecoveryConsumableFile.Parse(id, path, tokens, resolve), item, targets);
    }

    internal static AvatarSocketOpenerDefinition? Parse(RecoveryConsumableFile file, ItemTemplate item,
        ImmutableHashSet<int>? targets = null)
    {
        var raw = file.Stackable;
        if (file.TemplateId != item.Id || raw.TemplateId != item.Id || item.Kind != ItemKind.Consumable || item.UseForbidden ||
            item.Grade < 0 || file.Diagnostics.Length != 0 ||
            raw.TypeDeclaration is not { Label: "[waste]", Parameters: [0] } ||
            StackableActionCatalog.Arguments(raw) is not { Label: "[add avatar socket]", Parameters: [>= 0 and <= 3] } action ||
            raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            file.DeclaredFields.Contains("add socket limited avatar") && targets is not { Count: > 0 } ||
            file.DeclaredFields.Any(field => field is not ("name" or "explain" or "flavor text" or "grade" or "rarity" or
                "usable job" or "attach type" or "minimum level" or "maximum level" or "weight" or "icon" or "icon mark" or
                "stackable type" or "move wav" or "action type" or "stack limit" or "expiration date" or "usable period" or
                "impossible contents" or "item category" or "need material" or "purchase limit" or "use action packet" or
                "add socket limited avatar")))
            return null;
        return new(item, action.Parameters[0]) { AllowedTargets = targets ?? [] };
    }

    public bool Allows(ItemTemplate target) => target.Kind == ItemKind.Avatar && target.Grade == Item.Grade &&
        (Mode == 3 || target.EquipmentSlot != 8) &&
        (Mode != 0 || !target.CloneAvatar) && (Mode != 1 || target.CloneAvatar) &&
        (AllowedTargets.IsEmpty || AllowedTargets.Contains(target.Id));
}
