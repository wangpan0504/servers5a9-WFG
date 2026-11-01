using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 合成资源、真实SQLite与owner接管，确定性截住生产Task.Yield；不声称TCP连接全流程。
internal static class LukePhaseExitOwnerTests
{
    internal static async Task RunCoreAsync()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-phase-exit-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool passed = false;
        try
        {
            foreach (bool takeover in new[] { false, true })
            {
                using var stop = new CancellationTokenSource();
                var owners = new AccountOwners();
                using var owner = owners.Acquire("phase-exit", () => stop.Cancel());
                AccountOwners.Lease? replacement = null;
                var point = new Location(1, 0, 10, 10);
                var map = new TownMap("synthetic/exit.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
                var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                    point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
                var repository = new CharacterRepository(Path.Combine(root, takeover ? "takeover.db" : "current.db"));
                repository.Create(owner, new("ExitOwner", point, point) { Level = 90, CompletedQuests = [2903, 4872] });
                var session = new GameSession(repository, towns, owner, entry: new(new(2, 20), 32, "[raid]"));
                session.Select(0); session.MarkReady();
                var actor = session.CaptureRaidActor(new RaidRegistry())!;
                That(actor is { IsCurrent: true }, "原角色经真实选角就绪取得actor");
                var clock = new LukeRaidClockTests.Clock();
                var battle = new AntonRaidBattle(AntonRaidBattleTests.Script(10, 20), kind: RaidKind.Luke,
                    clock: clock, phaseTimeout: new("fixture/phase-exit-seconds", [1, 1]));
                var policies = Enumerable.Range(0, 2).Select(phase => new RaidPhaseRewardPolicy("fixture/exit",
                    [new(0, 0, [new(100, 1)], [])], new(0, (byte)phase, 1, 0, 0, 0))).ToImmutableArray();
                That(session.AttachRaidCombat(actor!, battle, policies), "隔离绑定真实Battle，不伪造过期字段");
                await using var clocks = new LukeRaidClocks(clock, repository, (_, error) => throw error);
                using var timer = clocks.Reserve(battle)!;
                timer.Activate(); clock.Advance(1);
                That(battle.View.PhaseExpired && ReferenceEquals(session.CurrentRaidBattle(actor!), battle), "实际阶段时钟生成原实例到期view");
                string stored = JsonSerializer.Serialize(repository.LoadCharacter(owner, 0));
                using var stream = new MemoryStream();
                await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, _) => { });
                var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
                using var log = new ServerLog(Path.Combine(root, takeover ? "takeover-log" : "current-log"));
                var town = new TownProjection(towns, new CharacterStatComputer(new CharacterCatalog([])), null,
                    new EpicPieceCatalog("[piece list]\n9001 9002\n[/piece list]"));
                var projection = new LukePhaseExitProjection(context, new PartyDungeonService(new PartyService(repository), repository), town, log, repository);
                try
                {
                    var held = new HeldPostContext();
                    var previous = SynchronizationContext.Current;
                    try { SynchronizationContext.SetSynchronizationContext(held); projection.Notice(battle.View, actor!); }
                    finally { SynchronizationContext.SetSynchronizationContext(previous); }
                    That(held.Count == 1 && !projection.Completion.IsCompleted, "确定截住生产Yield而非sleep竞争");
                    if (takeover)
                    {
                        That(owners.TryAcquire(owner.Account, () => { }, DuplicateLoginPolicy.ReplaceExisting, out replacement, out _), "合法账号接管");
                        That(!owner.IsCurrent && replacement!.IsCurrent && stop.IsCancellationRequested, "真实旧连接回调取消且新owner成立");
                    }
                    held.Release();
                    await projection.Completion.WaitAsync(TimeSpan.FromSeconds(3));
                    await projection.DisposeAsync();
                    That(stream.Length == 0 && JsonSerializer.Serialize(repository.LoadCharacter(replacement ?? owner, 0)) == stored,
                        "到期候战无Run或接管取消不发包、不修改角色资产");
                    That((replacement ?? owner).IsCurrent, "退出投影不能释放当前owner");
                }
                finally
                {
                    try { await projection.DisposeAsync(); }
                    finally { try { session.End(); } finally { replacement?.Dispose(); } }
                }
            }
            passed = true;
        }
        finally { if (passed) TestFiles.DeleteDirectory(root, true); else Console.WriteLine("FAIL fixture: " + root); }
        Console.WriteLine($"PASS {Count - before} 阶段退场owner接管与取消断言");
    }

    sealed class HeldPostContext : SynchronizationContext
    {
        readonly Queue<(SendOrPostCallback Callback, object? State)> callbacks = new();
        internal int Count => callbacks.Count;
        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Enqueue((callback, state));
        internal void Release() { var item = callbacks.Dequeue(); item.Callback(item.State); }
    }
}
