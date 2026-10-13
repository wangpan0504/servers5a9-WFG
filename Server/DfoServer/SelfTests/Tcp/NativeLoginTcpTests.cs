using System.Text;
using DfoServer.Persistence;
using DfoServer.Game.Auth;

namespace DfoServer.Testing;

internal static class NativeLoginTcpTests
{
    static TcpPeer Begin(TcpTestFixture fixture, string account)
    {
        var peer = new TcpPeer(fixture.Options.GamePort);
        try
        {
            var hello = peer.Expect(0, 1).Body;
            TestCheck.That(hello.Length >= 357, "native channel initialization contains complete codec key, shared identity, clock and packet counts");
            TestCheck.Bytes(hello.AsSpan(4, 5), [0x96, 0xd2, 0xbe, 0xed, 0xde], "first NOTI uses its dedicated wire encoding");
            // 独立复现客户端 0369FA30 解码，再检查 protobuf，避免只验证服务端内部明文。
            for (int i = 0; i < hello.Length; i++)
                hello[i] = (byte)((((hello[i] << 6) | (hello[i] >> 2)) ^ 0xb5) & 0xff);
            TestCheck.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hello) == hello.Length - 4,
                "protobuf长度前缀覆盖时钟及所有原字段");
            TestCheck.Bytes(hello.AsSpan(4, 5), [0x10, 1, 0x1a, 0xce, 2], "protobuf result and bytes field framing");
            TestCheck.Bytes(hello.AsSpan(343, 4), [0x38, 2, 0x40, 10], "主游戏端口仍安装默认入口身份2/10");
            using (var reader = new BinaryReader(new MemoryStream(hello, 347, hello.Length - 347)))
            {
                TestCheck.That(reader.ReadByte() == 88 && Math.Abs(reader.Read7BitEncodedInt64() - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) <= 5,
                    "原生握手field11为Unix秒，完整关闭/重开不沿用旧服务器时钟");
                TestCheck.Bytes(reader.ReadBytes(10), [0xa0, 0xd3, 0x0e, 0xd4, 0x0d, 0xa8, 0xd3, 0x0e, 0x94, 0x0e], "protobuf fields 30004=1748 CMD and 30005=1812 NOTI");
                TestCheck.That(reader.BaseStream.Position == reader.BaseStream.Length, "完整消费新增时钟及两项枚举数量");
            }
            peer.Send(1562, NativeLoginWire.Precheck(account));
            peer.Expect(1, 1562, [1]);
            return peer;
        }
        catch
        {
            peer.Dispose();
            throw;
        }
    }

    static (TcpPeer Peer, byte[] Roster) Open(TcpTestFixture fixture, string account)
    {
        var peer = Begin(fixture, account);
        try
        {
            peer.Send(1, NativeLoginWire.Login(account));
            peer.Expect(1, 1);
            return (peer, peer.Expect(0, 2).Body);
        }
        catch
        {
            peer.Dispose();
            throw;
        }
    }

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "native-login"), resources: resources);
        TestCheck.That(fixture.Options.DuplicateLoginPolicy == Game.Accounts.DuplicateLoginPolicy.ReplaceExisting,
            "native TCP server uses fixed replacement policy");
        await fixture.StartAsync();
        byte[] roster;
        using (var first = Open(fixture, "alice").Peer)
        {
            roster = first.Create("凭据测试");
            first.Select();
            first.Barrier();
            using (var wrong = Begin(fixture, "alice"))
            {
                wrong.Send(1, NativeLoginWire.Login("alice", NativeLoginWire.WrongDigest));
                wrong.Closed();
            }
            first.Barrier(); // 错误凭据不得接管当前 owner。
            var other = Open(fixture, "bob");
            using (other.Peer)
                TestCheck.That(!other.Roster.AsSpan().SequenceEqual(roster), "native accounts own separate rosters");
            var replacement = Open(fixture, "ALICE");
            using (replacement.Peer)
            {
                TestCheck.Bytes(replacement.Roster, roster, "same account reconnect retains roster");
                first.Closed();
                replacement.Peer.Select();
                replacement.Peer.Barrier();
                replacement.Peer.Send(1, NativeLoginWire.Login("ALICE"));
                replacement.Peer.Closed();
            }
        }
        using (var mismatch = Begin(fixture, "new-account"))
        {
            mismatch.Send(1, NativeLoginWire.Login("different-account"));
            mismatch.Closed();
        }
        var repository = new AuthenticationService(new AuthenticationRepository(Path.Combine(fixture.Options.DataDirectory, "game.db")));
        repository.Create("different-account", NativeLoginWire.Digest); // 前后账号不一致不得创建。
        using (var truncated = Begin(fixture, "incomplete"))
        {
            truncated.Send(1, NativeLoginWire.Login("incomplete")[..^1]);
            truncated.Closed();
        }
        repository.Create("incomplete", NativeLoginWire.Digest);
        repository.Disable("alice");
        using (var disabled = Begin(fixture, "alice"))
        {
            disabled.Send(1, NativeLoginWire.Login("alice"));
            disabled.Closed();
        }
        repository.Enable("alice");
        await fixture.StopAsync();
        await fixture.StartAsync("restarted");
        var resumed = Open(fixture, "alice");
        using (resumed.Peer)
        {
            TestCheck.Bytes(resumed.Roster, roster, "native account and characters survive service restart");
            resumed.Peer.Select();
            resumed.Peer.Barrier();
        }
        await fixture.StopAsync();
        string[] logFiles = Directory.GetFiles(fixture.Root, "server.jsonl", SearchOption.AllDirectories);
        TestCheck.That(logFiles.Length == 2, "native log redaction checked before and after restart");
        foreach (string path in logFiles)
        {
            string log = File.ReadAllText(path);
            foreach (string credential in new[] { NativeLoginWire.Digest, NativeLoginWire.WrongDigest })
                TestCheck.That(!log.Contains(credential, StringComparison.OrdinalIgnoreCase) &&
                    !log.Contains(Convert.ToHexString(Encoding.ASCII.GetBytes(credential)), StringComparison.OrdinalIgnoreCase), "native credential absent from logs");
        }
    }
}
