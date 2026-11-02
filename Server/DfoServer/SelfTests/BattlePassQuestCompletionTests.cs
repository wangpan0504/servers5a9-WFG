using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.BattlePass;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassQuestCompletionTests
{
    public static void RunCore()
    {
        int before = Count;
        var parent = new ActiveMainQuest(0, 100, 0) { AcceptanceId = Guid.NewGuid() };
        var child = new ActiveMainQuest(1, 101, 0) { AcceptanceId = Guid.NewGuid() };
        var character = new Character(1, 0, "QuestFact", 7, new(1, 0, 100, 100), null)
            { ActiveMainQuests = [parent, child] };
        int[] Targets(Character actor, ushort id = 100, Guid? acceptance = null, int? archived = null, ActiveMainQuest? terminal = null, byte conditionType = 4) =>
            BattlePassQuestCompletion.CompletedTargets(actor, id, acceptance ?? parent.AcceptanceId, archived, terminal, conditionType).ToArray();
        That(Targets(character).SequenceEqual([100]), "原真实领奖接取计一次，不按奖励数量或当前活动槽扩增");
        That(Targets(character, terminal: child).SequenceEqual([100, 101]), "原44同时真正完成父子两接取保留两项");
        That(Targets(character with { ActiveMainQuests = [parent with { ConditionState = 1 }, child] }, terminal: child).Length == 2,
            "原父任务剩余一次由同批真实已达成末子完成");
        That(Targets(character with { ActiveMainQuests = [parent with { ConditionState = 2 }, child] }, conditionType: 42).SequenceEqual([100]),
            "原42合法非零回答沿原完成规划计一次，不把答案当未完成scalar");
        That(Targets(character with { ActiveMainQuests = [parent with { ConditionState = 3 }, child] }, conditionType: 0).SequenceEqual([100]),
            "原0寻物由原材料交付复验，非零客户端scalar不被新增来源门禁收窄");
        That(Targets(character with { ActiveMainQuests = [parent with { ConditionState = 1 }, child] }).Length == 0,
            "未达成且无真实末子不能借接取计完成");
        foreach (var bad in new[] { character with { Id = 0 }, character with { CompletedQuests = [100] },
            character with { AccountCompletedQuests = [100] }, character with { ActiveMainQuests = [child] } })
            That(Targets(bad).Length == 0, "零角色、历史个人/账号完成及无原接取均不计完成");
        That(Targets(character, acceptance: Guid.Empty).Length == 0 && Targets(character, acceptance: Guid.NewGuid()).Length == 0,
            "空接取及不同历史接取不能制造完成来源");
        That(Targets(character, archived: 99).Length == 0, "已完成父任务后的历史子归档不冒实际完成");
        foreach (var bad in new[] { child with { AcceptanceId = Guid.Empty }, child with { ConditionState = 1 },
            child with { AcceptanceId = parent.AcceptanceId }, child with { QuestId = 100 }, child with { QuestId = 102 } })
            That(Targets(character, terminal: bad).Length == 0, "末子必须属于同快照的原已达成独立接取");
        var definition = new BattlePassMission(13, 1, 100, 13, "完成任务", -1);
        That(BattlePassQuestCompletion.MatchesMission(0, definition), "原日13真实tuple匹配");
        That(BattlePassQuestCompletion.MatchesMission(1, definition with { Id = 41, RequiredCount = 3, Experience = 1000 }),
            "原周41真实tuple匹配");
        foreach (var bad in new[] { definition with { Id = 12 }, definition with { RawTypeCode = 12 },
            definition with { RawParameter = 0 }, definition with { RequiredCount = 2 }, definition with { Experience = 101 } })
            That(!BattlePassQuestCompletion.MatchesMission(0, bad), "变异任务tuple不能借真实完成来源");
        That(!BattlePassQuestCompletion.MatchesMission(1, definition), "不将日任务套用到周分类");
        Console.WriteLine($"PASS {Count - before} BattlePassQuestCompletionTests core");
    }
}
