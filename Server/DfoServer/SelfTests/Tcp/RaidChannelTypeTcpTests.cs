using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// ACK LOGIN第3字节安装已确认的频道类型：团本准入与普通频道冒险团标签共用该上下文。
internal static class RaidChannelTypeTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "raid-channel-type"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        byte normal = endpoints.Keys.Min();
        foreach (var (channel, type) in new[] { ((byte)19, (byte)0x17), ((byte)20, (byte)0x20), ((byte)39, (byte)0x2D),
            ((byte)41, (byte)0x36), ((byte)42, (byte)0x32), (normal, (byte)22), ((byte)32, (byte)0), ((byte)43, (byte)0) })
        {
            using var peer = Open(fixture, "chtype" + channel, gamePort: endpoints[channel], channelNumber: channel);
            That(peer.LoginEndpoint.Length > 3 && peer.LoginEndpoint[3] == type,
                $"频道 {channel} ACK LOGIN 第 3 字节 = 0x{type:X2}（实际 0x{(peer.LoginEndpoint.Length > 3 ? peer.LoginEndpoint[3] : -1):X2}）");
        }
        int ticketCase = 0;
        foreach (var (raidType, channel, guide, required, held, box) in new[] {
            (0, 19, false, 10096296, 10096296, 0), (1, 20, false, 10159649, 10159649, 10162065),
            (2, 39, false, 10306760, 10306760, 10306761), (3, 42, false, 10308560, 10308560, 10308561),
            (3, 42, true, 10309342, 10309342, 10309343), (2, 39, false, 10306760, 10309054, 10309055),
            (3, 42, false, 10308560, 10309342, 0), (3, 42, true, 10309342, 10308560, 0) })
        {
            string account = "panelticket" + ticketCase++;
            var point = new DfoServer.GameWorld.Towns.Location(38, 0, 629, 196);
            using (var owner = new DfoServer.Game.Accounts.AccountOwners().Acquire(account, () => { }))
            {
                var repository = new DfoServer.Persistence.CharacterRepository(fixture.Database);
                var candidate = DfoServer.Game.Characters.CharacterCreation.Prepare(account, resources.Characters.Get(0), point, point, resources.Learning.Get);
                var learning = DfoServer.Game.Skills.SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                var role = repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95,
                    CompletedQuests = [12829, 3779, 2903, 4872, 8935, 8672], InitialLearning = learning });
                var items = resources.ItemResources;
                DfoServer.Game.Inventory.Services.InventoryService.Commit(repository, owner, role,
                    new DfoServer.Game.Inventory.Services.InventoryService(items.Items, items.GoldLimits, items.EpicPieces)
                        .Grant(role, [new(box == 0 ? held : box, 1)]));
            }
            using var peer = Open(fixture, account, gamePort: endpoints[(byte)channel], channelNumber: (byte)channel);
            peer.Select(); AntonRaidTcpTests.Drain(peer);
            byte recruitRoles = checked((byte)(1 + ticketCase % 7));
            peer.Send(664, Build(writer => { writer.Write((byte)raidType); writer.Write(Name("Ticket"));
                writer.Write(new byte[] { guide ? (byte)1 : (byte)0, 0, 0, recruitRoles }); }));
            var initial = AntonRaidTcpTests.Until(peer, 0, 590).Body;
            That(RaidPanelTests.ReadGuideFlag(initial, 4) == guide, "四团真实建队DC仅取实际模式，不随可开战人数变化");
            That(RaidPanelTests.ReadRecruitRoles(initial, 4) == recruitRoles, "四团普通/引导建队列表回填实际招募对象");
            var created = AntonRaidTcpTests.Until(peer, 1, 664).Body;
            That(created is [1, ..], "四团原生664创建供费用查询的真实攻坚队");
            uint key = U32(created, 1);
            string changedTitle = "Changed" + ticketCase;
            foreach (byte changedRoles in Enumerable.Range(1, 7).Select(value => (byte)value))
            {
                peer.Send(670, Build(writer => { writer.Write((byte)raidType); writer.Write(Name(changedTitle)); writer.Write(changedRoles); }));
                var changed = AntonRaidTcpTests.Until(peer, 0, 591).Body;
                That(U32(changed, 4) == 2 && RaidPanelTests.ReadRecruitRoles(changed, 8) == changedRoles,
                    "四团七种改名招募组合经真实670和591回填，不被默认7覆盖");
                Bytes(AntonRaidTcpTests.Until(peer, 1, 670).Body, [1], "队长修改标题及招募对象成功");
                foreach (byte mode in new byte[] { 0, 2 })
                {
                    peer.Send(1361, Build(writer => { writer.Write(mode); writer.Write(key); }));
                    That(RaidPanelTests.ReadRecruitRoles(AntonRaidTcpTests.Until(peer, 1, 1361).Body, 6) == changedRoles,
                        "四团重新查询完整记录继续保留真实招募对象");
                }
            }
            foreach (byte invalid in new byte[] { 0, 8, 255 })
            {
                peer.Send(670, Build(writer => { writer.Write((byte)raidType); writer.Write(Name("Invalid")); writer.Write(invalid); }));
                Bytes(AntonRaidTcpTests.Until(peer, 1, 670).Body, [0, 19, 0], "非法招募对象不提交改名");
            }
            peer.Send(833, Build(writer => { writer.Write((byte)0); writer.Write(0u); writer.Write(0x100u);
                writer.Write((uint)raidType); writer.Write(Name(changedTitle)); }));
            var search = AntonRaidTcpTests.Until(peer, 0, 756).Body;
            That(U32(search) >= 1 && U32(search, 8) >= 1 && RaidPanelTests.ReadRecruitRoles(search, 12) == 7,
                "非法修改后搜索仍保留最后合法标题及招募组合");
            if (box != 0)
            {
                peer.Send(658, [0]);
                var unopened = AntonRaidTcpTests.Until(peer, 0, 598).Body;
                That(unopened.Length == 9 && unopened[6] == 0 && U16(unopened, 7) == 0,
                    "只有门票盒时未就绪，不能把盒子当作票据");
                AntonRaidTcpTests.Until(peer, 1, 658);
                peer.Send(160, Build(writer => { writer.Write((short)65); writer.Write(1); }));
                var opened = AntonRaidTcpTests.Until(peer, 1, 160).Body;
                That(opened.Length == 26 && opened[0] == 1 && U32(opened, 2) == box &&
                    U32(opened, 18) == held && U32(opened, 22) == 1, "卢克/漩涡/普雷普通与引导盒真实160产出票据");
                var delta = AntonRaidTcpTests.Until(peer, 0, 14).Body;
                int at = Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + 123 * index)
                    .Single(index => U32(delta, index + 2) == held);
                int expires = resources.ItemResources.Items.Get(held).Expiration.Resolve(DateTimeOffset.UtcNow);
                That(U32(delta, at + 56) == expires && expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    "各票据按自身资源截止生成123B期限字段");
                That(fixture.Query("SELECT json_extract(item_core,'$.ExpireTime') FROM character_inventory_items " +
                    "WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1", peer.Id, held) is [[long saved]] && saved == expires,
                    "各票据期限真实持久化，与客户端收到的期限相同");
            }
            string before = fixture.Snapshot("SELECT item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id);
            foreach (byte flag in new byte[] { 0, 1 })
            {
                peer.Send(658, [flag]);
                var body = AntonRaidTcpTests.Until(peer, 0, 598).Body;
                That(body.Length == 9 && U32(body) == 1 && U16(body, 4) == peer.Id &&
                    body[6] == (held == required ? 1 : 0) && U16(body, 7) == (held == required ? 1 : 0),
                    "四团费用查询返回本人真实就绪，普通与引导门票隔离：" + account);
                Bytes(AntonRaidTcpTests.Until(peer, 1, 658).Body, [1], "两种658查询都正确收尾");
            }
            That(before == fixture.Snapshot("SELECT item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id),
                "658的0和1都只查询，不扣票或修改期限");
        }
        // 在名册发布后改库，覆盖直接C2S4权威准入、入城后换频道及新登录先8的936分流。
        foreach (var (channel, label, level, entryQuest, prerequisite) in new[]
            { ((byte)19, "anton", 85, 12829, 3779), ((byte)20, "luke", 90, 2903, 4872) })
        foreach (int scenario in Enumerable.Range(0, 4))
        {
            string account = label + "admission" + scenario;
            var point = new DfoServer.GameWorld.Towns.Location(38, 0, 629, 196);
            DfoServer.Game.Characters.Character role;
            using (var owner = new DfoServer.Game.Accounts.AccountOwners().Acquire(account, () => { }))
            {
                var repository = new DfoServer.Persistence.CharacterRepository(fixture.Database);
                if (scenario < 2)
                    role = repository.Create(owner, new(label + "Gate" + scenario, point, point)
                        { Level = (byte)level, CompletedQuests = [entryQuest, prerequisite] });
                else
                {
                    // 此场景先真实入城，使用与本组票据角色一致的完整剧情、技能和经验前置。
                    var candidate = DfoServer.Game.Characters.CharacterCreation.Prepare(label + "Gate" + scenario,
                        resources.Characters.Get(0), point, point, resources.Learning.Get);
                    var learning = DfoServer.Game.Skills.SkillPointLedger.Synchronize(0, 0, (byte)level,
                        candidate.InitialLearning with { Experience = resources.Growth.GetLevelStartExperience(level) }, resources.Learning);
                    role = repository.Create(owner, candidate with { Level = (byte)level, AcceptedStoryLevel = (byte)level,
                        CompletedQuests = [entryQuest, prerequisite], InitialLearning = learning });
                }
            }
            using var peer = Open(fixture, account, gamePort: endpoints[channel], channelNumber: channel);
            if (scenario == 2)
            {
                // 先真实入城再返回选角，确保后面的过期等级走权威准入，而非新登录选频道门禁。
                peer.Select(nativeSwitch: true);
                peer.Barrier();
                peer.Send(7, []);
                peer.Expect(1, 7, [1]);
                peer.Expect(0, 2);
                peer.Send(8, [255, 255, 2]);
            }
            else if (scenario == 3) peer.Send(8, [255, 255, 2]);
            peer.Send(1278, []); peer.Expect(1, 1278, [1]);
            if (scenario < 2)
                fixture.Sql("DELETE FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",
                    role.Id, scenario == 0 ? entryQuest : prerequisite);
            else if (scenario == 2)
                fixture.Sql("UPDATE characters SET level=$p1 WHERE id=$p0", role.Id, level - 1);
            string before = fixture.Snapshot("SELECT town,area,x,y,return_town,return_area,revision FROM characters WHERE id=$p0", role.Id);
            peer.Send(4, [0, 0]);
            peer.Expect(1, 4, [0, 96, 0]);
            if (scenario == 3) peer.Expect(0, 936, []);
            peer.Send(1278, []); peer.Expect(1, 1278, [1]);
            That(fixture.Snapshot("SELECT town,area,x,y,return_town,return_area,revision FROM characters WHERE id=$p0", role.Id) == before,
                label + "拒绝过期名册、缺前置、等级越界或新登录沿旧团本频道入城，不保存落点/返回地区且连接仍可处理后续请求");
        }
    }
}
