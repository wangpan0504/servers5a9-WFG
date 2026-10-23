using System.Collections.Immutable;

namespace DfoServer.Game.Adventure;

// 服务端下发模式：Off=保持旧行为；Slots=回读721/720并按790请求补发719；Full=就绪时另发整份719列表。
internal enum AdventureWeeklyMode { Off, Slots, Full }

// 每周地下城信息：客户端固定提交5个槽位，0xFF表示该列未选择。
// 服务端按客户端提交的原字节保存排列；719的次数来自角色本周实际入场计数。
internal static class AdventureWeekly
{
    public const int Slots = 5;
    public const byte Empty = 0xFF;

    // 从未保存过账号排列时使用 PVF `etc/adventurersystem/adventurersystem2018.etc`
    // 的 [first weekly dungeon info config] = 0 1 3 4 6：伊希斯/超时空/泰波尔斯/希望/眩惑之塔。
    public static readonly ImmutableArray<byte> Default = [0, 1, 3, 4, 6];
    // 显式清空与读库失败时下发全未选择，不把上一轮排列当成默认值。
    public static readonly ImmutableArray<byte> EmptySlots = [Empty, Empty, Empty, Empty, Empty];

    public static ImmutableArray<byte> Validate(ImmutableArray<byte> slots) =>
        slots.Length == Slots ? slots : throw new InvalidDataException("每周地下城配置必须为5个槽位");
}

// 718名册与719次数共用的一行：等级/职业/成长供客户端解析职业名，Used按9个面板列保存本周已用次数，
// -1表示该列没有服务端统计来源（客户端显示"-"）。
internal sealed record AdventureWeeklyCharacter(uint Id, string Name, ushort Level, byte Job, byte Grow, ImmutableArray<int> Used)
{
    public int UsedAt(int slot) => slot >= 0 && slot < Used.Length ? Used[slot] : -1;
}
