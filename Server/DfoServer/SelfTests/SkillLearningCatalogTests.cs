using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer;

internal static class SkillLearningCatalogTests
{
    private static readonly string[] FixtureStrings =
    [
        "[type]", "[purchase cost]", "[maximum level]", "[growtype maximum level]", "[required level]",
        "[required level range]", "[pre required skill]", "[/purchase cost]", "[/pre required skill]", "[active]", "[unknown]", "[growtype fitness]", "[feature skill type]", "[combo set]",
        "[passive]", "[skill class]", "[skill fitness growtype]", "[guild skill]", "[expert skill]", "[steel learning skill]"
    ];

    public static void RunCore()
    {
        string[] commandStrings = ["(UP)", "(DOWN)", "(LEFT)", "(RIGHT)", "(SKILL)", "(JUMP)", "(ATTACK)", "(BUFF)", "&", ","];
        string Command(PvfToken[] tokens) => SkillLearningCatalog.ParseCommand(tokens, id => commandStrings[id]);
        for (int direction = 0; direction < 4; direction++)
            That(Command([new(5, direction), new(7, 8), new(5, 4)]) == Convert.ToHexString([(byte)(9 + direction), 4]),
                "四方向按住编码9..12，不是普通方向重复 " + direction);
        That(Command([new(5, 3), new(7, 9), new(5, 3), new(7, 9), new(5, 4)]) == "030304",
            "连续右右Z与按住右Z的0C04明确分开");
        That(Command([new(5, 1), new(7, 8), new(5, 5)]) == "0A06" &&
            Command([new(5, 1), new(7, 8), new(5, 6)]) == "0A05" && Command([new(5, 7)]) == "08",
            "按住方向加跳跃/攻击和BUFF操作键分别保留");
        That(Command([]) == "" && Command([new(5, 5)]) == "" && Command([new(5, 6)]) == "",
            "空声明和固有单跳跃/攻击不冒充可安装的自定义指令");
        Reject<InvalidDataException>(() => Command([new(0, 3), new(7, 8), new(5, 4)]));
        Reject<InvalidDataException>(() => Command([new(5, 3), new(7, 8)]));
        Reject<InvalidDataException>(() => Command([new(5, 4), new(7, 8), new(5, 4)]));
        var active = Fixture([7], [4], [4, 4, 4, 4, 4, 4], [1], [0], []);
        var steel=Parse([..active,new(3,19),new(0,1),new(0,50),new(0,200),new(0,4)]);
        That(steel.SteelLearning==new SteelLearningRule(true,50,200,4) && steel.IsSteelLearning(0) &&
            steel.GetPointCost(0,0)==14 && steel.GetLearningMaximum(0)==2 && steel.GetNextRequiredCharacterLevel(0,0)==5,
            "偷学标签四参数独立解析，费用及门槛走集中规则");
        foreach(int[] invalid in new int[][]{[1,50,200],[2,50,200,4],[1,101,200,4],[1,50,0,4],[1,50,200,-1]})
            Reject<InvalidDataException>(()=>Parse([..active,new(3,19),..invalid.Select(value=>new PvfToken(0,value))]),
                "畸形偷学声明不能默认为普通技能绕过门禁");
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
        That(Parse(passive).Type == SkillType.Passive && Parse(passive).SkillClass == 0 && Parse(passive).SlotGroup == 1,
            "被动缺class沿原生初始化0，空方向进入转职组而非快捷槽");
        That(Parse([.. passive, new(3, 16), new(0, 0), new(0, 1), new(0, 2)]).SlotGroup == 0 &&
            Parse([.. passive, new(3, 15)]).SlotGroup is null,
            "缺class仍按方向数量分组，显式空class不沿用缺省0");
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
        That(repeatedClass.SlotGroup == 2 && repeatedClass.UnsupportedFields.Contains("skill class") &&
            repeatedClass.SupportsOrdinarySpLearning && repeatedClass.GetOrdinaryResourceSpCost(0, 0) == 7,
            "重复class按首段取分组，重复事实登记诊断且不影响费用和升级资格");
        PvfToken[] repeatedFitness = [new(3, 15), new(0, 0), new(3, 16), new(0, 0), new(3, 16), new(0, 1)];
        That(Parse([.. active, .. repeatedFitness]).SlotGroup == 3 && Parse([.. active, .. repeatedFitness]).SupportsOrdinarySpLearning,
            "重复方向元数据不影响主动学习和主动组");
        That(Parse([.. passive, .. repeatedFitness]).SlotGroup == 1 && Parse([.. passive, .. repeatedFitness]).SkillFitnessGrowtypeCount == 1 &&
            Parse([.. passive, .. repeatedFitness]).SupportsOrdinarySpLearning,
            "重复方向元数据的被动按首段一项归转职组，后续同名段不覆盖");
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
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        That(catalog.ActiveExpertContractLevels(1001, 1000) == catalog.ExpertContractLevels &&
            catalog.ActiveExpertContractLevels(1000, 1000) == 0 && catalog.ActiveExpertContractLevels(0, 1000) == 0,
            "目录只在契约有效时提供显式装配的资源加成，到期和无契约均为零");
        var ghost = catalog.Get(0, 5);
        var ghostNight = catalog.Get(0, 296);
        That(ghostNight.Type == SkillType.Passive && ghostNight.SkillClass == 0 && ghostNight.SlotGroup == 1 &&
            ghostNight.SupportsOrdinarySpLearning && ghostNight.GetMaximum(5) == 0 && ghostNight.GetMaximum(0x11) == 0 &&
            ghostNight.GetMaximum(0x15) == 30 && ghostNight.GetMaximum(0x25) == 30 &&
            ghostNight.GetOrdinaryNextRequiredCharacterLevel(0) == 48 && ghostNight.GetOrdinaryResourceSpCost(0, 5) == 45 &&
            catalog.FreeSkills(0, 0x15).All(skill => skill.Id != 296),
            "真实鬼夜缺class使用转职被动组，仍受剑影觉醒、Lv48与45SP资格限制且非免费赠技");
        That(ghost.Type == SkillType.Active && ghost.SlotGroup == 3 && ghost.SkillClass == 3 && ghost.SkillFitnessGrowtypeCount == 6,
            "真实鬼斩保留主动类型、分组及方向数量");
        foreach (ushort passiveId in new ushort[] { 174, 179, 511 })
            That(catalog.Get(0, passiveId).Type == SkillType.Passive && catalog.Get(0, passiveId).SlotGroup == 2,
                "真实基础被动资源分类 " + passiveId);
        That(catalog.Get(0, 113).Type == SkillType.Active && catalog.Get(0, 113).SlotGroup == 3 &&
            catalog.Get(0, 113).SupportsOrdinarySpLearning && catalog.Get(0, 113).UnsupportedFields.Contains("skill fitness growtype"),
            "真实资源的重复分组声明不连带拒绝原有主动学习字段");
        That(catalog.Get(0, 239) is { Type: SkillType.Passive, SlotGroup: 1 } swordGod &&
            swordGod.SkillFitnessGrowtypeCount == 0 && swordGod.SupportsPointLearning &&
            swordGod.GetOrdinaryResourceSpCost(0, 1) == 80,
            "真实极·神剑术首段方向为空归转职组，二觉被动可分配新槽");
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
        That(catalog.Get(0, 8).DefaultCommand == "0C04" && catalog.Get(0, 5).DefaultCommand == "0904" &&
            catalog.Get(0, 46).DefaultCommand == "04" && catalog.Get(0, 1).DefaultCommand == "010105" &&
            catalog.Get(0, 3).DefaultCommand == "010008" && catalog.Get(0, 169).DefaultCommand == "" &&
            !catalog.Get(0, 169).CommandCustomizing,
            "三段斩/鬼斩按住方向、上挑Z、顺序攻击/BUFF取真实资源，禁止自定义的固有输入保持");
        That(catalog.Get(4, 56).DefaultCommand == "0C05" && catalog.Get(4, 57).DefaultCommand == "0A05" &&
            catalog.Get(4, 58).DefaultCommand == "0905" && catalog.Get(4, 3).DefaultCommand == "010106",
            "客户端线上 X(ATTACK)=5、C(JUMP)=6：蓝拳俯冲系以攻击键结束，摆动以跳跃键结束");
        string[] commandLists = ["swordmanskill.lst", "fighterskill.lst", "gunnerskill.lst", "mageskill.lst", "priestskill.lst",
            "atgunnerskill.lst", "thiefskill.lst", "atfighterskill.lst", "atmageskill.lst", "dsswordmanskill.lst",
            "creatormageskill.lst", "atswordmanskill.lst", "knight.lst", "demoniclancerskill.lst", "atpriestskill.lst", "gunbladerskill.lst"];
        for (byte job = 0; job < commandLists.Length; job++)
        {
            var commands = PvfScript.List(pvf.ReadText("skill/" + commandLists[job])).Keys
                .Select(id => catalog.Get(job, checked((ushort)id))).Where(skill => skill.DefaultCommand.Length > 0).ToArray();
            That(commands.All(skill => skill.Type == SkillType.Active && skill.CommandCustomizing &&
                Convert.FromHexString(skill.DefaultCommand).Length is >= 1 and <= 5), "职业默认指令只投影可安装的主动技能 " + job);
            int held = commands.Count(skill => Convert.FromHexString(skill.DefaultCommand).Any(key => key is >= 9 and <= 12));
            Console.WriteLine($"PASS 默认指令资源 job={job} commands={commands.Length} held={held}");
        }
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
