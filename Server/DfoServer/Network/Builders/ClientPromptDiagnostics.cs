namespace DfoServer.Network.Builders;

// 仍保留未绑定、未接入、仅诊断及成功上下文文本；值是dstr索引，绝不是可发送的wire错误码。
// 每项都有消费者和底层缺口。ClientPromptDiagnostics.Explain可供服务端诊断；尝试当失败包发送会明确报错。
internal enum ClientUnboundDstr : uint
{
    PartyOnlyInTown = 306, // dstr 306: "只能在城镇内组队"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterCreationSucceeded = 680, // dstr 680: "新建角色成功"
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    CharacterCreationDatabaseError = 681, // dstr 681: "FALL : DB错误！ "
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    CharacterLimitReached = 682, // dstr 682: "角色数量已达上限， 无法继续创建新角色"
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterNameBlockedOrDuplicate = 683, // dstr 683: "含有屏蔽字或已存在的角色名"
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterCreationStateInvalid = 684, // dstr 684: "FALL : 您处在无法生成角色状态， 不能生成角色！ "
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    CharacterJobDoesNotExist = 685, // dstr 685: "FALL : 角色职业不存在， 无法生成！ "
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    CharacterNameTemporarilyBlocked = 686, // dstr 686: "FALL : 被限制的角色名， 在一定时间内无法使用！ "
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    CharacterDeletedNameCooldown = 687, // dstr 687: "在一定时间内， 无法使用该角色名\\n已删除的角色名， 需经过30天后才能再次使用"
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterDeletionSucceeded = 688, // dstr 688: "删除角色成功"
        // 原生入口：CMD6 consumer 0233DD20。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    CharacterDeletionDatabaseError = 689, // dstr 689: "FALL : DB错误！ "
        // 原生入口：CMD6 consumer 0233DD20。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    CharacterActiveCharacterCannotBeDeleted = 690, // dstr 690: "无法删除正在使用中的角色"
        // 原生入口：CMD6 consumer 0233DD20。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterDoesNotExist = 691, // dstr 691: "FALL : 不能删除一个不存在的角色！ "
        // 原生入口：CMD6 consumer 0233DD20。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    CharacterSameDayDeletionForbidden = 692, // dstr 692: "新创建的角色无法在当日内删除。"
        // 原生入口：CMD6 consumer 0233DD20。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterLeaveGuildBeforeDeletion = 693, // dstr 693: "加入公会的角色， 只有在退出公会后才能删除"
        // 原生入口：CMD6 consumer 0233DD20。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyDuelGoldShortfall = 716, // dstr 716: "对战费用不足， 还缺少%d金币， 无法进入房间"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    InventoryMoveItemFailed = 732, // dstr 732: "移动物品失败！ "
        // 原生入口：CMD19 consumer 02372610。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    InventoryMoveItemFailedAlternate = 733, // dstr 733: "移动物品失败！ "
        // 原生入口：CMD19 consumer 02372610。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    InventoryEquipTradeFailed = 734, // dstr 734: "装备交易失败！ "
        // 原生入口：CMD19 consumer 02372610。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    InventoryMoveTradeItemFailed = 735, // dstr 735: "移动交易物品失败！ "
        // 原生入口：CMD19 consumer 02372610。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    InventoryRemoveTradeItemFailed = 736, // dstr 736: "取下交易物品失败！ "
        // 原生入口：CMD19 consumer 02372610。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    InventoryPlaceTradeItemFailed = 737, // dstr 737: "放置交易物品失败！ "
        // 原生入口：CMD19 consumer 02372610。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    SkillChangeSlotFailed = 769, // dstr 769: "替换技能失败！ "
        // 原生入口：CMD28 consumer 0234B050。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    QuestGivenUp = 789, // dstr 789: "放弃[%s]任务"
        // 原生入口：CMD32 consumer 02352C40。
        // 底层问题：需要真实放弃任务的ACK32及任务号，客户端从原任务对象取得名称。
    QuestCompleted = 790, // dstr 790: "完成[%s]任务"
        // 原生入口：CMD34 consumer 02376BF0。
        // 底层问题：需要真实ACK34及完整奖励字段；索引本身不能证明任务已完成。
    InventoryUseItemFailed = 794, // dstr 794: "使用物品失败！ "
        // 原生入口：CMD44 consumer 02356A00。
        // 底层问题：该客户端路径用于诊断，未闭合独立可见提示；不能把日志字符串冒充UI通知或替代原业务结果。
    InventoryInsufficientItemsToDrop = 799, // dstr 799: "可掉落物品不足"
        // 原生入口：CMD47 consumer 02356FA0。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    InventoryCurrentStateBlocksItemDrop = 800, // dstr 800: "目前无法丢弃物品"
        // 原生入口：CMD47 consumer 02356FA0。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    GuildLevelUpError = 825, // dstr 825: "             发生错误"
        // 原生入口：CMD68 consumer 0234B7D0。
        // 底层问题：这是共用default分支文案，尚无唯一已核验业务错误码，不猜一个数字来强行显示。
    GuildLevelUpSucceeded = 826, // dstr 826: "公会Lv已上升"
        // 原生入口：CMD73 consumer 023585A0。
        // 底层问题：需要公会升级已提交结果及原CMD73成功字段，不提供仅显示成功的捷径。
    GuildGeneralError = 832, // dstr 832: "发生错误"
        // 原生入口：CMD73 consumer 023585A0; CMD156 consumer 0234B6F0。
        // 底层问题：这是共用default分支文案，尚无唯一已核验业务错误码，不猜一个数字来强行显示。
    GuildNameMissing = 833, // dstr 833: "无公会名"
        // 原生入口：CMD75 consumer 0233E480。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    GuildJoinInvitation = 834, // dstr 834: "[%s]邀请您加入[%s]公会\\n点击〈同意〉按钮， 就会进入该公会页面"
        // 原生入口：CMD75 consumer 0233E480。
        // 底层问题：需要原公会邀请对象及成功包字段，不能只用文本ID打开有效邀请。
    GuildOrdinaryError = 838, // dstr 838: "          普通错误"
        // 原生入口：CMD75 consumer 0233E480。
        // 底层问题：这是共用default分支文案，尚无唯一已核验业务错误码，不猜一个数字来强行显示。
    PartyOwnPrivateStoreBlocksParty = 1344, // dstr 1344: "个人商店开设中， 无法组队"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyOwnPrivateStoreBlocksTrade = 1345, // dstr 1345: "个人商店开设中， 无法进行交易"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyOwnPrivateStoreBlocksPvp = 1346, // dstr 1346: "个人商店开设中， 无法进入决斗场"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyTargetPrivateStoreBlocksParty = 1347, // dstr 1347: "对方正在开启个人商店， 无法组队"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyTargetPrivateStoreBlocksTrade = 1348, // dstr 1348: "对方正在开启个人商店， 无法交易"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyTargetPrivateStoreBlocksPvp = 1349, // dstr 1349: "对方正在开启个人商店， 无法进入决斗场"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterInvalidNameRequiresWebsiteChange = 2040, // dstr 2040: "角色名有误， 需到官方网站上进行更改， 才能进入游戏"
        // 原生入口：CMD4 consumer 023415C0。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    InventoryGoldDropLimit = 2453, // dstr 2453: "已超过可丢弃的金币上限"
        // 原生入口：CMD47 consumer 02356FA0。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    MailAttachmentOnlyMailDisappearsAfterClaim = 5496, // dstr 5496: "若邮件中只有附件物品， 把附件中的物品或金币移到物品栏后， 邮件将消失。 "
        // 原生入口：CMD94/95/134/324 consumer 02E194A0。
        // 底层问题：这是邮箱界面对真实邮件对象的说明，不是领取错误或领取完成事实。
    CharacterDailyCreationLimit = 6160, // dstr 6160: "每天最多可创建2个新角色"
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyStreetPvpOrGuildArenaUnavailable = 6553, // dstr 6553: "无法进行街头争霸或公会擂台战。"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyChallengeTooFar = 6554, // dstr 6554: "距离太远， 无法发起挑战。"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    GuildNameAvailable = 6715, // dstr 6715: "可以使用的公会名。"
        // 原生入口：CMD156 consumer 0234B6F0。
        // 底层问题：公会名可用属于CMD156成功分支，需要实际名称检查结果，不是错误码。
    PartyUntranslated11137 = 11137, // dstr 11137: "dnf_11137"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：客户端资源本身仍为占位文本，本地化及业务绑定均未完成，不能靠发送某个ACK把占位内容变成完整中文。
    PartySinglesDayGenderPartyRestriction = 13186, // dstr 13186: "光棍节特别行动正在进行！！\\n\\n活动期间， 无法与异性组队。"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyUntranslated19317 = 19317, // dstr 19317: "dnf_19317"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：客户端资源本身仍为占位文本，本地化及业务绑定均未完成，不能靠发送某个ACK把占位内容变成完整中文。
    PartyPvpGradeChannelMismatch = 19318, // dstr 19318: "您的决斗等级不适合在当前频道进行决斗， 请选择与自身决斗等级相符合的决斗场。"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    InventoryDungeonBlocksArtifactOrHigherDrop = 19323, // dstr 19323: "无法在地下城内丢弃神器及以上物品， 请在城镇中进行物品交易。"
        // 原生入口：CMD47 consumer 02356FA0。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    GuildCreateCertificationCompleted = 19393, // dstr 19393: "已经完成认可创建公会。"
        // 原生入口：CMD742 consumer 0234ED30。
        // 底层问题：必须先提交公会认证创建事务，之后发送ACK742成功及原公会投影。
    PartyStreetPvpInvitationUndeliverable = 22235, // dstr 22235: "对方的队员无法收到街头争霸邀请"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyStreetPvpCancelledMemberCountChanged = 22236, // dstr 22236: "争霸双方人数变更， 本次街头争霸取消。"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyStreetPvpTargetOffline = 22238, // dstr 22238: "该玩家不在线"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartySiegeBlocksStreetPvp = 22242, // dstr 22242: "怪物攻城中， 无法进行街头争霸。"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyStreetPvpRefused = 22247, // dstr 22247: "拒绝了街头争霸的邀请"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterPendingConfirmation = 22273, // dstr 22273: "后续确认"
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：客户端资源本身仍为占位文本，本地化及业务绑定均未完成，不能靠发送某个ACK把占位内容变成完整中文。
    PartyOwnShopBlocksParty = 22450, // dstr 22450: "商店开设中， 无法组队。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyOwnShopBlocksTrade = 22451, // dstr 22451: "商店开设中， 无法交易。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyOwnShopBlocksPvp = 22452, // dstr 22452: "商店开设中， 无法进入决斗场。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyTargetShopBlocksParty = 22453, // dstr 22453: "对方商店正在开设中， 无法组队。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyTargetShopBlocksTrade = 22454, // dstr 22454: "对方商店正在开设中， 无法交易。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyTargetShopBlocksPvp = 22455, // dstr 22455: "对方商店正在开设中， 无法进入决斗场。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterHellModeCharacterUnavailable = 22543, // dstr 22543: "该角色无法进行地狱模式。"
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterHellModeRequiresLevel85Character = 22544, // dstr 22544: "只有账号内存在至少一个Lv85的角色时， 才可以挑战地狱模式。"
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterHellModeDailyCreationLimit = 22557, // dstr 22557: "每日只可创建1次地狱模式角色， 今日无法再次在地狱模式下进行游戏。\\n每日凌晨6点重置地狱模式角色的创建次数。"
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    MailGoldReceived = 23017, // dstr 23017: "您获得了%d金币"
        // 原生入口：CMD94/95/134/324 consumer 02E194A0。
        // 底层问题：需要ACK95逐附件真实领取结果及资产提交；不能用文本索引代替入账、完成或部分完成事实。
    CharacterGuildMasterOrMarriageBlocksDeletion = 25005, // dstr 25005: "无法删除角色。 请确认以下事项。\\n1. 担任公会会长的角色必须委任了新会长后才能删除。\\n2. 已订婚或结婚的角色必须在完成退婚或离婚后才能删除。"
        // 原生入口：CMD6 consumer 0233DD20。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartySelectedPlayerTeleporting = 31056, // dstr 31056: "选定的玩家正处于组队传送状态"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyTeleportCannotInvite = 31058, // dstr 31058: "队伍正在传送中， 无法邀请队员。"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyTeleportWait = 31066, // dstr 31066: "队伍正在传送中， 请稍候再发出请求。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyTeleportBlocksEnchant = 31070, // dstr 31070: "队伍正在传送中， 无法进行附魔。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    AuctionPurchaseCompleted = 31547, // dstr 31547: "购买完成。\\n\\n购买的物品可在邮箱中保存6个月。"
        // 原生入口：client helper consumer 03729530。
        // 底层问题：需要拍卖真实结果、面板模式及钱货提交；失败宽度已统一为原生u16，但原领域错误选码仍不能证明文案符合实际原因。
    CharacterPvpCharacterSharesRp = 31698, // dstr 31698: "原有角色已有RP， 创建同一个角色时， 会以原有角色的当前RP值创建角色。"
        // 原生入口：CMD872 consumer 0233CDB0。
        // 底层问题：依赖决斗专用角色的原预检及RP/创建历史事实，不能只为显示该文本而改预检成功标志。
    CharacterDuplicatePvpAdvancement = 31704, // dstr 31704: "已经创建可进入决斗场的相同转职角色。"
        // 原生入口：CMD872 consumer 0233CDB0。
        // 底层问题：依赖决斗专用角色的原预检及RP/创建历史事实，不能只为显示该文本而改预检成功标志。
    CharacterDeletedPvpCharacterCooldown = 31706, // dstr 31706: "删除角色后， 30天内无法再创建角色。"
        // 原生入口：CMD872 consumer 0233CDB0。
        // 底层问题：依赖决斗专用角色的原预检及RP/创建历史事实，不能只为显示该文本而改预检成功标志。
    MailPvpExclusiveCharacterCannotReceive = 31716, // dstr 31716: "无法给决斗专属角色发送邮件。"
        // 原生入口：CMD324 consumer 0234D0B0。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    RaidParticipationUnavailable = 31726, // dstr 31726: "目前无法参加。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterNameAvailable = 33193, // dstr 33193: "可以使用的ID"
        // 原生入口：CMD692 consumer 0233B8F0。
        // 底层问题：名称可用来自真实查重成功，不是可单独发送的错误；应使用CharacterPackets.NameCheck的真实查询结果。
    PartyNotCurrentPartyMember = 33307, // dstr 33307: "该角色非当前队伍成员。\\n请注意道具交易。"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：此警告依赖玩家交互回应的来源身份及mode，不是独立错误分支。
    MailPendingConfirmation = 36199, // dstr 36199: "后续确认"
        // 原生入口：CMD94/95/134/324 consumer 02E194A0。
        // 底层问题：客户端资源本身仍为占位文本，本地化及业务绑定均未完成，不能靠发送某个ACK把占位内容变成完整中文。
    PartyUntranslated38232 = 38232, // dstr 38232: "dnf_38232"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：客户端资源本身仍为占位文本，本地化及业务绑定均未完成，不能靠发送某个ACK把占位内容变成完整中文。
    PartyParkourModeUnavailable = 38233, // dstr 38233: "跑酷竞技场中， 无法使用该功能。"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    AccountUnverifiedTargetAutoRejectsTrade = 40185, // dstr 40185: "当前冒险家未完成安全认证， 已自动拒绝其他冒险家发送的交易请求。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    AccountUnverifiedRequesterTradeStopped = 40186, // dstr 40186: "申请交易的玩家未处在安全认证状态， 交易终止。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    AccountVerificationRequiredForTrade = 40187, // dstr 40187: "您尚未进行安全认证， 无法与对方交易。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyDispatchedMercenaryUnavailable = 42365, // dstr 42365: "出战中的佣兵角色无法参与该内容"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyTargetMercenaryDispatched = 42366, // dstr 42366: "对方正作为佣兵出战中"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    ShopUntranslated42770 = 42770, // dstr 42770: "dnf_42770"
        // 原生入口：CMD21 consumer 0236B0A0。
        // 底层问题：客户端资源本身仍为占位文本，本地化及业务绑定均未完成，不能靠发送某个ACK把占位内容变成完整中文。
    CharacterNameContainsUnusableText = 44462, // dstr 44462: "包含无法使用的文字"
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    InventoryExpiredItemCannotDrop = 44482, // dstr 44482: "无法丢弃超过使用期限的物品。"
        // 原生入口：CMD47 consumer 02356FA0。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyUntranslated44513 = 44513, // dstr 44513: "dnf_44513"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：客户端资源本身仍为占位文本，本地化及业务绑定均未完成，不能靠发送某个ACK把占位内容变成完整中文。
    PartyTargetShopBlocksRaid = 44514, // dstr 44514: "对方正在经营商店， 无法加入攻坚队。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartySiegeBlocksChallenge = 46110, // dstr 46110: "怪物攻城进行中， 玩家之间不能互相挑战。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterUntranslated46145 = 46145, // dstr 46145: "dnf_46145"
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：客户端资源本身仍为占位文本，本地化及业务绑定均未完成，不能靠发送某个ACK把占位内容变成完整中文。
    RepairSucceeded = 46382, // dstr 46382: "装备修理成功"
        // 原生入口：CMD23 consumer 02355D60。
        // 底层问题：需要RepairPackets.Ack的真实修理提交、余额及源位置，不能只为显示成功文本而伪造成功。
    PartyStreetPvpPeacePartyRestriction = 46493, // dstr 46493: "发起或接受挑战的玩家(或队伍)， 在结束争霸后的10分钟内， 无法与处于“和平的呐喊”状态下的玩家(或队伍)组队。"
        // 原生入口：CMD10 consumer 02363C00; CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterServerFullCreationPaused = 46553, // dstr 46553: "%s由于用户数量过多， 为了保证现有用户的体验， 该服务器暂停创建新角色。\\n您可以选择其它服务器进行游戏， 谢谢合作。\\n    -- 地下城与勇士"
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterServerCreationPaused = 46654, // dstr 46654: "%s目前暂停创建新角色， 给您带来不便， 敬请见谅。\\n具体开放信息请留意官网公告。\\n    -- 地下城与勇士"
        // 原生入口：CMD5 consumer 02343150。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyUntranslated46690 = 46690, // dstr 46690: "dnf_46690"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：客户端资源本身仍为占位文本，本地化及业务绑定均未完成，不能靠发送某个ACK把占位内容变成完整中文。
    PartyParkourUnavailable = 46691, // dstr 46691: "跑酷竞技场中， 无法使用该功能。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    RaidPreviouslyKicked = 46792, // dstr 46792: "强制退出的攻坚队， 无法加入。"
        // 原生入口：CMD10 consumer 02363C00; CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    InventoryTitleAnimationCannotBeReplaced = 48414, // dstr 48414: "当前称号无法替换称号动画。"
        // 原生入口：CMD19 consumer 02372610。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    InventoryTitleAnimationReplacementRemoved = 48417, // dstr 48417: "已解除替换的称号动画。"
        // 原生入口：CMD19 consumer 02372610。
        // 底层问题：依赖称号动画移动结果和目标物品，未确认独立成功通知。
    CharacterDispatchedMercenaryCannotBeDeleted = 49100, // dstr 49100: "出战状态下的佣兵角色无法被删除。"
        // 原生入口：CMD6 consumer 0233DD20。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyGuildArenaRefused = 49228, // dstr 49228: "已拒绝公会擂台战。"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterUntranslated49516 = 49516, // dstr 49516: "dnf_49516"
        // 原生入口：CMD4 consumer 023415C0; CMD10 consumer 02363C00。
        // 底层问题：客户端资源本身仍为占位文本，本地化及业务绑定均未完成，不能靠发送某个ACK把占位内容变成完整中文。
    PartyUntranslated49519 = 49519, // dstr 49519: "dnf_49519"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：客户端资源本身仍为占位文本，本地化及业务绑定均未完成，不能靠发送某个ACK把占位内容变成完整中文。
    CharacterUntranslated49525 = 49525, // dstr 49525: "dnf_49525"
        // 原生入口：CMD4 consumer 023415C0。
        // 底层问题：客户端资源本身仍为占位文本，本地化及业务绑定均未完成，不能靠发送某个ACK把占位内容变成完整中文。
    AuctionListingSucceeded = 60083, // dstr 60083: "已完成拍卖物品的上架"
        // 原生入口：client helper consumer 03729310。
        // 底层问题：需要拍卖真实结果、面板模式及钱货提交；失败宽度已统一为原生u16，但原领域错误选码仍不能证明文案符合实际原因。
    AuctionListingCancelled = 60084, // dstr 60084: "已取消拍卖物品的上架。\\n取消的拍卖品将在邮箱保管6个月。"
        // 原生入口：client helper consumer 037294E0; client helper consumer 026FD570。
        // 底层问题：需要拍卖真实结果、面板模式及钱货提交；失败宽度已统一为原生u16，但原领域错误选码仍不能证明文案符合实际原因。
    AuctionBiddingSucceeded = 60085, // dstr 60085: "已完成竞拍"
        // 原生入口：client helper consumer 026FD5C0。
        // 底层问题：需要拍卖真实结果、面板模式及钱货提交；失败宽度已统一为原生u16，但原领域错误选码仍不能证明文案符合实际原因。
    RaidStartedPreviousDay = 66023, // dstr 66023: "无法参加前一天开始的攻坚战。"
        // 原生入口：CMD10 consumer 02363C00; CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    RaidWeeklyChallengeLimit = 66066, // dstr 66066: "已超过每周可挑战次数。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyEventAreaUnavailable = 66213, // dstr 66213: "位于活动区域的用户， 无法使用。"
        // 原生入口：CMD10 consumer 02363C00; CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    QuestReaperMoodGranted = 66260, // dstr 66260: "已获得[死神代理 - 心情状态]。"
        // 原生入口：CMD34 consumer 02376BF0。
        // 底层问题：特定任务成功分支还依赖实际任务及客户端状态，不是独立的通知索引。
    MailExtractionComplete = 68017, // dstr 68017: "邮件领取完毕。"
        // 原生入口：CMD94/95/134/324 consumer 02E194A0。
        // 底层问题：需要ACK95逐附件真实领取结果及资产提交；不能用文本索引代替入账、完成或部分完成事实。
    MailPartialExtractionComplete = 68019, // dstr 68019: "除了一部分邮件以外领取完毕。"
        // 原生入口：CMD94/95/134/324 consumer 02E194A0。
        // 底层问题：需要ACK95逐附件真实领取结果及资产提交；不能用文本索引代替入账、完成或部分完成事实。
    PartyCreateEvildomPartyFirst = 68134, // dstr 68134: "在魔界大战频道中， 通过NPC或[寻找队伍]创建队伍后， 可以邀请其他玩家。"
        // 原生入口：CMD10 consumer 02363C00; CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyEvildomTaskForbidsNewMembers = 68135, // dstr 68135: "新成员无法加入正在执行魔界大战任务的队伍中。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyEvildomEntryOrFatigueLimit = 68137, // dstr 68137: "魔界大战入场次数超过限制或疲劳值不足， 无法加入队伍。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    RaidCharacterDailyLimit = 69006, // dstr 69006: "该角色已超出每日可挑战的次数。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    RaidCharacterWeeklyLimit = 69007, // dstr 69007: "该角色已超出每周可挑战的次数。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    ChatMessageIntervalRequired = 70137, // dstr 70137: "无法连续发送聊天信息， 每条聊天信息发送间隔为%d秒"
        // 原生入口：CMD17 consumer 0234D280。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    AccountTargetConfinedToSeriaRoom = 70353, // dstr 70353: "对方被禁闭在赛丽亚旅馆， 无法进行该操作。"
        // 原生入口：CMD10 consumer 02363C00; CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    AccountTargetAccountSecurityMode = 70369, // dstr 70369: "对方帐号现在处于安全模式中， 您无法进行该操作。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    AccountTargetLoginTradeDelay = 70380, // dstr 70380: "为了保护对方的帐号安全，对方登录%u秒后才可以交易。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    MailTokenReceived = 70516, // dstr 70516: "获得%d代币券"
        // 原生入口：CMD94/95/134/324 consumer 02E194A0。
        // 底层问题：需要ACK95逐附件真实领取结果及资产提交；不能用文本索引代替入账、完成或部分完成事实。
    PartyBattlefieldSchedule = 70816, // dstr 70816: "无法进入战场。 战场仅在每周二、 周五开放。 请届时参战。"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    AccountTargetSanctionedForStreetPvp = 72875, // dstr 72875: "对方处于被制裁状态，无法进行街头争霸"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    ChatMegaphoneRequiresLevel86Character = 74154, // dstr 74154: "帐号内拥有Lv86及以上角色的冒险家才能使用喇叭。"
        // 原生入口：CMD17 consumer 0234D280。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    RaidJoinAntiMagicInsufficient = 75630, // dstr 75630: "抗魔值不足， 无法加入攻坚队。 抗魔值需要高于(%d)。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyWithAdvertiserForbidden = 75745, // dstr 75745: "无法与发布公告状态下的玩家组队。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyTargetCannotAcceptMentorship = 75748, // dstr 75748: "对方目前无法接受拜师申请。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：本服尚未接入拜师请求和状态owner，仍缺真实交互mode、资格判定及原生应答路径。
    PartyMentorFriendListFull = 75749, // dstr 75749: "冒险团好友人数已满， 无法申请拜师。"
        // 原生入口：CMD10 consumer 02363C00; CMD11 consumer 02354540。
        // 底层问题：本服尚未接入拜师请求和状态owner，仍缺真实交互mode、资格判定及原生应答路径。
    PartyTemporarilyUnavailable = 75846, // dstr 75846: "该队伍暂时无法加入"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    CharacterChangeAdventureRepresentativeBeforeDeletion = 100002098, // dstr 100002098: "请在冒险团角色收藏簿页面中更改代表角色后， 再进行删除。"
        // 原生入口：CMD6 consumer 0233DD20。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    MailItemsReceived = 100002127, // dstr 100002127: "获得了%d个%s。"
        // 原生入口：CMD94/95/134/324 consumer 02E194A0。
        // 底层问题：需要ACK95逐附件真实领取结果及资产提交；不能用文本索引代替入账、完成或部分完成事实。
    PartyGrowthModeMismatch = 100002271, // dstr 100002271: "成长模式不同， 无法组队。\\n达到满级后， 即使模式不同也可以组队。"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyEradicationCannotInviteWaitingPlayer = 100003109, // dstr 100003109: "灭杀成功后， 无法邀请待机中的玩家。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    RaidTutorialRequiredForBasicMode = 100007074, // dstr 100007074: "未通关教程的角色无法申请加入或被邀请加入基础模式的攻坚队。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    RaidAlreadyStarted = 100007075, // dstr 100007075: "攻坚队已开始攻坚。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyJoinStateUnavailable = 100020035, // dstr 100020035: "当前状态下无法加入该队伍。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyTargetInEvildomGuideMode = 100020039, // dstr 100020039: "对方正在进行魔界大战引导模式。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    RaidStartedBattleCannotJoin = 300000191, // dstr 300000191: "无法加入已开始战斗的攻坚队。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    RaidRankedAdvancementMemberLimit = 300000192, // dstr 300000192: "排位模式中， 相同转职最多仅可进入2名玩家。"
        // 原生入口：CMD11 consumer 02354540。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
    PartyAntiMagicInsufficient = 300000986 // dstr 300000986: "抗魔值不足， 无法加入队伍。"
        // 原生入口：CMD10 consumer 02363C00。
        // 底层问题：尚未在类型接口中闭合该业务的mode/错误码及完整字段绑定；不能把dstr索引当作错误码发送，不代表文本不存在。
}

