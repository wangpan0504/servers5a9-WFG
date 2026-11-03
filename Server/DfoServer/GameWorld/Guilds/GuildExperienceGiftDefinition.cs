using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.Guilds;

internal sealed record GuildExperienceGiftDefinition(ItemTemplate Item, int Amount)
{
    public const uint Action = 145;
    public static GuildExperienceGiftDefinition? Parse(StackableFile raw, ItemTemplate item)
    {
        var action = StackableActionCatalog.Arguments(raw);
        if (raw.TemplateId != item.Id || item.Kind != ItemKind.Consumable || item.UseForbidden ||
            action is not { Label: "[increase guild exp fund]", Parameters: [1, > 0] } ||
            raw.DeclaredUseActionPacket != 1 || raw.TypeDeclaration is null ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 }) return null;
        // 仅接通本版实际声明的类型1经验；不能把旧服145公会积分或其它类型混入经验账本。
        return new(item, action.Parameters[1]);
    }
}
