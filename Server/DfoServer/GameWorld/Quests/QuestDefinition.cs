using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Quests;

internal sealed record QuestField(string Name, ImmutableArray<QuestRewardToken> Values, ImmutableArray<QuestField> Children);
internal readonly record struct QuestRequiredAnswer(int QuestId, int Answer);
internal readonly record struct QuestTargetCharacter(string Job, int GrowType, int Awakening);
internal readonly record struct QuestDungeonRequirement(int DungeonId, int Parameter);
internal readonly record struct QuestAlternativeNpc(int Source, int Target, int CompletedQuest);
internal sealed record QuestNpcVisibility(ImmutableArray<int> Npcs, string Condition, string Visibility);

// 完整资源声明；未恢复的字段保留原值，不能以缺省值取得资格。
internal sealed record QuestDefinition(ImmutableArray<QuestField> Fields)
{
    public ImmutableArray<ImmutableArray<int>> PrerequisiteGroups { get; init; } = [];
    public ImmutableArray<QuestRequiredAnswer> RequiredAnswers { get; init; } = [];
    public ImmutableArray<int> Collisions { get; init; } = [];
    public ImmutableArray<int> AccountCollisions { get; init; } = [];
    public string? Job { get; init; }
    public ImmutableArray<QuestTargetCharacter> TargetCharacters { get; init; } = [];
    public ImmutableArray<QuestTargetCharacter> NonTargetCharacters { get; init; } = [];
    public ImmutableArray<QuestDungeonRequirement> Dungeons { get; init; } = [];
    public ImmutableArray<string> GradeModifiers { get; init; } = [];
    public int GrowType { get; init; } = -1;
    public int JobChange { get; init; }
    public bool AccountQuest { get; init; }
    public bool CannotGiveup { get; init; }
    public int GiveupRule { get; init; }
    public bool ExposedByNpc { get; init; } = true;
    public bool IsEvent { get; init; }
    public bool HardcoreOnly { get; init; }
    public ImmutableArray<int> HideOnCompletedQuests { get; init; } = [];
    public int CreatureKind { get; init; } = -1;
    public int CreatureLevel { get; init; } = -1;
    public ImmutableArray<int> ExpertJobLevel { get; init; } = [];
    public int? FirstExposedByNpc { get; init; }
    public ImmutableArray<int> RelationQuests { get; init; } = [];
    public ImmutableArray<QuestRewardToken> Exceptions { get; init; } = [];
    public int? MinimumClearCount { get; init; }
    public ImmutableArray<QuestRewardToken> SelectRewards { get; init; } = [];
    public ImmutableArray<QuestRewardToken> AcceptanceRewards { get; init; } = [];
    public ImmutableArray<QuestNpcVisibility> NpcVisibility { get; init; } = [];
    public ImmutableArray<QuestAlternativeNpc> AlternativeNpcs { get; init; } = [];
    public bool RewardItemTradeInstance { get; init; }
    public ImmutableArray<QuestDropDefinition> Drops { get; init; } = [];
    public ImmutableArray<string> Diagnostics { get; init; } = [];

    public ImmutableArray<QuestRewardToken> Values(string name) => Fields.FirstOrDefault(field => field.Name == name)?.Values ?? [];
    public bool Has(string name) => Fields.Any(field => field.Name == name);
}

