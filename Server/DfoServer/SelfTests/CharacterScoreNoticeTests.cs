using System.Buffers.Binary;
using System.Text;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CharacterScoreNoticeTests
{
    public static async Task RunCore()
    {
        int before = Count;
        using (var f = new Fixture())
        {
            Func<bool>? guard = null;
            byte[]? sent = null;
            var target = new ChatProbeConnection(1, f.Session, (_, _, _) => throw new Exception("unguarded path"))
            { GuardedProbe = (opcode, body, action, current) =>
                { That(opcode == 0x06AD && action == "character_score_notice", "使用既有AAS_TIPS而非PVP字段"); guard = current; sent = body; return true; } };
            var result = DevelopmentGm.PrepareCharacterScoreNotice(f.Request, target, f.Calculate, _ => null);
            That(result.Status == "queued" && !result.DeliveryConfirmed && result.ScoreStatus == "Partial" && result.ScoreCapped,
                "管理提示只确认入队，先标Partial及封顶不宣称显示");
            That(result.ExpiresAt == f.Clock.Utc + 10 && guard?.Invoke() == true, "正常身份最长10秒可展示");
            string body = Text(sent!);
            That(body.StartsWith("不完整", StringComparison.Ordinal) && body.Contains("含封顶项") && body.Contains(CharacterInvestmentScore.Profile),
                "重要状态与版本排在摘要前方");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(guard!() == false, "同一角色重新进入也因新selection epoch拒绝旧提示");
            bool denied = false;
            try { DevelopmentGm.PrepareCharacterScoreNotice(DevelopmentRequest.Parse("character-score-notice NoticeHero", "other"), target, f.Calculate, _ => null); }
            catch (InvalidDataException) { denied = true; }
            That(denied, "不同账号不能向同名在线目标显示资产评分");
            denied = false;
            try { DevelopmentGm.PrepareCharacterScoreNotice(DevelopmentRequest.Parse("character-score-notice OtherHero", "notice"), target, f.Calculate, _ => null); }
            catch (InvalidDataException) { denied = true; }
            That(denied, "必须目标当前已选角色，不能借离线JSON目标");
            var unavailable = target with { GuardedProbe = null };
            denied = false;
            try { DevelopmentGm.PrepareCharacterScoreNotice(f.Request, unavailable, f.Calculate, _ => null); }
            catch (InvalidDataException) { denied = true; }
            That(denied, "未装配守卫回调不能退回无身份Probe");
            var rejection = DevelopmentGm.PrepareCharacterScoreNotice(f.Request, target with { GuardedProbe = (_, _, _, _) => false }, f.Calculate, _ => null);
            That(rejection.Status == "queue_rejected" && !rejection.DeliveryConfirmed, "入队失败不伪作成功");
            var expiry = DevelopmentGm.PrepareCharacterScoreNotice(f.Request, target, snapshot =>
                { var score = f.Calculate(snapshot); f.Clock.Advance(10); return score; }, _ => null);
            That(expiry.Status == "expired", "资源计算超过TTL不入队");
        }
        using (var f = new Fixture(expiring: true))
        {
            var target = new ChatProbeConnection(1, f.Session, (_, _, _) => { }) { GuardedProbe = (_, _, _, _) => true };
            var details = DevelopmentGm.PrepareCharacterScoreNotice(f.Request, target, f.Calculate, _ => null);
            That(details.ExpiresAt == f.Clock.Utc + 4, "取宠物detail4秒而非时装detail6秒或核心0永久");
            var box = DevelopmentGm.PrepareCharacterScoreNotice(f.Request, target, f.Calculate, _ => f.Clock.Utc + 3);
            That(box.ExpiresAt == f.Clock.Utc + 3, "选中箱更早3秒期限也约束提示");
        }
        using (var f = new Fixture())
        {
            var score = f.Calculate(CharacterInvestmentScore.Capture(f.Session.Selected!, f.Clock.Utc));
            byte[] packet = DfoServer.Network.Builders.CharacterScorePackets.Body(string.Concat(Enumerable.Repeat("角😀色", 300)), score, out bool truncated);
            int titleBytes = BinaryPrimitives.ReadInt32LittleEndian(packet);
            int length = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4 + titleBytes));
            That(truncated && titleBytes <= 24 && length <= 600 && packet[^1] == 0, "UTF8摘要遵守标题24/正文600字节合同");
            _ = new UTF8Encoding(false, true).GetString(packet, 8 + titleBytes, length);
            That(Text(packet).StartsWith("不完整", StringComparison.Ordinal), "UTF8截断不拆码点且保留状态前缀");
            var capture = f.Session.CaptureCharacterScoreNotice("notice", "NoticeHero");
            using var replacement = f.Owners.Acquire("notice", () => { });
            That(f.Session.CharacterScoreNoticeStatus(capture, f.Clock.Utc + 10) == "stale_generation", "账号接管拒绝原generation而非抛给新会话");
        }
        foreach (string change in new[] { "none", "same-role", "other-role", "revision", "end", "ttl", "rollback", "item-expiry", "box-expiry", "item-rollback", "box-rollback" })
            await Queued(change);
        await QueueFailure();
        Console.WriteLine($"CharacterScoreNoticeTests: {Count - before} passed");
    }

    static async Task Queued(string change)
    {
        using var f = new Fixture(expiring: change is "item-expiry" or "item-rollback");
        using var stop = new CancellationTokenSource();
        using var stream = new GameSenderBudgetTests.HeldStream();
        await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
        Task held = sender.Send(0, 2, [1], "held");
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var target = new ChatProbeConnection(1, f.Session, (_, _, _) => { })
        { GuardedProbe = (opcode, body, action, current) => sender.TryPost(0, opcode, body, action, () => !stop.IsCancellationRequested && current()) };
        var result = DevelopmentGm.PrepareCharacterScoreNotice(f.Request, target, f.Calculate,
            _ => change is "box-expiry" or "box-rollback" ? f.Clock.Utc + 3 : null);
        That(result.Status == "queued" && stream.Frames.Count == 0, "慢队列入队不等待网络，尚未显示：" + change);
        switch (change)
        {
            case "same-role": f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady(); break;
            case "other-role": f.Session.ReturnToRoster(); f.Session.Select(1); f.Session.MarkReady(); break;
            case "revision":
                f.Session.CommitDevelopmentInventory("NoticeHero", false, c => new(true, 0,
                    new(c.Inventory, c.Inventory with { Gold = c.Inventory.Gold + 1 }, "notice-test")), "rejected"); break;
            case "end": f.Session.End(); break;
            case "ttl": f.Clock.Advance(10); break;
            case "rollback": f.Clock.Advance(8); f.Clock.RollBack(7); f.Clock.Advance(2); break;
            case "item-expiry": f.Clock.Advance(4); break;
            case "box-expiry": f.Clock.Advance(3); break;
            case "item-rollback": f.Clock.Advance(3); f.Clock.RollBack(2); f.Clock.Advance(1); break;
            case "box-rollback": f.Clock.Advance(2); f.Clock.RollBack(1); f.Clock.Advance(1); break;
        }
        stream.Release.TrySetResult(); await held;
        await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        That(stream.Frames.Count == (change == "none" ? 2 : 1) && sender.PendingBytes == 0,
            "写前复核丢弃旧提示且释放预算：" + change);
    }

    static async Task QueueFailure()
    {
        using var f = new Fixture();
        using var stop = new CancellationTokenSource();
        using var stream = new GameSenderBudgetTests.HeldStream();
        await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
        Task held = sender.Send(0, 2, [1], "held");
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool rejected = false;
        for (int i = 0; i < 257; i++)
            if (!sender.TryPost(0, 2, [1], "fill")) { rejected = true; break; }
        var target = new ChatProbeConnection(1, f.Session, (_, _, _) => { })
        { GuardedProbe = (op, body, action, guard) => sender.TryPost(0, op, body, action, guard) };
        var result = DevelopmentGm.PrepareCharacterScoreNotice(f.Request, target, f.Calculate, _ => null);
        That(rejected && stop.IsCancellationRequested && result.Status == "queue_rejected", "真实有界队列满拒绝，保留原断连策略");
        try { await held.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
    }

    static string Text(byte[] body)
    {
        int title = BinaryPrimitives.ReadInt32LittleEndian(body);
        int length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4 + title));
        return new UTF8Encoding(false, true).GetString(body, 8 + title, length);
    }

    internal sealed class Clock : TimeProvider
    {
        long utc = 2_000_000_000, ticks;
        public long Utc => Interlocked.Read(ref utc);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Utc);
        public void Advance(int seconds) { Interlocked.Add(ref utc, seconds); Interlocked.Add(ref ticks, seconds * TimeSpan.TicksPerSecond); }
        public void RollBack(int seconds) => Interlocked.Add(ref utc, -seconds);
    }

    internal sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "score-notice-" + Guid.NewGuid().ToString("N"));
        public Clock Clock { get; } = new();
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public GameSession Session { get; }
        public DevelopmentRequest Request => DevelopmentRequest.Parse("character-score-notice NoticeHero", "notice");
        readonly Dictionary<int, ItemTemplate> templates = [];
        public Fixture(bool expiring = false)
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("notice.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) }, point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            Owner = Owners.Acquire("notice", () => { });
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var items = new List<InventoryItem>
            {
                new(0, new(InventoryListType.Equipment, 12), new(1, ItemKind.Equipment, 1) { Attr = 31 }),
                new(0, new(InventoryListType.Equipment, 13), new(2, ItemKind.Equipment, 1))
            };
            templates[1] = new(1, ItemKind.Equipment, 12, 95, 30, 1, 0, 4, 0, "[free]", ushort.MaxValue, 0, "weapon");
            templates[2] = templates[1] with { Id = 2, EquipmentSlot = 13, Rarity = 99 };
            if (expiring)
            {
                items.Add(new(0, new(InventoryListType.Equipment, 0), new(3, ItemKind.Avatar, 0)) { Avatar = new() { ExpireTime = checked((int)Clock.Utc + 6) } });
                items.Add(new(0, new(InventoryListType.Equipment, 26), new(4, ItemKind.Creature, 0)) { Creature = new() { ExpireTime = checked((int)Clock.Utc + 4) } });
                templates[3] = templates[1] with { Id = 3, Kind = ItemKind.Avatar, EquipmentSlot = 0, Grade = 1 };
                templates[4] = templates[1] with { Id = 4, Kind = ItemKind.Creature, EquipmentSlot = 26 };
            }
            repository.Create(Owner, new("NoticeHero", point, point) { Level = 95, Inventory = new([.. items]) });
            repository.Create(Owner, new("OtherHero", point, point) { Level = 95 });
            Session = new(repository, towns, Owner, timeProvider: Clock);
            Session.Select(0); Session.MarkReady();
        }
        public CharacterInvestmentResult Calculate(CharacterInvestmentSnapshot snapshot) => CharacterInvestmentScore.Calculate(snapshot,
            id => templates[id], (_, _, _, _) => true, _ => true, _ => null, _ => null, _ => null,
            _ => throw new InvalidDataException("empty box"), (_, _) => false);
        public void Dispose() { Session.End(); Owner.Dispose(); Directory.Delete(root, true); }
    }
}
