using System.Collections.Immutable;

namespace DfoServer.GameWorld.Quests;

internal enum QuestParameterState { Parsed, Invalid, Unconfirmed }
internal enum QuestDungeonSelectorKind { Any, Dungeon, Recommended, Area, FilteredArea }
internal readonly record struct QuestDungeonSelector(QuestDungeonSelectorKind Kind, int Id, int Difficulty);
internal readonly record struct QuestHuntObjective(QuestDungeonSelector Dungeon, int Enemy, int EnemyType, int Count);
internal readonly record struct QuestValuePair(int Key, int Value);
internal enum QuestComparison { Equal, AtLeast, AtMost, Greater, Less }
internal readonly record struct QuestNestedCheck(string Kind, string Name, int Id, int Threshold, QuestComparison Comparison);
internal sealed record QuestNestedObjective(int Dungeon, int Difficulty, int TargetType, int Target,
    ImmutableArray<QuestValuePair> Targets, ImmutableArray<QuestNestedCheck> Checks);

// 参数模型不授予进度；玩法缺失与原码未闭合不冒充格式错误。
internal sealed record QuestTargetParameters(QuestParameterState State, string Reason = "")
{
    public ImmutableArray<QuestDungeonSelector> Dungeons { get; init; } = [];
    public ImmutableArray<QuestHuntObjective> Hunts { get; init; } = [];
    public ImmutableArray<QuestValuePair> Pairs { get; init; } = [];
    public ImmutableArray<QuestNestedObjective> Nested { get; init; } = [];
    public ImmutableArray<int> Additional { get; init; } = [];
    public ImmutableArray<int> DungeonLimits { get; init; } = [];
    public ImmutableArray<string> Strings { get; init; } = [];
    public uint Initial { get; init; } = 1;
}

internal static class QuestTargetSchema
{
    public static bool IsTargetField(StoryQuest quest, QuestField field)
    {
        if (field.Name is "string data" or "check count" or "limit dungeon index" or "clear a dungeon sub int data" or "light int data" or "first int data")
            return field.Children.Length == 0 && Read(quest).State == QuestParameterState.Parsed &&
                (field.Name == "string data" ? field.Values.All(value => value.Type == 6 && value.Text is not null) :
                    field.Values.All(value => value.Type == 0) && (field.Name != "check count" || field.Values.Length <= 3 && field.Values.All(value => value.Value is >= 0 and <= 65535)));
        return field.Name == quest.Condition.Trim('[', ']') && quest.TargetKind is QuestTargetKind.EnemyActionCheck or QuestTargetKind.DamageByEnemy or
            QuestTargetKind.CheckActiveStatus or QuestTargetKind.CheckControl or QuestTargetKind.CheckMyTeamDeadCount or QuestTargetKind.EnemyKillOrder or QuestTargetKind.EnemyFewKill &&
            Read(quest).State == QuestParameterState.Parsed;
    }
    static QuestTargetParameters Invalid(string reason) => new(QuestParameterState.Invalid, reason);
    static QuestTargetParameters Pending(string reason) => new(QuestParameterState.Unconfirmed, reason);
    static QuestTargetParameters Parsed() => new(QuestParameterState.Parsed);