internal static class QuestDefinitionReader
{
    public static StoryQuest Read(int id, string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve, new Dictionary<string, string>
        {
            ["dialog depend on job"] = "dialog"
        }, preserveExtraEndTags: true);
        var retained = ImmutableArray.CreateBuilder<QuestField>();
        int redundantEnds = 0;
        foreach (var field in document.Sections.Select(Convert))
        {
            // external将闭合标签作为独立段；完整整数前置后的重复空闭合不改变前置语义。
            if (field is {Name:"/pre required quest",Values.IsEmpty:true,Children.IsEmpty:true} && retained.Count>0 &&
                retained[^1] is {Name:"pre required quest",Children.IsEmpty:true} previous && !previous.Values.IsEmpty &&
                previous.Values.All(value=>value is {Type:0,Text:null,Value:>0}))
            { redundantEnds++; continue; }
            retained.Add(field);
        }
        var fields = retained.ToImmutable();
        var errors = ImmutableArray.CreateBuilder<string>();
        foreach (string diagnostic in document.Diagnostics)
            if (redundantEnds>0 && diagnostic=="多余闭合标签: /pre required quest") redundantEnds--;
            else errors.Add(diagnostic);
        var level = Integers("level");
        if (level.Length != 2)
            errors.Add("level不是完整上下限");
        string? type = Text("type");
        if (type is null)
            errors.Add("缺少type");
        // external sectionIntGroups忽略空前置段；原段仍留在Fields，不能用空OR组绕过其它有效前置。
        var groups = fields.Where(field => field.Name == "pre required quest").Select(field => IntegerValues(field))
            .Where(group => group.Length != 0).ToImmutableArray();
        var answers = Integers("pre required quest answer");
        if (answers.Length % 2 != 0)
            errors.Add("前置回答不是完整任务/答案对");
        var dungeon = Integers("dungeon info");
        if (dungeon.Length % 2 != 0)
            errors.Add("dungeon info不是完整地下城/参数对");
        var targets = Values("target character");
        if (targets.Length % 3 != 0 || targets.Where((value, at) => at % 3 == 0 ? value.Type != 6 : value.Type != 0).Any())
            errors.Add("target character不是完整职业/转职/觉醒组");
        var excludedTargets = Values("non target character");
        if (excludedTargets.Length % 3 != 0 || excludedTargets.Where((value, at) => at % 3 == 0 ? value.Type != 6 : value.Type != 0).Any())
            errors.Add("non target character不是完整职业/转职/觉醒组");
        var grades = Values("grade");
        if (grades.Any(value => value.Type != 6))
            errors.Add("grade含非字符串");
        var visibility = ImmutableArray.CreateBuilder<QuestNpcVisibility>();
        foreach (var field in fields.Where(field => field.Name == "npc visibility"))
        {
            ImmutableArray<QuestRewardToken> Child(string name) => field.Children.Count(child => child.Name == name) == 1
                ? field.Children.First(child => child.Name == name).Values : [];
            var npcs = Child("npc"); var condition = Child("condition"); var show = Child("visibility");
            if (field.Values.Length != 0 || field.Children.Length != 3 || npcs.Length == 0 || npcs.Any(value => value.Type != 0 || value.Value <= 0) ||
                condition is not [{ Type: 6, Text: not null }] || show is not [{ Type: 6, Text: not null }])
                errors.Add("NPC可见性声明不完整");
            else visibility.Add(new(npcs.Select(value => value.Value).ToImmutableArray(),condition[0].Text!,show[0].Text!));
        }
        var alternativeNpcs = ImmutableArray.CreateBuilder<QuestAlternativeNpc>();
        foreach (var field in fields.Where(field => field.Name == "alternative npc index"))
        {
            // Full03 QST+91C按资源顺序保留原NPC、替代NPC和已完成任务条件。
            if (field.Children.Length != 0 || field.Values is not [{ Type: 0 }, { Type: 0 },
                { Type: 6, Text: "[cleared quest]" }, { Type: 0 }])
                errors.Add("alternative npc index需要原NPC/替代NPC/[cleared quest]/任务编号");
            else alternativeNpcs.Add(new(field.Values[0].Value,field.Values[1].Value,field.Values[3].Value));
        }
        var metadata = new QuestDefinition(fields)
        {
            PrerequisiteGroups = groups,
            RequiredAnswers = Enumerable.Range(0, answers.Length / 2).Select(at => new QuestRequiredAnswer(answers[at * 2], answers[at * 2 + 1])).ToImmutableArray(),
            Collisions = Integers("collision quest"), AccountCollisions = Integers("account collision quest"),
            Job = Text("job"), GrowType = Scalar("grow type") ?? -1,
            TargetCharacters = targets.Length % 3 == 0 ? Enumerable.Range(0, targets.Length / 3)
                .Where(at => targets[at * 3].Text is not null).Select(at => new QuestTargetCharacter(targets[at * 3].Text!,
                    targets[at * 3 + 1].Value, targets[at * 3 + 2].Value)).ToImmutableArray() : [],
            NonTargetCharacters = excludedTargets.Length % 3 == 0 ? Enumerable.Range(0, excludedTargets.Length / 3)
                .Where(at => excludedTargets[at * 3].Text is not null).Select(at => new QuestTargetCharacter(excludedTargets[at * 3].Text!,
                    excludedTargets[at * 3 + 1].Value, excludedTargets[at * 3 + 2].Value)).ToImmutableArray() : [],
            Dungeons = Enumerable.Range(0, dungeon.Length / 2).Select(at => new QuestDungeonRequirement(dungeon[at * 2], dungeon[at * 2 + 1])).ToImmutableArray(),
            GradeModifiers = grades.Skip(1).Where(value => value.Text is not null).Select(value => value.Text!).ToImmutableArray(),
            JobChange = Scalar("job change quest") ?? 0, AccountQuest = Scalar("account quest") == 1,
            CannotGiveup = (Scalar("cant giveup") ?? 0) != 0, ExposedByNpc = Scalar("exposed by npc") != 0,
            GiveupRule = Scalar("cant giveup") ?? 0,
            HardcoreOnly = fields.Any(field => field.Name == "hardcore mode only"), HideOnCompletedQuests = Integers("condition quest hide"),
            IsEvent = (Scalar("event") ?? 0) != 0, CreatureKind = Scalar("creature kind") ?? -1,
            CreatureLevel = Scalar("creature level") ?? -1, ExpertJobLevel = Integers("expertjob level"),
            FirstExposedByNpc = Scalar("first exposed by npc"), RelationQuests = Integers("relation quest"),
            Exceptions = Values("exception quest"),
            MinimumClearCount = Scalar("multi condition min clear"),
            SelectRewards = Values("reward selection int data"), AcceptanceRewards = Values("depend give item"),
            NpcVisibility = visibility.ToImmutable(),
            AlternativeNpcs = alternativeNpcs.ToImmutable(),
            RewardItemTradeInstance = (Scalar("reward item trade instance") ?? 0) != 0,
            Drops = QuestDropDefinition.Read(fields,errors),
            Diagnostics = errors.ToImmutable()
        };
        var quest = new StoryQuest(id, path, type ?? "", Scalar("sub type") ?? -1, Integers("int data"),
            Scalar("npc index") ?? -1, Scalar("complete npc index") ?? -1,
            level.Length == 2 ? level[0] : -1, level.Length == 2 ? level[1] : -1,
            groups.SelectMany(group => group).ToImmutableArray(), Scalar("main quest"),
            dungeon.Length == 2 ? dungeon[0] : null, dungeon.Length == 2 ? dungeon[1] : null,
            Values("reward int data"), null, null, [])
        {
            Grade = grades.FirstOrDefault().Text, Difficulty = Text("difficulty"), RewardType = Text("reward type"),
            DeclaredGoldMultiple = Scalar("gold multiple"), DeclaredIgnoreLevelForExperience = Scalar("ignore quest level 4 exp"),
            Attributes = Values("attribute").Where(value => value.Text is not null).Select(value => value.Text!).ToImmutableArray(),
            Definition = metadata
        };
        return quest with { Definition = metadata with { Diagnostics = errors.ToImmutable() } };

