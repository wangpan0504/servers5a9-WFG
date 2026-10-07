using Microsoft.Data.Sqlite;

namespace DfoServer.Testing;

internal static class StartupTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "startup files"));
        await fixture.StartAsync();
        TestCheck.That(File.Exists(fixture.Database) && Directory.GetFiles(fixture.Options.DataDirectory, "*.db").Length == 1,
            "CLI startup creates one database for accounts and game data");
        TestCheck.That(File.Exists(Path.Combine(fixture.Root, "Data", "Pvf", "Script.pvf")), "PVF is consumed from the fixed Data/Pvf directory");
        using (var peer = new TcpPeer(fixture.Options.GamePort, "127.0.0.2"))
        {
            peer.Expect(0, 1);
            peer.Send(1562, NativeLoginWire.Precheck("different-source"));
            peer.Expect(1, 1562, [1]);
            peer.Send(1, NativeLoginWire.Login("different-source", NativeLoginWire.Digest));
            peer.Expect(1, 1);
            peer.Expect(0, 2);
        }
        await fixture.StopAsync();
        CheckDatabase(fixture.Database);
        await fixture.StartAsync("restart", packetCapture: true);
        TestCheck.That(File.Exists(fixture.Database), "CLI-only restart reuses the existing player database");
        await fixture.StopAsync();
        CheckDatabase(fixture.Database);
    }

    static void CheckDatabase(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        TestCheck.That((string?)command.ExecuteScalar() == "ok", "CLI startup and restart preserve SQLite integrity");
        command.CommandText = "PRAGMA user_version";
        TestCheck.That(command.ExecuteScalar() is 1L, "CLI database starts at release schema v1");
        command.CommandText = "SELECT value FROM schema_metadata WHERE key='baseline_id'";
        TestCheck.That(command.ExecuteScalar() is Persistence.SqliteMigrations.BaselineId, "CLI database has the S5A9 baseline");
        command.CommandText = "SELECT count(*) FROM accounts WHERE account_id='different-source'";
        TestCheck.That(command.ExecuteScalar() is 1L, "native credentials persist in the same game database across restart");
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader();
        TestCheck.That(!reader.Read(), "CLI-created databases preserve foreign keys");
    }
}