    public static QuestTargetParameters Read(StoryQuest quest)
    {
        if (quest.TargetRegistration is not { NativeType: not null } registration)
            return Pending("没有已确认的本版类型赋值");
        if (quest.Definition?.Values("light int data").Length > 0)
            return Pending("light int data需要匹配客户端当前变体开关，不能无条件替换或追加");
        if (quest.Definition?.Values("first int data").Length > 0)
            return Pending("first int data保留独立向量，其业务消费者尚未闭合");
        var data = quest.IntData;
        int sub = quest.SubType;
        QuestTargetParameters result;
        switch (quest.TargetKind)
        {
            case QuestTargetKind.Seeking:
            case QuestTargetKind.SeekAndMeetNpc:
            case QuestTargetKind.CheckItem:
                result = ReadSeeking(quest); break;
            case QuestTargetKind.HuntMonster:
            case QuestTargetKind.HuntEnemy:
                result = ReadHunt(quest); break;
            case QuestTargetKind.ConditionUnderClear:
            case QuestTargetKind.ClearDungeon:
                result = ReadClear(quest); break;
            case QuestTargetKind.ClearMap:
                result = data.Count > 0 && data[0] > 0 && (sub != 0 || data.Count > 1 && data.Skip(1).All(value => value > 0))
                    ? Parsed() : Invalid("清图需要地图ID；sub0还须声明保护目标"); break;
            case QuestTargetKind.MeetNpc:
                result = sub is -1 or 0 or 1 && data.Count is 1 or 2 && data[0] > 0 &&
                    (data.Count == 1 || data[1] is -1 or 0) ? Parsed() : Pending("会面参数超出已确认的NPC及附加窗口0"); break;
            case QuestTargetKind.PvpRank:
                result = data is [>= 0] ? Parsed() : Invalid("段位需要单一非负编号"); break;
            case QuestTargetKind.LevelUp:
                result = data is [> 0] ? Parsed() : Invalid("升级目标需要单一正等级"); break;
            case QuestTargetKind.UserLevel:
                result = data is [>= 0, >= 0] && data[0] <= data[1] ? Parsed() : Invalid("等级区间需要完整闭区间"); break;
            case QuestTargetKind.FirstGrow:
            case QuestTargetKind.QuestAccept:
            case QuestTargetKind.EpicProduction:
            case QuestTargetKind.PlayOnly:
            case QuestTargetKind.TurnGoldCard:
            case QuestTargetKind.AssaultCount:
                result = data.Count == 0 ? Parsed() : Pending("已确认事件不读取这些附加整数，需单独核对新增声明"); break;
            case QuestTargetKind.Awakening:
                result = data is [> 0] ? Parsed() : Invalid("觉醒事件需要明确阶段，不以空向量视为自动完成"); break;
            case QuestTargetKind.ClearQuest:
                result = data is [> 0 and < 30000] ? Parsed() : Invalid("清任务目标需要单一合法任务ID"); break;
            case QuestTargetKind.QuestClear:
            case QuestTargetKind.ClearMultiQuest:
                result = data.Count > 0 && data.All(value => value is > 0 and < 30000) ? Parsed() : Invalid("关系目标需要完整任务列表"); break;
            case QuestTargetKind.ClearQuestByGrade:
                result = data is [>= -1 and <= 31, > 0] ? Parsed() with { Initial = (uint)data[1] } : Invalid("按分类清任务需要分类及正次数"); break;
            case QuestTargetKind.UseItem:
            case QuestTargetKind.CustomQuest:
                result = ReadCountedPairs(data, sub); break;
            case QuestTargetKind.UseFortuneCoin:
                result = data.Count > 0 && data.Count % 2 == 0 && data.Where((_, at) => at % 2 == 0).All(value => value > 0) &&
                    data.Where((_, at) => at % 2 != 0).All(value => value is 0 or 1) ? Parsed() with { Pairs = Pairs(data, 0) } : Invalid("幸运币需要物品/布尔事件对"); break;
            case QuestTargetKind.GetScore:
                result = sub is -1 or 0 or 1 or 2 && data is [>= 0, >= 0] ? Parsed() : Invalid("评分需要指标、阈值及比较子型"); break;
            case QuestTargetKind.GetItemCheckIndex:
            case QuestTargetKind.EnterMap:
            case QuestTargetKind.UiClick:
            case QuestTargetKind.MeetSecretNpc:
                result = data.Count > 0 && data.All(value => value > 0) ? Parsed() : Invalid("事件选择列表为空或含非法编号"); break;
            case QuestTargetKind.MeetApc:
            case QuestTargetKind.MeetMonster:
            case QuestTargetKind.MeetPassiveObject:
                result = data is [> 0] or [> 0, -1] ? Parsed() : Pending("会面第二事件参数域尚未闭合"); break;
            case QuestTargetKind.CheckTime:
                result = data is [-1 or 1, >= -1 and <= 2, >= 0, >= -1 and <= 23] ? Parsed() : Pending("计时场景、身份或小时超出已确认范围"); break;
            case QuestTargetKind.CheckLife:
                result = sub is 0 or 1 && data.Count >= 2 && data[0] >= 0 ? Parsed() : Pending("生命事件的来源及参数尚无完整资源对拍"); break;
            case QuestTargetKind.AmplifyItem:
                result = sub is >= 0 and <= 3 && data.Count is 3 or 4 && data[0] >= -1 && data[1] is >= -1 and <= 6 && data[2] >= -1
                    ? Parsed() : Invalid("打造结果需要等级、品级、装备等级及可选分类"); break;
            case QuestTargetKind.DisjointItem:
            case QuestTargetKind.SelfDisjointItem:
                result = sub == 1 ? ReadCountedPairs(data, 1) : sub == 0 && data is [>= -2, >= -1 and <= 6, >= -1]
                    ? Parsed() : Invalid("分解需要sub0装备范围或sub1产物表"); break;
            case QuestTargetKind.GetItem:
                result = sub is 1 or 2 && data is [>= -1, >= -1 and <= 6, >= -1] ? Parsed() : Pending("获取事件只闭合sub1/2的品级等级筛选"); break;
            case QuestTargetKind.EquippedItem:
                result = ReadEquipped(quest); break;
            case QuestTargetKind.PvpQuest:
                result = ReadPvp(quest); break;
            case QuestTargetKind.PvpMatch:
            case QuestTargetKind.PvpWin:
            case QuestTargetKind.PvpLose:
                result = data is [> 0] && sub is -1 or 0 or 7 ? Parsed() with { Initial = (uint)data[0] } : Invalid("决斗累计需要正场次与已确认模式"); break;
            case QuestTargetKind.RaidPhaseClear:
                result = data.Count >= 4 && data.Count % 4 == 0 && data.Where((_, at) => at % 4 == 1).All(value => value is >= 0 and <= 3)
                    ? Parsed() : Pending("攻坚阶段需要阶段、攻坚类型、目标次数和参与身份四元组"); break;
            case QuestTargetKind.Question:
                result = quest.Npc > 0 && data is [> 0] ? Parsed() : Invalid("问题需要独立NPC字段和已确认整数形状"); break;
            case QuestTargetKind.UseSkill:
                result = data is [0 or 1, > 0] ? Parsed() with { Initial = (uint)data[1] } : Pending("技能事件只闭合类别0/1，类别2没有事件来源"); break;
            case QuestTargetKind.CustomMultiCondition:
                result = data.Count == 4 && data.Take(3).All(value => value is >= 0 and <= 511) ? Parsed() : Pending("多条件需要三个9位通道及已解释模式"); break;
            case QuestTargetKind.ReachRange:
                result = sub == 0 && data is [> 0, >= 0, >= 0] || sub is 1 or 2 && data.Count == 6 && data[4] > 0 && data[5] > 0
                    ? Parsed() : Invalid("范围目标与对应sub形状不符"); break;
            case QuestTargetKind.LookCinematic:
                result = data is [> 0] ? Parsed() : Invalid("演出需要单一ID"); break;
            case QuestTargetKind.SendChatting:
                result = data.Count > 0 && data.All(value => value >= 0) && ReadStrings(quest).Length > 0 ? Parsed() : Invalid("聊天目标需要频道列表及字符串条件"); break;
            case QuestTargetKind.EnemyActionCheck:
            case QuestTargetKind.DamageByEnemy:
            case QuestTargetKind.CheckActiveStatus:
            case QuestTargetKind.CheckControl:
            case QuestTargetKind.CheckMyTeamDeadCount:
            case QuestTargetKind.EnemyKillOrder:
            case QuestTargetKind.EnemyFewKill:
                result = ReadNested(quest); break;
            case QuestTargetKind.ClearTreasureDungeon:
                result = sub is 1 or 2 && data.Count > 1 && data[0] == data.Count - 1 && data.Skip(1).All(value => value > 0)
                    ? Parsed() : Pending("宝物目标需要完整副本候选表，奖励事件未闭合"); break;
            case QuestTargetKind.PowerWarWin:
                result = data.Count == 0 ? Parsed() : Pending("势力战胜利的额外参数未确认"); break;
            case QuestTargetKind.PowerWarPoint:
                result = data is [>= 0] ? Parsed() : Invalid("势力点数需要单一阈值"); break;
            default:
                result = Pending("类型赋值已确认，专用参数或事件消费者未闭合"); break;
        }
        var limits = quest.Definition?.Values("limit dungeon index") ?? [];
        if (limits.Any(value => value.Type != 0 || value.Value <= 0)) return Invalid("limit dungeon index需要正副本列表");
        return result with { Strings = ReadStrings(quest), DungeonLimits = limits.Select(value => value.Value).ToImmutableArray() };
    }

