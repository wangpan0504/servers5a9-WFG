namespace DfoServer.Network.Builders;

// 这些分支不因本服尚未接入对应玩法而消失；失败包只触发原生提示，不表示业务已经实现。

// CMD121，消费者 02333640。
// 底层问题：当前委任队长通用拒绝只记日志；23仅指匹配、95仅指街头争霸申请，不能拿它们代替未授权或目标槽错误。
internal enum ClientPartyHostError : ushort
{
    Matching = 23, // dstr 36288: "匹配中，无法使用"
    StreetPvpApplication = 95 // dstr 22262: "街头争霸申请过程中， 无法委任队长。"
}

// CMD68，消费者 0234B7D0。
// 底层问题：历史建会权限窗口不是当前认证邀请事务；33/113虽在失败分支显示“获得创建公会的权限”，也不能据此当作授权或建会成功。
internal enum ClientGuildCreateRightError : ushort
{
    GoldInsufficient = 31, // dstr 822: "创建公会所需的金币不足"
    AlreadyJoinedOrWaiting = 32, // dstr 21720: "您已加入公会或正处于等待加入公会的状态"
    LegacyPermissionText = 33, // dstr 824: "获得创建公会的权限"
    MemberOffline = 45, // dstr 19477: "创建成员不在线， 创建失败。\\n\\n请向在线的角色重新发送申请。"
    FatigueReset = 104, // dstr 42218: "退出公会后， 上午6点疲劳值初始化之前无法创建公会。"
    LeftGuildFatigueReset = 109, // dstr 823: "退出公会后， 在凌晨6点疲劳值重置之前， 无法创建公会。 "
    LegacyPermissionTextAlternative = 113, // dstr 824: "获得创建公会的权限"
    SecondaryPassword = 137 // dstr 13008: "二级密码认证失败， 请重新操作"
}

// CMD73，消费者 023585A0。
internal enum ClientGuildLevelError : ushort
{
    FundInsufficient = 22, // dstr 46339: "公会基金不足"
    GuildMissing = 34, // dstr 827: "    公会不存在"
    GoldInsufficient = 35, // dstr 828: "    金币不足"
    MasterRequired = 36, // dstr 829: "    您不是公会会长"
    MaximumLevel = 37, // dstr 830: "已达到最高Lv"
    MembersInsufficient = 38, // dstr 831: "    公会成员数不足"
    ExperienceInsufficient = 84 // dstr 6401: "    经验值不足"
}

// CMD75，消费者 0233E480。
internal enum ClientGuildJoinError : ushort
{
    CharacterMissing = 3, // dstr 835: "不存在的角色名"
    NotInGuild = 34, // dstr 836: "您还没加入公会"
    AlreadyJoined = 39 // dstr 837: "当前角色已加入了公会"
}

// CMD739，消费者 0234EB40。
internal enum ClientGuildIntroductionError : ushort
{
    IllegalCharacters = 108 // dstr 40149: "公会介绍包含非法字符。"
}

// CMD740，消费者 0234EBA0。
internal enum ClientGuildPromoteChangeError : ushort
{
    PermissionDenied = 7, // dstr 60120: "您没有权限"
    PermissionDeniedAlternative = 34, // dstr 60120: "您没有权限"
    IllegalCharacters = 108 // dstr 40149: "公会介绍包含非法字符。"
}
