using System.Collections.Immutable;

namespace DfoServer.GameWorld.Items;

[Flags]
internal enum ItemWorlds { None = 0, Common = 1, Fair = 2 }

internal enum StackableUseRejection { None, Place, Condition, World }

internal sealed record StackableUseRules(ImmutableArray<string> Places, ImmutableArray<string> Conditions = default,
    string? StackableType = null, ItemWorlds Worlds = ItemWorlds.Common)
{
    public static StackableUseRules Unrestricted { get; } = new([]);
    public bool IsDirectEnchantment => StackableType is "[enchant waste]" or "[auto enchant]";

    public StackableUseRejection Check(StackableUseContext context, ItemWorlds? targetWorlds = null, bool directUse = true)
    {
        if (!StackableUsePlaces.Allows(Places, context)) return StackableUseRejection.Place;
        if (!Conditions.IsDefaultOrEmpty && Conditions.Contains("[guildmember]") && !context.InGuild)
            return StackableUseRejection.Condition;
        if (!Conditions.IsDefaultOrEmpty && Conditions.Contains("[guildmaster]") && (!context.InGuild || !context.IsGuildMaster))
            return StackableUseRejection.Condition;
        // ARAD 029B9B00：3是专用角色许可，不是“只允许专用角色”；13/16/39保留原生例外。
        if (directUse && context.IsPvpPrivate && StackableType is not null &&
            (Conditions.IsDefaultOrEmpty || !Conditions.Contains("[privatePvpChar]")) &&
            StackableType is not ("[only effect]" or "[booster random]" or "[couple room item]"))
            return StackableUseRejection.Condition;
        // [guildmaster]解析为2，客户端通用使用没有强制；服务端按实际会长身份补齐资格。
        // df_game_r的双向IsEnableWorld要求集合相等，不是有交集，也不以当前频道代替目标物品。
        return targetWorlds is { } target && Worlds != target ? StackableUseRejection.World : StackableUseRejection.None;
    }

    public static ItemWorlds ParseWorlds(IReadOnlyList<string>? worlds)
    {
        if (worlds is null) return ItemWorlds.Common;
        var result = ItemWorlds.None;
        foreach (string world in worlds)
        {
            if (world == "[common]") result |= ItemWorlds.Common;
            else if (world == "[fair]") result |= ItemWorlds.Fair;
            else break; // scanWorldType的2为结束哨兵，后续字符串不能继续授予世界资格。
        }
        return result;
    }
}