    public static ImmutableArray<string> ReadStrings(StoryQuest quest) => quest.Definition?.Values("string data")
        .Where(value => value.Type == 6 && value.Text is not null).Select(value => value.Text!).ToImmutableArray() ?? [];

    static ImmutableArray<QuestValuePair> Pairs(IReadOnlyList<int> values, int start) =>
        Enumerable.Range(0, (values.Count - start) / 2).Select(at => new QuestValuePair(values[start + at * 2], values[start + at * 2 + 1])).ToImmutableArray();

    static QuestTargetParameters ReadCountedPairs(IReadOnlyList<int> data, int subtype) =>
        subtype is -1 or 0 or 1 or 2 && data.Count > 0 && data[0] >= 0 && data.Count == 1L + 2L * data[0]
            ? Parsed() with { Pairs = Pairs(data, 1) } : Invalid("目标需要N及N组键值，不能把首项当物品ID");

    static QuestTargetParameters ReadSeeking(StoryQuest quest)
    {
        bool recommended = quest.TargetKind == QuestTargetKind.Seeking && quest.SubType == 20;
        int tail = quest.TargetKind == QuestTargetKind.SeekAndMeetNpc || recommended ? 1 : 0;
        int length = quest.IntData.Count - tail;
        if (length <= 0 || length % 2 != 0) return Invalid("持有条件需要完整物品选择符/数量对");
        for (int at = 0; at < length; at += 2)
            if (quest.IntData[at] < -7 || quest.IntData[at + 1] <= 0) return Invalid("物品选择符或数量越界");
        if (quest.TargetKind == QuestTargetKind.SeekAndMeetNpc && quest.IntData[^1] <= 0 ||
            recommended && quest.IntData[^1] is < -1 or > 4) return Invalid("寻物尾部NPC或难度无效");
        var pairs = Enumerable.Range(0, length / 2).Select(at => new QuestValuePair(quest.IntData[at * 2], quest.IntData[at * 2 + 1])).ToImmutableArray();
        return Parsed() with { Pairs = pairs };
    }

