using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 只验证真实会话的刷新门禁与显示回执，不重复团本演出和人数矩阵。
internal static class RaidQuotaSessionTests
{
    internal static void RunCore()
    {
        int before = Count;
        using var f = new MailboxPersistenceTests.Fixture();
        var origin = new Location(32, 4, 10, 10);
        var map = new TownMap("fixture/quota-wait.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
        var towns = new TownCatalog(new() { [(32, 4)] = new(32, 4, 1, [], [], [], origin, map, [], []) },
            origin, origin, new(0, [], []), [], new Dictionary<uint, uint[]>());
        using var owner = f.Owners.Acquire("quota", () => { });
        f.Execute("INSERT INTO accounts(account_id,salt,password_hash,iterations) VALUES('quota',x'01',x'02',1);");
        var created = f.Repository.Create(owner, new("QuotaGuard", origin, origin) { Level = 90, CompletedQuests = [2903, 4872] });
        var clock = new LukeRaidClockTests.Clock();
        var session = new GameSession(f.Repository, towns, owner, entry: new(new(2, 20), 32, "[raid]"), timeProvider: clock);
        try
        {
            var rules = Rules();
            session.MarkRosterPublished(session.PrepareRoster());
            CheckSelectionReset(false);
            CheckSelectionReset(true);
            session.Select(0); session.MarkReady();
            CheckRaidDisplayReset(false);
            CheckRaidDisplayReset(true);

            // 数据库失败必须发生在真实仓储事务内；不伪造会话成功结果。
            foreach (bool reward in new[] { false, true })
            {
                SeedCurrentRun();
                var token = session.CaptureRaidClearCounts(null)!;
                f.Execute("CREATE TRIGGER quota_reset_fail BEFORE UPDATE ON account_raid_quota_epochs " +
                    "WHEN NEW.account='quota' BEGIN SELECT RAISE(ABORT,'quota reset fixture failure'); END;");
                string saved = f.Snapshot();
                try
                {
                    Reject<SqliteException>(() => Reset(reward), "真实额度事务故障向调用方报告失败");
                    That(f.Snapshot() == saved && session.CanPublishRaidClearCounts(token, null),
                        "事务失败回滚全部数据且不使当前团本次数显示回执失效");
                }
                finally { f.Execute("DROP TRIGGER quota_reset_fail;"); }
            }

            var script = AntonRaidBattleTests.Script(10, 20) with { WaitingRoom = new(32, 4), Headers =
                [new("[phase break time]", 1, [new(0, 600, null)]), new("[start delay time]", 2, [new(0, 3, null)]),
                    new("[movie end time]", 3, [new(0, 65, null)])] };
            var policies = Enumerable.Range(0, 2).Select(phase => new RaidPhaseRewardPolicy("fixture/quota",
                [new(0, 0, [new(100, 1)], [])], new(0, (byte)phase, 0, 0, 0, 0))).ToImmutableArray();
            var battle = new AntonRaidBattle(script, kind: RaidKind.Luke, clock: clock, flow: LukeRaidFlowPolicy.Shared95);
            var actor = session.CaptureRaidActor(new RaidRegistry())!;
            That(session.AttachRaidCombat(actor, battle, policies), "刷新夹具安装真实当前会话的攻坚绑定");
            RejectActive(AntonRaidBattleState.Fighting);
            var instance = Guid.NewGuid();
            var identity = new DungeonRunIdentity(actor.Character, 0, 1, instance, 1);
            using (var reservation = battle.ReserveDungeon(new(battle.Instance, 0, 1, RaidKind.Luke), 10, [(actor, identity)])!)
                reservation.Install();
            battle.CompleteLukeDungeon([(actor, new DungeonClearFact(identity, 10, [Guid.NewGuid()])
                { ClearMap = new("boss", new(0, 0), 77, "fixture/quota-boss.map") })]);
            battle.ReleaseDungeon(actor, instance);
            RejectActive(AntonRaidBattleState.Reward);
            battle.ConfirmReward(actor, new(battle.Instance, 0, 0, 0, actor.Character, "fixture/quota", [new(100, 1)], false, 1)
                { RaidType = RaidKind.Luke });
            That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision)?.After.State == AntonRaidBattleState.Intermission,
                "真实领奖确认后进入卢克休息待命");
            RejectActive(AntonRaidBattleState.Intermission);
            f.Integrity();

            int Reset(bool reward) => reward ? session.ResetRewardLimits().RaidEpochsAdvanced : session.ResetDungeonEntries().RaidEpochsAdvanced;
            void SeedCurrentRun()
            {
                // 每轮都放入当前两轴的真实持久run事实，避免空世代无更新导致故障夹具未触发。
                f.Execute($"""
                    INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state,entry_epoch,reward_epoch)
                    VALUES({created.Id},'quota','{Guid.NewGuid():N}',1,'fixture/quota-session',1,1,
                      COALESCE((SELECT entry_epoch FROM account_raid_quota_epochs WHERE account='quota' AND raid_type=1),0),
                      COALESCE((SELECT reward_epoch FROM account_raid_quota_epochs WHERE account='quota' AND raid_type=1),0));
                    """);
            }
            void CheckSelectionReset(bool reward)
            {
                SeedCurrentRun();
                var old = session.CaptureSelectionContents(rules, 0)!;
                That(old is not null && session.CanPublishSelectionContents(old), "选角额度查询捕获真实可发布回执");
                That(Reset(reward) == 1, "真实当前世代run使选角刷新只推进一个已用团本类型");
                That(!session.CanPublishSelectionContents(old!) && session.CanPublishSelectionContents(session.CaptureSelectionContents(rules, 0)!),
                    "刷新成功只淘汰旧选角额度回执，新查询仍可发布");
                var current = session.CaptureSelectionContents(rules, 0)!;
                SeedCurrentRun();
                f.Execute("CREATE TRIGGER quota_reset_fail BEFORE UPDATE ON account_raid_quota_epochs " +
                    "WHEN NEW.account='quota' BEGIN SELECT RAISE(ABORT,'quota reset fixture failure'); END;");
                string saved = f.Snapshot();
                try
                {
                    Reject<SqliteException>(() => Reset(reward), "选角刷新沿真实仓储报告事务失败");
                    That(f.Snapshot() == saved && session.CanPublishSelectionContents(current),
                        "事务失败不写库且当前选角额度显示回执继续有效");
                }
                finally { f.Execute("DROP TRIGGER quota_reset_fail;"); }
            }
            void CheckRaidDisplayReset(bool reward)
            {
                SeedCurrentRun();
                var old = session.CaptureRaidClearCounts(null)!;
                That(session.CanPublishRaidClearCounts(old, null), "刷新前真实团本次数回执有效");
                That(Reset(reward) == 1, "真实当前世代run使城镇刷新只推进一个已用团本类型");
                That(!session.CanPublishRaidClearCounts(old, null) && session.CanPublishRaidClearCounts(session.CaptureRaidClearCounts(null)!, null),
                    "刷新成功淘汰旧团本次数回执，允许新结果发布");
            }
            void RejectActive(AntonRaidBattleState expected)
            {
                That(session.Phase == SessionPhase.Town && battle.View.State == expected,
                    "角色在城镇待命时仍保留真实攻坚阶段 " + expected);
                foreach (bool reward in new[] { false, true })
                {
                    var token = session.CaptureRaidClearCounts(null)!;
                    string saved = f.Snapshot();
                    var view = battle.View;
                    Reject<InvalidOperationException>(() => Reset(reward), "活动攻坚阶段拒绝刷新 " + expected);
                    That(f.Snapshot() == saved && session.CanPublishRaidClearCounts(token, null) && ReferenceEquals(view, battle.View),
                        "拒绝刷新不写库、不使显示回执失效，也不推进攻坚状态 " + expected);
                }
            }
        }
        finally { session.End(); }
        Console.WriteLine($"PASS {Count - before} 团本额度会话门禁与显示回执断言");
    }

    static SelectionContentsRules Rules()
    {
        object[] grief = ["[enterable count]", 1, "[account enterable max count]", 1, "[min level]", 1, "[top layer]", 1,
            "[use allow item list in dungeon]", "[/use allow item list in dungeon]",
            "[adventure group add enterable count]", "[/adventure group add enterable count]",
            "[each layer matching dungeon]", 1, 5115, -1, "[/each layer matching dungeon]",
            "[each layer dungeon anti evil value]", 1, 0, "[/each layer dungeon anti evil value]",
            "[each layer dungeon clear reward]", 1, 1, "fixture", "[/each layer dungeon clear reward]"];
        var strings = new List<string>();
        PvfToken[] Encode(object[] values) => values.Select(value =>
        {
            if (value is int number) return new PvfToken(0, number);
            string text = (string)value; strings.Add(text);
            return new PvfToken(text.StartsWith('[') ? (byte)3 : (byte)6, strings.Count - 1);
        }).ToArray();
        var tower = new GriefTowerCatalog(Encode(grief), id => strings[id]);
        var incounts = DungeonIncountCatalog.Parse(Encode(["[dungeon incount info]", "[/dungeon incount info]"]), id => strings[id]);
        return new(new(1, 1, 1, [], [new(1, 11008, -1)]), tower, incounts, null);
    }
}
