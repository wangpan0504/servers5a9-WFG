using System.Security.Cryptography;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Parsers;

namespace DfoServer;

internal static class NativeDungeonCombatAuthorityTests
{
    public static void Run()
    {
        int checks = 0;
        var (authority, run) = Fixture(bind: false);
        var report = Observation(100);
        Check(authority.ValidateAndIssue(run, run.Snapshot.Identity, report).Outcome == DungeonRunOutcome.Rejected,
            "未绑定实例不能签发");
        Check(authority.BindTo(run) && authority.BindTo(run), "exact权威只绑定一个run，同run重复绑定无副作用");
        Check(authority.ValidateAndIssue(run, run.Snapshot.Identity, report).Outcome == DungeonRunOutcome.Rejected,
            "未完成房间加载不能签发");
        Load(run);
        var identity = run.Snapshot.Identity;
        foreach (var wrong in new[]
        {
            identity with { CharacterId = 98 }, identity with { QuestId = 3146 }, identity with { TaskAttempt = 2 },
            identity with { InstanceId = Guid.NewGuid() }, identity with { Epoch = identity.Epoch - 1 },
            identity with { Epoch = identity.Epoch + 1 }
        })
            Check(Rejected(authority.ValidateAndIssue(run, wrong, report)), "角色/任务/尝试/实例/旧新epoch不符拒绝");

        foreach (var invalid in new[]
        {
            report with { MonsterKey = 65536 }, report with { MonsterKey = 999 }, report with { MonsterKey = 102 },
            report with { ReporterKey = 98 }, report with { ParticipantKey = 98 }, report with { ParticipantKind = 0x0211 },
            report with { SpawnLevel = 2 }, report with { SpawnLevel = uint.MaxValue }, report with { SpawnHpSnapshot = 0 },
            report with { CumulativeHpDecrease = 0 },
            report with { PositiveHpDecreaseCount = 0, Unknown26 = 0 }, report with { Unknown26 = 2 },
            report with { Unknown18 = 503 },
            report with { CumulativeHpDecrease = 400, Unknown18 = 503, Unknown26 = 4 },
            report with { CumulativeHpDecrease = 0, Unknown18 = 800, Unknown26 = 4 },
            report with { Unknown18 = 503, Unknown26 = 4 },
            report with { ChecksumMatches = false }, report with { BodySha256 = new string('A', 63) },
            report with { BodySha256 = new string('G', 64) }, report with { BodySha256 = null! }
        })
            Check(Rejected(authority.ValidateAndIssue(run, identity, invalid)), "本地HP统计/归属/完整指纹门槛拒绝");
        foreach (byte rewrite in new byte[] { 1, 255 })
        {
            var unsupported = authority.ValidateAndIssue(run, identity, report with { RewriteFlag = rewrite });
            Check(unsupported.Outcome == DungeonRunOutcome.Unsupported && unsupported.Death is null,
                "限定3145 Boss以外的统计改写不作为普通死亡事实");
        }

        var first = authority.ValidateAndIssue(run, identity, report);
        var death = first.Death!;
        Check(first.Outcome == DungeonRunOutcome.Accepted && death.EventId != Guid.Empty && death.CombatSequence == 1 &&
            death.Identity == identity && death.MonsterKey == 100 && ReferenceEquals(death.Authority, authority),
            "首次完全合格才签发；之前拒绝和Unsupported不消费序号");
        Check(run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 100, 101 }),
            "签发不直接消费死亡、清房或奖励状态");
        Check(Duplicate(authority.ValidateAndIssue(run, identity, report), death), "精确重放返回原事实，不重新签发");
        foreach (var changed in new[]
        {
            report with { ReporterKey = 98 }, report with { ParticipantKind = 0x0211 }, report with { ParticipantKey = 98 },
            report with { CumulativeHpDecrease = 505, Unknown18 = 521 }, report with { PositiveHpDecreaseCount = 4, Unknown26 = 4 },
            report with { RewriteFlag = 1 }, report with { SpawnLevel = 2 }, report with { SpawnHpSnapshot = 503 },
            report with { Unknown18 = 521 }, report with { Unknown26 = 4 }, report with { ChecksumMatches = false },
            report with { BodySha256 = new string('B', 64) }, report with { BodySha256 = new string('a', 64) }
        })
            Check(Rejected(authority.ValidateAndIssue(run, identity, changed)),
                "同key完整观察任意变化拒绝，包括未知前缀变化所反映的正文指纹");

        var preparation = run.PrepareConfirmedDeath(death);
        var pending = preparation.Pending!;
        Check(preparation.Outcome == DungeonRunOutcome.Accepted && run.IsPendingDeath(pending), "原事实可进入现有Prepare");
        var pendingReplay = authority.ValidateAndIssue(run, identity, report);
        Check(Duplicate(pendingReplay, death) && ReferenceEquals(run.PrepareConfirmedDeath(pendingReplay.Death!).Pending, pending),
            "SQL结果未定的重试仍使用原事实和同一准备对象");
        Check(run.CancelUncommittedDeath(pending), "明确SQL回滚取消原准备");
        var rollbackReplay = authority.ValidateAndIssue(run, identity, report);
        var retried = run.PrepareConfirmedDeath(rollbackReplay.Death!);
        Check(Duplicate(rollbackReplay, death) && retried.Outcome == DungeonRunOutcome.Accepted &&
            ReferenceEquals(retried.Pending!.Death, death) && !ReferenceEquals(retried.Pending, pending),
            "回滚后再Prepare保留同一EventId与序号，不重签");
        Check(run.ConfirmCommittedDeath(retried.Pending!).Outcome == DungeonRunOutcome.Accepted &&
            run.CurrentLivingMonster(100) is null && Duplicate(authority.ValidateAndIssue(run, identity, report), death),
            "确认后的本房精确重复返回原事实，已死key不再签发");
        Check(run.PrepareConfirmedDeath(death).Outcome == DungeonRunOutcome.Duplicate,
            "原已提交事实继续由Run识别Duplicate，不再输出准备或奖励");

        var secondReport = Observation(101) with { CumulativeHpDecrease = 600, Unknown18 = 799 };
        var second = authority.ValidateAndIssue(run, identity, secondReport);
        Check(second.Outcome == DungeonRunOutcome.Accepted && second.Death!.CombatSequence == 2 &&
            second.Death.EventId != death.EventId, "允许夹取前overkill及总统计差异，另一key只递增一次");
        Confirm(run, second.Death!);
        Check(run.Move(identity, Guid.NewGuid(), new(1, 0)).Outcome == DungeonRunOutcome.Accepted, "合成首房清空后过房");
        Check(Rejected(authority.ValidateAndIssue(run, identity, report)) &&
            Rejected(authority.ValidateAndIssue(run, run.Snapshot.Identity, report)), "过房旧epoch及Loading中的缓存都拒绝");
        Load(run);
        var bossIdentity = run.Snapshot.Identity;
        Check(Rejected(authority.ValidateAndIssue(run, bossIdentity, report)), "当前新房不能借旧房缓存事实");
        var bossReport = Observation(102);
        var boss = authority.ValidateAndIssue(run, bossIdentity, bossReport);
        Check(boss.Outcome == DungeonRunOutcome.Accepted && boss.Death!.CombatSequence == 3, "Boss房合成观察独立签发");
        Confirm(run, boss.Death!);
        var settlementIdentity = run.Snapshot.Identity;
        Check(run.Phase == SinglePlayerDungeonPhase.Settlement && settlementIdentity.Epoch == bossIdentity.Epoch + 1 &&
            Duplicate(authority.ValidateAndIssue(run, settlementIdentity, bossReport), boss.Death!),
            "Settlement本房精确重复返回原事实，接受Run明确的一次终止epoch推进");
        Check(Rejected(authority.ValidateAndIssue(run, bossIdentity, bossReport)), "Settlement旧expected epoch先拒绝");
        Check(Rejected(authority.ValidateAndIssue(run, settlementIdentity, bossReport with { Unknown18 = 521 })) &&
            Rejected(authority.ValidateAndIssue(run, settlementIdentity, secondReport)) &&
            Rejected(authority.ValidateAndIssue(run, settlementIdentity, Observation(103))),
            "Settlement篡改、旧房及无缓存观察均不产生新事实");
        Check(run.Return(settlementIdentity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted &&
            Rejected(authority.ValidateAndIssue(run, run.Snapshot.Identity, bossReport)), "Returned不放行缓存或新签发");

        var (_, otherRun) = Fixture(authority, bind: false);
        Load(otherRun);
        Check(!authority.BindTo(otherRun) && Rejected(authority.ValidateAndIssue(otherRun, otherRun.Snapshot.Identity, report)),
            "同权威不能换绑另一实例，旧实例报告不能影响新对象");
        var wrongAuthority = new NativeDungeonCombatAuthority();
        Check(!wrongAuthority.BindTo(otherRun) && Rejected(wrongAuthority.ValidateAndIssue(otherRun, otherRun.Snapshot.Identity, report)),
            "非exact实例权威不能绑定或签发");

        // 正文仅供Parser核对实收统计；签发时actor/key明确换成合成夹具，不重现原实机实例身份。
        foreach (var sample in new (ushort Key, ulong Total, uint Count, ulong Own, ushort OwnCount, string Body)[]
        {
            (421, 831, 3, 696, 2, "A501000004000000000000000000000000003F03000000000000030000000111010400B80200000000000002008902F200000000004C490003000000F8010000000000009A01DE009A01DE0007000A000B00000013C1E948"),
            (468, 1728, 6, 1528, 5, "D40100000400000000000000000000000000C006000000000000060000000111010400F8050000000000000500F802CF0000000000911B0003000000F8010000000000003D02C5002C02C50007000C000C000000AE73A489"),
            (523, 1486, 5, 1415, 4, "0B0200000400000000000000000000000000CE0500000000000005000000011101040087050000000000000400D002FC000000000099110003000000F8010000000000008302D0003F02060107000C001900000006CF40E1"),
            (577, 1355, 3, 1164, 2, "4102000004000000000000000000000000004B050000000000000300000001110104008C040000000000000200AA02F800000000007B5E0003000000F801000000000000870207017B02FD0007000C002100000058A21776"),
            (636, 1841, 6, 1735, 5, "7C02000004000000000000000000000000003107000000000000060000000111010400C70600000000000005006E01CE0000000000D93C0003000000F8010000000000007301DE00EF00DE0007000D002F000000D6E88C44")
        })
        {
            byte[] body = Convert.FromHexString(sample.Body);
            Check(DungeonRequests.TryParseDeath(body, out var parsed) && parsed?.SinglePlayerStatistics is not null,
                "控制技能死亡实收88B可按单参与者正文解析 " + sample.Key);
            var request = parsed!;
            var statistics = request.SinglePlayerStatistics!;
            Check(request.ObjectKey == sample.Key && request.Reporter == 4 && statistics.ChecksumMatches &&
                statistics.Participant.Kind == 0x0111 && statistics.Participant.ObjectKey == 4 &&
                statistics.Unknown18 == sample.Total && statistics.Unknown26 == sample.Count &&
                statistics.Participant.CumulativeHpDecrease == sample.Own &&
                statistics.Participant.PositiveHpDecreaseCount == sample.OwnCount &&
                statistics.RewriteFlag == 0 && statistics.SpawnLevel == 3 && statistics.SpawnHpSnapshot == 504,
                "控制技能死亡实收检查值及完整统计不变 " + sample.Key);

            var (sampleAuthority, sampleRun) = Fixture();
            Load(sampleRun);
            var sampleIdentity = sampleRun.Snapshot.Identity;
            var sampleReport = Observation(100) with
            {
                CumulativeHpDecrease = statistics.Participant.CumulativeHpDecrease,
                PositiveHpDecreaseCount = statistics.Participant.PositiveHpDecreaseCount,
                Unknown18 = statistics.Unknown18, Unknown26 = statistics.Unknown26,
                SpawnLevel = statistics.SpawnLevel, SpawnHpSnapshot = statistics.SpawnHpSnapshot,
                RewriteFlag = statistics.RewriteFlag, ChecksumMatches = statistics.ChecksumMatches,
                BodySha256 = Convert.ToHexString(SHA256.HashData(body))
            };
            var accepted = sampleAuthority.ValidateAndIssue(sampleRun, sampleIdentity, sampleReport);
            Check(accepted.Outcome == DungeonRunOutcome.Accepted && accepted.Death!.CombatSequence == 1,
                "总次数多于本人次数的控制技能统计可签发合成事实 " + sample.Key);
            Check(Duplicate(sampleAuthority.ValidateAndIssue(sampleRun, sampleIdentity, sampleReport), accepted.Death!),
                "控制技能统计精确重放不重复签发 " + sample.Key);
            Check(Rejected(sampleAuthority.ValidateAndIssue(sampleRun, sampleIdentity,
                sampleReport with { Unknown18 = sampleReport.Unknown18 + 1 })),
                "同key改写相容统计也不能替换已签发事实 " + sample.Key);
            Check(sampleRun.Move(sampleIdentity, Guid.NewGuid(), new(1, 0)).Outcome == DungeonRunOutcome.Rejected,
                "控制技能报告签发后尚未提交仍不能过房 " + sample.Key);
            Confirm(sampleRun, accepted.Death!);
            Check(Duplicate(sampleAuthority.ValidateAndIssue(sampleRun, sampleIdentity, sampleReport), accepted.Death!) &&
                sampleRun.Move(sampleIdentity, Guid.NewGuid(), new(1, 0)).Outcome == DungeonRunOutcome.Rejected,
                "控制技能死亡已确认可幂等重放，但存活怪未清空仍不能过房 " + sample.Key);
            Confirm(sampleRun, sampleAuthority.ValidateAndIssue(sampleRun, sampleIdentity, Observation(101)).Death!);
            Check(sampleRun.Move(sampleIdentity, Guid.NewGuid(), new(1, 0)).Outcome == DungeonRunOutcome.Accepted,
                "控制技能统计与普通统计的死亡均确认后才能过房 " + sample.Key);
        }

        foreach (var sample in new[]
        {
            report with { CumulativeHpDecrease = 503 }, report with { Unknown18 = 704 },
            report with { Unknown18 = 4600 },
            report with { CumulativeHpDecrease = 400, Unknown18 = 800 },
            report with { Unknown26 = 4 }, report with { Unknown26 = 65539 },
            report with { CumulativeHpDecrease = 400, Unknown18 = 800, Unknown26 = 4 },
            report with
            {
                SpawnHpSnapshot = 1UL << 40, CumulativeHpDecrease = 400, Unknown18 = (1UL << 40) + 1000,
                PositiveHpDecreaseCount = ushort.MaxValue, Unknown26 = uint.MaxValue
            }
        })
        {
            var (mixedAuthority, mixedRun) = Fixture();
            Load(mixedRun);
            var mixed = mixedAuthority.ValidateAndIssue(mixedRun, mixedRun.Snapshot.Identity, sample);
            Check(mixed.Outcome == DungeonRunOutcome.Accepted && mixed.Death!.CombatSequence == 1,
                "同次数或更多来源均核验总终点，不限制总量差或本人独自覆盖HP，保留u64/u16/u32完整宽度");
            Check(Duplicate(mixedAuthority.ValidateAndIssue(mixedRun, mixedRun.Snapshot.Identity, sample), mixed.Death!),
                "完整宽度混合来源统计精确重放仍幂等");
        }
        var (mixedHighAuthority, mixedHighRun) = Fixture();
        Load(mixedHighRun);
        Check(Rejected(mixedHighAuthority.ValidateAndIssue(mixedHighRun, mixedHighRun.Snapshot.Identity, report with
        {
            SpawnHpSnapshot = 1UL << 40, CumulativeHpDecrease = 400, Unknown18 = 800, Unknown26 = 4
        })), "混合来源总终点低于完整u64创建HP时拒绝，不截高位放行");

        var (highAuthority, highRun) = Fixture();
        Load(highRun);
        ulong highValue = (1UL << 40) + 100;
        var highReport = Observation(100) with { SpawnHpSnapshot = 1UL << 40, CumulativeHpDecrease = highValue, Unknown18 = highValue + 199 };
        var high = highAuthority.ValidateAndIssue(highRun, highRun.Snapshot.Identity, highReport);
        Check(high.Outcome == DungeonRunOutcome.Accepted && high.Death!.CombatSequence == 1,
            "完整u64高位参与HP终点与总量比较，不截成u32");
        var (edgeAuthority, edgeRun) = Fixture();
        Load(edgeRun);
        var edge = edgeAuthority.ValidateAndIssue(edgeRun, edgeRun.Snapshot.Identity, Observation(100) with
        {
            SpawnHpSnapshot = ulong.MaxValue, CumulativeHpDecrease = ulong.MaxValue, Unknown18 = ulong.MaxValue,
            PositiveHpDecreaseCount = ushort.MaxValue, Unknown26 = ushort.MaxValue
        });
        Check(edge.Outcome == DungeonRunOutcome.Accepted, "u64上界差0及u16正计数上界不发生加法溢出");
        Check(Rejected(edgeAuthority.ValidateAndIssue(edgeRun, edgeRun.Snapshot.Identity, Observation(101) with
        {
            SpawnHpSnapshot = ulong.MaxValue, CumulativeHpDecrease = ulong.MaxValue, Unknown18 = 198
        })), "总量低于完整u64累计值时先拒绝，不用无符号下溢冒充扰动");

        var (revisitAuthority, revisitRun) = Fixture(withMiddle: true);
        Load(revisitRun);
        var firstRoomIdentity = revisitRun.Snapshot.Identity;
        foreach (ushort key in new ushort[] { 100, 101 })
            Confirm(revisitRun, revisitAuthority.ValidateAndIssue(revisitRun, revisitRun.Snapshot.Identity, Observation(key)).Death!);
        revisitRun.Move(revisitRun.Snapshot.Identity, Guid.NewGuid(), new(1, 0));
        Load(revisitRun);
        Confirm(revisitRun, revisitAuthority.ValidateAndIssue(revisitRun, revisitRun.Snapshot.Identity, Observation(102)).Death!);
        Check(revisitRun.Move(revisitRun.Snapshot.Identity, Guid.NewGuid(), new(0, 0)).Outcome == DungeonRunOutcome.Accepted,
            "合成中间房清空后回访首房");
        Load(revisitRun);
        Check(Rejected(revisitAuthority.ValidateAndIssue(revisitRun, revisitRun.Snapshot.Identity, report)) &&
            Rejected(revisitAuthority.ValidateAndIssue(revisitRun, firstRoomIdentity, report)),
            "回访同地图也不能借旧签发epoch或旧expected重新接受观察");

        // 数值取970D实例实收Boss统计，但run、checksum布尔和Parser输出均为合成前提；不替代88B解析/实机。
        var (rewriteAuthority, rewriteRun) = BossFixture();
        var rewriteReport = BossObservation();
        Check(Rejected(rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteRun.Snapshot.Identity, rewriteReport)),
            "Boss改写仍须完成当前房间加载");
        Load(rewriteRun);
        var rewriteIdentity = rewriteRun.Snapshot.Identity;
        foreach (var wrong in new[]
        {
            rewriteIdentity with { CharacterId = 2 }, rewriteIdentity with { QuestId = 3146 },
            rewriteIdentity with { TaskAttempt = 2 }, rewriteIdentity with { InstanceId = Guid.NewGuid() },
            rewriteIdentity with { Epoch = rewriteIdentity.Epoch - 1 }
        })
            Check(Rejected(rewriteAuthority.ValidateAndIssue(rewriteRun, wrong, rewriteReport)),
                "Boss改写不能绕过角色/任务/尝试/实例/epoch身份");
        foreach (var invalid in new[]
        {
            rewriteReport with { MonsterKey = 65536 }, rewriteReport with { MonsterKey = 284 },
            rewriteReport with { ReporterKey = 2 }, rewriteReport with { ParticipantKey = 2 },
            rewriteReport with { ParticipantKind = 0x0211 }, rewriteReport with { SpawnLevel = 2 },
            rewriteReport with { SpawnHpSnapshot = 0 }, rewriteReport with { CumulativeHpDecrease = 2225 },
            rewriteReport with { PositiveHpDecreaseCount = 0 }, rewriteReport with { ChecksumMatches = false },
            rewriteReport with { BodySha256 = new string('G', 64) }, rewriteReport with { Unknown26 = 0 },
            rewriteReport with { Unknown18 = 6345 }, rewriteReport with { Unknown26 = 12 }
        })
            Check(Rejected(rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteIdentity, invalid)),
                "Boss改写仍核验真实HP终点/正次数/本人/checksum/指纹和独立prefix关系");
        foreach (byte unsupportedFlag in new byte[] { 2, 255 })
        {
            var unsupported = rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteIdentity,
                rewriteReport with { RewriteFlag = unsupportedFlag });
            Check(unsupported.Outcome == DungeonRunOutcome.Unsupported && unsupported.Death is null,
                "Boss不能放开任意非零改写标志" + unsupportedFlag);
        }
        foreach (var unsupportedFixture in new[]
        {
            BossFixture(dungeonId: 4), BossFixture(questId: 3146), BossFixture(mapId: 76125),
            BossFixture(monsterId: 1), BossFixture(rank: "[normal]"), BossFixture(level: 2), BossFixture(startAtBoss: false)
        })
        {
            Load(unsupportedFixture.Run);
            var unsupported = unsupportedFixture.Authority.ValidateAndIssue(unsupportedFixture.Run,
                unsupportedFixture.Run.Snapshot.Identity, rewriteReport);
            Check(unsupported.Outcome == DungeonRunOutcome.Unsupported && unsupported.Death is null,
                "错副本/任务/地图/模板/来源品级/生成等级或当前房间类型不能由flag取得Boss资格");
        }

        var rewritten = rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteIdentity, rewriteReport);
        Check(rewritten.Outcome == DungeonRunOutcome.Accepted && rewritten.Death!.CombatSequence == 1,
            "Boss实收统计2561覆盖2226且T8906/C29相容，拒绝不消费签发序号");
        Check(rewriteRun.CurrentLivingMonster(283) is not null &&
            Duplicate(rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteIdentity, rewriteReport), rewritten.Death!),
            "Boss签发不清房，精确重复返回原事实");
        foreach (var changed in new[]
        {
            rewriteReport with { RewriteFlag = 0 }, rewriteReport with { Unknown18 = 8907 },
            rewriteReport with { CumulativeHpDecrease = 2562 }, rewriteReport with { PositiveHpDecreaseCount = 13 },
            rewriteReport with { BodySha256 = new string('B', 64) }
        })
            Check(Rejected(rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteIdentity, changed)),
                "Boss同key改载荷不重签，即使新统计也相容");
        var rewritePending = rewriteRun.PrepareConfirmedDeath(rewritten.Death!);
        Check(rewritePending.Outcome == DungeonRunOutcome.Accepted &&
            rewriteRun.CancelUncommittedDeath(rewritePending.Pending!), "Boss原事实的未提交准备可取消");
        Check(Duplicate(rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteIdentity, rewriteReport), rewritten.Death!),
            "Boss回滚后的观察仍复用同EventId与序号");
        Confirm(rewriteRun, rewritten.Death!);
        Check(rewriteRun.Phase == SinglePlayerDungeonPhase.Settlement &&
            Duplicate(rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteRun.Snapshot.Identity, rewriteReport), rewritten.Death!) &&
            Rejected(rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteIdentity, rewriteReport)),
            "Boss确认清图后本房精确重放复用原事实，旧expected仍拒绝");

        foreach (var sample in new (ulong Total, uint Count, bool Accepted)[]
        {
            (0, 1, false), (1, 0, false), (1, 1, true), (599, 1, true), (798, 1, true), (799, 1, false),
            (599, 2, false), (600, 2, true), (1098, 2, true), (1099, 2, false),
            (8699, 29, false), (8700, 29, true), (9198, 29, true), (9199, 29, false),
            (19660800, 65536, true), (1288490188500, uint.MaxValue, true),
            (1288490188998, uint.MaxValue, true), (1288490188999, uint.MaxValue, false),
            (1288490189100, 1, false), ((ulong)long.MaxValue, uint.MaxValue, false),
            ((ulong)long.MaxValue + 1, 1, false), (ulong.MaxValue, 1, false)
        })
        {
            var (prefixAuthority, prefixRun) = BossFixture();
            Load(prefixRun);
            // 边界使用独立合成正HP/次数，prefix不与participant count相等，也不猜M=snapshot。
            var prefix = prefixAuthority.ValidateAndIssue(prefixRun, prefixRun.Snapshot.Identity, BossObservation() with
            {
                SpawnHpSnapshot = 1, CumulativeHpDecrease = 1, PositiveHpDecreaseCount = 1,
                Unknown18 = sample.Total, Unknown26 = sample.Count
            });
            Check(sample.Accepted ? prefix.Outcome == DungeonRunOutcome.Accepted : Rejected(prefix),
                "Boss派生次数夹取/199扰动/完整u32计数/下溢/signed64及不回绕边界 " + sample.Total + "/" + sample.Count);
        }
        var (unchangedAuthority, unchangedRun) = BossFixture();
        Load(unchangedRun);
        var unchanged = BossObservation() with { RewriteFlag = 0, Unknown18 = 2760, Unknown26 = 12 };
        Check(unchangedAuthority.ValidateAndIssue(unchangedRun, unchangedRun.Snapshot.Identity, unchanged).Outcome == DungeonRunOutcome.Accepted,
            "已规划Boss的flag0仍沿原累计与次数合同，不强制flag1");
        Console.WriteLine($"PASS {checks} 原生普通及限定Boss死亡统计权威合成断言（非实机）");

        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL 原生战斗权威: " + name);
        }
    }

    static bool Rejected(NativeDungeonDeathResult result) => result.Outcome == DungeonRunOutcome.Rejected && result.Death is null;
    static bool Duplicate(NativeDungeonDeathResult result, ConfirmedDungeonMonsterDeath death) =>
        result.Outcome == DungeonRunOutcome.Duplicate && ReferenceEquals(result.Death, death);

    // 全部观察是明确合成的统计输入，checksum布尔值与指纹也是假定Parser输出，不证明实际战斗。
    static NativeDungeonDeathObservation Observation(ushort key) =>
        new(key, 99, 0x0111, 99, 504, 3, 0, 3, 504, 520, 3, true, new string('A', 64));

    static NativeDungeonDeathObservation BossObservation() =>
        new(283, 1, 0x0111, 1, 2561, 12, 1, 3, 2226, 8906, 29, true,
            "59BAA2DC5CA23AC29317F0BEF7EE7C3BCD4194B4EBAF33256B86BCA04A0AF9FF");

    static void Load(SinglePlayerDungeonRun run)
    {
        if (run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome != DungeonRunOutcome.Accepted)
            throw new InvalidOperationException("FAIL 合成房间不能完成加载");
    }

    static void Confirm(SinglePlayerDungeonRun run, ConfirmedDungeonMonsterDeath death)
    {
        var prepared = run.PrepareConfirmedDeath(death);
        if (prepared.Outcome != DungeonRunOutcome.Accepted ||
            run.ConfirmCommittedDeath(prepared.Pending!).Outcome != DungeonRunOutcome.Accepted)
            throw new InvalidOperationException("FAIL 合成事实不能经过Prepare/Confirm");
    }

    static (NativeDungeonCombatAuthority Authority, SinglePlayerDungeonRun Run) Fixture(
        NativeDungeonCombatAuthority? authority = null, bool bind = true, bool withMiddle = false)
    {
        authority ??= new();
        var a = new DungeonMap("map", new(0, 0), 1, "map/fixture-native-a.map");
        var b = new DungeonMap(withMiddle ? "map" : "boss", new(1, 0), 2, "map/fixture-native-b.map");
        var c = new DungeonMap("boss", new(2, 0), 3, "map/fixture-native-c.map");
        DungeonMap[] maps = withMiddle ? [a, b, c] : [a, b];
        var maze = new DungeonMaze(0, maps.Length, 1, "明确合成", [a.Position], [maps[^1].Position], Array.AsReadOnly(maps), []);
        var entry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, maze, a, maps[^1]);
        DungeonMonsterPlan[] plans = withMiddle ? [Plan(a, 100, 2), Plan(b, 102, 1), Plan(c, 103, 1)]
            : [Plan(a, 100, 2), Plan(b, 102, 1)];
        DungeonRouteConnection[] connections = withMiddle ? [new(a.Position, b.Position), new(b.Position, c.Position)]
            : [new(a.Position, b.Position)];
        if (!SinglePlayerDungeonRun.TryCreate(entry, 1, 10, new(plans, connections), authority, out var run, out _) ||
            bind && !authority.BindTo(run!))
            throw new InvalidOperationException("FAIL 合成正式实例无法建立或绑定");
        return (authority, run!);
    }

    static DungeonMonsterPlan Plan(DungeonMap map, ushort key, int count)
    {
        var sources = Enumerable.Range(0, count).Select(_ => new DungeonRoomMonster(1,
            Array.AsReadOnly(new[] { 1, 0, 3, 100, 100, 0, 1, 1 }), "[fixed]", "[normal]", "monster/fixture-native.mob")).ToArray();
        return DungeonMonsterPlan.FromRoom(new(map, Array.AsReadOnly(sources), [], []), 99, key);
    }

    // 只合成生成身份及本地统计核验上下文；不是加载PVF、创建真实Boss或完成生产提交的证据。
    static (NativeDungeonCombatAuthority Authority, SinglePlayerDungeonRun Run) BossFixture(
        uint dungeonId = 3, uint questId = 3145, int mapId = 76126, int monsterId = 107000903,
        string rank = "[boss]", int level = 3, bool startAtBoss = true)
    {
        var authority = new NativeDungeonCombatAuthority();
        var map = new DungeonMap(startAtBoss ? "boss" : "map", new(0, 0), mapId, "map/fixture-native-rewrite-boss.map");
        var bossMap = startAtBoss ? map : new DungeonMap("boss", new(1, 0), 76127, "map/fixture-native-rewrite-final.map");
        DungeonMap[] maps = startAtBoss ? [map] : [map, bossMap];
        var maze = new DungeonMaze(0, maps.Length, 1, "明确合成Boss", [map.Position], [bossMap.Position], maps, []);
        var entry = new DungeonEntryPlan(1, 17, dungeonId, questId, 0, 0, maze, map, bossMap);
        var source = new DungeonRoomMonster(monsterId, Array.AsReadOnly(new[] { monsterId, 0, level, 831, 239, 0, 1, 1 }),
            "[fixed]", rank, "monster/fixture-native-rewrite-boss.mob");
        var plan = DungeonMonsterPlan.FromRoom(new(map, [source], [], []), 1, 283);
        DungeonMonsterPlan[] plans = startAtBoss ? [plan] :
            [plan, DungeonMonsterPlan.FromRoom(new(bossMap, [source], [], []), 1, 284)];
        DungeonRouteConnection[] connections = startAtBoss ? [] : [new(map.Position, bossMap.Position)];
        if (!SinglePlayerDungeonRun.TryCreate(entry, 1, 10, new(plans, connections), authority, out var run, out _) ||
            !authority.BindTo(run!))
            throw new InvalidOperationException("FAIL 合成Boss实例无法建立或绑定");
        return (authority, run!);
    }
}
