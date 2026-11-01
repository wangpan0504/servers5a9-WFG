using DfoServer.GameWorld.Dungeons;
using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

// 仅表示匹配普通单人报告的已解析值；完整正文指纹及检查结果由正式网络转换提供。
// SpawnHpSnapshot是客户端创建时当前HP快照，不是服务端恢复的最大HP公式。
internal readonly record struct NativeDungeonDeathParticipant(ushort Kind, ushort ObjectKey,
    ulong CumulativeHpDecrease, ushort PositiveHpDecreaseCount);

internal sealed record NativeDungeonDeathObservation(
    uint MonsterKey, ushort ReporterKey, ushort ParticipantKind, ushort ParticipantKey,
    ulong CumulativeHpDecrease, ushort PositiveHpDecreaseCount, byte RewriteFlag,
    uint SpawnLevel, ulong SpawnHpSnapshot, ulong Unknown18, uint Unknown26,
    bool ChecksumMatches, string BodySha256)
{
    // 旧单人调用保留标量合同；正式网络入口提供原序全列表，不能把首行当作killer。
    public ImmutableArray<NativeDungeonDeathParticipant> Participants { get; init; }
    public ImmutableArray<NativeDungeonDeathParticipant> SourceParticipants => Participants.IsDefault
        ? [new(ParticipantKind,ParticipantKey,CumulativeHpDecrease,PositiveHpDecreaseCount)] : Participants;
}

internal sealed record NativeDungeonDeathResult(DungeonRunOutcome Outcome, ConfirmedDungeonMonsterDeath? Death = null);
internal sealed record NativeDungeonScriptedDeathObservation(uint MonsterKey, uint ActorFlags, bool ChecksumMatches, string BodySha256);
internal sealed record NativeDungeonAltarDeathObservation(ushort MonsterKey, string BodySha256);

// 正式死亡报告的实例与提交身份；来源和目标统计用于审计，不作为死亡门槛。
// 调用方在原正式实例owner内串行使用；缓存只保存本实例已签发事实，不复制在线角色状态。
internal sealed class NativeDungeonCombatAuthority : DungeonCombatAuthority
{
    sealed record IssuedObservation(object Observation, DungeonMap Map, ConfirmedDungeonMonsterDeath Death);
    readonly Dictionary<ushort, IssuedObservation> issued = [];
    SinglePlayerDungeonRun? boundRun;
    long sequence;

    public bool BindTo(SinglePlayerDungeonRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!run.UsesCombatAuthority(this))
            return false;
        if (boundRun is not null)
            return ReferenceEquals(boundRun, run);
        boundRun = run;
        return true;
    }

    public bool MatchesIssuedSpawn(SinglePlayerDungeonRun run, ushort monsterKey, uint level, ulong hp) =>
        ReferenceEquals(boundRun, run) && issued.TryGetValue(monsterKey, out var accepted) &&
        accepted.Map == run.Snapshot.CurrentMap && accepted.Observation is NativeDungeonDeathObservation observation &&
        observation.SpawnLevel == level && observation.SpawnHpSnapshot == hp;

    public NativeDungeonDeathResult ValidateAndIssue(SinglePlayerDungeonRun run, DungeonRunIdentity expectedIdentity,
        NativeDungeonDeathObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (run.CurrentAltar is not null) return new(DungeonRunOutcome.Rejected);
        return Issue(run,expectedIdentity,observation.MonsterKey,observation.ChecksumMatches,observation.BodySha256,observation);
    }

    public NativeDungeonDeathResult ValidateAndIssueScripted(SinglePlayerDungeonRun run, DungeonRunIdentity expectedIdentity,
        NativeDungeonScriptedDeathObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (run.CurrentAltar is not null) return new(DungeonRunOutcome.Rejected);
        return Issue(run,expectedIdentity,observation.MonsterKey,observation.ChecksumMatches,observation.BodySha256,observation);
    }

    public NativeDungeonDeathResult ValidateAndIssueAltar(SinglePlayerDungeonRun run, DungeonRunIdentity expectedIdentity,
        NativeDungeonAltarDeathObservation observation)
    {
        if (run.CurrentAltar is not { } altar || !altar.WasPublished(observation.MonsterKey))
            return new(DungeonRunOutcome.Rejected);
        // CMD255本身没有统计校验字段；完整计数/长度由专用Parser核验，这里只授权原实例已发布的祭坛对象。
        return Issue(run, expectedIdentity, observation.MonsterKey, true, observation.BodySha256, observation);
    }

    NativeDungeonDeathResult Issue(SinglePlayerDungeonRun run, DungeonRunIdentity expectedIdentity, uint monsterKey,
        bool checksumMatches, string fingerprint, object observation)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ArgumentNullException.ThrowIfNull(observation);
        if (!ReferenceEquals(boundRun, run) || !run.UsesCombatAuthority(this))
            return new(DungeonRunOutcome.Rejected);

        var snapshot = run.Snapshot;
        // 旧expected、加载或退出中的报告不能操作当前实例，正文完整性仍由Parser提供。
        if (snapshot.Identity != expectedIdentity ||
            snapshot.Phase is not (SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement) ||
            monsterKey > ushort.MaxValue || !ValidFingerprint(fingerprint) || !checksumMatches)
            return new(DungeonRunOutcome.Rejected);

        ushort key = (ushort)monsterKey;
        if (issued.TryGetValue(key, out var cached))
        {
            bool sameEpoch = snapshot.Identity == cached.Death.Identity ||
                snapshot.Phase == SinglePlayerDungeonPhase.Settlement && cached.Death.Identity.Epoch < long.MaxValue &&
                    snapshot.Identity.Epoch == cached.Death.Identity.Epoch + 1 &&
                    snapshot.Identity == (cached.Death.Identity with { Epoch = snapshot.Identity.Epoch });
            // 同房同提交身份复用原事实，以便SQL失败后继续恢复；新的来源/正文不重签。
            // 跨房或旧epoch只审计忽略，不把旧事实带入新场景发奖。
            return cached.Map == snapshot.CurrentMap && sameEpoch
                ? new(DungeonRunOutcome.Duplicate, cached.Death)
                : new(DungeonRunOutcome.Duplicate);
        }
        // Boss通关已向客户端清除的小怪可能随后补报39；作为清场重放收尾，不重新授奖。
        if (run.IsCulledMonster(key)) return new(DungeonRunOutcome.Duplicate);
        // 只额外签发本Run已登记的有限动态演员；Session先分流为零奖励退休，未知key仍不签发。
        if (run.CurrentLivingMonster(key) is null && run.FindCurrentFiendwarDynamic(expectedIdentity, key) is null)
            return new(DungeonRunOutcome.Accepted);
        if (sequence == long.MaxValue)
            return new(DungeonRunOutcome.Rejected);

        long nextSequence = checked(sequence + 1);
        var death = ConfirmDeath(snapshot.Identity, Guid.NewGuid(), key, nextSequence);
        issued.Add(key, new(observation, snapshot.CurrentMap, death));
        sequence = nextSequence;
        return new(DungeonRunOutcome.Accepted, death);
    }

    static bool ValidFingerprint(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');

}
