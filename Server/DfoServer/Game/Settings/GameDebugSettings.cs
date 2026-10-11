namespace DfoServer.Game.Settings;

// 当前服务实例的调试设置；默认开启，不写入玩家存档，重启恢复默认。
internal sealed class GameDebugSettings
{
    // 沿用既有GM开关字段；开启统一覆盖商城及已有礼包发奖的绝对/相对期限。
    public volatile bool IgnoreCashShopAbsoluteExpiration = true;
    public volatile bool InfiniteFatigue = true;
}
