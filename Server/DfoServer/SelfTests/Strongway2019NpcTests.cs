using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Management;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer;

internal static class Strongway2019NpcTests
{
    // 639消费者的独立固定金样：地图字段为town、area；action、u32字节长60、ASCII正文，无NUL尾部。
    const string TextHex = "5B6D61705D2033382031205B2F6D61705D205B6E70635D2034303030303130353120605B72696768745D6020363835203234302030205B2F6E70635D";
    static byte[] Golden(bool active) => Convert.FromHexString((active ? "00" : "02") + "3C000000" + TextHex);

    internal static void AssertDisplay(byte[] body, bool active)
    {
        Bytes(body, Golden(active), "639精确action、u32字节长、原生地图/NPC脚本与无NUL尾部");
        That(body.Length == 65 && BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1)) == 60 &&
            !body.AsSpan(5).Contains((byte)0) && Encoding.ASCII.GetString(body, 5, 60) ==
                "[map] 38 1 [/map] [npc] 400001051 `[right]` 685 240 0 [/npc]",
            "原NPC400001051放置于城镇38/地区1右向685/240，正文不含虚构资源或终止字节");
    }

    public static void RunCore()
    {
        int before = Count;
        byte[] shown = Strongway2019NpcPackets.Display(true), hidden = Strongway2019NpcPackets.Display(false);
        AssertDisplay(shown, true); AssertDisplay(hidden, false);
        That(shown.AsSpan(1).SequenceEqual(hidden.AsSpan(1)) && shown[0] == 0 && hidden[0] == 2,
            "显隐只改变action，删除仍携带相同NPC定位脚本");
        byte[] shownSnapshot = shown.ToArray(), hiddenSnapshot = hidden.ToArray();
        shown[5] = 0; hidden[^1] = 0;
        foreach (bool active in new[] { false, true, true, false })
        {
            var body = Strongway2019NpcPackets.Display(active);
            AssertDisplay(body, active);
            That(!ReferenceEquals(body, shown) && !ReferenceEquals(body, hidden), "639每次生成独立包体，调用方修改不污染下一连接");
        }
        Bytes(shownSnapshot, Golden(true), "交替显隐不回写已冻结的添加包");
        Bytes(hiddenSnapshot, Golden(false), "交替显隐不回写已冻结的删除包");
        Console.WriteLine($"PASS {Count - before} 首期活动NPC639独立金样及包体隔离");
    }

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        var clock = new Testing.Inventory.DailyBoosterTestClock(new(2026, 11, 2, 1, 0, 0, TimeSpan.Zero));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "strongway2019-npc"), gm: true,
            resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        foreach (var (account, name) in new[] { ("swnpcnormal", "SWNpcNormal"), ("swnpccontent", "SWNpcContent") })
        {
            using var owner = owners.Acquire(account, () => { });
            repository.Create(owner, new(name, resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
        }
        var events = new ServerEventService(new ServerEventRepository(fixture.Database), clock);
        events.Override(312, false);
        await fixture.StartAsync();
        using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "ready.json")));
        var content = ready.RootElement.GetProperty("ChannelEntries").EnumerateArray()
            .Single(row => row.GetProperty("Entry").GetProperty("Type").GetInt32() == 55);
        int contentPort = content.GetProperty("Port").GetInt32();
        byte contentNumber = content.GetProperty("Entry").GetProperty("Identity").GetProperty("Number").GetByte();
        byte[] initialEvents, initialMileage;
        using (var normal = Open(fixture, "swnpcnormal"))
        using (var special = Open(fixture, "swnpccontent", gamePort: contentPort, channelNumber: contentNumber))
        {
            normal.Select(prepared: true); special.Select(prepared: true);
            AssertDisplay(normal.StrongwayNpcDisplay, false); AssertDisplay(special.StrongwayNpcDisplay, false);
            That(normal.LoginEndpoint[3] != 55 && special.LoginEndpoint[3] == 55,
                "NPC初始化同时覆盖真实普通频道及原生type55内容频道");
            initialEvents = normal.EventInfoPayload.ToArray(); initialMileage = normal.MileageLimits.ToArray();
            Bytes(special.EventInfoPayload, initialEvents, "内容频道保留同一原108活动目录");
            Bytes(special.MileageLimits, initialMileage, "内容频道保留原706积分限购初始化");
            normal.Barrier(); special.Barrier();
            Gm("event on 312"); RefreshBoth(true);
            Gm("event off 312"); RefreshBoth(false);
            Gm("event on 312"); RefreshBoth(true);
            Gm("event on 312"); normal.Barrier(); special.Barrier();
            var now = clock.GetUtcNow();
            Gm($"event schedule 312 {now.AddSeconds(1):yyyy-MM-ddTHH:mm:sszzz} {now.AddSeconds(3):yyyy-MM-ddTHH:mm:sszzz}");
            RefreshBoth(false);
            await clock.WaitForOneShotTimerAsync(now.AddSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(1)); RefreshBoth(true);
            await clock.WaitForOneShotTimerAsync(now.AddSeconds(3));
            clock.Advance(TimeSpan.FromSeconds(2)); RefreshBoth(false);

            void RefreshBoth(bool active)
            {
                foreach (var peer in new[] { normal, special }) Refresh(peer, active, true);
            }
        }
        Gm("event on 312");
        using (var normal = Open(fixture, "swnpcnormal"))
        using (var special = Open(fixture, "swnpccontent", gamePort: contentPort, channelNumber: contentNumber))
        {
            normal.Select(prepared: true); special.Select(prepared: true);
            AssertDisplay(normal.StrongwayNpcDisplay, true); AssertDisplay(special.StrongwayNpcDisplay, true);
            normal.Barrier(); special.Barrier();
            Gm("event off 266");
            foreach (var peer in new[] { normal, special })
            {
                CashShopTcpTests.AssertPromotionNativeRefresh(peer.Expect(0, 108), 0, false, [2532]);
                peer.Barrier(packet => That(packet.Opcode != 639 && packet.Opcode != 706,
                    "同epoch纯积分入口关闭不重复NPC639或残留706"));
            }
            Gm("event off 312");
            Refresh(normal, false, false); Refresh(special, false, false);
        }
        ushort switchedCharacter;
        using (var normal = Open(fixture, "swnpcnormal"))
        {
            normal.ExpectedOpenEvents = [2532]; normal.Select(prepared: true);
            AssertDisplay(normal.StrongwayNpcDisplay, false);
            That(normal.MileageLimits.Length == 0, "积分关闭时108后直接639删除，未伪造706");
            normal.Barrier();
            switchedCharacter = normal.Id;
            normal.Send(3, [1]); normal.Expect(1, 3, [1]); normal.Closed();
        }
        // 同一客户端换线时动态NPC登记是否清空没有正证；目标连接必须按当前312重放添加或删除。
        Gm("event on 312");
        using (var special = Open(fixture, "swnpcnormal", gamePort: contentPort, channelNumber: contentNumber))
        {
            special.ExpectedOpenEvents = [2532]; special.Select(prepared: true, nativeSwitch: true);
            That(special.Id == switchedCharacter && special.LoginEndpoint[3] == 55,
                "普通频道退出后，同账号原角色以原生直接C2S4进入type55频道");
            AssertDisplay(special.StrongwayNpcDisplay, true);
            That(special.MileageLimits.Length == 0, "切入强者频道的639添加不恢复已关闭的706");
            special.Barrier(packet => That(packet.Opcode != 639, "切入强者频道只有一次初始化639"));
            special.Send(3, [1]); special.Expect(1, 3, [1]); special.Closed();
        }
        Gm("event off 312");
        using (var normal = Open(fixture, "swnpcnormal"))
        {
            normal.ExpectedOpenEvents = [2532]; normal.Select(prepared: true, nativeSwitch: true);
            That(normal.Id == switchedCharacter && normal.LoginEndpoint[3] != 55,
                "同账号原角色从强者频道以原生直接C2S4切回普通频道");
            AssertDisplay(normal.StrongwayNpcDisplay, false);
            normal.Barrier(packet => That(packet.Opcode != 639, "切回普通频道仅发一次639删除以清理旧活动登记"));
            normal.Send(3, [1]); normal.Expect(1, 3, [1]); normal.Closed();
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "NPC显隐及定时通知不损坏角色或账号持久状态");
        Console.WriteLine($"PASS {Count - before} 首期NPC普通/内容频道初始化、GM切换及定时边界TCP");

        void Gm(string command) => That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, command, out string reply) &&
            reply.StartsWith("OK "), "真实GM活动开关：" + command);
        void Refresh(TcpPeer peer, bool active, bool mileage)
        {
            var info = peer.Expect(0, 108);
            CashShopTcpTests.AssertPromotionNativeRefresh(info, 0, false, mileage ? [2532, 266] : [2532]);
            if (mileage) Bytes(peer.Expect(0, 706).Body, initialMileage, "312变化保留原七条706限购值与先后顺序");
            var display = peer.Expect(0, 639);
            That(!display.Protected, "在线639仍使用普通原生帧"); AssertDisplay(display.Body, active);
            peer.Barrier(packet => That(packet.Opcode is not (639 or 108 or 706), "一次312变化只投影一次完整目录及NPC状态"));
        }
    }

    public static async Task RunResourcesAsync(TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        foreach (string mutation in new[] { "epoch", "event", "owner" })
        {
            using var fixture = new Strongway2019MatchingTests.Fixture(towns);
            var session = fixture.Team()[0];
            var actor = session.CaptureSocialActor()!;
            var events = new ServerEventService(new ServerEventRepository(fixture.DatabasePath), fixture.Clock);
            var town = new TownProjection(towns, null!, null, resources.EpicPieces, clock: fixture.Clock,
                events: events, mileageShop: resources.CashShop.Mileage);
            using var stop = new CancellationTokenSource();
            using var output = new GameSenderBudgetTests.HeldStream();
            var sent = new List<ushort>();
            await using var sender = new GameConnectionSender(actor.Owner, output, stop, 0,
                (_, opcode, _, _, _) => sent.Add(opcode));
            var context = new GameConnectionContext(new(639, IPAddress.Loopback, output, stop), actor.Owner, session, sender);
            var blocking = sender.Send(0, 1232, [0], "npc_test_hold");
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                // 命令已提交的外层发布保持有效，639自身必须再核活动版本和选角身份。
                using (sender.QueuePublication(() => true)) await town.ServerEventsAsync(context);
                if (mutation == "epoch") { session.ReturnToRoster(); session.Select(0); session.MarkReady(); }
                else if (mutation == "event") events.Override(312, false);
                else fixture.Takeover(session);
            }
            finally { output.Release.TrySetResult(); }
            try
            {
                await blocking;
                await sender.DrainAsync();
            }
            catch (Exception error) when (mutation == "owner" && error is InvalidOperationException or IOException or OperationCanceledException) { }
            That(!sent.Contains(639), "已排队639在真实发送前丢弃旧" + mutation + "，不把前角色/前活动NPC写进新视图");
            if (mutation != "owner")
            {
                await town.ServerEventsAsync(context); await sender.DrainAsync();
                That(sent.Count(opcode => opcode == 639) == 1, "旧639被丢弃后当前身份仍可初始化一次新639：" + mutation);
            }
        }
        await ReopenedBeforeInitializationWrittenAsync(towns, resources);
        await InventoryCommitDoesNotInvalidateQueuedNpcAsync(towns, resources);
        Console.WriteLine($"PASS {Count - before} 首期NPC排队发布的旧owner、旧epoch、旧活动版本失效及重开补发");
    }

    static async Task ReopenedBeforeInitializationWrittenAsync(TownCatalog towns, ItemResources resources)
    {
        using var fixture = new Strongway2019MatchingTests.Fixture(towns);
        var session = fixture.Team()[0];
        var actor = session.CaptureSocialActor()!;
        var events = new ServerEventService(new ServerEventRepository(fixture.DatabasePath), fixture.Clock);
        That(events.Read(312).Active, "受阻初始化开始时312确实开放");
        var town = new TownProjection(towns, null!, null, resources.EpicPieces, clock: fixture.Clock,
            events: events, mileageShop: resources.CashShop.Mileage);
        using var log = new ServerLog(Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "npc-reopen-log"));
        using var stop = new CancellationTokenSource();
        using var output = new GameSenderBudgetTests.HeldStream();
        var displayed = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sender = new GameConnectionSender(actor.Owner, output, stop, 0, (_, opcode, _, body, _) =>
        {
            if (opcode != 639) return;
            displayed.Enqueue(body.ToArray()); delivered.TrySetResult();
        });
        var context = new GameConnectionContext(new(640, IPAddress.Loopback, output, stop), actor.Owner, session, sender);
        await using var projection = new ServerEventProjection(context, events, town, log, fixture.Clock);
        context.ServerEvents = projection;
        // 与真实选角命令相同地持有连接门；worker只会看到关闭后重开的最终状态。
        await context.CommandGate.WaitAsync();
        try
        {
            var blocking = sender.Send(0, 1232, [0], "npc_reopen_hold");
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                using (sender.QueuePublication(() => true)) await town.ServerEventsAsync(context);
                events.Override(312, false);
                events.Override(312, true);
            }
            finally { output.Release.TrySetResult(); }
            await blocking;
            await sender.DrainAsync();
            That(displayed.IsEmpty, "关闭重开使初始639版本过期，连接门未释放前不得发送该旧添加包");
        }
        finally { context.CommandGate.Release(); }
        bool received = true;
        try { await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { received = false; }
        That(received, "初始化639未写出前312关闭重开，真实活动worker仍须补发最终有效NPC添加，不能把入队当已显示");
        await sender.DrainAsync();
        That(displayed.Count == 1, "过期初始化包丢弃后最终有效639只实际写出一次");
        AssertDisplay(displayed.Single(), true);
    }

    static async Task InventoryCommitDoesNotInvalidateQueuedNpcAsync(TownCatalog towns, ItemResources resources)
    {
        using var fixture = new Strongway2019MatchingTests.Fixture(towns);
        var session = fixture.Team()[0];
        var actor = session.CaptureSocialActor()!;
        var events = new ServerEventService(new ServerEventRepository(fixture.DatabasePath), fixture.Clock);
        var town = new TownProjection(towns, null!, null, resources.EpicPieces, clock: fixture.Clock,
            events: events, mileageShop: resources.CashShop.Mileage);
        var currency = new DfoServer.Game.Currency.CurrencyService(resources.GoldLimits);
        using var log = new ServerLog(Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "npc-inventory-log"));
        using var stop = new CancellationTokenSource();
        using var output = new GameSenderBudgetTests.HeldStream();
        var displayed = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        await using var sender = new GameConnectionSender(actor.Owner, output, stop, 0, (_, opcode, _, body, _) =>
        {
            if (opcode == 639) displayed.Enqueue(body.ToArray());
        });
        var context = new GameConnectionContext(new(641, IPAddress.Loopback, output, stop), actor.Owner, session, sender);
        await using var projection = new ServerEventProjection(context, events, town, log, fixture.Clock);
        context.ServerEvents = projection;
        await context.CommandGate.WaitAsync();
        try
        {
            var blocking = sender.Send(0, 1232, [0], "npc_inventory_hold");
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                using (sender.QueuePublication(() => true)) await town.ServerEventsAsync(context);
                var original = session.Selected!;
                long epoch = session.SelectionEpoch;
                var committed = session.PlanAndCommitInventory(character => currency.Grant(character,
                    DfoServer.Game.Currency.CurrencyKind.Gold, 1));
                That(committed.Accepted && committed.Receipt is not null &&
                    !ReferenceEquals(session.Selected, original) && session.SelectionEpoch == epoch &&
                    session.Selected!.Id == original.Id && session.Selected.Inventory.Gold == original.Inventory.Gold + 1,
                    "原Session库存事务真实增加1金币并替换角色快照，身份和选角epoch保持不变");
            }
            finally { output.Release.TrySetResult(); }
            await blocking;
            await sender.DrainAsync();
            // 仍持连接门，后台不能补发掩盖丢包；活动NPC不依赖角色资产revision。
            That(displayed.Count == 1, "同角色同epoch的合法库存提交不能使已排队639失效，且无需后台重复排队");
            AssertDisplay(displayed.Single(), true);
        }
        finally { context.CommandGate.Release(); }
    }
}
