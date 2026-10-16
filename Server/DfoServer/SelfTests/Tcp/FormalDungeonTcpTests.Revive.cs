using System.Diagnostics;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunReviveAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        // 服务端定时器按 1/10 缩短，墙钟读数保持真实；原先实际等待死亡期限约 17.5 秒。
        var timers = new ScaledTimers(ReviveTimeScale);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "dungeon-revive"), resources: resources, clock: timers);
        await fixture.StartAsync();
        using var peer = Open(fixture);
        peer.Create("DeathShopTcp");
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var repository = new CharacterRepository(fixture.Database);
            var character = repository.LoadCharacter(owner, 0);
            InventoryService.Commit(repository, owner, character,
                new CurrencyService(resources.ItemResources.GoldLimits).Grant(character, CurrencyKind.Cera, 100));
        }
        byte[] purchase = Convert.FromHexString("0000010000FF00A48601000000000000000000");
        byte[] key = [(byte)peer.Id, (byte)(peer.Id >> 8)];
        string Assets() => JsonSerializer.Serialize(fixture.Query("SELECT revision,revive_coins FROM characters WHERE id=$p0", peer.Id)) +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY currency_kind");
        peer.Select(); EntryTown(peer); Enter(peer);
        string before = Assets();
        peer.Send(64, purchase); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
        That(Assets() == before, "现场商城仍拒绝加载中购买，不扣点券");
        Load(peer);
        var watch = Stopwatch.StartNew();
        peer.Send(40, [0x74, 0, 4, 1, 0, 0]);
        peer.Expect(0, 32, [key[0], key[1], 0, 0]);
        That(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", peer.Id) is [[0L]], "死亡窗口购买前角色确实零复活币");
        peer.Send(64, purchase);
        byte[] ack = new byte[24]; ack[0] = 1; ack.AsSpan(2, 4).Fill(0xFF);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(6), 100004);
        peer.Expect(1, 64, ack);
        peer.Expect(0, 14, CoinBalance(1));
        peer.Expect(0, 53, [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        That(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", peer.Id) is [[1L]] &&
            fixture.Query("SELECT COALESCE((SELECT balance FROM account_currency_balances WHERE currency_kind=15),0)") is [[0L]],
            "实收CMD64购买提交扣100点券发一币，ACK64→余额14→点券53完整匹配");
        peer.Send(41, key); peer.Expect(1, 41, [1, key[0], key[1], 0]);
        peer.Expect(0, 32, [key[0], key[1], 1, 0]); peer.Expect(0, 14, CoinBalance(0));
        peer.Barrier();
        await Task.Delay(Scaled(TimeSpan.FromSeconds(11)) - watch.Elapsed);
        peer.Barrier();
        That(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", peer.Id) is [[0L]],
            "购买后正常消费一币复活，超过死亡期限仍在原副本且没有旧24回城通知");
        watch.Restart();
        peer.Send(40, [0x74, 0, 4, 1, 0, 0]); peer.Expect(0, 32, [key[0], key[1], 0, 0]);
        before = Assets();
        peer.Send(64, purchase); peer.Expect(1, 64, [0, 0, 0]);
        peer.Send(41, key); peer.Expect(1, 41, [0, 1, 0]); peer.Barrier();
        That(Assets() == before, "无点券购买和零币复活失败均不改资产、revision或死亡计时");
        // 等待回城事件本身（读超时 5 秒），不再固定休眠到期限之后。
        peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0, 24); RefreshedQuests(peer); peer.Barrier();
        That(DfoServer.Network.Projection.DungeonDeathProjection.ReturnDelay == TimeSpan.FromSeconds(10) &&
            watch.Elapsed >= Scaled(TimeSpan.FromSeconds(9.5)) && watch.Elapsed < TimeSpan.FromSeconds(9.5) && Assets() == before,
            "死亡期限常量为10秒并按缩放计时恢复本人城镇state0，再发布24及任务刷新，无ACK40/42或虚构扣币");
        peer.Send(41, key); peer.Barrier();
        That(Assets() == before, "自动回城后旧复活请求静默且不能再扣币");
        Console.WriteLine("PASS 零币死亡现场购买、实际复活取消计时及10秒（缩放计时）自动回城TCP（非Windows实机）");
    }

    const int ReviveTimeScale = 10;
    static TimeSpan Scaled(TimeSpan value) => value / ReviveTimeScale;

    // 只缩短服务端定时器（Task.Delay/CreateTimer），GetUtcNow 仍为真实时间，不影响期限与日期判断。
    sealed class ScaledTimers(int factor) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, Scale(dueTime), Scale(period));

        TimeSpan Scale(TimeSpan value) => value == Timeout.InfiniteTimeSpan || value <= TimeSpan.Zero ? value : value / factor;
    }

    static byte[] CoinBalance(int remaining)
    {
        byte[] balance = new byte[126];
        balance[1] = 1; balance[3] = 1; balance[5] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(balance.AsSpan(9), remaining);
        return balance;
    }
}
