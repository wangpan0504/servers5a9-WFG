using System.Text;

namespace DfoServer.Testing;

internal static class PacketCaptureTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "packet-capture"));
        await fixture.StartAsync();
        using (var peer = TcpPeer.Open(fixture))
        {
            peer.Send(3, [1]);
            peer.Expect(1, 3, [1]);
        }
        await fixture.StopAsync();
        TestCheck.That(!File.Exists(Path.Combine(fixture.Options.LogDirectory, "packet_log.txt")), "capture off creates no packet file");
        await fixture.StartAsync("enabled", packetCapture: true);
        ChannelTcpTests.Run(fixture);
        using (var peer = TcpPeer.Open(fixture))
        {
            peer.Send(3, [1]);
            peer.Expect(1, 3, [1]);
        }
        using (var earlyExit = new TcpPeer(fixture.Options.GamePort))
        {
            earlyExit.Expect(0, 1);
            earlyExit.Send(3, [1]);
            earlyExit.Closed();
        }
        await fixture.StopAsync();
        string text = File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "packet_log.txt"));
        foreach (string marker in new[] { "protocol=CHANNEL RECV", "protocol=CHANNEL SEND", "SEND kind=0x00 opcode=0x0001 name=CHANNELINFO", "RECV kind=0x01 opcode=0x061A name=LOGIN_PRECHECK", "RECV kind=0x01 opcode=0x0001 name=LOGIN", "SEND kind=0x01 opcode=0x061A", "SEND kind=0x01 opcode=0x0003 name=EXIT", "RECV kind=0x01 opcode=0x0003 name=EXIT len=14 body=(1B) [01]" })
            TestCheck.That(text.Contains(marker), "capture covers " + marker);
        TestCheck.That(text.Split("RECV kind=0x01 opcode=0x0003").Length == 3, "capture includes rejected pre-authentication traffic");
        string credential = "e10adc3949ba59abbe56e057f20f883e";
        TestCheck.That(!text.Contains(credential) && !text.Contains(BitConverter.ToString(Encoding.ASCII.GetBytes(credential)).Replace('-', ' ')), "raw and body omit credential bytes");
        TestCheck.That(text.Contains("[authentication payload redacted]"), "redaction is explicit in capture");
    }
}
