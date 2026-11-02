using System.Collections.Frozen;
using System.Collections.Immutable;

namespace DfoServer.GameWorld.Quests;

// 领域分类与原生编号分别保存；50、51、61没有已确认映射，不按空位补值。
internal enum QuestTargetKind
{
    Unknown, Seeking, ConditionUnderClear, HuntMonster, ClearMap, MeetNpc, PvpRank, HuntEnemy, PowerWarWin, PowerWarPoint, Mobile, UseItem, GetScore, ClearQuest, CheckTime, CheckLife, AmplifyItem, DisjointItem, EquippedItem, GetItem, CustomQuest, SendChatting, TurnGoldCard, UseFortuneCoin, MeetSecretNpc, UiClick, SeekAndMeetNpc, AssaultCount, PvpQuest, ComboSuccess, GetItemCheckIndex, AreaBase, LevelUp, PvpMatch, PvpWin, PvpLose, MeetApc, MeetMonster, MeetPassiveObject, QuestAccept, FirstGrow, Awakening, RaidPhaseClear, Question, UserLevel, QuestClear, ClearDungeon, ClearQuestByGrade, UseSkill, ExpertJob, PlayOnly, EnemyActionCheck, DamageByEnemy, CheckActiveStatus, CheckControl, CheckMyTeamDeadCount, EnemyKillOrder, EnemyFewKill, SeasonSpecialClear, CustomMultiCondition, PvpTotalMatch, PvpTotalWin, ReachRange, LookCinematic, SelfDisjointItem, EpicProduction, CheckItem, EnterMap, ClearMultiQuest, ClearTreasureDungeon, MissionRoulette, AccountAchievementConquer, OutlawValue, UnconfirmedQuestClear
}

[Flags]
internal enum QuestProgressSource
{
    None = 0, Inventory = 1, MonsterDeath = 2, EnemyDeath = 4, RoomClear = 8,
    DungeonClear = 16, Npc = 32, Character = 64, RelatedQuest = 128,
    ClientScene = 256, Answer = 512, Dedicated = 1024, Pvp = 2048
}

internal enum QuestTargetEvidence { ClientConsumer, ClientMapping, ResourceOnly, Unassigned }
internal sealed record QuestTargetRegistration(string Tag, QuestTargetKind Kind,
    QuestProgressSource Sources, byte? NativeType, QuestTargetEvidence Evidence = QuestTargetEvidence.ClientConsumer)
{
    public byte? CompletionType => NativeType;
}

