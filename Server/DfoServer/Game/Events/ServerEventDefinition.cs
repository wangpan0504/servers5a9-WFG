using System.Collections.Immutable;

namespace DfoServer.Game.Events;

// 只登记已经有真实业务消费和持久控制的活动；资源目录不能自动扩大此白名单。
internal sealed record ServerEventDefinition(ushort Id, string Name, string Purpose, string Conditions,
    bool SupportsSchedule, string DefaultState)
{
    public static ImmutableArray<ServerEventDefinition> Registered { get; } =
    [
        new(64, "黄金卡牌双倍物品奖励", "通关后的付费黄金卡牌获得双份物品奖励。",
            "只影响之后生成的付费黄金卡牌奖励；不改变普通翻牌、金币或地下城爆率。", true, "未设置时间时关闭"),
        new(266, "积分商城", "显示积分商城，并允许按真实商品价格兑换。",
            "需要当前PVF包含可用积分商品；关闭后不再开放新兑换，已领取物品不会收回。", true, "未设置时间时常驻开启"),
        new(312, "2019强者之路", "开放首期强者之路入口，沿正式流程挑战并领取对应奖励。",
            "需要当前PVF具备活动地下城及配套资源；关闭后不开放新入场，已提交奖励和进度保留。", true, "未设置时间时关闭"),
        new(2520, "阿拉德探险记", "开放本服已冻结赛季的任务、经验、资格证及等级奖励。",
            "需要启动前装配稳定赛季及完整资源；关闭保留进度，过期不会自动换季。", true, "普通启动未配置时关闭，新启动器首次连接初始化开启"),
        new(2037, "赛丽亚幸运奖励", "控制赛丽亚开盒的幸运累计和阶段双倍奖励。",
            "关闭后仍可普通开盒，历史幸运值保留；再次开启后继续累计。", true, "未设置时间时常驻开启"),
        new(2378, "公会红包新购买与发放", "控制公会红包的新购买和新发放。",
            "关闭后不再接受新购买或发放；已发红包仍可领取、退款和查询。", true, "未设置时间时常驻开启"),
        new(2395, "红尾乔纳森显示", "显示或隐藏红尾乔纳森NPC。",
            "仅改变NPC显示，不改变时空石商店的交易资格。", false, "默认显示"),
        new(2532, "诺兰显示", "显示或隐藏诺兰NPC。",
            "仅控制NPC显示，不会补齐未实现的节日玩法；不支持设置起止时间。", false, "默认显示"),
        new(228, "累积在线得好礼", "按当前客户端资源累计在线并通过系统邮件发放每日和累计奖励。",
            "活动关闭时暂停计时但保留账号进度；奖励按账号幂等领取。", false, "默认关闭")
    ];

    public static ServerEventDefinition? Find(int id) => Registered.FirstOrDefault(entry => entry.Id == id);
}
