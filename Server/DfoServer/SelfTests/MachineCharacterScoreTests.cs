using System.Buffers.Binary;
using System.Net;
using System.Text;
using DfoServer.Game.Chat;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MachineCharacterScoreTests
{
    const string Nonce = "0123456789abcdef0123456789abcdef";
    public static async Task RunCore()
    {
        int before = Count;
        string Read(byte[] body)
        {
            int title = BinaryPrimitives.ReadInt32LittleEndian(body);
            That(Encoding.ASCII.GetString(body, 4, title) == "CP_SCORE/1" && body[^1] == 0, "机器标题/type固定");
            int length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4 + title));
            That(body.Length == 9 + title + length, "机器帧无尾数据");
            return Encoding.ASCII.GetString(body, 8 + title, length);
        }
        That(Read(MachineCharacterScorePackets.Build(Nonce, 42, 7, "character-investment", "v2.1", "partial", 1234, true, 10000)) ==
            "1|0123456789abcdef0123456789abcdef|42|7|character-investment|v2.1|partial|1234|1|10000", "双端相同黄金正文");
        That(Read(MachineCharacterScorePackets.Build(Nonce, 42, long.MaxValue, "character-investment", "v2.1", "unrated", null, false, 1)).EndsWith("|unrated|-|0|1", StringComparison.Ordinal), "无分值不能变0");
        foreach (ushort id in new ushort[] { 0, ushort.MaxValue })
            That(Read(MachineCharacterScorePackets.Build(Nonce, id, 0, "p", "v", "complete", int.MaxValue, false, 1)).Contains($"|{id}|0|p|v|complete|2147483647|0|1", StringComparison.Ordinal), "u16边界不作为身份授权哨兵");
        void Reject(Action action) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } That(rejected, "无效机器字段拒绝而不截断"); }
        foreach (string nonce in new[] { "", Nonce.ToUpperInvariant(), Nonce + "0", Nonce[..31], "中" + Nonce[1..] })
            Reject(() => MachineCharacterScorePackets.Build(nonce, 42, 0, "p", "v", "complete", 0, false, 1));
        foreach (string token in new[] { "", "中文", "a|b", "a b", "a\0b", new string('a', 49) })
            Reject(() => MachineCharacterScorePackets.Build(Nonce, 42, 0, token, "v", "complete", 0, false, 1));
        // 面板接受带修订的规则标签，并保留两个 token 的独立长度上限。
        foreach (string version in new[] { "", "中文", "a|b", new string('a', 33) })
            Reject(() => MachineCharacterScorePackets.Build(Nonce, 0, 0, "p", version, "complete", 0, false, 1));
        string maximum = Read(MachineCharacterScorePackets.Build(Nonce, ushort.MaxValue, long.MaxValue,
            new string('p', 48), new string('v', 32), "complete", int.MaxValue, true, 10000));
        That(maximum.Split('|').Length == 10 && Encoding.ASCII.GetByteCount(maximum) <= 600, "最大合法字段仍在客户端十段/600字节边界内");
        That(Read(MachineCharacterScorePackets.Build(Nonce, 0, 0, "character-investment", "v2.3", "complete", 0, false, 1)) ==
            "1|0123456789abcdef0123456789abcdef|0|0|character-investment|v2.3|complete|0|0|1", "当前规则版本及合法零分的黄金正文");
        Reject(() => MachineCharacterScorePackets.Build(Nonce, 0, 0, "p", "v", "complete", -1, false, 1));
        Reject(() => MachineCharacterScorePackets.Build(Nonce, 0, 0, "p", "v", "unknown", 0, false, 1));
        foreach (int ttl in new[] { 0, -1, 10001 }) Reject(() => MachineCharacterScorePackets.Build(Nonce, 42, 0, "p", "v", "complete", 0, false, ttl));
        Reject(() => MachineCharacterScorePackets.Build(Nonce, 42, -1, "p", "v", "complete", 0, false, 1));
        Reject(() => MachineCharacterScorePackets.Build(Nonce, 42, 0, "p", "v", "complete", null, false, 1));
        Reject(() => MachineCharacterScorePackets.Build(Nonce, 42, 0, "p", "v", "unrated", 0, false, 1));
        Reject(() => MachineCharacterScorePackets.Build(Nonce, 42, 0, "p", "v", "unrated", null, true, 1));
        foreach (string text in new[] { "#cp-score/1", "#cp-score/1 bad", "#cp-score/1  " + Nonce, "#cp-score/1\t" + Nonce, "#cp-score/1 " + Nonce + " " })
            That(SelfCharacterScoreProjection.MachineNamespace(new(3, 0, 0, 0, text)) && SelfCharacterScoreProjection.MachineNonce(new(3, 0, 0, 0, text)) is null, "命名空间坏语法拒绝");
        foreach (string text in new[] { "#cp-score/1x", " #cp-score/1 " + Nonce, "#CP-score/1 " + Nonce })
            That(!SelfCharacterScoreProjection.MachineNamespace(new(3, 0, 0, 0, text)), "相似普通消息不劫持");
        foreach (byte kind in new byte[] { 6, 11, 13, 36, 46 }) That(!SelfCharacterScoreProjection.MachineNamespace(new(kind, 0, 0, 0, "#cp-score/1 " + Nonce)), "其他频道保持原合同");
        await Routing();
        await SelfCharacterScoreTests.ConcurrentCapture(Nonce);
        await SelfCharacterScoreTests.RejectedQueue(Nonce);
        Console.WriteLine($"MachineCharacterScoreTests: {Count - before} passed");
    }

    static async Task Routing()
    {
        string logs = Path.Combine(Path.GetTempPath(), "machine-score-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var f = new CharacterScoreNoticeTests.Fixture();
            using var stop = new CancellationTokenSource(); using var stream = new MemoryStream();
            int frames = 0, calculations = 0;
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, op, _, _, _) => { That(op == 1709, "机器仅AAS_TIPS"); frames++; });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            var query = new SelfCharacterScoreProjection(snapshot => { calculations++; return f.Calculate(snapshot); }, _ => null);
            var delivery = new Delivery(); using var log = new ServerLog(logs);
            var commands = new GameCommandRegistry(); new ChatHandler(new ChatService(new Store(), delivery), null, log, selfScore: query).Register(commands); commands.Seal();
            async Task Send(string text) { f.Clock.Advance(1); await commands.DispatchAsync(context, new(1, (ushort)CmdPacketType.SEND_MESSAGE, Body(text))); await sender.DrainAsync(); }
            await Send("#cp-score/1 bad"); That(frames == 0 && calculations == 0 && delivery.Count == 0, "坏nonce不广播不计算");
            await Send("#cp-score/1 " + Nonce); That(frames == 1 && calculations == 1 && delivery.Count == 0, "真实Handler机器只查本人");
            await Send("#战力"); That(frames == 1 && calculations == 1, "人与机器共用5秒限频");
            await Send("#cp-score/1x"); That(delivery.Count == 1, "相似前缀正常广播");
            f.Clock.Advance(5);
            var expired = new SelfCharacterScoreProjection(snapshot => { f.Clock.Advance(10); return f.Calculate(snapshot); }, _ => null);
            That(expired.Query(context, Nonce) == "expired", "计算耗尽TTL不抬为1毫秒");
            f.Clock.Advance(5);
            var capture = f.Session.TryBeginSelfCharacterScore().Capture!;
            long deadline = capture.Snapshot.Equipment.ObservedAt + 10;
            f.Clock.Advance(3);
            That(f.Session.CharacterScoreRemainingMilliseconds(capture, deadline) == 7000, "构造TTL扣掉计算延迟；客户端从发起时计时更保守");
            f.Clock.Advance(7);
            That(f.Session.CharacterScoreNoticeStatus(capture, deadline) == "expired" && f.Session.CharacterScoreRemainingMilliseconds(capture, deadline) == 0, "排队不延长服务器期限");
            f.Session.EndSelfCharacterScore(capture);
        }
        finally { Directory.Delete(logs, true); }
    }
    static byte[] Body(string text) { byte[] data = ChatText.Encoding.GetBytes(text); byte[] body = new byte[11 + data.Length]; body[0] = 3; BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), (uint)data.Length); data.CopyTo(body, 11); return body; }
    sealed class Store : IChatDirectoryStore { public string? AccountOfCharacter(ushort id) => null; public string[] GuildMemberAccounts(long id) => []; }
    sealed class Delivery : IChatDelivery { public int Count; public int DeliverAll(Func<ChatRecipient, bool> filter, ChatNotice notice) { Count++; return 0; } public bool DeliverTo(string account, Func<ChatRecipient, bool> filter, ChatNotice notice) { Count++; return false; } }
}
