namespace DfoServer.GameWorld.Items;

// 只列匹配资源及客户端仍存在的旧端特殊分支；有声明的时长、冷却和使用资格继续取PVF。
internal static class ConsumableSpecialItems
{
    public const int StaminaRecoveryItem = 10087825;
    // 这些活动药剂没有独立效果标签；明确编号只绑定防虚弱语义，期限与资格仍读取各自STK。
    public static bool IsStaminaRecovery(int id) => id is
        10005376 or 10005909 or 10006075 or 10006487 or 10006572 or 10087825 or
        490000113 or 490000348 or 490000702 or 490001415 or 490002087 or 490002492 or
        490003164 or 490003223 or 490003267 or 490003594 or 490003621 or 490003693 or 490003699 or 490003991 or
        490004184 or 490004191 or 490004314 or 490004398 or 490004641 or
        490005155 or 490005202 or 490005299 or 490005338 or 490005630 or 490005699 or 490005756 or 490005942 or
        490006116 or 490006128 or 490006356 or 490006403 or 490006573 or 490006758 or 490006790 or 490006925 or
        490007035 or 490007360 or 490007515 or 490007894 or 490007908 or 490007910 or 490007912 or 490007926 or
        490008119 or 490008213 or 490008358 or 490008441 or 490008541 or 490008976 or 490008995 or
        490009118 or 490009722 or 490009983 or 490010141 or 490010243 or 490010881 or
        490700706 or 490701082 or 490701255 or 490701313 or 490701627 or 490701641 or 490701705 or 490701808 or
        490702160 or 490702200 or 490702311 or 490702532 or 490702714 or 490702879 or 490702976;
    public const int RevivalMilliseconds = 1_200_000;
    public const int TownGiftSeconds = 3_600;
    public static bool IsRevivalBlessing(int id) => id is 2600020 or 2680783 or 2749063 or 2749165;
    public static int TownGiftItem(int id) => id is >= 7789 and <= 7795 ? id + 7 : id == 8004 ? 8005 : 0;
}
