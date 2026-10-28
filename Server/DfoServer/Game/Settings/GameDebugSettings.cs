namespace DfoServer.Game.Settings;

// 当前服务实例的调试设置，不写入玩家存档，重启恢复各自默认值。
internal sealed class GameDebugSettings
{
    // 两个开关均为true时忽略对应声明，不能互相代替或翻转成启用期限。
    public volatile bool IgnoreCashShopAbsoluteExpiration = true;
    public volatile bool IgnoreCashShopUsablePeriod = false;
    public volatile bool InfiniteFatigue = true;
    // 网吧身份独立于账号黑钻；字段默认关闭，线上实例由 ServerRuntime 启动时按数据目录 pc-room.json 置为开（缺文件即开），
    // GM 的 on/off 只改当前进程，重启不沿用上次的临时调试资格。
    public volatile bool PremiumPcRoom;
    // 进图伴侣开关。默认关闭，只改当前进程，重启恢复关闭；塔与祭坛等类型仍由资源标记排除。
    // 真值来自地图 [ai character] 里的20900行（王富贵）与5409行（28号光兵），两个开关各自独立、可同时开。
    public volatile bool SummonWangFugui;
    public volatile bool SummonGuangbing;
}
