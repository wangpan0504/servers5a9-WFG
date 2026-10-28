namespace DfoServer.Network.Builders;

// 值为所属业务 ACK 的错误码，不是 dstr 索引；由 ClientFailurePackets 绑定 opcode 和完整失败正文。
// 原文以 JSON 字符串表示；同一码的不同文本仍由客户端模式及当前状态选择，不能指定其中某一句。

// CMD29，匹配消费者 02360630。
internal enum ClientSkillError : ushort
{
    Silent = 2, // 客户端默认静默分支，没有独立提示文本。
    PvpUnavailable = 7, // dstr 46153: "在决斗场中无法学习技能"
    Mastered = 18, // dstr 71362: "已精通该技能"
    ExpansionError = 19, // dstr 46091: "出现错误。 (技能类型扩展券)"
    SkillMissing = 21, // dstr 71363: "不存在的技能"
    SpInsufficient = 22, // dstr 770: "SP点数不足"
    GoldInsufficient = 91, // dstr 6426: "金币不足"
    GuildLevelInsufficient = 92, // dstr 6427: "公会Lv不足"
    Confined = 206, // dstr 70352: "您被禁闭在赛丽亚旅馆， 无法进行该操作。"
    TargetLevelUnavailable = 217, // dstr 46141: "无法学习指定Lv的该技能"
    WarriorDuelUnavailable = 235 // dstr 300000184: "勇士对决中无法进行技能设置。"
}

// CMD260，匹配消费者 0234E170。
internal enum ClientSkillTreeError : ushort
{
    ExpansionRequired = 1, // dstr 46092: "购买技能类型扩展券后， 才能进行技能类型变更。"
    RegionUnavailable = 19, // dstr 46090: "该地区无法进行技能类型变更。"
    WarriorDuelUnavailable = 235 // dstr 300000184: "勇士对决中无法进行技能设置。"
}

// CMD502，匹配消费者 012A1870。
internal enum ClientClassChangeError : ushort
{
    InvalidInformation = 1, // dstr 27164: "信息有误， 无法完成当前操作。"
    SameAdvancement = 3, // dstr 27160: "选择同样的职业"
    ItemMissing = 17, // dstr 27161: "搜索不到该物品"
    AdvancementRequired = 19, // dstr 27159: "未转职状态"
    AdvancementQuestInProgress = 22, // dstr 27188: "进行转职任务时， 无法使用转职变更书。\\n可以在完成转职任务或放弃转职任务后使用。"
    CannotUse = 23, // dstr 27162: "无法使用该物品"
    CurrentAdvancementUnavailable = 27, // dstr 42468: "当前转职无法更变。"
    TownOnly = 30, // dstr 1201: "只能在城镇内使用"
    AppearanceConfirmation = 133, // dstr 42516: "试图将转职变更为正在游戏中的角色职业。"
        // dstr 100007181: "若继续转职， 则部分添加到皮肤仓库的武器外观将无法再使用。 确定要继续吗？"
        // 底层问题：本服尚无皮肤仓库资产owner及迁移事务；显示确认不代表确认后已完成资产迁移。
    Trading = 217, // dstr 27163: "交易中无法使用"
    AdvancementNotOpen = 235 // dstr 27180: "新的转职他日开放"
}

// CMD31，匹配消费者 0236DDE0。
internal enum ClientQuestAcceptError : ushort
{
    InventoryFull = 17, // dstr 788: "物品栏空间不足， 无法接受任务"
    Silent = 18, // 客户端默认静默分支，没有独立提示文本。
    CannotAccept = 23, // dstr 787: "无法接受的任务"
    PublicChannel = 96, // dstr 44472: "在公共频道无法进行该任务"
    AntiAddiction = 126 // dstr 70005: "正在适用防沉迷系统的限制"
}

// CMD32，匹配消费者 02352C40。
internal enum ClientQuestGiveUpError : ushort
{
    CannotGiveUp = 18, // dstr 13026: "无法放弃已满足完成条件或正在进行中的任务"
    CannotGiveUpAlternative = 20, // dstr 13026: "无法放弃已满足完成条件或正在进行中的任务"
    AntiAddiction = 126 // dstr 70005: "正在适用防沉迷系统的限制"
}

// CMD34，匹配消费者 02376BF0。
internal enum ClientQuestFinishError : ushort
{
    PvpUnavailable = 1, // dstr 46306: "在决斗场中无法使用该功能"
    RewardInventoryFull = 4, // dstr 791: "物品栏空间不足， 无法获得奖励物品"
    CannotCompleteInPvp = 8, // dstr 44467: "无法再决斗场完成该任务"
    SilentState = 19, // 客户端默认静默分支，没有独立提示文本。
    SilentCondition = 22, // 客户端默认静默分支，没有独立提示文本。
    LoginDelay = 105, // dstr 70379: "为了保护您的帐号安全，登录%u秒后才可以交易。"
    AntiAddiction = 126, // dstr 70005: "正在适用防沉迷系统的限制"
    FactionNotVictorious = 209, // dstr 21700: "不是势力战中的胜利方。"
    SecurityMode = 210 // dstr 70149: "您的帐户现在处于安全模式中，不能进行任何的交易、丢弃等敏感操作。如需解除安全模式，请点击下方的确定解除按钮。"
}
