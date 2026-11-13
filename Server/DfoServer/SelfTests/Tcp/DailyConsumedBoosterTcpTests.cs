using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class DailyConsumedBoosterTcpTests
{
    const int Source = 490000524, Reward = 3330, Heart = 10008893;
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        DailyConsumedBoosterTests.Run(resources.Towns, resources.ItemResources);
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-18T21:59:59Z"));
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "daily-consumed-booster"),
            gm: true, resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("DailyConsume", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, null);
            repository.Create(owner, candidate with { Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.AddRange(new InventoryItem[] {
                new(0, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(Source), 2)),
                new(0, new(InventoryListType.Main, 66), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(Heart))) }) } });
        }
        long Quantity(int id) => fixture.Query("SELECT COALESCE(SUM(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=$p0", id)
            is [[long count]] ? count : throw new InvalidOperationException("missing quantity");
        string State() => System.Text.Json.JsonSerializer.Serialize(fixture.Query("SELECT revision,daily_booster_usage FROM characters WHERE name='DailyConsume'"));
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Send(160, Build(writer => { writer.Write((short)65); writer.Write(1); }));
            BoosterTcpTests.ExpectAck(peer, Source, 65, [(Reward, 100)]);
            peer.Expect(0, 14);
            Used(peer, resources, Source, 1, false, Source);
            peer.Barrier();
            That(Quantity(Source) == 1 && Quantity(Reward) == 100,
                "普通每日盒真实TCP一次精确扣源1并完整发邀请函100");
            string saved = State();
            peer.Send(160, Build(writer => { writer.Write((short)65); writer.Write(1); }));
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(State() == saved && Quantity(Source) == 1 && Quantity(Reward) == 100,
                "当日重放没有扣源、发奖或提交revision");
            peer.Send(160, Build(writer => { writer.Write((short)66); writer.Write(1); }));
            That(peer.Expect(1, 160).Body[0] == 1, "自奖盒TCP成功ACK");
            var delta = peer.Expect(0, 14).Body;
            // 随机奖励先占原源槽时，正式投影先清除旧UID，再发布新实例；只接收这个精确清槽包。
            byte[] replaced = Build(writer =>
            {
                writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)66);
                writer.Write(-1); writer.Write(new byte[117]);
            });
            if (delta.AsSpan().SequenceEqual(replaced)) peer.Expect(0, 14);
            Used(peer, resources, Heart, 1, false, Source, Heart);
            peer.Barrier();
            That(Quantity(Heart) == 1, "实际随机奖励后自奖礼盒精确净0，不额外保留源");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(peer.DailyBoosterUsed[Source] == 1 && peer.DailyBoosterUsed[Heart] == 1,
                "普通每日盒与自奖盒重连恢复已用额度");
            peer.Barrier();
            await clock.WaitForOneShotTimerAsync(DailyBoosterUsage.NextReset(clock.GetUtcNow()));
            clock.Advance(TimeSpan.FromSeconds(1));
            Used(peer, resources, Source, 0, true, Source, Heart);
            peer.Barrier(peer.ValidateServiceDayNotice);
            That(peer.DailyBoosterUsed[Heart] == 0, "在线跨服务日同时归零两个普通每日模板");
            peer.Send(160, Build(writer => { writer.Write((short)65); writer.Write(1); }));
            BoosterTcpTests.ExpectAck(peer, Source, 65, [(Reward, 100)]);
            peer.Expect(0, 14);
            Used(peer, resources, Source, 1, false, Source, Heart);
            peer.Barrier();
            That(Quantity(Source) == 0 && Quantity(Reward) == 200,
                "次日可正常消耗最后一个源，累计完整奖励200");
        }
        await fixture.StopAsync();
    }

    static void Used(TcpPeer peer, TcpTestResources resources, int id, byte expected, bool reset, params int[] dailyKeys)
    {
        var keys = resources.ItemResources.Boosters.DailyRetainedTemplateIds.Concat(dailyKeys).ToHashSet();
        var rows = new Dictionary<int, byte>();
        int notices = 0;
        do
        {
            var packet = peer.Read();
            That(++notices <= 200, "日切及每日次数分片数量有界");
            if (reset && packet is not { Kind: 0, Opcode: 541 })
            {
                peer.ValidateServiceDayNotice(packet);
                continue;
            }
            That(packet is { Kind: 0, Opcode: 541 }, "成功ACK和差量后严格接收每日次数541");
            foreach (var row in DailyBoosterProjectionTests.Decode(packet.Body))
                rows.Add(row.Key, row.Value);
        } while (rows.Count < keys.Count);
        That(keys.SetEquals(rows.Keys) && rows[id] == expected, "每日普通盒541完整并集投影与真实账本一致");
        foreach (var row in rows) peer.DailyBoosterUsed[row.Key] = row.Value;
    }
}
