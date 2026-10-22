using PvfLib;

namespace DfoServer.GameWorld.Items;

internal enum LegacyStatusConsumableKind : byte { ReviveCoins = 13 }

internal sealed record LegacyStatusConsumableDefinition(ItemTemplate Item, LegacyStatusConsumableKind Kind, int Amount,
    int CoolTime, int CoolGroup, bool VillageOnly)
{
    public static bool IsNativeItem(int id) => id is 7180 or 7181;

    public static LegacyStatusConsumableDefinition? Parse(ExperienceConsumableFile file, ItemTemplate item)
    {
        var raw = file.Stackable;
        (string Path, LegacyStatusConsumableKind Kind, int Amount, string Type)? effect = item.Id switch
        {
            7180 => ("event/newyear/coin_newyear.stk", LegacyStatusConsumableKind.ReviveCoins, 1, "[waste]"),
            7181 => ("event/newyear/coin_book.stk", LegacyStatusConsumableKind.ReviveCoins, 5, "[waste]"),
            _ => null
        };
        // 3已失效，28未证本版消费发送者；42是钱包资产，均不按旧端ID强行消费。
        if (effect is not { } value || raw.TemplateId != item.Id || item.Kind != ItemKind.Consumable || item.UseForbidden ||
            !raw.ScriptPath.Equals("stackable/" + value.Path, StringComparison.OrdinalIgnoreCase) ||
            raw.TypeDeclaration is not { Parameters: [0 or 1] } type || type.Label != value.Type ||
            file.Diagnostics.Length != 0 || file.Effect is not null || file.ExcludedJobs.Length != 0 ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            raw.HasCoolTime && raw.DeclaredCoolTime is null || raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 ||
            raw.HasActionUsablePlace && raw.DeclaredActionUsablePlaces is not ["[village]"] ||
            raw.DeclaredActionUsableServerTypes is { Count: > 0 } ||
            file.DeclaredFields.Any(field => field is "action type" or "booster info" or "hp recovery" or "mp recovery" or
                "stat change" or "stat change duration" or "levelup range" or "usable first growtype" or "dont change grow type"))
            return null;
        return new(item, value.Kind, value.Amount, raw.DeclaredCoolTime ?? 0, file.CoolGroup ?? -item.Id, raw.HasActionUsablePlace);
    }
}
