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
        var after = QuestHuntProgress.MonsterDeath([active],entry,catalog,1)[0];
        That(after.ConditionState == 1u + (3u << 18) && after.AcceptanceId == active.AcceptanceId,
            "一条真实匹配死亡推进全部命中目标，保留未匹配通道及接取身份");
        That(QuestHuntProgress.MonsterDeath([after],entry,catalog,2)[0] == after,
            "最低难度不满足时，模板相同也不能推进");
        That(QuestHuntProgress.MonsterDeath([after],entry with { Difficulty = 1 },catalog,2)[0].ConditionState == 1u + (2u << 18),
            "合法难度与对应目标匹配后只减其通道");
        foreach (var stale in new[] { active with { AcceptanceId = Guid.NewGuid() }, active with { AcceptanceId = Guid.Empty },
            active with { Slot = 2 }, active with { ConditionState = initial | (1u << 29) }, active with { ConditionState = initial + 1 } })
            That(QuestHuntProgress.MonsterDeath([stale],entry,catalog,1)[0] == stale,
                "旧实例不能推进重新接取、空身份、换槽或未知scalar编码");
        var passive = quest with { IntData = [3,-1,13099,3,1] };
        var passiveCatalog = new QuestCatalog(new() { [100] = passive });
        var target = active with { ConditionState = 1 };
        var passiveEntry = entry with { QuestAcceptances = [target] };
        That(QuestHuntProgress.MonsterDeath([target],passiveEntry,passiveCatalog,13099)[0] == target &&
            QuestHuntProgress.PassiveDeath([target],passiveEntry,passiveCatalog,13099)[0].ConditionState == 0,
            "相同编号的普通怪物和被动物件只推进对应目标类型");
        var apc = quest with { IntData = [3,-1,13099,2,1] };
        var apcCatalog = new QuestCatalog(new() { [100] = apc });
        That(QuestHuntProgress.MonsterDeath([target],passiveEntry,apcCatalog,13099)[0] == target &&
            QuestHuntProgress.PassiveDeath([target],passiveEntry,apcCatalog,13099)[0] == target &&
            QuestHuntProgress.AiCharacterDeath([target],passiveEntry,apcCatalog,13099)[0].ConditionState == 0,
            "APC类型2只由实际APC死亡推进，同号怪物和被动物件不能替代");
        foreach (var unsupported in new[] { quest with { IntData = [3,-1,13099,0,1] }, quest with { IntData = [3,-1,1,1,512] },
            quest with { IntData = [3,-1,1,1] }, quest with { SubType = 0 }, quest with { Grade = "[daily random]" }, quest with { Grade = "[training]" },
            quest with { MinimumLevel = 255 } })
            That(!QuestHuntProgress.TryTargets(unsupported,out _), "非普通怪物、编码越界或专用状态不借用猎杀成功合同");
        var done = active with { ConditionState = 0 };
        That(QuestHuntProgress.TryTargets(quest with { Grade = "[daily]" },out _) &&
            QuestHuntProgress.TryTargets(quest with { Grade = "[normaly repeat]" },out _),
            "已接入周期与重复接取身份的猎杀使用同一权威事件模型");
        That(QuestHuntProgress.MonsterDeath([done],entry,catalog,1)[0] == done, "达成通道不会下溢或重置");
        var monsterQuest = quest with { Condition = "[hunt monster]", IntData = [3,-1,1,2,3,1,2,3] };
        var monsterCatalog = new QuestCatalog(new() { [100] = monsterQuest });
        var monsterActive = active with { ConditionState = QuestHuntProgress.Initial(monsterQuest) };
        var monsterEntry = entry with { QuestAcceptances = [monsterActive], Difficulty = 2 };
        That(monsterActive.ConditionState == 2+(3u<<9) &&
            QuestHuntProgress.MonsterDeath([monsterActive],monsterEntry,monsterCatalog,1)[0].ConditionState == 1+(3u<<9),
            "分类2四元组保留Full03共用9bit通道，只按实际怪物模板推进");
        That(QuestHuntProgress.MonsterDeath([monsterActive],monsterEntry,monsterCatalog,2)[0] == monsterActive &&
            QuestHuntProgress.MonsterDeath([monsterActive],monsterEntry with { Difficulty = 1 },monsterCatalog,2)[0].ConditionState == 2+(2u<<9),
            "猎杀怪物的指定难度按两端精确匹配，不借用猎杀敌人的最低难度条件");
        That(QuestHuntProgress.PassiveDeath([monsterActive],monsterEntry,monsterCatalog,1)[0] == monsterActive,
            "分类2不能由同编号被动物件事件推进");
        foreach (var unsupported in new[] { monsterQuest with { IntData = [-2,1,1,1] }, monsterQuest with { IntData = [3,-1,-5,53] },
            monsterQuest with { IntData = [3,-1,1,512] }, monsterQuest with { IntData = [3,-1,1,1,2] } })
            That(!QuestHuntProgress.TryTargets(unsupported,out _),"特殊选择符、溢出数量和五元组不能冒充普通四元组猎杀");
        That(QuestHuntProgress.TryTargets(monsterQuest with { Definition = monsterQuest.Definition! with {
            AccountQuest = true, AccountCollisions = [101], RequiredAnswers = [new(102,1)] } },out _),
            "已有账号及回答资格合同不阻止同一接取内的真实猎杀事件");
        Console.WriteLine($"PASS {Count - checks} 猎杀目标、三通道与接取身份隔离断言");
    }
}
