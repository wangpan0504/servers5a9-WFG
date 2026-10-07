using DfoServer.GameWorld.Characters;
using PvfLib;

namespace DfoServer;

internal static class SkillLearningCatalogTests
{
    private static readonly string[] FixtureStrings =
    [
        "[type]", "[purchase cost]", "[maximum level]", "[growtype maximum level]", "[required level]",
        "[required level range]", "[pre required skill]", "[/purchase cost]", "[/pre required skill]", "[active]", "[unknown]", "[growtype fitness]", "[feature skill type]", "[combo set]"
    ];

    public static void Run(PvfArchive pvf)
    {
        var catalog = SkillLearningCatalog.Load(pvf);
        var ghost = catalog.Get(0, 5);
        Check(ghost.Path == "skill/swordman/hardattack.skl" && ghost.PointCostLevelCount == 60 &&
            ghost.GetBasePointCost(0) == 15 && ghost.GetBasePointCost(1) == 15 && ghost.GetBasePointCost(59) == 15,
            "鬼斩费用15重复末项至60级，索引为当前已学等级");
        Check(ghost.GetOrdinaryNextRequiredCharacterLevel(0) == 1 && ghost.GetOrdinaryNextRequiredCharacterLevel(1) == 3 &&
            ghost.GetDeclaredGrowtypeMaximum(0) == 50 && ghost.GetDeclaredGrowtypeMaximum(5) == 50,
            "鬼斩下一等级角色门槛与growtype声明分开");
        Check(ghost.Prerequisites is { Count: 0 } && ghost.SupportsOrdinarySpLearning && ghost.GetOrdinaryResourceSpCost(1, 0) == 15,
            "原生初始化确认缺失前置为空、普通 SP 费用15");
        foreach (ushort id in new ushort[] { 5, 46 })
            Check(catalog.Get(0, id).SupportsOrdinarySpLearning, "鬼剑士普通初始技能资源资格" + id);
        var upper = catalog.Get(0, 46);
        Check(upper.GetOrdinaryResourceSpCost(1, 0) == 20 && upper.GetOrdinaryNextRequiredCharacterLevel(1) == 4 &&
            upper.GetDeclaredGrowtypeMaximum(0) == 10, "上挑普通资源费用及下一等级门槛");
        var backstep = catalog.Get(0, 169);
        Check(!backstep.SupportsOrdinarySpLearning && backstep.GetBasePointCost(0) == 30 &&
            backstep.GetDeclaredGrowtypeMaximum(0) == 1 && backstep.UnsupportedFields.Contains("required level range"),
            "后跳满一级且缺 range，不猜默认继续升级");
        Check(!ghost.IsFeatureSkill && !ghost.HasComboDeclaration && ghost.DeclaredGrowtypeFitness!.All(value => value == 0),
            "缺少 feature、combo、fitness 使用原生重置默认");
        Reject<ArgumentOutOfRangeException>(() => ghost.GetBasePointCost(-1));
        Reject<ArgumentOutOfRangeException>(() => ghost.GetBasePointCost(60));
        Reject<ArgumentOutOfRangeException>(() => ghost.GetOrdinaryNextRequiredCharacterLevel(-1));
        Reject<ArgumentOutOfRangeException>(() => ghost.GetOrdinaryNextRequiredCharacterLevel(60));
        Reject<ArgumentOutOfRangeException>(() => ghost.GetDeclaredGrowtypeMaximum(-1));
        Reject<ArgumentOutOfRangeException>(() => ghost.GetDeclaredGrowtypeMaximum(6));
        Reject<KeyNotFoundException>(() => catalog.Get(16, 5));
        Reject<KeyNotFoundException>(() => catalog.Get(0, ushort.MaxValue));

        ushort[] ids = [5, 5, 4, 11, 1, 4, 1, 5, 1, 5, 250, 1, 1, 2, 1, 1];
        int[] costs = [15, 20, 20, 15, 20, 20, 15, 20, 20, 5, 0, 15, 20, 15, 20, 20];
        int[] maximum = [60, 20, 20, 60, 20, 20, 20, 20, 20, 60, 1, 60, 20, 60, 20, 20];
        string[] directories = ["swordman", "fighter", "gunner", "mage", "priest", "atgunner", "thief", "atfighter",
            "atmage", "demonicswordman", "creatormage", "atswordman", "knight", "demoniclancer", "atpriest", "gunblader"];
        for (byte job = 0; job < ids.Length; job++)
        {
            var definition = catalog.Get(job, ids[job]);
            Check(definition.Job == job && definition.SkillId == ids[job] &&
                definition.Path.StartsWith("skill/" + directories[job] + "/", StringComparison.Ordinal) &&
                definition.GetBasePointCost(0) == costs[job] && definition.PointCostLevelCount == maximum[job] &&
                definition.GetOrdinaryNextRequiredCharacterLevel(0) == 1,
                "职业" + job + "使用自己的技能ID与资源基础字段");
        }
        Check(catalog.Get(0, 5).Path != catalog.Get(1, 5).Path &&
            catalog.Get(10, 250).GetDeclaredGrowtypeMaximum(1) == 0,
            "同技能ID不跨职业合并，growtype零声明原样保留");

        PvfToken[] valid = Fixture([15, 20], [4], [1, 2, 3, 4, 5, 6], [1], [2], [46, 2, 169, 1]);
        var synthetic = Parse(valid);
        Check(synthetic.GetBasePointCost(0) == 15 && synthetic.GetBasePointCost(1) == 20 &&
            synthetic.GetBasePointCost(3) == 20 && synthetic.GetOrdinaryNextRequiredCharacterLevel(1) == 3 &&
            synthetic.Prerequisites!.SequenceEqual(new[] { new SkillPrerequisite(46, 2), new SkillPrerequisite(169, 1) }),
            "真实声明形状的fixture保留逐级费用及前置对");
        Check(Parse(Fixture([7], [2, 3, 4, 5, 6, 7], [1, 2, 3, 4, 5, 6], [1], [2], []))
            .GetBasePointCost(6) == 7, "六个maximum声明取最大值补尾");
        Check(Parse(Fixture([7, 8, 9], [1], [1, 1, 1, 1, 1, 1], [1], [2], []))
            .PointCostLevelCount == 3, "maximum不会截断原本更长的费用vector");
        Check(Parse(Fixture([7], [4], [1, 1, 1, 1, 1, 1], [1], [2], [])).Prerequisites!.Count == 0,
            "显式空前置块与缺标签分开");
        var missing = Parse([new PvfToken(3, 0), new PvfToken(6, 9)]);
        Check(missing.UnsupportedFields.Contains("purchase cost") && missing.Prerequisites is { Count: 0 },
            "缺少字段公开unsupported而不填默认值");
        Reject<NotSupportedException>(() => missing.GetBasePointCost(0));
        Reject<NotSupportedException>(() => missing.GetOrdinaryNextRequiredCharacterLevel(0));
        Reject<NotSupportedException>(() => missing.GetDeclaredGrowtypeMaximum(0));
        var unknownMaximum = Parse(Fixture([7], [4, 5], [1, 1, 1, 1, 1, 1], [1], [2], []));
        Check(unknownMaximum.UnsupportedFields.Contains("maximum level"), "未支持maximum形状明确标记");
        Reject<NotSupportedException>(() => unknownMaximum.GetBasePointCost(0));
        var badGrowtype = Parse(Fixture([7], [4], [1, 2], [1], [2], []));
        Reject<NotSupportedException>(() => badGrowtype.GetDeclaredGrowtypeMaximum(0));
        var unknownTypeTokens = (PvfToken[])valid.Clone();
        unknownTypeTokens[1] = new PvfToken(6, 10);
        Reject<NotSupportedException>(() => Parse(unknownTypeTokens).GetBasePointCost(0));
        Reject<InvalidDataException>(() => Parse(Fixture([-1], [4], [1, 1, 1, 1, 1, 1], [1], [2], [])));
        Reject<InvalidDataException>(() => Parse(Fixture([7], [4], [1, 1, 1, 1, 1, 1], [-1], [2], [])));
        Reject<InvalidDataException>(() => Parse(Fixture([7], [4], [1, 1, 1, 1, 1, 1], [1], [2], [46])));
        Reject<InvalidDataException>(() => Parse(Fixture([7], [4], [1, 1, 1, 1, 1, 1], [1], [2], [65536, 1])));
        var badType = (PvfToken[])valid.Clone();
        badType[3] = new PvfToken(2, BitConverter.SingleToInt32Bits(15));
        Reject<InvalidDataException>(() => Parse(badType));
        Reject<InvalidDataException>(() => Parse(valid[..^1]));
        Reject<InvalidDataException>(() => Parse([.. valid, new PvfToken(3, 4), new PvfToken(0, 1)]));
        var discounted = Parse([.. valid, new PvfToken(3, 11), .. Enumerable.Repeat(new PvfToken(0, 33), 6)]);
        Check(discounted.GetOrdinaryResourceSpCost(0, 0) == 10, "fitness 百分比整数截断，未减角色状态费用");
        Reject<ArgumentOutOfRangeException>(() => discounted.GetOrdinaryResourceSpCost(0, 6));
        var tp = Parse([.. valid, new PvfToken(3, 12), new PvfToken(0, -1)]);
        Check(tp.IsFeatureSkill && !tp.SupportsOrdinarySpLearning, "feature 整数非零布尔消费且 TP 不冒充 SP");
        Reject<NotSupportedException>(() => tp.GetOrdinaryResourceSpCost(0, 0));
        Check(Parse([.. valid, new PvfToken(3, 12), new PvfToken(0, 0)]).SupportsOrdinarySpLearning,
            "显式 feature 零允许普通 SP");
        Check(!Parse([.. valid, new PvfToken(3, 13)]).SupportsOrdinarySpLearning,
            "combo 声明即排除普通任意空槽分配");
        Check(!Parse([.. valid, new PvfToken(3, 11), new PvfToken(0, 1)]).SupportsOrdinarySpLearning,
            "fitness 必须六项");
        Check(!Parse([.. valid, new PvfToken(3, 11), .. Enumerable.Repeat(new PvfToken(0, 101), 6)]).SupportsOrdinarySpLearning,
            "fitness 超出已支持百分比边界不猜费用");
        Reject<InvalidDataException>(() => Parse([.. valid, new PvfToken(3, 11), new PvfToken(0, -1)]));
        Reject<InvalidDataException>(() => Parse([.. valid, new PvfToken(3, 12), new PvfToken(2, 0)]));
        Console.WriteLine("PASS 技能学习资源基础规则、16职业映射与非法声明拒绝");
    }

    private static SkillLearningDefinition Parse(PvfToken[] tokens) =>
        SkillLearningCatalog.Parse(0, 5, "skill/fixture.skl", tokens, id => FixtureStrings[id]);

    private static PvfToken[] Fixture(int[] costs, int[] maximum, int[] growtype, int[] basis, int[] range, int[] prerequisites)
    {
        var tokens = new List<PvfToken> { new(3, 0), new(6, 9) };
        Add(1, costs, 7);
        Add(2, maximum);
        Add(3, growtype);
        Add(4, basis);
        Add(5, range);
        Add(6, prerequisites, 8);
        return tokens.ToArray();

        void Add(int tag, int[] values, int? endTag = null)
        {
            tokens.Add(new PvfToken(3, tag));
            tokens.AddRange(values.Select(value => new PvfToken(0, value)));
            if (endTag is int end)
                tokens.Add(new PvfToken(3, end));
        }
    }

    private static void Check(bool valid, string name)
    {
        if (!valid)
            throw new InvalidOperationException("FAIL: " + name);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException("FAIL: 未拒绝 " + typeof(T).Name);
    }
}
