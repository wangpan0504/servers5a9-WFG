using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 仅表示匹配普通单人报告的已解析值；完整正文指纹及检查结果由正式网络转换提供。
// SpawnHpSnapshot是客户端创建时当前HP快照，不是服务端恢复的最大HP公式。
internal sealed record NativeDungeonDeathObservation(
    uint MonsterKey, ushort ReporterKey, ushort ParticipantKind, ushort ParticipantKey,
    ulong CumulativeHpDecrease, ushort PositiveHpDecreaseCount, byte RewriteFlag,
    uint SpawnLevel, ulong SpawnHpSnapshot, ulong Unknown18, uint Unknown26,
    bool ChecksumMatches, string BodySha256);

internal sealed record NativeDungeonDeathResult(DungeonRunOutcome Outcome, ConfirmedDungeonMonsterDeath? Death = null);

// 本地HP统计核验能力；不重算伤害、不借用诊断报告，不写库或发送通知。
// 调用方在原正式实例owner内串行使用；缓存只保存本实例已签发事实，不复制在线角色状态。
internal sealed class NativeDungeonCombatAuthority : DungeonCombatAuthority
{
    sealed record IssuedObservation(NativeDungeonDeathObservation Observation, DungeonMap Map, ConfirmedDungeonMonsterDeath Death);
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
        accepted.Map == run.Snapshot.CurrentMap && accepted.Observation.SpawnLevel == level &&
        accepted.Observation.SpawnHpSnapshot == hp;

    public NativeDungeonDeathResult ValidateAndIssue(SinglePlayerDungeonRun run, DungeonRunIdentity expectedIdentity,
        NativeDungeonDeathObservation observation)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ArgumentNullException.ThrowIfNull(observation);
        if (!ReferenceEquals(boundRun, run) || !run.UsesCombatAuthority(this))
            return new(DungeonRunOutcome.Rejected);

        var snapshot = run.Snapshot;
        // 旧expected先拒绝；已确认事实的缓存也不能放行另一房间、加载或退出中的旧报告。
        if (snapshot.Identity != expectedIdentity ||
            snapshot.Phase is not (SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement) ||
            observation.MonsterKey > ushort.MaxValue || !ValidFingerprint(observation.BodySha256))
            return new(DungeonRunOutcome.Rejected);

        ushort key = (ushort)observation.MonsterKey;
        if (issued.TryGetValue(key, out var cached))
        {
            bool sameEpoch = snapshot.Phase == SinglePlayerDungeonPhase.Dungeon
                ? snapshot.Identity == cached.Death.Identity
                : cached.Death.Identity.Epoch < long.MaxValue &&
                    snapshot.Identity.Epoch == cached.Death.Identity.Epoch + 1 &&
                    snapshot.Identity == (cached.Death.Identity with { Epoch = snapshot.Identity.Epoch });
            return cached.Observation == observation && cached.Map == snapshot.CurrentMap && sameEpoch
                ? new(DungeonRunOutcome.Duplicate, cached.Death)
                : new(DungeonRunOutcome.Rejected);
        }
        if (snapshot.Phase != SinglePlayerDungeonPhase.Dungeon || run.CurrentLivingMonster(key) is not { } monster)
            return new(DungeonRunOutcome.Rejected);
        if (observation.ReporterKey != snapshot.Identity.CharacterId ||
            observation.ParticipantKey != snapshot.Identity.CharacterId || observation.ParticipantKind != 0x0111)
            return new(DungeonRunOutcome.Rejected);
        // 仅支持已实收的3145 Boss改写分支；Boss身份取正式生成计划，不由flag授予。
        bool bossRewrite = observation.RewriteFlag == 1;
        if (observation.RewriteFlag != 0 && (!bossRewrite || run.DungeonId != 3 || snapshot.Identity.QuestId != 3145 ||
            snapshot.CurrentMap.Id != 76126 || snapshot.CurrentMap.Kind != "boss" || monster.MonsterId != 107000903 ||
            monster.SpawnType != 3 || monster.CandidateLevel != 3 ||
            monster.Source.PlacementKind != "[fixed]" || monster.Source.RankKind != "[boss]"))
            return new(DungeonRunOutcome.Unsupported);
        if (!observation.ChecksumMatches || (long)observation.SpawnLevel != monster.CandidateLevel ||
            observation.SpawnHpSnapshot == 0 || observation.CumulativeHpDecrease == 0 ||
            observation.PositiveHpDecreaseCount == 0 || sequence == long.MaxValue)
            return new(DungeonRunOutcome.Rejected);
        // 普通prefix统计全部来源，参与者行只是本人分项。单行不代表只有一种HP降低来源。
        // 总量含客户端随机扰动，只核对报告相容性，不构成服务端独立伤害模拟。
        bool validPrefix = bossRewrite
            ? observation.CumulativeHpDecrease >= observation.SpawnHpSnapshot &&
                ValidBossRewritePrefix(observation.Unknown18, observation.Unknown26)
            : observation.Unknown26 >= observation.PositiveHpDecreaseCount &&
                observation.Unknown18 >= observation.CumulativeHpDecrease &&
                observation.Unknown18 >= observation.SpawnHpSnapshot;
        if (!validPrefix)
            return new(DungeonRunOutcome.Rejected);

        // setter统计的是夹取前正降幅，允许overkill；完整u64比较不截高位，不计算溢出的sum+noise。
        long nextSequence = checked(sequence + 1);
        var death = ConfirmDeath(snapshot.Identity, Guid.NewGuid(), key, nextSequence);
        issued.Add(key, new(observation, snapshot.CurrentMap, death));
        sequence = nextSequence;
        return new(DungeonRunOutcome.Accepted, death);
    }

    static bool ValidBossRewritePrefix(ulong total, uint count)
    {
        // V=maxHP+random%10000，total=V+random%200，count=max(1,trunc(V/300))。
        // 只支持正signed64且计数未回绕的范围；两个区间相交仅核验prefix，不独立签发死亡。
        if (total == 0 || total > (ulong)long.MaxValue || count == 0)
            return false;
        ulong totalLower = total > 199 ? total - 199 : 1;
        ulong countLower = count == 1 ? 1 : (ulong)count * 300;
        ulong countUpper = count == 1 ? 599 : countLower + 299;
        return Math.Max(totalLower, countLower) <= Math.Min(total, countUpper);
    }

    static bool ValidFingerprint(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');
}
