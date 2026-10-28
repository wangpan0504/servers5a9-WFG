namespace DfoServer.Network.Builders;

// 值为所属业务 ACK 的错误码，不是 dstr 索引；由 ClientFailurePackets 绑定 opcode 和完整失败正文。
// 原文以 JSON 字符串表示；同一码的不同文本仍由客户端模式及当前状态选择，不能指定其中某一句。

// CMD324，匹配消费者 0234D0B0 / 02E02340。
internal enum ClientMailQueryError : ushort
{
    TryLater = 2, // dstr 5560: "请稍后再使用"
    SelfSend = 7, // dstr 2071: "无法向自身发送邮件"
    RecipientMissing = 21, // dstr 5558: "该ID不存在"
    TargetCannotReceive = 74, // dstr 100007047: "对方处于无法接收邮件的状态。"
    TargetBlacklisted = 77 // dstr 100007046: "对方已在您的黑名单中， 无法进行该操作。"
}

// CMD94，匹配消费者 023524C0 / 02E17D50。
internal enum ClientMailSendError : ushort
{
    RecipientMissing = 3, // dstr 2059: "无法找到该角色\\n请重新确认收件人名字"
    SelfSend = 7, // dstr 2071: "无法向自身发送邮件"
    Untranslated = 8, // dstr 49520: "dnf_49520"
    GoldInsufficient = 10, // dstr 2060: "所持金币不足"
    RecipientLevelGoldLimit = 14, // dstr 2068: "相比收件人的Lv， 发送金额过多"
    InvalidAttachment = 17, // dstr 2061: "发送的物品不符合要求， 请确认"
    RecipientMissingState = 19, // dstr 2059: "无法找到该角色\\n请重新确认收件人名字"
    EmptyContent = 22, // dstr 21941: "无内容"
    NotTradable = 23, // dstr 2062: "该物品无法交易"
    RecipientMissingAlternative = 24, // dstr 2059: "无法找到该角色\\n请重新确认收件人名字"
    GoldUsageLimit = 70, // dstr 2450: "已超出金币的使用限制"
    SenderBlacklisted = 77, // dstr 4763: "您在对方的黑名单内， 无法进行该操作。"
    GmTarget = 90, // dstr 6424: "无法向GM做出该动作"
    DailyGoldSpendingLimit = 95, // dstr 39133: "已达到每日金币支出上限， 无法支付金币。"
    TradeCountExhausted = 114, // dstr 35251: "该物品的可交易次数为0， 无法进行交易。"
    AvatarPreset = 127, // dstr 46921: "添加到装扮预设中的装扮无法出售或移动。"
    UnusableText = 159, // dstr 281: "『包含无法使用的文字』"
    Confined = 206, // dstr 70352: "您被禁闭在赛丽亚旅馆， 无法进行该操作。"
    Locked = 214, // dstr 42232: "在物品已锁定状态下， 您无法进行当前操作。"
    AccountBound = 217, // dstr 44246: "帐号绑定装备只能邮寄给同一帐号内的其他角色"
    DailyExpiry = 219, // dstr 44500: "每日期限物品无法使用邮件发送。"
    LevelOrSendCountLimit = 227, // dstr 70136: "无法发送\\n当前角色还未达到Lv5或已超出邮包发送次数限制"
    Expired = 235 // dstr 44480: "该物品已超过使用期限， 无法邮寄给他人。"
}

// CMD95，匹配消费者 02366310 / 02E16700 / 02E194A0。错误位于逐附件结果行的 u32 内，不能编码成整个请求失败。
internal enum ClientMailClaimError : ushort
{
    InventoryFull = 4, // dstr 2057: "物品栏空间不足"
    GoldCarryLimit = 10, // dstr 2058: "已超过所持金币上限\\n清理所持金币或提升Lv， 可解决此问题"
    TradingRestricted = 19, // dstr 7520: "正处于限制交易状态， 系统将取消相关请求。"
    MailMissing = 21, // dstr 2056: "无邮件"
    OwnStoreOpen = 60, // dstr 1358: "个人商店开设中， 无法使用此服务"
    InventoryWeightLimit = 119, // dstr 68018: "已达到物品栏重量上限。"
    Trading = 219, // dstr 36132: "无法在交易时使用该功能"
    ItemCarryLimit = 231 // dstr 68020: "存在超过可携带上限的道具。"
}

// CMD134，匹配消费者 02369D20 / 02E16C20。
internal enum ClientMailSaveError : ushort
{
    MailboxFull = 22, // dstr 2072: "邮箱已满！ 请先整理邮箱， 再进行邮件保存"
    LoginDelay = 105, // dstr 70379: "为了保护您的帐号安全，登录%u秒后才可以交易。"
    SecurityMode = 210 // dstr 70149: "您的帐户现在处于安全模式中，不能进行任何的交易、丢弃等敏感操作。如需解除安全模式，请点击下方的确定解除按钮。"
}

// CMD183，匹配消费者 02359FF0。114、127 分支提示后还读取面板 mode:u8。
internal enum ClientAuctionRegisterError : ushort
{
    LoginDelay = 105, // dstr 70379: "为了保护您的帐号安全，登录%u秒后才可以交易。"
    TradeCountExhausted = 114, // dstr 35251: "该物品的可交易次数为0， 无法进行交易。"
    Silent123 = 123, // 客户端直接收尾，无独立dstr；业务含义未闭合，不猜成具体余额或资格错误。
    Silent126 = 126, // 客户端直接收尾，无独立dstr。
    AvatarPreset = 127, // dstr 46921: "添加到装扮预设中的装扮无法出售或移动。"
    Silent137 = 137, // 客户端直接收尾，无独立dstr。
    Silent206 = 206, // 客户端直接收尾，无独立dstr。
    SecurityMode = 210, // dstr 70149: "您的帐户现在处于安全模式中，不能进行任何的交易、丢弃等敏感操作。如需解除安全模式，请点击下方的确定解除按钮。"
        // 底层问题：旧领域以D2作通用兜底，但客户端实际显示安全模式，不能将该文案解释为所有失败原因。
    LevelRequirement = 232 // dstr 75330: "Lv60以下的角色无法使用该功能。\\n(*若同一大区下有Lv95的角色， 则可以正常使用该功能。)"
}

// CMD184，匹配消费者 0235A230。
internal enum ClientAuctionCancelError : ushort
{
    LoginDelay = 105, // dstr 70379: "为了保护您的帐号安全，登录%u秒后才可以交易。"
    SecurityMode = 210, // dstr 70149: "您的帐户现在处于安全模式中，不能进行任何的交易、丢弃等敏感操作。如需解除安全模式，请点击下方的确定解除按钮。"
    LevelRequirement = 232 // dstr 75330: "Lv60以下的角色无法使用该功能。\\n(*若同一大区下有Lv95的角色， 则可以正常使用该功能。)"
}

// CMD185，匹配消费者 0235A3F0。
internal enum ClientAuctionBuyError : ushort
{
    LoginDelay = 105, // dstr 70379: "为了保护您的帐号安全，登录%u秒后才可以交易。"
    SecurityMode = 210, // dstr 70149: "您的帐户现在处于安全模式中，不能进行任何的交易、丢弃等敏感操作。如需解除安全模式，请点击下方的确定解除按钮。"
    LevelRequirement = 232 // dstr 75330: "Lv60以下的角色无法使用该功能。\\n(*若同一大区下有Lv95的角色， 则可以正常使用该功能。)"
}