    static bool ReadDungeon(IReadOnlyList<int> data, ref int at, out QuestDungeonSelector selector)
    {
        selector = default;
        if (at + 1 >= data.Count) return false;
        int id = data[at++];
        if (id is -2 or -4)
        {
            if (at + 1 >= data.Count || data[at] < 0) return false;
            int area = data[at++], difficulty = data[at++];
            selector = new(id == -2 ? QuestDungeonSelectorKind.FilteredArea : QuestDungeonSelectorKind.Area, area, difficulty);
        }
        else
        {
            if (id == 0 || id < -3) return false;
            selector = new(id == -1 ? QuestDungeonSelectorKind.Any : id == -3 ? QuestDungeonSelectorKind.Recommended : QuestDungeonSelectorKind.Dungeon, id, data[at++]);
        }
        return selector.Difficulty is >= -1 and <= 4;
    }

    static QuestTargetParameters ReadHunt(StoryQuest quest)
    {
        var data = quest.IntData;
        if (data.Count == 0) return Invalid("猎杀列表为空");
        bool monster = quest.TargetKind == QuestTargetKind.HuntMonster;
        int width = monster && data[0] is -2 or -4 ? 5 : monster ? 4 : 5;
        if (data.Count % width != 0 || data.Count / width > 3) return Invalid("猎杀行宽或三通道边界无效");
        var rows = ImmutableArray.CreateBuilder<QuestHuntObjective>();
        for (int at = 0; at < data.Count;)
        {
            int start = at;
            if (!ReadDungeon(data, ref at, out var dungeon) || !monster && dungeon.Kind is QuestDungeonSelectorKind.Area or QuestDungeonSelectorKind.FilteredArea)
                return Invalid("猎杀副本选择符形状未确认");
            int targetFields = monster ? 2 : 3;
            if (at + targetFields > data.Count || at - start + targetFields != width)
                return Invalid("猎杀缺少目标或混用了整段行宽");
            int enemy = data[at++], kind = monster ? 1 : data[at++], count = data[at++];
            if (at - start != width || enemy <= 0 && enemy is not (-1 or -2 or -3 or -4 or -5 or -11) || kind is not (1 or 2 or 3 or 10) || count is < 1 or > 511)
                return Invalid("猎杀选择符、对象类型或数量无效");
            rows.Add(new(dungeon, enemy, kind, count));
        }
        return Parsed() with { Hunts = rows.ToImmutable() };
    }

