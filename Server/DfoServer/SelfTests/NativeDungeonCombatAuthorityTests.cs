using static DfoServer.Testing.TestCheck;
using System.Security.Cryptography;
using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Parsers;

namespace DfoServer;

internal static class NativeDungeonCombatAuthorityTests
{
    public static void RunFriendlySources()
    {
        int checks = Count;
        var (authority,run) = Fixture(withFriendly: true);
        var report = Observation(100) with { ReporterKey = ushort.MaxValue, Participants = [new(0x0111,99,0,0),new(0xF111,102,1,0)] };
        That(Rejected(authority.ValidateAndIssue(run,run.Snapshot.Identity,report)),"死亡报告仍须当前实例完成加载");
        Load(run);
        var identity = run.Snapshot.Identity;
        foreach (uint key in new uint[] { 0,999,102,103,200 })
            That(authority.ValidateAndIssue(run,identity,report with { MonsterKey = key }) is { Outcome: DungeonRunOutcome.Accepted, Death:null },
                "未知、友方、neutral和非本房目标不因身份拒绝，由Session分类且不产生奖励事实");
        That(Rejected(authority.ValidateAndIssue(run,identity,report with { MonsterKey = 999, ChecksumMatches = false })),
            "无奖励应答也保留正文校验");
        foreach (var source in new[]
        {
            report with { Participants = [] }, report with { ReporterKey = 98 },
            report with { Participants = [new(0xF111,103,1,1)] },
            report with { Participants = [new(0xF111,104,1,1)] },
            report with { Participants = [new(0xF111,201,1,1)] },
            report with { Participants = [new(0xF111,65535,1,1)] },
            report with { Participants = [new(0x0211,99,1,1)] },
            report with { Participants = [new(0x0111,98,1,1),new(0xF111,102,1,1)] },
            report with { Participants = [new(0xABCD,65535,ulong.MaxValue,ushort.MaxValue)] }
        })
        {
            var (sourceAuthority,sourceRun) = Fixture(); Load(sourceRun);
            That(sourceAuthority.ValidateAndIssue(sourceRun,sourceRun.Snapshot.Identity,source) is { Outcome: DungeonRunOutcome.Accepted, Death:not null },
                "Reporter和全部来源kind/key/HP只作审计，不按本人、阵营、是否已发布或数量授权");
        }
        var accepted = authority.ValidateAndIssue(run,identity,report);
        That(accepted.Outcome == DungeonRunOutcome.Accepted && accepted.Death!.CombatSequence == 1,
            "FFFF带完整参与者可签发，之前无奖励目标不消费事实序号");
        That(Duplicate(authority.ValidateAndIssue(run,identity,report with { Participants = report.Participants.ToArray().ToImmutableArray() }),accepted.Death!),
            "同目标重复观察复用同一事实，不重新签发");
        foreach (var changed in new[]
        {
            report with { ReporterKey = 98 }, report with { Participants = [new(0xABCD,65535,0,0)] },
            report with { Participants = report.Participants.Reverse().ToImmutableArray() },
            report with { Participants = [report.Participants[0],report.Participants[1] with { CumulativeHpDecrease = 2 }] },
            report with { Participants = [report.Participants[0],report.Participants[1] with { PositiveHpDecreaseCount = 1 }] },
            report with { BodySha256 = new string('B',64) }
        })
            That(Duplicate(authority.ValidateAndIssue(run,identity,changed),accepted.Death!),"来源、原序和正文变化仍返回原事实，不创建新奖励身份");
        Confirm(run,accepted.Death!);
        var onlyApc = report with { MonsterKey = 101, Participants = [new(0xF111,102,0,0)] };
        var second = authority.ValidateAndIssue(run,identity,onlyApc);
        That(second.Outcome == DungeonRunOutcome.Accepted && second.Death!.CombatSequence == 2,
            "FFFF仅APC来源允许，不强制玩家贡献且新目标只递增一次序号");
        Confirm(run,second.Death!);
        That(Duplicate(authority.ValidateAndIssue(run,identity,onlyApc with { Participants = [new(0x0211,201,0,0)] }),second.Death!),
            "已提交后改变来源也不重发奖励身份");
        That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(1,0)).Outcome == DungeonRunOutcome.Accepted,"确认死亡后沿原清房合同过房");
        That(Rejected(authority.ValidateAndIssue(run,run.Snapshot.Identity,report with { MonsterKey = 200 })),"过房loading仍不能报死亡");
        Load(run);
        var followed = onlyApc with { MonsterKey = 200 };
        That(authority.ValidateAndIssue(run,run.Snapshot.Identity,followed).Outcome == DungeonRunOutcome.Accepted,
            "跨房保留的APC来源不会阻止下一房死亡报告");
        That(Rejected(authority.ValidateAndIssue(run,identity,followed)),"跟随来源不放宽当前map/epoch身份");
        var (nextAuthority,nextRun) = Fixture(); Load(nextRun);
        var next = nextAuthority.ValidateAndIssue(nextRun,nextRun.Snapshot.Identity,report);
        That(next.Outcome == DungeonRunOutcome.Accepted && next.Death!.EventId != accepted.Death!.EventId &&
            Rejected(nextAuthority.ValidateAndIssue(nextRun,identity,Observation(100))),"来源只是审计，新run独立事实不继承旧实例观察");
        Console.WriteLine($"PASS {Count-checks} 原生来源审计、无奖励目标与提交身份去重断言");
    }

    public static void Run()
    {
        int checks = Count;
        var (authority, run) = Fixture(bind: false);
        var report = Observation(100);
        That(authority.ValidateAndIssue(run, run.Snapshot.Identity, report).Outcome == DungeonRunOutcome.Rejected,
            "未绑定实例不能签发");
        That(authority.BindTo(run) && authority.BindTo(run), "exact权威只绑定一个run，同run重复绑定无副作用");
        That(authority.ValidateAndIssue(run, run.Snapshot.Identity, report).Outcome == DungeonRunOutcome.Rejected,
            "未完成房间加载不能签发");
        Load(run);
        var identity = run.Snapshot.Identity;
        foreach (var wrong in new[]
        {
            identity with { CharacterId = 98 }, identity with { QuestId = 3146 }, identity with { TaskAttempt = 2 },
            identity with { InstanceId = Guid.NewGuid() }, identity with { Epoch = identity.Epoch - 1 },
            identity with { Epoch = identity.Epoch + 1 }
        })
            That(Rejected(authority.ValidateAndIssue(run, wrong, report)), "角色/任务/尝试/实例/旧新epoch不符拒绝");

        foreach (var invalid in new[]
        {
            report with { MonsterKey = 65536 },
            report with { ChecksumMatches = false }, report with { BodySha256 = new string('A', 63) },
            report with { BodySha256 = new string('G', 64) }, report with { BodySha256 = null! }
        })
            That(Rejected(authority.ValidateAndIssue(run, identity, invalid)), "协议范围、检查值与完整指纹格式仍拒绝");

        var first = authority.ValidateAndIssue(run, identity, report);
        var death = first.Death!;
        That(first.Outcome == DungeonRunOutcome.Accepted && death.EventId != Guid.Empty && death.CombatSequence == 1 &&
            death.Identity == identity && death.MonsterKey == 100 && ReferenceEquals(death.Authority, authority),
            "首次资格通过才签发；之前拒绝不消费序号");
        That(run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 100, 101 }),
            "签发不直接消费死亡、清房或奖励状态");
        That(Duplicate(authority.ValidateAndIssue(run, identity, report), death), "同提交身份重放返回原事实，不重新签发");
        foreach (var changed in new[]
        {
            report with { ReporterKey = 98 }, report with { ParticipantKind = 0x0211 }, report with { ParticipantKey = 98 },
            report with { CumulativeHpDecrease = 505, Unknown18 = 521 }, report with { PositiveHpDecreaseCount = 4, Unknown26 = 4 },
            report with { RewriteFlag = 1 }, report with { SpawnLevel = 2 }, report with { SpawnHpSnapshot = 503 },
            report with { Unknown18 = 521 }, report with { Unknown26 = 4 },
            report with { BodySha256 = new string('B', 64) }, report with { BodySha256 = new string('a', 64) }
        })
            That(Duplicate(authority.ValidateAndIssue(run, identity, changed),death),
                "同key的来源和统计变化只作审计，继续返回原提交事实");
        That(Rejected(authority.ValidateAndIssue(run,identity,report with { ChecksumMatches = false })),
            "已签发缓存也不绕过正文校验");

        var preparation = run.PrepareConfirmedDeath(death);
        var pending = preparation.Pending!;
        That(preparation.Outcome == DungeonRunOutcome.Accepted && run.IsPendingDeath(pending), "原事实可进入现有Prepare");
        var pendingReplay = authority.ValidateAndIssue(run, identity, report with { ReporterKey = ushort.MaxValue, BodySha256 = new string('B',64) });
        That(Duplicate(pendingReplay, death) && ReferenceEquals(run.PrepareConfirmedDeath(pendingReplay.Death!).Pending, pending),
            "SQL结果未定时来源正文变化的重试仍使用原事实和同一准备对象");
        That(run.CancelUncommittedDeath(pending), "明确SQL回滚取消原准备");
        var rollbackReplay = authority.ValidateAndIssue(run, identity, report with { ParticipantKind = 0xABCD,ParticipantKey = 65535 });
        var retried = run.PrepareConfirmedDeath(rollbackReplay.Death!);
        That(Duplicate(rollbackReplay, death) && retried.Outcome == DungeonRunOutcome.Accepted &&
            ReferenceEquals(retried.Pending!.Death, death) && !ReferenceEquals(retried.Pending, pending),
            "回滚后再Prepare保留同一EventId与序号，不重签");
        That(run.ConfirmCommittedDeath(retried.Pending!).Outcome == DungeonRunOutcome.Accepted &&
            run.CurrentLivingMonster(100) is null && Duplicate(authority.ValidateAndIssue(run, identity, report), death),
            "确认后的本房精确重复返回原事实，已死key不再签发");
        That(run.PrepareConfirmedDeath(death).Outcome == DungeonRunOutcome.Duplicate,
            "原已提交事实继续由Run识别Duplicate，不再输出准备或奖励");

        var secondReport = Observation(101) with { CumulativeHpDecrease = 600, Unknown18 = 799 };
        var second = authority.ValidateAndIssue(run, identity, secondReport);
        That(second.Outcome == DungeonRunOutcome.Accepted && second.Death!.CombatSequence == 2 &&
            second.Death.EventId != death.EventId, "允许夹取前overkill及总统计差异，另一key只递增一次");
        Confirm(run, second.Death!);
        That(run.Move(identity, Guid.NewGuid(), new(1, 0)).Outcome == DungeonRunOutcome.Accepted, "合成首房清空后过房");
        That(Rejected(authority.ValidateAndIssue(run, identity, report)) &&
            Rejected(authority.ValidateAndIssue(run, run.Snapshot.Identity, report)), "过房旧epoch及Loading中的缓存都拒绝");
        Load(run);
        var bossIdentity = run.Snapshot.Identity;
        That(authority.ValidateAndIssue(run,bossIdentity,report) is { Outcome: DungeonRunOutcome.Duplicate, Death:null },
            "当前新房旧目标只审计忽略，不携带旧房奖励事实");
        var bossReport = Observation(102);
        var boss = authority.ValidateAndIssue(run, bossIdentity, bossReport);
        That(boss.Outcome == DungeonRunOutcome.Accepted && boss.Death!.CombatSequence == 3, "Boss房合成观察独立签发");
        Confirm(run, boss.Death!);
        var settlementIdentity = run.Snapshot.Identity;
        That(run.Phase == SinglePlayerDungeonPhase.Settlement && settlementIdentity.Epoch == bossIdentity.Epoch + 1 &&
            Duplicate(authority.ValidateAndIssue(run, settlementIdentity, bossReport), boss.Death!),
            "Settlement本房精确重复返回原事实，接受Run明确的一次终止epoch推进");
        That(Rejected(authority.ValidateAndIssue(run, bossIdentity, bossReport)), "Settlement旧expected epoch先拒绝");
        That(Duplicate(authority.ValidateAndIssue(run,settlementIdentity,bossReport with { Unknown18 = 521 }),boss.Death!) &&
            authority.ValidateAndIssue(run,settlementIdentity,secondReport) is { Outcome: DungeonRunOutcome.Duplicate, Death:null } &&
            authority.ValidateAndIssue(run,settlementIdentity,Observation(103)) is { Outcome: DungeonRunOutcome.Accepted, Death:null },
            "Settlement同目标复用原事实，旧房和未知目标均不产生新奖励身份");
        That(run.Return(settlementIdentity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted &&
            Rejected(authority.ValidateAndIssue(run, run.Snapshot.Identity, bossReport)), "Returned不放行缓存或新签发");

        var (_, otherRun) = Fixture(authority, bind: false);
        Load(otherRun);
        That(!authority.BindTo(otherRun) && Rejected(authority.ValidateAndIssue(otherRun, otherRun.Snapshot.Identity, report)),
            "同权威不能换绑另一实例，旧实例报告不能影响新对象");
        var wrongAuthority = new NativeDungeonCombatAuthority();
        That(!wrongAuthority.BindTo(otherRun) && Rejected(wrongAuthority.ValidateAndIssue(otherRun, otherRun.Snapshot.Identity, report)),
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
            That(DungeonRequests.TryParseDeath(body, out var parsed) && parsed?.SinglePlayerStatistics is not null,
                "控制技能死亡实收88B可按单参与者正文解析 " + sample.Key);
            var request = parsed!;
            var statistics = request.SinglePlayerStatistics!;
            That(request.ObjectKey == sample.Key && request.Reporter == 4 && statistics.ChecksumMatches &&
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
            That(accepted.Outcome == DungeonRunOutcome.Accepted && accepted.Death!.CombatSequence == 1,
                "总次数多于本人次数的控制技能统计可签发合成事实 " + sample.Key);
            That(Duplicate(sampleAuthority.ValidateAndIssue(sampleRun, sampleIdentity, sampleReport), accepted.Death!),
                "控制技能统计精确重放不重复签发 " + sample.Key);
            That(Duplicate(sampleAuthority.ValidateAndIssue(sampleRun,sampleIdentity,
                sampleReport with { Unknown18 = sampleReport.Unknown18+1 }),accepted.Death!),
                "同key改写统计仍恢复唯一签发事实 " + sample.Key);
            That(sampleRun.Move(sampleIdentity, Guid.NewGuid(), new(1, 0)).Outcome == DungeonRunOutcome.Rejected,
                "控制技能报告签发后尚未提交仍不能过房 " + sample.Key);
            Confirm(sampleRun, accepted.Death!);
            That(Duplicate(sampleAuthority.ValidateAndIssue(sampleRun, sampleIdentity, sampleReport), accepted.Death!) &&
                sampleRun.Move(sampleIdentity, Guid.NewGuid(), new(1, 0)).Outcome == DungeonRunOutcome.Rejected,
                "控制技能死亡已确认可幂等重放，但存活怪未清空仍不能过房 " + sample.Key);
            Confirm(sampleRun, sampleAuthority.ValidateAndIssue(sampleRun, sampleIdentity, Observation(101)).Death!);
            That(sampleRun.Move(sampleIdentity, Guid.NewGuid(), new(1, 0)).Outcome == DungeonRunOutcome.Accepted,
                "控制技能统计与普通统计的死亡均确认后才能过房 " + sample.Key);
        }

        foreach (var sample in new[]
        {
            report with { SpawnLevel = 2, SpawnHpSnapshot = 0, CumulativeHpDecrease = 0,
                PositiveHpDecreaseCount = 0, Unknown18 = 0, Unknown26 = 0, RewriteFlag = 1 },
            report with { CumulativeHpDecrease = 503, Unknown18 = 503, Unknown26 = 2 },
            report with { SpawnHpSnapshot = 1UL << 40, CumulativeHpDecrease = 400, Unknown18 = 800 },
            report with { SpawnLevel = uint.MaxValue, SpawnHpSnapshot = ulong.MaxValue,
                CumulativeHpDecrease = ulong.MaxValue, Unknown18 = 198,
                PositiveHpDecreaseCount = ushort.MaxValue, Unknown26 = uint.MaxValue, RewriteFlag = 255 }
        })
        {
            var (statsAuthority, statsRun) = Fixture();
            Load(statsRun);
            var accepted = statsAuthority.ValidateAndIssue(statsRun, statsRun.Snapshot.Identity, sample);
            That(accepted.Outcome == DungeonRunOutcome.Accepted && accepted.Death!.CombatSequence == 1,
                "客户端等级、HP、次数、统计前缀和改写标志不作为死亡门槛");
            Confirm(statsRun, accepted.Death!);
            That(Duplicate(statsAuthority.ValidateAndIssue(statsRun, statsRun.Snapshot.Identity, sample), accepted.Death!),
                "统计不设门槛仍保留已提交事实的身份去重");
        }

        foreach (string hex in new[]
        {
            "770100000B00000000000000000000000000A80E000000000000090000000111010B00FB0700000000000006003A01E70000000000544F000200000000040000000000005102EE000402EE00160034002400000042343987",
            "AA0100000B00000000000000000000000000050F000000000000150000000111010B004A0E00000000000015002B03EE0000000000A605000600000000040000000000005102EE005102EE001600350011000000B6798560"
        })
        {
            byte[] body = Convert.FromHexString(hex);
            That(DungeonRequests.TryParseDeath(body, out var parsed) && parsed!.SinglePlayerStatistics is { ChecksumMatches: true },
                "胆小哥布林实收88B正文仍通过原解析及检查值");
            var stats = parsed!.SinglePlayerStatistics!;
            var (goblinAuthority, goblinRun) = Fixture(level: 5);
            Load(goblinRun);
            var observation = new NativeDungeonDeathObservation(100, 99, stats.Participant.Kind, 99,
                stats.Participant.CumulativeHpDecrease, stats.Participant.PositiveHpDecreaseCount, stats.RewriteFlag,
                stats.SpawnLevel, stats.SpawnHpSnapshot, stats.Unknown18, stats.Unknown26,
                stats.ChecksumMatches, Convert.ToHexString(SHA256.HashData(body)));
            var accepted = goblinAuthority.ValidateAndIssue(goblinRun, goblinRun.Snapshot.Identity, observation);
            That(stats.SpawnLevel is 2 or 6 && goblinRun.CurrentLivingMonster(100)!.CandidateLevel == 5 &&
                accepted.Outcome == DungeonRunOutcome.Accepted, "生成Lv5、报告Lv2或Lv6的死亡可签发");
            Confirm(goblinRun, accepted.Death!);
            Confirm(goblinRun, goblinAuthority.ValidateAndIssue(goblinRun, goblinRun.Snapshot.Identity, Observation(101)).Death!);
            That(goblinRun.Move(goblinRun.Snapshot.Identity, Guid.NewGuid(), new(1, 0)).Outcome == DungeonRunOutcome.Accepted,
                "哥布林死亡确认后清房与过房正常");
        }

        var (revisitAuthority, revisitRun) = Fixture(withMiddle: true);
        Load(revisitRun);
        var firstRoomIdentity = revisitRun.Snapshot.Identity;
        foreach (ushort key in new ushort[] { 100, 101 })
            Confirm(revisitRun, revisitAuthority.ValidateAndIssue(revisitRun, revisitRun.Snapshot.Identity, Observation(key)).Death!);
        revisitRun.Move(revisitRun.Snapshot.Identity, Guid.NewGuid(), new(1, 0));
        Load(revisitRun);
        Confirm(revisitRun, revisitAuthority.ValidateAndIssue(revisitRun, revisitRun.Snapshot.Identity, Observation(102)).Death!);
        That(revisitRun.Move(revisitRun.Snapshot.Identity, Guid.NewGuid(), new(0, 0)).Outcome == DungeonRunOutcome.Accepted,
            "合成中间房清空后回访首房");
        Load(revisitRun);
        That(revisitAuthority.ValidateAndIssue(revisitRun,revisitRun.Snapshot.Identity,report) is { Outcome: DungeonRunOutcome.Duplicate, Death:null } &&
            Rejected(revisitAuthority.ValidateAndIssue(revisitRun, firstRoomIdentity, report)),
            "回访同地图的旧epoch目标只审计忽略，旧expected仍拒绝且不能重新发奖");

        // 数值取970D实例实收Boss统计，但run、checksum布尔和Parser输出均为合成前提；不替代88B解析/实机。
        var (rewriteAuthority, rewriteRun) = BossFixture();
        var rewriteReport = BossObservation();
        That(Rejected(rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteRun.Snapshot.Identity, rewriteReport)),
            "Boss改写仍须完成当前房间加载");
        Load(rewriteRun);
        var rewriteIdentity = rewriteRun.Snapshot.Identity;
        foreach (var wrong in new[]
        {
            rewriteIdentity with { CharacterId = 2 }, rewriteIdentity with { QuestId = 3146 },
            rewriteIdentity with { TaskAttempt = 2 }, rewriteIdentity with { InstanceId = Guid.NewGuid() },
            rewriteIdentity with { Epoch = rewriteIdentity.Epoch - 1 }
        })
            That(Rejected(rewriteAuthority.ValidateAndIssue(rewriteRun, wrong, rewriteReport)),
                "Boss改写不能绕过角色/任务/尝试/实例/epoch身份");
        foreach (var invalid in new[]
        {
            rewriteReport with { MonsterKey = 65536 }, rewriteReport with { ChecksumMatches = false },
            rewriteReport with { BodySha256 = new string('G', 64) }
        })
            That(Rejected(rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteIdentity, invalid)),
                "Boss死亡仍核验协议范围、checksum与指纹格式");
        That(rewriteAuthority.ValidateAndIssue(rewriteRun,rewriteIdentity,rewriteReport with { MonsterKey = 284 }) is
            { Outcome: DungeonRunOutcome.Accepted, Death:null },"Boss房未知目标只审计忽略，不签发奖励事实");
        foreach (var ordinaryFixture in new[]
        {
            BossFixture(rank: "[normal]"), BossFixture(startAtBoss: false)
        })
        {
            Load(ordinaryFixture.Run);
            var accepted = ordinaryFixture.Authority.ValidateAndIssue(ordinaryFixture.Run,
                ordinaryFixture.Run.Snapshot.Identity, rewriteReport);
            That(accepted.Outcome == DungeonRunOutcome.Accepted,
                "改写标志不限制当前敌对活对象的死亡，清图仍由正式生成计划决定");
        }

        foreach (var general in new[] { BossFixture(dungeonId: 4), BossFixture(questId: 3146), BossFixture(mapId: 76125), BossFixture(monsterId: 1) })
        {
            Load(general.Run);
            That(general.Authority.ValidateAndIssue(general.Run,general.Run.Snapshot.Identity,rewriteReport).Outcome == DungeonRunOutcome.Accepted,
                "固定Boss死亡不按副本、任务、地图或模板编号设白名单");
        }
        var lower = BossFixture(level: 2); Load(lower.Run);
        That(lower.Authority.ValidateAndIssue(lower.Run,lower.Run.Snapshot.Identity,rewriteReport).Outcome == DungeonRunOutcome.Accepted,
            "Boss报告等级与生成计划不同也能接受死亡");

        var rewritten = rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteIdentity, rewriteReport);
        That(rewritten.Outcome == DungeonRunOutcome.Accepted && rewritten.Death!.CombatSequence == 1,
            "Boss死亡资格通过即可签发，拒绝不消费签发序号");
        That(rewriteRun.CurrentLivingMonster(283) is not null &&
            Duplicate(rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteIdentity, rewriteReport), rewritten.Death!),
            "Boss签发不清房，精确重复返回原事实");
        foreach (var changed in new[]
        {
            rewriteReport with { RewriteFlag = 0 }, rewriteReport with { Unknown18 = 8907 },
            rewriteReport with { CumulativeHpDecrease = 2562 }, rewriteReport with { PositiveHpDecreaseCount = 13 },
            rewriteReport with { BodySha256 = new string('B', 64) }
        })
            That(Duplicate(rewriteAuthority.ValidateAndIssue(rewriteRun,rewriteIdentity,changed),rewritten.Death!),
                "Boss同key改载荷仍恢复原奖励身份，不重新签发");
        That(rewriteAuthority.MatchesIssuedSpawn(rewriteRun,283,rewriteReport.SpawnLevel,rewriteReport.SpawnHpSnapshot) &&
            !rewriteAuthority.MatchesIssuedSpawn(rewriteRun,283,rewriteReport.SpawnLevel+1,rewriteReport.SpawnHpSnapshot),
            "117已有查询保留首次签发的spawn观察，不被后续审计报告改写");
        var rewritePending = rewriteRun.PrepareConfirmedDeath(rewritten.Death!);
        That(rewritePending.Outcome == DungeonRunOutcome.Accepted &&
            rewriteRun.CancelUncommittedDeath(rewritePending.Pending!), "Boss原事实的未提交准备可取消");
        That(Duplicate(rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteIdentity, rewriteReport), rewritten.Death!),
            "Boss回滚后的观察仍复用同EventId与序号");
        Confirm(rewriteRun, rewritten.Death!);
        That(rewriteRun.Phase == SinglePlayerDungeonPhase.Settlement &&
            Duplicate(rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteRun.Snapshot.Identity, rewriteReport), rewritten.Death!) &&
            Rejected(rewriteAuthority.ValidateAndIssue(rewriteRun, rewriteIdentity, rewriteReport)),
            "Boss确认清图后本房精确重放复用原事实，旧expected仍拒绝");

        var (prefixAuthority, prefixRun) = BossFixture();
        Load(prefixRun);
        That(prefixAuthority.ValidateAndIssue(prefixRun, prefixRun.Snapshot.Identity, BossObservation() with
        {
            SpawnLevel = 0, SpawnHpSnapshot = 0, CumulativeHpDecrease = 0, PositiveHpDecreaseCount = 0,
            Unknown18 = ulong.MaxValue, Unknown26 = 0, RewriteFlag = 255
        }).Outcome == DungeonRunOutcome.Accepted, "Boss HP和派生次数公式不再作为死亡门槛");
        var (unchangedAuthority, unchangedRun) = BossFixture();
        Load(unchangedRun);
        var unchanged = BossObservation() with { RewriteFlag = 0, Unknown18 = 2760, Unknown26 = 12 };
        That(unchangedAuthority.ValidateAndIssue(unchangedRun, unchangedRun.Snapshot.Identity, unchanged).Outcome == DungeonRunOutcome.Accepted,
            "已规划Boss的flag0报告也接受死亡");
        var (culledAuthority,culledRun) = BossFixture(withMinion: true); Load(culledRun);
        Confirm(culledRun,culledAuthority.ValidateAndIssue(culledRun,culledRun.Snapshot.Identity,BossObservation()).Death!);
        That(culledRun.IsCulledMonster(284) && culledAuthority.ValidateAndIssue(culledRun,culledRun.Snapshot.Identity,
            BossObservation() with { MonsterKey = 284, ReporterKey = ushort.MaxValue, ParticipantKind = 0xABCD }) is
            { Outcome: DungeonRunOutcome.Duplicate, Death:null },"Boss清场后杂兵补报只审计忽略，不签发额外击杀或奖励事实");
        Console.WriteLine($"PASS {Count - checks} 原生死亡实例、正文完整性与提交身份合成断言（非实机）");

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
        NativeDungeonCombatAuthority? authority = null, bool bind = true, bool withMiddle = false, int level = 3, bool withFriendly = false)
    {
        authority ??= new();
        var a = new DungeonMap("map", new(0, 0), 1, "map/fixture-native-a.map");
        var b = new DungeonMap(withMiddle ? "map" : "boss", new(1, 0), 2, "map/fixture-native-b.map");
        var c = new DungeonMap("boss", new(2, 0), 3, "map/fixture-native-c.map");
        DungeonMap[] maps = withMiddle ? [a, b, c] : [a, b];
        var maze = new DungeonMaze(0, maps.Length, 1, "明确合成", [a.Position], [maps[^1].Position], Array.AsReadOnly(maps), []);
        var entry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, maze, a, maps[^1]);
        DungeonMonsterPlan[] plans = withMiddle ? [Plan(a, 100, 2, level), Plan(b, 102, 1, level), Plan(c, 103, 1, level)]
            : [Plan(a, 100, 2, level), Plan(b, 102, 1, level)];
        if (withFriendly)
        {
            DungeonRoomAiCharacter Actor(int id,string faction) => new(id,[id,0,1,100,100,0],faction,"[normal]","aicharacter/fixture.aic",level);
            plans[0] = DungeonMonsterPlan.FromRoom(plans[0].Room with { AiCharacters = [Actor(3,"[character]"),Actor(4,"[neutral]"),Actor(5,"[monster]")] },99,100);
            var next = Plan(b,200,1,level);
            plans[1] = DungeonMonsterPlan.FromRoom(next.Room with { AiCharacters = [Actor(3,"[character]")] },99,200);
        }
        DungeonRouteConnection[] connections = withMiddle ? [new(a.Position, b.Position), new(b.Position, c.Position)]
            : [new(a.Position, b.Position)];
        if (!SinglePlayerDungeonRun.TryCreate(entry, 1, 10, new(plans, connections), authority, out var run, out _) ||
            bind && !authority.BindTo(run!))
            throw new InvalidOperationException("FAIL 合成正式实例无法建立或绑定");
        return (authority, run!);
    }

    static DungeonMonsterPlan Plan(DungeonMap map, ushort key, int count, int level)
    {
        var sources = Enumerable.Range(0, count).Select(_ => new DungeonRoomMonster(1,
            Array.AsReadOnly(new[] { 1, 0, level, 100, 100, 0, 1, 1 }), "[fixed]", "[normal]", "monster/fixture-native.mob")).ToArray();
        return DungeonMonsterPlan.FromRoom(new(map, Array.AsReadOnly(sources), [], []), 99, key);
    }

    // 只合成生成身份及死亡核验上下文；不是加载PVF、创建真实Boss或完成生产提交的证据。
    static (NativeDungeonCombatAuthority Authority, SinglePlayerDungeonRun Run) BossFixture(
        uint dungeonId = 3, uint questId = 3145, int mapId = 76126, int monsterId = 107000903,
        string rank = "[boss]", int level = 3, bool startAtBoss = true, bool withMinion = false)
    {
        var authority = new NativeDungeonCombatAuthority();
        var map = new DungeonMap(startAtBoss ? "boss" : "map", new(0, 0), mapId, "map/fixture-native-rewrite-boss.map");
        var bossMap = startAtBoss ? map : new DungeonMap("boss", new(1, 0), 76127, "map/fixture-native-rewrite-final.map");
        DungeonMap[] maps = startAtBoss ? [map] : [map, bossMap];
        var maze = new DungeonMaze(0, maps.Length, 1, "明确合成Boss", [map.Position], [bossMap.Position], maps, []);
        var entry = new DungeonEntryPlan(1, 17, dungeonId, questId, 0, 0, maze, map, bossMap);
        var source = new DungeonRoomMonster(monsterId, Array.AsReadOnly(new[] { monsterId, 0, level, 831, 239, 0, 1, 1 }),
            "[fixed]", rank, "monster/fixture-native-rewrite-boss.mob");
        DungeonRoomMonster[] sources = withMinion ? [source,source with { RankKind = "[normal]" }] : [source];
        var plan = DungeonMonsterPlan.FromRoom(new(map, sources, [], []), 1, 283);
        DungeonMonsterPlan[] plans = startAtBoss ? [plan] :
            [plan, DungeonMonsterPlan.FromRoom(new(bossMap, [source], [], []), 1, 284)];
        DungeonRouteConnection[] connections = startAtBoss ? [] : [new(map.Position, bossMap.Position)];
        if (!SinglePlayerDungeonRun.TryCreate(entry, 1, 10, new(plans, connections), authority, out var run, out _) ||
            !authority.BindTo(run!))
            throw new InvalidOperationException("FAIL 合成Boss实例无法建立或绑定");
        return (authority, run!);
    }
}
