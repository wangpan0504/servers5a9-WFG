using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record RecoveryConsumableTuple(string Operation, int Amount, int Auxiliary, string Target);
internal sealed record RecoveryConsumableDefinition(int ItemId, int MinimumLevel, int CoolTime, int CoolGroup,
    RecoveryConsumableTuple? HpRecovery, RecoveryConsumableTuple? MpRecovery);

internal sealed class RecoveryConsumableCatalog
{
    static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "name", "explain", "flavor text", "grade", "rarity", "usable job", "attach type", "minimum level", "weight",
        "price", "cool time", "icon", "field image", "stackable type", "item group name", "move wav", "stack limit",
        "cooltime group", "hp recovery", "mp recovery"
    };
    readonly Dictionary<int, RecoveryConsumableDefinition> definitions = [];

    public RecoveryConsumableCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable)
    {
        foreach (int id in new[] { 1108, 1114 })
            if (stackable.TryGetValue(id, out string? path))
            {
                var raw = RecoveryConsumableFile.Parse(id, "stackable/" + path, archive.ReadTokens("stackable/" + path), archive.ResolveString);
                if (Read(raw) is { } definition) definitions.Add(id, definition);
            }
    }

    internal RecoveryConsumableCatalog(IEnumerable<RecoveryConsumableDefinition> source)
    {
        foreach (var definition in source) definitions.Add(definition.ItemId, definition);
    }

    internal static RecoveryConsumableDefinition? Read(RecoveryConsumableFile file)
    {
        var raw = file.Stackable;
        if (file.TemplateId is not (1108 or 1114) || file.Diagnostics.Length != 0 || file.DeclaredFields.Any(field => !KnownFields.Contains(field)) ||
            raw.TypeDeclaration is not { Label: "[waste]", Parameters: [4] } || raw.DeclaredUsableJobs is not ["[all]"] ||
            raw.DeclaredMinimumLevel != 16 || raw.DeclaredStackLimit != 1000 || raw.DeclaredItemGroupName != "potion" ||
            file.CoolTime != 10000 || file.CoolGroup != (file.TemplateId == 1108 ? 0 : 1) ||
            file.TemplateId == 1108 && (file.HpRecovery != new RecoveryDeclaration("+", 410, 1000, "myself") || file.MpRecovery is not null) ||
            file.TemplateId == 1114 && (file.MpRecovery != new RecoveryDeclaration("+", 330, 1000, "myself") || file.HpRecovery is not null))
            return null;
        static RecoveryConsumableTuple? Convert(RecoveryDeclaration? value) => value is null ? null :
            new(value.Operation, value.Amount, value.Auxiliary, value.Target);
        return new(file.TemplateId, 16, file.CoolTime.Value, file.CoolGroup.Value, Convert(file.HpRecovery), Convert(file.MpRecovery));
    }

    public bool TryGet(int id, out RecoveryConsumableDefinition definition) => definitions.TryGetValue(id, out definition!);
}
