using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Session;
using DfoServer.Game.Settings;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    // 持住真实命令门禁，让日切/命令收尾先于活动worker观察开关，确定性覆盖108/1103竞态。
    static async Task VerifyEndlessCatalogOrderingAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-11-03T07:00:00+08:00"));
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "catalog-order"), resources: resources, clock: clock);
        var policy = new EndlessAdventurePolicy(resources.ItemResources.Items.Get(EndlessAdventurePolicy.CoinId), new GameDebugSettings(), clock);
        var repository = new CharacterRepository(fixture.Database) { EndlessAdventure = policy };
        var events = new ServerEventService(new ServerEventRepository(fixture.Database), clock);
        events.Override(2257, false);
        using var stop = new CancellationTokenSource();
        using var owner = new AccountOwners().Acquire("endless-catalog", () => stop.Cancel());
        repository.Create(owner, CharacterCreation.Prepare("EndlessOrder", resources.Characters.Get(9),
            resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
        var session = new GameSession(repository, resources.Towns, owner, timeProvider: clock, serverEvents: events, endlessAdventure: policy);
        session.Select(0); session.MarkReady();
        fixture.Sql("INSERT INTO account_endless_adventure_days(account,day,earned) VALUES($p0,$p1,199)", owner.Account, EndlessAdventurePolicy.Day(clock.GetUtcNow()));
        using var log = new ServerLog(fixture.Options.LogDirectory);
        var town = new TownProjection(resources.Towns, new CharacterStatComputer(resources.Characters), null,
            resources.ItemResources.EpicPieces, clock: clock, events: events);
        using var stream = new MemoryStream();
        var delivered = new List<(ushort Opcode, byte[] Body)>();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, opcode, _, body, _) =>
        {
            lock (delivered) delivered.Add((opcode, body.ToArray()));
        });
        var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
        await context.CommandGate.WaitAsync();
        await using var projection = new ServerEventProjection(context, events, town, log, clock);
        context.ServerEvents = projection;
        try
        {
            await town.ServerEventsAsync(context);
            await sender.DrainAsync();
            lock (delivered) delivered.Clear();
            foreach (bool active in new[] { true, false })
            {
                events.Override(2257, active);
                await EndlessAdventureProjection.PublishAsync(context);
                await sender.DrainAsync();
                lock (delivered)
                    That(delivered.All(row => row.Opcode != 1103), "开关变化后普通刷新等待匹配108，不抢先发布1103");
                That(session.NeedsEndlessAdventureRefresh, "目录尚未更新时不把活动进度记成已发布");
                await town.ServerEventsAsync(context);
                await sender.DrainAsync();
                lock (delivered)
                {
                    var relevant = delivered.Where(row => row.Opcode is 108 or 1103).ToArray();
                    That(relevant.Select(row => row.Opcode).SequenceEqual(new ushort[] { 108, 1103 }),
                        "真实活动切换先完整108，再且仅一次1103，保持开启与关闭顺序");
                    That(relevant[1].Body.Length == 152 && BinaryPrimitives.ReadUInt32LittleEndian(relevant[1].Body.AsSpan(4)) == (active ? 199u : 0u),
                        "目录发布后沿原账号日账本显示199或关闭零值");
                    delivered.Clear();
                }
                That(!session.NeedsEndlessAdventureRefresh, "实际进度发布后才确认对应开关快照");
            }
        }
        finally
        {
            stop.Cancel(); context.CommandGate.Release(); context.ServerEvents = null; session.End();
        }
    }
}
