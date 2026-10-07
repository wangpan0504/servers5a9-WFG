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
    QuestAdd,
    QuestRemove,
    QuestClear,
    Skill,
    Gold,
    Item
}

internal sealed record DevelopmentRequest(DevelopmentAction Action, string Account, string Name, int Primary, int Secondary)
{
    public const string Usage = "用法：list | show <角色> | quests <角色> | skills <角色> | level <角色> <1-95> | story <角色> <0-95> | grow <角色> <0-127> | quest-add <角色> <任务> | quest-del <角色> <任务> | quest-clear <角色> | skill <角色> <技能> <0-255> | gold <角色> <0-2147483647> | item <角色> <道具ID> <数量>。其他账号在命令前加 account <账号>。";
    public static DevelopmentRequest Parse(string line, string defaultAccount)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 512)
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
        DevelopmentRequest request = op switch
        {
            "list" when tokens.Length == i + 1 => new(DevelopmentAction.List, account, "", 0, 0),
            "show" when tokens.Length == i + 2 => Named(DevelopmentAction.Show, account, tokens[i + 1]),
            "quests" when tokens.Length == i + 2 => Named(DevelopmentAction.Quests, account, tokens[i + 1]),
            "skills" when tokens.Length == i + 2 => Named(DevelopmentAction.Skills, account, tokens[i + 1]),
            "quest-clear" when tokens.Length == i + 2 => Named(DevelopmentAction.QuestClear, account, tokens[i + 1]),
            "level" when tokens.Length == i + 3 => Numbered(DevelopmentAction.Level, account, tokens[i + 1], tokens[i + 2]),
            "story" when tokens.Length == i + 3 => Numbered(DevelopmentAction.Story, account, tokens[i + 1], tokens[i + 2]),
            "grow" when tokens.Length == i + 3 => Numbered(DevelopmentAction.Grow, account, tokens[i + 1], tokens[i + 2]),
            "quest-add" when tokens.Length == i + 3 => Numbered(DevelopmentAction.QuestAdd, account, tokens[i + 1], tokens[i + 2]),
            "quest-del" when tokens.Length == i + 3 => Numbered(DevelopmentAction.QuestRemove, account, tokens[i + 1], tokens[i + 2]),
            "skill" when tokens.Length == i + 4 => Skilled(account, tokens[i + 1], tokens[i + 2], tokens[i + 3]),
            "gold" when tokens.Length == i + 3 => Numbered(DevelopmentAction.Gold, account, tokens[i + 1], tokens[i + 2]),
            "item" when tokens.Length == i + 4 => new(DevelopmentAction.Item, account, tokens[i + 1], Integer(tokens[i + 2]), Integer(tokens[i + 3])),
            _ => throw new InvalidDataException(Usage)
        };
        request.Validate();
        return request;
    }

    static DevelopmentRequest Named(DevelopmentAction action, string account, string name) => new(action, account, name, 0, 0);
    static DevelopmentRequest Numbered(DevelopmentAction action, string account, string name, string text) => new(action, account, name, Integer(text), 0);
    static DevelopmentRequest Skilled(string account, string name, string skill, string level) => new(DevelopmentAction.Skill, account, name, Integer(skill), Integer(level));
    static int Integer(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : throw new InvalidDataException(Usage);
    public void Validate()
    {
        if (Account.Length is < 1 or > 64 || Account.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')))
            throw new InvalidDataException("账号无效");
        if (Action == DevelopmentAction.List)
            return;
        if (!CharacterName.IsValid(Name))
            throw new InvalidDataException("角色名无效");
        if (Action == DevelopmentAction.Gold && Primary < 0 || Action == DevelopmentAction.Item && (Primary <= 0 || Secondary <= 0))
            throw new InvalidDataException("金币、道具ID或数量无效");
        if (Writes && Action is not (DevelopmentAction.Gold or DevelopmentAction.Item))
            ToProgressEdit().Validate();
    }

    public bool Writes => Action is DevelopmentAction.Level or DevelopmentAction.Story or DevelopmentAction.Grow or DevelopmentAction.QuestAdd or DevelopmentAction.QuestRemove or DevelopmentAction.QuestClear or DevelopmentAction.Skill or DevelopmentAction.Gold or DevelopmentAction.Item;

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
            _ => throw new InvalidDataException("不是写入命令")
        };
        return new(field, Account, Name, Primary, Secondary);
    }
}
