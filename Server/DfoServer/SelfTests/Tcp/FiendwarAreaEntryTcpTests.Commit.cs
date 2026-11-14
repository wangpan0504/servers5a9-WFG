using System.Reflection;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class FiendwarAreaEntryTcpTests
{
    // 复用原TCP四人及夹具库；两人准备动态遭遇，两名队员各持一个原本体Run。
    static void VerifyCommitWindows(GameSession[] sessions, TcpTestFixture fixture, LukeRaidClockTests.Clock clock, TcpTestResources resources)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var admissions = sessions.Select(session => (RaidDungeonAdmission)typeof(GameSession)
            .GetField("raidDungeonAdmission", flags)!.GetValue(session)!).ToArray();
        var battle = admissions[0].Battle!;
        var actors = admissions.Select(row => row.Actor).ToArray();
        var source = battle.View;
        var stateField = typeof(AntonRaidBattle).GetField("current", flags)!;
        var project = typeof(AntonRaidBattle).GetMethod("ProjectFiendwar", flags)!;
        var engine = new FiendwarBattlefield(battle.FiendwarDefinition!, new());
        int enemyArea = source.FiendwarPositions[actors[0].Character];
        int enemyDungeon = battle.FiendwarDefinition!.Areas[enemyArea].DungeonId;
        var repository = new CharacterRepository(fixture.Database);
        foreach (string window in new[] { "after-commit", "delete-before-commit", "local-deadline-before-commit", "deadline-before-commit" })
        {
            // 三营完成只解锁双世界，不清兵；实际删除由两个原Run进入地下城后的状态2触发。
            var field = engine.CompleteArea(source.Fiendwar!, 27, clock.GetUtcNow()).State;
            field = engine.CompleteArea(field, 28, clock.GetUtcNow()).State;
            field = engine.CompleteArea(field, 29, clock.GetUtcNow()).State;
            var positions = source.FiendwarPositions;
            for (int index = 0; index < actors.Length; index++) positions = positions.SetItem(actors[index].Character, index < 2 ? enemyArea : index == 2 ? 30 : 31);
            var staged = (AntonRaidBattleView)project.Invoke(battle, [source with { FiendwarPositions = positions }, field])!;
            stateField.SetValue(battle, staged);
            That(staged.Fiendwar!.Areas[enemyArea].Enemy is not null && staged.Symbols["[top boss state]"] == 1 &&
                staged.Symbols["[bot boss state]"] == 1, "真实三营锁存开放双世界，原动态敌军仍存在");
            var target = Target(enemyDungeon);
            var topTarget = Target(battle.FiendwarDefinition.Areas[30].DungeonId);
            var botTarget = Target(battle.FiendwarDefinition.Areas[31].DungeonId);
            Guid runId = Guid.NewGuid(), topId = Guid.NewGuid(), botId = Guid.NewGuid();
            var primary = actors.Take(2).Select(actor => (actor, Identity(actor, runId))).ToArray();
            GameSession.PartyDungeonPreparation[] preparations = [];
            PartyDungeonReservation[] preparedReservations = [];
            PartyDungeonInstance? instance = null;
            var originalPositions = sessions.Take(2).Select(session => session.Selected!.Position).ToArray();
            if (window == "after-commit")
            {
                var party = sessions[0].CurrentParty!.Invoke()!;
                var members = party.Members.Where(member => sessions.Take(2).Contains(member.Actor.Session)).ToArray();
                instance = new(runId, party with { Members = members }, repository);
                var plans = new List<GameSession.PartyDungeonPreparation>();
                for (int index = 0; index < 2; index++)
                {
                    // 已有TCP场景完成其入口验证；在同一Session重建真实原选择/准备，随后用生产Install及退场消费。
                    typeof(GameSession).GetProperty("formalDungeon", flags)!.SetValue(sessions[index], null);
                    typeof(GameSession).GetProperty("PartyDungeon")!.SetValue(sessions[index], null);
                    typeof(GameSession).GetField("phase", flags)!.SetValue(sessions[index], SessionPhase.MapSelect);
                    sessions[index].InstallRaidDungeonSelection(new(admissions[index], enemyDungeon, target.MazeOrdinal) { FiendwarTarget = target });
                    var prepared = sessions[index].PreparePartyDungeon(checked((uint)enemyDungeon), 2, resources.Dungeons,
                        SelfTestCatalogs.Rooms(resources.Archive), null, null, null, null, runId,
                        sessions.Take(2).Max(session => session.Selected!.DungeonState!.NextObjectKey), plans.FirstOrDefault(), memberCount: 2);
                    That(prepared is not null, $"提交后窗口使用真实Session资源准备：{sessions[index].LastFormalDungeonRejection}");
                    plans.Add(prepared!);
                }
                preparations = plans.ToArray();
                preparedReservations = preparations.Select(token => token.Session.ReservePartyDungeon(token)!).ToArray();
                That(preparedReservations.All(row => row is not null), "提交后窗口原两名Session正式预留均成立");
                primary = actors.Take(2).Select((actor, index) => (actor, preparedReservations[index].Charge.Identity)).ToArray();
            }
            _ = battle.FiendwarEntryScale(topTarget.Dungeon, actors.Skip(2).Select(actor => actor.Character));
            using var run = battle.ReserveDungeon(new(battle.Instance, 0, 1, RaidKind.Fiendwar) { FiendwarTarget = target },
                checked((uint)enemyDungeon), primary, mazeOrdinal: target.MazeOrdinal, coinLimit: 1);
            using var top = battle.ReserveDungeon(new(battle.Instance, 0, 2, RaidKind.Fiendwar) { FiendwarTarget = topTarget },
                checked((uint)topTarget.Dungeon), [(actors[2], Identity(actors[2], topId))], mazeOrdinal: topTarget.MazeOrdinal, coinLimit: 1);
            using var bot = battle.ReserveDungeon(new(battle.Instance, 0, 3, RaidKind.Fiendwar) { FiendwarTarget = botTarget },
                checked((uint)botTarget.Dungeon), [(actors[3], Identity(actors[3], botId))], mazeOrdinal: botTarget.MazeOrdinal, coinLimit: 1);
            That(run is not null && top is not null && bot is not null, "SQL窗口原四名actor持有动态预留和两个互不重叠的本体Run");
            top!.CommitFiendwarEntry(topId, topTarget, static () => { }); top.Install();
            bot!.CommitFiendwarEntry(botId, botTarget, static () => { }); bot.Install();
            var reservations = primary.Select(row => new PartyDungeonReservation(row.actor.Owner, row.actor.Session.Selected!,
                row.actor.Session.Selected!.DungeonState!.NextObjectKey,
                new(null, 0, row.Item2, admissions[0].Catalog.Find(enemyDungeon)!.Mazes.Single(maze => maze.SourceOrdinal == target.MazeOrdinal)
                    .MapSpecification!.First().Id) { DungeonId = enemyDungeon })
                { FiendwarTarget = target, FiendwarCommit = run!.CommitFiendwarEntry }).ToArray();
            if (window == "after-commit")
                reservations = preparedReservations.Select(row => row with { FiendwarCommit = run!.CommitFiendwarEntry }).ToArray();
            string beforeSql = fixture.Snapshot("SELECT c.id,c.revision,s.next_object_key,s.last_attempt FROM characters c JOIN character_dungeon_state s ON s.character_id=c.id ORDER BY c.id");
            if (window == "after-commit")
            {
                var committed = repository.CommitPartyDungeonEntry(reservations);
                That(committed.Length == 2 && beforeSql != fixture.Snapshot("SELECT c.id,c.revision,s.next_object_key,s.last_attempt FROM characters c JOIN character_dungeon_state s ON s.character_id=c.id ORDER BY c.id"),
                    "最终guard在线性化点一次提交原两名成员的真实SQL");
                EnterOtherWorlds();
                for (int index = 0; index < committed.Length; index++)
                    sessions[index].InstallPartyDungeon(preparations[index], committed[index], reservations[index], instance!);
                run!.Install();
                That(sessions.Take(2).All(session => session.Phase == SessionPhase.Loading && session.PartyDungeon == instance &&
                    session.CurrentFormalDungeonScene()?.Snapshot.Identity.InstanceId == runId), "SQL后目标失效仍完整安装两名原成员的正式Run");
                That(battle.View.FiendwarExpiredRuns.Contains(runId) && !battle.View.FiendwarExpiredRuns.Contains(topId) &&
                    !battle.View.FiendwarExpiredRuns.Contains(botId), "提交后删除只标记失配原Run，两个本体Run不受影响");
                using (var returning = battle.ReserveFiendwarReturn(primary[0].actor, runId))
                    That(returning is null && !battle.View.FiendwarBridgeReturns.ContainsKey(runId), "普通目标失效不伪造桥返回规则");
                That(battle.ObserveFiendwarRun(runId, primary.Select(row => row.actor).ToArray(), reservations[0].Charge.MapId, false) is null,
                    "SQL提交后的全局删除不拒绝原Run完整安装，迟到观察安全失效");
                for (int index = 0; index < committed.Length; index++)
                {
                    int member = index;
                    var returned = instance!.Execute(_ => sessions[member].ReturnFromFormalDungeon());
                    That(returned is not null && sessions[index].Phase == SessionPhase.Town &&
                        sessions[index].CurrentFormalDungeonScene() is null && returned.Character.Position == originalPositions[index] &&
                        battle.FiendwarPosition(actors[index].Character) == enemyArea,
                        "原系统退场入口消费过期Run并返回既有安全点，保留原区域身份");
                    _ = instance.Detach(sessions[index]);
                }
                That(!battle.View.FiendwarExpiredRuns.Contains(runId) && battle.IsFiendwarRunCurrent(topId) &&
                    battle.IsFiendwarRunCurrent(botId), "退场释放原Run且不释放两个有效本体Run");
            }
            else
            {
                using var held = new ManualResetEventSlim();
                using var proceed = new ManualResetEventSlim();
                var writer = Task.Run(() =>
                {
                    using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Database, Pooling = false }.ToString());
                    db.Open();
                    using var lease = db.BeginWrite("FiendwarAreaEntryTcpTests.TargetCommitWindow");
                    held.Set();
                    That(proceed.Wait(TimeSpan.FromSeconds(10)), "SQL窗口注入器等待本次原提交调用");
                    var gate = (SemaphoreSlim)typeof(SqliteWriteGate).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                    var waiters = typeof(SemaphoreSlim).GetField("m_waitCount", flags) ?? typeof(SemaphoreSlim).GetField("_waitCount", flags)!;
                    That(SpinWait.SpinUntil(() => Convert.ToInt32(waiters.GetValue(gate)) > 0, TimeSpan.FromSeconds(10)), "原Run预留后真实SQL进入writer等待，实例gate仍可推进另一Run");
                    if (window == "delete-before-commit") EnterOtherWorlds();
                    else if (window == "local-deadline-before-commit")
                    {
                        clock.AdvanceMillisecondsWithoutCallbacks(checked((battle.FiendwarDefinition.Areas[enemyArea].LimitSeconds * 2 + 1) * 1000));
                        That(clock.GetUtcNow() < staged.Fiendwar!.Deadline, "局部截止窗口尚未达到总期限，独立覆盖原encounter期限");
                    }
                    else clock.AdvanceMillisecondsWithoutCallbacks(3610 * 1000);
                });
                That(held.Wait(TimeSpan.FromSeconds(10)), "受控writer在原提交前取得预算");
                bool rejected = false;
                try { proceed.Set(); repository.CommitPartyDungeonEntry(reservations); }
                catch (FiendwarEntryChangedException) { rejected = true; }
                finally { writer.GetAwaiter().GetResult(); }
                That(rejected && beforeSql == fixture.Snapshot("SELECT c.id,c.revision,s.next_object_key,s.last_attempt FROM characters c JOIN character_dungeon_state s ON s.character_id=c.id ORDER BY c.id"),
                    $"writer等待中的{window}在最终COMMIT拒绝，全部角色revision和尝试号真实回滚");
            }
            foreach (var actor in primary) battle.ReleaseDungeon(actor.actor, runId);
            battle.ReleaseDungeon(actors[2], topId);
            battle.ReleaseDungeon(actors[3], botId);

            void EnterOtherWorlds()
            {
                AccountOwners.Lease.ExecuteGroup(actors.Skip(2).Select(actor => (actor.Owner, actor.Character)).ToArray(), () =>
                {
                    var topChange = battle.ObserveFiendwarRun(topId, [actors[2]], Start(topTarget), loaded: true);
                    That(topChange is not null && battle.View.Symbols["[top boss state]"] == 2 && battle.View.Fiendwar!.Areas[enemyArea].Enemy is not null,
                        "第一个真实本体Run进入起始图写状态2，另一世界尚为1时不清敌军");
                    var botChange = battle.ObserveFiendwarRun(botId, [actors[3]], Start(botTarget), loaded: true);
                    That(botChange is not null, "第二个原本体Run真实EnterDungeon/EnterMap事件被接纳");
                    That(battle.View.Symbols["[top boss state]"] == 2 && battle.View.Symbols["[bot boss state]"] == 2,
                        "两原Run进入后原资源状态均为2");
                    That(battle.View.Fiendwar!.Areas[enemyArea].Enemy is null,
                        "原状态2/2规则DeleteAllEnemy删除SQL预留方serial");
                    return true;
                });
            }
        }

        FiendwarDungeonTarget Target(int dungeon) => battle.FiendwarTarget(dungeon, admissions[0].Catalog.Find(dungeon)!.Mazes
            .Where(maze => maze.QuestConnectionTokens.Count == 0).Select(maze => maze.SourceOrdinal).ToArray())!;
        int Start(FiendwarDungeonTarget target)
        {
            var maze = admissions[0].Catalog.Find(target.Dungeon)!.Mazes.Single(row => row.SourceOrdinal == target.MazeOrdinal);
            return maze.MapSpecification!.First(map => maze.Starts.Contains(map.Position)).Id;
        }
        static DungeonRunIdentity Identity(RaidActor actor, Guid id) => new(actor.Character, 0,
            actor.Session.Selected!.DungeonState!.LastAttempt + 1, id, 1);
    }
}
