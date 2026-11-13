using PvfLib;

internal static class AiPrograms
{
    // 原生矩形查询会转为有符号坐标；使用有限房间包围范围，不传无穷或溢出值。
    internal const float RoomRange = 1048576;
    internal static bool HasBasicAttack(TemplateDefinition definition) => definition.Id is 20900 or 9986;
    internal static List<ScriptToken> BasicAttack() =>
        [ScriptToken.Tag("[input]"), ScriptToken.String("target current direction"), ScriptToken.Int(30), ScriptToken.String("time"), ScriptToken.Int(30),
         ScriptToken.Tag("[input]"), ScriptToken.String("x"), ScriptToken.Int(30), ScriptToken.String("time"), ScriptToken.Int(300)];
    // 专用负选择器只在成功分支使用目标；未闭合的能力仍阻止写包。
    internal static Dictionary<string, List<ScriptToken>> Action(TemplateDefinition definition, bool includeBasicAttack = false,
        IReadOnlyDictionary<int, int>? skillOrdinals = null)
    {
        bool revised = skillOrdinals is not null;
        var keys = skillOrdinals ?? definition.Active.Select((id, ordinal) => (id, ordinal)).ToDictionary(pair => pair.id, pair => pair.ordinal);
        var rules = new List<Func<List<ScriptToken>, List<ScriptToken>>>();
        switch (definition.Id)
        {
            case 20900:
                Heal(51, 70); Heal(26, 80); Heal(239, 60);
                Buff(52); Buff(240);
                foreach (int skill in new[] {45,20,19,23,22,21}) Buff(skill);
                Awakening(84); if (!revised) Awakening(238); Small(107);
                break;
            case 9986:
                Field(42);
                Buff(30); Toggle(36);
                if (revised) { Small(46); Small(58); }
                else { Awakening(67); Awakening(26); }
                break;
            case 74990:
                // 开场先补主增益；足够时由同一增益条件跳过，治疗不能无限抢占开场。
                Buff(14); Buff(21);
                Heal(3, 70); Heal(32, 55); Buff(6); Revive(30);
                foreach (int skill in new[] {23,28}) Buff(skill);
                if (keys.ContainsKey(36)) Buff(36);
                if (keys.ContainsKey(91)) Buff(91, battle: true);
                Buff(24, battle: true);
                Awakening(33); if (!revised) Awakening(93);
                if (revised) { Small(16); Small(31); } else { Small(31); Small(16); }
                break;
            case 19973:
                // 核心诅咒先覆盖当前玩家；偏爱选人或动画尚未完成不能无限抢占主BUFF。
                Buff(335); Buff(330, firstOnly: true);
                Heal(367, 70); Heal(339, 50);
                Revive(345); Buff(337, battle: true); Buff(332, battle: true);
                if (keys.ContainsKey(341)) Heal(341, 35);
                Awakening(343); if (!revised) Awakening(350);
                // 两项高阶技能承担控制与续杯辅助，不作为低阶攻击的替代。
                if (keys.ContainsKey(344)) Small(344);
                if (keys.ContainsKey(349)) Small(349);
                Small(333); if (revised) { Small(370); Small(372); }
                break;
            default: throw new InvalidDataException("Unsupported support identity");
        }
        if (!revised && (HasBasicAttack(definition) || includeBasicAttack))
            rules.Add(next => BattleForDefinition(Target(100, Return(definition.Active.Length), next, 50), next));
        var programs = new Dictionary<string, List<ScriptToken>>(StringComparer.Ordinal);
        if (rules.Count is < 1 or > 64) throw new InvalidDataException("Support decision graph exceeds bounded rule count");
        for (int i = 0; i < rules.Count; i++)
        {
            // 分支共用后续资源引用，生成器不复制整条后续链。
            List<ScriptToken> next = i == rules.Count - 1 ? Return(-1) : Import(Name(i + 1));
            var program = Wrap(rules[i](next));
            if (program.Count > 256) throw new InvalidDataException("Support decision rule exceeds bounded token count");
            programs.Add(Name(i), program);
        }
        programs.Add("action.ai", Wrap(Maintained(definition, Import(Name(0)))));
        return programs;

        void Heal(int skill, float threshold)
        {
            foreach (int slot in Enumerable.Range(1, 4))
            {
                int selected = slot;
                rules.Add(next =>
                {
                    var cast = Return(keys[skill]);
                    // 慢愈合有长于冷却的持续治疗；必须读取目标当前有效来源，不能冷却一好便覆盖。
                    if (definition.Id == 20900 && skill == 51)
                        cast = Think("check party buff on", [ScriptToken.Int(4), ScriptToken.Int(51)], next, cast);
                    return Select(selected,
                        Think("party member hp", [ScriptToken.String("%<"), ScriptToken.Float(threshold)],
                            Ready(skill, cast, next), next), next);
                });
            }
        }
        void Buff(int skill, bool battle = false, bool firstOnly = false)
        {
            foreach (int slot in Enumerable.Range(1, firstOnly ? 1 : 4))
            {
                int selected = slot;
                rules.Add(next =>
                {
                    var cast = Return(keys[skill]);
                    if (battle) cast = BattleForDefinition(cast, next);
                    // true 包括已有增益及客户端判定无效；仅有效缺失/到期才进入施法。
                    return Select(selected, Ready(skill, Think("check party buff on", [ScriptToken.Int(JobId(definition.Job)), ScriptToken.Int(skill)], next, cast), next), next);
                });
            }
        }
        void Revive(int skill)
        {
            foreach (int slot in Enumerable.Range(101, 4))
            {
                int selected = slot;
                rules.Add(next => Select(selected, Ready(skill, Return(keys[skill]), next), next));
            }
        }
        void Toggle(int skill) => rules.Add(next =>
            Think("check skill on", [ScriptToken.Int(skill)], next,
                Select(1, Ready(skill, Return(keys[skill]), next), next)));
        void Field(int skill) => rules.Add(next =>
            Think("check skill on", [ScriptToken.Int(skill)], next,
                Select(1, Ready(skill, BattleForDefinition(Return(keys[skill]), next), next), next)));
        void Awakening(int skill) => rules.Add(next => Select(1, Ready(skill,
            BattleForDefinition(revised || HasBasicAttack(definition) ? Return(keys[skill]) : Target(600, Return(keys[skill]), next), next), next), next));
        void Small(int skill) => rules.Add(next => Select(1, Ready(skill, BattleForDefinition(Target(350, Return(keys[skill]), next), next), next), next));
        List<ScriptToken> BattleForDefinition(List<ScriptToken> yes, List<ScriptToken> no) => Battle(yes, no, revised || HasBasicAttack(definition) ? RoomRange : 1200);
        List<ScriptToken> Ready(int skill, List<ScriptToken> yes, List<ScriptToken> no) => Think("check use skill", [ScriptToken.Int(skill)], yes, no);
    }

