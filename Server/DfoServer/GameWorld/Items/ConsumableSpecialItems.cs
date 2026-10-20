namespace DfoServer.GameWorld.Items;

// 只列匹配资源及客户端仍存在的旧端特殊分支；有声明的时长、冷却和使用资格继续取PVF。
internal static class ConsumableSpecialItems
{
    public const int StaminaRecoveryItem = 10087825;
    public const int RevivalMilliseconds = 1_200_000;
    public const int TownGiftSeconds = 3_600;
    public static bool IsRevivalBlessing(int id) => id is 2600020 or 2680783 or 2749063 or 2749165;
    public static int TownGiftItem(int id) => id is >= 7789 and <= 7795 ? id + 7 : id == 8004 ? 8005 : 0;
}
