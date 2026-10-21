using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using DfoServer.Persistence;
using DfoServer.Game.Characters;
using System.Text.Json;
using PvfLib;

namespace DfoServer.Development;

internal static class OfflineCharacterPreparation
{
    public static void Run(ServerOptions options, PvfArchive pvf, TownCatalog towns, string name, string auditDirectory)
    {
        if (!CharacterName.IsValid(name))
            throw new InvalidDataException("Invalid prepared character name");
        auditDirectory = Path.GetFullPath(auditDirectory);
        if (Directory.Exists(auditDirectory) || File.Exists(auditDirectory))
            throw new IOException("Fresh audit directory required");
        var preparation = CharacterPreparation.Load(pvf);
        string database = Path.Combine(options.DataDirectory, "game.db");
        if (!File.Exists(database))
            throw new FileNotFoundException("Initialize the account database before preparing a role", database);
        Directory.CreateDirectory(auditDirectory);
        string backup = Path.Combine(auditDirectory, "game-before.db");
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString()))
        {
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
            using var check = destination.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            if ((string?)check.ExecuteScalar() != "ok")
                throw new InvalidDataException("Backup integrity failed");
        }

        Save("intent.json", new
        {
            Database = database,
            Backup = backup,
            Account = options.DevelopmentAccount,
            Name = name,
            Job = 0,
            GrowType = 0x25,
            Level = 95,
            AcceptedStoryLevel = 95,
            Preparation = preparation
        });
        var repository = new CharacterRepository(database);
        using var owner = new AccountOwners().Acquire(options.DevelopmentAccount, () =>
        {
        });
        Character created = repository.Create(owner, preparation.ToCharacter(name, towns));
        Character persisted = repository.List(owner).Single(c => c.Id == created.Id);
        if (persisted.Name != name || persisted.Job != 0 || persisted.GrowType != 0x25 ||
            persisted.Level != 95 || persisted.AcceptedStoryLevel != 95 ||
            persisted.DungeonState != CharacterDungeonState.NewOrdinary || persisted.Fatigue is null ||
            !persisted.Learning.Initialized || persisted.Learning.Experience != preparation.InitialLearning.Experience ||
            persisted.Learning.BonusSp != preparation.InitialLearning.BonusSp || persisted.Learning.BonusTp != preparation.InitialLearning.BonusTp ||
            persisted.Learning.Trees.Where((tree, index) => tree.Sp != preparation.InitialLearning.Trees[index].Sp ||
                tree.Tp != preparation.InitialLearning.Trees[index].Tp ||
                !tree.Skills.SequenceEqual(preparation.InitialLearning.Trees[index].Skills)).Any() ||
            !persisted.CompletedQuests.SequenceEqual(preparation.CompletedQuests) ||
            !persisted.Skills.SequenceEqual(preparation.Skills))
            throw new InvalidDataException("Committed character readback differs");
        Save("committed.json", new { Character = persisted, CommitUtc = DateTime.UtcNow });
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            persisted.Id,
            persisted.Slot,
            persisted.Name,
            persisted.Job,
            persisted.GrowType,
            persisted.Level,
            persisted.AcceptedStoryLevel,
            CompletedQuests = persisted.CompletedQuests.Length,
            Skills = persisted.Skills.Length,
            Backup = backup
        }));
        void Save(string file, object value) => File.WriteAllText(Path.Combine(auditDirectory, file), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
