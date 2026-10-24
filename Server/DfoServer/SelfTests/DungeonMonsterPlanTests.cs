using static DfoServer.Testing.TestCheck;
using System.Collections;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class DungeonMonsterPlanTests
{
    public static void RunCore()
    {
        int checks = Count;
        var source = new DungeonRoomMonster(1, Array.AsReadOnly(new[] { 1, 0, 3, 100, 100, 0, 1, 1 }),
            "[fixed]", "[normal]", "monster/fixture.mob");
        var room = new DungeonRoomDefinition(new("boss", new(0, 0), 1, "map/fixture.map"),
            Array.AsReadOnly(new[] { source with { RankKind = "[boss]" }, source }), [], []);
        var defaultPlan = DungeonMonsterPlan.FromRoom(room, 99, 10);
        var dummyPlan = DungeonMonsterPlan.FromRoom(room with { DeclaredType = "[dummy]" },99,10);
        That(dummyPlan.Monsters[0] is { SpawnType:3,RewardType:1,IsDummyMapBoss:true,IsBoss:false,IsBlocking:true } &&
            ReferenceEquals(dummyPlan.Monsters[0].Source,room.Monsters[0]) && dummyPlan.Monsters[1] is { SpawnType:0,IsDummyMapBoss:false } &&
            defaultPlan.Monsters[0] is { SpawnType:3,RewardType:3,IsBoss:true,IsDummyMapBoss:false },
            "MAP级dummy按类型3生成并保留目标更新资格，奖励维持品级1；原行、普通怪及真Boss不变");
        var ordinaryDummy = DungeonMonsterPlan.FromRoom(room with
            { Map = room.Map with { Kind = "map" },DeclaredType = "[dummy]",MonsterTeams = [7,8] },99,10);
        That(ordinaryDummy.Monsters is [{ SourceOrdinal:1,ObjectKey:10,SpawnType:0,IsDummyMapBoss:false,Team:8 }] &&
            ReferenceEquals(ordinaryDummy.Monsters[0].Source,source),
            "无Boss标记借用dummy场景时不创建假Boss，普通怪保留原资源行号和对应阵营，不留下幽灵阻挡key");
        foreach (var active in new[] { room.Map with { Kind = "map",StoryStageIndex = 0 },room.Map with { Kind = "map",IsConditional = true } })
            That(DungeonMonsterPlan.FromRoom(room with { Map = active,DeclaredType = "[dummy]" },99,10).Monsters[0] is
                { ObjectKey:10,SpawnType:3,RewardType:1,IsDummyMapBoss:true },"有序剧情阶段或已批准的条件替换不是未标记普通候选，保留实际dummy Boss演员");
        var relative = source with { RawValues = [1, 1, 2, 100, 100, 0, 2, 1], RankKind = "[champion]", RankParameterCount = 0 };
        var ordinaryRoom = room with { Monsters = [relative, source] };
        var ordinaryPlan = DungeonMonsterPlan.FromRoom(ordinaryRoom, 99, 10, 5);
        That(ordinaryPlan.Monsters.Select(monster => monster.CandidateLevel).SequenceEqual([7, 3]) &&
            ordinaryPlan.Monsters.Select(monster => monster.SpawnType).SequenceEqual(new byte[] { 1, 0 }) &&
            ReferenceEquals(ordinaryPlan.Monsters[0].Source, relative), "本服普通相对和绝对等级分别计算，完整冠军原行和类型保留");
        That(defaultPlan.WithChampionRolls([100, 0, 0, 0], 0, _ => 0).Monsters.Single(monster => monster.Source?.RankKind == "[normal]").SpawnType == 1 &&
            defaultPlan.WithChampionRolls([0, 0, 0, 0], 0, _ => 0).Monsters.Single(monster => monster.Source?.RankKind == "[normal]").SpawnType == 0 &&
            defaultPlan.WithChampionRolls([100, 0, 0, 0], 0, _ => 0).Monsters.Single(monster => monster.IsBoss).SpawnType == 3,
            "断头谷精英率只把普通怪升成类型1，Boss保持类型3");
        var named = relative with { RankKind = "[named]", RankParameterCount = null };
        var namedPlan = DungeonMonsterPlan.FromRoom(room with { Monsters = [source,named] },99,98,5,playerTeam:0);
        That(namedPlan.Monsters[1] is { SourceOrdinal:1, ObjectKey:100, CandidateLevel:7, SpawnType:0, RewardType:0,
                IsFriendly:false, IsBlocking:true, CanReportDeath:true, IsBoss:false, IsScriptedBoss:false } &&
            ReferenceEquals(namedPlan.Monsters[1].Source,named) && named.RankKind == "[named]",
            "裸named按匹配原生默认类型0规划，保留真实原行、等级、ordinal及跳过角色的独立key");
        var friendlyNamed = DungeonMonsterPlan.FromRoom(room with { Monsters = [named], MonsterTeams = [7] },99,10,5,playerTeam:7).Monsters.Single();
        That(friendlyNamed is { SpawnType:0, Team:7, IsFriendly:true, IsBlocking:false, CanReportDeath:false, IsBoss:false },
            "named仍按本人实际阵营排除同阵营死亡与清房资格");
        foreach (var invalid in new[]
        {
            named with { RankKind="[future]" }, named with { RankKind="[Named]" },
            named with { RankParameterCount=0 }, named with { NpcParameter=1, Modifiers=["[NPC]"] }
        })
            Reject<NotSupportedException>(()=>DungeonMonsterPlan.FromRoom(room with { Monsters=[invalid] },99,10,5),
                "精确named兼容不放开未知品级、额外计数或未闭合NPC组合");
        var display = source with { RankKind = "[boss]", Modifiers = ["[displayhuntdummy]"] };
        var displayPlan = DungeonMonsterPlan.FromRoom(room with { Monsters = [source, display] },99,10,playerTeam:0);
        That(displayPlan.Monsters[1] is { SourceOrdinal:1, ObjectKey:11, SpawnType:3, Team:100,
                IsFriendly:false, IsBlocking:false, CanReportDeath:false, IsBoss:false, IsScriptedBoss:false } &&
            ReferenceEquals(displayPlan.Monsters[1].Source,display),
            "显示控制演员保留原Boss生成与敌方阵营，但不冒充战斗、dummy或清房阻挡");
        foreach (var invalid in new[]
        {
            display with { RankKind="[normal]" }, display with { PlacementKind="[random]" },
            display with { NpcParameter=0 },
            display with { Modifiers=["[displayhuntdummy]","[displayhuntdummy]"] },
            display with { Modifiers=["[dummy]","[displayhuntdummy]"] },
            display with { Modifiers=["[displayhuntdummy]","[dummy]"] },
            display with { Modifiers=["[future]"] }
        })
            Reject<NotSupportedException>(()=>DungeonMonsterPlan.FromRoom(room with { Monsters=[invalid] },99,10),
                "显示标记只接纳已确认的单标记fixed Boss，未知及组合修饰仍拒绝");
        var npc = source with { RankKind = "[boss]", Modifiers = ["[NPC]"], NpcParameter = 1069 };
        foreach (int parameter in new[] { 0, -1, 1069, int.MinValue, int.MaxValue })
        {
            var original = npc with { NpcParameter = parameter };
            var npcPlan = DungeonMonsterPlan.FromRoom(room with { Monsters = [source, original] },99,98,playerTeam: 0);
            That(npcPlan.Monsters[1] is { SourceOrdinal: 1, ObjectKey: 100, SpawnType: 3, CandidateLevel: 3,
                    Team: 100, IsFriendly: false, IsBlocking: true, CanReportDeath: true, IsBoss: true, IsScriptedBoss: false,
                    Source: { } retained } && ReferenceEquals(retained,original) && retained.NpcParameter == parameter &&
                retained.RawValues.SequenceEqual(source.RawValues),
                "纯NPC参数保留完整有符号值、原行/ordinal及独立key，不授予友方或脚本Boss身份");
        }
        foreach (var (rank,spawn) in new[] { ("[normal]",0),("[champion]",1),("[super champion]",2),("[boss]",3) })
        {
            var row = npc with { RankKind = rank, RankParameterCount = rank is "[champion]" or "[super champion]" ? 0 : null };
            var actor = DungeonMonsterPlan.FromRoom(room with { Monsters = [row], MonsterTeams = [7] },99,10,playerTeam: 7).Monsters.Single();
            That(actor.SpawnType == spawn && actor.Team == 7 && actor.IsFriendly && !actor.IsBlocking && !actor.CanReportDeath &&
                !actor.IsBoss && !actor.IsScriptedBoss, "NPC品级仍来自rank，同阵营排除仍来自本人实际team");
        }
        var npcDummy = DungeonMonsterPlan.FromRoom(room with
            { Monsters = [npc with { Modifiers = ["[NPC]", "[dummy]"] }], MonsterTeams = null }, 99, 10).Monsters.Single();
        That(npcDummy is { SpawnType: 3, Team: 100, IsFriendly: false, IsBlocking: true, CanReportDeath: true,
                IsBoss: true, IsScriptedBoss: false } &&
            npcDummy.Source!.Modifiers.SequenceEqual(["[NPC]", "[dummy]"]) && npcDummy.Source.NpcParameter == 1069,
            "NPC附带dummy仍按原Boss品级生成，不因dummy改成脚本Boss或整行拒绝");
        foreach (var row in new[]
        {
            npc with { NpcParameter = null }, npc with { Modifiers = [] },
            npc with { Modifiers = ["[NPC]","[displayhuntdummy]"] },
            npc with { Modifiers = ["[NPC]","[NPC]"] }, npc with { Modifiers = ["[future]"] }
        })
            Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(room with { Monsters = [source,row] },99,10),
                "NPC缺参数、缺标记或未闭合修饰组合整批拒绝，不发布残缺演员");
        var child = new DungeonRoomSpecialChild(1, "[monster]", Array.AsReadOnly(new[] { 1, 5, 2, 5, -1 }), "monster/fixture.mob");
        var trap = new DungeonRoomSpecialChild(0, "[trap]", [40002, 40, 1, 10, -1], null);
        var parent = new DungeonRoomSpecialObject(0, 3, [3, 100, 200, 0, 2], "passiveobject/fixture.obj", [trap, child]);
        var specialRoom = room with { SpecialObjects = [parent] };
        var specialPlan = DungeonMonsterPlan.FromRoom(specialRoom, 99, 98, 5);
        That(specialPlan.SpecialObjects.Single().Source == parent && specialPlan.Monsters.Count == 3 &&
            specialPlan.Monsters[^1] is { IsSpecialChild: true, IsBlocking: false, CandidateLevel: 5 } specialMonster &&
            specialMonster.Source!.SpecialChild == child && specialMonster.Source!.RawValues.SequenceEqual([1, 5, 2, 5, -1]) &&
            specialPlan.Monsters.Select(monster => monster.ObjectKey).Concat(specialPlan.SpecialObjects.Select(actor => actor.ObjectKey)).Distinct().Count() == 4 &&
            specialPlan.NextObjectKey == 103, "特殊父子与普通怪物统一预留key并跳过角色，非怪物原规则未丢失");
        That(DungeonMonsterPlan.FromRoom(specialRoom, 99, 10).SpecialObjects.Count == 1,
            "剧情与普通房间均按资源装载特殊父子");
        var lastSpecialKey = DungeonMonsterPlan.FromRoom(specialRoom, 0, 65532, 5);
        That(lastSpecialKey.SpecialObjects.Single().ObjectKey == 65534 && lastSpecialKey.Monsters[^1].ObjectKey == ushort.MaxValue,
            "普通怪、特殊父对象和子怪共享容量，最后一个ushort key仍可使用");
        Reject<DungeonObjectKeyOverflowException>(() => DungeonMonsterPlan.FromRoom(specialRoom, 0, 65534, 5),
            "普通怪占满ushort后，特殊父对象仍须拒绝key溢出而不是复用或绕回");
        Reject<DungeonObjectKeyOverflowException>(() => DungeonMonsterPlan.FromRoom(specialRoom, 0, 65533, 5),
            "普通怪与特殊父对象占满ushort后，特殊子怪仍须拒绝key溢出");
        Reject<DungeonObjectKeyOverflowException>(() => DungeonMonsterPlan.FromRoom(specialRoom, ushort.MaxValue, 65532, 5),
            "特殊子怪跳过末尾角色key后拒绝耗尽的共享容量");
        foreach (int automatic in new[] { 0, -1 })
        {
            var automaticRoom = room with { SpecialObjects = [parent with { Children = [trap,child with { RawValues = [1,automatic,2,5,-1] }] }] };
            That(DungeonMonsterPlan.FromRoom(automaticRoom,99,10,37).Monsters[^1].CandidateLevel == 37,
                "特殊子怪物的非正数等级采用副本basis，不截断原声明");
            Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(automaticRoom,99,10), "自动等级缺少副本basis时拒绝");
        }
        Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(ordinaryRoom, 99, 10), "普通等级规则不扩到任务路线");
        Reject<InvalidDataException>(() => DungeonMonsterPlan.FromRoom(ordinaryRoom, 99, 10, 0), "非法副本基准等级不能生成成功");
        That(defaultPlan.Monsters.All(monster => monster.IsBlocking), "missing teams default every source row to blocking");
        That(DungeonMonsterPlan.FromRoom(room with { MonsterTeams = [] }, 99, 10).Monsters.All(monster => monster.IsBlocking),
            "empty team section keeps default blocking behavior");
        var teams = room with { MonsterTeams = Array.AsReadOnly(new[] { 100, 0 }) };
        var plan = DungeonMonsterPlan.FromRoom(teams, 99, 10, playerTeam: 0);
        That(plan.Monsters.Select(monster => monster.IsBlocking).SequenceEqual([true, false]),
            "明确本人team0后排除同阵营actor，不删除生成行");
        That(plan.Monsters.Select(monster => monster.ObjectKey).SequenceEqual(new ushort[] { 10, 11 }) &&
            plan.Monsters.Select(monster => monster.SourceOrdinal).SequenceEqual([0, 1]) &&
            plan.Monsters.Select(monster => monster.SpawnType).SequenceEqual(new byte[] { 3, 0 }) &&
            ReferenceEquals(plan.Monsters[1].Source, room.Monsters[1]), "friendly actor keeps its source ordinal key level and spawn type");
        That(DungeonMonsterPlan.FromRoom(room with { MonsterTeams = [0] }, 99, 10, playerTeam: 0).Monsters.Select(monster => monster.IsBlocking)
            .SequenceEqual([false, true]), "missing trailing team defaults to blocking by its source ordinal");
        var scripted = source with { RankKind = "[boss]", Modifiers = ["[dummy]"], Team = 0 };
        var scriptedPlan = DungeonMonsterPlan.FromRoom(room with { Monsters = [scripted, source with { Team = 0 }] }, 99, 10, playerTeam: 0);
        That(scriptedPlan.Monsters[0] is { IsScriptedBoss: true, IsFriendly: false, IsBlocking: true, CanReportDeath: true, IsBoss: true, Team: 0 } &&
            scriptedPlan.Monsters[1] is { IsScriptedBoss: false, IsFriendly: true, IsBlocking: false, CanReportDeath: false, Team: 0 },
            "同team0的精确dummy Boss仍可报告死亡，普通同阵营演员继续排除");
        var cinematic = scripted with { MonsterId = 62959, RawValues = [62959, 0, 3, 100, 100, 0, 1, 1] };
        That(DungeonMonsterPlan.FromRoom(room with { Monsters = [cinematic] }, 99, 10, playerTeam: 0).Monsters.Single() is
            { IsScriptedBoss: true, IsFriendly: true, IsBlocking: false, CanReportDeath: false, Team: 0 },
            "76114 的 62959 同阵营时仍排除普通死亡");
        That(DungeonMonsterPlan.FromRoom(room with { MonsterTeams = [-1, int.MaxValue] }, 99, 10).Monsters.All(monster => monster.IsBlocking),
            "all nonzero team values remain blocking without guessed team enums");
        bool rejected = false;
        try { DungeonMonsterPlan.FromRoom(room with { MonsterTeams = [100, 0, 0] }, 99, 10); }
        catch (InvalidDataException) { rejected = true; }
        That(rejected, "synthetic metadata cannot add team indices beyond the resource roster");
        That(new DungeonPlannedMonster(0, 10, 1, 3, source).IsBlocking,
            "existing fixture constructors keep ordinary blocking behavior");
        var relativeTeams = room with { MonsterTeams = [0,7] };
        var sameTeam = DungeonMonsterPlan.FromRoom(relativeTeams,99,10,playerTeam: 7);
        That(sameTeam.PlayerTeam == 7 && sameTeam.Monsters.Select(monster => monster.Team).SequenceEqual([0,7]) &&
            sameTeam.Monsters.Select(monster => monster.IsFriendly).SequenceEqual([false,true]) &&
            sameTeam.Monsters.Select(monster => monster.CanReportDeath).SequenceEqual([true,false]),
            "明确team7时team0仍是敌方，同team7才排除战斗奖励");
        That(DungeonMonsterPlan.FromRoom(relativeTeams,99,10).Monsters.All(monster => !monster.IsFriendly && monster.IsBlocking),
            "未知本人阵营不能自动排除team0或其他正值");
        var defaults = DungeonMonsterPlan.FromRoom(room with { MonsterTeams = [-1] },99,10,playerTeam: 0);
        That(defaults.Monsters.All(monster => monster.Team == 100 && !monster.IsFriendly),
            "负值与缺失阵营按原生创建回退100，不变成友军");
        Reject<InvalidDataException>(() => DungeonMonsterPlan.FromRoom(room,99,10,playerTeam: -1),
            "未知本人阵营使用null而不是怪物的负值回退标记");
        var apcs = new[] { "[normal]", "[champion]", "[super champion]", "[boss]" }.Select((rank,index) =>
            new DungeonRoomAiCharacter(2000+index,[2000+index,100,200,0,0,0],"[monster]",rank,"aicharacter/fixture.aic",32)).ToArray();
        var mixed = DungeonMonsterPlan.FromRoom(room with { AiCharacters = apcs },99,10);
        That(mixed.Monsters.Select(row => row.SourceOrdinal).SequenceEqual([0,1,0,1,2,3]) &&
            mixed.Monsters.Skip(2).Select(row => row.SpawnType).SequenceEqual(new byte[] { 5,6,7,8 }) &&
            mixed.Monsters.Select(row => row.ObjectKey).SequenceEqual(new ushort[] { 10,11,12,13,14,15 }),
            "APC独立资源行索引和四种类型保留，全部演员共享唯一对象key空间");
        That(mixed.Monsters.Skip(2).All(row => row.Source is null && row.AiCharacter is not null && row.CanReportDeath && !row.IsBlocking) &&
            mixed.Monsters.Skip(2).Select(row => row.RewardType).SequenceEqual(new byte[] { 0,1,2,3 }) && mixed.Monsters[^1].IsBoss,
            "敌对APC拥有真实死亡资格和自身经验品级，同时保持普通门不阻塞合同");
        foreach (string faction in new[] { "[character]", "[neutral]" })
            That(!DungeonMonsterPlan.FromRoom(room with { AiCharacters = [apcs[^1] with { Faction = faction }] },99,10).Monsters[^1].CanReportDeath,
                "友方和中立APC不能通过死亡报告生成奖励或Boss通关事实");
        Console.WriteLine($"PASS {Count - checks} dungeon blocking monster plan assertions");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;

        void Reject<T>(Action action, string name) where T : Exception
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (T)
            {
                rejected = true;
            }

            That(rejected, name);
        }

        var definition = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf).Get(3);
        var maze = definition.Mazes.Single(candidate => candidate.QuestConnectionTokens.Count == 3
            && candidate.QuestConnectionTokens[1] == new PvfToken(0, 3145));
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var dazzlement = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf).Get(7601);
        var dazzlementMaze = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf)
            .ResolveOrdinaryMaze(dazzlement,dazzlement.Mazes.Single(),_=>0);
        That(dazzlementMaze.StaticTopology.Supported && dazzlementMaze.MapSpecification!.Select(map=>map.Id).SequenceEqual([450001,450002]),
            "7601真实MAP归属和BBEE拓扑选择450001/450002，不猜地图或补造出口");
        var dazzlementStart = DungeonMonsterPlan.Create(catalog,dazzlementMaze.MapSpecification![0],99,100,dazzlement.BasisLevel,0);
        That(dazzlementStart.Monsters.Select(actor=>actor.MonsterId).SequenceEqual([107013343,107013344,107013343]) &&
            dazzlementStart.Monsters.Select(actor=>actor.ObjectKey).SequenceEqual(new ushort[]{100,101,102}) &&
            dazzlementStart.Monsters.Select(actor=>actor.SourceOrdinal).SequenceEqual([0,1,2]) &&
            dazzlementStart.Monsters.All(actor=>actor.CandidateLevel==95 && actor.SpawnType==0 && actor.IsBlocking && actor.CanReportDeath && !actor.IsBoss),
            "眩惑首房保留三个实际模板及原行，named演员按原生类型0规划，不删行冒充空房");
        That(dazzlementStart.Monsters[1].Source is { RankKind:"[named]", RankParameterCount:null } retainedNamed &&
            ReferenceEquals(retainedNamed,dazzlementStart.Room.Monsters[1]) &&
            retainedNamed.RawValues.SequenceEqual([107013344,1,0,878,209,0,1,1]),
            "450001命名演员保留完整原始八整数和标签，不伪装成normal源资源");
        var dazzlementBoss = DungeonMonsterPlan.Create(catalog,dazzlementMaze.MapSpecification![1],99,103,dazzlement.BasisLevel,0);
        That(dazzlementBoss.Monsters.Select(actor=>actor.MonsterId).SequenceEqual([107013343,107013345,107013343]) &&
            dazzlementBoss.Monsters.Select(actor=>actor.SpawnType).SequenceEqual(new byte[]{0,3,0}) &&
            dazzlementBoss.Monsters.Single(actor=>actor.IsBoss).MonsterId==107013345,
            "眩惑末房真实Boss仍为类型3，与首房named类型0独立；不代表专用塔准入或自然通关已验证");
        var apcBoss = catalog.Get(new("boss",new(0,0),53529,"map/Cataclysm/Behemoth/07_2ndBackbone/3213_53529.map"),requirePassiveDefinitions: false);
        var apcPlan = DungeonMonsterPlan.FromRoom(apcBoss,99,200);
        That(apcBoss.Monsters.Count == 0 && apcPlan.Monsters is [{ MonsterId: 2004, SourceOrdinal: 0, CandidateLevel: 32, SpawnType: 8,
            IsBlocking: false, CanReportDeath: true, IsBoss: true, AiCharacter.Faction: "[monster]" }],
            "53529真实敌对阿甘左APC Boss使用AIC声明Lv32，没有普通怪物替身或空房间冒充通关");
        var firstMap = maze.MapSpecification!.Single(map => map.Id == 76121);
        var first = DungeonMonsterPlan.Create(catalog, firstMap, 11, 10);
        That(first.Room.Map == firstMap && first.Monsters.Count == 4, "matching first room produces all four monsters");
        That(first.Monsters.Select(item => item.ObjectKey).SequenceEqual(new ushort[] { 10, 12, 13, 14 }),
            "object allocation skips the current character without replacing a resource row");
        That(first.Monsters.Select(item => item.SourceOrdinal).SequenceEqual([0, 1, 2, 3])
            && first.Monsters.All(item => item.MonsterId == 1 && item.CandidateLevel == 3),
            "source ordinals template IDs and candidate levels remain separate");
        That(first.Monsters.Select((item, index) => ReferenceEquals(item.Source, first.Room.Monsters[index])).All(value => value)
            && first.Monsters[0].Source!.RawValues.SequenceEqual([1, 0, 3, 573, 197, 0, 1, 1]),
            "plan retains the original resource reference and complete row");
        That(first.Monsters is IList list && list.IsReadOnly, "planned monster collection is read only");

        var nextMap = maze.MapSpecification!.Single(map => map.Id == 76122);
        var next = DungeonMonsterPlan.Create(catalog, nextMap, 99, 20);
        That(next.Room.Map == nextMap && next.Monsters.Count == 5
            && next.Monsters.Select(item => item.MonsterId).SequenceEqual([1, 1, 1, 1, 2])
            && next.Monsters.Select(item => item.ObjectKey).SequenceEqual(new ushort[] { 20, 21, 22, 23, 24 }),
            "next room uses its own five source rows rather than the first room roster");
        That(next.Monsters[^1].Source!.Path == "monster/goblin/goblinthrower.mob"
            && next.Monsters[^1].Source!.RawValues.SequenceEqual([2, 0, 3, 522, 244, 0, 1, 1]),
            "next room thrower keeps its resource identity and full placement row");
        foreach (var expected in new[] { (Map: 76123, Count: 7), (Map: 76124, Count: 5) })
        {
            var map = maze.MapSpecification!.Single(value => value.Id == expected.Map);
            var plan = DungeonMonsterPlan.Create(catalog, map, 99, 100);
            That(plan.Monsters.Count == expected.Count && plan.Monsters.All(value => value.CandidateLevel == 3),
                "3145中间真实房间保持完整固定普通怪物行");
        }
        var fifth = catalog.Get(maze.MapSpecification!.Single(map => map.Id == 76125));
        That(fifth.Monsters.Count == 6 && fifth.Monsters[4].RawValues.SequenceEqual([1, 1, 0, 503, 233, 0, 1, 1]),
            "76125第五行特殊整数原样保留，不替换为前三整数1/0/3");
        var fifthPlan = DungeonMonsterPlan.FromRoom(fifth, 99, 100, definition.BasisLevel);
        That(fifthPlan.Monsters.Count == 6 && fifthPlan.Monsters[4].CandidateLevel == definition.BasisLevel &&
            fifthPlan.Monsters[4].SourceOrdinal == 4 && fifthPlan.Monsters[4].SpawnType == 0 &&
            ReferenceEquals(fifthPlan.Monsters[4].Source, fifth.Monsters[4]) &&
            fifthPlan.Monsters.Where(monster => monster.SourceOrdinal != 4).All(monster => monster.CandidateLevel == 3),
            "76125完整六行保留，相对等级资源行按副本basis5加偏移0生成Lv5");
        var bossRoom = catalog.Get(maze.MapSpecification!.Single(map => map.Id == 76126));
        That(bossRoom.Monsters.Count == 2 && bossRoom.Monsters[0].MonsterId == 107000903 &&
            bossRoom.Monsters[0].RankKind == "[boss]" && bossRoom.Monsters[1].MonsterId == 107000906 &&
            bossRoom.Monsters[1].RankKind == "[normal]", "76126真实Boss与普通怪物身份分别保留");
        var bossPlan = DungeonMonsterPlan.FromRoom(bossRoom, 99, 100, playerTeam: 0);
        That(bossPlan.Monsters.Count == 2 && bossPlan.Monsters.All(monster => monster.CandidateLevel == 3) &&
            bossPlan.Monsters.Select(monster => monster.SpawnType).SequenceEqual(new byte[] { 3, 0 }),
            "76126完整Boss与普通怪物计划分别保留类型3和0，等级均为资源Lv3");
        That(bossRoom.MonsterTeams!.SequenceEqual([100, 0]) &&
            bossPlan.Monsters.Select(monster => monster.IsBlocking).SequenceEqual([true, false]),
            "76126友方任务对象仍生成完整actor，但不作为清房敌人");
        That(DungeonMonsterPlan.FromRoom(first.Room, 99, 10).Monsters.Select(item => item.ObjectKey)
            .SequenceEqual(new ushort[] { 10, 11, 12, 13 }), "unoccupied keys remain consecutive");
        That(DungeonMonsterPlan.FromRoom(first.Room, 10, 10).Monsters[0].ObjectKey == 11,
            "allocation skips a character collision at the initial key");
        // 普通怪容量不依赖房间装饰物；特殊父子共享容量由合成夹具独立验证。
        That(DungeonMonsterPlan.FromRoom(first.Room, 0, 65532, includeSpecial: false).Monsters[^1].ObjectKey == ushort.MaxValue,
            "last ushort key is usable when it does not collide");
        Reject<DungeonObjectKeyOverflowException>(() => DungeonMonsterPlan.FromRoom(first.Room, 0, 65533, includeSpecial: false), "allocation refuses overflow instead of wrapping");
        Reject<DungeonObjectKeyOverflowException>(() => DungeonMonsterPlan.FromRoom(first.Room, ushort.MaxValue, 65532, includeSpecial: false),
            "skipping the last ushort character key refuses exhausted capacity");

        var source = first.Room.Monsters[0];
        DungeonRoomDefinition With(DungeonRoomMonster value) => first.Room with { Monsters = Array.AsReadOnly(new[] { value }), MonsterTeams = null };
        void Unsupported(DungeonRoomMonster value, string name)
            => Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(With(value), 99, 10), name);
        Unsupported(source with { RawValues = [1, 0, 3, 573, 197, 0, 1] }, "unknown short row stays unsupported");
        Unsupported(source with { RawValues = [1, 0, 3, 573, 197, 0, 1, 1, 0] }, "unknown long row stays unsupported");
        Unsupported(source with { PlacementKind = "[random]" }, "random placement stays unsupported");
        That(DungeonMonsterPlan.FromRoom(With(source with { RankKind = "[boss]" }), 99, 10).Monsters[0].SpawnType == 3,
            "匹配固定Boss品级进入独立类型3，不能仍当普通0");
        Unsupported(source with { RankKind = "[future]" }, "unknown rank stays unsupported");
        foreach (int field in new[] { 1, 6, 7 })
        {
            int[] raw = source.RawValues.ToArray();
            raw[field] = field == 1 ? 2 : 256;
            Unsupported(source with { RawValues = Array.AsReadOnly(raw) }, "unknown monster condition " + field + " stays unsupported");
        }
        int[] counts = source.RawValues.ToArray(); counts[6] = 2; counts[7] = 2;
        That(DungeonMonsterPlan.FromRoom(With(source with { RawValues = counts }),99,10).Monsters.Count == 1,
            "资源尾部掉落数量2保留，不能误解释成未知创建条件而拒绝合法怪物");

        Unsupported(source with { MonsterId = 2 }, "inconsistent resource template ID stays unsupported");
        var special = fifth.Monsters[4];
        DungeonRoomDefinition ChangedSpecial(DungeonRoomMonster changed) => fifth with
        {
            Monsters = Array.AsReadOnly(fifth.Monsters.Select((monster, index) => index == 4 ? changed : monster).ToArray())
        };
        Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(fifth,99,100), "相对等级缺少副本基础时整批拒绝");
        var relocated = fifth with { Map = fifth.Map with { Id = 100, Path = "map/fixture.map" },
            Monsters = Array.AsReadOnly(fifth.Monsters.Reverse().ToArray()) };
        That(DungeonMonsterPlan.FromRoom(relocated,99,100,7).Monsters.Single(row => row.Source!.RawValues[1] == 1).CandidateLevel == 7,
            "相对等级按副本基础计算，和地图编号、路径、模板及资源顺序无关");
        int[] relative = special.RawValues.ToArray(); relative[2] = -2;
        That(DungeonMonsterPlan.FromRoom(ChangedSpecial(special with { RawValues = relative }),99,100,7).Monsters[4].CandidateLevel == 5,
            "相对等级保留有符号偏移，不把负偏移截断");
        relative = special.RawValues.ToArray(); relative[1] = 2;
        Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(ChangedSpecial(special with { RawValues = relative }),99,100,7),
            "未知相对等级标志拒绝");
        Reject<InvalidDataException>(() => DungeonMonsterPlan.FromRoom(fifth,99,100,0),"相对等级计算为零时拒绝生成");
        var mixed = first.Room with { Monsters = Array.AsReadOnly(new[] { source, source with { RankKind = "[future]" } }), MonsterTeams = null };
        Reject<NotSupportedException>(() => DungeonMonsterPlan.FromRoom(mixed, 99, 10),
            "mixed supported and unknown rows reject the whole plan rather than publishing a partial roster");
        var empty = first.Room with { Monsters = Array.Empty<DungeonRoomMonster>(), MonsterTeams = null, SpecialObjects = [], AiCharacters = [] };
        That(DungeonMonsterPlan.FromRoom(empty, ushort.MaxValue, ushort.MaxValue).Monsters.Count == 0,
            "a genuinely empty resource roster needs no object keys");
        var candidate = source with { RawValues = [1, 0, int.MaxValue, 573, 197, -7, 1, 1] };
        That(DungeonMonsterPlan.FromRoom(With(candidate), 99, 10).Monsters[0].CandidateLevel == int.MaxValue,
            "candidate level and unrelated opaque values are not narrowed or guessed");
        Console.WriteLine($"PASS {Count - checks} dungeon monster plan assertions");
    }
}
