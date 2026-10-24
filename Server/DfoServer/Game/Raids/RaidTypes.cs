namespace DfoServer.Game.Raids;

// 客户端 01DC69B0：当前频道类型减 0x17 查 01DC6A18 字节表、再经 01DC69FC 跳表返回团本类型；
// CREATE_RAID 的 u8 类型（0216E16D）与 RaidCreateWindow 标题图（0216E94B：2→raid_areaimg_Fiend、3→raid_areaimg_Prey）都用它。
internal static class RaidTypes
{
    public const byte Default = 6;

    public static byte FromChannelType(int channelType) => channelType switch
    {
        0x17 or 0x19 => 0,
        0x20 => 1,
        0x2D => 2,
        0x32 => 3,
        0x3E => 4,
        0x3F => 5,
        _ => Default
    };
}
