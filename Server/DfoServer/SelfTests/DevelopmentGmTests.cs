using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Town;
using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Characters;
using DfoServer.Development;
using DfoServer.Network.Parsers;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using PvfLib;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DfoServer;

internal static class DevelopmentGmTests
{
    public static async Task RunCoreAsync()
    {
        int checks = Count;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            using var stop = new CancellationTokenSource();
            int handled = 0;
            var (port, loop) = DevelopmentGmServer.StartEphemeral(_ => { handled++; return "OK"; }, stop.Token);
            using var client = new TcpClient();
            try
            {
                if (attempt == 7)
                {
                    // 完整回执确认监听可用，再以未完整命令覆盖接入/读取期间的取消。
                    using var first = new TcpClient();
                    await first.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3));
                    await first.GetStream().WriteAsync("probe\n"u8.ToArray()).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                    using var reader = new StreamReader(first.GetStream(), Encoding.UTF8);
                    That(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) == "OK", "GM取消前已返回完整命令回执");
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3));
                    await client.GetStream().WriteAsync("partial"u8.ToArray()).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
            finally
            {
                stop.Cancel();
                stop.Cancel();
                await loop.WaitAsync(TimeSpan.FromSeconds(3));
            }
            That(loop.IsCompletedSuccessfully && handled == (attempt == 7 ? 1 : 0), "GM待接入或未完整命令取消正常结束，重复取消不执行命令");
            client.Dispose();
            var released = new TcpListener(IPAddress.Loopback, port);
            try
            {
                released.Start();
                That(released.Server.IsBound, "GM停止后端口可重新绑定");
            }
            finally { released.Stop(); }
        }

        using var failureStop = new CancellationTokenSource();
        var expected = new InvalidOperationException("synthetic GM handler failure");
        var (failurePort, failed) = DevelopmentGmServer.StartEphemeral(_ => throw expected, failureStop.Token);
        using var failureClient = new TcpClient();
        bool propagated = false;
        try
        {
            await failureClient.ConnectAsync(IPAddress.Loopback, failurePort).WaitAsync(TimeSpan.FromSeconds(3));
            await failureClient.GetStream().WriteAsync("fail\n"u8.ToArray()).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await failed.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, expected)) { propagated = true; }
        finally
        {
            failureStop.Cancel();
            try { await failed.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, expected)) { }
        }
        That(propagated, "GM未纳入单连接隔离的处理异常仍向宿主传播");
        Console.WriteLine($"PASS {Count - checks} development GM cancellation assertions");
    }

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var progress = new CharacterProgressReset(characters, learning, global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning));
        int checks = Count;

        void Reject(Action action, string name)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or SqliteException)
            {
                rejected = true;
            }

            That(rejected, name);
        }

        var parsed = DevelopmentRequest.Parse("account other-1 level GmHero 40", "development-local");
        That(parsed.Account == "other-1" && parsed.Action == DevelopmentAction.Level && parsed.Name == "GmHero" && parsed.Primary == 40, "account prefix selects the owner");
        That(DevelopmentRequest.Parse("list", "development-local").Account == "development-local", "omitted account uses the development account");
        Reject(() => DevelopmentRequest.Parse("", "development-local"), "empty command rejected");
        Reject(() => DevelopmentRequest.Parse("list extra", "development-local"), "extra list token rejected");
        Reject(() => DevelopmentRequest.Parse("level GmHero 0", "development-local"), "level 0 rejected");
        Reject(() => DevelopmentRequest.Parse("level GmHero 96", "development-local"), "level 96 rejected");
        Reject(() => DevelopmentRequest.Parse("story GmHero 96", "development-local"), "story 96 rejected");
        Reject(() => DevelopmentRequest.Parse("grow GmHero 128", "development-local"), "grow 128 rejected");
        Reject(() => DevelopmentRequest.Parse("quest-add GmHero 0", "development-local"), "quest 0 rejected");
        Reject(() => DevelopmentRequest.Parse("quest-add GmHero 30000", "development-local"), "quest 30000 rejected");
        Reject(() => DevelopmentRequest.Parse("skill GmOther 1 256", "development-local"), "skill level 256 rejected");
        Reject(() => DevelopmentRequest.Parse("skill GmOther 1 -1", "development-local"), "negative skill level rejected");
        Reject(() => DevelopmentRequest.Parse("account bad/name list", "development-local"), "account separator rejected");
        string root = Path.Combine(Path.GetTempPath(), "ServerS5A9-gm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using (var owner = owners.Acquire("gm", () =>
            {
            }))
            {
                repo.Create(owner, new("GmHero", towns.Room, towns.InitialReturn));
                repo.Create(owner, new("GmOther", towns.Room, towns.InitialReturn));
                Reject(() => repo.UpdateProgress(owner, new(CharacterProgressField.Level, "gm", "GmHero", 0, 0)), "repository rejects level 0");
                Reject(() => repo.UpdateProgress(owner, new(CharacterProgressField.Level, "other", "GmHero", 2, 0)), "lease account mismatch rejected");
            }

            string Level(string name, int level) => DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("level " + name + " " + level, "gm"), progress: progress);

            void KeepSkillsOnLevel()
            {
                using (var owner = owners.Acquire("keep", () => { }))
                    repo.Create(owner, new("GmKeep", towns.Room, towns.InitialReturn));
                DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("level GmKeep 20", "keep"), progress: progress);
                var base20 = Read("keep", "GmKeep");
                That(base20.Learning.BonusSp is int && base20.Learning.BonusTp is int, "新角色额外SP/TP来源已登记");
                int bonusSp = base20.Learning.BonusSp ?? 0;
                var planned = global::DfoServer.Game.Skills.SkillLearning.Plan(base20.Job, base20.GrowType, base20.Level, base20.Learning, new(0, 0, [new(5, false, 2)]), learning);
                That(planned.Success, "GM改等级前学习鬼斩两级");
                Character learned;
                using (var owner = owners.Acquire("keep", () => { }))
                    learned = repo.CommitLearning(owner, base20, planned.State!, learning.TotalSp(20) + bonusSp);
                var ghost = learned.Learning.Trees[0].Skills.Single(skill => skill.Id == 5);
                int spent20 = learning.TotalSp(20) + bonusSp - learned.Learning.Trees[0].Sp;
                That(spent20 > 0, "学习消耗了SP");
                That(DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("level GmKeep 30", "keep"), progress: progress).Contains("保留已学技能"), "level 回执说明保留技能");
                var up = Read("keep", "GmKeep");
                That(up.Level == 30 && up.Learning.Trees[0].Skills.Contains(ghost) &&
                    up.Learning.Trees[0].Sp == learning.TotalSp(30) + bonusSp - spent20 &&
                    up.Learning.Trees[1].Sp == learned.Learning.Trees[1].Sp + learning.TotalSp(30) - learning.TotalSp(20) &&
                    up.Learning.BonusSp == bonusSp && up.Learning.BonusTp == base20.Learning.BonusTp, "升级保留已学技能与槽位，SP=新总额+额外−已用");
                var down = DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("level GmKeep 25", "keep"), progress: progress);
                That(Read("keep", "GmKeep") is { Level: 25 } d25 && d25.Learning.Trees[0].Skills.Contains(ghost) &&
                    d25.Learning.Trees[0].Sp == learning.TotalSp(25) + bonusSp - spent20 && down.StartsWith("OK"), "门槛内降级保留技能并按新总额重算");
                int required = learning.Get(base20.Job, 5).GetOrdinaryNextRequiredCharacterLevel(ghost.Level - 1);
                That(required > 1, "鬼斩当前等级有高于1级的门槛");
                var before = Read("keep", "GmKeep");
                var rejected = DevelopmentGm.Answer("level GmKeep 1", "keep", repo, owners, _ => null, progress: progress);
                var after = Read("keep", "GmKeep");
                That(rejected.StartsWith("ERR") && rejected.Contains("需求等级") && after.Level == 25 && after.Revision == before.Revision &&
                    after.Learning.Trees[0].Skills.Contains(ghost), "降级低于已学技能门槛整笔拒绝，不截断技能");
                That(DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("tp GmKeep 9", "keep"), progress: progress).StartsWith("OK") &&
                    Read("keep", "GmKeep").Learning.BonusTp == 9, "tp 设置额外TP来源");
                Reject(() => DevelopmentRequest.Parse("tp GmKeep 50001", "keep"), "tp 50001 rejected");
                Reject(() => DevelopmentRequest.Parse("tp GmKeep -1", "keep"), "negative tp rejected");
                That(DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("level GmKeep 40", "keep"), progress: progress).StartsWith("OK") &&
                    Read("keep", "GmKeep") is { } tp40 && tp40.Learning.Trees.All(tree => tree.Tp == learning.TotalTp(40) + 9) && tp40.Learning.Trees[0].Skills.Contains(ghost),
                    "改等级把额外TP计入总额");
                DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("grow GmKeep 0", "keep"), progress: progress);
                var reset = Read("keep", "GmKeep");
                That(reset.Learning.Trees[0].Skills.Single(skill => skill.Id == 5).Level < ghost.Level &&
                    reset.Learning.Trees.All(tree => tree.Sp == learning.TotalSp(40) + bonusSp && tree.Tp == learning.TotalTp(40) + 9), "grow 仍重置技能树并恢复完整额度");
            }
            var written = Level("GmHero", 40);
            Character Read(string account, string name)
            {
                using var owner = owners.Acquire(account, () =>
                {
                });
                return repo.List(owner).Single(c => c.Name == name);
            }

            var hero = Read("gm", "GmHero");
            var other = Read("gm", "GmOther");
            That(hero.Level == 40 && hero.Revision == 1 && hero.Position == towns.Room && hero.Return == towns.InitialReturn && other.Level == 1 && other.Revision == 0, "等级重置只递增目标revision，保留位置和另一角色");
            That(written.Contains("level=40") && written.Contains("rev=1") && written.Contains("已写入"), "level response reports the committed row");
            That(DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("story GmHero 7", "gm"), progress: progress).Contains("story=7") && Read("gm", "GmHero").AcceptedStoryLevel == 7, "story level persists");
            That(DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("grow GmHero 33", "gm"), progress: progress).Contains("grow=33") && Read("gm", "GmHero") is { GrowType: 33, Level: 40, Revision: 2 }, "转职重置技能与点数并递增revision");
            KeepSkillsOnLevel();
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-add GmHero 10100", "gm"), progress: progress);
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-add GmHero 10100", "gm"), progress: progress);
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-add GmHero 12", "gm"), progress: progress);
            That(DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quests GmHero", "gm"), progress: progress) == "OK GmHero quests=12,10100", "duplicate quest add stays idempotent and ordered");
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-del GmHero 12", "gm"), progress: progress);
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-del GmHero 12", "gm"), progress: progress);
            That(Read("gm", "GmHero").CompletedQuests.SequenceEqual([10100]), "missing quest delete is idempotent");
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-clear GmHero", "gm"), progress: progress);
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-clear GmHero", "gm"), progress: progress);
            That(Read("gm", "GmHero").CompletedQuests.Length == 0 && Read("gm", "GmHero").Revision == 2, "任务清空保留当前revision");
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmOther 179 7", "gm"), progress: progress);
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmOther 179 3", "gm"), progress: progress);
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmOther 180 1", "gm"), progress: progress);
            That(DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skills GmOther", "gm"), progress: progress) == "OK GmOther skills=179:3,180:1", "skill upsert replaces level");
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmOther 179 0", "gm"), progress: progress);
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmOther 179 0", "gm"), progress: progress);
            That(Read("gm", "GmOther").Skills.SequenceEqual([new CharacterSkill(180, 1)]), "skill level 0 deletes and a second delete stays empty");
            Reject(() => DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("level Missing 2", "gm"), progress: progress), "missing character rejected");
            using (var beta = owners.Acquire("beta", () =>
            {
            }))
                repo.Create(beta, new("BetaHero", towns.Room, towns.InitialReturn));
            Reject(() => DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("level BetaHero 2", "gm"), progress: progress), "another account cannot edit the name");
            That(Read("beta", "BetaHero").Level == 1, "isolated account keeps level 1");
            That(DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("list", "gm"), progress: progress).StartsWith("OK count=2"), "list counts only the requested account");
            using (var reopen = owners.Acquire("gm", () =>
            {
            }))
                That(new CharacterRepository(path).List(reopen).Single(c => c.Name == "GmHero").Level == 40, "reopen restores the development edit");
            using (var liveOwner = owners.Acquire("live", () =>
            {
            }))
            {
                repo.Create(liveOwner, new("GmLive", towns.Room, towns.InitialReturn));
                var session = new GameSession(repo, towns, liveOwner);
                session.Select(0);
                session.MarkReady();
                long revision = session.Selected!.Revision;
                var text = DevelopmentGm.Execute(repo, owners, account => account == "live" ? session : null, DevelopmentRequest.Parse("level GmLive 40", "live"), progress: progress);
                That(session.Selected!.Level == 40 && session.Selected.Revision == revision + 1 && text.Contains("已写入"), "在线角色安装已提交等级与新revision");
                byte[] movement = Convert.FromHexString("CD01EA00066400030000000000");
                That(session.Move(TownRequests.Position(631, movement)) && session.Selected.Position.X == 461 && session.Selected.Revision == revision + 2 && session.Selected.Level == 40, "movement still commits after a development edit");
                repo.Create(liveOwner, new("GmIdle", towns.Room, towns.InitialReturn));
                DevelopmentGm.Execute(repo, owners, account => account == "live" ? session : null, DevelopmentRequest.Parse("level GmIdle 12", "live"), progress: progress);
                That(session.Selected.Name == "GmLive" && session.Selected.Level == 40 && repo.List(liveOwner).Single(c => c.Name == "GmIdle").Level == 12, "editing another role leaves the selected role in place");
            }

            using (var gateOwner = owners.Acquire("gate", () =>
            {
            }))
            {
                repo.Create(gateOwner, new("GmGate", towns.Room, towns.InitialReturn));
                var session = new GameSession(repo, towns, gateOwner);
                session.Select(0);
                session.MarkReady();
                DevelopmentGm.Execute(repo, owners, account => account == "gate" ? session : null, DevelopmentRequest.Parse("level GmGate 95", "gate"), progress: progress);
                var area = towns.Areas[((byte)39, (byte)0)];
                foreach (int quest in area.Quests.Concat(area.AnyQuests.Take(1)).Where(q => q != 10100).Distinct())
                    DevelopmentGm.Execute(repo, owners, account => account == "gate" ? session : null, DevelopmentRequest.Parse("quest-add GmGate " + quest, "gate"), progress: progress);
                var here = session.Selected!.Position;
                var gate = new AreaRequest(39, 0, 1837, 285, 5, here.Town, here.Area, 5);
                bool blocked = !session.Travel(gate);
                That(session.Selected!.Level == 95 && blocked && session.TravelReason == "portal-quest-required" && session.Selected.Position == here, "level 95 is live and quest 10100 still blocks the room portal (" + session.TravelReason + ")");
                DevelopmentGm.Execute(repo, owners, account => account == "gate" ? session : null, DevelopmentRequest.Parse("quest-add GmGate 10100", "gate"), progress: progress);
                That(session.Travel(gate) && session.Selected!.Position == new Location(39, 0, 1837, 285) && session.Selected.CompletedQuests.Contains(10100), "live quest set opens the portal immediately");
            }

            bool kicked = false;
            using (var held = owners.Acquire("held", () => kicked = true))
            {
                repo.Create(held, new("HeldOne", towns.Room, towns.InitialReturn));
                string? message = null;
                try
                {
                    DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("level HeldOne 4", "held"), progress: progress);
                }
                catch (InvalidOperationException ex)
                {
                    message = ex.Message;
                }

                That(message == "账号正在使用，请重试" && !kicked && repo.List(held).Single().Level == 1, "live owner is not replaced or written");
            }

            using (var stale = owners.Acquire("stale", () => kicked = true))
            {
                kicked = false;
                repo.Create(stale, new("StaleOne", towns.Room, towns.InitialReturn));
                var session = new GameSession(repo, towns, stale);
                session.Select(0);
                using var newer = owners.Acquire("stale", () =>
                {
                });
                string? message = null;
                try
                {
                    DevelopmentGm.Execute(repo, owners, _ => session, DevelopmentRequest.Parse("level StaleOne 4", "stale"), progress: progress);
                }
                catch (InvalidOperationException ex)
                {
                    message = ex.Message;
                }

                That(kicked && message == "账号世代已变化，请重试" && repo.List(newer).Single().Level == 1, "stale generation cannot write");
            }

            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TRIGGER reject_gm_level BEFORE UPDATE ON characters BEGIN SELECT RAISE(ABORT,'level rejected');END";
                cmd.ExecuteNonQuery();
                using var owner = owners.Acquire("gm", () =>
                {
                });
                var before = repo.List(owner).Single(c => c.Name == "GmHero");
                Reject(() => repo.ResetProgress(owner, progress.Prepare(before, DevelopmentRequest.Parse("level GmHero 50", "gm").ToProgressEdit())), "failed level update aborts");
                var after = repo.List(owner).Single(c => c.Name == "GmHero");
                That(after.Level == before.Level && after.Revision == before.Revision && after.CompletedQuests.SequenceEqual(before.CompletedQuests), "aborted level update leaves the row unchanged");
                cmd.CommandText = "DROP TRIGGER reject_gm_level";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE TRIGGER reject_gm_quest BEFORE INSERT ON character_completed_quests BEGIN SELECT RAISE(ABORT,'quest rejected');END";
                cmd.ExecuteNonQuery();
                Reject(() => repo.UpdateProgress(owner, DevelopmentRequest.Parse("quest-add GmHero 12", "gm").ToProgressEdit()), "failed quest insert aborts");
                That(repo.List(owner).Single(c => c.Name == "GmHero").CompletedQuests.SequenceEqual(before.CompletedQuests), "aborted quest insert leaves the quest table unchanged");
                cmd.CommandText = "DROP TRIGGER reject_gm_quest";
                cmd.ExecuteNonQuery();
            }

            using (var full = owners.Acquire("full", () =>
            {
            }))
                repo.Create(full, new("GmFull", towns.Room, towns.InitialReturn));
            for (int i = 0; i < 255; i++)
                DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmFull " + i + " 1", "full"), progress: progress);
            Reject(() => DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmFull 255 1", "full"), progress: progress), "skill 256 is rejected");
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmFull 0 2", "full"), progress: progress);
            That(Read("full", "GmFull").Skills.Length == 255 && Read("full", "GmFull").Skills[0] == new CharacterSkill(0, 2), "existing skill can change inside the 255 limit");
            using (var net = owners.Acquire("net", () =>
            {
            }))
                repo.Create(net, new("测角", towns.Room, towns.InitialReturn));
            using var stop = new CancellationTokenSource();
            var (port, loop) = DevelopmentGmServer.StartEphemeral(line => DevelopmentGm.Answer(line, "net", repo, owners, _ => null, progress: progress), stop.Token);
            try
            {
                string reply = "";
                bool shown = false;
                for (int i = 0; i < 20 && !shown; i++)
                {
                    if (DevelopmentGmClient.TryExecute(port, "show 测角", out reply))
                        shown = true;
                    else
                        Thread.Sleep(20);
                }

                That(shown && reply.Contains("测角") && reply.Contains("level=1"), "loopback show uses UTF-8");
                That(DevelopmentGmClient.TryExecute(port, "level 测角 8", out var updated) && updated.Contains("level=8") && updated.Contains("已写入"), "loopback level write");
                That(DevelopmentGmClient.TryExecute(port, "show Missing", out var missing) && missing.StartsWith("ERR ", StringComparison.Ordinal), "loopback missing character is an error");
                That(Read("net", "测角").Level == 8, "loopback write is durable");
            }
            finally
            {
                stop.Cancel();
                That(loop.Wait(TimeSpan.FromSeconds(3)), "gm listener stops");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }

        Console.WriteLine($"PASS {Count - checks} development GM assertions");
    }
}
