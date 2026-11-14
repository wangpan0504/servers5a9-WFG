using System.Reflection;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class FiendwarAreaEntryTcpTests
{
    // 复用本组实际TCP已安装的四人Loading，不另起服务。只注入代次更替，验证原生产守卫。
    static void VerifyTargetGuards(GameSession[] sessions, int dungeon, TcpTestResources resources)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var admissions = sessions.Select(session => (RaidDungeonAdmission)typeof(GameSession)
            .GetField("raidDungeonAdmission", flags)!.GetValue(session)!).ToArray();
        var actors = admissions.Select(admission => admission.Actor).ToArray();
        var battle = admissions[0].Battle!;
        AccountOwners.Lease.ExecuteGroup(actors.Select(actor => (actor.Owner, actor.Character)).ToArray(), () =>
        {
            var receipts = sessions.Select(session => session.ReplayAntonLoading(dungeon)).ToArray();
            That(receipts.All(receipt => receipt is not null) && receipts.Select(receipt => receipt!.Snapshot.Identity.InstanceId).Distinct().Count() == 1,
                "实际TCP四名Loading保留同一Run，原代可正常重放");
            var receipt = receipts[0]!;
            var oldTarget = receipt.Entry.RaidEntry!.FiendwarTarget!;
            var oldRun = receipt.Snapshot.Identity.InstanceId;
            var selections = sessions.Select((session, index) => new RaidDungeonSelection(admissions[index], dungeon,
                receipts[index]!.Entry.Maze.SourceOrdinal) { FiendwarTarget = receipts[index]!.Entry.RaidEntry!.FiendwarTarget }).ToArray();
            var original = battle.View;
            var replacement = original with { DungeonGenerations = original.DungeonGenerations.SetItem(dungeon, oldTarget.Generation + 1) };
            typeof(AntonRaidBattle).GetField("current", flags)!.SetValue(battle, replacement);
            That(sessions.All(session => session.ReplayAntonLoading(dungeon) is null), "已发送未加载的旧代Loading不能向新代重放");
            var canInstall = typeof(GameSession).GetMethod("CanInstallRaidDungeon", flags)!;
            That(sessions.Select((session, index) => !(bool)canInstall.Invoke(session, [receipts[index]!.Entry])!).All(value => value),
                "已准备旧代entry不能在新代安装");
            That(battle.ObserveFiendwarRun(oldRun, actors, receipt.Entry.StartMap.Id, loaded: true) is null,
                "原已安装Run迟到Loading事件不能写新代符号");
            var facts = actors.Select((actor, index) => (actor, new DungeonClearFact(receipts[index]!.Snapshot.Identity,
                checked((uint)dungeon), [Guid.NewGuid()]) { ClearMap = receipts[index]!.Entry.BossMap })).ToArray();
            That(battle.CompleteFiendwarDungeon(facts) is null && ReferenceEquals(battle.View, replacement),
                "原Run四人迟到完成事实被拒绝且Battle快照完全不变");

            // 用实际Session预置原选择尚未安装的边界；其它准入条件必须成立，避免空拒绝断言。
            var formalProperty = typeof(GameSession).GetProperty("formalDungeon", flags)!;
            var phaseField = typeof(GameSession).GetField("phase", flags)!;
            var selectionProperty = typeof(GameSession).GetProperty("CurrentDungeonSelection")!;
            var partyProperty = typeof(GameSession).GetProperty("PartyDungeon")!;
            for (int index = 0; index < sessions.Length; index++)
            {
                var session = sessions[index];
                var formal = formalProperty.GetValue(session);
                var party = partyProperty.GetValue(session);
                formalProperty.SetValue(session, null);
                partyProperty.SetValue(session, null);
                phaseField.SetValue(session, SessionPhase.MapSelect);
                try
                {
                    typeof(AntonRaidBattle).GetField("current", flags)!.SetValue(battle, original);
                    session.InstallRaidDungeonSelection(selections[index]);
                    typeof(AntonRaidBattle).GetField("current", flags)!.SetValue(battle, replacement);
                    That(session.CanBeginRaidDungeonSelection(admissions[index]), "旧选择测试的角色owner位置及原Battle准入仍有效");
                    bool rejected = false;
                    try { session.InstallRaidDungeonSelection(selections[index]); }
                    catch (InvalidOperationException) { rejected = true; }
                    That(rejected, "实际Session安装拒绝旧代选择，不能拿新代替换选择target");
                    var prepared = session.PreparePartyDungeon(checked((uint)dungeon), 0, resources.Dungeons,
                        SelfTestCatalogs.Rooms(resources.Archive), null, null, null, null, Guid.NewGuid(), 1);
                    That(prepared is null && session.LastFormalDungeonRejection == "raid-entry-selection-changed",
                        $"实际资源准备入口拒绝旧选择，未分配新Run：{session.LastFormalDungeonRejection}");
                }
                finally
                {
                    formalProperty.SetValue(session, formal); phaseField.SetValue(session, SessionPhase.Loading);
                    partyProperty.SetValue(session, party);
                    selectionProperty.SetValue(session, null);
                    typeof(AntonRaidBattle).GetField("current", flags)!.SetValue(battle, replacement);
                }
            }
            foreach (var actor in actors) battle.ReleaseDungeon(actor, oldRun);
            int[] candidates = admissions[0].Catalog.Find(dungeon)!.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0)
                .Select(maze => maze.SourceOrdinal).ToArray();
            var target = battle.FiendwarTarget(dungeon, candidates)!;
            var identity = receipt.Entry.RaidEntry with { FiendwarTarget = target };
            var nextRun = Guid.NewGuid();
            var runActors = actors.Select((actor, index) => (actor, receipts[index]!.Snapshot.Identity with { InstanceId = nextRun })).ToArray();
            using (var uninstalled = battle.ReserveDungeon(identity, checked((uint)dungeon), runActors,
                mazeOrdinal: target.MazeOrdinal, coinLimit: 1))
                That(uninstalled is not null, "旧Run释放后新代能预留，预留撤销沿原实例释放");
            using var installed = battle.ReserveDungeon(identity, checked((uint)dungeon), runActors,
                mazeOrdinal: target.MazeOrdinal, coinLimit: 1);
            That(installed is not null, "撤销未安装预留后同一新代能重新预留");
            installed!.CommitFiendwarEntry(nextRun, target, static () => { });
            installed!.Install();
            foreach (var actor in actors) battle.ReleaseDungeon(actor, oldRun);
            That(battle.ObserveFiendwarRun(nextRun, actors, receipt.Entry.StartMap.Id, loaded: false) is not null,
                "迟到旧Run释放不拆新Run，新代正式观察仍可提交");
            var valid = battle.View;
            var expired = valid with { FiendwarExpiredRuns = valid.FiendwarExpiredRuns.Add(nextRun) };
            typeof(AntonRaidBattle).GetField("current", flags)!.SetValue(battle, expired);
            That(!battle.IsFiendwarRunCurrent(nextRun) && battle.ObserveFiendwarRun(nextRun, actors,
                receipt.Entry.StartMap.Id, loaded: true) is null && ReferenceEquals(battle.View, expired),
                "已标记局部过期的原Run禁止迟到EnterMap写符号，快照完全不变");
            typeof(AntonRaidBattle).GetField("current", flags)!.SetValue(battle, valid);
            foreach (var actor in actors) battle.ReleaseDungeon(actor, nextRun);
            return true;
        });
    }
}
