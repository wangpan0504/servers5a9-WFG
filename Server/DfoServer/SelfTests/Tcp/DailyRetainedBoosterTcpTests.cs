using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class DailyRetainedBoosterTcpTests
{
    const int Source = 490704515, Reward = 490704522;
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        BoosterQualificationTests.Run(resources.Towns, resources.ItemResources);
        ConsumableUseSessionTests.RunQualificationExpiry(resources.Towns, resources.Characters, resources.Archive);
        await BoosterDeadlineTcpTests.RunAsync(options, root, resources);
        DailyRetainedBoosterTests.Run(resources.Towns, resources.ItemResources);
        await DailyBoosterProjectionTests.RunResourcesAsync(resources);
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-18T21:59:59Z"));
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "daily-retained-booster"),
            gm: true, resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        var template = resources.ItemResources.Items.Get(Source);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("DailyBox", resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, null);
            var role = repository.Create(owner, candidate with { Inventory = candidate.Inventory with
            { Items = candidate.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(template))) } });
            var inventory = role.Inventory with { NpcFavor = [new(17, 30000)
                { GiftDay = ShopPurchaseCount.Today(clock.GetUtcNow()), GiftCount = 5, Colors = 47 }] };
            InventoryService.Commit(repository, owner, role, new(true, 0, new(role.Inventory, inventory, "test_favor_daily_seed")));
            repository.Create(owner, candidate with { Name = "DailyPeer" });
        }
        await fixture.StartAsync();
        BoosterTcpTests.SetExpirationDebug(fixture, true);
        string SourceState() => System.Text.Json.JsonSerializer.Serialize(fixture.Query(
            "SELECT i.item_uid,s.list_type,s.slot_index,i.item_core FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) WHERE json_extract(i.item_core,'$.ItemId')=$p0", Source));
        string sourceBefore = SourceState();
        using (var peer = Open(fixture))
        {
            peer.Select();
            Bytes(peer.NpcFavor, [1, 17, 0, 0, 0, 48, 117, 0, 0, 5, 4, 0, 47, 1, 0, 2, 0, 3, 0],
                "登录以服务时钟恢复赠礼次数，不混用机器当前日期");
            That(peer.DailyBoosterUsed.TryGetValue(Source, out byte initial) && initial == 0 &&
                resources.ItemResources.Boosters.DailyRetainedTemplateIds.All(peer.DailyBoosterUsed.ContainsKey),
                "首次选角对Catalog所有支持模板显式恢复零次数");
            OpenBox(peer);
            BoosterTcpTests.ExpectAck(peer, Source, 65, [(Reward, 10)]);
            var delta = peer.Expect(0, 14).Body;
            That(Enumerable.Range(0, U16(delta, 1)).All(index => U16(delta, 3 + index * 123) != 65), "保留源没有删除或扣减差量");
            ExpectUsed(peer, resources, 1);
            peer.Barrier();
            That(SourceState() == sourceBefore, "源UID、槽位、完整Core与数量原样保留");
            OpenBox(peer);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(fixture.Query("SELECT SUM(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=$p0", Reward) is [[10L]],
                "同服务日重复请求没有重复发奖");
        }
        await clock.WaitForTimersAsync(0);
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(peer.DailyBoosterUsed[Source] == 1, "重连从真实账本恢复已用次数");
            peer.Barrier();
            await clock.WaitForTimersAsync(2);
            clock.Advance(TimeSpan.FromSeconds(1));
            // 没有发送请求，必须由连接独立时钟下发跨日显式zero。
            var notices = new List<WirePacket>();
            while (!notices.Any(row => row.Opcode == 541) || !notices.Any(row => row.Opcode == 746))
            {
                That(notices.Count < 200, "无请求的日切通知数量有界");
                notices.Add(peer.Read());
            }
            await clock.WaitForTimersAsync(2);
            notices.AddRange(PartyDungeonTcpTests.Drain(peer));
            var usedRows = notices.Where(row => row is { Kind: 0, Opcode: 541 })
                .SelectMany(row => DailyBoosterProjectionTests.Decode(row.Body)).ToDictionary(row => row.Key, row => row.Value);
            That(usedRows.Count == resources.ItemResources.Boosters.DailyRetainedTemplateIds.Count && usedRows[Source] == 0 &&
                resources.ItemResources.Boosters.DailyRetainedTemplateIds.All(usedRows.ContainsKey), "无请求时收到完整的礼盒零次数视图");
            var favor = notices.Single(row => row is { Kind: 0, Opcode: 746 });
            Bytes(favor.Body, [1, 17, 0, 0, 0, 48, 117, 0, 0, 0, 4, 0, 47, 1, 0, 2, 0, 3, 0], "NPC好感在线日切保留颜色并清零视图次数");
            That(notices.Any(row => row is { Kind: 0, Opcode: 550, Body.Length: 6 }) &&
                notices.Any(row => row is { Kind: 0, Opcode: 1536, Body.Length: 5 }) &&
                notices.Where(row => row.Opcode == 550).All(row => row.Body.Length == 6 && U32(row.Body) > 0) &&
                notices.All(row => row.Kind == 0 && row.Opcode is 21 or 356 or 541 or 550 or 574 or 746 or 1343 or 1349 or 1536),
                "礼盒、好感、副本和强袭在线日切通知均按已登记格式接收");
            That(fixture.Query("SELECT json_extract(npc_favor,'$[0].GiftCount') FROM characters WHERE name='DailyBox'") is [[long count]] && count == 5,
                "跨日只从持久日戳投影零次数，保留好感与颜色，不额外写库");
            OpenBox(peer);
            BoosterTcpTests.ExpectAck(peer, Source, 65, [(Reward, 10)]);
            peer.Expect(0, 14);
            ExpectUsed(peer, resources, 1);
            peer.Barrier();
            That(SourceState() == sourceBefore, "跨日再次成功仍不消费源");
            peer.Send(7);
            peer.Expect(1, 7, [1]);
            peer.Expect(0, 2);
            peer.Select(1);
            That(peer.DailyBoosterUsed[Source] == 0, "同连接换角显式覆盖上一角色已用值");
            peer.Barrier();
            await clock.WaitForTimersAsync(2);
            using var replacement = Open(fixture);
            peer.Closed();
            replacement.Select();
            That(replacement.DailyBoosterUsed[Source] == 1, "接管连接从当前角色账本恢复，不被旧计时器归零");
            replacement.Barrier();
        }
        await clock.WaitForTimersAsync(0);
        await fixture.StopAsync();
        That(clock.ActiveTimers == 0, "断线、换角、接管与停服回收全部daily时钟");
    }

    static void OpenBox(TcpPeer peer) => peer.Send(160, Build(writer => { writer.Write((short)65); writer.Write(1); }));
    static void ExpectUsed(TcpPeer peer, TcpTestResources resources, byte used)
    {
        var result = new Dictionary<int, byte>();
        int supported = resources.ItemResources.Boosters.DailyRetainedTemplateIds.Count;
        do
        {
            foreach (var row in DailyBoosterProjectionTests.Decode(peer.Expect(0, 541).Body)) result.Add(row.Key, row.Value);
        } while (result.Count < supported);
        That(result[Source] == used && resources.ItemResources.Boosters.DailyRetainedTemplateIds.All(result.ContainsKey),
            "ACK160与奖励差量之后收到完整541真实已用视图");
    }
}
