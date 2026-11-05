using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 这些统计由匹配客户端自己的事件消费者判定；33没有技能ID/伤害明细，不能声称是服务器战斗证明。
internal static class QuestClientObservation
{
    public static bool Supports(StoryQuest quest) => QuestTargetSchema.Read(quest).State == QuestParameterState.Parsed && quest.TargetKind switch
    {
        QuestTargetKind.UseSkill => quest.IntData[0] is 0 or 1,
        QuestTargetKind.GetScore => quest.IntData[0] is 0 or 4 or 5 or 10,
        QuestTargetKind.EnterMap or QuestTargetKind.MeetApc or QuestTargetKind.MeetMonster or QuestTargetKind.MeetPassiveObject
            or QuestTargetKind.EnemyActionCheck or QuestTargetKind.DamageByEnemy or QuestTargetKind.CheckActiveStatus
            or QuestTargetKind.CheckControl or QuestTargetKind.CheckMyTeamDeadCount or QuestTargetKind.EnemyKillOrder
            or QuestTargetKind.EnemyFewKill => true,
        _ => false
    };

    public static bool Matches(StoryQuest quest, SinglePlayerDungeonRun run)
    {
        if (!Supports(quest) || run.Entry.HasFreeCombatConsumption || run.Entry.IsQuestReplay ||
            run.Phase != SinglePlayerDungeonPhase.Dungeon || run.LastLoadingFact?.Map != run.Snapshot.CurrentMap) return false;
        var parsed = QuestTargetSchema.Read(quest);
        var limits = parsed.DungeonLimits;
        if (!limits.IsEmpty && !limits.Contains(checked((int)run.Entry.DungeonId))) return false;
        return quest.TargetKind switch
        {
            QuestTargetKind.EnterMap => quest.IntData.Contains(run.Snapshot.CurrentMap.Id),
            QuestTargetKind.MeetApc => run.CurrentMonsterPlan.Monsters.Any(row => row.AiCharacter is not null && row.MonsterId == quest.IntData[0]),
            QuestTargetKind.MeetMonster => run.CurrentMonsterPlan.Monsters.Any(row => row.AiCharacter is null && row.MonsterId == quest.IntData[0]),
            // 物件的动态实例另有原生事件，缺少已发布实例绑定时不凭地图模板猜测会面成功。
            QuestTargetKind.MeetPassiveObject => false,
            QuestTargetKind.EnemyActionCheck or QuestTargetKind.DamageByEnemy or QuestTargetKind.CheckActiveStatus
                or QuestTargetKind.CheckControl or QuestTargetKind.CheckMyTeamDeadCount or QuestTargetKind.EnemyKillOrder
                or QuestTargetKind.EnemyFewKill => parsed.Nested.All(nested =>
                    (nested.Dungeon == -1 || nested.Dungeon == run.Entry.DungeonId) &&
                    (nested.Difficulty == -1 || run.Entry.Difficulty >= nested.Difficulty)),
            _ => true
        };
    }
}
