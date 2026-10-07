using Microsoft.Data.Sqlite;
namespace DfoServer.Persistence;

internal sealed class SqliteGameDatabase
{
    readonly string connectionString;
    public SqliteGameDatabase(string path)
    {
        connectionString = SqliteDatabaseBootstrap.Initialize(path);
    }

    public SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        try
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000;PRAGMA foreign_keys=ON";
            command.ExecuteNonQuery();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }
}
