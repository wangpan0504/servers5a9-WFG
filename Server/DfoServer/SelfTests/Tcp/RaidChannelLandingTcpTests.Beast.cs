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
        // 落点 51/2：51 只有区域2声明 [dungeon gate] 46，魔兽世界地图46的气球与 -1/-1 选图区域都在这里；
        // 落 51/0 开图只有底图没有气球（用户 2026-10-28 实测「走进中央公园，一张地图都没有」）。
        var landingDecision = DfoServer.Game.Characters.ChannelAreaLanding.Decide(resources.Towns,new(new(2,49),40,"[beast]"),95,done);
        var landing = landingDecision.Spot;
        // 魔兽落点就是等待区 51/2：它不是房间，出门因此不发 1379 返回城镇通知（与五陵 117/1 同一判定）。
        if (landing is not null)
        {
            peer.Travel(landing.Town,landing.Area,landing.X,landing.Y,38,1,bottom:true);
            That(landing is { Town:51,Area:2 },"魔兽频道从共享房间出门直达原生51/2等待区");
            That(fixture.Snapshot("SELECT town,area FROM characters WHERE id=$p0",peer.Id) == "[[51,2]]",
                "出门落点经真实网络提交为51/2，不通过SQL传送进选图");
        }
        else
        {
            That(landingDecision.Reason is "town-not-in-catalog" or "no-standable-door" &&
                fixture.Snapshot("SELECT town,area FROM characters WHERE id=$p0",peer.Id) == "[[38,1]]",
                "缺少已登记可站等待区时留在共享房间，不猜落点：" + landingDecision.Reason);
            // 尚无合法网关选图上下文，仍实际发送形状合法的入场帧，证明拒绝原因及零提交。
            string[] missingLandingStateSql =
            [
                "SELECT * FROM characters ORDER BY id",
                "SELECT * FROM character_inventory_items ORDER BY item_uid",
                "SELECT * FROM character_fatigue ORDER BY character_id",
                "SELECT * FROM character_dungeon_state ORDER BY character_id",
                "SELECT * FROM character_beast_dungeon_entries ORDER BY character_id,day,dungeon_id",
                "SELECT * FROM character_beast_dungeon_reservations ORDER BY character_id,instance_id",
                "SELECT * FROM character_beast_stage_rewards ORDER BY character_id,instance_id,stage"
            ];
            var missingLandingBefore = missingLandingStateSql.Select(sql => fixture.Snapshot(sql)).ToArray();
            byte[] missingLandingRequest = new byte[25];
            BinaryPrimitives.WriteUInt32LittleEndian(missingLandingRequest,5335);
            missingLandingRequest[9]=255; missingLandingRequest[10]=255;
            peer.Send(16,missingLandingRequest); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
            for (int index=0;index<missingLandingStateSql.Length;index++)
                That(fixture.Snapshot(missingLandingStateSql[index]) == missingLandingBefore[index],
                    "缺等待区的真实网关请求不改变角色、成本、疲劳、尝试号、次数、预约或阶段奖励：" + missingLandingStateSql[index]);
            peer.Send(3,[1]); peer.Expect(1,3,[1]); peer.Closed();
            await fixture.StopAsync();
            var missingLandingRejections = fixture.Events().Where(row => row.GetProperty("Kind").GetString() == "formal_dungeon_entry")
                .Select(row => row.GetProperty("Detail")).ToArray();
            That(missingLandingRejections.Length > 0 && missingLandingRejections.All(detail =>
                    !detail.GetProperty("Accepted").GetBoolean() && detail.GetProperty("Beast").GetBoolean() &&
                    detail.GetProperty("CharacterId").GetInt32() == peer.Id &&
                    detail.GetProperty("Request").GetProperty("DungeonIndex").GetUInt32() == 5335 &&
                    detail.GetProperty("RejectReason").GetString() == "beast-gateway-channel-and-mode-required"),
                "缺等待区的真实网关请求精确拒绝为beast-gateway-channel-and-mode-required，不以通用失败包替代原因");
            Console.WriteLine("beast-gateway：缺等待区拒绝及零提交断言完成；完整六阶段正向链仍需已登记可站的51/2等待区，本分支不证明正向通过。");
            return;
        }
        // 专属频道保留独立资源，第一版每日入侵名单只发布到普通频道原31/39。
        peer.Expect(0,27,peer.SelectionNoticeBody());
        byte[] request = new byte[25];
        BinaryPrimitives.WriteUInt32LittleEndian(request,5335);
        request[9]=255; request[10]=255;
        // 魔兽六个难度按钮按 0–5 下发（实机点击样本为2）；越界的6才是错难度。
        var bad = (byte[])request.Clone(); bad[4]=6;
        string before = fixture.Snapshot("SELECT * FROM character_beast_dungeon_entries");
        peer.Send(16,bad); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
        That(fixture.Snapshot("SELECT * FROM character_beast_dungeon_entries") == before,"错难度拒绝不扣魔兽次数");
        var catalog = SelfTestCatalogs.Dungeons(resources.Archive);
        var sourceDefinition = catalog.Get(310);
        bool gatewayMissing = !catalog.ContainsDungeon(5335);
        bool sourceUnavailable = sourceDefinition.BeastLayouts.Any(layout => layout.ResourceError is not null);
        if (sourceUnavailable)
            That(sourceDefinition.BeastLayouts.Where(layout => layout.ResourceError is not null).All(layout =>
                    layout.ResourceError!.Contains("魔兽地图未登记:",StringComparison.Ordinal) ||
                    layout.ResourceError.Contains("魔兽地图资源缺失:",StringComparison.Ordinal) ||
                    layout.ResourceError.Contains("魔兽平衡表资源缺失",StringComparison.Ordinal)),
                "源布局缺资源必须保留具体依赖诊断");
        if (gatewayMissing || sourceUnavailable)
        {
            bool accepted = DungeonEntryPlan.TryCreate(catalog,character,5335,0,0,out _,out var reason,beastMode:true);
            That(!accepted && reason == (gatewayMissing ? "dungeon-resource-unavailable" : "beast-gateway-source-unavailable"),
                "区分未登记请求网关与已登记网关的缺失源布局：" + reason);
        }
        if (landing is null || gatewayMissing || sourceUnavailable)
        {
            string characterBefore = fixture.Snapshot();
            string inventoryBefore = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
            string fatigueBefore = fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id");
            peer.Send(16,request); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
            That(fixture.Snapshot("SELECT * FROM character_beast_dungeon_entries") == before &&
                fixture.Snapshot() == characterBefore &&
                fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") == inventoryBefore &&
                fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id") == fatigueBefore,
                "缺等待区、网关或兽资源的真实请求拒绝不扣次数、资产、疲劳或尝试号");
            // 缺等待区时共享房间仍可打开普通选图，但不能宣称已打开魔兽气球。
            OpenSelection();
            peer.Send(7); peer.Expect(1,7,[1]); peer.Expect(0,2);
            peer.Select(character.Slot,prepared:true); peer.Barrier();
            That(fixture.Snapshot("SELECT * FROM character_beast_dungeon_entries") == before &&
                fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") == inventoryBefore &&
                fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id") == fatigueBefore,
                "同连接小退重选恢复原资产、疲劳和魔兽次数");
        }
        else
        {
            peer.Send(16,request);
            var information = peer.Expect(0,28).Body;
            var map = peer.Expect(0,29).Body;
            That(U32(information) == 5335 && information[4] == 0 && information[7] == 0,
                "专属入口普通diff0请求返回5335及唯一普通迷宫");
            var layout = sourceDefinition.BeastLayouts[0];
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
        peer.Send(3,[1]); peer.Expect(1,3,[1]); peer.Closed();
        if (landing is null || gatewayMissing || sourceUnavailable)
        {
            await fixture.StopAsync();
            var rejected = fixture.Events().Last(row => row.GetProperty("Kind").GetString() == "formal_dungeon_entry").GetProperty("Detail");
            string expectedReason = landing is null ? "beast-gateway-channel-and-mode-required" :
                gatewayMissing ? "dungeon-resource-unavailable" : "beast-gateway-source-unavailable";
            That(!rejected.GetProperty("Accepted").GetBoolean() && rejected.GetProperty("RejectReason").GetString() == expectedReason,
                "真实网络拒绝绑定实际缺依赖层，不以通用失败包掩盖其他原因：" + expectedReason);
        }

        void OpenSelection()
        {
            peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),1]);
            peer.Expect(0,1536);
            if (landing is not null)
            foreach (int target in Enumerable.Range(5335,5))
            {
                byte[] expected = new byte[6]; BinaryPrimitives.WriteUInt32LittleEndian(expected,(uint)target);
                expected[4] = BeastDungeonRotation.Contains(BeastDungeonRotation.ServiceDay(at),target) ? (byte)2 : (byte)0;
                peer.Expect(0,550,expected);
            }
            // 选图通知末尾带五个气球候选；不填这张列表客户端只有底图（用户 2026-10-28 实测）。
            peer.Expect(0,27,peer.SelectionNoticeBody(selectionCandidates:landing is null ? [] : [5335,5336,5337,5338,5339]));
        }
    }
}
