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

namespace DfoServer;

internal static class DevelopmentGmTests
{
    public static void Run(TownCatalog towns)
    {
        int checks = 0;
        void Check(bool ok, string name)
        {
            checks++;
            if (!ok)
                throw new Exception("FAIL: " + name);
        }

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

            Check(rejected, name);
        }

        var parsed = DevelopmentRequest.Parse("account other-1 level GmHero 40", "development-local");
        Check(parsed.Account == "other-1" && parsed.Action == DevelopmentAction.Level && parsed.Name == "GmHero" && parsed.Primary == 40, "account prefix selects the owner");
        Check(DevelopmentRequest.Parse("list", "development-local").Account == "development-local", "omitted account uses the development account");
        Reject(() => DevelopmentRequest.Parse("", "development-local"), "empty command rejected");
        Reject(() => DevelopmentRequest.Parse("list extra", "development-local"), "extra list token rejected");
        Reject(() => DevelopmentRequest.Parse("level GmHero 0", "development-local"), "level 0 rejected");
        Reject(() => DevelopmentRequest.Parse("level GmHero 96", "development-local"), "level 96 rejected");
        Reject(() => DevelopmentRequest.Parse("story GmHero 96", "development-local"), "story 96 rejected");
        Reject(() => DevelopmentRequest.Parse("grow GmHero 128", "development-local"), "grow 128 rejected");
        Reject(() => DevelopmentRequest.Parse("quest-add GmHero 0", "development-local"), "quest 0 rejected");
        Reject(() => DevelopmentRequest.Parse("quest-add GmHero 30000", "development-local"), "quest 30000 rejected");
        Reject(() => DevelopmentRequest.Parse("skill GmHero 1 256", "development-local"), "skill level 256 rejected");
        Reject(() => DevelopmentRequest.Parse("skill GmHero 1 -1", "development-local"), "negative skill level rejected");
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

