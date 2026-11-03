using System.Text.RegularExpressions;
using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class ItemModelTests
{
    public static void Run()
    {
        // 对照既有行首字段合同，覆盖 CRLF、空字段、重复标签和不完整边界。
        string[] parts = ["", "\n", "\r\n", "[name]", "[NaMe]", "[price] 0", "[/name]", "[broken", "[]", " [name]", "`text`", "  \t", "[name]\r\n`甲`", "\r"];
        var random = new Random(17);
        for (int i = 0; i < 1000; i++)
        {
            string text = string.Concat(Enumerable.Range(0, 20).Select(_ => parts[random.Next(parts.Length)]));
            var document = new PvfDocument(text);
            foreach (string tag in new[] { "name", "price", "/name", "missing" })
            {
                bool has = Regex.Matches(text, @"(?m)^\[([^\]\r\n]+)\]")
                    .Any(match => string.Equals(match.Groups[1].Value, tag, StringComparison.OrdinalIgnoreCase));
                string expected = has ? Regex.Match(text, @"^\[" + Regex.Escape(tag) + @"\](.*?)(?=\r?\n\[|\z)",
                    RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value.TrimStart() : "";
                That(document.Has(tag) == has && document.Section(tag) == expected, "indexed field preserves line and whitespace semantics");
            }
        }
        var parent = new PvfDocument("[name]\n`parent`\n[price]\n20\n[items]\n1\n[/items]");
        var categories = new PvfDocument("[item category]\n`random clear avatar`\n[/item category]\n[item category]\n`clear avatar`\n[/item category]");
        That(categories.ContainsString("item category", "clear avatar") &&
            !new PvfDocument("[item category]\n`random clear avatar`").ContainsString("item category", "clear avatar"),
            "分类精确匹配并保留重复声明，随机属性装扮不是透明克隆");
        That(!new PvfDocument("[item category]\n", categories).ContainsString("item category", "clear avatar") &&
            new PvfDocument("[name]\n`child`", categories).ContainsString("item category", "clear avatar"),
            "分类沿有效导入层查询，显式空声明不继承父分类");
        var child = new PvfDocument("[name]\n\n[price]\n0\n[items]\n[/items]", parent);
        That(child.Has("items") && child.Section("name") == "" && child.Section("price") == "0", "local empty and zero override parent");
        That(child.Block("items").Trim() == "" && parent.Block("items").Trim() == "1", "empty block shadows without mutating parent");
        That(child.TryGetInt32("price", out int zero) && zero == 0, "strict integer preserves explicit zero override");
        var subtypeParent = new PvfDocument("[sub type]\n4");
        That(new PvfDocument("[name]\n`child`", subtypeParent).TryGetInt32("sub type", out int inherited) && inherited == 4,
            "strict integer inherits the nearest declared parent");
        foreach (string value in new[] { "", "4 19", "4.0", "`4`", "2147483648", "4\n[sub type]\n4" })
            That(!new PvfDocument("[sub type]\n" + value, subtypeParent).TryGetInt32("sub type", out _),
                "empty ambiguous or invalid integer cannot fall back to parent");
        That(!new PvfDocument("").TryGetInt32("sub type", out _), "missing strict integer stays unknown");
        var stringParent = new PvfDocument("[item group name]\n`lt coat`");
        That(new PvfDocument("", stringParent).TryGetString("item group name", out string group) && group == "lt coat",
            "字符串字段沿有效导入继承，保留中间空格");
        foreach (string value in new[] { "", "lt coat", "`lt coat` 1", "`lt coat` `cl coat`", "`lt coat`\n[item group name]\n`cl coat`" })
            That(!new PvfDocument("[item group name]\n" + value, stringParent).TryGetString("item group name", out _),
                "空值、非字符串、尾随token与重复声明不能借父模板补成有效字符串");
        Reject<InvalidDataException>(() => new PvfDocument("[x]1[/x]\n[x]2[/x]").Block("x"));
        var scripts = new Dictionary<string, string>
        {
            ["equipment/base.equ"] = "[price]\n50\n[name]\n`base`",
            ["equipment/child.equ"] = "[price]\n0\n[import script]\n`./BASE.equ`"
        };
        string Read(string path) => scripts.TryGetValue(path, out var text) ? text : throw new FileNotFoundException(path);
        var effective = EquipmentFile.ReadScript(Read, "Equipment/child.equ");
        That(effective.Section("price") == "0" && effective.Section("name") == "`base`", "normalized root-relative import and child priority");
        foreach (string reference in new[] { "`child.equ`", "`missing.equ`", "", "`base.equ` `base.equ`", "`base.equ`\n[import script]\n`base.equ`" })
        {
            scripts["equipment/child.equ"] = "[import script]\n" + reference;
            Reject<InvalidDataException>(() => EquipmentFile.ReadScript(Read, "equipment/child.equ"));
        }
        scripts["equipment/child.equ"] = "[import script]\n`base.equ`";
        scripts["equipment/base.equ"] = "[import script]\n`child.equ`";
        Reject<InvalidDataException>(() => EquipmentFile.ReadScript(Read, "equipment/child.equ"));

        string[] strings = ["[price]", "[stack limit]", "[stackable type]", "[material]", "fixture.stk", "../outside.stk", "[equipment type]", "[weapon]", "[waste]"];
        string Resolve(int value) => strings[value];
        var equipment = EquipmentFile.Parse(1, "equipment/a.equ", [new(3, 0), new(0, 0), new(3, 6), new(6, 7), new(0, 19)], Resolve);
        That(equipment.DeclaredPrice == 0 && equipment.DeclaredEquipmentType == new EquipmentTypeDeclaration("[weapon]", 19), "equipment raw zero and type parameter");
        That(equipment.DeclaredMinimumLevel is null && equipment.UnsupportedFields.Contains("minimum level"), "missing fields stay distinct from default values");
        equipment = EquipmentFile.Parse(1, "equipment/a.equ", [new(3, 0), new(0, 1), new(3, 0), new(0, 2)], Resolve);
        That(equipment.DeclaredPrice is null && equipment.UnsupportedFields.Count(x => x == "price") == 1, "duplicate raw declaration is unsupported");
        var stackable = StackableFile.Parse(0, "stackable/a.stk", [new(3, 1), new(0, 0), new(3, 2), new(6, 3), new(0, -1), new(0, 2)], Resolve);
        That(stackable.DeclaredStackLimit == 0 && stackable.TypeDeclaration!.Parameters.SequenceEqual(new[] { -1, 2 }), "raw type parameters have no invented nonnegative constraint");
        stackable = StackableFile.Parse(1, "stackable/a.stk",
            [new(3,2),new(6,3),new(0,0),new(3,2),new(6,8),new(0,1)], Resolve);
        That(stackable.TypeDeclaration is { Label: "[waste]", Parameters: [1] } &&
            !stackable.UnsupportedFields.Contains("stackable type"), "重复堆叠类型按客户端取最后完整声明，与模板分页一致");
        foreach (PvfToken[] tail in new PvfToken[][] { [], [new(6,8)], [new(0,1)], [new(6,8),new(0,1),new(6,3)] })
            That(StackableFile.Parse(1, "stackable/a.stk", [new(3,2),new(6,3),new(0,0),new(3,2),..tail], Resolve)
                .TypeDeclaration is null, "损坏的末类型不得回退成前一个合法声明");
        That(StackableFile.Parse(1, "stackable/a.stk", [new(3,2),new(6,3),new(3,2),new(6,8),new(0,1)], Resolve)
            .TypeDeclaration is null, "损坏的前类型也不得被末声明悄悄掩盖");
        foreach (int count in new[] { 0, 1, 7 })
            That(StackableTradeLimitDeclaration.Read(new($"[trade limit]\n{count} `[account]`\n")) == new StackableTradeLimitDeclaration(count, "[account]"),
                "限次交易声明保留次数和后续账号绑定标签，零次数不是缺省值");
        That(StackableTradeLimitDeclaration.Read(new("[trade limit]\n1 `[trade]`\n"))?.Attachment == "[trade]",
            "原始解析保留角色绑定标签，不自动解释为账号绑定");
        foreach (string declaration in new[] { "", "1", "-1 `[account]`", "2147483648 `[account]`", "1 `[account]` 0", "1 `[account]`\n[trade limit]\n1 `[account]`" })
            That(StackableTradeLimitDeclaration.Read(new("[trade limit]\n" + declaration)) is null,
                "空值、缺少绑定标签、负数、溢出、多余参数及重复声明不能成为有效交易限制");
        That(StackableTradeLimitDeclaration.Read(new("[name]\n`fixture`")) is null, "没有交易限制声明时不创造绑定规则");
        Reject<InvalidDataException>(() => StackableFile.Parse(1, "stackable/a.stk", [new(3, 0), new(0, -1)], Resolve));
        That(EquipmentFile.Parse(1, "equipment/a.equ", [new(3, 0), new(0, -1)], Resolve).DeclaredPrice is null, "equipment unsupported scalar remains nonfatal");
        That(PvfItemIndex.Stackable([new(0, 0), new(6, 4)], Resolve, _ => true)[0] == "stackable/fixture.stk", "stackable zero identity is retained");
        string[] ticketStrings = ["[action type]","[quest clear]","[maximum level]","[use action packet]","[usable job]","[all]","[/action type]","[/usable job]","[any quest clear]",
            "[action usable place]","[village]","[/action usable place]","[seria room]"];
        string TicketResolve(int value) => ticketStrings[value];
        var ticket = StackableFile.Parse(100,"stackable/ticket.stk",
            [new(3,0),new(6,1),new(3,6),new(3,2),new(0,84),new(3,3),new(0,0),new(3,4),new(6,5),new(3,7)],TicketResolve);
        That(ticket.ActionDeclaration is { Label: "[quest clear]", Parameters.Count: 0 } && ticket.DeclaredMaximumLevel == 84 &&
            ticket.HasMaximumLevel && ticket.DeclaredUseActionPacket == 0 && ticket.HasUseActionPacket &&
            ticket.DeclaredUsableJobs!.SequenceEqual(new[] { "[all]" }),"ticket declarations preserve empty action arguments, explicit zero and job list");
        ticket = StackableFile.Parse(100,"stackable/ticket.stk",[new(3,0),new(6,8),new(0,674),new(0,649),new(3,6)],TicketResolve);
        That(ticket.ActionDeclaration!.Parameters.SequenceEqual(new[] { 674,649 }) && !ticket.HasMaximumLevel && ticket.DeclaredMaximumLevel is null,
            "ticket raw parameters and missing maximum remain distinct");
        ticket = StackableFile.Parse(100,"stackable/ticket.stk",[new(3,2),new(0,84),new(0,85),new(3,4),new(0,1)],TicketResolve);
        That(ticket.HasMaximumLevel && ticket.DeclaredMaximumLevel is null && ticket.HasUsableJobs && ticket.DeclaredUsableJobs is null,
            "malformed ticket bounds and job tokens cannot become absent defaults");
        ticket = StackableFile.Parse(100,"stackable/ticket.stk",[new(3,9),new(6,10),new(3,11)],TicketResolve);
        That(ticket.HasActionUsablePlace && ticket.DeclaredActionUsablePlaces!.SequenceEqual(new[] { "[village]" }),
            "ticket raw village declaration is retained");
        ticket = StackableFile.Parse(100,"stackable/ticket.stk",
            [new(3,9),new(6,10),new(6,12),new(3,11),new(3,9),new(6,12),new(6,10),new(3,11)],TicketResolve);
        That(ticket.DeclaredActionUsablePlaces!.SequenceEqual(new[] { "[village]", "[seria room]", "[seria room]", "[village]" }) &&
            !ticket.UnsupportedFields.Contains("action usable place"), "重复使用地点按原顺序追加而非因数量或重复拒绝");
        string NestedResolve(int index) => index switch { 0 => "[stackable type]", 1 => "[waste]",
            2 => "[booster info]", 3 => "[/booster info]", _ => throw new ArgumentOutOfRangeException(nameof(index)) };
        That(StackableFile.Parse(1,"stackable/a.stk",
            [new(3,0),new(6,1),new(0,0),new(3,2),new(3,0),new(6,1),new(0,0),new(3,3)], NestedResolve)
            .TypeDeclaration is null, "嵌套块中的堆叠类型不得覆盖根级声明");
        ticket = StackableFile.Parse(100,"stackable/ticket.stk",[],TicketResolve);
        That(!ticket.HasActionUsablePlace && ticket.DeclaredActionUsablePlaces is null,"missing usable place has no implicit village restriction");
        ticket = StackableFile.Parse(100,"stackable/ticket.stk",[new(3,9),new(0,1),new(6,10)],TicketResolve);
        That(ticket.HasActionUsablePlace && ticket.DeclaredActionUsablePlaces is { Count: 0 },
            "非字符串终止当前地点列表，不升级成资源拒绝");
        ticket = StackableFile.Parse(100,"stackable/ticket.stk",
            [new(3,9),new(6,10),new(3,11),new(3,9),new(6,12),new(3,11)],TicketResolve);
        That(ticket.DeclaredActionUsablePlaces!.SequenceEqual(new[] { "[village]", "[seria room]" }),
            "不同地点的重复声明按资源顺序追加");
        string[] qualificationStrings = ["[action usable condition]", "[guildmember]", "[/action usable condition]",
            "[guildmaster]", "[privatePvpChar]", "[usable world]", "[common]", "[fair]", "[/usable world]", "[unknown]"];
        string QualificationResolve(int index) => qualificationStrings[index];
        PvfToken[] qualificationTokens = [new(3,0),new(6,1),new(6,9),new(3,2),new(3,0),new(6,3),new(6,4),new(3,2),
            new(3,5),new(6,6),new(3,8),new(3,5),new(6,7),new(3,8)];
        var qualifications = StackableFile.Parse(100,"stackable/use.stk",qualificationTokens,QualificationResolve);
        That(qualifications.DeclaredActionUsableConditions!.SequenceEqual(new[] { "[guildmember]", "[unknown]", "[guildmaster]", "[privatePvpChar]" }) &&
            qualifications.DeclaredUsableWorlds!.SequenceEqual(new[] { "[fair]" }),
            "条件重复段追加且保留未知原值，世界重复段先清空再读取最后一段");
        That(ItemUseFields.ActionConditions([new(3,0),new(0,1),new(6,1)],QualificationResolve) is { Count: 0 } &&
            ItemUseFields.UsableWorlds([],QualificationResolve) is null &&
            ItemUseFields.UsableWorlds([new(3,5),new(0,1)],QualificationResolve) is { Count: 0 },
            "非字符串结束当前声明，不把缺席、空声明或声明数量升级为解析失败");
        var worldParent = new PvfDocument("[usable world]\n`[fair]`\n[/usable world]");
        That(ItemUseFields.UsableWorlds(new PvfDocument("[name]\n`child`",worldParent))!.SequenceEqual(new[] { "[fair]" }) &&
            ItemUseFields.UsableWorlds(new PvfDocument("[usable world]\n[/usable world]",worldParent)) is { Count: 0 } &&
            ItemUseFields.UsableWorlds(new PvfDocument("[usable world]\n`[common]`\n[/usable world]\n[usable world]\n`[fair]` 1 `[common]`\n[/usable world]"))!
                .SequenceEqual(new[] { "[fair]" }),
            "世界读取保留装备父模板继承、子层显式清空及最后声明的连续字符串边界");
        That(RecoveryConsumableFile.Parse(100,"stackable/use.stk",qualificationTokens,QualificationResolve).Diagnostics.Length == 0,
            "公共使用声明不被恢复物品的局部重复字段守卫再次拒绝");
        That(ExperienceConsumableFile.Parse(100,"stackable/use.stk",qualificationTokens,QualificationResolve).Diagnostics.IsEmpty,
            "公共使用条件追加、物品世界覆盖不被经验物品的局部重复字段守卫拒绝");
        That(ExperienceConsumableFile.Parse(100,"stackable/use.stk",
            [new(3,0),new(6,1),new(3,0),new(6,2)],
            index=>new[] {"[action usable place]","[village]","[dungeon]"}[index]).Diagnostics.IsEmpty,
            "经验物品允许重复地点段，地点并集只由统一规则解释");
        foreach (string field in new[] { "action usable server type", "usable game server type", "usable server type" })
        {
            string[] names = ["[" + field + "]", "[/" + field + "]", "[event server]"];
            string ServerResolve(int index) => names[index];
            PvfToken[] declarations = [new(3,0),new(6,2),new(3,1),new(3,0),new(0,-1),new(3,1)];
            var recovery = RecoveryConsumableFile.Parse(100,"stackable/use.stk",declarations,ServerResolve);
            var serverExperience = ExperienceConsumableFile.Parse(100,"stackable/use.stk",declarations,ServerResolve);
            That(ItemUseFields.IsSharedTag(field) && recovery.DeclaredFields.Contains(field) &&
                recovery.Diagnostics.IsEmpty && serverExperience.Diagnostics.IsEmpty &&
                !recovery.Stackable.UnsupportedFields.Contains(field),
                "服务器限用声明保留元数据，专用服和重复异值不成为局部解析拒绝：" + field);
        }
        Reject<InvalidDataException>(() => PvfItemIndex.Equipment([new(0, 0), new(6, 4)], Resolve, _ => true));
        string[] classStrings = ["[need material]", "[/need material]", "[dont change grow type]", "[/dont change grow type]",
            "[usable first growtype]", "[/usable first growtype]", "[action usable server type]", "[general server]",
            "[/action usable server type]", "[class change grow type check off]"];
        string ClassResolve(int value) => classStrings[value];
        var change = StackableFile.Parse(1, "stackable/change.stk",
            [new(3,0),new(0,1000),new(0,2),new(3,1),new(3,2),new(0,6),new(0,4),new(3,3),
                new(3,4),new(0,3),new(0,4),new(0,5),new(3,5),new(3,6),new(6,7),new(3,8),new(3,9)], ClassResolve);
        That(change.DeclaredNeededMaterials!.SequenceEqual(new[] { 1000,2 }) &&
            change.DeclaredDontChangeGrowTypes!.SequenceEqual(new[] { 6,4 }) &&
            change.DeclaredUsableFirstGrowTypes!.SequenceEqual(new[] { 3,4,5 }) && change.ClassChangeGrowTypeCheckOff &&
            change.DeclaredActionUsableServerTypes!.SequenceEqual(new[] { "[general server]" }),
            "class change raw pairs, source branches, server labels and empty flag are retained");
        change = StackableFile.Parse(1,"stackable/change.stk",[new(3,0),new(0,1),new(0,-2),new(3,1)],ClassResolve);
        That(change.DeclaredNeededMaterials!.SequenceEqual(new[] { 1,-2 }), "raw material values are not interpreted as business costs");
        change = StackableFile.Parse(1,"stackable/change.stk",[new(3,0),new(0,1),new(0,2),new(3,1),new(3,0),new(0,3),new(0,4)],ClassResolve);
        That(change.DeclaredNeededMaterials is null, "duplicate class change materials are not silently overwritten");
        change = StackableFile.Parse(1,"stackable/change.stk",[new(3,9),new(0,0)],ClassResolve);
        That(change.UnsupportedFields.Contains("class change grow type check off"), "class change flag with payload is not a valid empty flag");
        string[] creatureStrings = ["[stackable action limit indexes]", "[/stackable action limit indexes]",
            "[stackable action result limit indexes]", "[/stackable action result limit indexes]", "[creature minimum level]"];
        string CreatureResolve(int value) => creatureStrings[value];
        var creature = StackableFile.Parse(1, "stackable/change.stk",
            [new(3,0),new(0,7001),new(3,1),new(3,2),new(0,0),new(0,7002),new(0,10000),
                new(0,0),new(0,7002),new(0,2),new(3,3),new(3,4),new(0,16)], CreatureResolve);
        That(creature.DeclaredActionLimitIndexes!.SequenceEqual(new[] { 7001 }) &&
            creature.DeclaredActionResultLimitIndexes!.SequenceEqual(new[] { 0,7002,10000,0,7002,2 }) &&
            creature.HasCreatureMinimumLevel && creature.DeclaredCreatureMinimumLevel == 16,
            "creature conversion retains raw triples, order, duplicate candidates and independent minimum level");
        foreach (var tokens in new PvfToken[][]
        {
            [new(3,0),new(6,1)],
            [new(3,0),new(0,7001),new(3,1),new(3,0),new(0,7002)]
        })
            That(StackableFile.Parse(1, "stackable/change.stk", tokens, CreatureResolve).DeclaredActionLimitIndexes is null,
                "malformed or duplicate creature source restrictions cannot become an absent whitelist");
        creature = StackableFile.Parse(1, "stackable/change.stk", [], CreatureResolve);
        That(creature.DeclaredActionLimitIndexes is { Count: 0 } && creature.DeclaredActionResultLimitIndexes is { Count: 0 } &&
            !creature.HasCreatureMinimumLevel, "absent conversion declarations remain distinct from invalid declarations");
        creature = StackableFile.Parse(1, "stackable/change.stk", [new(3,4),new(0,16),new(0,17)], CreatureResolve);
        That(creature.HasCreatureMinimumLevel && creature.DeclaredCreatureMinimumLevel is null,
            "malformed creature minimum level cannot use the missing-field default");
        string LevelResolve(int value) => value == 0 ? "[levelup range]" : "[booster info]";
        var level = StackableFile.Parse(1,"stackable/level.stk",[new(3,0),new(0,1)],LevelResolve);
        That(level.DeclaredLevelUpRange == 1 && !level.HasBoosterInfo,"level ticket preserves its explicit increment");
        foreach (var tokens in new PvfToken[][] { [],[new(3,0),new(0,-1)],[new(3,0),new(0,1),new(0,2)],
            [new(3,0),new(0,1),new(3,0),new(0,1)] })
            That(StackableFile.Parse(1,"stackable/level.stk",tokens,LevelResolve).DeclaredLevelUpRange is null,
                "missing, negative, compound and duplicate increments never default to one");
        That(StackableFile.Parse(1,"stackable/level.stk",[new(3,1)],LevelResolve).HasBoosterInfo,
            "random booster declaration remains distinguishable from a fixed ticket");
        Reject<InvalidDataException>(() => PvfItemIndex.Stackable([new(0, 1), new(6, 5)], Resolve, _ => true));
        string[] experienceStrings = ["[increase status type]", "[expPercentUp]", "[/increase status type]", "[expUp]", "[levelUpByTable]"];
        string[] etcStrings = ["[equipment index]", "[/equipment index]", "defaultEquipment", "[key value index]", "[/key value index]", "other"];
        string EtcResolve(int index) => etcStrings[index];
        That(CharacterEquipmentFile.ReadDefaultEquipment([new(3,0),new(6,2),new(0,-1),new(0,101),new(0,102),new(3,1),
            new(3,0),new(6,5),new(0,999),new(3,1)],EtcResolve).SequenceEqual(new[] { -1,101,102 }),
            "默认装备旧顺序表保留整数与空外观，不混入其它命名表");
        That(CharacterEquipmentFile.ReadDefaultEquipment([new(3,3),new(6,2),new(0,-1),new(0,0),new(0,-1),
            new(0,1),new(0,101),new(0,11),new(0,300),new(3,4)],EtcResolve).SequenceEqual(new[] { -1,101,300 }),
            "默认装备键值表只返回EQU值，缺省值及键不充当装备编号");
        foreach (var invalid in new PvfToken[][]
        {
            [new(3,0),new(6,2),new(6,5),new(3,1)],
            [new(3,3),new(6,2),new(0,-1),new(0,0),new(3,4)],
            [new(3,3),new(6,2),new(0,-1),new(0,1),new(0,101),new(0,1),new(0,102),new(3,4)]
        }) Reject<InvalidDataException>(()=>CharacterEquipmentFile.ReadDefaultEquipment(invalid,EtcResolve));
        PvfToken[] awakeningTokens = new[] { 13, 1, 14, 1 }.Concat(Enumerable.Range(100, 12))
            .Select(value => new PvfToken(0, value)).ToArray();
        var awakened = CharacterEquipmentFile.ReadAwakeningEquipment(awakeningTokens);
        That(awakened is [{ Job: 14, GrowType: 1, Equipment.Length: 13 }] && awakened[0].Equipment[9] == 109 &&
            awakened[0].Equipment[10] == -1 && awakened[0].Equipment[11] == 110 && awakened[0].Equipment[12] == 111,
            "觉醒默认表跳过武器装扮槽10，尾部装备不左移");
        foreach (var invalid in new[] { awakeningTokens[..^1], awakeningTokens.Append(new PvfToken(0, 1)).ToArray(),
            awakeningTokens.Select((token, at) => at == 0 ? new PvfToken(0, 12) : token).ToArray(),
            awakeningTokens.Select((token, at) => at == 4 ? new PvfToken(0, -2) : token).ToArray(),
            awakeningTokens.Select((token, at) => at == 4 ? new PvfToken(6, 100) : token).ToArray(),
            (PvfToken[])[new(0, 13), new(0, 2), .. awakeningTokens[2..], .. awakeningTokens[2..]] })
            Reject<InvalidDataException>(() => CharacterEquipmentFile.ReadAwakeningEquipment(invalid));
        string ExperienceResolve(int index) => experienceStrings[index];
        PvfToken[] capsule = [new(3, 0), new(6, 1), new(0, 10), new(3, 2)];
        var experience = ExperienceConsumableFile.Parse(1, "stackable/capsule.stk", capsule, ExperienceResolve);
        That(experience.Effect is { Label: "[expPercentUp]", Parameters: [10] } && experience.Diagnostics.IsEmpty,
            "经验胶囊保留百分比标签和原始整数，不在资源库内计算角色经验");
        PvfToken[] table = [new(3,0),new(6,4),new(0,1),new(0,28),new(0,80),new(0,29),new(0,49),new(0,81),new(3,2)];
        That(ExperienceConsumableFile.Parse(1,"stackable/table.stk",table,ExperienceResolve) is
            { Effect.Parameters: [1,28,80,29,49,81], Diagnostics.IsEmpty: true },
            "等级表保留完整三元组、声明顺序及闭区间，不在解析层选择角色目标");
        foreach (var invalid in new[] { table.Concat(table).ToArray(), table.Where((_,index)=>index!=3).ToArray(),
            table.Select((token,index)=>index==3 ? new PvfToken(6,1) : token).ToArray() })
            That(!ExperienceConsumableFile.Parse(1,"stackable/table.stk",invalid,ExperienceResolve).Diagnostics.IsEmpty,
                "重复、残缺和非整数等级表不能成为可用增量");
        foreach (PvfToken[] invalid in new[] { capsule.Concat(capsule).ToArray(), capsule[..^2].Append(new PvfToken(3, 2)).ToArray(),
            capsule.Take(3).Append(new PvfToken(0, 20)).Append(new PvfToken(3, 2)).ToArray() })
            That(!ExperienceConsumableFile.Parse(1, "stackable/capsule.stk", invalid, ExperienceResolve).Diagnostics.IsEmpty,
                "重复、缺参数和多参数经验声明不能降级为单次增量");
        Reject<InvalidDataException>(() => PvfItemIndex.Stackable([new(0, 1), new(6, 4)], Resolve, _ => false));
    }
}
