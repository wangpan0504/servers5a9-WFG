namespace DfoServer.Network.Builders;

// 值为所属业务 ACK 的错误码，不是 dstr 索引；由 ClientFailurePackets 绑定 opcode 和完整失败正文。
// 原文以 JSON 字符串表示；同一码的不同文本仍由客户端模式及当前状态选择，不能指定其中某一句。

// CMD19，匹配消费者 02372610。失败还读取源、目标容器两个 u8。
internal enum ClientMoveItemError : ushort
{
    RegionOrSlotType = 1, // dstr 44468: "当前地区无法进行移动"
        // dstr 60353: "此类型道具无法放入该栏位"
    StorageFull = 4, // dstr 21912: "金库空间不足， 无法放入物品。"
    FairnessRestriction = 7, // dstr 44299: "天平系统裁定您暂时无法穿戴该装备"
    JobMismatch = 8, // dstr 741: "装备与职业不符"
    LevelInsufficient = 14, // dstr 742: "Lv过低， 无法使用"
    TargetCapacityOrTalisman = 17, // dstr 39348: "对方物品栏空间不足或物品栏内该物品数量持有达到上限， 无法添加物品。"
        // dstr 39137: "因对方持有的同一物品达到了物品栏数量上限或已达到每日金币支出上限， 无法支付金币。"
        // dstr 39343: "对方物品栏空间不足， 无法添加。"
        // dstr 60824: "无法装备与转职不符的护石。"
    OccupiedSlotDiagnostic = 18, // dstr 739: "FAIL : 栏中有其它物品， 无法进行移动！ "
        // 此分支仅写客户端诊断，不保证可见提示。
    Permission = 19, // dstr 35261: "无法移动该物品"
        // dstr 740: "FAIL : 您没有移动的权限！ "
    LockOrInventoryLimit = 22, // dstr 60396: "锁定的物品已超过250个， 无法继续锁定物品。"
        // dstr 746: "物品栏空间不足"
    CannotMove = 23, // dstr 35261: "无法移动该物品"
    TargetGoldLimit = 94, // dstr 6430: "已超出交易对象的金币持有上限"
    WearLimit = 95, // dstr 46732: "已超过装备穿戴数上限。"
    ExpiredItem = 104, // dstr 44485: "无法交易超过使用期限的物品。"
    TradeCountExhausted = 114, // dstr 35251: "该物品的可交易次数为0， 无法进行交易。"
    AvatarPreset = 127, // dstr 46921: "添加到装扮预设中的装扮无法出售或移动。"
    PendingUnlock = 133, // dstr 31484: "正在申请解锁的道具不能移动到账号金库。\\n请解除道具锁定或者撤回道具解锁申请。"
    UseOrRegionRestriction = 235 // dstr 31712: "不能使用该道具。"
        // dstr 39133: "已达到每日金币支出上限， 无法支付金币。"
        // dstr 36408: "该区域无法穿戴和脱下装扮"
}

// CMD44，匹配消费者 02356A00。失败还读取源容器和两个 u32 来源字段。
internal enum ClientUseItemError : ushort
{
    PuppeteerAlreadySummoned = 7, // dstr 44138: "已召唤控偶师的APC"
    JobMismatch = 8, // dstr 795: "该物品与当前职业不符， 无法使用"
    LevelInsufficient = 14, // dstr 796: "Lv过低， 无法使用该物品"
    Silent = 17, // 客户端默认静默分支，没有独立提示文本。
    InvalidState = 19, // dstr 797: "目前无法使用物品"
    NotConsumable = 23, // dstr 798: "非消耗品， 无法使用"
    PcRoomRequired = 78, // dstr 75455: "该物品只能在特权网吧使用。 "
    FatigueConsumptionRequired = 95, // dstr 21398: "消耗一定数值(该道具可恢复的疲劳值数值)以上的疲劳值后， 才能使用该道具"
    Untranslated = 250, // dstr 46486: "dnf_46486"
    PartyChallenge = 253 // dstr 46494: "队伍中有玩家发起或接受了挑战， 将暂时无法使用“和平的呐喊”。"
}

