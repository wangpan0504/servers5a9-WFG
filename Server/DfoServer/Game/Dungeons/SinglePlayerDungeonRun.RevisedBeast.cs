using DfoServer.GameWorld.Dungeons;
namespace DfoServer.Game.Dungeons;

internal sealed record RevisedBeastBossProgress(int MonsterId, bool Killed, ulong? ReportedSpawnHp,
    ulong? ReportedHpDecrease);
internal sealed record RevisedBeastTerminal(DungeonRunIdentity Identity, bool Success,
    IReadOnlyList<RevisedBeastBossProgress> Bosses);

internal sealed partial class SinglePlayerDungeonRun
{
    readonly Dictionary<int,RevisedBeastBossProgress> revisedBeastProgress=[];
    public RevisedBeastStage? RevisedBeastAnneStage { get; private set; }
    public RevisedBeastTerminal? RevisedBeastTerminal { get; private set; }
    bool RevisedBeastVisible(ushort key) => !entry.IsRevisedBeast || current.Plan.Room.Map.Position != new DungeonPoint(1,0) ||
        RevisedBeastTerminal is null;

    // 两位演员同房建立，客户端末动作才能按模板ID找到歇罗；服务端击杀资格仍按顺序开放。
    bool RevisedBeastCombatReady(ushort key) => !entry.IsRevisedBeast || current.Plan.Room.Map.Position != new DungeonPoint(1,0) ||
        RevisedBeastTerminal is null && entry.RevisedBeastEncounter is { } rules && current.Plan.Monsters.Any(actor=>actor.ObjectKey==key &&
            actor.MonsterId==(current.Plan.Monsters.Any(anne=>anne.MonsterId==rules.AnneId && !current.Dead.Contains(anne.ObjectKey))
                ? rules.AnneId : rules.HierroId));

    DungeonDeathPreparation PrepareRevisedBeastDeath(ConfirmedDungeonMonsterDeath confirmed,DungeonPlannedMonster target)
    {
        if (entry.RevisedBeastEncounter is not { } rules || RevisedBeastTerminal is not null ||
            target.MonsterId != rules.AnneId && target.MonsterId != rules.HierroId)
            return new(DungeonRunOutcome.Rejected);
        bool final=target.MonsterId == rules.HierroId;
        var next=final ? identity with { Epoch=checked(identity.Epoch+1) } : identity;
        var snapshot=new DungeonRunSnapshot(next,final ? SinglePlayerDungeonPhase.Settlement : SinglePlayerDungeonPhase.Dungeon,
            current.Plan.Room.Map,final ? [] : current.Plan.Monsters.Where(actor=>actor.MonsterId == rules.HierroId).Select(actor=>actor.ObjectKey).ToArray())
            { MapOperation=mapOperation,MapPayload=mapPayload,ConsumedFatigue=consumedFatigue,BossPosition=entry.BossMap.Position };
        pendingDeath=new(confirmed,target,new(DungeonRunOutcome.Accepted,snapshot));
        return new(DungeonRunOutcome.Accepted,pendingDeath);
    }

    void ConfirmRevisedBeastDeath(PreparedDungeonDeath prepared)
    {
        var rules=entry.RevisedBeastEncounter;
        if (rules is null || current.Plan.Room.Map.Position != new DungeonPoint(1,0)) return;
        var old=revisedBeastProgress.GetValueOrDefault(prepared.Monster.MonsterId);
        revisedBeastProgress[prepared.Monster.MonsterId]=new(prepared.Monster.MonsterId,true,old?.ReportedSpawnHp,old?.ReportedHpDecrease);
        mapOperation=prepared.Result.Snapshot.MapOperation; mapPayload=prepared.Result.Snapshot.MapPayload;
        if (prepared.Monster.MonsterId == rules.AnneId) RevisedBeastAnneStage ??= new(identity,rules.AnneId);
        if (prepared.Monster.MonsterId == rules.HierroId) FreezeRevisedBeastTerminal(true);
    }

    internal void RecordRevisedBeastDeathStatistics(int monsterId,NativeDungeonDeathObservation? report)
    {
        if (!entry.IsRevisedBeast || report is null || !revisedBeastProgress.TryGetValue(monsterId,out var row)) return;
        // 原报告统计只作来源记录，不能把未释义的HP值换算成已确认的原生奖励。
        revisedBeastProgress[monsterId]=row with { ReportedSpawnHp=report.SpawnHpSnapshot,
            ReportedHpDecrease=report.CumulativeHpDecrease };
        if (RevisedBeastTerminal is { Success:true }) FreezeRevisedBeastTerminal(true);
    }

    internal bool FinishRevisedBeastOnDeathTimeout()
    {
        if (!entry.IsRevisedBeast || entry.RevisedBeastEncounter is null || RevisedBeastAnneStage is null || RevisedBeastTerminal is not null ||
            HasPendingDeath || HasPendingSettlement || Phase != SinglePlayerDungeonPhase.Dungeon) return false;
        identity=identity with { Epoch=checked(identity.Epoch+1) };
        Phase=SinglePlayerDungeonPhase.Settlement;
        FreezeRevisedBeastTerminal(false);
        return true;
    }

    void FreezeRevisedBeastTerminal(bool success)
    {
        var rules=entry.RevisedBeastEncounter!;
        RevisedBeastTerminal=new(identity,success,new[]{rules.AnneId,rules.HierroId}
            .Select(id=>revisedBeastProgress.GetValueOrDefault(id) ?? new(id,false,null,null)).ToArray());
    }
}
