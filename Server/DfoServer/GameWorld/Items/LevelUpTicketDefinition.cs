using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record LevelUpTicketDefinition(int TemplateId, int MinimumLevel, int MaximumLevel, ushort Jobs,
    bool VillageOnly, int CoolTime)
{
    public static LevelUpTicketDefinition? Parse(StackableFile raw, ItemTemplate template)
    {
        int minimum = raw.DeclaredMinimumLevel ?? template.MinimumLevel;
        if (raw.TemplateId != template.Id || template.Kind != ItemKind.Consumable || template.UseForbidden ||
            raw.ActionDeclaration is not { Label: "[level up ticket]", Parameters.Count: 0 } ||
            raw.TypeDeclaration is not { Label: "[etc]", Parameters: [1] } || raw.DeclaredLevelUpRange != 1 || raw.HasBoosterInfo ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || minimum < 0 ||
            raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null || raw.DeclaredMaximumLevel < minimum ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 ||
            raw.HasActionUsablePlace && raw.DeclaredActionUsablePlaces is not ["[village]"] ||
            raw.HasCoolTime && raw.DeclaredCoolTime is null) return null;
        return new(template.Id, minimum, raw.DeclaredMaximumLevel ?? int.MaxValue, template.Jobs,
            raw.HasActionUsablePlace, raw.DeclaredCoolTime ?? 0);
    }

    public bool Allows(byte level, byte job) => level >= MinimumLevel && level <= MaximumLevel && job < 16 &&
        (Jobs & 1 << job) != 0;
}