internal static class ClientPromptDiagnostics
{
    public static string Explain(ClientUnboundDstr prompt)
    {
        var (source, problem) = prompt switch
        {
            ClientUnboundDstr.PartyOnlyInTown or
                ClientUnboundDstr.PartyOwnPrivateStoreBlocksParty or
                ClientUnboundDstr.PartyOwnPrivateStoreBlocksTrade or
                ClientUnboundDstr.PartyOwnPrivateStoreBlocksPvp or
                ClientUnboundDstr.PartyTargetPrivateStoreBlocksParty or
                ClientUnboundDstr.PartyTargetPrivateStoreBlocksTrade or
                ClientUnboundDstr.PartyTargetPrivateStoreBlocksPvp or
                ClientUnboundDstr.PartyOwnShopBlocksParty or
                ClientUnboundDstr.PartyOwnShopBlocksTrade or
                ClientUnboundDstr.PartyOwnShopBlocksPvp or
                ClientUnboundDstr.PartyTargetShopBlocksParty or
                ClientUnboundDstr.PartyTargetShopBlocksTrade or
                ClientUnboundDstr.PartyTargetShopBlocksPvp or
                ClientUnboundDstr.PartyTeleportWait or
                ClientUnboundDstr.PartyTeleportBlocksEnchant or
                ClientUnboundDstr.RaidParticipationUnavailable or
                ClientUnboundDstr.AccountUnverifiedTargetAutoRejectsTrade or
                ClientUnboundDstr.AccountUnverifiedRequesterTradeStopped or
                ClientUnboundDstr.AccountVerificationRequiredForTrade or
                ClientUnboundDstr.PartyTargetMercenaryDispatched or
                ClientUnboundDstr.PartyTargetShopBlocksRaid or
                ClientUnboundDstr.PartySiegeBlocksChallenge or
                ClientUnboundDstr.PartyParkourUnavailable or
                ClientUnboundDstr.RaidWeeklyChallengeLimit or
                ClientUnboundDstr.PartyEvildomTaskForbidsNewMembers or
                ClientUnboundDstr.PartyEvildomEntryOrFatigueLimit or
                ClientUnboundDstr.RaidCharacterDailyLimit or
                ClientUnboundDstr.RaidCharacterWeeklyLimit or
                ClientUnboundDstr.AccountTargetAccountSecurityMode or
                ClientUnboundDstr.AccountTargetLoginTradeDelay or
                ClientUnboundDstr.RaidJoinAntiMagicInsufficient or
                ClientUnboundDstr.PartyWithAdvertiserForbidden or
                ClientUnboundDstr.PartyEradicationCannotInviteWaitingPlayer or
                ClientUnboundDstr.RaidTutorialRequiredForBasicMode or
                ClientUnboundDstr.RaidAlreadyStarted or
                ClientUnboundDstr.PartyJoinStateUnavailable or
                ClientUnboundDstr.PartyTargetInEvildomGuideMode or
                ClientUnboundDstr.RaidStartedBattleCannotJoin or
                ClientUnboundDstr.PartyAntiMagicInsufficient => ("CMD10 consumer 02363C00", "Native operation-specific mode/error routing is not yet bound by this typed interface. A dstr index is not a wire error; inspect the listed consumer before encoding."),
            ClientUnboundDstr.CharacterCreationSucceeded or
                ClientUnboundDstr.CharacterCreationDatabaseError or
                ClientUnboundDstr.CharacterCreationStateInvalid or
                ClientUnboundDstr.CharacterJobDoesNotExist or
                ClientUnboundDstr.CharacterNameTemporarilyBlocked => ("CMD5 consumer 02343150", "This client path uses diagnostic text; no independent visible prompt is established. Preserve the real business response instead of treating a log string as UI."),
            ClientUnboundDstr.CharacterLimitReached or
                ClientUnboundDstr.CharacterNameBlockedOrDuplicate or
                ClientUnboundDstr.CharacterDeletedNameCooldown or
                ClientUnboundDstr.CharacterDailyCreationLimit or
                ClientUnboundDstr.CharacterHellModeCharacterUnavailable or
                ClientUnboundDstr.CharacterHellModeRequiresLevel85Character or
                ClientUnboundDstr.CharacterHellModeDailyCreationLimit or
                ClientUnboundDstr.CharacterNameContainsUnusableText or
                ClientUnboundDstr.CharacterServerFullCreationPaused or
                ClientUnboundDstr.CharacterServerCreationPaused => ("CMD5 consumer 02343150", "Native operation-specific mode/error routing is not yet bound by this typed interface. A dstr index is not a wire error; inspect the listed consumer before encoding."),
            ClientUnboundDstr.CharacterDeletionSucceeded or
                ClientUnboundDstr.CharacterDeletionDatabaseError or
                ClientUnboundDstr.CharacterDoesNotExist => ("CMD6 consumer 0233DD20", "This client path uses diagnostic text; no independent visible prompt is established. Preserve the real business response instead of treating a log string as UI."),
            ClientUnboundDstr.CharacterActiveCharacterCannotBeDeleted or
                ClientUnboundDstr.CharacterSameDayDeletionForbidden or
                ClientUnboundDstr.CharacterLeaveGuildBeforeDeletion or
                ClientUnboundDstr.CharacterGuildMasterOrMarriageBlocksDeletion or
                ClientUnboundDstr.CharacterDispatchedMercenaryCannotBeDeleted or
                ClientUnboundDstr.CharacterChangeAdventureRepresentativeBeforeDeletion => ("CMD6 consumer 0233DD20", "Native operation-specific mode/error routing is not yet bound by this typed interface. A dstr index is not a wire error; inspect the listed consumer before encoding."),
            ClientUnboundDstr.PartyDuelGoldShortfall or
                ClientUnboundDstr.PartyStreetPvpOrGuildArenaUnavailable or
                ClientUnboundDstr.PartyChallengeTooFar or
                ClientUnboundDstr.PartySinglesDayGenderPartyRestriction or
                ClientUnboundDstr.PartyPvpGradeChannelMismatch or
                ClientUnboundDstr.PartyStreetPvpInvitationUndeliverable or
                ClientUnboundDstr.PartyStreetPvpCancelledMemberCountChanged or
                ClientUnboundDstr.PartyStreetPvpTargetOffline or
                ClientUnboundDstr.PartySiegeBlocksStreetPvp or
                ClientUnboundDstr.PartyStreetPvpRefused or
                ClientUnboundDstr.PartySelectedPlayerTeleporting or
                ClientUnboundDstr.PartyTeleportCannotInvite or
                ClientUnboundDstr.PartyParkourModeUnavailable or
                ClientUnboundDstr.PartyDispatchedMercenaryUnavailable or
                ClientUnboundDstr.PartyGuildArenaRefused or
                ClientUnboundDstr.PartyBattlefieldSchedule or
                ClientUnboundDstr.AccountTargetSanctionedForStreetPvp or
                ClientUnboundDstr.PartyTemporarilyUnavailable or
                ClientUnboundDstr.PartyGrowthModeMismatch or
                ClientUnboundDstr.RaidRankedAdvancementMemberLimit => ("CMD11 consumer 02354540", "Native operation-specific mode/error routing is not yet bound by this typed interface. A dstr index is not a wire error; inspect the listed consumer before encoding."),
            ClientUnboundDstr.InventoryMoveItemFailed or
                ClientUnboundDstr.InventoryMoveItemFailedAlternate or
                ClientUnboundDstr.InventoryEquipTradeFailed or
                ClientUnboundDstr.InventoryMoveTradeItemFailed or
                ClientUnboundDstr.InventoryRemoveTradeItemFailed or
                ClientUnboundDstr.InventoryPlaceTradeItemFailed => ("CMD19 consumer 02372610", "This client path uses diagnostic text; no independent visible prompt is established. Preserve the real business response instead of treating a log string as UI."),
            ClientUnboundDstr.SkillChangeSlotFailed => ("CMD28 consumer 0234B050", "This client path uses diagnostic text; no independent visible prompt is established. Preserve the real business response instead of treating a log string as UI."),
            ClientUnboundDstr.QuestGivenUp => ("CMD32 consumer 02352C40", "Requires QuestPackets.GivenUp for the actual relinquished quest; the client supplies its local quest name."),
            ClientUnboundDstr.QuestCompleted => ("CMD34 consumer 02376BF0", "Requires the real QuestPackets.Finish result and complete reward body; the dstr ID does not prove completion."),
            ClientUnboundDstr.InventoryUseItemFailed => ("CMD44 consumer 02356A00", "This client path uses diagnostic text; no independent visible prompt is established. Preserve the real business response instead of treating a log string as UI."),
            ClientUnboundDstr.InventoryInsufficientItemsToDrop or
                ClientUnboundDstr.InventoryCurrentStateBlocksItemDrop or
                ClientUnboundDstr.InventoryGoldDropLimit or
                ClientUnboundDstr.InventoryDungeonBlocksArtifactOrHigherDrop or
                ClientUnboundDstr.InventoryExpiredItemCannotDrop => ("CMD47 consumer 02356FA0", "Native operation-specific mode/error routing is not yet bound by this typed interface. A dstr index is not a wire error; inspect the listed consumer before encoding."),
            ClientUnboundDstr.GuildLevelUpError => ("CMD68 consumer 0234B7D0", "Shared default-branch text has no unique verified native error code in this interface; no arbitrary number is selected to force it."),
            ClientUnboundDstr.GuildLevelUpSucceeded => ("CMD73 consumer 023585A0", "Requires the committed guild level result and original CMD73 success fields; no standalone success shortcut."),
            ClientUnboundDstr.GuildGeneralError => ("CMD73 consumer 023585A0; CMD156 consumer 0234B6F0", "Shared default-branch text has no unique verified native error code in this interface; no arbitrary number is selected to force it."),
            ClientUnboundDstr.GuildNameMissing => ("CMD75 consumer 0233E480", "Native operation-specific mode/error routing is not yet bound by this typed interface. A dstr index is not a wire error; inspect the listed consumer before encoding."),
            ClientUnboundDstr.GuildJoinInvitation => ("CMD75 consumer 0233E480", "Requires the original guild invitation context and successful CMD75 fields; a bare text ID cannot open a valid invitation."),
            ClientUnboundDstr.GuildOrdinaryError => ("CMD75 consumer 0233E480", "Shared default-branch text has no unique verified native error code in this interface; no arbitrary number is selected to force it."),
            ClientUnboundDstr.CharacterInvalidNameRequiresWebsiteChange => ("CMD4 consumer 023415C0", "Native operation-specific mode/error routing is not yet bound by this typed interface. A dstr index is not a wire error; inspect the listed consumer before encoding."),
            ClientUnboundDstr.MailAttachmentOnlyMailDisappearsAfterClaim => ("CMD94/95/134/324 consumer 02E194A0", "This is a mailbox UI explanation linked to a real mail object, not an extraction error or a completion fact."),
            ClientUnboundDstr.GuildNameAvailable => ("CMD156 consumer 0234B6F0", "Guild-name availability is CMD156 success, not a guild-name error value; requires the real name lookup result."),
            ClientUnboundDstr.PartyUntranslated11137 or
                ClientUnboundDstr.PartyUntranslated44513 or
                ClientUnboundDstr.PartyUntranslated46690 or
                ClientUnboundDstr.PartyUntranslated49519 => ("CMD10 consumer 02363C00", "The client resource itself is a placeholder; localized wording and operation-specific binding are incomplete. A server ACK cannot turn it into translated text."),
            ClientUnboundDstr.PartyUntranslated19317 or
                ClientUnboundDstr.PartyUntranslated38232 => ("CMD11 consumer 02354540", "The client resource itself is a placeholder; localized wording and operation-specific binding are incomplete. A server ACK cannot turn it into translated text."),
            ClientUnboundDstr.GuildCreateCertificationCompleted => ("CMD742 consumer 0234ED30", "Requires guild creation certification to be committed before CMD742 success and the original guild projections."),
            ClientUnboundDstr.CharacterPendingConfirmation or
                ClientUnboundDstr.CharacterUntranslated46145 => ("CMD5 consumer 02343150", "The client resource itself is a placeholder; localized wording and operation-specific binding are incomplete. A server ACK cannot turn it into translated text."),
            ClientUnboundDstr.MailGoldReceived or
                ClientUnboundDstr.MailExtractionComplete or
                ClientUnboundDstr.MailPartialExtractionComplete or
                ClientUnboundDstr.MailTokenReceived or
                ClientUnboundDstr.MailItemsReceived => ("CMD94/95/134/324 consumer 02E194A0", "Requires actual ACK95 per-attachment claim results and committed assets; do not use text IDs to fake a claim."),
            ClientUnboundDstr.AuctionPurchaseCompleted => ("client helper consumer 03729530", "Requires the real auction result, panel mode and committed item/gold changes. Failure widths are now native u16; old domain error choices still do not prove the displayed reason."),
            ClientUnboundDstr.CharacterPvpCharacterSharesRp or
                ClientUnboundDstr.CharacterDuplicatePvpAdvancement or
                ClientUnboundDstr.CharacterDeletedPvpCharacterCooldown => ("CMD872 consumer 0233CDB0", "Depends on the original PvP-character precheck and RP/history facts; existing byte flags must not be changed merely to show a message."),
            ClientUnboundDstr.MailPvpExclusiveCharacterCannotReceive => ("CMD324 consumer 0234D0B0", "Native operation-specific mode/error routing is not yet bound by this typed interface. A dstr index is not a wire error; inspect the listed consumer before encoding."),
            ClientUnboundDstr.CharacterNameAvailable => ("CMD692 consumer 0233B8F0", "Name validation success is a business result, not an error code. Use CharacterPackets.NameCheck(false) after the real lookup."),
            ClientUnboundDstr.PartyNotCurrentPartyMember => ("CMD11 consumer 02354540", "This warning belongs to a peer response success context. Source identity and mode must be provided by that operation."),
            ClientUnboundDstr.MailPendingConfirmation => ("CMD94/95/134/324 consumer 02E194A0", "The client resource itself is a placeholder; localized wording and operation-specific binding are incomplete. A server ACK cannot turn it into translated text."),
            ClientUnboundDstr.ShopUntranslated42770 => ("CMD21 consumer 0236B0A0", "The client resource itself is a placeholder; localized wording and operation-specific binding are incomplete. A server ACK cannot turn it into translated text."),
            ClientUnboundDstr.RepairSucceeded => ("CMD23 consumer 02355D60", "Requires RepairPackets.Ack with the actual committed repair receipt, balance and source position; do not fabricate success."),
            ClientUnboundDstr.PartyStreetPvpPeacePartyRestriction or
                ClientUnboundDstr.RaidPreviouslyKicked or
                ClientUnboundDstr.RaidStartedPreviousDay or
                ClientUnboundDstr.PartyEventAreaUnavailable or
                ClientUnboundDstr.PartyCreateEvildomPartyFirst or
                ClientUnboundDstr.AccountTargetConfinedToSeriaRoom => ("CMD10 consumer 02363C00; CMD11 consumer 02354540", "Native operation-specific mode/error routing is not yet bound by this typed interface. A dstr index is not a wire error; inspect the listed consumer before encoding."),
            ClientUnboundDstr.InventoryTitleAnimationCannotBeReplaced => ("CMD19 consumer 02372610", "Native operation-specific mode/error routing is not yet bound by this typed interface. A dstr index is not a wire error; inspect the listed consumer before encoding."),
            ClientUnboundDstr.InventoryTitleAnimationReplacementRemoved => ("CMD19 consumer 02372610", "Depends on the title-animation move result and target item; no isolated success notification is verified."),
            ClientUnboundDstr.CharacterUntranslated49516 => ("CMD4 consumer 023415C0; CMD10 consumer 02363C00", "The client resource itself is a placeholder; localized wording and operation-specific binding are incomplete. A server ACK cannot turn it into translated text."),
            ClientUnboundDstr.CharacterUntranslated49525 => ("CMD4 consumer 023415C0", "The client resource itself is a placeholder; localized wording and operation-specific binding are incomplete. A server ACK cannot turn it into translated text."),
            ClientUnboundDstr.AuctionListingSucceeded => ("client helper consumer 03729310", "Requires the real auction result, panel mode and committed item/gold changes. Failure widths are now native u16; old domain error choices still do not prove the displayed reason."),
            ClientUnboundDstr.AuctionListingCancelled => ("client helper consumer 037294E0; client helper consumer 026FD570", "Requires the real auction result, panel mode and committed item/gold changes. Failure widths are now native u16; old domain error choices still do not prove the displayed reason."),
            ClientUnboundDstr.AuctionBiddingSucceeded => ("client helper consumer 026FD5C0", "Requires the real auction result, panel mode and committed item/gold changes. Failure widths are now native u16; old domain error choices still do not prove the displayed reason."),
            ClientUnboundDstr.QuestReaperMoodGranted => ("CMD34 consumer 02376BF0", "Quest-specific success text depends on the real finished quest and client state, not a standalone notification ID."),
            ClientUnboundDstr.ChatMessageIntervalRequired or
                ClientUnboundDstr.ChatMegaphoneRequiresLevel86Character => ("CMD17 consumer 0234D280", "Native operation-specific mode/error routing is not yet bound by this typed interface. A dstr index is not a wire error; inspect the listed consumer before encoding."),
            ClientUnboundDstr.PartyTargetCannotAcceptMentorship => ("CMD10 consumer 02363C00", "Mentorship request/state ownership is not integrated in this server. The peer mode, qualifications and native response path still need an actual implementation."),
            ClientUnboundDstr.PartyMentorFriendListFull => ("CMD10 consumer 02363C00; CMD11 consumer 02354540", "Mentorship request/state ownership is not integrated in this server. The peer mode, qualifications and native response path still need an actual implementation."),
            _ => throw new ArgumentOutOfRangeException(nameof(prompt), prompt, "Unknown client dstr reference.")
        };
        return $"DSTR {(uint)prompt} ({prompt}); {source}; {problem}";
    }
}
