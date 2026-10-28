using System.Collections.Immutable;
using System.Globalization;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;

namespace DfoServer.Development;

internal enum DevelopmentAction
{
    List,
    Show,
    Quests,
    Skills,
    Level,
    Story,
    Grow,
    GrowOptions,
    QuestAdd,
    QuestRemove,
    QuestClear,
    Skill,
    Gold,
    Item,
    Sp,
    Tp,
    Contract,
    BlackDiamond,
    GoldCard,
    Event,
    CashShopExpiration,
    InfiniteFatigue,
    PcRoom,
    SummonWangFugui,
    SummonGuangbing,
    CashShopUsablePeriod,
    Items,
    ItemInfo,
    Wallet,
    Currency,
    ItemCategories,
    ItemSearch,
    ItemFilter,
    Grant,
    SetGrant,
    ItemDelete,
    ItemClear,
    Mails,
    MailDelete,
    QuestSearch,
    QuestChain,
    QuestBatch,
    ItemAmplify,
    ItemEdit,
    SetMembers,
    Points,
    Rename,
    CharDelete,
    TitleList,
    Stats,
    TitleComplete,
    SkillList,
    ExpTable,
    ItemStatCounts,
    IndexStatus,
    ChatInject,
    InjectRaw,
    Notice,
    EquipSlots,
    Appearance,
    ExpertJob,
    WeeklyEntryReset,
    EquipmentScore,
    CharacterScore,
    LukeRewardList,
    LukeRewardInspect,
    LukeRewardClaim,
    CharacterScoreNotice
}