            string Level(string name, int level) => DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("level " + name + " " + level, "gm"));
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
            Check(hero.Level == 40 && hero.Revision == 0 && hero.Position == towns.Room && hero.Return == towns.InitialReturn && other.Level == 1 && other.Revision == 0, "level write keeps revision, position and the other character");
            Check(written.Contains("level=40") && written.Contains("rev=0") && written.Contains("已写入"), "level response reports the committed row");
            Check(DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("story GmHero 7", "gm")).Contains("story=7") && Read("gm", "GmHero").AcceptedStoryLevel == 7, "story level persists");
            Check(DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("grow GmHero 37", "gm")).Contains("grow=37") && Read("gm", "GmHero") is { GrowType: 37, Level: 40, Revision: 0 }, "grow byte persists without a revision bump");
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-add GmHero 10100", "gm"));
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-add GmHero 10100", "gm"));
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-add GmHero 12", "gm"));
            Check(DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quests GmHero", "gm")) == "OK GmHero quests=12,10100", "duplicate quest add stays idempotent and ordered");
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-del GmHero 12", "gm"));
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-del GmHero 12", "gm"));
            Check(Read("gm", "GmHero").CompletedQuests.SequenceEqual([10100]), "missing quest delete is idempotent");
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-clear GmHero", "gm"));
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("quest-clear GmHero", "gm"));
            Check(Read("gm", "GmHero").CompletedQuests.Length == 0 && Read("gm", "GmHero").Revision == 0, "quest clear removes every quest without a revision bump");
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmHero 179 7", "gm"));
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmHero 179 3", "gm"));
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmHero 180 1", "gm"));
            Check(DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skills GmHero", "gm")) == "OK GmHero skills=179:3,180:1", "skill upsert replaces level");
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmHero 179 0", "gm"));
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmHero 179 0", "gm"));
            Check(Read("gm", "GmHero").Skills.SequenceEqual([new CharacterSkill(180, 1)]), "skill level 0 deletes and a second delete stays empty");
            Reject(() => DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("level Missing 2", "gm")), "missing character rejected");
            using (var beta = owners.Acquire("beta", () =>
            {
            }))
                repo.Create(beta, new("BetaHero", towns.Room, towns.InitialReturn));
            Reject(() => DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("level BetaHero 2", "gm")), "another account cannot edit the name");
            Check(Read("beta", "BetaHero").Level == 1, "isolated account keeps level 1");
            Check(DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("list", "gm")).StartsWith("OK count=2"), "list counts only the requested account");
            using (var reopen = owners.Acquire("gm", () =>
            {
            }))
                Check(new CharacterRepository(path).List(reopen).Single(c => c.Name == "GmHero").Level == 40, "reopen restores the development edit");
            using (var liveOwner = owners.Acquire("live", () =>
            {
            }))
            {
                repo.Create(liveOwner, new("GmLive", towns.Room, towns.InitialReturn));
                var session = new GameSession(repo, towns, liveOwner);
                session.Select(0);
                session.MarkReady();
                long revision = session.Selected!.Revision;
                var text = DevelopmentGm.Execute(repo, owners, account => account == "live" ? session : null, DevelopmentRequest.Parse("level GmLive 40", "live"));
                Check(session.Selected!.Level == 40 && session.Selected.Revision == revision && text.Contains("已写入"), "selected character receives the new level without a revision change");
                byte[] movement = Convert.FromHexString("CD01EA00066400030000000000");
                Check(session.Move(TownRequests.Position(631, movement)) && session.Selected.Position.X == 461 && session.Selected.Revision == revision + 1 && session.Selected.Level == 40, "movement still commits after a development edit");
                repo.Create(liveOwner, new("GmIdle", towns.Room, towns.InitialReturn));
                DevelopmentGm.Execute(repo, owners, account => account == "live" ? session : null, DevelopmentRequest.Parse("level GmIdle 12", "live"));
                Check(session.Selected.Name == "GmLive" && session.Selected.Level == 40 && repo.List(liveOwner).Single(c => c.Name == "GmIdle").Level == 12, "editing another role leaves the selected role in place");
            }

            using (var gateOwner = owners.Acquire("gate", () =>
            {
            }))
            {
                repo.Create(gateOwner, new("GmGate", towns.Room, towns.InitialReturn));
                var session = new GameSession(repo, towns, gateOwner);
                session.Select(0);
                session.MarkReady();
                DevelopmentGm.Execute(repo, owners, account => account == "gate" ? session : null, DevelopmentRequest.Parse("level GmGate 95", "gate"));
                var area = towns.Areas[((byte)39, (byte)0)];
                foreach (int quest in area.Quests.Concat(area.AnyQuests.Take(1)).Where(q => q != 10100).Distinct())
                    DevelopmentGm.Execute(repo, owners, account => account == "gate" ? session : null, DevelopmentRequest.Parse("quest-add GmGate " + quest, "gate"));
                var here = session.Selected!.Position;
                var gate = new AreaRequest(39, 0, 1837, 285, 5, here.Town, here.Area, 5);
                bool blocked = !session.Travel(gate);
                Check(session.Selected!.Level == 95 && blocked && session.TravelReason == "portal-quest-required" && session.Selected.Position == here, "level 95 is live and quest 10100 still blocks the room portal (" + session.TravelReason + ")");
                DevelopmentGm.Execute(repo, owners, account => account == "gate" ? session : null, DevelopmentRequest.Parse("quest-add GmGate 10100", "gate"));
                Check(session.Travel(gate) && session.Selected!.Position == new Location(39, 0, 1837, 285) && session.Selected.CompletedQuests.Contains(10100), "live quest set opens the portal immediately");
            }

            bool kicked = false;
            using (var held = owners.Acquire("held", () => kicked = true))
            {
                repo.Create(held, new("HeldOne", towns.Room, towns.InitialReturn));
                string? message = null;
                try
                {
                    DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("level HeldOne 4", "held"));
                }
                catch (InvalidOperationException ex)
                {
                    message = ex.Message;
                }

                Check(message == "账号正在使用，请重试" && !kicked && repo.List(held).Single().Level == 1, "live owner is not replaced or written");
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
                    DevelopmentGm.Execute(repo, owners, _ => session, DevelopmentRequest.Parse("level StaleOne 4", "stale"));
                }
                catch (InvalidOperationException ex)
                {
                    message = ex.Message;
                }

                Check(kicked && message == "账号世代已变化，请重试" && repo.List(newer).Single().Level == 1, "stale generation cannot write");
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
                Reject(() => repo.UpdateProgress(owner, DevelopmentRequest.Parse("level GmHero 50", "gm").ToProgressEdit()), "failed level update aborts");
                var after = repo.List(owner).Single(c => c.Name == "GmHero");
                Check(after.Level == before.Level && after.Revision == before.Revision && after.CompletedQuests.SequenceEqual(before.CompletedQuests), "aborted level update leaves the row unchanged");
                cmd.CommandText = "DROP TRIGGER reject_gm_level";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE TRIGGER reject_gm_quest BEFORE INSERT ON character_completed_quests BEGIN SELECT RAISE(ABORT,'quest rejected');END";
                cmd.ExecuteNonQuery();
                Reject(() => repo.UpdateProgress(owner, DevelopmentRequest.Parse("quest-add GmHero 12", "gm").ToProgressEdit()), "failed quest insert aborts");
                Check(repo.List(owner).Single(c => c.Name == "GmHero").CompletedQuests.SequenceEqual(before.CompletedQuests), "aborted quest insert leaves the quest table unchanged");
                cmd.CommandText = "DROP TRIGGER reject_gm_quest";
                cmd.ExecuteNonQuery();
            }

            using (var full = owners.Acquire("full", () =>
            {
            }))
                repo.Create(full, new("GmFull", towns.Room, towns.InitialReturn));
            for (int i = 0; i < 255; i++)
                DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmFull " + i + " 1", "full"));
            Reject(() => DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmFull 255 1", "full")), "skill 256 is rejected");
            DevelopmentGm.Execute(repo, owners, _ => null, DevelopmentRequest.Parse("skill GmFull 0 2", "full"));
            Check(Read("full", "GmFull").Skills.Length == 255 && Read("full", "GmFull").Skills[0] == new CharacterSkill(0, 2), "existing skill can change inside the 255 limit");
            using (var net = owners.Acquire("net", () =>
            {
            }))
                repo.Create(net, new("测角", towns.Room, towns.InitialReturn));
            using var stop = new CancellationTokenSource();
            var (port, loop) = DevelopmentGmServer.StartEphemeral(line => DevelopmentGm.Answer(line, "net", repo, owners, _ => null), stop.Token);
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

                Check(shown && reply.Contains("测角") && reply.Contains("level=1"), "loopback show uses UTF-8");
                Check(DevelopmentGmClient.TryExecute(port, "level 测角 8", out var updated) && updated.Contains("level=8") && updated.Contains("已写入"), "loopback level write");
                Check(DevelopmentGmClient.TryExecute(port, "show Missing", out var missing) && missing.StartsWith("ERR ", StringComparison.Ordinal), "loopback missing character is an error");
                Check(Read("net", "测角").Level == 8, "loopback write is durable");
            }
            finally
            {
                stop.Cancel();
                Check(loop.Wait(TimeSpan.FromSeconds(3)), "gm listener stops");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }

        Console.WriteLine($"PASS {checks} development GM assertions");
    }
}