// 04191170的名称赋值不等于玩法可用；参数、事件、提交和投影继续分别检查。
internal static class QuestTargetTypes
{
    public static ImmutableArray<QuestTargetRegistration> All { get; } =
    [
        new("[seeking]", QuestTargetKind.Seeking, QuestProgressSource.Inventory | QuestProgressSource.DungeonClear, 0),
        new("[condition under clear]", QuestTargetKind.ConditionUnderClear, QuestProgressSource.DungeonClear, 1),
        new("[hunt monster]", QuestTargetKind.HuntMonster, QuestProgressSource.MonsterDeath, 2),
        new("[clear map]", QuestTargetKind.ClearMap, QuestProgressSource.RoomClear, 3),
        new("[meet npc]", QuestTargetKind.MeetNpc, QuestProgressSource.Npc, 4),
        new("[pvp rank]", QuestTargetKind.PvpRank, QuestProgressSource.Character, 5),
        new("[hunt enemy]", QuestTargetKind.HuntEnemy, QuestProgressSource.EnemyDeath, 6),
        new("[powerwar win]", QuestTargetKind.PowerWarWin, QuestProgressSource.Dedicated, 7),
        new("[powerwar point]", QuestTargetKind.PowerWarPoint, QuestProgressSource.Dedicated, 8),
        new("[mobile]", QuestTargetKind.Mobile, QuestProgressSource.Dedicated, 9, QuestTargetEvidence.ClientMapping),
        new("[use item]", QuestTargetKind.UseItem, QuestProgressSource.Inventory, 10),
        new("[get score]", QuestTargetKind.GetScore, QuestProgressSource.ClientScene, 11),
        new("[clear quest]", QuestTargetKind.ClearQuest, QuestProgressSource.RelatedQuest, 12),
        new("[check time]", QuestTargetKind.CheckTime, QuestProgressSource.ClientScene, 13),
        new("[check life]", QuestTargetKind.CheckLife, QuestProgressSource.ClientScene, 14),
        new("[amplify item]", QuestTargetKind.AmplifyItem, QuestProgressSource.Inventory, 15),
        new("[disjoint item]", QuestTargetKind.DisjointItem, QuestProgressSource.Inventory, 16),
        new("[equipped item]", QuestTargetKind.EquippedItem, QuestProgressSource.Inventory, 17),
        new("[get item]", QuestTargetKind.GetItem, QuestProgressSource.Inventory, 18),
        new("[custom quest]", QuestTargetKind.CustomQuest, QuestProgressSource.Dedicated, 19),
        new("[send chatting]", QuestTargetKind.SendChatting, QuestProgressSource.ClientScene, 20),
        new("[turn gold card]", QuestTargetKind.TurnGoldCard, QuestProgressSource.Dedicated, 21),
        new("[use fortune coin]", QuestTargetKind.UseFortuneCoin, QuestProgressSource.Inventory, 22),
        new("[meet secret npc]", QuestTargetKind.MeetSecretNpc, QuestProgressSource.Npc, 23),
        new("[ui click]", QuestTargetKind.UiClick, QuestProgressSource.ClientScene, 24),
        new("[seek n meet npc]", QuestTargetKind.SeekAndMeetNpc, QuestProgressSource.Inventory | QuestProgressSource.Npc, 25),
        new("[assault count]", QuestTargetKind.AssaultCount, QuestProgressSource.Dedicated, 26),
        new("[pvp quest]", QuestTargetKind.PvpQuest, QuestProgressSource.Pvp, 27),
        new("[combo success]", QuestTargetKind.ComboSuccess, QuestProgressSource.ClientScene, 28, QuestTargetEvidence.ClientMapping),
        new("[get item check index]", QuestTargetKind.GetItemCheckIndex, QuestProgressSource.Inventory, 29),
        new("[area base]", QuestTargetKind.AreaBase, QuestProgressSource.Dedicated, 30, QuestTargetEvidence.ClientMapping),
        new("[level up]", QuestTargetKind.LevelUp, QuestProgressSource.Character, 31),
        new("[pvp match]", QuestTargetKind.PvpMatch, QuestProgressSource.Pvp, 32),
        new("[pvp win]", QuestTargetKind.PvpWin, QuestProgressSource.Pvp, 33),
        new("[pvp lose]", QuestTargetKind.PvpLose, QuestProgressSource.Pvp, 34),
        new("[meet apc]", QuestTargetKind.MeetApc, QuestProgressSource.ClientScene, 35),
        new("[meet monster]", QuestTargetKind.MeetMonster, QuestProgressSource.ClientScene, 36),
        new("[meet passive object]", QuestTargetKind.MeetPassiveObject, QuestProgressSource.ClientScene, 37),
        new("[quest accept]", QuestTargetKind.QuestAccept, QuestProgressSource.RelatedQuest, 38),
        new("[first grow]", QuestTargetKind.FirstGrow, QuestProgressSource.Character, 39),
        new("[awakening]", QuestTargetKind.Awakening, QuestProgressSource.Character, 40),
        new("[raid phase clear]", QuestTargetKind.RaidPhaseClear, QuestProgressSource.Dedicated, 41),
        new("[question]", QuestTargetKind.Question, QuestProgressSource.Answer, 42),
        new("[user level]", QuestTargetKind.UserLevel, QuestProgressSource.Character, 43),
        new("[quest clear]", QuestTargetKind.QuestClear, QuestProgressSource.RelatedQuest, 44),
        new("[clear a dungeon]", QuestTargetKind.ClearDungeon, QuestProgressSource.DungeonClear, 45),
        new("[clear quest by grade]", QuestTargetKind.ClearQuestByGrade, QuestProgressSource.RelatedQuest, 46),
        new("[use skill]", QuestTargetKind.UseSkill, QuestProgressSource.ClientScene, 47),
        new("[expert job]", QuestTargetKind.ExpertJob, QuestProgressSource.Dedicated, 48, QuestTargetEvidence.ClientMapping),
        new("[play only]", QuestTargetKind.PlayOnly, QuestProgressSource.Character, 49),
        new("[enemy action check]", QuestTargetKind.EnemyActionCheck, QuestProgressSource.ClientScene, 52),
        new("[damage by enemy]", QuestTargetKind.DamageByEnemy, QuestProgressSource.ClientScene, 53),
        new("[check active status]", QuestTargetKind.CheckActiveStatus, QuestProgressSource.ClientScene, 54),
        new("[check control]", QuestTargetKind.CheckControl, QuestProgressSource.ClientScene, 55),
        new("[check my team dead count]", QuestTargetKind.CheckMyTeamDeadCount, QuestProgressSource.ClientScene, 56),
        new("[enemy kill order]", QuestTargetKind.EnemyKillOrder, QuestProgressSource.EnemyDeath, 57),
        new("[enemy few kill]", QuestTargetKind.EnemyFewKill, QuestProgressSource.EnemyDeath, 58, QuestTargetEvidence.ClientMapping),
        new("[season special clear]", QuestTargetKind.SeasonSpecialClear, QuestProgressSource.Dedicated, 59, QuestTargetEvidence.ClientMapping),
        new("[custom multi condition]", QuestTargetKind.CustomMultiCondition, QuestProgressSource.Dedicated, 60),
        new("[pvp total match]", QuestTargetKind.PvpTotalMatch, QuestProgressSource.Pvp, 62, QuestTargetEvidence.ClientMapping),
        new("[pvp total win]", QuestTargetKind.PvpTotalWin, QuestProgressSource.Pvp, 63, QuestTargetEvidence.ClientMapping),
        new("[reach the range]", QuestTargetKind.ReachRange, QuestProgressSource.ClientScene, 64),
        new("[look cinematic]", QuestTargetKind.LookCinematic, QuestProgressSource.ClientScene, 65),
        new("[self disjoint item]", QuestTargetKind.SelfDisjointItem, QuestProgressSource.Inventory, 66),
        new("[epic production]", QuestTargetKind.EpicProduction, QuestProgressSource.Dedicated, 67),
        new("[check item]", QuestTargetKind.CheckItem, QuestProgressSource.Inventory, 68),
        new("[enter map]", QuestTargetKind.EnterMap, QuestProgressSource.ClientScene, 69),
        new("[clear multi quest]", QuestTargetKind.ClearMultiQuest, QuestProgressSource.RelatedQuest, 70),
        new("[clear treasure dungeon]", QuestTargetKind.ClearTreasureDungeon, QuestProgressSource.Dedicated, 71),
        new("[mission roulette]", QuestTargetKind.MissionRoulette, QuestProgressSource.Dedicated, null, QuestTargetEvidence.Unassigned),
        new("[account achievement conquer]", QuestTargetKind.AccountAchievementConquer, QuestProgressSource.Dedicated, null, QuestTargetEvidence.ResourceOnly),
        new("[outlaw value]", QuestTargetKind.OutlawValue, QuestProgressSource.Dedicated, null, QuestTargetEvidence.ResourceOnly),
        new("[qust clesr]", QuestTargetKind.UnconfirmedQuestClear, QuestProgressSource.Dedicated, null, QuestTargetEvidence.ResourceOnly)
    ];

