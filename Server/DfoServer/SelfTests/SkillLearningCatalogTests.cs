using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Characters;
using PvfLib;

namespace DfoServer;

internal static class SkillLearningCatalogTests
{
    private static readonly string[] FixtureStrings =
    [
        "[type]", "[purchase cost]", "[maximum level]", "[growtype maximum level]", "[required level]",
        "[required level range]", "[pre required skill]", "[/purchase cost]", "[/pre required skill]", "[active]", "[unknown]", "[growtype fitness]", "[feature skill type]", "[combo set]",
        "[passive]", "[skill class]", "[skill fitness growtype]", "[guild skill]", "[expert skill]"
    ];

    public static void RunCore()
    {
        var active = Fixture([7], [4], [4, 4, 4, 4, 4, 4], [1], [0], []);
        That(Parse(active).Type == SkillType.Active && Parse(active).SlotGroup == 3, "主动类型保留且不依赖被动分组字段");
        var defaults = Parse(Fixture([7], [4], [4, 4, 4, 4, 4, 4], null, null, []));
        That(defaults.SupportsOrdinarySpLearning && defaults.GetOrdinaryNextRequiredCharacterLevel(0) == 1 &&
            defaults.GetOrdinaryNextRequiredCharacterLevel(2) == 3 && !defaults.UnsupportedFields.Contains("required level range"),
            "缺省等级与间隔沿匹配客户端初始化为1，普通多级门槛继续累计");
        That(Parse(active).GetOrdinaryNextRequiredCharacterLevel(2) == 1,
            "显式间隔0保留，不能被缺省值覆盖");
        That(!Parse(Fixture([7], [4], [4, 4, 4, 4, 4, 4], [], [1], [])).SupportsOrdinarySpLearning &&
            !Parse(Fixture([7], [4], [4, 4, 4, 4, 4, 4], [1], [], [])).SupportsOrdinarySpLearning,
            "显式空等级或间隔仍保持不支持，不用缺省值遮盖非法声明");
        Reject<InvalidDataException>(() => Parse(Fixture([7], [4], [4, 4, 4, 4, 4, 4], [1], [-1], [])));
        That(Parse([.. active, new(3, 17)]).IsGuildSkill && !Parse([.. active, new(3, 17)]).SupportsOrdinarySpLearning &&
            Parse([.. active, new(3, 18)]).IsExpertSkill && !Parse([.. active, new(3, 18)]).SupportsOrdinarySpLearning,
            "公会与expert技能资源不混用普通SP或达人超等级资格");
        var passive = (PvfToken[])active.Clone();
        passive[1] = new(6, 14);
        That(Parse(passive).Type == SkillType.Passive && Parse(passive).SlotGroup is null,
            "被动类型与未知分组分别保留，不默认主动");
        foreach (int rawClass in Enumerable.Range(0, 4))
        {
            var common = Parse([.. passive, new(3, 15), new(0, rawClass), new(3, 16), new(0, 0), new(0, 1), new(0, 2)]);
            That(common.SlotGroup == 0 && common.SkillClass == rawClass && common.SkillFitnessGrowtypeCount == 3 &&
                common.GetOrdinaryResourceSpCost(0, 0) == 7, "多方向被动为共通组且分组元数据不影响SP费用");
            var branch = Parse([.. passive, new(3, 15), new(0, rawClass), new(3, 16), new(0, 0), new(0, 1)]);
            That(branch.SlotGroup == 1 && branch.SkillFitnessGrowtypeCount == 2, "两方向被动为转职组");
        }
        That(Parse([.. passive, new(3, 15), new(0, 4)]).SlotGroup == 2, "class4被动为基础生活组，缺fitness不影响分组");
        That(Parse([.. passive, new(3, 15), new(0, 0)]).SlotGroup == 1, "方向vector缺省为空，已声明普通被动归转职组");
        That(Parse([.. passive, new(3, 15), new(0, 7), new(3, 16), new(0, 0)]).SlotGroup == 0, "参考端的分组越界回退到普通被动组");
        var unknown = (PvfToken[])active.Clone();
        unknown[1] = new(6, 10);
        That(Parse(unknown).Type == SkillType.Unknown && !Parse(unknown).SupportsOrdinarySpLearning, "未知type禁止普通学习");
        Reject<InvalidDataException>(() => Parse([.. passive, new(3, 15), new(2, 4)]));
        Reject<InvalidDataException>(() => Parse([.. passive, new(3, 16), new(0, -1)]));
        var repeatedClass = Parse([.. passive, new(3, 15), new(0, 4), new(3, 15), new(0, 4)]);
        That(repeatedClass.SlotGroup is null && repeatedClass.SupportsOrdinarySpLearning && repeatedClass.GetOrdinaryResourceSpCost(0, 0) == 7,
            "重复class不猜被动组，保留原费用和已绑定技能升级资格");
        PvfToken[] repeatedFitness = [new(3, 15), new(0, 0), new(3, 16), new(0, 0), new(3, 16), new(0, 1)];
        That(Parse([.. active, .. repeatedFitness]).SlotGroup == 3 && Parse([.. active, .. repeatedFitness]).SupportsOrdinarySpLearning,
            "重复方向元数据不影响主动学习和主动组");
        That(Parse([.. passive, .. repeatedFitness]).SlotGroup is null && Parse([.. passive, .. repeatedFitness]).SupportsOrdinarySpLearning,
            "重复方向元数据的被动新分组未知，原有普通学习字段继续读取");
        var repeatedFields = Parse([.. active, new(3, 0), new(6, 14), new(3, 1), new(0, 40), new(3, 7),
            new(3, 4), new(0, 35), new(3, 6), new(0, 12), new(0, 1), new(3, 8),
            new(3, 12), new(0, 1), new(3, 12), new(0, 0)]);
        That(repeatedFields.Type == SkillType.Active && repeatedFields.GetBasePointCost(0) == 7 &&
            repeatedFields.GetOrdinaryNextRequiredCharacterLevel(0) == 1 && repeatedFields.Prerequisites is { Count: 0 } &&
            repeatedFields.IsFeatureSkill, "重复类型、费用、等级、前置及feature按external取首段，保留空首段");
        var firstPrerequisites = Fixture([7], [4], [4, 4, 4, 4, 4, 4], [1], [0], [12, 1]);
        That(Parse([.. firstPrerequisites, new(3, 6), new(3, 8)]).Prerequisites!
            .SequenceEqual(new[] { new SkillPrerequisite(12, 1) }), "后续空前置块不清除首段要求");
        Reject<InvalidDataException>(() => Parse([.. firstPrerequisites, new(3, 6), new(0, 12), new(0, 1)]));
        Reject<InvalidDataException>(() => Parse([.. Fixture([-1], [4], [4, 4, 4, 4, 4, 4], [1], [0], []),
            new(3, 1), new(0, 7), new(3, 7)]));
        Console.WriteLine("PASS skill resource type and slot group assertions");
    }

