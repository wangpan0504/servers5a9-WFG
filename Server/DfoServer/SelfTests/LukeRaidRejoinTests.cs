using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeRaidRejoinTests
{
    internal static void RunCore()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-luke-rejoin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var leases = new List<AccountOwners.Lease>(); var sessions = new List<GameSession>();
        bool passed = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            var origin = new Location(1, 0, 10, 10);
            var map = new TownMap("synthetic/rejoin.map", [new(new(0, 0, 30, 30), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], origin, map with { IsRoom = true }, [], []),
                [(32, 0)] = new(32, 0, 1, [], [], [], new(32, 0, 10, 10), map with { Links = [new(new(10, 6, 4, 10), 32, 4, [], [])] }, [], []),
                [(32, 4)] = new(32, 4, 1, [], [], [], null, map with { Links = [new(new(10, 6, 4, 10), 1, 0, [], []), new(new(10, 6, 4, 10), 32, 0, [], [])] }, [], []) },
                origin, origin, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var owners = new AccountOwners(); var raids = new RaidRegistry();
            var repository = new CharacterRepository(path); var parties = new PartyService(repository);
            var clock = new LukeRaidClockTests.Clock(DateTimeOffset.UtcNow);
            var records = new AntonRaidRejoin(true, clock: clock); raids.BindRejoin(records);
            var recovery = new LukeRaidRejoin(raids, parties, records);
            var script = AntonRaidBattleTests.Script(10, 20) with { WaitingRoom = new(32, 4), EntryCost = new(100, 1) };
            var definition = new RaidDefinition(1, script.Path, 8, 1, 2) { Resources = new(script, [], [], [], [], []) };
            var start = new LukeRaidStart(raids, parties, repository, towns,
                new EpicPieceCatalog("[piece list]\n9001 9002\n[/piece list]"), new("synthetic/rejoin", script), clock: clock);
            RaidActor Login(int index, bool create)
            {
                var owner = owners.Acquire("luke-rejoin-" + index, () => { }); leases.Add(owner);
                if (create) repository.Create(owner, new("LkRej" + index, origin, origin) { Level = 90, CompletedQuests = [2903, 4872],
                    Inventory = new([new InventoryItem(0, new(InventoryListType.Main, 121), new(100, ItemKind.Material, 2))]) });
                var session = new GameSession(repository, towns, owner, entry: new(new(2, 20), 32, "[luke_raid]")); sessions.Add(session);
                session.Select(0); session.MarkReady(); session.CurrentParty = () => parties.Current(session);
                return session.CaptureRaidActor(raids)!;
            }
            LukeRaidRejoinResult Restore(RaidActor actor, uint key)
            {
                // 与正式668相同：已持本连接命令门禁，先恢复真实身份，再由调用方迁城。
                actor.Session.CommandGate.Wait();
                try { return recovery.Restore(actor, key, callerCommandHeld: true); }
                finally { actor.Session.CommandGate.Release(); }
            }
            var actors = Enumerable.Range(0, 3).Select(index => Login(index, true)).ToArray();
            That(actors[0].Session.LeaveRoom(), "无团角色可离开共享房间到普通频道街区");
            var unbound = actors[0].Session.Selected;
            string unboundSql = Assets(path);
            That(!actors[0].Session.Travel(new(32, 4, 12, 11, 5, 32, 0, 0), out var denied) && denied is null &&
                actors[0].Session.TravelReason == "luke-raid-required" && ReferenceEquals(unbound, actors[0].Session.Selected) &&
                Assets(path) == unboundSql, "未组真实卢克攻坚队时入口拒绝，位置、资产及切图回执均不变");
            That(parties.SetInformation(actors[0].Session, 0, PartySettings.Default).Accepted &&
                parties.Current(actors[0].Session) is not null &&
                !actors[0].Session.Travel(new(32, 4, 12, 11, 5, 32, 0, 0)) && Assets(path) == unboundSql,
                "真实普通队伍也不能代替卢克攻坚队准入");
            That(parties.Leave(actors[0].Session).Accepted, "普通队伍门禁夹具正常解散");
            That(raids.Create(actors[0], 0, "wrong", "wrong"u8.ToArray(), 8, 1) is not null &&
                !actors[0].Session.Travel(new(32, 4, 12, 11, 5, 32, 0, 0)) &&
                ReferenceEquals(unbound, actors[0].Session.Selected) && Assets(path) == unboundSql,
                "其它团本目录身份不能授权卢克入口");
            That(raids.Leave(actors[0], actors[0].Character) is not null, "其它团本夹具正常离团");
            That(raids.Create(actors[0], 1, "rejoin", "rejoin"u8.ToArray(), 8, 1) is not null, "普通街区可真实创建卢克攻坚队");
            That(actors[0].Session.Travel(new(32, 4, 12, 11, 5, 32, 0, 0)), "真实团员才可沿右侧门进入候战街区");
            var unstarted = actors[0].Session.Selected;
            That(actors[0].Session.BeginDungeonSelection() is null &&
                actors[0].Session.Phase == SessionPhase.Town && ReferenceEquals(actors[0].Session.Selected, unstarted),
                "未开团残留在候战区也不能建立普通选图，原角色及Town阶段保持不变");

            foreach (var member in actors.Skip(1))
            { raids.Invite(actors[0], member, 0); That(raids.Respond(member, actors[0].Character, true).Accepted, "原团邀请"); }
            foreach (var member in actors) raids.Assign(actors[0], member.Character, 1);
            var started = start.Start(actors[0], raids.Find(actors[0])!, definition, [], () => true);
            That(started.Error == 0 && started.Receipt is not null, "真实SQL开团扣票和普通分队安装");
            var battle = actors[0].Session.CurrentRaidBattle(actors[0])!;
            var beforeRaid = raids.Find(actors[0])!; var beforeParty = parties.Current(actors[0].Session)!;
            var fact = recovery.TryCaptureForDisconnect(actors[2].Session, out var captureReason);
            That(captureReason == "captured", "原owner空闲时自动捕获通过真实授权门禁");
            That(fact is { Luke: not null } && fact.Instance == battle.Instance && fact.Slot == 2 && fact.Squad == 1 &&
                fact.Luke.OriginalParty.Id == beforeParty.Id && fact.Luke.PartySlot == 2, "断线前捕获两个不同层次的精确槽位和原实例");
            That(!recovery.RecordDisconnected(fact!), "旧owner仍有效不发布恢复候选");
            using (var held = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
            {
                var blocker = Task.Run(() => actors[1].Owner.Execute(() =>
                { held.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("owner占用夹具超时"); return true; }));
                Task? cleanup = null;
                try
                {
                    That(held.Wait(TimeSpan.FromSeconds(5)), "其它成员owner确定已占用，非仅sleep推测");
                    cleanup = Task.Run(() =>
                    {
                        That(recovery.TryCaptureForDisconnect(actors[2].Session, out var reason) is null && reason == "owner-busy-or-stale",
                            "可选自动捕获遇其它owner占用零等待放弃");
                        actors[2].Session.End(); parties.Detach(actors[2].Session); actors[2].Owner.Dispose();
                        That(!recovery.TryRecordDisconnected(fact!, out reason) && reason == "owner-busy-or-stale" && ReferenceEquals(records.Peek(actors[2].Character), fact),
                            "自动登记不等待幸存者事务，保留已捕获的五分钟身份预约");
                    });
                    That(cleanup.Wait(TimeSpan.FromSeconds(5)) && !release.IsSet && !actors[2].Owner.IsCurrent,
                        "占用者尚未释放时原Session/Party/owner清理已完成");
                    var probe = Task.Run(() => actors[0].Owner.Execute(() => true));
                    That(probe.Wait(TimeSpan.FromSeconds(2)) && probe.Result, "排序中先取得的owner在后续TryEnter失败时已归还");
                }
                finally
                {
                    release.Set();
                    Task.WhenAll(new Task?[] { blocker, cleanup }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
            }
            bool called = false;
            That(!AccountOwners.Lease.TryExecuteLukeDisconnect([(actors[2].Owner, actors[2].Character)], () => { called = true; return true; }, out _) && !called,
                "失效owner组合不执行授权动作");
            Reject<InvalidOperationException>(() => actors[0].Owner.Execute(() =>
                AccountOwners.Lease.TryExecuteLukeDisconnect([(actors[0].Owner, actors[0].Character)], () => true, out _)),
                "可选捕获禁止单owner升级，保持原锁权威");
            Reject<InvalidDataException>(() => AccountOwners.Lease.TryExecuteLukeDisconnect<bool>([(actors[0].Owner, actors[0].Character)],
                () => throw new InvalidDataException("合成动作失败"), out _), "动作异常仍同步归还组合锁");
            That(actors[0].Owner.Execute(() => true), "失败后线程组合标记和owner均可正常使用");
            That(recovery.TryRecordDisconnected(fact!, out var recordedReason) && recordedReason == "recorded", "占用释放后自动登记原身份，不依赖旧owner仍有效");
            var fresh = Login(2, false);
            var captureOnly = new AntonRaidRejoin(false, lukeCandidatesEnabled: true, clock: clock);
            That(captureOnly.Record(fact!) && captureOnly.Peek(fresh.Character) == fact && !captureOnly.Enabled &&
                captureOnly.HasCandidate(fresh.Character) && !captureOnly.DescribeNotice(fresh, true, false).Matched &&
                !captureOnly.Evaluate(fresh, fact!.Key, true, false).Matched,
                "仅Luke内部候选开关不打开Anton通知或668，不把候选当重返成功");
            string sql = Assets(path); var character = fresh.Session.Selected;
            That(fresh.Session.CurrentRaidBattle(fresh) is null && raids.Find(fresh) is null &&
                fresh.Session.ReturnLukeRejoinToWaiting(fresh) is null && Assets(path) == sql &&
                ReferenceEquals(character, fresh.Session.Selected), "普通重登没有原团身份时不能借迁城接口进入候战");
            sql = Assets(path); character = fresh.Session.Selected;
            var wrongPhase = fact! with { Luke = new(fact!.Luke!.Original, battle, beforeParty, 2, fact.Luke.Phase + 1) };
            That(!raids.RecordLukeDisconnected(raids.Find(actors[0])!, wrongPhase, records) && ReferenceEquals(records.Peek(fresh.Character), fact),
                "不同阶段候选不能覆盖原断线资格");
            Guid heldRun = Guid.NewGuid();
            using (var held = battle.ReserveDungeon(new(battle.Instance, 0, 1, RaidKind.Luke), 10,
                actors.Take(2).Select(a => (a, new DungeonRunIdentity(a.Character, 0, 1, heldRun, 1))).ToArray()))
            {
                That(held is not null && !Restore(fresh, fact.Key).Accepted && fresh.Session.CurrentRaidBattle(fresh) is null,
                    "原团有在途Run预约时拒绝恢复，释放预约后才能继续");
            }
            That(records.Evaluate(fresh, fact!.Key, true, false).Reason == "luke-rejoin-wire-unavailable" && !fact.Attempted,
                "共用候选容器不让旧Anton668诊断消费Luke预约或发假成功");
            That(!Restore(fresh, fact.Key + 1).Accepted && fresh.Session.CurrentRaidBattle(fresh) is null, "错key拒绝且会话未安装");
            That(!Restore(fresh with { Channel = new(2, 21) }, fact.Key).Accepted, "错频道拒绝");
            That(!Restore(actors[2], fact.Key).Accepted, "旧owner请求拒绝");
            actors[1].Session.CommandGate.Wait();
            try { That(Restore(fresh, fact.Key).Reason == "command-busy", "全团命令忙时全部保持未安装"); }
            finally { actors[1].Session.CommandGate.Release(); }
            That(ReferenceEquals(character, fresh.Session.Selected) && Assets(path) == sql && raids.Find(fresh) is null,
                "失败规划不改SQL资产、角色快照或registry");
            var restored = Restore(fresh, fact.Key);
            That(restored.Accepted && restored.Reason == "bound-without-wire" && ReferenceEquals(fresh.Session.CurrentRaidBattle(fresh), battle) &&
                restored.Battle!.Instance == beforeRaid.Started!.InstanceId && restored.Battle.Phase == fact.Luke!.Phase,
                "领域恢复绑定同一个Battle同phase，无新实例");
            That(restored.Raid!.Key == beforeRaid.Key && restored.Raid.Members.Single(m => m.Actor == fresh) is { Slot: 2, Squad: 1 } &&
                restored.Party!.Id == beforeParty.Id && restored.Party.Members.Single(m => ReferenceEquals(m.Actor.Session, fresh.Session)).Slot == 2,
                "原团和原普通分队ID/槽位保持");
            That(restored.Raid.Leader == actors[0].Character && restored.Party!.Leader == 0, "普通成员恢复不改原队长");
            long revision = battle.View.Revision;
            That(Restore(fresh, fact.Key).Reason == "already-bound" && battle.View.Revision == revision, "同新owner重复恢复幂等且不重锚时钟");
            raids.RemoveSession(actors[2].Session, actors[2].Epoch);
            That(raids.Find(fresh)?.Started?.InstanceId == battle.Instance && Assets(path) == sql &&
                ReferenceEquals(character, fresh.Session.Selected), "旧session迟到清理不能移除新owner，恢复不写票据/资产/资格SQL");
            That(fresh.Session.ReturnLukeRejoinToWaiting(fresh) is not null, "正式重返先绑定真实团，再迁入受保护候战街区");
            var maze = new DungeonMaze(0, 1, 1, "1", [new(0, 0)], [new(0, 0)], [new("boss", new(0, 0), 77, "fixture/rejoin.map")], []);
            var definitions = new[] { 10, 20 }.ToImmutableDictionary(id => id, id => new DungeonDefinition(id, "fixture/rejoin.dgn", 1, 90, [maze], []));
            var catalog = new RaidDungeonCatalog(script, definitions, [], new Dictionary<int, string> { [77] = "fixture/rejoin.map" }, _ => true, 1);
            var selection = new LukeRaidDungeonService(raids, parties, catalog,
                new("synthetic/rejoin", [new(0, 10, 0), new(1, 20, 0)], LukeRaidRunEconomy.ControlledNoOrdinaryRewards));
            var selected = selection.Select(actors[0].Session, 10, parties.Current(actors[0].Session));
            That(selected.Length == 3 && fresh.Session.HasLukeRaidSelection,
                "成功恢复后原队长走真实Luke选择器，三名成员均绑定同实例选择而非仅检测名册");
            That(selection.CancelSelection(actors[0].Session, parties.Current(actors[0].Session)), "原选图合同恢复城镇状态");
            var leaderFact = recovery.Capture(actors[0])!;
            actors[0].Session.End(); parties.Detach(actors[0].Session); actors[0].Owner.Dispose();
            That(recovery.RecordDisconnected(leaderFact), "原队长断线可保留身份，但不代表可恢复");
            var leaderLogin = Login(0, false);
            var retainedRaid = raids.Find(actors[1])!; var retainedParty = parties.Current(actors[1].Session)!;
            string leaderAssets = Assets(path);
            var leaderRestored = Restore(leaderLogin, leaderFact.Key);
            That(leaderRestored.Accepted && leaderRestored.Party!.Leader == retainedParty.Leader &&
                leaderRestored.Raid!.Leader == retainedRaid.Leader && Assets(path) == leaderAssets,
                "原队长回团保留继任队长，不改变资产");
            That(leaderLogin.Session.ReturnLukeRejoinToWaiting(leaderLogin) is not null, "旧队长重返绑定后也可迁城");
            That(selection.Select(actors[1].Session, 10, parties.Current(actors[1].Session)).Length == 3,
                "低slot旧队长回来后，继任队长仍能为完整分队选择地下城");
            That(selection.CancelSelection(actors[1].Session, parties.Current(actors[1].Session)), "重返后取消选图正常");
            foreach (var disconnect in new[] { fresh, leaderLogin, actors[1] })
            {
                var reserved = recovery.Capture(disconnect)!;
                That(reserved is not null, "连续断线保留各成员原槽位");
                disconnect.Session.End(); parties.Detach(disconnect.Session); disconnect.Owner.Dispose();
                That(recovery.RecordDisconnected(reserved!), "全团最后一人离线后仍可登记原预约");
            }
            That(raids.Find(leaderFact.Key, leaderFact.Channel) is { Members.IsEmpty: true } &&
                records.Peek(fresh.Character) is not null, "全团离线保留同一原团五分钟，不复制资产");
            clock.Advance(299);
            var allBack = new[] { Login(2, false), Login(0, false), Login(1, false) };
            foreach (var returning in allBack)
            {

                That(Restore(returning, leaderFact.Key).Accepted &&
                    ReferenceEquals(returning.Session.CurrentRaidBattle(returning), battle) &&
                    returning.Session.ReturnLukeRejoinToWaiting(returning) is not null,
                    "全团与普通分队均消失后，第299秒依次返回同一实例");
            }
            That(parties.Current(allBack[0].Session) is { Members.Length: 3 } recreated &&
                allBack.All(actor => parties.Current(actor.Session)?.Id == recreated.Id),
                "首位重返重建分队，其余成员汇合而非另建平行分队");
            // 第299秒恢复后保持在线跨过原第300秒，真实触发候选清理与重返幂等入口。
            clock.Advance(1);
            That(allBack.All(actor => records.Peek(actor.Character) is { LukeDeadlineAnchor: null } &&
                Restore(actor, leaderFact.Key).Reason == "already-bound") &&
                raids.Find(allBack[0]) is { Members.Length: 3 },
                "在线重返跨原300秒后仍绑定原团，旧断线截止不能清除已绑定候选");
            var onlineParty = parties.Current(allBack[0].Session)!;
            var onlineLeader = onlineParty.Members.Single(member => member.Slot == onlineParty.Leader).Actor.Session;
            That(selection.Select(onlineLeader, 10, onlineParty).Length == 3 &&
                allBack.All(actor => actor.Session.HasLukeRaidSelection),
                "跨原断线截止后原分队仍能实际选图，不能只保留名册或候选");
            That(selection.CancelSelection(onlineLeader, parties.Current(onlineLeader)), "跨旧截止后的选图仍可正常取消");
            foreach (var disconnect in allBack)
            {
                var reserved = recovery.Capture(disconnect)!;
                That(reserved is { LukeDeadlineAnchor: { } anchor } && clock.GetElapsedTime(anchor) == TimeSpan.Zero,
                    "再次断线捕获当前新锚点，不复用已过期的首次断线锚点");
                disconnect.Session.End(); parties.Detach(disconnect.Session); disconnect.Owner.Dispose();
                That(recovery.RecordDisconnected(reserved), "重返后再次断线以本次离线重新计五分钟");
            }
            clock.Advance(299);
            That(allBack.All(actor => records.Peek(actor.Character) is { LukeDeadlineAnchor: not null }) &&
                raids.Find(leaderFact.Key, leaderFact.Channel) is { Members.IsEmpty: true },
                "再次离线未重返者在新锚点299秒仍保留原预约和空团");
            clock.Advance(1);
            That(records.Peek(fresh.Character) is null && raids.Count == 0,
                "恰好五分钟原预约及空团回收，不可用历史key复活实例");
            // 同一数据库第二次真实开团，独立于上面的仍Fighting末成员清理竞态。
            var phaseActors = new[] { Login(0, false), Login(1, false), Login(2, false) };
            raids.Create(phaseActors[0], 1, "phase", "phase"u8.ToArray(), 8, 1);
            foreach (var member in phaseActors.Skip(1))
            { raids.Invite(phaseActors[0], member, 0); That(raids.Respond(member, phaseActors[0].Character, true).Accepted, "阶段拒绝夹具真实入团"); }
            foreach (var member in phaseActors) raids.Assign(phaseActors[0], member.Character, 1);
            That(start.Start(phaseActors[0], raids.Find(phaseActors[0])!, definition, [], () => true).Error == 0,
                "第二独立实例沿真实SQL消费剩余票，不手工改Battle阶段");
            var phaseBattle = phaseActors[0].Session.CurrentRaidBattle(phaseActors[0])!;
            var phaseFact = recovery.Capture(phaseActors[2])!;
            phaseActors[2].Session.End(); parties.Detach(phaseActors[2].Session); phaseActors[2].Owner.Dispose();
            That(recovery.RecordDisconnected(phaseFact), "兼容高slot候选登记成功");
            var phaseLogin = Login(2, false);
            That(phaseBattle.CanRestoreLukeRejoin(phaseFact.Luke!) && raids.Find(phaseLogin) is null &&
                phaseFact.Slot == 2 && parties.Current(phaseActors[0].Session)!.Leader == 0,
                "pending变化之前候选phase/owner及普通队长均兼容，新登录仍未绑定");
            string phaseSql = Assets(path);
            Guid ending = Guid.NewGuid();
            var rows = phaseActors.Take(2).Select(a => (Actor: a, Identity: new DungeonRunIdentity(a.Character, 0, 1, ending, 1))).ToArray();
            using (var run = phaseBattle.ReserveDungeon(new(phaseBattle.Instance, 0, 1, RaidKind.Luke), 10, rows))
            {
                That(run is not null, "终结屏障回归预留原正式Run"); run!.Install();
                var facts = rows.Select(row => (row.Actor, new DungeonClearFact(row.Identity, 10, [Guid.NewGuid()])
                    { ClearMap = new("boss", new(0, 0), 77, "fixture/rejoin.map") })).ToArray();
                That(phaseBattle.CompleteLukeDungeon(facts) is not null && phaseBattle.HasPendingTransition &&
                    Restore(phaseLogin, phaseFact.Key).Reason == "stale-reservation",
                    "唯一新变化为Run终结待退出屏障，兼容高slot候选仍必须拒绝");
                foreach (var row in rows) phaseBattle.ReleaseDungeon(row.Actor, ending);
                That(phaseBattle.View.State == AntonRaidBattleState.Reward &&
                    Restore(phaseLogin, phaseFact.Key).Accepted &&
                    ReferenceEquals(phaseLogin.Session.CurrentRaidBattle(phaseLogin), phaseBattle) && Assets(path) == phaseSql,
                    "Run退出后可恢复原Reward阶段，已提交奖励资格和资产不重抽");
            }
            passed = true;
        }
        finally
        {
            foreach (var session in sessions) try { session.End(); } catch (InvalidOperationException) { }
            foreach (var lease in leases) lease.Dispose();
            if (passed) TestFiles.DeleteDirectory(root, true);
        }
    }

    static string Assets(string path)
    {
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"); db.Open();
        using var tables = db.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND (name='characters' OR name LIKE '%inventory%' OR name LIKE '%raid%') ORDER BY name";
        var names = new List<string>(); using (var reader = tables.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
        var rows = new List<string>();
        foreach (string name in names)
        {
            using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\"";
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(name + ":" + JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()));
        }
        return string.Join("\n", rows.Order(StringComparer.Ordinal));
    }
}
