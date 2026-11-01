using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using DfoServer.Development;
using DfoServer.Persistence;
using DfoServer.GameWorld.Items;

namespace DfoServer;

internal static class CharacterBoundaryTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters)
    {
        int checks = Count;

        void Reject(Action action, string message)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or SqliteException)
            {
                rejected = true;
            }

            That(rejected, message);
        }

        string root = Path.Combine(Path.GetTempPath(), "S5A9-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("creator", () =>
            {
            });
            var session = new GameSession(repository, towns, owner);
            That(!session.NameExists("Created"), "name check before creation");
            var created = session.CreateCharacter("Created", characters.Get(15));
            That(session.NameExists("Created") && created.Job == 15 && created.Skills.Length > 0, "session creation uses matching profile");
            session.Select(created.Slot);
            Reject(() => session.NameExists("Created"), "name check after selection");
            Reject(() => session.CreateCharacter("Extra", characters.Get(0)), "create after selection");
            That(repository.List(owner).Length == 1, "rejected creation leaves no row");
            session.ReturnToRoster();
            That(session.CreateCharacter("Second", characters.Get(0)).Slot == 1, "creation resumes after returning to roster");
            using var replacement = owners.Acquire("creator", () =>
            {
            });
            Reject(() => session.NameExists("Created"), "stale name check");
            Reject(() => session.CreateCharacter("Stale", characters.Get(0)), "stale creation");
            string import = Path.Combine(root, "legacy.txt");
            using var importer = owners.Acquire("importer", () =>
            {
            });
            File.WriteAllLines(import, ["matched-client\tordinary-job0\t1", "100\t0\tImportOne", "101\t1\tCreated"]);
            Reject(() => LegacyCharacterImport.Run(repository, importer, towns, import), "duplicate second name rolls back entire import");
            That(repository.List(importer).Length == 0, "failed batch import is atomic");
            File.WriteAllLines(import, ["matched-client\tordinary-job0\t1", "100\t0\tImportOne", "101\t1\tImportTwo"]);
            That(LegacyCharacterImport.Run(repository, importer, towns, import) == 2, "legacy import succeeds");
            var imported = new CharacterRepository(path).List(importer);
            That(imported.Select(role => role.Id).SequenceEqual(new ushort[] { 100, 101 }) && imported.All(role => role.Position == towns.Room && role.Return == towns.InitialReturn), "legacy identity and spawn survive reopen");
            Reject(() => LegacyCharacterImport.Run(repository, importer, towns, import), "nonempty account rejects import");
            using var empty = owners.Acquire("empty", () =>
            {
            });
            File.WriteAllLines(import, ["matched-client\tordinary-job0\t1", "110\t1\tGap"]);
            Reject(() => LegacyCharacterImport.Run(repository, empty, towns, import), "noncontiguous legacy slots rejected");
            That(repository.List(empty).Length == 0, "invalid legacy file writes nothing");
            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                int newerVersion = SqliteMigrations.CurrentVersion + 1;
                command.CommandText = $"PRAGMA user_version={newerVersion}";
                command.ExecuteNonQuery();
                Reject(() => new CharacterRepository(path), "newer schema rejected");
                command.CommandText = "SELECT count(*) FROM characters";
                That((long)command.ExecuteScalar()! == 4, "schema rejection preserves existing rows");
                command.CommandText = "PRAGMA user_version";
                That((long)command.ExecuteScalar()! == newerVersion, "schema rejection does not downgrade version");
            }
        }
        finally
        {
            TestFiles.DeleteDirectory(root, true);
        }

        Console.WriteLine($"PASS {Count - checks} character boundary assertions");
    }
}
