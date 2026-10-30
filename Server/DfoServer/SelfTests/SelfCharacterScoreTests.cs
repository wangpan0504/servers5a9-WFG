using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Chat;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class SelfCharacterScoreTests
{
    public static async Task RunCore()
    {
        int before = Count;
        foreach (string text in new[] { "#战力x", " #战力", "#战力 ", "#战力\n", "/战力", "L1 TEST", "L2 TEST", "L3 TEST" })
            That(!SelfCharacterScoreProjection.Matches(new(3, 0, 0, 0, text)), "只全文精确命令，不trim/前缀：" + text);
        foreach (byte kind in new byte[] { 6, 46, 11, 13, 36, 255 })
            That(!SelfCharacterScoreProjection.Matches(new(kind, 0, 0, 0, "#战力")), "非普通频道不劫持：" + kind);
        That(SelfCharacterScoreProjection.Matches(new((byte)ChatRequestType.Normal, 0, 0, 0, "#战力")), "普通频道精确命令有效");
        await Routing();
        await ConcurrentCapture();
        await RejectedQueue();
        Console.WriteLine($"SelfCharacterScoreTests: {Count - before} passed");
    }

    static async Task Routing()
    {
        string logs = Path.Combine(Path.GetTempPath(), "self-score-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var f = new CharacterScoreNoticeTests.Fixture();
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            var frames = new List<ushort>();
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, op, _, _, _) => frames.Add(op));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            int calculations = 0;
            var query = new SelfCharacterScoreProjection(snapshot => { calculations++; return f.Calculate(snapshot); }, _ => null);
            var delivery = new Delivery();
            using var log = new ServerLog(logs);
            var commands = new GameCommandRegistry();
            new ChatHandler(new ChatService(new Store(), delivery), null, log, selfScore: query).Register(commands);
            commands.Seal();
            async Task Send(byte kind, string text)
            {
                await commands.DispatchAsync(context, new(1, (ushort)CmdPacketType.SEND_MESSAGE, Body(kind, text)));
                await sender.DrainAsync();
            }
            var original = f.Session.Selected!;
            await Send(3, "#战力");
            That(calculations == 1 && frames.SequenceEqual(new ushort[] { 0x06AD }) && delivery.Count == 0,
                "真实ChatHandler仅向本人AAS_TIPS，不进入广播或其他GM路由");
            That(ReferenceEquals(original, f.Session.Selected) && f.Session.Selected.Revision == original.Revision,
                "只读评分不替换角色、不修改revision或资产");
            await Send(3, "#战力");
            That(calculations == 1 && frames.Count == 1, "5秒冷却拒绝不反复弹框");
            f.Clock.Advance(5);
            foreach (string text in new[] { "#战力x", " #战力", "#战力 ", "L1 TEST", "L2 TEST", "L3 TEST" })
            { f.Clock.Advance(1); await Send(3, text); }
            That(delivery.Count == 6 && calculations == 1, "近似命令及旧诊断词全部仍正式聊天");
            foreach (byte kind in new byte[] { 6, 11, 13, 46 })
            { f.Clock.Advance(1); await Send(kind, "#战力"); }
            That(calculations == 1, "公会/悄悄话/喇叭不触发评分，喇叭仍走实际道具资格");
            f.Clock.Advance(5);
            await Send(3, "#战力");
            That(calculations == 2 && frames.Count == 2, "冷却结束后可以重新查询");
            f.Session.ReturnToRoster();
            That(f.Session.TryBeginSelfCharacterScore().Status == "not_ready", "未选角无法查询");
            f.Session.Select(1); f.Session.MarkReady();
            That(query.Query(context) == "throttled", "切换另一角色不重置会话冷却");
            f.Clock.Advance(5);
            That(query.Query(context) == "queued", "新角色冷却结束允许查询其本人");
            await sender.DrainAsync();
            f.Clock.Advance(5);
            var failing = new SelfCharacterScoreProjection(_ => throw new InvalidDataException("synthetic"), _ => null);
            That(failing.Query(context) == "unavailable", "无法计算不能伪作零分");
            f.Clock.Advance(5);
            That(query.Query(context) == "queued", "计算异常finally释放占用，可在冷却后重试");
            await sender.DrainAsync();
        }
        finally { Directory.Delete(logs, true); }
    }

    internal static async Task ConcurrentCapture(string? nonce = null)
    {
        using var f = new CharacterScoreNoticeTests.Fixture();
        using var stop = new CancellationTokenSource();
        using var stream = new MemoryStream();
        int sent = 0;
        await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => sent++);
        var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var query = new SelfCharacterScoreProjection(snapshot =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("score test barrier");
            return f.Calculate(snapshot);
        }, _ => null);
        var task = Task.Run(() => query.Query(context, nonce));
        try
        {
            That(entered.Wait(TimeSpan.FromSeconds(5)), "已进入锁外资源计算");
            That(f.Session.TryBeginSelfCharacterScore().Status == "busy", "最多一个在算请求，拒绝不再捕获资源");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
        }
        finally { release.Set(); }
        That(await task.WaitAsync(TimeSpan.FromSeconds(5)) == "selection_changed", "计算期间同角色新epoch不发送旧结果");
        await sender.DrainAsync();
        That(sent == 0, "过期身份不向新角色显示任何摘要");
        f.Clock.Advance(5);
        var next = f.Session.TryBeginSelfCharacterScore();
        That(next.Capture is not null, "旧请求结束只释放自己的占用");
        f.Session.EndSelfCharacterScore(next.Capture!);
        f.Clock.RollBack(3); f.Clock.Advance(5);
        var newer = f.Session.TryBeginSelfCharacterScore();
        That(newer.Capture is not null, "墙钟回拨不改变已过5秒的单调冷却");
        f.Session.EndSelfCharacterScore(next.Capture!);
        That(f.Session.TryBeginSelfCharacterScore().Status == "busy", "旧capture重复结束不能清掉新在算请求");
        f.Session.EndSelfCharacterScore(newer.Capture!);
    }

    internal static async Task RejectedQueue(string? nonce = null)
    {
        using var f = new CharacterScoreNoticeTests.Fixture();
        using var stop = new CancellationTokenSource();
        using var stream = new GameSenderBudgetTests.HeldStream();
        await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
        var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
        Task held = sender.Send(0, 2, [1], "held");
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 257 && !stop.IsCancellationRequested; i++) sender.TryPost(0, 2, [1], "fill");
        var query = new SelfCharacterScoreProjection(f.Calculate, _ => null);
        That(query.Query(context, nonce) == "queue_rejected", "自助查询真实有界队列拒绝不伪作已显示");
        try { await held.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
    }

    static byte[] Body(byte kind, string text)
    {
        byte[] data = ChatText.Encoding.GetBytes(text);
        byte[] body = new byte[11 + data.Length]; body[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), (uint)data.Length); data.CopyTo(body, 11);
        return body;
    }
    sealed class Store : IChatDirectoryStore
    {
        public string? AccountOfCharacter(ushort id) => null;
        public string[] GuildMemberAccounts(long id) => [];
    }
    sealed class Delivery : IChatDelivery
    {
        public int Count;
        public int DeliverAll(Func<ChatRecipient, bool> filter, ChatNotice notice) { Count++; return 0; }
        public bool DeliverTo(string account, Func<ChatRecipient, bool> filter, ChatNotice notice) { Count++; return false; }
    }
}
