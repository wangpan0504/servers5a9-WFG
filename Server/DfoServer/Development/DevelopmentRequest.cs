using System.Collections.Immutable;
using System.Globalization;
using DfoServer.Game.Characters;

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
    IndexStatus
}

internal sealed record DevelopmentRequest(DevelopmentAction Action, string Account, string Name, int Primary, int Secondary)
{
    public DevelopmentEventRequest? Event { get; init; }
    // 仅 item 使用：普通装备强化 0–31，其他种类须为 0，由统一创建策略复验。
    public int Upgrade { get; init; }
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
        ["grant"] = (DevelopmentAction.Grant, true, 5, 7),
        ["set-grant"] = (DevelopmentAction.SetGrant, true, 4, 5),
        ["item-amplify"] = (DevelopmentAction.ItemAmplify, true, 3, 3),
        ["item-edit"] = (DevelopmentAction.ItemEdit, true, 2, 8),
        ["set-members"] = (DevelopmentAction.SetMembers, true, 1, 1),
        ["item-delete"] = (DevelopmentAction.ItemDelete, true, 1, 1),
        ["item-clear"] = (DevelopmentAction.ItemClear, true, 1, 1),
        ["mails"] = (DevelopmentAction.Mails, true, 0, 0),
        ["mail-delete"] = (DevelopmentAction.MailDelete, true, 1, 1),
        ["quest-search"] = (DevelopmentAction.QuestSearch, true, 0, 32),
        ["quest-chain"] = (DevelopmentAction.QuestChain, true, 1, 1),
        ["quest-batch"] = (DevelopmentAction.QuestBatch, true, 1, 1),
        ["points"] = (DevelopmentAction.Points, true, 0, 0),
        ["rename"] = (DevelopmentAction.Rename, true, 1, 1),
        ["char-delete"] = (DevelopmentAction.CharDelete, true, 1, 1),
        ["stats"] = (DevelopmentAction.Stats, true, 0, 0),
        ["title-list"] = (DevelopmentAction.TitleList, true, 0, 1),
        ["title-complete"] = (DevelopmentAction.TitleComplete, true, 1, 1),
        ["skill-list"] = (DevelopmentAction.SkillList, true, 0, 0),
        ["exp-table"] = (DevelopmentAction.ExpTable, false, 0, 0),
        ["item-stat-counts"] = (DevelopmentAction.ItemStatCounts, false, 0, 0),
        ["index-status"] = (DevelopmentAction.IndexStatus, false, 0, 0)
    };
    public const string Usage = "用法：list | show <角色> | quests <角色> | skills <角色> | level <角色> <1-95>（保留技能） | story <角色> <0-95> | grow-options <角色> | grow <角色> <分支0-5> <觉醒0-2>（兼容单个grow值0-127） | quest-add <角色> <任务> | quest-del <角色> <任务> | quest-clear <角色> | skill <角色> <技能> <0-255> | sp <角色> <额外SP 0-50000> | tp <角色> <额外TP 0-50000> | contract <角色> <剩余秒数> | black-diamond <角色> <天数0-3650> | gold-card <角色> <天数0-3650> | event status/on/off/auto <活动ID> | event schedule <活动ID> <开始ISO8601> <结束ISO8601> | gold <角色> <0-2147483647> | item <角色> <道具ID> <数量> [强化0-31] | item-info <道具ID> | items <角色> | wallet <角色> | currency <角色> <revive|win|cera|token|happy|rental|*-cube|*-soul> <set|add|sub> <数量> | item-categories | item-filter <偏移> <最低等级> <最高等级> <稀有度|*> <分类|*> <词条|*> <期限*|none|rel|abs|any> <技能职业*|all|职业> [关键字]（词条键空格用_） | item-search <偏移> <最低等级> <最高等级> <稀有度|*> <分类|*> [关键字] | grant <角色> <道具ID> <数量> <强化> <锻造> <mail|direct> [amp:<类型1-4>:<异次元值>] [seal:open（仅[sealing]封装装备，直接发为已解封）] | set-grant <角色> <套装编号> <强化> <锻造> <mail|direct> [amp:<类型>:<值>] | item-amplify <角色> <容器:位置:UID> <amp:<类型0-4>:<值>> <等级0-31> | item-delete <角色> <容器:位置:UID,…> | item-clear <角色> <容器> | mails <角色> | mail-delete <角色> <邮件ID,…|all> | quest-search <角色> [关键字] | quest-chain <角色> <任务> | quest-batch <角色> <任务,…> | rename <角色> <新名>（角色须在选角界面） | char-delete <角色> <确认角色名>（永久删除，不可恢复，须在选角界面） | title-list <角色> [general|specific|pvp|despair|event|关键字]（称号簿成就，只读） | title-complete <角色> <成就ID,…|all|book|book:分类>（直接完成成就，发称号与奖励；须在选角界面） | stats <角色>（服务端基础属性，只读） | skill-list <角色>（技能名称、等级与SP/TP，只读） | exp-table（等级累计经验表，只读） | points <角色>（额外与当前 SP/TP，只读） | cash-shop-expiration [on|off]（忽略绝对日期，默认on） | cash-shop-usable-period [on|off]（忽略相对期限，默认off） | pc-room [on|off]（全局网吧身份，默认off，不授予黑钻） | infinite-fatigue [on|off]（显示满疲劳且不扣点，默认on）。调试开关仅在线。其他账号在命令前加 account <账号>。";
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
            "cash-shop-expiration" when tokens.Length == i + 1 => new(DevelopmentAction.CashShopExpiration, account, "", -1, 0),
            "cash-shop-expiration" when tokens.Length == i + 2 && tokens[i + 1] is "on" or "off" =>
                new(DevelopmentAction.CashShopExpiration, account, "", tokens[i + 1] == "on" ? 1 : 0, 0),
            "cash-shop-usable-period" when tokens.Length == i + 1 => new(DevelopmentAction.CashShopUsablePeriod, account, "", -1, 0),
            "cash-shop-usable-period" when tokens.Length == i + 2 && tokens[i + 1] is "on" or "off" =>
                new(DevelopmentAction.CashShopUsablePeriod, account, "", tokens[i + 1] == "on" ? 1 : 0, 0),
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
        if (Action is DevelopmentAction.CashShopExpiration or DevelopmentAction.CashShopUsablePeriod or DevelopmentAction.InfiniteFatigue or DevelopmentAction.PcRoom)
        {
            if (Primary is < -1 or > 1 || Name.Length != 0 || Secondary != 0)
                throw new InvalidDataException("调试开关无效");
            return;
        }
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
        if (Action == DevelopmentAction.List)
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
        Action is DevelopmentAction.CashShopExpiration or DevelopmentAction.CashShopUsablePeriod or DevelopmentAction.InfiniteFatigue or DevelopmentAction.PcRoom ? Primary >= 0 :
        Action is DevelopmentAction.Currency or DevelopmentAction.Grant or DevelopmentAction.SetGrant or DevelopmentAction.ItemDelete or DevelopmentAction.ItemClear or DevelopmentAction.MailDelete or DevelopmentAction.QuestChain or DevelopmentAction.QuestBatch or DevelopmentAction.ItemAmplify or DevelopmentAction.ItemEdit or DevelopmentAction.Rename or DevelopmentAction.CharDelete or DevelopmentAction.TitleComplete ||
        Action is DevelopmentAction.Level or DevelopmentAction.Story or DevelopmentAction.Grow or DevelopmentAction.QuestAdd or DevelopmentAction.QuestRemove or DevelopmentAction.QuestClear or DevelopmentAction.Skill or DevelopmentAction.Gold or DevelopmentAction.Item or DevelopmentAction.Sp or DevelopmentAction.Tp or DevelopmentAction.Contract or DevelopmentAction.BlackDiamond or DevelopmentAction.GoldCard;

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