// CMD23，匹配消费者 02355D60。
internal enum ClientRepairError : ushort
{
    GoldInsufficient = 10, // dstr 751: "修理费不足"
    RequiredItemMissing = 21, // dstr 46379: "缺少相应物品"
    TargetItemMissing = 22, // dstr 46380: "指定物品不存在"
    LoginDelay = 105, // dstr 70379: "为了保护您的帐号安全，登录%u秒后才可以交易。"
    DailyLimit = 115, // dstr 70727: "已达到今日使用上限。"
    AntiAddiction = 126, // dstr 70005: "正在适用防沉迷系统的限制"
    SecurityMode = 210 // dstr 70149: "您的帐户现在处于安全模式中，不能进行任何的交易、丢弃等敏感操作。如需解除安全模式，请点击下方的确定解除按钮。"
}

// CMD21，匹配消费者 0236B0A0。
internal enum ClientShopBuyError : ushort
{
    ConditionsUnmet = 1, // dstr 70743: "不符合购买条件， 无法购买。"
    InventoryFull = 4, // dstr 746: "物品栏空间不足"
    Untranslated = 8, // dstr 42704: "dnf_42704"
    GoldInsufficient = 10, // dstr 747: "所持金额不足"
    LevelInsufficient = 14, // dstr 1250: "未满足要求Lv， 无法购买"
    ItemMissing = 17, // dstr 36000: "不存在该道具"
    UserCannotPurchase = 19, // dstr 21977: "该用户无法购买"
    EventState = 21, // dstr 46057: "当前状态无法购买活动商品"
    VictoryPointsInsufficient = 22, // dstr 44509: "胜利点数不足， 无法进行购买。"
    GuildMasterRequired = 36, // dstr 829: "    您不是公会会长"
    OwnStoreOpen = 60, // dstr 1342: "个人商店开设中， 无法收购物品"
    AntiAddiction = 126, // dstr 70005: "正在适用防沉迷系统的限制"
    PurchaseLimit = 133, // dstr 44465: "超过购买限制"
    GoldInsufficientAlternative = 200, // dstr 747: "所持金额不足"
    ItemStorageFull = 203, // dstr 72839: "物品保管箱已满。"
    Confined = 206, // dstr 70352: "您被禁闭在赛丽亚旅馆， 无法进行该操作。"
    OutsideSalePeriod = 217 // dstr 44466: "现在不是出售时间"
}

// CMD22，匹配消费者 023552A0。
internal enum ClientShopSellError : ushort
{
    OwnStoreOpen = 60, // dstr 1343: "个人商店开设中， 无法出售物品"
    AntiAddiction = 126, // dstr 70005: "正在适用防沉迷系统的限制"
    Confined = 206, // dstr 70352: "您被禁闭在赛丽亚旅馆， 无法进行该操作。"
    CannotSell = 235 // dstr 31710: "不能出售该道具。"
}

// CMD25，匹配消费者 0236C6D0。
internal enum ClientCompoundError : ushort
{
    InventoryFull = 4, // dstr 746: "物品栏空间不足"
    LevelInsufficient = 14, // dstr 755: "Lv过低， 无法使用"
    PurificationRequired = 19, // dstr 19223: "需要净化装备中蕴含的异界气息。"
    AntiAddiction = 126, // dstr 70005: "正在适用防沉迷系统的限制"
    EventClosed = 179, // dstr 13107: "活动未开放"
    FreeSlotRequired = 204, // dstr 767: "栏位空间不足。\\n请整理出至少一格的空间后再次尝试。 "
    RepairRequired = 217 // dstr 19222: "只有修理过的物品， 才能作为材料。"
}

// CMD26，匹配消费者 0236CED0。
internal enum ClientDisjointError : ushort
{
    InventoryFull = 4, // dstr 764: "物品栏空间不足， 无法获得分解物品。\\n请整理物品快捷栏、 材料栏或副职材料栏后再试。"
    GoldInsufficient = 10, // dstr 1660: "金币不足"
    InvalidTarget = 17, // dstr 760: "无法分解该物品。"
    InvalidState = 19, // dstr 763: "目前无法分解"
    SkillRequired = 22, // dstr 762: "缺少分解所需的技能"
    CannotDisjoint = 23, // dstr 761: "无法分解的物品"
    AntiAddiction = 126, // dstr 70005: "正在适用防沉迷系统的限制"
    PurificationRequired = 175, // dstr 19137: "无法使用未净化的装备。"
    ShopUnavailable = 179 // dstr 44074: "目前无法使用商店"
}