        QuestField Convert(PvfTokenSection section) => new(section.Name,
            section.Values.Select(token => new QuestRewardToken(token.Type, token.Value, token.Type == 6 ? resolve(token.Value) : null)).ToImmutableArray(),
            section.Children.Select(Convert).ToImmutableArray());
        ImmutableArray<QuestRewardToken> Values(string name)
        {
            var matches = fields.Where(field => field.Name == name).ToArray();
            // 同值重复声明不改变解释；冲突声明保留原文并禁止取得资格。
            if (matches.Length > 1 && matches.All(field => field.Values.SequenceEqual(matches[0].Values)) &&
                name is not ("pre required quest" or "collision quest" or "account collision quest"))
                return matches[0].Values;
            if (matches.Length > 1 && name is not ("pre required quest" or "collision quest" or "account collision quest"))
                errors.Add("重复字段: " + name);
            return matches.SelectMany(field => field.Values).ToImmutableArray();
        }
        ImmutableArray<int> IntegerValues(QuestField field)
        {
            if (field.Values.Any(value => value.Type != 0))
                errors.Add("非整数字段: " + field.Name);
            return field.Values.Where(value => value.Type == 0).Select(value => value.Value).ToImmutableArray();
        }
        ImmutableArray<int> Integers(string name)
        {
            var values = Values(name);
            if (values.Any(value => value.Type != 0))
                errors.Add("非整数字段: " + name);
            return values.Where(value => value.Type == 0).Select(value => value.Value).ToImmutableArray();
        }
        int? Scalar(string name)
        {
            var values = Integers(name);
            if (values.Length > 1)
                errors.Add("标量字段含多值: " + name);
            return values.Length == 1 ? values[0] : null;
        }
        string? Text(string name)
        {
            var values = Values(name);
            if (values.Length == 0)
                return null;
            if (values.Length != 1 || values[0].Type != 6)
            {
                errors.Add("非字符串标量: " + name);
                return null;
            }
            return values[0].Text;
        }
    }
}
