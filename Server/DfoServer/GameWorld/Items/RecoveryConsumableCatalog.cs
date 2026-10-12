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
        foreach (int id in new[] { 1065, 1107, 1108, 1114 })
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
        int minimumLevel = file.TemplateId == 1065 ? 1 : file.TemplateId == 1107 ? 6 : 16;
        int? hp = file.TemplateId == 1107 ? 180 : file.TemplateId == 1108 ? 410 : null;
        int? mp = file.TemplateId == 1065 ? 115 : file.TemplateId == 1114 ? 330 : null;
        if (file.TemplateId is not (1065 or 1107 or 1108 or 1114) || file.Diagnostics.Length != 0 || file.DeclaredFields.Any(field => !KnownFields.Contains(field)) ||
            raw.TypeDeclaration is not { Label: "[waste]", Parameters: [4] } || raw.DeclaredUsableJobs is not ["[all]"] ||
            raw.DeclaredMinimumLevel != minimumLevel || raw.DeclaredStackLimit != 1000 || raw.DeclaredItemGroupName != (file.TemplateId == 1065 ? "plant stuff" : "potion") ||
            file.CoolTime != 10000 || file.CoolGroup != (hp is not null ? 0 : 1) ||
            file.HpRecovery != (hp is { } hpAmount ? new RecoveryDeclaration("+",hpAmount,1000,"myself") : null) ||
            file.MpRecovery != (mp is { } mpAmount ? new RecoveryDeclaration("+",mpAmount,1000,"myself") : null))
            return null;
        static RecoveryConsumableTuple? Convert(RecoveryDeclaration? value) => value is null ? null :
            new(value.Operation, value.Amount, value.Auxiliary, value.Target);
        return new(file.TemplateId, minimumLevel, file.CoolTime.Value, file.CoolGroup.Value, Convert(file.HpRecovery), Convert(file.MpRecovery));
    }

    public bool TryGet(int id, out RecoveryConsumableDefinition definition) => definitions.TryGetValue(id, out definition!);
}