    static QuestTargetParameters ReadClear(StoryQuest quest)
    {
        var data = quest.IntData;
        var dungeons = ImmutableArray.CreateBuilder<QuestDungeonSelector>();
        ImmutableArray<int> additional;
        if (quest.TargetKind == QuestTargetKind.ClearDungeon)
        {
            if (data.Count == 0 || data.Count % 2 != 0) return Invalid("列表通关需要副本/最低难度对");
            for (int at = 0; at < data.Count; at += 2)
            {
                if (data[at] <= 0 || data[at + 1] is < -1 or > 4) return Invalid("列表通关副本或难度无效");
                dungeons.Add(new(QuestDungeonSelectorKind.Dungeon, data[at], data[at + 1]));
            }
            var field = quest.Definition?.Values("clear a dungeon sub int data") ?? [];
            if (field.Any(value => value.Type != 0)) return Invalid("通关附加条件不是整数");
            additional = field.Select(value => value.Value).ToImmutableArray();
        }
        else
        {
            int at = 0;
            if (!ReadDungeon(data, ref at, out var dungeon)) return Invalid("通关副本选择符或难度无效");
            dungeons.Add(dungeon); additional = data.Skip(at).ToImmutableArray();
        }
        int needed = quest.SubType switch
        {
            0 or 1 or 2 or 3 or 5 or 7 or 8 or 9 or 10 or 11 or 17 => 1,
            12 or 13 or 14 or 15 or 16 or 18 => 2,
            4 or 6 or 22 or 25 => 0,
            _ => -1
        };
        if (needed < 0) return Pending("此条件通关子型尚未闭合");
        if (additional.Length < needed) return Invalid("通关附加参数不足");
        if (quest.SubType is 0 or 1 or 2 or 3 or 5 or 7 or 8 or 9 or 10 && additional[0] < 0 ||
            quest.SubType == 15 && additional[1] < 0 ||
            quest.SubType == 11 && additional[0] < -1 || quest.SubType == 18 && additional[0] < 0)
            return Invalid("通关统计阈值超出已确认范围");
        if (quest.TargetKind == QuestTargetKind.ConditionUnderClear && quest.SubType is 6 or 17 or 22 && additional.Length > 1)
            return Pending("通关次数后的额外参数尚未闭合，不忽略尾项或重解释成次数");
        uint initial = 1;
        if (quest.TargetKind == QuestTargetKind.ConditionUnderClear && quest.SubType is 6 or 17 or 22 && additional.Length != 0)
        {
            if (additional[0] <= 0) return Invalid("通关次数须为正数");
            initial = (uint)additional[0];
        }
        return Parsed() with { Dungeons = dungeons.ToImmutable(), Additional = additional, Initial = initial };
    }

    static QuestTargetParameters ReadEquipped(StoryQuest quest)
    {
        var slots = ReadStrings(quest);
        return quest.SubType is 0 or 1 && slots.Length > 0 && quest.IntData.Count == slots.Length * 2 &&
            quest.IntData.Where((_, at) => at % 2 == 0).All(value => value is >= -1 and <= 6) &&
            quest.IntData.Where((_, at) => at % 2 != 0).All(value => value >= -1)
            ? Parsed() with { Pairs = Pairs(quest.IntData, 0) } : Invalid("穿戴需要位置字符串及对应品级/等级对");
    }

