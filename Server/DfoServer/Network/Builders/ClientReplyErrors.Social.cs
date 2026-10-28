namespace DfoServer.Network.Builders;

// 值为所属业务 ACK 的错误码，不是 dstr 索引；由 ClientFailurePackets 绑定 opcode 和完整失败正文。
// 原文以 JSON 字符串表示；同一码的不同文本仍由客户端模式及当前状态选择，不能指定其中某一句。

// CMD10，匹配消费者 02363C00。失败还读取本次交互 mode:u8。
internal enum ClientPeerRequestError : ushort
{
    TargetOffline = 3, // dstr 698: "对方已退出游戏"
    PartyOrRoomFull = 4, // dstr 699: "队伍人员已满"
        // dstr 700: "房间人已满"
    TargetBusy = 18, // dstr 701: "对方已有队伍"
        // dstr 702: "对方已进入物品交易状态"
        // dstr 703: "对方已在决斗场内"
    TargetUnavailable = 19 // dstr 704: "对方无法加入队伍"
        // dstr 705: "对方无法交易物品"
        // dstr 706: "对方目前无法决斗"
        // dstr 48386: "当前状态下， 无法闯入对方所在地下城。"
}

// CMD11，匹配消费者 02354540。失败还读取本次交互 mode:u8。
internal enum ClientPeerResponseError : ushort
{
    TargetOffline = 3, // dstr 707: "对方已退出游戏"
    PartyOrRoomFull = 4, // dstr 708: "队伍人员已满"
        // dstr 709: "房间人已满"
    TargetBusy = 18, // dstr 710: "对方已有队伍"
        // dstr 711: "对方已进入物品交易状态"
        // dstr 712: "对方已在决斗场内"
    TargetUnavailable = 19, // dstr 713: "对方无法加入队伍"
        // dstr 714: "对方无法交易物品"
        // dstr 715: "对方目前无法决斗"
    RefusedOrOffline = 133 // dstr 70055: "对方不在线， 或拒绝了您的组队请求"
        // dstr 70064: "对方不在线，或拒绝了您的交易请求"
        // dstr 19014: "对方拒绝了您的附魔请求"
}

// CMD12，匹配消费者 02364B70。
internal enum ClientPartySettingsError : ushort
{
    NotOwnerDiagnostic = 8, // dstr 717: "FAIL : 您不是房主,不能要求变更房间环境！ "
        // 此分支仅写客户端诊断，不保证可见提示。
    Silent = 21, // 客户端默认静默分支，没有独立提示文本。
    FatigueInsufficient = 22, // dstr 19603: "疲劳值不足， 无法进入该地下城。"
    Matching = 25, // dstr 19825: "匹配中无法创建队伍。"
    FilteredName = 159, // dstr 31722: "包含违禁语， 无法创建队伍。"
    AntiMagicInsufficient = 247 // dstr 300000985: "抗魔值不足， 无法创建队伍。"
}

// CMD13，匹配消费者 0234AAC0。
internal enum ClientPartyLeaveError : ushort
{
    Matching = 8, // dstr 36288: "匹配中，无法使用"
    NotInPartyDiagnostic = 18, // dstr 718: "FAIL : 您处于未组队状态， 无法请求退出队伍！ "
        // 此分支仅写客户端诊断，不保证可见提示。
    InvalidStateDiagnostic = 19 // dstr 719: "FAIL : 您处于无法退出组队状态， 请求退出失败！ "
        // 此分支仅写客户端诊断，不保证可见提示。
}

// CMD14，匹配消费者 0234AB70。
internal enum ClientPartyKickError : ushort
{
    EmptySlotDiagnostic = 4, // dstr 720: "FAIL : 对空队员位置， 无法请求强行退出！ "
        // 此分支仅写客户端诊断，不保证可见提示。
    PermissionDiagnostic = 8, // dstr 722: "FAIL : 无强行退出权限， 请求退出失败！ "
        // 此分支仅写客户端诊断，不保证可见提示。
    InvalidStateDiagnostic = 19 // dstr 721: "FAIL : 无法强行退出的状态下， 请求退出失败！ "
        // 此分支仅写客户端诊断，不保证可见提示。
}

// CMD156，匹配消费者 0234B6F0。
internal enum ClientGuildNameError : ushort
{
    LengthLimit = 106, // dstr 6718: "公会名称长度上限 : 中文7字符或英文14字符。"
    Duplicate = 107, // dstr 6709: "此公会名称已被其他公会使用。"
    UnusableText = 108 // dstr 6710: "包含无法使用的文字"
}

// CMD741，匹配消费者 0234EBF0。
internal enum ClientGuildInviteError : ushort
{
    TargetMissing = 3, // dstr 19465: "玩家不在线或者不存在。"
    SameAccount = 5, // dstr 40152: "该角色与创建者属于相同账号。\\n\\n请向其它账号的角色重新发送申请。"
    CannotJoin = 23, // dstr 44512: "无法加入该公会。"
    CannotCreate = 34, // dstr 19487: "无法创建公会。"
    TargetJoinedOrWaiting = 39, // dstr 21721: "该用户已加入公会或正处于等待加入公会的状态"
    TargetFatigueReset = 40, // dstr 40151: "对方角色需到上午6点疲劳值初始化后才能接受。"
    TargetCertifiedOtherGuild = 45, // dstr 19468: "该玩家已经认可了其它公会的创建， 无法发送认可申请。"
    TargetCannotJoin = 74, // dstr 100007055: "对方处于无法加入公会的状态。"
    TargetBlacklisted = 77, // dstr 100007046: "对方已在您的黑名单中， 无法进行该操作。"
    FatigueReset = 104 // dstr 40150: "需要上午6点疲劳值初始化后才能进行。"
}

