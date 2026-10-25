using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Pvp;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Pvp;

// 入场计划只在锁外构造；运行实例绑定原接取身份，不因同任务再次接取而继承事实。
internal sealed record PvpTrainingEntry(Character Expected, ActiveMainQuest Quest, PvpArcadeMode? Mode)
{
    public bool IsCombo => Mode is null;
    public ImmutableArray<PvpPracticeCombo> Combos { get; init; }=[];
    public static PvpTrainingEntry? PrepareCombo(Character character,StoryQuest quest,ImmutableArray<PvpPracticeCombo> combos)
    {
        var active=character.ActiveMainQuests.SingleOrDefault(row=>row.QuestId==quest.Id);
        if(!PvpTrainingExposure.Allows(character,quest) || active is null || active.AcceptanceId==Guid.Empty || !PvpTrainingProgress.Valid(quest,active.ConditionState) ||
            active.ConditionState==0 || quest.IntData[3]==2 || combos.IsDefaultOrEmpty ||
            combos.Any(row=>row.Job!=character.Job || row.Grow!=character.GrowType)) return null;
        return new(character,active,null){Combos=combos};
    }
    public ImmutableArray<ImmutableHashSet<int>> Rounds { get; init; }=[];
    public ImmutableHashSet<int> RandomRounds { get; init; }=[];

    public static PvpTrainingEntry? Prepare(Character character, StoryQuest quest, PvpArcadeMode mode,
        PvpArcadeOpponents opponents)
    {
        var active=character.ActiveMainQuests.SingleOrDefault(row=>row.QuestId==quest.Id);
        if(!PvpTrainingExposure.Allows(character,quest) || active is null || active.AcceptanceId==Guid.Empty || !PvpTrainingProgress.Valid(quest,active.ConditionState) ||
            active.ConditionState==0 || mode.Index is <0 or >254 || mode.Difficulty is <0 or >2 ||
            mode.Rounds is <1 or >255 || mode.Coins is <0 or >255 ||
            opponents.FixedRounds.Count==0 && opponents.RandomOpponents.IsEmpty) return null;
        return new(character,active,mode);
    }
}
internal sealed record PvpTrainingRun(Guid InstanceId, PvpTrainingEntry Entry, long Generation, long SelectionEpoch)
{
    public PvpArcadeRoundState Round { get; init; }=PvpArcadeRoundState.Initial;
}
internal sealed record PvpTrainingRoundReceipt(PvpTrainingRun Run, bool WonAllRounds);
internal sealed record PvpTrainingLifecycleReceipt(PvpTrainingRun Run, bool Removed, long Version);
