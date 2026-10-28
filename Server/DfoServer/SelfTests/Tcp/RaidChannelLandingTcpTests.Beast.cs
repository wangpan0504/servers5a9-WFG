using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class RaidChannelLandingTcpTests
{
    // 本服兼容入口走现有普通选图帧；网络夹具不代表真实客户端显示和点击通过。
    public static async Task RunBeastGatewayAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var at = new DateTimeOffset(2026,10,26,12,0,0,TimeSpan.FromHours(8));
        while (!BeastDungeonRotation.Contains(BeastDungeonRotation.ServiceDay(at),310)) at = at.AddDays(1);
        var clock = new DailyBoosterTestClock(at);
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"beast-gateway"),resources:resources,clock:clock);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var character = CreateRaider(fixture,resources,"BeastWire");
        var done = character.CompletedQuests.ToHashSet();
        using var peer = Open(fixture,gamePort:endpoints[49],channelNumber:49);
        That(peer.LoginEndpoint[3] == 40,"专属入口真实LOGIN保留type40");
        peer.Select(character.Slot,prepared:true,nativeSwitch:true,room:true);
        peer.Barrier();
        var landing = DfoServer.Game.Characters.ChannelAreaLanding.Place(resources.Towns,new(new(2,49),40,"[beast]"),95,done)!;
        // 魔兽落点就是等待区 51/2：它不是房间，出门因此不发 1379 返回城镇通知（与五陵 117/1 同一判定）。
        peer.Travel(landing.Town,landing.Area,landing.X,landing.Y,38,1,bottom:true);
        That(landing is { Town:51,Area:2 },"魔兽频道从共享房间出门直达原生51/2等待区");
        That(fixture.Snapshot("SELECT town,area FROM characters WHERE id=$p0",peer.Id) == "[[51,2]]",
            "出门落点经真实网络提交为51/2，不通过SQL传送进选图");
        peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
        peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),1]);
        peer.Expect(0,1536);
        foreach (int target in Enumerable.Range(5335,5))
        {
            byte[] expected = new byte[6]; BinaryPrimitives.WriteUInt32LittleEndian(expected,(uint)target);
            expected[4] = BeastDungeonRotation.Contains(BeastDungeonRotation.ServiceDay(at),target) ? (byte)2 : (byte)0;
            peer.Expect(0,550,expected);
        }
        peer.Expect(0,27,peer.SelectionNoticeBody());
        byte[] request = new byte[25];
        BinaryPrimitives.WriteUInt32LittleEndian(request,5335);
        request[9]=255; request[10]=255;
        var bad = (byte[])request.Clone(); bad[4]=4;
        string before = fixture.Snapshot("SELECT * FROM character_beast_dungeon_entries");
        peer.Send(16,bad); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
        That(fixture.Snapshot("SELECT * FROM character_beast_dungeon_entries") == before,"错难度拒绝不扣魔兽次数");
        peer.Send(16,request);
        var information = peer.Expect(0,28).Body;
        var map = peer.Expect(0,29).Body;
        That(U32(information) == 5335 && information[4] == 0 && information[7] == 0,
            "专属入口普通diff0请求返回5335及唯一普通迷宫");
        var catalog = SelfTestCatalogs.Dungeons(resources.Archive);
        var layout = catalog.Get(310).BeastLayouts[0];
        // 官方入口正文只是选图预览（[boss map] 为 -1 -1），运行路线按同源布局重建后再推静态拓扑。
        var maze = DungeonEntryPlan.ApplyBeastLayout(catalog.Get(5335).Mazes[0], layout);
        var visited = new HashSet<DungeonPoint>();
        var stages = new HashSet<DungeonPoint>();
        // 先清脚本末格，再走剩余阶段；完整通关之前不能把普通Boss坐标当成终末。
        var targets = new[] { layout.Maps.Last(row=>row.Kind=="Beast").Position }
            .Concat(layout.Maps.Where(row=>row.Kind=="Beast").Select(row=>row.Position)).Distinct().ToArray();
        ClearCurrent();
        foreach (var target in targets)
        {
            var route = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),target);
            foreach (var point in route.Skip(1))
            {
                byte[] move = new byte[100]; move[0]=(byte)point.X; move[1]=(byte)point.Y;
                peer.Send(45,move); peer.Expect(1,45,[1]); map=peer.Expect(0,29).Body;
                That(map[0] == point.X && map[1] == point.Y,"专属入口按实际门墙跨房");
                ClearCurrent();
            }
        }
        That(stages.Count == 6,"生产TCP实际加载并清除六间魔兽阶段房");
        using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{});
        var repository = new CharacterRepository(fixture.Database);
        var saved = repository.LoadCharacter(owner,character.Slot);
        That(repository.LoadBeastDungeonUsed(owner,saved.Id,BeastDungeonRotation.ServiceDay(at),310)==1 &&
            repository.LoadBeastDungeonUsed(owner,saved.Id,BeastDungeonRotation.ServiceDay(at),5335)==1,
            "实际网络入场按源图原子扣一次，新旧查询一致");
        That(BeastDungeonRotation.StageRewardItems.All(id=>saved.Inventory.Items.Where(item=>item.Core.ItemId==id).Sum(item=>item.Core.Count)==1),
            "生产TCP六阶段各发一个罐，回访房间不重复发奖");
        peer.Send(3,[1]); peer.Expect(1,3,[1]); peer.Closed();

        void ClearCurrent()
        {
            var point = new DungeonPoint(map[0],map[1]);
            var expected = layout.Maps.Single(row=>row.Position==point);
            bool first = !visited.Contains(point);
            That(first ? map.Length>=19 && map[13]==1 && U32(map,14)==expected.Id : map.Length==16 && map[13]==0 && map[14]==0 && map[15]==255,
                "专属入口首次NOTI29对应原地图ID，回访使用已加载房间短包");
            FormalDungeonTcpTests.Load(peer);
            var fatigue = fixture.Query("SELECT used,maximum,used_maximum,remaining,context_auxiliary FROM character_fatigue WHERE character_id=$p0",peer.Id).Single();
            byte[] expectedFatigue = new byte[10];
            for(int i=0;i<5;i++) BinaryPrimitives.WriteUInt16LittleEndian(expectedFatigue.AsSpan(2*i),Convert.ToUInt16(fatigue[i]));
            peer.Expect(0,36,expectedFatigue); peer.Barrier();
            if (!visited.Add(point)) return;
            var actors = Enumerable.Range(0,map[18]).Select(index=>19+22*index)
                .Where(offset=>map[offset+13]<=3 && map[offset+21]==0).OrderBy(offset=>map[offset+13]==3).ToArray();
            foreach (int offset in actors)
            {
                ushort key=U16(map,offset+6);
                peer.Send(39,FormalDungeonTcpTests.Death(key,peer.Id,map[offset+12]));
                That(U16(peer.Expect(0,38).Body)==key,"魔兽网络死亡回执绑定实际演员");
                peer.Barrier(packet=>
                {
                    That(packet.Opcode switch
                    {
                        37 => packet.Body.Length==91 && packet.Body[0]==95,
                        14 => packet.Body.Length==126 && packet.Body[0]==0 && U16(packet.Body,1)==1 &&
                            BeastDungeonRotation.StageRewardItems.Contains(checked((int)U32(packet.Body,5))) &&
                            U32(packet.Body,9)==1 && expected.Kind=="Beast",
                        115 => packet.Body.Length==4 && packet.Body[0]==1 && U16(packet.Body,2)==key,
                        31 => packet.Body.Length==0 && stages.Count==5 && expected.Kind=="Beast",
                        _ => false
                    },"魔兽死亡只接收完整成长、阶段库存及唯一终末许可通知："+packet.Opcode);
                });
            }
            if (expected.Kind=="Beast") stages.Add(point);
        }
    }
}
