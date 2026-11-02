using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestHuntProgressTests
{
    public static void Run()
    {
        int checks = Count;
        var quest = QuestEligibilityTests.Quest(100) with { Condition = "[hunt enemy]", IntData = [3,-1,1,1,2, -1,0,1,1,1, 3,1,2,1,3] };
        var catalog = new QuestCatalog(new() { [100] = quest });
        uint initial = QuestHuntProgress.Initial(quest);
        var active = new ActiveMainQuest(1,100,initial) { AcceptanceId = Guid.NewGuid() };
        var map = new DungeonMap("boss",new(0,0),76126,"map/fixture.map");
        var maze = new DungeonMaze(0,1,1,"fixture",[map.Position],[map.Position],[map],[]);
        var entry = new DungeonEntryPlan(1,0,3,3145,0,0,maze,map,map) { QuestAcceptances = [active] };
        That(initial == 2u + (1u << 9) + (3u << 18), "五元组的三项数量按完整9bit原序编码");
        var after = QuestHuntProgress.MonsterDeath([active],entry,catalog,Monster(1))[0];
        That(after.ConditionState == 1u + (3u << 18) && after.AcceptanceId == active.AcceptanceId,
            "一条真实匹配死亡推进全部命中目标，保留未匹配通道及接取身份");
        That(QuestHuntProgress.MonsterDeath([after],entry,catalog,Monster(2))[0] == after,
            "最低难度不满足时，模板相同也不能推进");
        That(QuestHuntProgress.MonsterDeath([after],entry with { Difficulty = 1 },catalog,Monster(2))[0].ConditionState == 1u + (2u << 18),
            "合法难度与对应目标匹配后只减其通道");
        foreach (byte difficulty in new byte[] {0,1,2,3})
            That(QuestHuntProgress.MonsterDeath([after],entry with { Difficulty = difficulty },catalog,Monster(2))[0].ConditionState ==
                (difficulty == 0 ? after.ConditionState : 1u+(2u<<18)),
                "分类6最低难度边界与更高合法难度保持："+difficulty);
        foreach (var stale in new[] { active with { AcceptanceId = Guid.NewGuid() }, active with { AcceptanceId = Guid.Empty },
            active with { Slot = 2 }, active with { ConditionState = initial | (1u << 29) }, active with { ConditionState = initial + 1 } })
            That(QuestHuntProgress.MonsterDeath([stale],entry,catalog,Monster(1))[0] == stale,
                "旧实例不能推进重新接取、空身份、换槽或未知scalar编码");
        var passive = quest with { IntData = [3,-1,13099,3,1] };
        var passiveCatalog = new QuestCatalog(new() { [100] = passive });
        var target = active with { ConditionState = 1 };
        var passiveEntry = entry with { QuestAcceptances = [target] };
        That(QuestHuntProgress.MonsterDeath([target],passiveEntry,passiveCatalog,Monster(13099))[0] == target &&
            QuestHuntProgress.PassiveDeath([target],passiveEntry,passiveCatalog,13099)[0].ConditionState == 0,
            "相同编号的普通怪物和被动物件只推进对应目标类型");
        var apc = quest with { IntData = [3,-1,13099,2,1] };
        var apcCatalog = new QuestCatalog(new() { [100] = apc });
        That(QuestHuntProgress.MonsterDeath([target],passiveEntry,apcCatalog,Monster(13099))[0] == target &&
            QuestHuntProgress.PassiveDeath([target],passiveEntry,apcCatalog,13099)[0] == target &&
            QuestHuntProgress.AiCharacterDeath([target],passiveEntry,apcCatalog,13099)[0].ConditionState == 0,
            "APC类型2只由实际APC死亡推进，同号怪物和被动物件不能替代");
        foreach (var unsupported in new[] { quest with { IntData = [3,-1,13099,0,1] }, quest with { IntData = [3,-1,1,1,512] },
            quest with { IntData = [3,-1,1,1] }, quest with { SubType = 1 }, quest with { Grade = "[daily random]" }, quest with { Grade = "[training]" },
            quest with { MinimumLevel = 255 } })
            That(!QuestHuntProgress.TryTargets(unsupported,out _), "非普通怪物、编码越界或专用状态不借用猎杀成功合同");
        var done = active with { ConditionState = 0 };
        That(QuestHuntProgress.TryTargets(quest with { Grade = "[daily]" },out _) &&
            QuestHuntProgress.TryTargets(quest with { Grade = "[normaly repeat]" },out _),
            "已接入周期与重复接取身份的猎杀使用同一权威事件模型");
        That(QuestHuntProgress.MonsterDeath([done],entry,catalog,Monster(1))[0] == done, "达成通道不会下溢或重置");
        var monsterQuest = quest with { Condition = "[hunt monster]", IntData = [3,-1,1,2,3,1,2,3] };
        var monsterCatalog = new QuestCatalog(new() { [100] = monsterQuest });
        var monsterActive = active with { ConditionState = QuestHuntProgress.Initial(monsterQuest) };
        var monsterEntry = entry with { QuestAcceptances = [monsterActive], Difficulty = 2 };
        That(monsterActive.ConditionState == 2+(3u<<9) &&
            QuestHuntProgress.MonsterDeath([monsterActive],monsterEntry,monsterCatalog,Monster(1))[0].ConditionState == 1+(3u<<9),
            "分类2四元组保留Full03共用9bit通道，只按实际怪物模板推进");
        foreach (byte difficulty in new byte[] {0,1,2,3})
            That(QuestHuntProgress.MonsterDeath([monsterActive],monsterEntry with { Difficulty = difficulty },monsterCatalog,Monster(2))[0].ConditionState ==
                (difficulty == 0 ? monsterActive.ConditionState : 2u+(2u<<9)),
                "Full03分类2正模板按最低难度，不把更高难度误拒："+difficulty);
        That(QuestHuntProgress.PassiveDeath([monsterActive],monsterEntry,monsterCatalog,1)[0] == monsterActive &&
            QuestHuntProgress.AiCharacterDeath([monsterActive],monsterEntry,monsterCatalog,1)[0] == monsterActive,
            "分类2正模板不能由同编号APC或被动物件事件推进");
        var anyMonster = monsterQuest with { IntData = [3,1,-5,3] };
        var anyCatalog = new QuestCatalog(new() { [100] = anyMonster });
        var anyActive = active with { ConditionState = QuestHuntProgress.Initial(anyMonster) };
        var anyEntry = monsterEntry with { QuestAcceptances = [anyActive] };
        var first = QuestHuntProgress.MonsterDeath([anyActive],anyEntry,anyCatalog,Monster(400))[0];
        var second = QuestHuntProgress.MonsterDeath([first],anyEntry,anyCatalog,Monster(401))[0];
        That(anyActive.ConditionState == 3 && first.ConditionState == 2 && second.ConditionState == 1 &&
            first.AcceptanceId == anyActive.AcceptanceId && second.AcceptanceId == anyActive.AcceptanceId &&
            QuestHuntProgress.MonsterDeath([second],anyEntry,anyCatalog,Monster(402))[0].ConditionState == 0 &&
            QuestHuntProgress.MonsterDeath([anyActive],anyEntry,anyCatalog,Monster(0))[0].ConditionState == 2,
            "分类2的-5跨真实怪物模板推进同一接取，既不要求rank也不伪造清图");
        That(QuestHuntProgress.TryTargets(anyMonster with { IntData = [159,-1,-5,53] },out var realShape) &&
            realShape is [{ DungeonId: 159,MinimumDifficulty: -1,MonsterId: -5,Count: 53 }],
            "真实3462四元组保留任意难度和53次原始阈值，不按文案缩减为一次清图");
        foreach (byte difficulty in new byte[] {0,1,2,3})
            That(QuestHuntProgress.MonsterDeath([anyActive],anyEntry with { Difficulty = difficulty },anyCatalog,Monster(400))[0].ConditionState ==
                (difficulty == 0 ? 3u : 2u),"-5仍遵守本版最低难度："+difficulty);
        That(QuestHuntProgress.AiCharacterDeath([anyActive],anyEntry,anyCatalog,400)[0] == anyActive &&
            QuestHuntProgress.PassiveDeath([anyActive],anyEntry,anyCatalog,401)[0] == anyActive &&
            QuestHuntProgress.MonsterDeath([anyActive],anyEntry with { DungeonId = 4 },anyCatalog,Monster(400))[0] == anyActive,
            "任意怪物仍不能借用APC、物件或其它副本事实");
        foreach (var stale in new[] {anyActive with { AcceptanceId = Guid.NewGuid() },anyActive with { AcceptanceId = Guid.Empty },anyActive with { Slot = 2 }})
            That(QuestHuntProgress.MonsterDeath([stale],anyEntry,anyCatalog,Monster(400))[0] == stale,
                "-5不放宽旧实例、放弃重接或槽位隔离");
        That(QuestHuntProgress.MonsterDeath([anyActive with { ConditionState = 0 }],anyEntry,anyCatalog,Monster(400))[0].ConditionState == 0,
            "-5达成后继续真实死亡不会下溢");
        var bossQuest = monsterQuest with { IntData = [3,1,-3,2] };
        var bossCatalog = new QuestCatalog(new() { [100] = bossQuest });
        var bossActive = active with { ConditionState = QuestHuntProgress.Initial(bossQuest) };
        var bossEntry = monsterEntry with { QuestAcceptances = [bossActive] };
        That(QuestHuntProgress.TryTargets(bossQuest with { IntData = [530,-1,-3,1] },out var bossShape) &&
            bossShape is [{ DungeonId: 530, MinimumDifficulty: -1, MonsterId: -3, Count: 1 }],
            "真实6085只核对四元组声明，不执行实际任务或地图流程");
        foreach (byte spawnType in new byte[] {0,1,2,3})
            That(QuestHuntProgress.MonsterDeath([bossActive],bossEntry,bossCatalog,Monster(400,spawnType))[0].ConditionState ==
                (spawnType == 3 ? 1u : 2u),"-3只计已确认普通SpawnType3，普通与两种精英不计："+spawnType);
        var bossMonster = Monster(400,3);
        foreach (byte difficulty in new byte[] {0,1,2,3})
            That(QuestHuntProgress.MonsterDeath([bossActive],bossEntry with { Difficulty = difficulty },bossCatalog,bossMonster)[0].ConditionState ==
                (difficulty == 0 ? 2u : 1u),"-3仍遵守真实副本最低难度："+difficulty);
        var apcBoss = bossMonster with
        {
            Source = null, SpawnType = 8,
            AiCharacter = new(400,[],"[monster]","[boss]","aicharacter/fixture.aic",1)
        };
        That(QuestHuntProgress.AiCharacterDeath([bossActive],bossEntry,bossCatalog,400)[0] == bossActive &&
            QuestHuntProgress.PassiveDeath([bossActive],bossEntry,bossCatalog,400)[0] == bossActive &&
            QuestHuntProgress.MonsterDeath([bossActive],bossEntry,bossCatalog,apcBoss)[0] == bossActive &&
            QuestHuntProgress.MonsterDeath([bossActive],bossEntry,bossCatalog,apcBoss with { SpawnType = 3 })[0] == bossActive,
            "普通-3不借用APC/被动物件死亡，未闭合的APC rank8不与普通Boss合并");
        That(QuestHuntProgress.MonsterDeath([bossActive],bossEntry with { DungeonId = 4 },bossCatalog,bossMonster)[0] == bossActive,
            "其它副本的Boss不能推进本次目标");
        foreach (var stale in new[] {bossActive with { AcceptanceId = Guid.NewGuid() },bossActive with { AcceptanceId = Guid.Empty },
            bossActive with { Slot = 2 }})
            That(QuestHuntProgress.MonsterDeath([stale],bossEntry,bossCatalog,bossMonster)[0] == stale,
                "Boss目标保持旧副本、放弃重接与任务槽隔离");
        var firstBoss = QuestHuntProgress.MonsterDeath([bossActive],bossEntry,bossCatalog,bossMonster)[0];
        var secondBoss = QuestHuntProgress.MonsterDeath([firstBoss],bossEntry,bossCatalog,bossMonster with { ObjectKey = 11, SourceOrdinal = 1 })[0];
        That(firstBoss.ConditionState == 1 && secondBoss.ConditionState == 0 && secondBoss.AcceptanceId == bossActive.AcceptanceId &&
            QuestHuntProgress.MonsterDeath([secondBoss],bossEntry,bossCatalog,bossMonster)[0] == secondBoss,
            "不同Boss事实分别减量，目标达成后不下溢且保留接取身份");
        var mixedBoss = bossQuest with { IntData = [3,-1,-3,1,3,-1,-5,1] };
        var mixedCatalog = new QuestCatalog(new() { [100] = mixedBoss });
        var mixedActive = active with { ConditionState = QuestHuntProgress.Initial(mixedBoss) };
        var mixedEntry = bossEntry with { QuestAcceptances = [mixedActive] };
        That(QuestHuntProgress.MonsterDeath([mixedActive],mixedEntry,mixedCatalog,Monster(400))[0].ConditionState == 1 &&
            QuestHuntProgress.MonsterDeath([mixedActive],mixedEntry,mixedCatalog,bossMonster)[0].ConditionState == 0,
            "Boss与-5混合目标独立匹配，一次真实Boss死亡推进全部命中通道");
        var mission = monsterQuest with { Grade = "[mission]", DungeonIndex = 3, IntData = [3,-1,-1,20] };
        var missionCatalog = new QuestCatalog(new() { [100] = mission });
        var missionActive = active with { ConditionState = QuestHuntProgress.Initial(mission) };
        var missionEntry = entry with { QuestAcceptances = [missionActive] };
        That(QuestHuntProgress.MonsterDeath([missionActive],missionEntry,missionCatalog,Monster(400))[0].ConditionState == 19,
            "明确地下城的冒险普通怪物目标按真实非Boss死亡减一");
        foreach (var excluded in new[] { Monster(400,3), Monster(50001), Monster(56001),
            Monster(400) with { IsFriendly = true }, Monster(400) with { IsBlocking = false },
            Monster(400) with { IsDummyMapBoss = true } })
            That(QuestHuntProgress.MonsterDeath([missionActive],missionEntry,missionCatalog,excluded)[0] == missionActive,
                "冒险-1不混入Boss、特殊模板、友军或演出对象");
        That(QuestHuntProgress.MonsterDeath([missionActive],missionEntry with { DungeonId = 4 },missionCatalog,Monster(400))[0] == missionActive &&
            QuestHuntProgress.AiCharacterDeath([missionActive],missionEntry,missionCatalog,400)[0] == missionActive &&
            QuestHuntProgress.PassiveDeath([missionActive],missionEntry,missionCatalog,400)[0] == missionActive,
            "冒险-1保持副本、APC与物件隔离");
        That(!QuestHuntProgress.TryTargets(mission with { DungeonIndex = 4 },out _) &&
            !QuestHuntProgress.TryTargets(mission with { Grade = "[side]" },out _), "不推广未确认的负选择符形状");
        foreach (int selector in new[] {-1,-2,-11})
            That(!QuestHuntProgress.TryTargets(monsterQuest with { IntData = [3,-1,selector,1] },out _),
                "未闭合的怪物对象谓词不只按rank启用："+selector);
        var eliteQuest = monsterQuest with { IntData = [3,-1,-4,1] };
        var eliteCatalog = new QuestCatalog(new() { [100] = eliteQuest });
        var eliteActive = active with { ConditionState = 1 };
        foreach (byte rank in new byte[] { 0, 1, 2, 3 })
            That(QuestHuntProgress.MonsterDeath([eliteActive], entry with { QuestAcceptances = [eliteActive] }, eliteCatalog,
                Monster(400, rank))[0].ConditionState == (rank is 1 or 2 ? 0u : 1u),
                "原生-4只匹配普通怪物精英，不替代-2稀有或-3领主");
        That(QuestHuntProgress.TryTargets(quest with { IntData = [3,4,-5,1,1], SubType = 0 }, out _),
            "省略sub的原生0与第五档难度保持实际猎杀合同，不沿旧-1/前三档白名单误拒");
        foreach (var unsupported in new[] { monsterQuest with { IntData = [-2,1,1,1] }, quest with { IntData = [3,-1,-3,10,1] },
            monsterQuest with { IntData = [3,-1,1,512] }, monsterQuest with { IntData = [3,-1,1,1,2] },
            monsterQuest with { IntData = [3,-2,1,1] }, monsterQuest with { IntData = [3,5,1,1] } })
            That(!QuestHuntProgress.TryTargets(unsupported,out _),"非法副本、分类6负码、溢出数量和五元组不能冒充普通四元组猎杀");
        That(QuestHuntProgress.TryTargets(monsterQuest with { Definition = monsterQuest.Definition! with {
            AccountQuest = true, AccountCollisions = [101], RequiredAnswers = [new(102,1)] } },out _),
            "已有账号及回答资格合同不阻止同一接取内的真实猎杀事件");
        Console.WriteLine($"PASS {Count - checks} 猎杀目标、三通道与接取身份隔离断言");
    }

    static DungeonPlannedMonster Monster(int id, byte spawnType = 0)
    {
        var source = new DungeonRoomMonster(id,[id,0,1,0,0,0,1,1],"[fixed]",
            spawnType switch { 1 => "[champion]", 2 => "[super champion]", 3 => "[boss]", _ => "[normal]" },"monster/fixture.mob");
        return new(0,10,id,1,source) { SpawnType = spawnType };
    }
}