    static QuestTargetParameters ReadPvp(StoryQuest quest)
    {
        var data = quest.IntData;
        if (quest.SubType == 8) return data.Count > 0 && data[0] >= 0 ? Parsed() : Invalid("决斗段位参数缺失");
        if (data.Count < 2 || data[0] <= 0 || data[1] is < -1 or > 3) return Invalid("决斗目标需要正次数和合法模式");
        if (quest.SubType is 2 or 4 or 5 or 7 && data.Count < 3) return Invalid("决斗附加条件缺失");
        if (quest.SubType == 7 && data[2] <= 0) return Invalid("连胜周期须为正数");
        return quest.SubType is 0 or 1 or 2 or 3 or 4 or 5 or 7 or 9 ? Parsed() with { Initial = (uint)data[0] } : Pending("决斗子型未闭合");
    }

    static QuestTargetParameters ReadNested(StoryQuest quest)
    {
        var fields = quest.Definition?.Fields.Where(field => field.Name == quest.Condition.Trim('[', ']')).ToArray() ?? [];
        if (fields.Length == 0) return Invalid("嵌套目标缺少同名条件段，空int data不等于无条件");
        var targets = ImmutableArray.CreateBuilder<QuestNestedObjective>();
        foreach (var field in fields)
        {
            if (!field.Values.IsEmpty) return Invalid("嵌套目标根节点不接受游离参数");
            int? Int(string name) => field.Children.Where(row => row.Name == name).ToArray() is [{ Values: [{ Type: 0 } value], Children.Length: 0 }] ? value.Value : null;
            string? Text(string name) => field.Children.Where(row => row.Name == name).ToArray() is [{ Values: [{ Type: 6, Text: not null } value], Children.Length: 0 }] ? value.Text : null;
            int dungeon = Int("dungeon index") ?? -1, difficulty = Int("difficult") ?? -1;
            int kind = Text("target type") switch { "monster" => 0, "apc" => 1, "passive object" => 2, "player" => 3, null => -1, _ => -2 };
            if (difficulty is < -1 or > 5) difficulty = -1;
            if (kind == -2) return Invalid("嵌套对象类型无效，不与hunt enemy编号混用");
            var checks = ImmutableArray.CreateBuilder<QuestNestedCheck>();
            var order = ImmutableArray.CreateBuilder<QuestValuePair>();
            foreach (var child in field.Children)
            {
                if (!child.Children.IsEmpty || child.Name is not ("dungeon index" or "difficult" or "target type" or "target index" or
                    "target index list" or "kill order" or "check action" or "check motion" or "check skill" or "active status" or "control type" or "count"))
                    return Pending("嵌套目标含未确认条件: " + child.Name);
                if (child.Name is "target index list" or "kill order")
                {
                    if (child.Values.Length % 2 != 0) return Invalid("对象列表缺少类型/模板对");
                    for (int at = 0; at < child.Values.Length; at += 2)
                    {
                        int type = child.Values[at].Text switch { "monster" => 0, "apc" => 1, "passive object" => 2, "player" => 3, _ => -1 };
                        if (type < 0 || child.Values[at + 1].Type != 0) return Invalid("对象列表参数类型无效");
                        order.Add(new(type, child.Values[at + 1].Value));
                    }
                }
                if (child.Name is not ("check action" or "check motion" or "check skill" or "active status" or "control type" or "count")) continue;
                var values = child.Values;
                if (values.Length < 2 || values[^2].Type != 0 || values[^1].Text is not ("excess" or "under")) return Invalid("嵌套阈值或严格比较符无效");
                int expected = child.Name == "count" ? 2 : child.Name is "active status" or "control type" ? 3 : 4;
                if (values.Length != expected || expected > 2 && values[0].Type != 6 || expected == 4 && values[1].Type != 0)
                    return Invalid("嵌套条件行宽或字段类型无效");
                if (child.Name == "active status" && values[0].Text is "any" or "all") return Invalid("此状态条件不接受any/all");
                checks.Add(new(child.Name, expected > 2 ? values[0].Text! : "", expected == 4 ? values[1].Value : -1,
                    values[^2].Value, values[^1].Text == "excess" ? QuestComparison.Greater : QuestComparison.Less));
            }
            if (checks.Count == 0 && order.Count == 0) return Invalid("嵌套条件没有有效谓词");
            targets.Add(new(dungeon, difficulty, kind, Int("target index") ?? -1, order.ToImmutable(), checks.ToImmutable()));
        }
        return Parsed() with { Nested = targets.ToImmutable() };
    }
}
