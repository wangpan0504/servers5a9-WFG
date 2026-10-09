using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Quests;

internal readonly record struct QuestConnectionStep(int QuestId, int Parameter);
internal readonly record struct QuestQuickClearDefinition(int MarkerQuestId, int Answer, int StartQuestId, int EndQuestId);
internal sealed record QuestConnectionUnit(int SourceOrdinal, int ParentKey, string Type, string Name,
    int? QuestionQuestId, QuestQuickClearDefinition? QuickClear, ImmutableArray<QuestConnectionStep> Sequence)
{
    public ImmutableArray<string> Diagnostics { get; init; } = [];
}

// 只保存资源原顺序和显式四元组；跳过范围及完成事实由任务领域校验。
internal sealed class QuestConnectionCatalog
{
    public const string ResourcePath = "n_quest/connectquestlist.etc";
    public ImmutableArray<QuestConnectionUnit> Units { get; }
    public ImmutableArray<string> Diagnostics { get; }
    readonly Dictionary<int,QuestConnectionUnit> byParent;

    QuestConnectionCatalog(ImmutableArray<QuestConnectionUnit> units,ImmutableArray<string> diagnostics)
    {
        Units = units; Diagnostics = diagnostics;
        byParent = units.GroupBy(row => row.ParentKey).Where(group => group.Count() == 1 && group.Single().Diagnostics.Length == 0)
            .ToDictionary(group => group.Key,group => group.Single());
    }

    public bool TryGet(int parentKey,out QuestConnectionUnit unit) => byParent.TryGetValue(parentKey,out unit!);

    public bool TryGetQuestion(int questId,out QuestConnectionUnit unit)
    {
        var matches = byParent.Values.Where(row => row.QuestionQuestId == questId).Take(2).ToArray();
        unit = matches.Length == 1 ? matches[0] : null!;
        return matches.Length == 1;
    }

    // Full03 0418D044把sequence第二值保留为路线；回放UI只暴露正值，不按目标地图猜路线。
    public bool TryGetReplayDungeon(int questId,out uint dungeonId)
    {
        var routes = byParent.Values.SelectMany(unit => unit.Sequence)
            .Where(step => step.QuestId == questId && step.Parameter > 0)
            .Select(step => step.Parameter).Distinct().Take(2).ToArray();
        dungeonId = routes.Length == 1 ? checked((uint)routes[0]) : 0;
        return routes.Length == 1;
    }

    public static QuestConnectionCatalog Load(PvfArchive pvf) => Parse(pvf.ReadTokens(ResourcePath),pvf.ResolveString);

    internal static QuestConnectionCatalog Parse(IReadOnlyList<PvfToken> tokens,Func<int,string> resolve)
    {
        var units = ImmutableArray.CreateBuilder<QuestConnectionUnit>();
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        int ordinal = 0;
        for (int at = 0; at < tokens.Count;)
        {
            if (tokens[at].Type != 3 || Tag(tokens[at]) != "[connect quest unit]")
            {
                diagnostics.Add("关联任务单元之外的声明尚未读取: " + (tokens[at].Type == 3 ? Tag(tokens[at]) : "无标签token"));
                do { at++; } while (at < tokens.Count && tokens[at].Type != 3);
                continue;
            }
            int source = ordinal++, start = ++at;
            while (at < tokens.Count && !(tokens[at].Type == 3 && Tag(tokens[at]) is "[/connect quest unit]" or "[connect quest unit]")) at++;
            if (at == tokens.Count || Tag(tokens[at]) != "[/connect quest unit]")
            {
                diagnostics.Add("关联任务单元未闭合: " + source);
                continue;
            }
            try
            {
                var document = new PvfTokenDocument(tokens.Skip(start).Take(at-start).ToArray(),resolve);
                units.Add(Read(source,document.Sections));
            }
            catch (InvalidDataException error) { diagnostics.Add("关联任务单元不可用: " + source + ": " + error.Message); }
            at++;
        }
        foreach (var group in units.GroupBy(row => row.ParentKey).Where(group => group.Count() > 1))
            diagnostics.Add("重复parent key不能选择跳过单元: " + group.Key);
        return new(units.ToImmutable(),diagnostics.ToImmutable());

        string Tag(PvfToken token) => resolve(token.Value).Trim().ToLowerInvariant();

        QuestConnectionUnit Read(int source,ImmutableArray<PvfTokenSection> fields)
        {
            PvfTokenSection? Field(string name,bool required = true)
            {
                var rows = fields.Where(field => field.Name == name).ToArray();
                if (rows.Length > 1 || required && rows.Length == 0) throw new InvalidDataException("关联任务字段缺失或重复: " + name);
                return rows.SingleOrDefault();
            }
            int[] Integers(string name,bool required = true)
            {
                var field = Field(name,required);
                if (field is null) return [];
                if (field.Children.Length != 0 || field.Values.Any(token => token.Type != 0))
                    throw new InvalidDataException("关联任务字段不是整数声明: " + name);
                return field.Values.Select(token => token.Value).ToArray();
            }
            string Text(string name)
            {
                var field = Field(name)!;
                if (field.Values is not [{ Type: 6 }] || field.Children.Length != 0)
                    throw new InvalidDataException("关联任务字段不是单字符串: " + name);
                return resolve(field.Values[0].Value);
            }
            int[] parent = Integers("parent key");
            int[] question = Integers("question quest",false);
            int[] quick = Integers("quick clear",false);
            int[] sequence = Integers("sequence");
            bool QuestId(int id) => id is > 0 and < 30000;
            if (parent is not [> 0] || question.Length > 1 || question.Length == 1 && !QuestId(question[0]) ||
                sequence.Length == 0 || sequence.Length % 2 != 0 || sequence.Where((_,index) => index % 2 == 0).Any(id => !QuestId(id)))
                throw new InvalidDataException("parent key、question或sequence不是完整合法声明");
            if (quick.Length != 0 && (quick.Length != 4 || question.Length != 1 || !QuestId(quick[0]) || quick[1] < 0 ||
                !QuestId(quick[2]) || !QuestId(quick[3])))
                throw new InvalidDataException("quick clear不是问题任务对应的完整四元组");
            var pending = fields.Where(field => field.Name is not ("type" or "parent key" or "worldmap index" or "level section" or
                "name" or "summary condition" or "question quest" or "quick clear" or "sequence" or "quest group" or "complete text" or "movie file path"))
                .Select(field => "关联任务字段尚未确认: " + field.Name).Distinct().ToImmutableArray();
            return new(source,parent[0],Text("type"),Text("name"),question.Length == 1 ? question[0] : null,
                quick.Length == 0 ? null : new(quick[0],quick[1],quick[2],quick[3]),
                Enumerable.Range(0,sequence.Length/2).Select(index => new QuestConnectionStep(sequence[index*2],sequence[index*2+1])).ToImmutableArray())
                { Diagnostics = pending };
        }
    }
}