    static int JobId(string job) => job switch { "priest" => 4, "fighter" => 1, "mage" => 3, "atpriest" => 14, _ => throw new InvalidDataException("Unsupported skill job") };
    static string Name(int index) => "support_rule_" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".ai";
    static List<ScriptToken> Import(string path) => [ScriptToken.Tag("[import ai]"), ScriptToken.String(path)];
    static List<ScriptToken> Wrap(List<ScriptToken> body) => [ScriptToken.Tag("[ai pattern]"), ..body, ScriptToken.Tag("[/ai pattern]")];
    static List<ScriptToken> Return(int value) => [ScriptToken.Tag("[return]"), ScriptToken.Int(value)];
    static List<ScriptToken> Select(int slot, List<ScriptToken> yes, List<ScriptToken> no) => Think("set party member", [ScriptToken.Int(-slot)], yes, no);
    static List<ScriptToken> Think(string condition, List<ScriptToken> args, List<ScriptToken> yes, List<ScriptToken> no) =>
        [ScriptToken.Tag("[think]"), ScriptToken.Tag("[" + condition + "]"), ..args,
         ScriptToken.Tag("[true]"), ..yes, ScriptToken.Tag("[false]"), ..no, ScriptToken.Tag("[/think]")];
    static List<ScriptToken> Target(float range, List<ScriptToken> yes, List<ScriptToken> no, float depth = 100) => Think("void",
        [ScriptToken.String("is target in attack area()"), ScriptToken.Float(range), ScriptToken.Float(range), ScriptToken.Float(depth), ScriptToken.Float(depth)], yes, no);
    static List<ScriptToken> Battle(List<ScriptToken> yes, List<ScriptToken> no, float range = 1200) =>
        [ScriptToken.Tag("[think]"), ScriptToken.Tag("[void]"), ScriptToken.String("enemy number of in attack area()"),
         ScriptToken.Float(range), ScriptToken.Float(range), ScriptToken.Float(range), ScriptToken.Float(range),
         ..Think("last result", [ScriptToken.String(">="), ScriptToken.Float(1)], yes, no), ScriptToken.Tag("[/think]")];

    static List<ScriptToken> Maintained(TemplateDefinition definition, List<ScriptToken> next) => definition.Id switch
    {
        9986 => Think("check skill on", [ScriptToken.Int(42)], Return(-1), next),
        74990 => Think("check skill on", [ScriptToken.Int(24)], Return(-1),
            Think("check skill on", [ScriptToken.Int(32)], Return(-1), next)),
        _ => next
    };

    internal static List<ScriptToken> Escort(PvfArchive source, string path, TemplateDefinition definition)
    {
        // 目的地5的原生对象回调由配套客户端限定为本次真实队员；10实际追敌，不能继续沿用。
        // 跟随与技能各有目标；移动只用原步行寻路，近玩家停止由同次原AI方向钩子完成。
        _ = TemplateDelta.Read(source, path);
        return Path.GetFileName(path).ToLowerInvariant() switch
        {
            "destinationselect.ai" => Wrap(Return(5)),
            "movemethod.ai" => Wrap(Maintained(definition, Return(0))),
            _ => throw new InvalidDataException("Unsupported escort program: " + path)
        };
    }
}
