using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class RaidChannelLandingTcpTests
{
    public static async Task RunBeastSelectionAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new DailyBoosterTestClock(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(8)));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "beast-selection-wire"), resources: resources, clock: clock);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var area = resources.Towns.Areas[(31, 2)];
        var bounds = area.Map(RaidQuests.ToHashSet()).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(RaidQuests.Contains)).Bounds;
        var position = new Location(31, 2, (ushort)(bounds.X + bounds.Width / 2), (ushort)(bounds.Y + bounds.Height / 2));
        var character = CreateRaider(fixture, resources, "BeastSelTCP", position);
        var pair = BeastDungeonRotation.Pair(BeastDungeonRotation.ServiceDay(clock.GetUtcNow()));
        foreach (byte number in new byte[] { 10, 11 })
        {
            using var peer = Open(fixture, gamePort: endpoints[number], channelNumber: number);
            That(peer.LoginEndpoint[3] == 22, "两个普通频道都使用真实LOGIN type22");
            peer.Select(character.Slot, prepared: true, resumeTown: true, room: false);
            peer.Barrier();
            peer.Send(15, new byte[4]);
            peer.Expect(1, 15, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
            peer.Expect(0, 27, peer.SelectionNoticeBody(selectionCandidates: [pair.First, pair.Second]));
            new BeastDisplayWire(peer).Selection(BeastDungeonRotation.ServiceDay(clock.GetUtcNow()), pair);
            That(!Enumerable.Range(310, 5).Any(peer.DungeonEnterCounts.ContainsKey),
                "原五图不收到冒充Beast额度的550普通次数通知");
            peer.Barrier();
            peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
        }
        That(fixture.Query("SELECT count(*) FROM character_beast_dungeon_entries") is [[0L]],
            "查看每日两图及换普通频道均不写入或消耗Beast额度");
        var catalog = SelfTestCatalogs.Dungeons(resources.Archive);
        bool firstAvailable = NativeBeastLayoutAvailable(catalog,pair.First);
        bool secondAvailable = NativeBeastLayoutAvailable(catalog,pair.Second);
        if (firstAvailable)
        {
            await RunBeastNativeDeadlineAsync(fixture, resources, clock, endpoints[10], character.Slot, pair.First);
            await RunBeastNativeDeadlineAsync(fixture, resources, clock, endpoints[10], character.Slot, pair.First,
                timeout: false, expectedUsed: 2);
        }
        else await VerifyUnavailableNativeBeastAsync(fixture,clock,endpoints[10],character.Slot,pair.First,pair,2);
        if (secondAvailable)
        {
            if (!firstAvailable) PrepareIndependentSecondTickets(fixture,character.Slot);
            await RunBeastNativeDeadlineAsync(fixture, resources, clock, endpoints[10], character.Slot, pair.Second,
                timeout: false,expectedFirstRemaining:firstAvailable ? (byte)0 : (byte)2);
        }
        else await VerifyUnavailableNativeBeastAsync(fixture,clock,endpoints[10],character.Slot,pair.Second,pair,firstAvailable ? (byte)0 : (byte)2);
        if (firstAvailable)
            await RunBeastPartyDisplayAsync(fixture, resources, clock, endpoints[10], position);
        else Console.WriteLine("beast-native: First布局有正式ResourceError；原native party deadline未执行，不以Session合成证明补充TCP正向");
    }

    // First未入场时独立准备Second正向的初始票据；不冒充First扣票或次数，不替换已有库存/UID。
    static void PrepareIndependentSecondTickets(TcpTestFixture fixture,ushort slot)
    {
        using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { });
        var repository = new CharacterRepository(fixture.Database);
        var character = repository.LoadCharacter(owner,slot);
        repository.CommitInventory(owner,character,new(character.Inventory,character.Inventory with
        {
            Items=character.Inventory.Items.Add(new(0,new(InventoryListType.Main,121),new(BeastDungeonTicket.Event,ItemKind.Material,3)))
                .Add(new(0,new(InventoryListType.Main,122),new(BeastDungeonTicket.Ordinary,ItemKind.Material,5)))
        },"test_beast_independent_second_tickets"));
    }

    // 只按正式定义的坏块前提分支，不把结构错误转成可用布局，不替换实际资源。
    static bool NativeBeastLayoutAvailable(DungeonCatalog catalog,int dungeon)
    {
        var broken = catalog.Get(dungeon).BeastLayouts.Select((layout,index) => (layout,index))
            .Where(row => row.layout.ResourceError is not null).ToArray();
        foreach (var row in broken)
            Console.WriteLine($"beast-native: dungeon={dungeon} layout[{row.index}] ResourceError={row.layout.ResourceError}");
        return broken.Length == 0;
    }

    static async Task VerifyUnavailableNativeBeastAsync(TcpTestFixture fixture,DailyBoosterTestClock clock,int port,ushort slot,
        int dungeon,(int First,int Second) pair,byte firstRemaining)
    {
        using var peer = Open(fixture,gamePort:port,channelNumber:10);
        peer.Select(slot,prepared:true,resumeTown:true,room:false); peer.Barrier();
        var display = new BeastDisplayWire(peer);
        OpenSelection();
        string[] stateSql =
        [
            "SELECT * FROM characters WHERE id=$p0",
            "SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",
            "SELECT * FROM character_fatigue WHERE character_id=$p0",
            "SELECT * FROM character_dungeon_state WHERE character_id=$p0",
            "SELECT * FROM character_beast_dungeon_entries WHERE character_id=$p0 ORDER BY day,dungeon_id",
            "SELECT * FROM character_beast_dungeon_reservations WHERE character_id=$p0 ORDER BY instance_id",
            "SELECT * FROM character_beast_stage_rewards WHERE character_id=$p0 ORDER BY instance_id,stage"
        ];
        string[] before = stateSql.Select(sql => fixture.Snapshot(sql,peer.Id)).ToArray();
        byte[] request = new byte[25];
        BinaryPrimitives.WriteUInt32LittleEndian(request,checked((uint)dungeon));
        request[4]=4; request[7]=2; request[9]=255; request[10]=255;
        peer.Send(16,request); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
        System.Text.Json.JsonElement rejected;
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            while (true)
            {
                var events = fixture.Events().Where(row => row.GetProperty("Kind").GetString() == "formal_dungeon_entry")
                    .Select(row => row.GetProperty("Detail")).Where(detail =>
                        detail.TryGetProperty("CharacterId",out var id) && id.GetInt32() == peer.Id &&
                        detail.TryGetProperty("Request",out var entryRequest) &&
                        entryRequest.GetProperty("DungeonIndex").GetInt32() == dungeon).ToArray();
                if (events.Length > 0) { rejected=events[^1]; break; }
                if (deadline.IsCancellationRequested)
                    throw new InvalidOperationException($"beast-native:missing persisted formal entry character={peer.Id} dungeon={dungeon}");
                await Task.Delay(20);
            }
        }
        That(!rejected.GetProperty("Accepted").GetBoolean() && rejected.GetProperty("Beast").GetBoolean() &&
            rejected.GetProperty("CharacterId").GetInt32() == peer.Id &&
            rejected.GetProperty("Request").GetProperty("DungeonIndex").GetInt32() == dungeon &&
            rejected.GetProperty("RejectReason").GetString() == "entry-plan:beast-layout-resource-unavailable",
            "坏布局的合法原图CMD16必须绑定精确正式拒绝，不以通用ACK掩盖其它错误");
        CheckUnchanged("合法CMD16因布局资源错误拒绝，角色、票据库存、疲劳、副本状态、额度、预约与阶段奖励零提交");
        // 保持同一个MapSelect身份重放15，不借退出落点/revision变化污染16的零提交证明。
        OpenSelection();
        CheckUnchanged("拒绝后原连接仍可重放15并获得完整27及display，七类持久状态保持不变");
        peer.Send(3,[1]); peer.Expect(1,3,[1]); peer.Closed();
        Console.WriteLine($"beast-native: dungeon={dungeon}实际CMD16精确资源拒绝且七类零提交；同MapSelect重放15通过；未证明deadline或六阶段正向");

        void CheckUnchanged(string label)
        {
            for (int index=0;index<stateSql.Length;index++)
                That(fixture.Snapshot(stateSql[index],peer.Id) == before[index],label+" / "+stateSql[index]);
        }
        void OpenSelection()
        {
            peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),1]);
            peer.Expect(0,27,peer.SelectionNoticeBody(selectionCandidates:[pair.First,pair.Second]));
            display.Selection(BeastDungeonRotation.ServiceDay(clock.GetUtcNow()),pair,firstRemaining,2);
            peer.Barrier();
        }
    }
}
