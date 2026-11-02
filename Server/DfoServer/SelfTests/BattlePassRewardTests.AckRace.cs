using System.Buffers.Binary;
using System.Net;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BattlePassRewardTests
{
    internal static Task RunRewardAckAsync() => AckRaceCoreAsync(mission: false);
    internal static Task RunMissionAckAsync() => AckRaceCoreAsync(mission: true);

    static async Task AckRaceCoreAsync(bool mission)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-ack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string change in new[] { "unchanged", "reselect", "takeover", "recharge" })
                await Scenario(change);
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }

        async Task Scenario(string change)
        {
            const string season = "ack-race-season";
            var fixture = new Fixture();
            var repository = new CharacterRepository(Path.Combine(root, (mission ? "mission-" : "reward-") + change + ".db"));
            var (policy, now) = BattlePassMissionTests.AckFixturePolicy(season);
            if (mission) repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("pass-ack", () => { });
            var point = new Location(1, 0, 10, 10);
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), point, point,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            repository.Create(owner, new NewCharacter("AckOne", point, null) { Level = 95 });
            repository.Create(owner, new NewCharacter("AckTwo", point, null) { Level = 95 });
            var session = new GameSession(repository, towns, owner, timeProvider: new AckClock(now));
            session.Select(0);
            session.MarkReady();
            var selected = session.Selected!;
            long epoch = session.SelectionEpoch;
            var state = repository.ReadBattlePass(owner, season);
            That(state.TotalExperience == 0 && state.Claims.IsEmpty, "初始赛季没有四级资格或领取记录");

            using var log = new ServerLog(root);
            var commands = new GameCommandRegistry();
            // 失败请求不会调用库存投影。
            new BattlePassHandler(fixture.Service, season, null!, log).Register(commands);
            new BattlePassMissionHandler(new AckClock(now), (_, _, _) => Task.CompletedTask, log).Register(commands);
            commands.Seal();
            using var stop = new CancellationTokenSource();
            using var output = new AckHeldOutput();
            var sent = new List<(ushort Opcode, byte[] Body, string Action)>();
            await using var sender = new GameConnectionSender(owner, output, stop, 0,
                (_, opcode, _, body, action) => sent.Add((opcode, body.ToArray(), action)));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), owner, session, sender);
            Task held = sender.Send(1, 0, [], "held");
            await output.Started.WaitAsync(TimeSpan.FromSeconds(5));
            long heldBytes = sender.PendingBytes;
            byte[] request = new byte[mission ? 24 : 19];
            if (mission)
            {
                request[13] = 1;
                BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(14), 12);
            }
            else BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(15), 4);
            var opcode = mission ? CmdPacketType.BATTLE_PASS_REQUEST_MISSION : CmdPacketType.BATTLE_PASS_REQUEST_REWARD;
            Task<CommandResult> dispatch = commands.DispatchAsync(context,
                new(1, (ushort)opcode, request));
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (sender.PendingBytes <= heldBytes && !dispatch.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Yield();
            That(sender.PendingBytes > heldBytes && !dispatch.IsCompleted, "战令失败ACK已排在被阻塞的输出帧后");

            AccountOwners.Lease? replacement = null;
            try
            {
                if (change == "recharge")
                {
                    string answer = DevelopmentGm.Answer("recharge cera 100", owner.Account, repository, owners, _ => session);
                    That(answer.StartsWith("OK ", StringComparison.Ordinal) && session.Selected is { } credited &&
                        !ReferenceEquals(credited, selected) && credited.Id == selected.Id && session.SelectionEpoch == epoch &&
                        credited.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 100 &&
                        repository.LoadCharacter(owner, 0).Inventory.AccountWallet.Get(CurrencyKind.Cera) == 100,
                        "真实在线GM充值提交并替换同角色快照，选角纪元不变");
                }
                else if (change == "reselect")
                {
                    session.ReturnToRoster();
                    session.Select(1);
                    session.MarkReady();
                    That(session.SelectionEpoch != epoch && session.Selected!.Id != selected.Id, "换角改变角色及选角纪元");
                }
                else if (change == "takeover")
                {
                    replacement = owners.Acquire(owner.Account, () => { });
                    That(!owner.IsCurrent && replacement.IsCurrent, "接管使旧owner失效");
                }
            }
            finally { output.Release(); }

            try
            {
                await Observe(held);
                await Observe(dispatch);
            }
            finally { replacement?.Dispose(); }

            async Task Observe(Task task)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) when (change == "takeover" &&
                    error is InvalidOperationException or IOException or OperationCanceledException) { }
            }

            var acks = sent.Where(frame => frame.Opcode == (ushort)opcode).ToArray();
            if (change is "unchanged" or "recharge")
            {
                var persisted = repository.ReadBattlePass(owner, season);
                That(persisted.Claims.IsEmpty && persisted.Revision == state.Revision &&
                    persisted.TotalExperience == state.TotalExperience &&
                    (!mission || repository.ReadBattlePassMissions(owner, now) is { } missions &&
                        missions.Slots.All(slot => !slot.Completed)) &&
                    repository.LoadCharacter(owner, 0).Inventory.Items.SequenceEqual(selected.Inventory.Items),
                    "失败请求不改变领取记录、经验或奖励物品");
                That(acks.Length == 1 && acks[0].Body.SequenceEqual(new byte[] { 0, 19, 0 }) &&
                    acks[0].Action == (mission ? "battle_pass_mission_rejected" : "battle_pass_reward_rejected"),
                    "同角色同纪元在" + change + "后收到" + (mission ? "任务" : "奖励") + "失败ACK");
            }
            else
                That(acks.Length == 0, "换角或接管后不发送旧失败ACK：" + change);
        }
    }

    sealed class AckClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    sealed class AckHeldOutput : MemoryStream
    {
        readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => started.Task;
        public void Release() => release.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}

internal static partial class BattlePassMissionTests
{
    internal static (BattlePassMissionPolicy Policy, DateTimeOffset Now) AckFixturePolicy(string season) =>
        (new(season, Start, Catalog()), Start.AddDays(58).AddHours(1));
}
