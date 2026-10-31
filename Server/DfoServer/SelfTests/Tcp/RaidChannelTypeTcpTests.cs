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
                    CompletedQuests = [12829, 3779, 2903, 4872, 8935], InitialLearning = learning });
                var items = resources.ItemResources;
                DfoServer.Game.Inventory.Services.InventoryService.Commit(repository, owner, role,
                    new DfoServer.Game.Inventory.Services.InventoryService(items.Items, items.GoldLimits, items.EpicPieces)
                        .Grant(role, [new(box == 0 ? held : box, 1)]));
            }
            using var peer = Open(fixture, account, gamePort: endpoints[(byte)channel], channelNumber: (byte)channel);
            peer.Select(); AntonRaidTcpTests.Drain(peer);
            peer.Send(664, Build(writer => { writer.Write((byte)raidType); writer.Write(Name("Ticket"));
                writer.Write(new byte[] { guide ? (byte)1 : (byte)0, 0, 0, 0 }); }));
            That(AntonRaidTcpTests.Until(peer, 1, 664).Body is [1, ..], "四团原生664创建供费用查询的真实攻坚队");
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
        // 在名册发布后改库，证明权威门禁不信任旧资格位；同时覆盖直接C2S4与先C2S8的入口。
        foreach (var (channel, label, level, entryQuest, prerequisite) in new[]
            { ((byte)19, "anton", 85, 12829, 3779), ((byte)20, "luke", 90, 2903, 4872) })
        foreach (int scenario in Enumerable.Range(0, 3))
        {
            string account = label + "admission" + scenario;
            var point = new DfoServer.GameWorld.Towns.Location(38, 0, 629, 196);
            DfoServer.Game.Characters.Character role;
            using (var owner = new DfoServer.Game.Accounts.AccountOwners().Acquire(account, () => { }))
                role = new DfoServer.Persistence.CharacterRepository(fixture.Database).Create(owner,
                    new(label + "Gate" + scenario, point, point) { Level = (byte)level, CompletedQuests = [entryQuest, prerequisite] });
            using var peer = Open(fixture, account, gamePort: endpoints[channel], channelNumber: channel);
            if (scenario >= 2) peer.Send(8, [255, 255, 2]);
            peer.Send(1278, []); peer.Expect(1, 1278, [1]);
            if (scenario < 2)
                fixture.Sql("DELETE FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",
                    role.Id, scenario == 0 ? entryQuest : prerequisite);
            else
                fixture.Sql("UPDATE characters SET level=$p1 WHERE id=$p0", role.Id, level - 1);
            string before = fixture.Snapshot("SELECT town,area,x,y,return_town,return_area,revision FROM characters WHERE id=$p0", role.Id);
            peer.Send(4, [0, 0]);
            peer.Expect(1, 4, [0, 96, 0]);
            peer.Send(1278, []); peer.Expect(1, 1278, [1]);
            That(fixture.Snapshot("SELECT town,area,x,y,return_town,return_area,revision FROM characters WHERE id=$p0", role.Id) == before,
                label + "拒绝过期名册、缺前置或等级越界，不保存落点/返回地区且连接仍可处理后续请求");
        }
    }
}