    static readonly FrozenDictionary<string, QuestTargetRegistration> ByTag = All.ToFrozenDictionary(row => row.Tag, StringComparer.Ordinal);
    static readonly FrozenSet<byte> NativeTypes = All.Where(row => row.NativeType.HasValue).Select(row => row.NativeType!.Value).ToFrozenSet();
    public static QuestTargetRegistration? Find(string tag) => ByTag.GetValueOrDefault(tag);
    public static bool IsNative(byte type) => NativeTypes.Contains(type);
}

internal readonly record struct QuestSeekingObjective(int MaterialLength, int? MinimumClearDifficulty, int? Npc)
{
    // 保留原int data；尾部难度或NPC不是材料，也不能被领奖、放弃和跳过当成物品编号。
    public static bool TryRead(StoryQuest quest, out QuestSeekingObjective objective)
    {
        objective = default;
        bool meet = quest.TargetKind == QuestTargetKind.SeekAndMeetNpc;
        bool recommended = quest.TargetKind == QuestTargetKind.Seeking && quest.SubType == 20;
        if (quest.TargetKind is not (QuestTargetKind.Seeking or QuestTargetKind.SeekAndMeetNpc or QuestTargetKind.CheckItem)) return false;
        int length = quest.IntData.Count - (meet || recommended ? 1 : 0);
        if (length <= 0 || length % 2 != 0) return false;
        for (int at = 0; at < length; at += 2)
            if (quest.IntData[at] < (recommended ? 1 : -7) || quest.IntData[at + 1] <= 0) return false;
        if (meet && quest.IntData[^1] <= 0 || recommended && quest.IntData[^1] is < -1 or > 4) return false;
        objective = new(length, recommended ? quest.IntData[^1] : null, meet ? quest.IntData[^1] : null);
        return true;
    }
}
