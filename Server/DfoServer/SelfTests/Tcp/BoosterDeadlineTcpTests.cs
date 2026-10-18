using System.Diagnostics;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class BoosterDeadlineTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        foreach (bool waitForWriter in new[] { false, true })
        {
            var clock = new CommitClock(DateTimeOffset.Parse("2026-10-18T21:59:59Z"));
            await using var fixture = new TcpTestFixture(options, Path.Combine(root, "booster-deadline-" + waitForWriter),
                gm: true, resources: resources, clock: clock);
            var repository = new CharacterRepository(fixture.Database);
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var role = CharacterCreation.Prepare("Deadline", resources.Characters.Get(0), resources.Towns.Room,
                    resources.Towns.InitialReturn, null);
                repository.Create(owner, role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Add(new(0,
                    new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(490704515), utcNow: clock.GetUtcNow()))) } });
            }
            await fixture.StartAsync();
            BoosterTcpTests.SetExpirationDebug(fixture, true);
            using var peer = Open(fixture);
            peer.Select(); peer.Barrier();
            string State() => JsonSerializer.Serialize(fixture.Query("SELECT revision,daily_booster_usage FROM characters")) +
                JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
                JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_slots ORDER BY item_uid"));
            string before = State();
            using var db = new SqliteConnection("Data Source=" + fixture.Database);
            db.Open();
            using var writer = waitForWriter ? db.BeginTransaction(deferred: false) : null;
            clock.Arm(block: !waitForWriter);
            peer.Send(160, Build(w => { w.Write((short)65); w.Write(1); }));
            await clock.Observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // writer仍持有唯一写锁，原请求不可能越过BeginTransaction完成提交。
            if (waitForWriter) await Task.Delay(100);
            clock.Advance(TimeSpan.FromSeconds(1));
            clock.Release();
            writer?.Commit();
            peer.Expect(1, 160, [0, 19, 0]);
            ExpectUsed(peer, resources, 0);
            peer.Barrier();
            That(State() == before, "同一真实请求跨截止失败ACK19且源、奖励、账本与revision完整不变，writer等待=" + waitForWriter);
            That(clock.RepositoryReads >= (waitForWriter ? 2 : 1), "期限复验实际位于原请求提交边界");
            peer.Send(160, Build(w => { w.Write((short)65); w.Write(1); }));
            BoosterTcpTests.ExpectAck(peer, 490704515, 65, [(490704522, 10)]);
            peer.Expect(0, 14);
            ExpectUsed(peer, resources, 1);
            peer.Barrier();
            That(fixture.Query("SELECT SUM(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=490704522") is [[10L]],
                "失败后同连接Barrier及合法新日开箱成功，旧计划没有重抽或补发");
        }
    }

    static void ExpectUsed(TcpPeer peer, TcpTestResources resources, byte used)
    {
        var rows = new Dictionary<int, byte>();
        do
        {
            foreach (var row in DailyBoosterProjectionTests.Decode(peer.Expect(0, 541).Body)) rows.Add(row.Key, row.Value);
        } while (rows.Count < resources.ItemResources.Boosters.DailyRetainedTemplateIds.Count);
        That(rows[490704515] == used, "跨截止仍严格接收完整541次数视图");
    }

    // 仅在真实Repository读取提交时钟时发信号，后台541时钟不会误触发测试门闩。
    sealed class CommitClock(DateTimeOffset origin) : TimeProvider
    {
        readonly DailyBoosterTestClock inner = new(origin);
        readonly ManualResetEventSlim release = new(false);
        int armed;
        bool block;
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RepositoryReads { get; private set; }
        public void Arm(bool block) { this.block = block; Volatile.Write(ref armed, 1); }
        public void Release() => release.Set();
        public void Advance(TimeSpan amount) => inner.Advance(amount);
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override long GetTimestamp() => inner.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);
        public override DateTimeOffset GetUtcNow()
        {
            if (Volatile.Read(ref armed) != 0 && new StackTrace().GetFrames().Any(frame =>
                frame.GetMethod()?.DeclaringType == typeof(CharacterRepository) ||
                frame.GetMethod()?.DeclaringType?.DeclaringType == typeof(CharacterRepository)))
            {
                RepositoryReads++;
                if (RepositoryReads == 1)
                {
                    var observedNow = inner.GetUtcNow();
                    Observed.TrySetResult();
                    if (block && !release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("提交测试门闩超时");
                    if (!block) return observedNow;
                }
            }
            return inner.GetUtcNow();
        }
    }
}
