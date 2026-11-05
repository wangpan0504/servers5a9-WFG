using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Settings;

namespace DfoServer.Game.Dungeons;

// 进图伴侣由PVF地图的 [ai character] 行驱动，服务端只决定本次入场放行哪几行。
// 客户端换房间不销毁上一间房的AIC，逐房重发会每房多出一名并卡清房，因此只保留起始房间的行。
internal static class DungeonCompanion
{
    // 王富贵：priest/heal_soldier/heal_soldier.aic，20900 行写在每张可解析的副本地图里。
    public const int WangFuguiId = 20900;
    // 专用光兵不再复用人偶2600261的5409；奶妈、奶萝与光兵须配套各自注册的AIC及MAP行。
    public const int GuangbingId = 9986;
    public const int FemaleCrusaderId = 74990;
    public const int EnchantressId = 19973;

    public static int BaseId(SupportCompanionKind kind) => kind switch
    {
        SupportCompanionKind.WangFugui => WangFuguiId,
        SupportCompanionKind.Guangbing => GuangbingId,
        SupportCompanionKind.FemaleCrusader => FemaleCrusaderId,
        SupportCompanionKind.Enchantress => EnchantressId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static string NameForBaseId(int id) => id switch
    {
        WangFuguiId => "wangfugui", GuangbingId => "guangbing", FemaleCrusaderId => "female-crusader", EnchantressId => "enchantress",
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };
    public static ImmutableArray<SupportCompanionProfile> FreezeProfiles(SupportCompanionConfiguration selected,
        SupportCompanionCatalog catalog, Func<int, int> draw) =>
        [.. GameDebugSettings.SupportCompanionKinds(selected.SelectionMask)
            .Select(kind => catalog.Pick(BaseId(kind), selected.TierFor(kind), draw)).OfType<SupportCompanionProfile>()];

    // 入图仅消费一次两组选择快照；共享实例成员沿用创建者冻结的集合。
    public static ImmutableArray<int> EnabledRows(int mask)
    {
        if (!GameDebugSettings.ValidSupportCompanions(mask)) throw new ArgumentOutOfRangeException(nameof(mask));
        return GameDebugSettings.SupportCompanionKinds(mask).SelectMany(kind => EnabledRows(kind)).ToImmutableArray();
    }

    // 旧单选调用仍映射到同一份专用资源 ID。
    public static ImmutableArray<int> EnabledRows(SupportCompanionKind selected) => selected switch
    {
        SupportCompanionKind.Off => [],
        SupportCompanionKind.WangFugui => [WangFuguiId],
        SupportCompanionKind.Guangbing => [GuangbingId],
        SupportCompanionKind.FemaleCrusader => [FemaleCrusaderId],
        SupportCompanionKind.Enchantress => [EnchantressId],
        _ => throw new ArgumentOutOfRangeException(nameof(selected))
    };

    // ID判定只覆盖专用模板；5409原生演员与人偶不能全局归入伴侣。
    public static bool IsCompanionRow(int id) => id is WangFuguiId or GuangbingId or FemaleCrusaderId or EnchantressId;

    // 活动地图由新 DLL 全权接管；服务端按旧支援兵的完整友方普通 APC 形状屏蔽 5409，
    // 不把普通副本中的未知 5409 原生演员误判为支援兵。
    public static bool IsLegacyCompanionRow(DungeonRoomAiCharacter row) =>
        row.Id == 5409 && row.Faction == "[character]" && row.RankKind == "[normal]" &&
        row.RawValues is [5409, _, _, 0, 0, 0];

    // 旧5409仅在来源清单中的MAP原行及完整六字段仍匹配时过滤；不改人偶或原生剧情演员。
    public static bool IsCompanionRow(DungeonRoomDefinition room, int ordinal)
    {
        var row = room.AiCharacters[ordinal];
        return IsCompanionRow(row.Id) || row.Id == 5409 && DungeonCompanionLegacyRows.IsKnown(room.Map.Path, ordinal, row);
    }
}
