using Microsoft.Data.Sqlite;
using System.Text;
using DfoServer.Network;

namespace DfoServer.Testing;

internal static class StartupTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root)
    {
        await using var fixture = new TcpTestFixture(source with { FileLogsEnabled = true }, Path.Combine(root, "startup files"), gm: true);
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
            peer.Send(65535, [0xca, 0xfe, 0xba, 0xbe]);
            peer.Send(65534, []);
            peer.Send((ushort)CmdPacketType.GIVEUP_GAME, [0xab]);
            peer.Send((ushort)CmdPacketType.CLIENT_SPEC_STATISTIC, Encoding.ASCII.GetBytes(NativeLoginWire.Digest));
            // 后继合法命令同时证明未处理包没有伪造ACK，也没有关闭连接。
            peer.Send((ushort)CmdPacketType.EXIT, [1]);
            peer.Expect(1, 3, [1]);
        }
        await fixture.StopAsync();
        string stdout = File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "stdout.txt"));
        foreach (string stage in new[] { "Configuration", "Script.pvf", "Town resources", "Item resources", "Character resources",
            "Quest resources", "Skill resources", "Database initialization", "Database schema", "Runtime assembly", "Command registration" })
            TestCheck.That(stdout.Contains(stage), "startup stdout reports " + stage);
        TestCheck.That(stdout.Contains("Server ready") && stdout.Contains($"127.0.0.1:{fixture.Options.ChannelPort}") &&
            stdout.Contains($"127.0.0.1:{fixture.Options.GamePort}") && stdout.Contains($"127.0.0.1:{fixture.Options.GmPort}") &&
            stdout.Contains("#ch.10") && stdout.Contains("disabled"), "actual startup exposes channel, game, GM and disabled capture");
        TestCheck.That(stdout.Contains("Opcode=65535 (0xFFFF)") && stdout.Contains("Body=CAFEBABE") && stdout.Contains("Body=empty") &&
            stdout.Contains("Name=GIVEUP_GAME Length=1 Body=AB"), "stdout covers unknown, empty, and registered-but-unhandled packets");
        TestCheck.That(stdout.Split("Unhandled packet:").Length == 5 && stdout.Contains("Body=[login-redacted]") &&
            !stdout.Contains(NativeLoginWire.Digest) && !stdout.Contains(Convert.ToHexString(Encoding.ASCII.GetBytes(NativeLoginWire.Digest))),
            "unhandled events print once and authentication bytes remain redacted");
        TestCheck.That(stdout.Contains("[authenticated]") && stdout.Contains("[connected]") && stdout.Contains("[disconnected]") &&
            stdout.Contains("[stopped]") && !stdout.Contains("[send]") && !stdout.Contains("[recv]"), "stdout shows lifecycle without routine packet events");
        TestCheck.That(stdout.Contains("Loading Script.pvf...") && stdout.Contains("Game server listening on TCP") &&
            !stdout.Contains('{') && !stdout.Contains('}'), "actual startup stdout contains human-readable English and no JSON objects");
        CheckDatabase(fixture.Database);
        await fixture.StartAsync("restart", packetCapture: true);
        TestCheck.That(File.Exists(fixture.Database), "CLI-only restart reuses the existing player database");
        await fixture.StopAsync();
        TestCheck.That(File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "stdout.txt"))
            .Contains("Packet capture: " + fixture.Options.LogDirectory),
            "restart exposes the enabled capture directory");
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
        TestCheck.That(command.ExecuteScalar() is long version && version == Persistence.SqliteMigrations.CurrentVersion,
            "CLI新建和实际服务重启均保持当前结构版本");
        command.CommandText = "SELECT count(*) FROM character_dungeon_growth_receipts";
        TestCheck.That(command.ExecuteScalar() is 0L, "CLI创建与重启保留空成长账本，不凭启动发奖");
        command.CommandText = "SELECT value FROM schema_metadata WHERE key='baseline_id'";
        TestCheck.That(command.ExecuteScalar() is Persistence.SqliteMigrations.BaselineId, "CLI database has the S5A9 baseline");
        command.CommandText = "SELECT count(*) FROM accounts WHERE account_id='different-source'";
        TestCheck.That(command.ExecuteScalar() is 1L, "native credentials persist in the same game database across restart");
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader();
        TestCheck.That(!reader.Read(), "CLI-created databases preserve foreign keys");
    }
}
