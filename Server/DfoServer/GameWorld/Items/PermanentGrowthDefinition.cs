using PvfLib;

namespace DfoServer.GameWorld.Items;

// CMD30 的种类值来自匹配客户端 023612D0，不是 stackable type 的第二参数。
internal enum PermanentGrowthKind : byte
{
    Sp = 0, MaxHp = 2, MaxMp = 3, Strength = 4, Vitality = 5, Intelligence = 6,
    Spirit = 7, MoveSpeed = 8, AllElementResistance = 9, Tp = 17
}

internal sealed record PermanentGrowthDefinition(ItemTemplate Item, PermanentGrowthKind Kind, int Amount,
    int CoolTime, int CoolGroup, bool VillageOnly)
{
    public static bool IsNativeItem(int id) => id is 1031 or 1038 or >= 1039 and <= 1046 or 1204 or 1205;

    public static PermanentGrowthDefinition? Parse(ExperienceConsumableFile file, ItemTemplate template)
    {
        var raw = file.Stackable;
        // 这些原生道具没有数值效果标签；同时核对注册 ID 与脚本身份，不从中文说明猜数值。
        (string Path, PermanentGrowthKind Kind, int Amount)? effect = template.Id switch
        {
            1031 => ("book_skill1.stk", PermanentGrowthKind.Sp, 5),
            1038 => ("book_skill2.stk", PermanentGrowthKind.Sp, 20),
            1039 => ("stone_str.stk", PermanentGrowthKind.Strength, 50),
            1040 => ("stone_int.stk", PermanentGrowthKind.Intelligence, 50),
            1041 => ("stone_helth.stk", PermanentGrowthKind.Vitality, 50),
            1042 => ("stone_mind.stk", PermanentGrowthKind.Spirit, 50),
            1043 => ("stone_hp.stk", PermanentGrowthKind.MaxHp, 250),
            1044 => ("stone_mp.stk", PermanentGrowthKind.MaxMp, 250),
            1045 => ("stone_speed.stk", PermanentGrowthKind.MoveSpeed, 10),
            1046 => ("stone_allr.stk", PermanentGrowthKind.AllElementResistance, 10),
            1204 => ("book_fskill1.stk", PermanentGrowthKind.Tp, 1),
            1205 => ("book_fskill2.stk", PermanentGrowthKind.Tp, 5),
            _ => null
        };
        if (effect is not { } value || raw.TemplateId != template.Id ||
            !raw.ScriptPath.Equals("stackable/" + value.Path, StringComparison.OrdinalIgnoreCase) ||
            template.Kind != ItemKind.Consumable || template.UseForbidden || file.Diagnostics.Length != 0 ||
            raw.TypeDeclaration is not { Label: "[etc]", Parameters: [1 or 8] } || file.Effect is not null ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } || file.ExcludedJobs.Length != 0 ||
            raw.HasCoolTime && raw.DeclaredCoolTime is null || raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 ||
            raw.HasActionUsablePlace && raw.DeclaredActionUsablePlaces is not ["[village]"] ||
            raw.DeclaredActionUsableServerTypes is { Count: > 0 } ||
            file.DeclaredFields.Any(field => field is "action type" or "booster info" or "hp recovery" or "mp recovery" or
                "levelup range" or "usable first growtype" or "dont change grow type"))
            return null;
        return new(template, value.Kind, value.Amount, raw.DeclaredCoolTime ?? 0,
            file.CoolGroup ?? -template.Id, raw.HasActionUsablePlace);
    }
}