    public static void Run(PvfArchive pvf)
    {
        var catalog = SkillLearningCatalog.Load(pvf);
        var ghost = catalog.Get(0, 5);
        That(ghost.Type == SkillType.Active && ghost.SlotGroup == 3 && ghost.SkillClass == 3 && ghost.SkillFitnessGrowtypeCount == 6,
            "真实鬼斩保留主动类型、分组及方向数量");
        foreach (ushort passiveId in new ushort[] { 174, 179, 511 })
            That(catalog.Get(0, passiveId).Type == SkillType.Passive && catalog.Get(0, passiveId).SlotGroup == 2,
                "真实基础被动资源分类 " + passiveId);
        That(catalog.Get(0, 113).Type == SkillType.Active && catalog.Get(0, 113).SlotGroup == 3 &&
            catalog.Get(0, 113).SupportsOrdinarySpLearning && catalog.Get(0, 113).UnsupportedFields.Contains("skill fitness growtype"),
            "真实资源的重复分组声明不连带拒绝原有主动学习字段");
        That(ghost.Path == "skill/swordman/hardattack.skl" && ghost.PointCostLevelCount == 60 &&
            ghost.GetBasePointCost(0) == 15 && ghost.GetBasePointCost(1) == 15 && ghost.GetBasePointCost(59) == 15,
            "鬼斩费用15重复末项至60级，索引为当前已学等级");
        That(ghost.GetOrdinaryNextRequiredCharacterLevel(0) == 1 && ghost.GetOrdinaryNextRequiredCharacterLevel(1) == 3 &&
            ghost.GetDeclaredGrowtypeMaximum(0) == 50 && ghost.GetDeclaredGrowtypeMaximum(5) == 50,
            "鬼斩下一等级角色门槛与growtype声明分开");
        That(ghost.Prerequisites is { Count: 0 } && ghost.SupportsOrdinarySpLearning && ghost.GetOrdinaryResourceSpCost(1, 0) == 15,
            "原生初始化确认缺失前置为空、普通 SP 费用15");
        foreach (ushort id in new ushort[] { 5, 46 })
            That(catalog.Get(0, id).SupportsOrdinarySpLearning, "鬼剑士普通初始技能资源资格" + id);
        var upper = catalog.Get(0, 46);
        That(upper.GetOrdinaryResourceSpCost(1, 0) == 20 && upper.GetOrdinaryNextRequiredCharacterLevel(1) == 4 &&
            upper.GetDeclaredGrowtypeMaximum(0) == 10, "上挑普通资源费用及下一等级门槛");
        var backstep = catalog.Get(0, 169);
        That(backstep.SupportsOrdinarySpLearning && backstep.GetBasePointCost(0) == 30 &&
            backstep.GetDeclaredGrowtypeMaximum(0) == 1 && backstep.GetOrdinaryNextRequiredCharacterLevel(0) == 1,
            "后跳缺省间隔为1，仍受资源一级上限限制");
        That(!ghost.IsFeatureSkill && !ghost.HasComboDeclaration && ghost.DeclaredGrowtypeFitness!.All(value => value == 0),
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
            That(definition.Job == job && definition.SkillId == ids[job] &&
                definition.Path.StartsWith("skill/" + directories[job] + "/", StringComparison.Ordinal) &&
                definition.GetBasePointCost(0) == costs[job] && definition.PointCostLevelCount == maximum[job] &&
                definition.GetOrdinaryNextRequiredCharacterLevel(0) == 1,
                "职业" + job + "使用自己的技能ID与资源基础字段");
        }
        That(catalog.Get(0, 5).Path != catalog.Get(1, 5).Path &&
            catalog.Get(10, 250).GetDeclaredGrowtypeMaximum(1) == 0,
            "同技能ID不跨职业合并，growtype零声明原样保留");
        foreach (var (job, id) in new (byte, ushort)[] { (2, 109), (2, 143), (4, 49), (5, 143), (6, 122),
            (8, 3), (8, 216), (8, 218), (14, 68) })
            That(catalog.Get(job, id).SupportsPointLearning, "真实重复标签技能完整解析并保留SP/TP资格 " + job + "/" + id);
        That(catalog.Get(4, 49).DeclaredPointCosts!.SequenceEqual(new[] { 10 }) &&
            catalog.Get(6, 122).Prerequisites is { Count: 0 }, "不同费用和先空后有的前置声明均沿external取首段");
        That(catalog.Get(14, 68).Prerequisites!.SequenceEqual(new[] { new SkillPrerequisite(12, 1) }) &&
            catalog.Get(14, 68).GetBasePointCost(0) == 20, "双重切割保留首段前置技能12一级与20SP费用");

        PvfToken[] valid = Fixture([15, 20], [4], [1, 2, 3, 4, 5, 6], [1], [2], [46, 2, 169, 1]);
        var synthetic = Parse(valid);
        That(synthetic.GetBasePointCost(0) == 15 && synthetic.GetBasePointCost(1) == 20 &&
            synthetic.GetBasePointCost(3) == 20 && synthetic.GetOrdinaryNextRequiredCharacterLevel(1) == 3 &&
            synthetic.Prerequisites!.SequenceEqual(new[] { new SkillPrerequisite(46, 2), new SkillPrerequisite(169, 1) }),
            "真实声明形状的fixture保留逐级费用及前置对");
        That(Parse(Fixture([7], [2, 3, 4, 5, 6, 7], [1, 2, 3, 4, 5, 6], [1], [2], []))
            .GetBasePointCost(6) == 7, "六个maximum声明取最大值补尾");
        That(Parse(Fixture([7, 8, 9], [1], [1, 1, 1, 1, 1, 1], [1], [2], []))
            .PointCostLevelCount == 3, "maximum不会截断原本更长的费用vector");
        That(Parse(Fixture([7], [4], [1, 1, 1, 1, 1, 1], [1], [2], [])).Prerequisites!.Count == 0,
            "显式空前置块与缺标签分开");
        var missing = Parse([new PvfToken(3, 0), new PvfToken(6, 9)]);
        That(missing.UnsupportedFields.Contains("purchase cost") && missing.Prerequisites is { Count: 0 },
            "缺少字段公开unsupported而不填默认值");
        Reject<NotSupportedException>(() => missing.GetBasePointCost(0));
        Reject<NotSupportedException>(() => missing.GetOrdinaryNextRequiredCharacterLevel(0));
        Reject<NotSupportedException>(() => missing.GetDeclaredGrowtypeMaximum(0));
        var unknownMaximum = Parse(Fixture([7], [4, 5], [1, 1, 1, 1, 1, 1], [1], [2], []));
        That(unknownMaximum.UnsupportedFields.Contains("maximum level"), "未支持maximum形状明确标记");
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
        That(Parse([.. valid, new PvfToken(3, 4), new PvfToken(0, 35)]).GetOrdinaryNextRequiredCharacterLevel(0) == 1,
            "重复等级声明按首段读取");
        var discounted = Parse([.. valid, new PvfToken(3, 11), .. Enumerable.Repeat(new PvfToken(0, 33), 6)]);
        That(discounted.GetOrdinaryResourceSpCost(0, 0) == 10, "fitness 百分比整数截断，未减角色状态费用");
        Reject<ArgumentOutOfRangeException>(() => discounted.GetOrdinaryResourceSpCost(0, 6));
        var tp = Parse([.. valid, new PvfToken(3, 12), new PvfToken(0, -1)]);
        That(tp.IsFeatureSkill && !tp.SupportsOrdinarySpLearning, "feature 整数非零布尔消费且 TP 不冒充 SP");
        Reject<NotSupportedException>(() => tp.GetOrdinaryResourceSpCost(0, 0));
        That(Parse([.. valid, new PvfToken(3, 12), new PvfToken(0, 0)]).SupportsOrdinarySpLearning,
            "显式 feature 零允许普通 SP");
        Reject<InvalidDataException>(() => Parse([.. valid, new PvfToken(3, 13)]));
        That(!Parse([.. valid, new PvfToken(3, 11), new PvfToken(0, 1)]).SupportsOrdinarySpLearning,
            "fitness 必须六项");
        That(!Parse([.. valid, new PvfToken(3, 11), .. Enumerable.Repeat(new PvfToken(0, 101), 6)]).SupportsOrdinarySpLearning,
            "fitness 超出已支持百分比边界不猜费用");
        Reject<InvalidDataException>(() => Parse([.. valid, new PvfToken(3, 11), new PvfToken(0, -1)]));
        Reject<InvalidDataException>(() => Parse([.. valid, new PvfToken(3, 12), new PvfToken(2, 0)]));
        Console.WriteLine("PASS 技能学习资源基础规则、16职业映射与非法声明拒绝");
    }

    private static SkillLearningDefinition Parse(PvfToken[] tokens) =>
        SkillLearningCatalog.Parse(0, 5, "skill/fixture.skl", tokens, id => FixtureStrings[id]);

    private static PvfToken[] Fixture(int[] costs, int[] maximum, int[] growtype, int[]? basis, int[]? range, int[] prerequisites)
    {
        var tokens = new List<PvfToken> { new(3, 0), new(6, 9) };
        Add(1, costs, 7);
        Add(2, maximum);
        Add(3, growtype);
        Add(4, basis);
        Add(5, range);
        Add(6, prerequisites, 8);
        return tokens.ToArray();

        void Add(int tag, int[]? values, int? endTag = null)
        {
            if (values is null) return;
            tokens.Add(new PvfToken(3, tag));
            tokens.AddRange(values.Select(value => new PvfToken(0, value)));
            if (endTag is int end)
                tokens.Add(new PvfToken(3, end));
        }
    }


}
