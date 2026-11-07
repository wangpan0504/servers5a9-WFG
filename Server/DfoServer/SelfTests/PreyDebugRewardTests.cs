#if DEBUG
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using DfoServer.Persistence;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PreyDebugRewardTests
{
    // 复用已满额的真实SQLite角色和正式Start；清图事实为显式领域输入，不冒称客户端验收。
    internal static void Run(AntonRaidBattle battle, RaidActor actor, GameSession session, CharacterRepository repository,
        TcpTestResources resources, RaidScript script, DateTimeOffset now, bool ignoreLimits)
    {
        string source = battle.PreyGuide ? PreyGuideRewards.Source : PreyGuideRewards.TeamSource;
        int week = new RaidWeeklyCycle(DayOfWeek.Thursday, new(6, 0), TimeSpan.FromHours(8)).Key(now);
        That(battle.ExpireLukeTimer(battle.Instance, new(9000, 0), battle.View.TimerGenerations[new(9000, 0)]) is not null,
            "Debug奖励用例沿原准备计时器开放正式目标");
        for (int phase = 0; phase < 2; phase++)
        {
            if (!battle.PreyGuide && phase == 1)
                That(battle.ExpireLukeTimer(battle.Instance, new(2, 1001), battle.View.TimerGenerations[new(2, 1001)]) is not null,
                    "普通二阶段保留原准备事件，不由奖励调试开关跳过玩法");
            int[] targets = battle.PreyGuide
                ? phase == 0 ? battle.View.Symbols["[single raid type]"] == 0 ? [291100391, 291100393] : [291100390, 291100392] : [291100394, 291100397]
                : script.Phases[phase].Dungeons.Where(row => row.Declarations.Any(field => field.Tag == "[type]" &&
                    field.Values is [{ Text: var type }] && type == (phase == 0 ? "sanctuary" : "boss"))).Select(row => row.Id).ToArray();
            That(targets.Length == (battle.PreyGuide ? 2 : phase == 0 ? 4 : 1), "两种模式保留各自完整阶段终结目标");
            foreach (int dungeon in targets)
            {
                var identity = new DungeonRunIdentity(actor.Character, 0, 1, Guid.NewGuid(), 1);
                var admission = new RaidDungeonIdentity(battle.Instance, phase, battle.PreyGuide ? (byte)1 : (byte)0, RaidKind.Prey)
                    { PreyGuide = battle.PreyGuide, PreySoloCharacter = battle.PreyGuide ? null : actor.Character };
                using var reservation = battle.ReserveDungeon(admission, checked((uint)dungeon), [(actor, identity)]);
                That(reservation is not null, "奖励额度豁免仍要求当前正式Run与开放目标");
                reservation!.Install();
                var fact = new DungeonClearFact(identity, checked((uint)dungeon), [Guid.NewGuid()])
                    { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/prey-debug-reward.map") };
                That(battle.CompletePreyDungeon([(actor, fact)]) is not null, "真实当前成员的合成终点事实满足原贡献门槛");
                battle.ReleaseDungeon(actor, identity.InstanceId);
            }
            That(session.CommitPendingLukePhase(repository), "已满额角色的新实例仍沿真实阶段资格SQL提交");
            var eligibility = battle.LukeEligibility(actor.Character, phase);
            That(eligibility is not null && eligibility.Eligible == ignoreLimits && eligibility.Source == source &&
                eligibility.Week == week && eligibility.CountsAsClear == (ignoreLimits && phase == 1),
                "Debug豁免仅改变次数资格，来源、业务周期及最终通关记录保留");
            if (!ignoreLimits)
            {
                That(eligibility!.Reason == (phase == 0 ? "phase-eligibility-limit" : "weekly-clear-limit"),
                    "仅豁免开团时，正式满额角色仍按原阶段奖励及周通关上限拒绝");
                var before = session.Selected;
                That(session.ClaimRaidReward(0, 0, repository, resources.ItemResources.Items, resources.ItemResources.GoldLimits,
                    resources.ItemResources.EpicPieces, now) is null && ReferenceEquals(before, session.Selected),
                    "默认奖励限制拒绝不发放库存资产");
                continue;
            }
            if (battle.PreyGuide || phase == 1)
            {
                var beforeMovie = session.Selected;
                That(session.ClaimRaidReward(1, 0, repository, resources.ItemResources.Items,
                    resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces, now) is null &&
                    ReferenceEquals(beforeMovie, session.Selected), "Debug额度豁免不绕过真实阶段电影前拒领且不改资产");
                That(battle.CompletePreyMovie(actor, battle.View), "引导各阶段及普通最终奖励均等待真实演出完成业务确认");
            }
            foreach (byte kind in new byte[] { 1, 0 })
            {
                var receipt = session.ClaimRaidReward(kind, 0, repository, resources.ItemResources.Items,
                    resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces, now);
                That(receipt is { Commit.Duplicate: false } && receipt.Prepared.Source == source && receipt.Prepared.Week == week,
                    "已有满额旧记录时Debug两阶段各类奖励仍实际入库并沿原来源周期记收据");
                long revision = session.Selected!.Revision;
                var replay = session.ClaimRaidReward(kind, 0, repository, resources.ItemResources.Items,
                    resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces, now);
                That(replay is { Commit.Duplicate: true } && session.Selected.Revision == revision,
                    "Debug重复领取同一实例仍重放唯一收据，不增加资产或角色revision");
                session.ConfirmRaidRewardPublished(receipt!);
            }
        }
    }
}
#endif
