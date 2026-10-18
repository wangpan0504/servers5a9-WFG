namespace DfoServer.Game.Settings;

// 当前服务实例的调试设置，不写入玩家存档，重启恢复各自默认值。
internal sealed class GameDebugSettings
{
    // 两个开关均为true时忽略对应声明，不能互相代替或翻转成启用期限。
    public volatile bool IgnoreCashShopAbsoluteExpiration = true;
    public volatile bool IgnoreCashShopUsablePeriod = false;
    public volatile bool InfiniteFatigue = true;
    // 网吧身份独立于账号黑钻；默认关闭，重启不沿用上次的调试资格。
    public volatile bool PremiumPcRoom;
}
