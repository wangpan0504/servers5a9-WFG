using DfoServer.GameWorld.Quests;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestConnectionCatalogTests
{
    public static void RunCore()
    {
        int checks = Count;
        var strings = new Dictionary<string,int>(StringComparer.Ordinal);
        var values = new List<PvfToken>();
        void Tag(string text) => Add(3,text);
        void Text(string text) => Add(6,text);
        void Add(byte type,string text)
        {
            if (!strings.TryGetValue(text,out int id)) strings.Add(text,id = strings.Count);
            values.Add(new(type,id));
        }
        void Integers(params int[] numbers) { foreach (int number in numbers) values.Add(new(0,number)); }
        void Unit(int key,bool invalid = false,bool unknown = false)
        {
            Tag("[connect quest unit]"); Tag("[type]"); Text("episode quest"); Tag("[parent key]"); Integers(key);
            Tag("[name]"); Text("fixture"); Tag("[question quest]"); Integers(6383);
            Tag("[quick clear]"); if (invalid) Integers(6380,1,6330); else Integers(6380,1,6330,6352);
            Tag("[sequence]"); Integers(6383,0,6330,0,6342,5410,6381,-1,6343,5433,6352,0); Tag("[/sequence]");
            if (unknown) { Tag("[future skip rule]"); Integers(9); }
            Tag("[/connect quest unit]");
        }
        string Resolve(int id) => strings.Single(pair => pair.Value == id).Key;
        Unit(9,invalid: true); Unit(23); Unit(24,unknown: true);
        var catalog = QuestConnectionCatalog.Parse(values,Resolve);
        That(catalog.TryGet(23,out var unit) && unit.SourceOrdinal == 1 && unit.ParentKey == 23 && unit.QuestionQuestId == 6383 &&
            unit.QuickClear == new QuestQuickClearDefinition(6380,1,6330,6352),"坏的归档单元不阻断后面的完整四元组和question映射");
        That(unit.Sequence.Select(step => step.QuestId).SequenceEqual([6383,6330,6342,6381,6343,6352]) &&
            unit.Sequence[3].Parameter == -1,"sequence完整保留原顺序和未解释的原参数，不数值排序或忽略6381");
        That(catalog.Diagnostics.Length == 1 && catalog.Units.Single(row => row.ParentKey == 24).Diagnostics.Length == 1 &&
            !catalog.TryGet(24,out _),"未知关联声明保留诊断，不冒充已确认跳过入口");
        That(catalog.TryGetQuestion(6383,out var byQuestion) && byQuestion.ParentKey == 23,"问题反查使用实际资源question值");
        Unit(23);
        var duplicate = QuestConnectionCatalog.Parse(values,Resolve);
        That(!duplicate.TryGet(23,out _) && duplicate.Diagnostics.Any(reason => reason.Contains("重复parent key",StringComparison.Ordinal)),
            "重复parent key保留资源并拒绝任意覆盖选择");
        Console.WriteLine($"PASS {Count-checks} 任务关联目录原序及归档隔离断言");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = QuestConnectionCatalog.Load(pvf);
        var expected = new[]
        {
            (Key: 19, Name: "夜之摩天楼", Question: 6217, Quick: new QuestQuickClearDefinition(6218,2,6200,6211)),
            (Key: 21, Name: "魔界风云", Question: 6382, Quick: new QuestQuickClearDefinition(6315,1,6300,6308)),
            (Key: 23, Name: "魔界大战", Question: 6383, Quick: new QuestQuickClearDefinition(6380,1,6330,6352))
        };
        foreach (var row in expected)
        {
            That(catalog.TryGet(row.Key,out var unit) && unit.Type == "episode quest" && unit.Name == row.Name &&
                unit.QuestionQuestId == row.Question && unit.QuickClear == row.Quick && unit.Sequence[0].QuestId == row.Question,
                "真实关联单元完整保存名称、question、四元组及原始首任务: "+row.Key);
            That(catalog.TryGetQuestion(row.Question,out var found) && ReferenceEquals(found,unit),"真实问题反查相同只读单元: "+row.Question);
        }
        That(catalog.TryGet(23,out var war),"魔界大战关联单元可读取");
        int before = war.Sequence.IndexOf(new(6342,5410));
        That(before >= 0 && before+2 < war.Sequence.Length && war.Sequence[before+1] == new QuestConnectionStep(6381,0) &&
            war.Sequence[before+2] == new QuestConnectionStep(6343,5433),"真实sequence在6342与6343之间保留插入任务6381及各自原参数");
        That(war.Sequence.Any(step => step.QuestId == 6330) && war.Sequence[^1].QuestId == 6352 &&
            war.Sequence.Any(step => step.QuestId > war.QuickClear!.Value.EndQuestId),"声明的终止ID不能被用于数值区间过滤整个sequence");
        Console.WriteLine($"PASS {Count-checks} 匹配PVF任务关联、quick clear及插入任务断言");
    }
}
