using System.Collections.Immutable;

namespace DfoServer.Game.Adventure;

// 每周地下城信息：客户端固定提交5个槽位，0xFF表示该列未选择。
// 719/720信息包尚未接入，服务端只按客户端提交的原字节保存排列，不按服务端规则重算。
internal static class AdventureWeekly
{
    public const int Slots = 5;
    public const byte Empty = 0xFF;

    // 客户端首次打开面板的默认排列：伊希斯/超时空/卢克/泰波尔斯/未选择（实机抓包默认值）。
    public static readonly ImmutableArray<byte> Default = [0, 1, 2, 3, Empty];

    public static ImmutableArray<byte> Validate(ImmutableArray<byte> slots) =>
        slots.Length == Slots ? slots : throw new InvalidDataException("每周地下城配置必须为5个槽位");
}