// CMD742，匹配消费者 0234ED30。
internal enum ClientGuildCertificationError : ushort
{
    PlayerMissing = 3, // dstr 36652: "无法找到指定玩家"
    SilentRejected = 34, // 客户端默认静默分支，没有独立提示文本。
    SelfJoinedOrWaiting = 39, // dstr 21720: "您已加入公会或正处于等待加入公会的状态"
    CertifiedOtherGuild = 45, // dstr 19469: "已经认可公会创建， 无法认可其它公会的创建。"
    InvitationExpired = 103, // dstr 6580: "公会邀请的等待时间已达到上限"
    TargetFatigueReset = 104 // dstr 40151: "对方角色需到上午6点疲劳值初始化后才能接受。"
}

// CMD664，匹配消费者 02343D80。217、247使用客户端当前攻坚模型作为格式参数，不追加网络字段。
internal enum ClientRaidCreateError : ushort
{
    WeeklyEntryLimit = 1, // dstr 67067: "已耗尽每周入场次数。"
    TargetMissing = 3, // dstr 32059: "抱歉， 不存在该玩家。"
    TimeUnavailable = 7, // dstr 32061: "当前时间段内无法进行。"
    StateUnavailable = 19, // dstr 32060: "抱歉， 当前状态下无法入场。"
    GuildRequired = 34, // dstr 75475: "未加入公会， 无法加入攻坚队。"
    AlreadyJoined = 41, // dstr 32056: "您已经加入了攻坚队。"
    RaidMissing = 46, // dstr 32057: "抱歉， 不存在该攻坚队。"
    MaterialsRequired = 48, // dstr 32058: "抱歉， 缺乏入场材料。"
    StateUnavailableAlternative = 117, // dstr 32129: "当前状态下无法入场。"
    UnusableText = 159, // dstr 281: "『包含无法使用的文字』"
    TargetSanctioned = 166, // dstr 72874: "对方正受到收益制裁处罚， 无法加入队伍或者攻坚队。"
    ClearLimitByRaidModel = 217, // dstr 66020: "已经超过每日通关次数上限。"
        // dstr 66065: "该角色已超过%s的每周可挑战次数。"
        // 底层依赖：客户端当前攻坚模型提供格式参数，服务端不能把次数或名称另附在ACK里。
    DailyLimit = 220, // dstr 69003: "已超出该团队模式每日可挑战的次数。"
    WeeklyLimit = 221, // dstr 69004: "已超出该团队模式每周可挑战的次数。"
    BoundToOtherCharacter = 246, // dstr 25178: "其他角色已绑定， 无法使用。"
    AntiMagicByRaidModel = 247 // dstr 75629: "抗魔值不足， 无法创建攻坚队。 抗魔值需要高于(%d)。"
        // 底层依赖：阈值由客户端攻坚对象计算，不是本回包的附加字段。
}

// CMD666，匹配消费者 023441F0。17、217必须显式提供count:u32和逐项u32，不能退化成三字节失败。
internal enum ClientRaidStartError : ushort
{
    LeaderRequired = 8, // dstr 49075: "只有攻坚队长才能开始。"
    MembersMissingMaterials = 17, // dstr 49085: "攻坚队中存在未准备入场材料的队员。"
        // 底层问题：客户端读完数组即丢弃，成员值的业务含义和来源未闭合，不能擅自解释成角色UID或队伍槽。
    StateUnavailable = 19, // dstr 65066: "当前状态下， 无法开始攻坚战。 "
    TooFewMembers = 22, // dstr 49077: "攻坚队要求的最低人数不足， 无法开始。"
    MembersDailyClearLimit = 217 // dstr 66021: "攻坚队有队员超过每日的通关次数上限。"
        // 底层问题同17；无附加载荷入口会明确报告缺失数组，不自动制造空数组。
}

// CMD667，匹配消费者 023449F0。
internal enum ClientRaidWaitingError : ushort
{
    AlreadyWaiting = 18, // dstr 49081: "已经添加到攻坚队的待机列表。"
    AlreadyJoined = 19, // dstr 49080: "已经加入了攻坚队。"
    DailyLimit = 220, // dstr 69003: "已超出该团队模式每日可挑战的次数。"
    WeeklyLimit = 221 // dstr 69004: "已超出该团队模式每周可挑战的次数。"
}

// CMD670，匹配消费者 0233CF90。
internal enum ClientRaidModifyError : ushort
{
    LeaderRequired = 8, // dstr 65095: "只有攻坚队队长可以使用。"
    RaidMissing = 19, // dstr 32057: "抱歉， 不存在该攻坚队。"
    UnusableText = 159 // dstr 281: "『包含无法使用的文字』"
}