internal sealed record DevelopmentRequest(DevelopmentAction Action, string Account, string Name, int Primary, int Secondary)
{
    public DevelopmentEventRequest? Event { get; init; }
    // 仅 item 使用：普通装备强化 0–31，其他种类须为 0，由统一创建策略复验。
    public int Upgrade { get; init; }
    // chat-inject（诊断）：测试正文变体（L1/L2/L3，已转大写）与剩余整行文本（可含空格）；0x0C 正文带 CP936 文本。
    // notice（公告）：剩余整行文本；Name 为空表示 all（全服广播），否则是目标角色名。
    public string Variant { get; init; } = "";
    public string Text { get; init; } = "";
    // inject-raw（诊断）：原样发一帧 NOTI 的 opcode 与已解码正文（RawBody 可为空），命令行只给字节，不构造正文。
    public ushort RawOpcode { get; init; }
    public byte[] RawBody { get; init; } = [];
    // 扩展命令的其余参数，按命令在执行入口逐项严格解析。
    public ImmutableArray<string> Args { get; init; } = [];
    // 扩展命令：是否需要角色名，以及角色名之后参数个数的范围。
    static readonly Dictionary<string, (DevelopmentAction Action, bool Named, int Min, int Max)> Extended = new()
    {
        ["wallet"] = (DevelopmentAction.Wallet, true, 0, 0),
        ["currency"] = (DevelopmentAction.Currency, true, 3, 3),
        ["item-categories"] = (DevelopmentAction.ItemCategories, false, 0, 0),
        ["item-search"] = (DevelopmentAction.ItemSearch, false, 5, 32),
        ["item-filter"] = (DevelopmentAction.ItemFilter, false, 8, 32),
        // 5 个必填（模板、数量、强化、锻造、投递方式）+ 最多 6 个可选（amp/seal:open/ability/period:off/expire/quality）。
        ["grant"] = (DevelopmentAction.Grant, true, 5, 11),
        ["set-grant"] = (DevelopmentAction.SetGrant, true, 4, 5),
        ["item-amplify"] = (DevelopmentAction.ItemAmplify, true, 3, 3),
        ["item-edit"] = (DevelopmentAction.ItemEdit, true, 2, 8),
        ["set-members"] = (DevelopmentAction.SetMembers, true, 1, 1),
        ["item-delete"] = (DevelopmentAction.ItemDelete, true, 1, 1),
        ["item-clear"] = (DevelopmentAction.ItemClear, true, 1, 1),
        ["mails"] = (DevelopmentAction.Mails, true, 0, 0),
        ["mail-delete"] = (DevelopmentAction.MailDelete, true, 1, 2),
        ["quest-search"] = (DevelopmentAction.QuestSearch, true, 0, 32),
        ["quest-chain"] = (DevelopmentAction.QuestChain, true, 1, 1),
        ["quest-batch"] = (DevelopmentAction.QuestBatch, true, 1, 1),
        ["points"] = (DevelopmentAction.Points, true, 0, 0),
        ["rename"] = (DevelopmentAction.Rename, true, 1, 1),
        ["char-delete"] = (DevelopmentAction.CharDelete, true, 1, 1),
        ["stats"] = (DevelopmentAction.Stats, true, 0, 0),
        ["luke-reward-list"] = (DevelopmentAction.LukeRewardList, true, 1, 3),
        ["luke-reward-inspect"] = (DevelopmentAction.LukeRewardInspect, true, 5, 5),
        ["luke-reward-claim"] = (DevelopmentAction.LukeRewardClaim, true, 7, 7),
        ["equipment-score"] = (DevelopmentAction.EquipmentScore, true, 0, 0),
        ["character-score"] = (DevelopmentAction.CharacterScore, true, 0, 0),
        ["character-score-notice"] = (DevelopmentAction.CharacterScoreNotice, true, 0, 0),
        ["title-list"] = (DevelopmentAction.TitleList, true, 0, 1),
        ["title-complete"] = (DevelopmentAction.TitleComplete, true, 1, 1),
        ["skill-list"] = (DevelopmentAction.SkillList, true, 0, 0),
        ["exp-table"] = (DevelopmentAction.ExpTable, false, 0, 0),
        ["item-stat-counts"] = (DevelopmentAction.ItemStatCounts, false, 0, 0),
        ["index-status"] = (DevelopmentAction.IndexStatus, false, 0, 0),
        ["equip-slots"] = (DevelopmentAction.EquipSlots, true, 0, 1),
        ["appearance"] = (DevelopmentAction.Appearance, true, 0, 0),
        ["expert-job"] = (DevelopmentAction.ExpertJob, true, 0, 2)
    };
    public const string ChatInjectUsage = "用法：chat-inject <连接号> <L1|L2|L3|P3E|AAS|AAS8|AASU> <文本>（只对已在线连接有效；文本可含空格）";
    /// <summary><c>inject-raw</c> 正文上限（字节）：足以放下聊天/弹框任意变体，又不至于把一行 GM 命令撑爆。</summary>
    public const int RawInjectBodyMaxBytes = 4096;
    /// <summary><c>inject-raw</c> 命令行用法；正文上限与 opcode 上限在 <see cref="ParseHexBody"/>、<see cref="ParseOpcode"/> 各自报具体原因。</summary>
    public const string InjectRawUsage = "用法：inject-raw <连接号> <opcode十进制|0x十六进制> <body-hex|->（只对已在线连接有效；body-hex 可含空格，'-' 表示空 body，偶数长度且不超过 4096 字节；opcode 不超过 0xFFFF）";
    public const string NoticeUsage = "用法：notice <all|角色名> <文本>（all = 全服在线广播，角色名 = 只发给该角色当前连接；文本可含空格）";
    public const string Usage = "用法：list | show <角色> | quests <角色> | skills <角色> | level <角色> <1-95>（保留技能） | story <角色> <0-95> | grow-options <角色> | grow <角色> <分支0-5> <觉醒0-2>（兼容单个grow值0-127） | quest-add <角色> <任务> | quest-del <角色> <任务> | quest-clear <角色> | skill <角色> <技能> <0-255> | sp <角色> <额外SP 0-50000> | tp <角色> <额外TP 0-50000> | contract <角色> <剩余秒数> | black-diamond <角色> <天数0-3650> | gold-card <角色> <天数0-3650> | event status/on/off/auto <活动ID> | event schedule <活动ID> <开始ISO8601> <结束ISO8601> | gold <角色> <0-2147483647> | item <角色> <道具ID> <数量> [强化0-31] | item-info <道具ID> | items <角色> | wallet <角色> | currency <角色> <revive|win|cera|token|happy|rental|*-cube|*-soul> <set|add|sub> <数量> | item-categories | item-filter <偏移> <最低等级> <最高等级> <稀有度|*> <分类|*> <词条|*> <期限*|none|rel|abs|any> <技能职业*|all|职业> [关键字]（词条键空格用_） | item-search <偏移> <最低等级> <最高等级> <稀有度|*> <分类|*> [关键字]（item-search/item-filter 关键字区可加 tags:<分类>[@e|@s],… kind:e|s sub:<武器子类>,… real:1 sort:id|-id|level|-level|rarity|-rarity|name） | grant <角色> <道具ID> <数量> <强化> <锻造> <mail|direct> [amp:<类型1-4>:<异次元值>] [seal:open（仅[sealing]封装装备，直接发为已解封）] [ability:<选项>（仅装扮属性）] [period:off（改为不过期）] [expire:<Unix秒>（指定到期时间）] [quality:<1-100>（仅普通装备品级，100 = 最上级）] | set-grant <角色> <套装编号> <强化> <锻造> <mail|direct> [amp:<类型>:<值>] | item-amplify <角色> <容器:位置:UID> <amp:<类型0-4>:<值>> <等级0-31> | item-delete <角色> <容器:位置:UID,…> | item-clear <角色> <容器> | mails <角色> | mail-delete <角色> <邮件ID,…|all> [force（不校验未领取/过期，连附件与金币一起删）] | quest-search <角色> [关键字] | quest-chain <角色> <任务> | quest-batch <角色> <任务,…> | rename <角色> <新名>（角色须在选角界面） | char-delete <角色> <确认角色名>（永久删除，不可恢复，须在选角界面） | title-list <角色> [general|specific|pvp|despair|event|关键字]（称号簿成就，只读） | title-complete <角色> <成就ID,…|all|book|book:分类>（直接完成成就，发称号与奖励；须在选角界面） | stats <角色>（服务端基础属性，只读） | appearance <角色>（纸娃娃外观来源：职业、0–12 号装备位物品与克隆外观、武器幻化，只读） | expert-job <角色> [类型1-4 等级1-11]（不带参数只读当前副职业与累计熟练度；带参数写入该等级的最低熟练度，等级上限按 PVF 阈值） | skill-list <角色>（技能名称、等级与SP/TP，只读） | exp-table（等级累计经验表，只读） | points <角色>（额外与当前 SP/TP，只读） | equip-slots <角色> [位掩码 1|2|16 的组合]（扩展装备栏：不带位掩码只读；带位掩码按位或开启辅助装备1/魔法石2/耳环16，只增不减，须在选角界面） | cash-shop-expiration [on|off]（忽略绝对日期，默认on） | cash-shop-usable-period [on|off]（忽略相对期限，默认off） | pc-room [on|off]（全局网吧身份，线上默认on，不授予黑钻） | infinite-fatigue [on|off]（显示满疲劳且不扣点，默认on） | summon-wangfugui [on|off]（实验性，需配套PVF的地图 [ai character] 20900 行；进图起始房间放出奶爸支援兵王富贵，默认off，进图异常先关掉再重新进图） | summon-guangbing [on|off]（实验性，需配套PVF的地图 [ai character] 5409 行；进图起始房间放出28号光兵，默认off，进图异常先关掉再重新进图）。调试开关仅在线。其他账号在命令前加 account <账号>。 | chat-inject <连接号> <L1|L2|L3|P3E|AAS|AAS8|AASU> <文本>（诊断工具：向指定在线连接注入一帧测试通知，核对收发路径与文本显示；L1/L2/L3 = NOTI 0x000C（正文 = [type][层 id][v][u32 CP936 字节数][CP936 文本]，长度 1..255，与正式投递同一编码）；P3E = NOTI 0x03E2 制裁弹窗，AAS/AAS8/AASU = NOTI 0x06AD 弹框（标题固定系统公告，标题/正文分别按 CP936/UTF-8/UTF-16LE 编码，超 24/600 字节截断）；文本可含空格） | inject-raw <连接号> <opcode十进制|0x十六进制> <body-hex|->（诊断工具：向指定在线连接原样发一帧 NOTI，服务端不解析、不校验、不重试，用于逐变体试长度字段/编码/结尾字节；body-hex 可含空格，'-' = 空 body，偶数长度 hex 且不超过 4096 字节；opcode 十进制或 0x 前缀，上限 0xFFFF） | notice <all|角色名> <文本>（正式公告：NOTI 0x06AD 弹框，UTF-8，标题固定系统公告，标题/正文超 24/600 字节按字符边界截断；all = 全服在线，角色名 = 该角色当前连接）";
    public static DevelopmentRequest Parse(string line, string defaultAccount)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 32768)
            throw new InvalidDataException(Usage);
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int i = 0;
        string account = defaultAccount;
        if (tokens[0] == "account")
        {
            if (tokens.Length < 3)
                throw new InvalidDataException(Usage);
            account = tokens[1];
            i = 2;
        }

        if (i >= tokens.Length)
            throw new InvalidDataException(Usage);
        string op = tokens[i];
        if (op == "luke-reward-list" && line.Any(char.IsControl))
            throw new InvalidDataException("历史资格查询不能含控制字符");
        if (op == "chat-inject")
        {
            // 按连接号注入，不接受 account 前缀；文本取剩余整行，保留内部空格。
            if (i != 0 || tokens.Length < 4 || !int.TryParse(tokens[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int connection) ||
                connection < 0)
                throw new InvalidDataException(ChatInjectUsage);
            string variant = tokens[i + 2].ToUpperInvariant();
            // 变体白名单只在 ChatProbes 维护一份，避免解析、校验与正文构造三处各写一套。
            if (!ChatProbes.TryParseVariant(variant, out _))
                throw new InvalidDataException(ChatInjectUsage);
            string text = Remainder(line, 3);
            if (text.Length == 0)
                throw new InvalidDataException(ChatInjectUsage);
            var inject = new DevelopmentRequest(DevelopmentAction.ChatInject, account, "", connection, 0)
                { Variant = variant, Text = text };
            inject.Validate();
            return inject;
        }
        if (op == "inject-raw")
        {
            // 按连接号注入，不接受 account 前缀；正文 hex 取剩余整行，允许内含空格（便于分段书写长包体）。
            if (i != 0 || tokens.Length < 4 || !int.TryParse(tokens[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int connection) ||
                connection < 0)
                throw new InvalidDataException(InjectRawUsage);
            var raw = new DevelopmentRequest(DevelopmentAction.InjectRaw, account, "", connection, 0)
                { RawOpcode = ParseOpcode(tokens[i + 2]), RawBody = ParseHexBody(Remainder(line, 3)) };
            raw.Validate();
            return raw;
        }
        if (op == "notice")
        {
            // 公告按全服/角色名定位，不接受 account 前缀；文本取剩余整行，保留内部空格。
            if (i != 0 || tokens.Length < 3)
                throw new InvalidDataException(NoticeUsage);
            string target = string.Equals(tokens[i + 1], "all", StringComparison.OrdinalIgnoreCase) ? "" : tokens[i + 1];
            string text = Remainder(line, i + 2);
            if (text.Length == 0)
                throw new InvalidDataException(NoticeUsage);
            var notice = new DevelopmentRequest(DevelopmentAction.Notice, account, target, 0, 0) { Text = text };
            notice.Validate();
            return notice;
        }
        if (op == "event")
        {
            if (i != 0) throw new InvalidDataException("活动是全服配置，不能使用account前缀");
            return new(DevelopmentAction.Event, account, "", 0, 0)
                { Event = DevelopmentEventRequest.Parse(tokens[1..]) };
        }
        if (Extended.TryGetValue(op, out var extended))
        {
            int start = i + 1 + (extended.Named ? 1 : 0);
            if (tokens.Length < start || tokens.Length - start < extended.Min || tokens.Length - start > extended.Max)
                throw new InvalidDataException(Usage);
            var parsed = new DevelopmentRequest(extended.Action, account, extended.Named ? tokens[i + 1] : "", 0, 0) { Args = [.. tokens[start..]] };
            parsed.Validate();
            return parsed;
        }
        DevelopmentRequest request = op switch
        {
            "infinite-fatigue" when tokens.Length == i + 1 => new(DevelopmentAction.InfiniteFatigue, account, "", -1, 0),
            "infinite-fatigue" when tokens.Length == i + 2 && tokens[i + 1] is "on" or "off" =>
                new(DevelopmentAction.InfiniteFatigue, account, "", tokens[i + 1] == "on" ? 1 : 0, 0),
            "pc-room" when tokens.Length == i + 1 => new(DevelopmentAction.PcRoom, account, "", -1, 0),
            "pc-room" when tokens.Length == i + 2 && tokens[i + 1] is "on" or "off" =>
                new(DevelopmentAction.PcRoom, account, "", tokens[i + 1] == "on" ? 1 : 0, 0),
            "summon-wangfugui" when tokens.Length == i + 1 => new(DevelopmentAction.SummonWangFugui, account, "", -1, 0),
            "summon-wangfugui" when tokens.Length == i + 2 && tokens[i + 1] is "on" or "off" =>
                new(DevelopmentAction.SummonWangFugui, account, "", tokens[i + 1] == "on" ? 1 : 0, 0),
            "summon-guangbing" when tokens.Length == i + 1 => new(DevelopmentAction.SummonGuangbing, account, "", -1, 0),
            "summon-guangbing" when tokens.Length == i + 2 && tokens[i + 1] is "on" or "off" =>
                new(DevelopmentAction.SummonGuangbing, account, "", tokens[i + 1] == "on" ? 1 : 0, 0),
            "cash-shop-expiration" when tokens.Length == i + 1 => new(DevelopmentAction.CashShopExpiration, account, "", -1, 0),
            "cash-shop-expiration" when tokens.Length == i + 2 && tokens[i + 1] is "on" or "off" =>
                new(DevelopmentAction.CashShopExpiration, account, "", tokens[i + 1] == "on" ? 1 : 0, 0),
            "cash-shop-usable-period" when tokens.Length == i + 1 => new(DevelopmentAction.CashShopUsablePeriod, account, "", -1, 0),
            "cash-shop-usable-period" when tokens.Length == i + 2 && tokens[i + 1] is "on" or "off" =>
                new(DevelopmentAction.CashShopUsablePeriod, account, "", tokens[i + 1] == "on" ? 1 : 0, 0),
            "weekly-entry-reset" when tokens.Length == i + 1 => new(DevelopmentAction.WeeklyEntryReset, account, "", 0, 0),
            "list" when tokens.Length == i + 1 => new(DevelopmentAction.List, account, "", 0, 0),
            "show" when tokens.Length == i + 2 => Named(DevelopmentAction.Show, account, tokens[i + 1]),
            "quests" when tokens.Length == i + 2 => Named(DevelopmentAction.Quests, account, tokens[i + 1]),
            "skills" when tokens.Length == i + 2 => Named(DevelopmentAction.Skills, account, tokens[i + 1]),
            "grow-options" when tokens.Length == i + 2 => Named(DevelopmentAction.GrowOptions, account, tokens[i + 1]),
            "quest-clear" when tokens.Length == i + 2 => Named(DevelopmentAction.QuestClear, account, tokens[i + 1]),
            "level" when tokens.Length == i + 3 => Numbered(DevelopmentAction.Level, account, tokens[i + 1], tokens[i + 2]),
            "story" when tokens.Length == i + 3 => Numbered(DevelopmentAction.Story, account, tokens[i + 1], tokens[i + 2]),
            "grow" when tokens.Length == i + 3 => Numbered(DevelopmentAction.Grow, account, tokens[i + 1], tokens[i + 2]),
            "grow" when tokens.Length == i + 4 => Growth(account, tokens[i + 1], tokens[i + 2], tokens[i + 3]),
            "sp" when tokens.Length == i + 3 => Numbered(DevelopmentAction.Sp, account, tokens[i + 1], tokens[i + 2]),
            "tp" when tokens.Length == i + 3 => Numbered(DevelopmentAction.Tp, account, tokens[i + 1], tokens[i + 2]),
            "contract" when tokens.Length == i + 3 => Numbered(DevelopmentAction.Contract, account, tokens[i + 1], tokens[i + 2]),
            "black-diamond" when tokens.Length == i+3 => Numbered(DevelopmentAction.BlackDiamond,account,tokens[i+1],tokens[i+2]),
            "gold-card" when tokens.Length == i+3 => Numbered(DevelopmentAction.GoldCard,account,tokens[i+1],tokens[i+2]),
            "quest-add" when tokens.Length == i + 3 => Numbered(DevelopmentAction.QuestAdd, account, tokens[i + 1], tokens[i + 2]),
            "quest-del" when tokens.Length == i + 3 => Numbered(DevelopmentAction.QuestRemove, account, tokens[i + 1], tokens[i + 2]),
            "skill" when tokens.Length == i + 4 => Skilled(account, tokens[i + 1], tokens[i + 2], tokens[i + 3]),
            "gold" when tokens.Length == i + 3 => Numbered(DevelopmentAction.Gold, account, tokens[i + 1], tokens[i + 2]),
            "items" when tokens.Length == i + 2 => Named(DevelopmentAction.Items, account, tokens[i + 1]),
            "item-info" when tokens.Length == i + 2 => new(DevelopmentAction.ItemInfo, account, "", Integer(tokens[i + 1]), 0),
            "item" when tokens.Length == i + 5 => new(DevelopmentAction.Item, account, tokens[i + 1], Integer(tokens[i + 2]), Integer(tokens[i + 3]))
                { Upgrade = Integer(tokens[i + 4]) },
            "item" when tokens.Length == i + 4 => new(DevelopmentAction.Item, account, tokens[i + 1], Integer(tokens[i + 2]), Integer(tokens[i + 3])),
            _ => throw new InvalidDataException(Usage)
        };
        request.Validate();
        return request;
    }

    static DevelopmentRequest Named(DevelopmentAction action, string account, string name) => new(action, account, name, 0, 0);
    static DevelopmentRequest Numbered(DevelopmentAction action, string account, string name, string text) => new(action, account, name, Integer(text), 0);
    static DevelopmentRequest Skilled(string account, string name, string skill, string level) => new(DevelopmentAction.Skill, account, name, Integer(skill), Integer(level));
    static DevelopmentRequest Growth(string account, string name, string first, string second)
    {
        int branch = Integer(first), awakening = Integer(second);
        if (branch > 5 || awakening > 2) throw new InvalidDataException("转职范围0–5，觉醒范围0–2");
        return new(DevelopmentAction.Grow, account, name, branch | awakening << 4, 0);
    }
    static int Integer(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : throw new InvalidDataException(Usage);

    /// <summary>
    /// <c>inject-raw</c> 的 opcode：十进制，或带 <c>0x</c> 前缀的十六进制（大小写不敏感），上限 <c>0xFFFF</c>。
    /// 只接受裸数字：正负号、空白、多余字符一律拒绝，避免把相邻参数误当成 opcode。
    /// </summary>
    static ushort ParseOpcode(string text)
    {
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string digits = hex ? text[2..] : text;
        bool invalid = hex ? digits.Any(static c => !IsHexDigit(c)) : digits.Any(static c => c is < '0' or > '9');
        const string message = "inject-raw opcode 必须是 0–65535 的十进制或 0x 十六进制";
        if (digits.Length == 0 || invalid)
            throw new InvalidDataException(message);
        // AllowHexSpecifier 不接受前缀，这里已经剥掉 0x；十进制分支走 None，拒绝符号与空白。
        if (!uint.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out uint opcode) ||
            opcode > ushort.MaxValue)
            throw new InvalidDataException(message);
        return (ushort)opcode;
    }

    /// <summary>
    /// <c>inject-raw</c> 的正文：剔除空格后按十六进制解码，<c>-</c> 表示空 body。
    /// 奇数长度、非 0-9a-f 字符和超过 <see cref="RawInjectBodyMaxBytes"/> 都直接报具体原因，不做容错猜测。
    /// </summary>
    static byte[] ParseHexBody(string text)
    {
        string hex = text.Replace(" ", "");
        if (hex == "-")
            return [];
        if ((hex.Length & 1) != 0)
            throw new InvalidDataException("inject-raw 正文 hex 长度必须为偶数");
        if (hex.Any(static c => !IsHexDigit(c)))
            throw new InvalidDataException("inject-raw 正文 hex 只能包含 0-9a-f");
        if (hex.Length / 2 > RawInjectBodyMaxBytes)
            throw new InvalidDataException($"inject-raw 正文上限 {RawInjectBodyMaxBytes} 字节");
        return Convert.FromHexString(hex);
    }

    static bool IsHexDigit(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    // 去掉前 skip 个空格分隔的 token 后返回剩余整行（首尾空格去掉，保留内部空格）。
    static string Remainder(string line, int skip)
    {
        int at = 0;
        for (int token = 0; token < skip; token++)
        {
            while (at < line.Length && line[at] == ' ') at++;
            while (at < line.Length && line[at] != ' ') at++;
        }
        while (at < line.Length && line[at] == ' ') at++;
        return line[at..].TrimEnd(' ');
    }
    public void Validate()
    {
        if (Action == DevelopmentAction.Event)
        {
            if (Event is null) throw new InvalidDataException(DevelopmentEventRequest.Usage);
            Game.Events.ServerEventService.RequireRegistered(Event.EventId);
            return;
        }
        if (Account.Length is < 1 or > 64 || Account.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')))
            throw new InvalidDataException("账号无效");
        if (Action is DevelopmentAction.CashShopExpiration or DevelopmentAction.CashShopUsablePeriod or DevelopmentAction.InfiniteFatigue or
            DevelopmentAction.PcRoom or DevelopmentAction.SummonWangFugui or DevelopmentAction.SummonGuangbing)
        {
            if (Primary is < -1 or > 1 || Name.Length != 0 || Secondary != 0)
                throw new InvalidDataException("调试开关无效");
            return;
        }
        if (Action == DevelopmentAction.ChatInject)
        {
            if (Primary < 0 || Secondary != 0 || Name.Length != 0 || Upgrade != 0 ||
                !ChatProbes.TryParseVariant(Variant, out _) || Text.Length == 0)
                throw new InvalidDataException(ChatInjectUsage);
            return;
        }
        if (Action == DevelopmentAction.InjectRaw)
        {
            if (Primary < 0 || Secondary != 0 || Name.Length != 0 || Upgrade != 0 || RawBody.Length > RawInjectBodyMaxBytes)
                throw new InvalidDataException(InjectRawUsage);
            return;
        }
        if (Action == DevelopmentAction.Notice)
        {
            if (Primary != 0 || Secondary != 0 || Upgrade != 0 || Text.Length == 0 ||
                Name.Length != 0 && !CharacterName.IsValid(Name))
                throw new InvalidDataException(NoticeUsage);
            return;
        }
        if (Action == DevelopmentAction.WeeklyEntryReset)
            return;
        if (Action >= DevelopmentAction.Wallet)
        {
            // 单参数上限要能放下任务批量（最多 3000 个编号）和称号批量；整行仍受 32768 限制。
            if (Args.IsDefault || Args.Any(arg => arg.Length is 0 or > 24000) ||
                Action is not (DevelopmentAction.ItemCategories or DevelopmentAction.ItemSearch or DevelopmentAction.ItemFilter or DevelopmentAction.ExpTable or DevelopmentAction.ItemStatCounts or DevelopmentAction.IndexStatus) && !CharacterName.IsValid(Name))
                throw new InvalidDataException("角色名或参数无效");
            return;
        }
        if (Upgrade is < 0 or > 31 || Upgrade != 0 && Action != DevelopmentAction.Item)
            throw new InvalidDataException("强化范围0–31，且只用于道具发放");
        if (Action == DevelopmentAction.ItemInfo)
        {
            if (Name.Length != 0 || Primary <= 0 || Secondary != 0)
                throw new InvalidDataException("道具ID无效");
            return;
        }
        if (Action == DevelopmentAction.List || Action == DevelopmentAction.WeeklyEntryReset)
            return;
        if (!CharacterName.IsValid(Name))
            throw new InvalidDataException("角色名无效");
        if (Action == DevelopmentAction.Gold && Primary < 0 || Action == DevelopmentAction.Item && (Primary <= 0 || Secondary <= 0))
            throw new InvalidDataException("金币、道具ID或数量无效");
        if (Action is DevelopmentAction.BlackDiamond or DevelopmentAction.GoldCard && Primary is < 0 or > 3650)
            throw new InvalidDataException("会员天数范围0–3650，0表示关闭该项会员");
        if (Writes && Action is not (DevelopmentAction.Gold or DevelopmentAction.Item or DevelopmentAction.BlackDiamond or DevelopmentAction.GoldCard))
            ToProgressEdit().Validate();
    }

    public bool Writes => Action == DevelopmentAction.Event ? Event?.Operation != "status" :
        Action is DevelopmentAction.CashShopExpiration or DevelopmentAction.CashShopUsablePeriod or DevelopmentAction.InfiniteFatigue or DevelopmentAction.PcRoom or DevelopmentAction.SummonWangFugui or DevelopmentAction.SummonGuangbing ? Primary >= 0 :
        Action is DevelopmentAction.LukeRewardClaim or DevelopmentAction.Currency or DevelopmentAction.Grant or DevelopmentAction.SetGrant or DevelopmentAction.ItemDelete or DevelopmentAction.ItemClear or DevelopmentAction.MailDelete or DevelopmentAction.QuestChain or DevelopmentAction.QuestBatch or DevelopmentAction.ItemAmplify or DevelopmentAction.ItemEdit or DevelopmentAction.Rename or DevelopmentAction.CharDelete or DevelopmentAction.TitleComplete ||
        Action == DevelopmentAction.EquipSlots && Args.Length == 1 ||
        Action == DevelopmentAction.ExpertJob && Args.Length == 2 ||
        Action is DevelopmentAction.Level or DevelopmentAction.Story or DevelopmentAction.Grow or DevelopmentAction.QuestAdd or DevelopmentAction.QuestRemove or DevelopmentAction.QuestClear or DevelopmentAction.Skill or DevelopmentAction.Gold or DevelopmentAction.Item or DevelopmentAction.Sp or DevelopmentAction.Tp or DevelopmentAction.Contract or DevelopmentAction.BlackDiamond or DevelopmentAction.GoldCard or DevelopmentAction.WeeklyEntryReset;

    public CharacterProgressEdit ToProgressEdit()
    {
        var field = Action switch
        {
            DevelopmentAction.Level => CharacterProgressField.Level,
            DevelopmentAction.Story => CharacterProgressField.Story,
            DevelopmentAction.Grow => CharacterProgressField.Grow,
            DevelopmentAction.QuestAdd => CharacterProgressField.QuestAdd,
            DevelopmentAction.QuestRemove => CharacterProgressField.QuestRemove,
            DevelopmentAction.QuestClear => CharacterProgressField.QuestClear,
            DevelopmentAction.Skill => CharacterProgressField.Skill,
            DevelopmentAction.Sp => CharacterProgressField.BonusSp,
            DevelopmentAction.Tp => CharacterProgressField.BonusTp,
            DevelopmentAction.Contract => CharacterProgressField.ExpertContract,
            _ => throw new InvalidDataException("不是写入命令")
        };
        return new(field, Account, Name, Primary, Secondary);
    }
}
