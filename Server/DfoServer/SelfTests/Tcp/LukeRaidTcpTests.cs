using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.AntonRaidTcpTests;

namespace DfoServer.Testing;

// 卢克沿正式招募owner与真实门票核对拒绝边界，不用普通卢克Run充当攻坚。
internal static class LukeRaidTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        LukeRaidResourceTests.RunResources(resources.Archive, RaidCatalog.Load(resources.Archive), resources.ItemResources.Items);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "luke-raid"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        List<TcpPeer> peers = [];
        try
        {
            for (int index = 0; index < 9; index++)
            {
                string account = "lukemember" + index;
                var point = new Location(38, 0, 629, 196);
                using (var owner = owners.Acquire(account, () => { }))
                {
                    var candidate = CharacterCreation.Prepare("LukM" + index, resources.Characters.Get(0), point, point, resources.Learning.Get);
                    var learning = SkillPointLedger.Synchronize(0, 0, 90, candidate.InitialLearning with
                        { Experience = resources.Growth.GetLevelStartExperience(90) }, resources.Learning);
                    var role = repository.Create(owner, candidate with
                        { Level = 90, AcceptedStoryLevel = 90, CompletedQuests = [2903, 4872], InitialLearning = learning });
                    if (index < 8)
                    {
                        var items = resources.ItemResources;
                        var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
                        InventoryService.Commit(repository, owner, role, inventory.Grant(role, [new(10159649, 2)]));
                    }
                }
                var peer = Open(fixture, account, gamePort: endpoints[20], channelNumber: 20);
                peers.Add(peer); peer.Select();
            }
            foreach (var peer in peers) Drain(peer);
            string before = fixture.Snapshot();
            string Assets() => string.Join('\n', new[] { "character_inventory_items", "character_fatigue", "character_dungeon_state",
                "character_dungeon_loot_receipts", "character_dungeon_growth_receipts" }
                .Select(table => fixture.Snapshot($"SELECT * FROM {table} ORDER BY 1,2")));
            string assets = Assets();
            That(fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10159649")
                is [[16L]], "八名卢克成员各持两张真实门票，不能靠全员无票掩盖扣费");
            var leader = peers[0]; var observer = peers[8];
            leader.Send(664, Build(w => { w.Write((byte)1); w.Write(Name("Luke")); w.Write(new byte[4]); }));
            var list = Until(leader, 0, 590).Body; uint key = U32(list, 4);
            That((key >> 16 & 255) == 20 && list[12 + checked((int)U32(list, 8))] == 1, "卢克招募key属于频道20且类型为1");
            Until(leader, 0, 591); Bytes(Until(leader, 1, 664).Body, Build(w => { w.Write((byte)1); w.Write(key); }), "卢克创建成功");
            leader.Send(666); Bytes(Until(leader, 1, 666).Body, [0, 19, 0], "资源最低一人已满足，正式流程仍拒绝");
            for (int index = 1; index < 8; index++)
            {
                var entrant = peers[index];
                entrant.Send(10, Peer(leader.Id, 123, true)); Bytes(Until(entrant, 1, 10).Body, [1], "卢克mode10申请");
                Until(leader, 0, 7);
                leader.Send(11, Peer(entrant.Id, 123, false)); Until(leader, 1, 11);
                foreach (var peer in peers) Drain(peer);
            }
            That(Information(observer, key, 1).Members.Length == 8, "真实卢克八人名册，非普通四人Party");
            observer.Send(10, Peer(leader.Id, 456, true));
            Bytes(Until(observer, 1, 10).Body, [0, 3, 0, 10], "第九人申请不能突破卢克八人上限");
            That(Information(observer, key, 1).Members.Length == 8, "超额申请不改原成员名册");
            for (int index = 0; index < 8; index++)
            {
                leader.Send(669, Build(w => { w.Write(0u); w.Write((uint)peers[index].Id); w.Write((uint)(index / 4 + 1)); }));
                Bytes(Until(leader, 1, 669).Body, [1], "卢克按两支四人分队编组");
            }
            leader.Send(1361, Build(w => { w.Write((byte)0); w.Write(key); }));
            LukeRaidStartTcpTests.AssertStartFlag(Until(leader, 1, 1361).Body, key, 0);
            var members = Information(observer, key, 1).Members;
            That(members.Count(m => m.Squad == 1) == 4 && members.Count(m => m.Squad == 2) == 4, "分队上限来自8人容量");
            foreach (var peer in new[] { leader, peers[1], observer })
                foreach (byte flag in new byte[] { 0, 1 })
                { peer.Send(658, [flag]); Bytes(Until(peer, 1, 658).Body, [1], "有票/无票费用报告不当扣费授权"); }
            for (int attempt = 0; attempt < 2; attempt++)
            { leader.Send(666); Bytes(Until(leader, 1, 666).Body, [0, 19, 0], "满团重复开战仍拒绝"); }
            peers[1].Send(666); Bytes(Until(peers[1], 1, 666).Body, [0, 8, 0], "非队长错误优先");
            leader.Send(668, Build(w => w.Write(key))); Bytes(Until(leader, 1, 668).Body, [0, 19, 0], "招募不能冒充正式重入");
            foreach (var peer in peers) Drain(peer);
            That(fixture.Snapshot() == before && Assets() == assets,
                "一人/八人重复开战及费用报告无准备/阶段/迁移通知，完整位置、票、疲劳、回执不变");
            peers[7].Send(3, [1]); Until(peers[7], 1, 3); peers[7].Closed();
            That(Information(observer, key, 1).Members.Length == 7 && Assets() == assets,
                "离开频道按原owner删除招募成员，不扣票或生成正式恢复资格");
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
        await fixture.StopAsync();
        var starts = fixture.Events().Where(e => e.GetProperty("Kind").GetString() == "raid_start_request")
            .Select(e => e.GetProperty("Detail")).Where(e => e.GetProperty("Reason").GetString() == "formal-raid-definition-unsupported").ToArray();
        That(starts.Length == 3 && starts.All(e => e.GetProperty("MissingDungeons").GetInt32() == 0 &&
            e.GetProperty("MissingMaps").GetInt32() == 0 && e.GetProperty("MissingItems").GetInt32() == 0 &&
            e.GetProperty("DefinitionIssues").GetInt32() == 3 && e.GetProperty("IssueSample").GetString()!.Contains("phase-dungeon-reference-missing")),
            "卢克START诊断来自共享装配结果：精确未闭合引用而非未准备资源或误报缺图");
    }
}
