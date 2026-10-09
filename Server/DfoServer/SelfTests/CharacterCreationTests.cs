using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using System.Buffers.Binary;
using DfoServer.Persistence;
using DfoServer.GameWorld.Items;

namespace DfoServer;

internal static class CharacterCreationTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters)
    {
        var computer = new CharacterStatComputer(characters);
        int checks = Count;

        void Reject(Action action, string name)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or SqliteException)
            {
                rejected = true;
            }

            That(rejected, name);
        }

        byte[] sample = Convert.FromHexString("0F0B0000007169616E677A68616E7368000000000000FF0000000000");
        var request = CharacterRequests.ParseCreate(sample);
        That(request.Job == 15 && request.Name == "qiangzhansh" && request.Tail.SequenceEqual(Convert.FromHexString("000000000000FF0000000000")), "live gunblader body retains job, CP936 name and raw tail");
        byte[] unobserved = sample.ToArray();
        unobserved[^4] = 1;
        Reject(() => CharacterRequests.ParseCreate(unobserved), "job15 tail[8]=1 is not an observed variant");
        Reject(() => characters.Get(16), "unknown job has no profile");
        var sword = CharacterStatsPackets.Build(characters.Get(0).InitialStats);
        That(BinaryPrimitives.ReadUInt32LittleEndian(sword) == 1800 && BinaryPrimitives.ReadUInt32LittleEndian(sword.AsSpan(4)) == 1400 && BinaryPrimitives.ReadInt16LittleEndian(sword.AsSpan(20)) == 200 && BinaryPrimitives.ReadInt16LittleEndian(sword.AsSpan(22)) == -200 && BinaryPrimitives.ReadUInt32LittleEndian(sword.AsSpan(60)) == 480000 && characters.Get(0).Skills.SequenceEqual(new CharacterSkill[] { new(5, 1), new(46, 1), new(169, 1), new(174, 1), new(179, 7), new(511, 1) }), "swordman stats and six CHR starter skills");
        CharacterSkill[] gunSkills = [new(1, 1), new(50, 1), new(169, 1), new(174, 1), new(179, 7), new(511, 1)];
        That(characters.Get(15).Skills.SequenceEqual(gunSkills), "matching gunblader CHR six starter skills");
        int[] skillCounts = [6, 6, 6, 6, 6, 6, 6, 6, 8, 21, 8, 7, 6, 6, 6, 6];
        string folder = Path.Combine(Path.GetTempPath(), "ServerS5A9-create-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("create-test", () =>
            {
            });
            for (byte job = 0; job < 16; job++)
            {
                byte[] name = CharacterName.Encoding.GetBytes("Job" + job);
                byte[] body = new byte[17 + name.Length];
                body[0] = job;
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), name.Length);
                name.CopyTo(body, 5);
                body[11 + name.Length] = 255;
                var parsed = CharacterRequests.ParseCreate(body);
                var profile = characters.Get(parsed.Job);
                var created = new GameSession(repo, towns, owner).CreateCharacter(parsed.Name, profile);
                That(created.Slot == job && created.Job == job && created.Level == 1 && created.GrowType == 0 && created.AcceptedStoryLevel == 1 && created.CompletedQuests.Length == 0 && created.ActiveMainQuests.Length == 0 && created.Revision == 0 && created.Skills.Length == skillCounts[job], "ordinary job " + job + " persisted with its own profile");
                byte[] select = TownPackets.Select(created), actor = TownPackets.Actor(created), player = TownPackets.Player(created, computer.Compute(created));
                That(select.Length == 235 && select[222] == 38 && BinaryPrimitives.ReadUInt16LittleEndian(select.AsSpan(22)) == ushort.MaxValue && BinaryPrimitives.ReadUInt32LittleEndian(select.AsSpan(24)) == 0 && actor[127 + name.Length] == job && player.Length == 622 + 6 * skillCounts[job] && player.AsSpan(36, 89).SequenceEqual(CharacterStatsPackets.Build(profile.InitialStats)), "job " + job + " selects initial town 38 and retains actor identity, quest and equipment");
            }

            var reopened = new CharacterRepository(path);
            var rows = reopened.List(owner);
            That(rows.Length == 16 && rows.Select(c => (int)c.Job).SequenceEqual(Enumerable.Range(0, 16)) && rows.All(c => c.AcceptedStoryLevel == 1 && c.CompletedQuests.Length == 0 && c.ActiveMainQuests.Length == 0) , "all job identities and distinct first quest acceptances survive database reopen");
            byte[] roster = CharacterPackets.Roster(rows.Select(CharacterRosterEntry.FromCharacter).ToArray());
            int at = 15;
            foreach (var row in rows)
            {
                int width = BinaryPrimitives.ReadInt32LittleEndian(roster.AsSpan(at + 2));
                That(roster[at + 8 + width] == row.Job, "roster keeps job " + row.Job);
                at += 195 + width;
            }

            var gun = rows[15];
            var relocated = gun with { Position = new(39, 0, 596, 144), Return = towns.InitialReturn };
            That(TownPackets.Select(relocated)[222] == 39 && gun.Position == towns.Room && gun.Job == 15, "select town follows the snapshot rather than job, return town or a fixed initial town");
            That(gun.Skills.SequenceEqual(gunSkills), "gunblader skills survive database reopen");
            var stats = CharacterStatsPackets.Build(characters.Get(15).InitialStats);
            That(BinaryPrimitives.ReadUInt32LittleEndian(stats) == 1700 && BinaryPrimitives.ReadUInt32LittleEndian(stats.AsSpan(4)) == 1500 && BinaryPrimitives.ReadInt16LittleEndian(stats.AsSpan(8)) == 70 && BinaryPrimitives.ReadInt16LittleEndian(stats.AsSpan(12)) == 50 && BinaryPrimitives.ReadUInt32LittleEndian(stats.AsSpan(60)) == 450000 && BinaryPrimitives.ReadUInt16LittleEndian(stats.AsSpan(72)) == 9000 && BinaryPrimitives.ReadUInt16LittleEndian(stats.AsSpan(74)) == 8000, "gunblader HP/MP, attributes, inventory and speeds use its CHR");
            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TRIGGER reject_initial_skill BEFORE INSERT ON character_skills BEGIN SELECT RAISE(ABORT,'initial skill failure');END";
                cmd.ExecuteNonQuery();
                Reject(() => new GameSession(repo, towns, owner).CreateCharacter("RollbackGun", characters.Get(15)), "initial skill failure rejects ordinary creation");
                cmd.CommandText = "SELECT count(*) FROM characters";
                That((long)cmd.ExecuteScalar()! == 16, "failed starter skill insert rolls back the character too");
                cmd.CommandText = "DROP TRIGGER reject_initial_skill";
                cmd.ExecuteNonQuery();
            }

            Reject(() => new GameSession(repo, towns, owner).CreateCharacter("Job15", characters.Get(15)), "duplicate ordinary name rejected");
            var saved = repo.Save(owner, gun, gun.Position with { X = 460 }, gun.Return);
            Reject(() => repo.Save(owner, gun, gun.Position, gun.Return), "stale ordinary revision rejected");
            That(saved.Job == 15 && repo.List(owner)[15].AcceptedStoryLevel == 1 && repo.List(owner)[15].Skills.SequenceEqual(gunSkills) && saved.ActiveMainQuests.SequenceEqual(gun.ActiveMainQuests), "position save preserves ordinary job, story level, skills and first main quest");
            var session = new GameSession(repo, towns, owner);
            session.Select(15);
            session.MarkReady();
            That(session.Selected!.Job == 15 && session.Selected.Skills.SequenceEqual(gunSkills) && session.Selected.ActiveMainQuests.SequenceEqual(gun.ActiveMainQuests), "reselect retains gunblader identity, skills and first quest acceptance");
            bool displaced = false;
            using var stale = owners.Acquire("stale-create", () => displaced = true);
            using var fresh = owners.Acquire("stale-create", () =>
            {
            });
            Reject(() => new GameSession(repo, towns, stale).CreateCharacter("StaleGun", characters.Get(15)), "old generation cannot create an ordinary character");
            stale.Dispose();
            That(displaced && repo.List(fresh).Length == 0, "old dispose cannot release the new owner or leave a character");
            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "PRAGMA user_version";
                That((long)cmd.ExecuteScalar()! == SqliteMigrations.CurrentVersion, "ordinary jobs persist initial equipment and account assets and learning through current schema");
                cmd.CommandText = "PRAGMA integrity_check";
                That((string?)cmd.ExecuteScalar() == "ok", "ordinary creation database integrity");
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }

        Console.WriteLine($"PASS {Count - checks} ordinary character creation assertions; jobs=16");
    }

}
