using System.Collections.Concurrent;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AdventurePublicationTests
{
    public static async Task RunCoreAsync()
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-adventure-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = AdventureCoreTests.SyntheticCatalog();
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("progress-owner", () => { });
            var point = new Location(1, 0, 10, 10);
            var towns = new TownCatalog(new(), point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            repository.Create(owner, new("ProgressHero", point, null) { Level = 95 });
            var session = new GameSession(repository, towns, owner, entry: new(new(2, 10), 0, "[normal]"));
            session.Select(0);
            var selected = session.Selected!;
            var state = AdventureState.Initial(owner.Account, DateTimeOffset.UtcNow) with
                { Level = 2, Experience = 7, CapsuleExperience = 10, Revision = 1 };
            var progress = new AdventureProgressionCommit(state, 100);
            var projection = new CharacterLevelProjection(null!, adventure: new(catalog));
            using var stop = new CancellationTokenSource();
            using var output = new HeldOutput();
            var sent = new ConcurrentQueue<(ushort Opcode, byte[] Body)>();
            await using var sender = new GameConnectionSender(owner, output, stop, 0,
                (_, opcode, _, body, _) => sent.Enqueue((opcode, body.ToArray())));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), owner, session, sender);

            await Refresh(progress);
            That(sent.IsEmpty, "未Ready的冒险团进度不向空实体发布");
            session.MarkReady();
            await Refresh(progress);
            That(sent.Count == 1 && sent.Single().Opcode == 1349 &&
                sent.Single().Body.SequenceEqual(AdventurePackets.Refresh(selected.Id, state, catalog)),
                "95级角色等级不变时主动发布已提交冒险团等级、经验、分母与胶囊，不追加Player或完整名册");
            await Refresh(null);
            await projection.RefreshAsync(context, selected, selected.Level, "stale_progress", () => false, adventureProgress: progress);
            That(sent.Count == 1, "无可见变化或原业务回执失效不追加冒险团通知");

            // 复用发送队列和文件库验证他人查询：查询者server2不能覆盖目标server7。
            using var otherOwner = owners.Acquire("region-other", () => { });
            repository.Create(otherOwner, new("RegionOther", point, null)
                { Level = 95, GrowType = 33, DungeonState = new(1, 0, 0, 197, 0) });
            var other = new GameSession(repository, towns, otherOwner, entry: new(new(7, 10), 0, "[normal]"));
            other.Select(0); other.MarkReady();
            var target = other.Selected!;
            ushort targetId = target.Id;
            var publicAdventure = other.PublicAdventure(targetId, catalog)!.Value;
            That(publicAdventure.Channel == new ChannelIdentity(7, 10) && publicAdventure.IsCurrent(),
                "他人冒险团快照从原owner捕获目标频道身份");
            var adventure = new AdventureProjection(catalog);
            await adventure.OtherInformationAsync(context, other, targetId);
            var otherInformation = sent.Single(row => row.Opcode == 1403).Body;
            That(otherInformation[0] == 1 && otherInformation[19 + 72 + 42] == 7 &&
                BitConverter.ToUInt32(otherInformation, 19 + 72 + 4) == 197,
                "他人1403收藏服务器取目标server7，独立熟练度197不冒充服务器或请求者server2");

            output.Hold = true;
            var blocked = context.Notify(NotiPacketType.USERINFO, [], "test_send_barrier");
            await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = Refresh(progress);
            var pendingOther = adventure.OtherInformationAsync(context, other, targetId);
            other.ReturnToRoster(); other.Select(0); other.MarkReady();
            That(!publicAdventure.IsCurrent(), "目标重选后旧冒险团身份与频道快照失效");
            session.ReturnToRoster();
            session.Select(0);
            session.MarkReady();
            output.Release.TrySetResult();
            await blocked.WaitAsync(TimeSpan.FromSeconds(5));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await pendingOther.WaitAsync(TimeSpan.FromSeconds(5));
            That(sent.Count(row => row.Opcode == 1349) == 1,
                "原进度排队后同角色重选推进epoch，迟到1349不覆盖新角色上下文");
            That(sent.Count(row => row.Opcode == 1403) == 1,
                "目标重选或查询者epoch变化后，排队的旧他人冒险团信息不发布");

            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => new AdventureProjection(catalog)
                .ProgressAsync(context, session.Selected!, progress, () => true).GetAwaiter().GetResult(),
                "接管后旧owner不能发布冒险团进度");
            That(sent.Count(row => row.Opcode == 1349) == 1, "旧owner拒绝不增加实际冒险团写帧");

            Task Refresh(AdventureProgressionCommit? value) => projection.RefreshAsync(context, selected, selected.Level,
                "adventure_growth", () => true, adventureProgress: value);
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - checks} 冒险团提交后主动刷新及旧owner/epoch隔离断言（非实机）");
    }

    sealed class HeldOutput : MemoryStream
    {
        public bool Hold { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!Hold) return;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}
