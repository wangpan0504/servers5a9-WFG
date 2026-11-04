using static DfoServer.Testing.TestCheck;
using System.Collections;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Dungeons;
using DfoServer.Testing;
using PvfLib;

namespace DfoServer;

internal static partial class DungeonRoomCatalogTests
{
    public static void RunCore()
    {
        int checks = Count;
        var fixture = new Fixture();
        foreach (string tag in new[] { "no champion", "is named monster" })
        {
            bool Restricted(params PvfToken[] body) => DungeonRoomCatalog.ParseChampionRestriction(body, id => fixture.Resolve(id));
            var marker = fixture.Tag(tag);
            That(!Restricted() && !Restricted(marker, new(0, 0)) && Restricted(marker, new(0, 1)) &&
                Restricted(marker) && Restricted(marker, new(6, 0)) && Restricted(marker, new(0, 0), new(0, 1)) &&
                Restricted(marker, new(0, 0), marker, new(0, 0)),
                "MOB精英限制尊重非零声明，歧义只禁止强化，不删除原演员：" + tag);
        }
        AltarResources(fixture);
        TournamentApcResources(fixture);
        That(fixture.Parse([fixture.Tag("type"),fixture.String("[boss]")]).DeclaredType == "[boss]" &&
            fixture.Parse(fixture.Monster()).DeclaredType is null,
            "MAP真实类型独立保留，迷宫身份不补造缺失类型");
        That(fixture.Parse([fixture.Tag("type"),fixture.String("[boss]"),fixture.Tag("type"),fixture.String("[boss]")]).DeclaredType is null &&
            fixture.Parse([fixture.Tag("type"),fixture.String("[boss]"),new(0,1)]).DeclaredType is null,
            "重复或多值MAP类型不能授予末层终点资格");
        var npcTokens = fixture.Block("NPC", [new(0,1000),fixture.String("[left]"),new(0,0),new(0,0),new(0,0),
            new(0,1298),fixture.String("[left]"),new(0,485),new(0,284),new(0,0)]);
        var npcRoom = fixture.Parse(npcTokens);
        That(npcRoom.NpcMetadataError is null && npcRoom.Npcs.Select(row => row.Id).SequenceEqual([1000,1298]),
            "地图NPC投影保留不同演员的完整来源，不用位置字段匹配任务NPC");
        var unresolvedNpc = fixture.Parse([.. fixture.Monster(),.. npcTokens[..^1]]);
        That(unresolvedNpc.NpcMetadataError is not null && unresolvedNpc.Npcs.Count == 0 && unresolvedNpc.Monsters.Count == 1,
            "未闭合NPC不授予会面来源，同时保留原可用怪物与入场能力");
        var apcRows = fixture.Block("ai character", [
            new(0,6001),new(0,410),new(0,308),new(0,0),fixture.String("[character]"),fixture.String("[normal]"),new(0,0),new(0,0),
            new(0,6000),new(0,1),new(0,2),new(0,0),fixture.String("[character]"),fixture.String("[normal]"),new(0,0),new(0,0)]);
        DungeonRoomAiCharacter? ResolveApc(PvfAiCharacterSpawn row) => row.Values[0] == 6000
            ? new(6000,row.Values,row.Faction,row.Rank,"aicharacter/fixture.aic",32) : null;
        var skippedApc = fixture.Parse([.. fixture.Monster(),.. apcRows],resolveAiCharacter: ResolveApc);
        That(skippedApc.AiCharacters is [{ Id: 6000, Faction: "[character]", RankKind: "[normal]", Level: 32 }] &&
            skippedApc.Monsters.Count == 1,
            "目录缺失的APC不阻断房间，已有定义仍生成");
        Reject(apcRows, "无APC解析器时仍拒绝，不把缺依赖当成跳过");
        var positions = fixture.Parse(fixture.Block("event monster position", [new(0, 330), new(0, -93), new(0, 0)]));
        That(positions.EventMonsterPositions.SequenceEqual([330, -93, 0]), "事件怪物位置保留三坐标与负值");
        var special = fixture.Block("special passive object", [new(0, 3), new(0, -20), new(0, 150), new(0, 0), new(0, 2),
            fixture.String("[trap]"), new(0, 40002), new(0, 40), new(0, 1), new(0, 10), new(0, -1),
            fixture.String("[monster]"), new(0, 1), new(0, 5), new(0, 3), new(0, 10), new(0, -1)]);
        var parsedSpecial = fixture.Parse(special).SpecialObjects;
        var emptySpecial = fixture.Block("special passive object", [
            new(0,0),new(0,1),new(0,-1),new(0,-1),new(0,0),
            .. special[1..^1],new(0,0),new(0,1),new(0,-1),new(0,-1),new(0,0)]);
        var emptyRoom = fixture.Parse([.. fixture.Monster(),.. emptySpecial]);
        That(emptyRoom.SpecialObjects is [{ IsEmptyPlaceholder: true, SourceOrdinal: 0 },
                { IsEmptyPlaceholder: false, SourceOrdinal: 1 },{ IsEmptyPlaceholder: true, SourceOrdinal: 2 }],
            "空特殊父行保留原序号，不能压缩后续真实父子关联");
        var emptyPlan = DungeonMonsterPlan.FromRoom(emptyRoom,99,10,5);
        That(emptyPlan.SpecialObjects is [{ ObjectKey: 11, Source.SourceOrdinal: 1 }] &&
            emptyPlan.Monsters.Count == 2 && emptyPlan.Monsters[1].Source!.SpecialParent!.SourceOrdinal == 1 &&
            emptyPlan.NextObjectKey == 13,
            "空父行不占key或生成type9，真实父子及普通怪仍完整生成");
        var zeroObjectPaths = new Dictionary<int,string> { [0] = "passiveobject/zero.obj" };
        var zeroObject = fixture.Block("special passive object", [new(0,0),new(0,1),new(0,-1),new(0,-1),new(0,0)]);
        var registeredZero = fixture.Parse(zeroObject,objectPaths:zeroObjectPaths);
        That(registeredZero.SpecialObjects is [{ IsEmptyPlaceholder: false, Path: "passiveobject/zero.obj" }] &&
            DungeonMonsterPlan.FromRoom(registeredZero,99,10,5).SpecialObjects is [{ ObjectKey: 10 }],
            "已登记ID0仍生成真实对象，不能仅凭整数形状吞掉演员");
        TestCheck.Reject<InvalidDataException>(() => fixture.Parse(zeroObject,exists:false,objectPaths:zeroObjectPaths),
            "已登记ID0的脚本缺失仍拒绝，不能作为空占位放行");
        foreach (int[] raw in new[] { new[] {99,1,-1,-1,0}, [0,1,0,-1,0], [0,1,-1,-1,1] })
            Reject(fixture.Block("special passive object", [.. raw.Select(value => new PvfToken(0,value))]),
                "其它缺失父对象、未知形状或截断子项仍拒绝");
        Reject(fixture.Block("special passive object", [new(0,0),new(0,1),new(0,-1),new(0,-1),new(0,1),
            fixture.String("[monster]"),new(0,1),new(0,5),new(0,3),new(0,10),new(0,-1)]),
            "缺失父对象带真实子怪不能沿空行规则放行");
        That(parsedSpecial.Count == 1 && parsedSpecial[0].SourceOrdinal == 0 && parsedSpecial[0].RawValues.SequenceEqual([3, -20, 150, 0, 2]) &&
            parsedSpecial[0].Children.Select(child => child.Kind).SequenceEqual(["[trap]", "[monster]"]) &&
            parsedSpecial[0].Children[1].SourceOrdinal == 1 && parsedSpecial[0].Children[1].Path == "monster/fixture.mob" &&
            parsedSpecial[0].Children[0].RawValues.SequenceEqual([40002, 40, 1, 10, -1]), "特殊对象保留父位置、原序与所有子规则，不遗漏非怪物机关");
        special[2] = new(0, 99);
        That(parsedSpecial[0].RawValues[1] == -20 && parsedSpecial is IList specialList && specialList.IsReadOnly,
            "特殊对象父子集合独立冻结原输入");
        var wrongCount = (PvfToken[])special.Clone(); wrongCount[5] = new(0, 3);
        Reject(wrongCount, "特殊对象数量越界拒绝，不发布局部子集合");
        void Reject(PvfToken[] tokens, string name)
        {
            bool rejected = false;
            try { fixture.Parse(tokens); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException) { rejected = true; }
            That(rejected, name);
        }
        Reject(fixture.Block("event monster position", [new(0, 330), new(0, 93)]), "事件怪物坐标不完整时拒绝");
        var row = fixture.Monster()[1..^1];
        var champion = fixture.Monster(rank: "[champion]")[1..^1];
        var withChampion = fixture.Parse(fixture.Block("monster", [.. champion, new(0, 0), .. row]));
        That(withChampion.Monsters.Count == 2 && withChampion.Monsters[0].RankParameterCount == 0 &&
            withChampion.Monsters[1].RankKind == "[normal]", "冠军空附加数量不吃掉下一行模板，不遗漏普通怪物");
        Reject(fixture.Block("monster", [.. champion, new(0, 1), .. row]), "未恢复的非空冠军附加不能被截断");
        Reject(fixture.Block("monster", champion), "冠军缺少数量字段不能当普通行");
        var monsters = fixture.Block("monster", [.. row, .. row]);
        var teamTokens = fixture.Block("monster team", [new(0, 100), new(0, 0)]);
        var parsed = fixture.Parse([.. monsters, .. teamTokens]);
        That(parsed.MonsterTeams!.SequenceEqual([100, 0]) && parsed.Monsters.Count == 2,
            "team values retain positional source order beside the complete actor roster");
        That(parsed.Monsters.Select(monster => monster.Team).SequenceEqual(new int?[] { 100,0 }) &&
            fixture.Parse(monsters).Monsters.All(monster => monster.Team is null),
            "每条源记录保留对应原始阵营，缺失不补0");
        teamTokens[1] = new(0, 0);
        That(parsed.MonsterTeams!.SequenceEqual([100, 0]) && parsed.MonsterTeams is IList teams && teams.IsReadOnly,
            "parsed teams are read only and do not retain mutable input tokens");
        That(fixture.Parse(monsters).MonsterTeams is { Count: 0 }, "absent team section preserves default blocking metadata");
        That(fixture.Parse([.. monsters, .. fixture.Block("monster team", [])]).MonsterTeams is { Count: 0 },
            "explicitly empty team section is valid");
        That(fixture.Parse([.. monsters, .. fixture.Block("monster team", [new(0, 0)])]).MonsterTeams!.SequenceEqual([0]),
            "missing trailing team entries remain absent instead of inventing friendly values");
        That(fixture.Parse([.. monsters, .. fixture.Block("monster team", [new(0, int.MinValue), new(0, int.MaxValue)])])
            .MonsterTeams!.SequenceEqual([int.MinValue, int.MaxValue]), "nonzero team integers stay opaque and complete");
        That(fixture.Parse([.. monsters, .. fixture.Block("monster team", [new(0,-1)])]).Monsters.Select(monster => monster.Team)
            .SequenceEqual(new int?[] { -1,null }), "原始负值与尾部缺项独立保留，由生成计划解释回退");
        Reject([.. monsters, .. fixture.Block("monster team", [new(6, fixture.String("0").Value)])], "nonnumeric team rejected");
        Reject([.. monsters, .. fixture.Block("monster team", [new(0, 100), new(0, 0), new(0, 0)])], "excess team index rejected");
        Reject(fixture.Block("monster team", [new(0, 0)]), "team without corresponding monster rejected");
        Reject([.. monsters, fixture.Tag("monster team"), new(0, 100)], "unclosed team section rejected");
        Reject([.. monsters, .. fixture.Block("monster team", []), .. fixture.Block("monster team", [])], "duplicate team sections rejected");
        That(fixture.Parse([.. monsters, .. fixture.Block("future room data", [new(6, fixture.String("opaque").Value)])]).Monsters.Count == 2,
            "unrelated unknown metadata keeps existing parsing behavior");
        var flags = fixture.Block("monster", [.. row[..^1], fixture.String("[dummy]"), row[^1], .. row]);
        var flagged = fixture.Parse(flags);
        That(flagged.Monsters is [{ PlacementKind: "[fixed]", RankKind: "[normal]", Modifiers: ["[dummy]"] }, { Modifiers.Count: 0 }] &&
            flagged.Monsters[0].Modifiers is IList modifiers && modifiers.IsReadOnly,
            "可变标记保留中间dummy与末尾品级，不吞掉下一行，也不暴露可写集合");
        TestCheck.Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(flagged,1,100),
            "未确认的dummy标记不能因资源解析成功而生成普通怪物");
        var npcValues = new[] { 62531, 1, 0, 912, 394, 0, 1, 1 };
        var npcPrefix = fixture.Monster(npcValues)[1..^2];
        var npcMarker = fixture.String("[NPC]");
        var dummyMarker = fixture.String("[dummy]");
        var displayMarker = fixture.String("[displayhuntdummy]");
        var bossMarker = fixture.String("[boss]");
        var npcMonsterTokens = fixture.Block("monster", [.. npcPrefix, npcMarker, new(0,0), dummyMarker, bossMarker, .. row]);
        var npcMonsters = fixture.Parse([.. npcMonsterTokens, .. npcTokens]);
        That(npcMonsters.Monsters is [{ MonsterId: 62531, NpcParameter: 0, RankKind: "[boss]", Modifiers: ["[NPC]", "[dummy]"] },
                { MonsterId: 1, NpcParameter: null, RankKind: "[normal]", Modifiers.Count: 0 }] &&
            npcMonsters.Monsters[0].RawValues.SequenceEqual(npcValues) && npcMonsters.Npcs.Select(npc => npc.Id).SequenceEqual([1000,1298]),
            "真实NPC0/dummy/Boss原行保留八整数与参数，下一普通怪和独立地图NPC节不混淆");
        npcMonsterTokens[npcPrefix.Length + 2] = new(0,9);
        That(npcMonsters.Monsters[0].NpcParameter == 0 && npcMonsters.Monsters[0].Modifiers is IList npcModifiers && npcModifiers.IsReadOnly,
            "原NPC参数及修饰独立于输入修改，缺省不会被补成0");
        var npcPlan = DungeonMonsterPlan.FromRoom(npcMonsters,1,100,62);
        That(npcPlan.Monsters.Count == 2 &&
            npcPlan.Monsters[0] is { MonsterId: 62531, SpawnType: 3, CandidateLevel: 62, IsBlocking: true,
                CanReportDeath: true, IsBoss: true, IsScriptedBoss: false } &&
            npcPlan.Monsters[0].Source is { NpcParameter: 0, Modifiers: ["[NPC]", "[dummy]"] } &&
            npcPlan.Monsters[1] is { MonsterId: 1, SpawnType: 0, IsBlocking: true, IsBoss: false },
            "NPC与dummy组合按原Boss品级生成，并保留相邻普通怪，房间不再整批拒绝");
        foreach (int parameter in new[] { -1, int.MinValue, int.MaxValue })
            That(fixture.Parse(fixture.Block("monster", [.. npcPrefix, npcMarker, new(0,parameter), bossMarker])).Monsters[0].NpcParameter == parameter,
                "NPC原整数保留显式负值及边界，不猜编号范围或布尔含义");
        var display = fixture.Parse(fixture.Block("monster", [.. npcPrefix, npcMarker, new(0,0), dummyMarker, displayMarker, bossMarker, .. row]));
        That(display.Monsters[0].Modifiers.SequenceEqual(["[NPC]", "[dummy]", "[displayhuntdummy]"]) && display.Monsters[1].NpcParameter is null,
            "NPC后的dummy与displayhuntdummy按原顺序保留，不吞下一行");
        That(fixture.Parse(fixture.Block("monster", [.. npcPrefix, npcMarker, new(0,0), displayMarker, bossMarker])).Monsters[0].Modifiers
            .SequenceEqual(["[NPC]", "[displayhuntdummy]"]), "独立displayhuntdummy声明不补造dummy标记");
        var npcChampion = fixture.Parse(fixture.Block("monster", [.. npcPrefix, npcMarker, new(0,0), fixture.String("[champion]"), new(0,0), .. row]));
        That(npcChampion.Monsters is [{ NpcParameter: 0, RankParameterCount: 0 }, { NpcParameter: null, RankKind: "[normal]" }],
            "NPC参数和冠军空附加数量分别消费，保留相邻普通行");
        var unknownMarker = fixture.String("[future modifier]");
        var existingOpaque = fixture.Parse(fixture.Block("monster", [.. row[..^1], unknownMarker, row[^1], .. row]));
        That(existingOpaque.Monsters[0].Modifiers.SequenceEqual(["[future modifier]"]) && existingOpaque.Monsters[1].Modifiers.Count == 0,
            "无NPC行保留既有不透明修饰和相邻行，不以新顺序规则收窄旧解析");
        TestCheck.Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(existingOpaque,1,100),
            "既有不透明修饰仍不能借结构保留生成普通怪物");
        var invalidNpcRows = new (string Name, PvfToken[] Tokens)[]
        {
            ("缺NPC整数", [.. npcPrefix, npcMarker, bossMarker]),
            ("NPC后整行截断", [.. npcPrefix, npcMarker]),
            ("NPC参数不是整数", [.. npcPrefix, npcMarker, new(4,0), bossMarker]),
            ("多余NPC整数", [.. npcPrefix, npcMarker, new(0,0), new(0,7), bossMarker]),
            ("重复NPC及参数", [.. npcPrefix, npcMarker, new(0,0), npcMarker, new(0,0), bossMarker]),
            ("NPC标记顺序错误", [.. npcPrefix, dummyMarker, npcMarker, new(0,0), bossMarker]),
            ("NPC后未知修饰", [.. npcPrefix, npcMarker, new(0,0), unknownMarker, bossMarker]),
            ("NPC后重复dummy", [.. npcPrefix, npcMarker, new(0,0), dummyMarker, dummyMarker, bossMarker]),
            ("NPC后display与dummy错序", [.. npcPrefix, npcMarker, new(0,0), displayMarker, dummyMarker, bossMarker]),
            ("缺NPC整数不能吞下一怪物ID", [.. npcPrefix, npcMarker, .. row]),
            ("缺NPC后品级不能吞下一普通行", [.. npcPrefix, npcMarker, new(0,0), .. row]),
            ("NPC冠军缺效果数量", [.. npcPrefix, npcMarker, new(0,0), fixture.String("[champion]"), .. row])
        };
        foreach (var invalid in invalidNpcRows)
            Reject(fixture.Block("monster", invalid.Tokens), invalid.Name);
        var elite = fixture.Parse(fixture.Block("monster",[.. row[..^1],fixture.String("[champion]"),new(0,0),.. row]));
        That(elite.Monsters is [{ RankKind: "[champion]" },{ MonsterId: 1, RankKind: "[normal]" }] &&
            DungeonMonsterPlan.FromRoom(elite,1,100).Monsters is [{ SpawnType: 1 },{ SpawnType: 0 }],
            "精英行后的空效果数量独立消费，下一怪物行及品级保持完整");
        Reject(fixture.Block("monster",[.. row[..^1],fixture.String("[champion]"),new(0,1),.. row]),
            "非空未知精英效果不能丢弃或吞掉下一怪物行");
        Console.WriteLine($"PASS {Count - checks} dungeon room team metadata assertions");
    }

    static void AltarResources(Fixture fixture)
    {
        PvfToken[] monster = [.. new[] { 1, 0, 440, 222, 0, 5000, 4000, 1, 0, 1 }.Select(value => new PvfToken(0,value))];
        PvfToken[] phase = [new(0,0),new(0,0),new(0,5000),new(2,BitConverter.SingleToInt32Bits(1.5f)),new(0,0),new(0,0)];
        var blood = fixture.Parse([.. fixture.Block("blood monster",monster),.. fixture.Block("blood phase time",phase)]);
        That(blood.Monsters.Count == 0 && blood.AltarWaves is { Ultimate: false, Monsters.Length: 1, Phases.Length: 1 } &&
            blood.AltarWaves.Monsters[0].Values.SequenceEqual(monster.Select(value => value.Value)) &&
            blood.AltarWaves.Phases[0] is { Round: 0, MonsterOrdinal: 0, Time: 5000, RawFactor: 1.5f, SpawnOrder: 0, GroupCount: 0, Difficulty: 0 },
            "祭坛十整数和阶段浮点字段独立保存，不错当普通怪物八整数或补造演员");
        TestCheck.Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(blood,1,100),
            "普通入口不能绕过专用波次将祭坛生成为已清空房间");
        foreach (char difficulty in "ABCDE")
        {
            var ultimate = fixture.Parse([.. fixture.Block("ultimate monster",monster),
                .. fixture.Block("ultimate phase time",[.. phase,fixture.String(difficulty.ToString())])]);
            That(ultimate.AltarWaves is { Ultimate: true } && ultimate.AltarWaves.Phases[0].Difficulty == difficulty - 'A' + 1,
                "极限难度按原生A至E映射1至5，不限死为两档");
        }
        That(fixture.Parse(fixture.Monster()).AltarWaves is null,"普通地图不创建祭坛元数据");
        var badIndex = phase.ToArray(); badIndex[1] = new(0,1);
        var badFactor = phase.ToArray(); badFactor[3] = new(2,BitConverter.SingleToInt32Bits(float.NaN));
        foreach (var invalid in new PvfToken[][]
        {
            [.. fixture.Block("blood monster",monster)],
            [.. fixture.Block("blood monster",monster[..^1]),.. fixture.Block("blood phase time",phase)],
            [.. fixture.Block("blood monster",monster),.. fixture.Block("blood phase time",phase[..^1])],
            [.. fixture.Block("blood monster",monster),.. fixture.Block("blood phase time",badIndex)],
            [.. fixture.Block("blood monster",monster),.. fixture.Block("blood phase time",badFactor)],
            [.. fixture.Block("blood monster",monster),.. fixture.Block("blood monster",monster),.. fixture.Block("blood phase time",phase)],
            [.. fixture.Block("ultimate monster",monster),.. fixture.Block("ultimate phase time",[.. phase,fixture.String("F")])],
            [.. fixture.Block("ultimate monster",monster),.. fixture.Block("ultimate phase time",[.. phase,new(0,1)])],
            [.. fixture.Block("blood monster",monster),.. fixture.Block("ultimate phase time",[.. phase,fixture.String("A")])],
            [.. fixture.Block("condition",[.. fixture.Block("blood monster",monster),.. fixture.Block("blood phase time",phase)])]
        }) TestCheck.Reject<InvalidDataException>(() => fixture.Parse(invalid),"畸形、混合或条件祭坛声明不能静默丢弃或放行");
        monster[0] = new(0,99);
        phase[3] = new(2,0);
        That(blood.AltarWaves!.Monsters[0].Values[0] == 1 && blood.AltarWaves.Phases[0].RawFactor == 1.5f,
            "祭坛源记录冻结输入，不受调用方修改影响");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;

        void Reject(Action action, string name)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            {
                rejected = true;
            }

            That(rejected, name);
        }

        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var maze = dungeons.Get(3).Mazes.Single(candidate => candidate.QuestConnectionTokens.Count == 3
            && candidate.QuestConnectionTokens[1] == new PvfToken(0, 3145));
        var first = maze.MapSpecification!.Single(map => map.Position == maze.Starts.Single());
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var despair = TowerOfDespairCatalog.Load(pvf);
        var admission = DungeonAltarAdmissionCatalog.Load(pvf);
        var altarRewards = DungeonAltarRewardCatalog.Load(pvf);
        var clearItems = DungeonAltarClearItemCatalog.Load(pvf);
        That(altarRewards.ItemProbabilities.SequenceEqual([50,47,100]) && altarRewards.GoldWeight == 7f &&
            altarRewards.Experience(15, 100, 1, false) == 12000 && altarRewards.Experience(7, 100, 1, true) == 14000 &&
            altarRewards.UltimateRewards.All(row => row[1] == 0 && row[2] == 0),
            "实际祭坛专用奖励倍率、入场序号和禁用的极限额外奖励完整读取");
        That(clearItems.Probabilities.Length == 5 && clearItems.Probabilities.All(row => row.Probability == 10000 && row.RawExtra == 0) &&
            clearItems.KindWeights.SequenceEqual([0,9500,0,500]) && clearItems.ItemReferences.Single(row => row.Level == 70) is { Value0: 7, Value1: 3 } &&
            clearItems.GoldReferences.Any(row => row.Level == 70) && clearItems.SoloGoldRate == 1.02f && clearItems.RankGoldRate == 0.1f,
            "祭坛翻牌使用本版四列默认行和自己的类别、稀有度、等级引用，不能套普通怪物表");
        That(admission.BloodFee(60, 0) == 22000 && admission.BloodFee(60, 1) == 29200 &&
            admission.BloodFee(60, 2) == 46000 && admission.BloodFee(95, 11) == 568000,
            "真实无尽收费表按已入次数选择下一次阈值，并保留lotto与价格百分比");
        foreach (var floor in despair.Floors)
        {
            var floorDefinition = dungeons.Get(floor.DungeonId);
            var floorMaze = dungeons.ResolveOrdinaryMaze(floorDefinition, floorDefinition.Mazes.Single(), _ => 0);
            That(floorDefinition.IsDespairTower && floorMaze.StaticTopology.Supported &&
                floorMaze.StaticTopology.Rooms!.Count == (floor.Layer == 15 ? 3 : floor.Layer == 25 ? 4 : floor.Layer == 55 ? 2 : 1),
                "绝望真实楼层保留单房或多房路线：" + floor.Layer);
            ushort nextFloorKey = 256;
            foreach (var floorMap in floorMaze.MapSpecification!)
            {
                var floorRoom = catalog.Get(floorMap, requirePassiveDefinitions: false);
                var floorPlan = DungeonMonsterPlan.FromRoom(floorRoom, 1, nextFloorKey, floorDefinition.BasisLevel);
                That(floorPlan.Monsters.Count > 0, "绝望楼层实际房间存在可实例化演员：" + floor.Layer);
                nextFloorKey = checked((ushort)floorPlan.NextObjectKey);
            }
        }
        var altarMap = new DungeonMap("map",new(0,0),17030,"map/towerofultimate/start.map");
        var ultimateDefinition = dungeons.Get(11007);
        var ultimateMaze = dungeons.ResolveOrdinaryMaze(ultimateDefinition, ultimateDefinition.Mazes.Single(), _ => 0);
        That(ultimateDefinition.AltarKind == 2 && ultimateDefinition.AltarMaxRounds == 7 && ultimateMaze.IsUltimateAltar &&
            ultimateMaze.StaticTopology.Supported && ultimateMaze.MapSpecification is [{ Kind: "boss", Id: 17030 }],
            "真实极限单房按唯一map声明及MAP归属解析，不补造BB网格外出口");
        var altar = catalog.Get(altarMap,requirePassiveDefinitions:false).AltarWaves;
        That(altar is { Ultimate: true, Monsters.Length: 20, Phases.Length: 20 } &&
            altar.Phases.Select(row => row.Round).Distinct().SequenceEqual(Enumerable.Range(0,7)) &&
            altar.Phases.Select(row => row.Difficulty).Distinct().SequenceEqual(new byte[] { 1,2 }) &&
            altar.Monsters[0].Values.SequenceEqual([56017,0,440,222,0,5000,4000,1,0,1]),
            "真实极限地图17030完整读取七轮20行与A/B选择，不从普通monster块猜演员");
        var ultimatePlan = DungeonMonsterPlan.FromAltarRoom(catalog.Get(altarMap with { Kind = "boss" }, requirePassiveDefinitions: false),
            1, 256, 70, _ => 0);
        That(ultimatePlan.Altar is { Actors.Length: 20, Rounds.Length: 12 } && ultimatePlan.Altar.Actors.All(actor => actor.Level == 70),
            "真实极限七回合十二个难度分支展开20个独立对象");
        var altarPaths = DungeonCatalog.ParseDirectory(pvf.ReadTokens("map/map.lst"),pvf.ResolveString,"map");
        (string Name,int Monsters,int Phases,int Actors)[] bloodSources =
        [
            ("f(0,0)start_lugaru.map",54,61,115), ("f(0,0)start_slime.map",36,36,197), ("f(0,0)start_spider.map",38,106,129),
            ("f(1,0)normal_dragonknight.map",37,37,109), ("f(1,0)normal_goblin.map",35,43,109),
            ("f(1,0)normal_soldier.map",70,70,118), ("f(2,0)boss_undead.map",61,40,204)
        ];
        foreach (var source in bloodSources)
        {
            var path = "map/towerofbloodyfight/" + source.Name;
            var map = new DungeonMap("map",new(0,0),altarPaths.Single(pair => pair.Value == path).Key,path);
            var actual = catalog.Get(map,requirePassiveDefinitions:false).AltarWaves;
            That(actual is { Ultimate: false } && actual.Monsters.Length == source.Monsters && actual.Phases.Length == source.Phases &&
                actual.Phases.Select(row => row.Round).Distinct().SequenceEqual(Enumerable.Range(0,5)) &&
                actual.Phases.All(row => row.Difficulty == 0),
                "无尽祭坛实际候选保留完整五轮、重复怪物引用与阶段行：" + source.Name);
            var expanded = DungeonMonsterPlan.FromAltarRoom(catalog.Get(map, requirePassiveDefinitions: false), 1, 256, 60, _ => 0);
            That(expanded.Altar is { } altarPlan && altarPlan.Actors.Length == source.Actors && altarPlan.Rounds.Length == 5 &&
                expanded.Monsters.Select(actor => actor.ObjectKey).Distinct().Count() == source.Actors,
                "真实无尽生成数量包含每阶段重复及递增且对象键唯一：" + source.Name);
        }
        var room = catalog.Get(first);
        var storyMap = dungeons.Get(9).Mazes.SelectMany(candidate => candidate.MapSpecification ?? []).Single(map => map.Id == 76155);
        var story = catalog.Get(storyMap);
        That(story.Monsters is [{ MonsterId: 107000909, PlacementKind: "[fixed]", RankKind: "[boss]", Modifiers: ["[dummy]"] }] &&
            story.Monsters[0].RawValues.SequenceEqual([107000909,1,0,855,233,0,1,1]),
            "Lv8真实76155保留完整八整数及fixed/dummy/boss声明");
        Reject(() => DungeonMonsterPlan.FromRoom(story,1,100), "76155未知dummy/相对等级生成规则仍拒绝，不能补普通Boss冒充剧情");
        var mermanDungeon = dungeons.Get(162);
        var mermanMaze = mermanDungeon.Mazes.Single(candidate => candidate.QuestConnectionTokens.SequenceEqual(
            [new PvfToken(0,0),new(0,3487),new(0,-1)]));
        var mermanMap = mermanMaze.MapSpecification!.Single(map => map.Id == 91548);
        var merman = catalog.Get(mermanMap,requirePassiveDefinitions: false);
        int[][] mermanRows = [[62531,1,0,912,394,0,1,1], [69022,1,0,662,316,0,1,1],
            [69022,1,0,663,392,0,1,1], [65314,1,0,532,318,0,1,1], [65314,1,0,511,430,0,1,1]];
        That(merman.Monsters.Count == 5 && merman.Monsters.Select((monster,index) => monster.RawValues.SequenceEqual(mermanRows[index])).All(value => value) &&
            merman.Monsters[0] is { MonsterId: 62531, NpcParameter: 0, PlacementKind: "[fixed]", RankKind: "[boss]", Modifiers: ["[NPC]", "[dummy]"] } &&
            merman.Monsters.Skip(1).All(monster => monster.NpcParameter is null && monster.PlacementKind == "[fixed]" &&
                monster.RankKind == "[normal]" && monster.Modifiers.Count == 0),
            "Q3487真实91548保留完整五怪原序、首NPC0/dummy/Boss及后续四行，不吞参数或下一怪物");
        // 最终累计候选保留剧情APC，并以四种专用AIC替换旧光兵5409；逐行核对真实位置和顺序。
        int[][] mermanApcRows = [[55430,-83,309,0,0,0], [20900,912,394,0,0,0], [9986,912,394,0,0,0],
            [74990,912,394,0,0,0], [19973,912,394,0,0,0]];
        That(merman.AiCharacters.Count == mermanApcRows.Length &&
            merman.AiCharacters.Select((actor,index) => actor.RawValues.SequenceEqual(mermanApcRows[index]) &&
                actor.Faction == "[character]" && actor.RankKind == "[normal]").All(value => value) &&
            merman.NpcMetadataError is null && merman.Npcs is [{ Id: 1000 }],
            "91548保留原APC55430、四种专用支援兵完整行和独立NPC1000，不被monster内NPC参数替代");
        var mermanPlan = DungeonMonsterPlan.FromRoom(merman,1,100,mermanDungeon.BasisLevel);
        var plannedRows = mermanPlan.Monsters.Where(monster => monster.AiCharacter is null).ToArray();
        var mermanApcs = mermanPlan.Monsters.Where(monster => monster.AiCharacter is not null).ToArray();
        That(plannedRows.Length == 5 &&
            plannedRows[0] is { MonsterId: 62531, SourceOrdinal: 0, SpawnType: 3, IsBlocking: true,
                CanReportDeath: true, IsBoss: true, IsScriptedBoss: false } &&
            plannedRows[0].Source is { NpcParameter: 0, Modifiers: ["[NPC]", "[dummy]"], SpecialChild: null } &&
            plannedRows.Skip(1).All(monster => monster.SpawnType == 0 && monster.IsBlocking && !monster.IsBoss &&
                monster.Source is { NpcParameter: null, SpecialChild: null }) &&
            mermanApcs.Select(actor => actor.MonsterId).SequenceEqual(merman.AiCharacters
                .Where(actor => !DungeonCompanion.IsCompanionRow(actor.Id)).Select(actor => actor.Id)) &&
            mermanApcs is [{ MonsterId: 55430 }] &&
            mermanApcs.All(actor => actor is { SpawnType: 5, IsFriendly: true, IsBlocking: false, CanReportDeath: false, IsBoss: false }),
            "Q3487/91548 人鱼之王 NPC+dummy 与后续四只普通怪可以生成，友方APC单独保留，房间不再整图拒绝");
        var mazeFailures = new List<string>();
        foreach (var map in mermanMaze.MapSpecification!.Concat(mermanMaze.LayeredMaps).Concat(mermanMaze.ConditionalMaps))
        {
            try
            {
                _ = DungeonMonsterPlan.FromRoom(catalog.Get(map, requirePassiveDefinitions: false), 1, 100, mermanDungeon.BasisLevel);
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException or OverflowException)
            {
                mazeFailures.Add($"{map.Id}: {error.Message}");
            }
        }
        That(mazeFailures.Count == 0, "Q3487 整条迷宫可以生成，不再因单房拒绝入场：" + string.Join(" | ", mazeFailures));
        That(room.Map.Id == 76121 && room.Map.Path == "map/cataclysm/granflores/01_mirkwood/3145_76121.map",
            "room resolves the matching task first-map path");
        int[][] expected = [[1, 0, 3, 573, 197, 0, 1, 1], [1, 0, 3, 557, 280, 0, 1, 1],
            [1, 0, 3, 643, 208, 0, 1, 1], [1, 0, 3, 647, 263, 0, 1, 1]];
        That(room.Monsters.Count == 4 && room.Monsters.Select((monster, index) => monster.RawValues.SequenceEqual(expected[index])).All(value => value),
            "first room retains all four full monster rows in source order");
        That(room.Monsters.All(monster => monster.MonsterId == 1 && monster.PlacementKind == "[fixed]" && monster.RankKind == "[normal]"
            && monster.Path == "monster/goblin/goblin.mob"), "monster references resolve without inventing placement or rank enums");
        That(room.PassiveObjects.Count == 25 && room.PassiveObjects.Count(item => item.ObjectId == 245) == 13
            && room.PassiveObjects.Count(item => item.ObjectId == 226) == 2 && room.PassiveObjects.Count(item => item.ObjectId == 109006903) == 4,
            "first room preserves its passive object list instead of treating it as monsters");
        var buffObject = room.PassiveObjects.Single(item => item.ObjectId == 15336);
        That(buffObject.RawValues.SequenceEqual([15336, 506, 250, 0])
            && buffObject.Path == "passiveobject/actionobject/map/stormofmetastasis/buff/total_buff/total_buff.obj",
            "visible buff object keeps its separate template and raw placement values");
        That(room.Buffs.SequenceEqual([0, 1, 2]), "map buff integers are separate from the passive buff object");
        That(room.Monsters is IList monsters && monsters.IsReadOnly && room.Monsters[0].RawValues is IList raw && raw.IsReadOnly
            && room.PassiveObjects is IList objects && objects.IsReadOnly && room.Buffs is IList buffs && buffs.IsReadOnly,
            "room data and raw rows are immutable projections");
        var next = catalog.Get(maze.MapSpecification!.Single(map => map.Id == 76122));
        int[][] nextExpected = [[1, 0, 3, 471, 208, 0, 1, 1], [1, 0, 3, 392, 266, 0, 1, 1],
            [1, 0, 3, 410, 222, 0, 1, 1], [1, 0, 3, 469, 295, 0, 1, 1], [2, 0, 3, 522, 244, 0, 1, 1]];
        That(next.Map.Id == 76122 && next.Monsters.Count == 5
            && next.Monsters.Select((monster, index) => monster.RawValues.SequenceEqual(nextExpected[index])).All(value => value)
            && next.Monsters.All(monster => monster.PlacementKind == "[fixed]" && monster.RankKind == "[normal]"),
            "next task room uses the same supported shape and preserves its own five monster rows");
        That(next.Monsters.Take(4).All(monster => monster.Path == "monster/goblin/goblin.mob")
            && next.Monsters[^1].MonsterId == 2 && next.Monsters[^1].Path == "monster/goblin/goblinthrower.mob"
            && next.PassiveObjects.Count == 21 && next.Buffs.Count == 0,
            "next room resolves the thrower and keeps its own objects without inventing absent buffs");
        Reject(() => catalog.Get(first with { Path = "map/../../monster/goblin/goblin.mob" }), "room rejects resource root escape");
        Reject(() => catalog.Get(first with { Path = "map/missing-fixture.map" }), "room rejects missing map resource");
        Reject(() => catalog.Get(first with { Id = -1 }), "room rejects negative map index");

        var fixture = new Fixture();
        var valid = fixture.Monster();
        var parsed = fixture.Parse(valid);
        valid[1] = new(0, 999);
        That(parsed.Monsters[0].MonsterId == 1, "parsed room does not retain mutable input tokens");
        var unusual = fixture.Parse(fixture.Monster(values: [1, int.MinValue, int.MaxValue, -3, 4, -5, 6, 7], placement: "[future placement]", rank: "[future rank]"));
        That(unusual.Monsters[0].RawValues.SequenceEqual([1, int.MinValue, int.MaxValue, -3, 4, -5, 6, 7])
            && unusual.Monsters[0].PlacementKind == "[future placement]" && unusual.Monsters[0].RankKind == "[future rank]",
            "structurally known raw values stay opaque rather than becoming a guessed business enum");
        That(fixture.Parse([]).Monsters.Count == 0 && fixture.Parse(fixture.Block("monster", [])).Monsters.Count == 0,
            "absent or explicitly empty monster sections stay empty only when the source is empty");
        Reject(() => fixture.Parse(fixture.Monster(values: [1, 2, 3, 4, 5, 6, 7])), "incomplete monster group rejected");
        Reject(() => fixture.Parse(fixture.Monster(values: [1, 2, 3, 4, 5, 6, 7, 8, 9])), "oversized monster group rejected");
        Reject(() => fixture.Parse(fixture.Monster(values: [-1, 0, 0, 0, 0, 0, 0, 0])), "negative monster ID rejected");
        Reject(() => fixture.Parse(fixture.Monster(values: [99, 0, 0, 0, 0, 0, 0, 0])), "unlisted monster ID rejected");
        Reject(() => fixture.Parse(fixture.Monster(), exists: false), "missing monster script rejected");
        var opaque = fixture.Monster();
        opaque[2] = new(7, 123);
        Reject(() => fixture.Parse(opaque), "unresolved monster numeric reference rejected");
        var badLabel = fixture.Monster();
        badLabel[9] = new(0, 123);
        Reject(() => fixture.Parse(badLabel), "numeric placement label rejected");
        Reject(() => fixture.Parse(fixture.Monster(placement: "")), "empty monster label rejected");
        Reject(() => fixture.Parse(fixture.Monster()[..^1]), "unclosed monster block rejected");
        Reject(() => fixture.Parse([.. fixture.Monster(), .. fixture.Monster()]), "duplicate monster blocks remain unsupported");
        var nested = fixture.Monster();
        nested[2] = fixture.Tag("condition");
        Reject(() => fixture.Parse(nested), "nested monster conditions remain unsupported");
        var obj = fixture.Parse(fixture.Block("passive object", [new(0, 3), new(0, -1), new(0, 2), new(0, int.MaxValue)]));
        That(obj.PassiveObjects.Single().RawValues.SequenceEqual([3, -1, 2, int.MaxValue]), "passive object raw integers are not narrowed");
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, 3), new(0, 1), new(0, 2)])), "incomplete object row rejected");
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, -1), new(0, 1), new(0, 2), new(0, 0)])), "negative object ID rejected");
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, 99), new(0, 1), new(0, 2), new(0, 0)])), "unlisted object ID rejected");
        That(fixture.Parse(fixture.Block("passive object", [new(0,99),new(0,1),new(0,2),new(0,0)]),requirePassiveDefinitions: false)
            .PassiveObjects.Single() is { ObjectId: 99, Path: null },"无关装饰保留原始行和缺失引用，不伪造任务脚本");
        var unlisted = fixture.Parse(fixture.Block("passive object", [new(0, 99), new(0, 1), new(0, 2), new(0, 0)]), requirePassiveDefinitions: false).PassiveObjects.Single();
        That(unlisted.ObjectId == 99 && unlisted.Path is null && unlisted.UnresolvedReason == "passive-object-unlisted" &&
            unlisted.RawValues.SequenceEqual([99, 1, 2, 0]), "客户端本地未知装饰保留全部原行和未解析原因，不换ID或伪造资源路径");
        foreach (var missingObject in new[] { (Dungeon: 7, Map: 58581, Object: 2109006955), (Dungeon: 8, Map: 58589, Object: 89092) })
        {
            var actual = catalog.Get(dungeons.Get(missingObject.Dungeon).Mazes[0].MapSpecification!.Single(map => map.Id == missingObject.Map), requirePassiveDefinitions: false);
            That(actual.PassiveObjects.Any(actor => actor.ObjectId == missingObject.Object && actor.Path is null && actor.RawValues.Count == 4) &&
                actual.Monsters.Count > 0 && actual.SpecialObjects.Count > 0, "真实普通图未列入目录的装饰与完整怪物、机关同时保留");
        }
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, 3), new(0, 1), new(0, 2), new(0, 0)]), exists: false), "missing object script rejected");
        Reject(() => fixture.Parse(fixture.Block("passive object", [new(0, 3), new(5, 1), new(0, 2), new(0, 0)])), "object numeric reference rejected");
        That(fixture.Parse(fixture.Block("buff", [new(0, -1), new(0, int.MaxValue)])).Buffs.SequenceEqual([-1, int.MaxValue]),
            "buff list retains complete integer values without interpreting their enum");
        Reject(() => fixture.Parse(fixture.Block("buff", [new(7, 1)])), "opaque buff reference rejected");
        Reject(() => fixture.Parse([fixture.Tag("buff"), new(0, 0)]), "unclosed buff block rejected");
        Console.WriteLine($"PASS {Count - checks} dungeon room resource assertions");
    }

    private sealed class Fixture
    {
        private readonly Dictionary<int, string> strings = new();
        public PvfToken String(string value)
        {
            int id = strings.Count;
            strings.Add(id, value);
            return new(6, id);
        }

        public PvfToken Tag(string value) => new(3, String("[" + value + "]").Value);
        public string Resolve(int id) => strings[id];
        public PvfToken[] Block(string tag, PvfToken[] values) => [Tag(tag), .. values, Tag("/" + tag)];
        public PvfToken[] Monster(int[]? values = null, string placement = "[fixed]", string rank = "[normal]") =>
            Block("monster", [.. (values ?? [1, 0, 3, 573, 197, 0, 1, 1]).Select(value => new PvfToken(0, value)), String(placement), String(rank)]);
        public DungeonRoomDefinition Parse(PvfToken[] tokens, bool exists = true, bool requirePassiveDefinitions = true,
            Func<PvfAiCharacterSpawn,DungeonRoomAiCharacter?>? resolveAiCharacter = null,
            IReadOnlyDictionary<int,string>? objectPaths = null) => DungeonRoomCatalog.ParseResource(
            new("map", new(0, 0), 1, "map/fixture.map"), tokens, id => strings[id],
            new Dictionary<int, string> { [1] = "monster/fixture.mob", [62531] = "monster/merman-king-fixture.mob" },
            objectPaths ?? new Dictionary<int, string> { [3] = "passiveobject/fixture.obj" }, _ => exists,requirePassiveDefinitions,resolveAiCharacter);
    }
}
