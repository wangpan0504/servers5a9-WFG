using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Skills;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ExpertJobLearningTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await ExpertJobAutoLearnTcpTests.RunAsync(options, root, resources);
        ExpertJobLearningTests.Run(resources.Towns, resources.ItemResources, resources.ItemResources.ExpertJobs);
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "expert-job-learning"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var items = resources.ItemResources.Items;
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        short start = ItemSlotBoundService.Range(items.Get(10015129).Kind, InventorySnapshot.Empty).Start;
        ushort cardSlot = 0;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var crafter = repository.Create(owner, new("LearnTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 50, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Slot(0), InventoryRewardGrantService.Create(items.Get(2600141))),
                    new(0, Slot(1), InventoryRewardGrantService.Create(items.Get(2600141))),
                    new(0, Slot(2), InventoryRewardGrantService.Create(items.Get(2600142))),
                    new(0, Slot(3), InventoryRewardGrantService.Create(items.Get(10015130)))], Gold: 999)
            });
            var supplies = InventoryRewardGrantService.TryGrantBatch(crafter, [new(3033, 30), new(2610030, 3), new(3619, 2), new(3227, 10), new(3166, 40), new(2610039, 6)], items,
                resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
            That(supplies.Accepted, "制作TCP准备真实钱包黑色晶体和实体魔力结晶");
            var supplied = repository.CommitInventory(owner, crafter, supplies.Plan!);
            cardSlot = (ushort)supplied.Inventory.Items.Single(item => item.Core.ItemId == 3619).Position.Index;
            var learning = SkillLearning.Initialize(0, resources.Characters.Get(0).Skills, resources.Learning).State!;
            var candidate = repository.Create(owner, new("GrantTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 20, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                InitialLearning = SkillPointLedger.Synchronize(0, 0, 20,
                    learning with { Experience = growth.GetLevelStartExperience(20) }, resources.Learning),
                Inventory = new([], Gold: 10_000)
            });
            var material = InventoryRewardGrantService.TryGrantBatch(candidate, [new(3037, 100)], items,
                resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
            That(material.Accepted, "授职TCP准备真实无色钱包材料");
            repository.CommitInventory(owner, candidate, material.Plan!);
        }
        fixture.Sql("INSERT INTO character_expert_job(character_id,account,job_type,expertness) SELECT id,account,1,0 FROM characters WHERE name='LearnTcp'");
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            string before = Snapshot();
            Use(peer, 0, 2600141); Ack(peer, 0, 2600141, false);
            That(Snapshot() == before, "未选角完整拒绝学习且不扣图");
            peer.Select(expertJobInfo: Info([], [0]));
            before = Snapshot();
            Craft(peer); peer.Expect(1, 238, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == before, "材料齐备但未学习制作图不能伪造238合成");
            Use(peer, 3, 10015130); Ack(peer, 3, 10015130, false); peer.Barrier();
            That(Snapshot() == before, "真实TCP拒绝未达到副职业等级的设计图");
            fixture.Sql("CREATE TRIGGER fail_learning BEFORE UPDATE OF learned_recipes ON character_expert_job BEGIN SELECT RAISE(ABORT,'learning failure'); END");
            Use(peer, 0, 2600141); peer.Closed();
            That(Snapshot() == before, "TCP末段资格写失败回滚源图与revision，不先发成功ACK");
            fixture.Sql("DROP TRIGGER fail_learning");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(expertJobInfo: Info([], [0]));
            Use(peer, 0, 2600141); Ack(peer, 0, 2600141, true);
            peer.Expect(0, 205, Info([2600141, 10015129], [])); peer.Barrier();
            string before = Snapshot();
            Use(peer, 1, 2600141); Ack(peer, 1, 2600141, false); peer.Barrier();
            That(Snapshot() == before, "重复学习不消费第二张图、不追加资格或再次增版本");
            Use(peer, 2, 2600142); Ack(peer, 2, 2600142, true);
            peer.Expect(0, 205, Info([2600141, 2600142, 10015129], [])); peer.Barrier();
            before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_craft_tcp BEFORE UPDATE OF expertness ON character_expert_job BEGIN SELECT RAISE(ABORT,'craft tcp failure'); END");
            Craft(peer); peer.Closed();
            That(Snapshot() == before, "真实238经验写入失败时钱包、材料、产物和版本一起回滚");
            fixture.Sql("DROP TRIGGER fail_craft_tcp");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(expertJobInfo: Info([2600141, 2600142, 10015129], [0])); peer.Barrier();
            long revision = (long)fixture.Query("SELECT revision FROM characters WHERE name='LearnTcp'")[0][0]!;
            Craft(peer);
            peer.Expect(0, 14); peer.Expect(0, 2); peer.Expect(0, 205, Info([2600141, 2600142, 10015129], []));
            peer.Expect(1, 238, Build(writer =>
            { writer.Write((byte)1); writer.Write((byte)1); writer.Write(2610034); writer.Write(2); writer.Write(2u); writer.Write(0u); writer.Write((byte)0); }));
            peer.Barrier();
            That(fixture.Query("SELECT revision FROM characters WHERE name='LearnTcp'") is [[long after]] && after == revision + 1,
                "真实合成只推进一次revision，不拆分材料与经验事务");
            string before = Snapshot();
            Craft(peer); peer.Expect(1, 238, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == before, "同数量重发因剩余材料不足拒绝，不重复发两件产物");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(expertJobInfo: Info([2600141, 2600142, 10015129], [0])); peer.Barrier();
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var restored = repository.LoadCharacter(owner, 0);
            That(restored.ExpertJobRecipes.SetEquals([2600141, 2600142]) && restored.Inventory.Find(Slot(0)) is null &&
                restored.Inventory.Find(Slot(2)) is null && restored.Inventory.Find(Slot(1)) is not null &&
                restored.Inventory.Gold == 999 && restored.ExpertJobExperience is >= 0 and <= 2 &&
                restored.Inventory.Items.Single(item => item.Core.ItemId == 2610034).Core.Count == 2 &&
                restored.Inventory.Items.Single(item => item.Core.ItemId == 2610030).Core.Count == 1 &&
                CurrencyWallet.Get(restored.Inventory, CurrencyKind.BlackCube) == 10,
                "重连投影完整资格，普通后续205不重复追加稀有度，图和资格同事务持久化");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(expertJobInfo: Info([2600141, 2600142, 10015129], [0])); peer.Barrier();
            string before = Snapshot();
            Bead(peer, 0); peer.Expect(1, 238, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == before, "238卡片槽配方0不再扣卡伪造无产物成功");
            fixture.Sql("CREATE TRIGGER fail_bead_tcp BEFORE UPDATE OF expertness ON character_expert_job BEGIN SELECT RAISE(ABORT,'bead tcp failure'); END");
            Bead(peer, 10015129); peer.Closed();
            That(Snapshot() == before, "宝珠TCP事务尾失败完整回滚卡、两组材料、成品与经验");
            fixture.Sql("DROP TRIGGER fail_bead_tcp");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(expertJobInfo: Info([2600141, 2600142, 10015129], [0])); peer.Barrier();
            Bead(peer, 10015129);
            peer.Expect(0, 14); peer.Expect(0, 2); peer.Expect(0, 205, Info([2600141, 2600142, 10015129], []));
            peer.Expect(1, 238, Build(writer =>
            { writer.Write((byte)1); writer.Write((byte)1); writer.Write(2600313); writer.Write(2); writer.Write(2u); writer.Write(0u); writer.Write((byte)0); }));
            peer.Barrier();
            string before = Snapshot();
            Bead(peer, 10015129); peer.Expect(1, 238, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == before, "卡片与材料耗尽后重发不重复创建宝珠");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(expertJobInfo: Info([2600141, 2600142, 10015129], [0])); peer.Barrier();
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var restored = repository.LoadCharacter(owner, 0);
            That(restored.Inventory.Items.Single(item => item.Core.ItemId == 2600313).Core.Count == 2 &&
                restored.Inventory.Items.All(item => item.Core.ItemId is not (3619 or 3227 or 3166 or 2610039)) &&
                restored.ExpertJobExperience is >= 6 and <= 14, "真实宝珠成品与完整材料扣除、经验重连一致");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(1);
            byte[] plain = entry.Single(packet => packet.Kind == 0 && packet.Opcode == 2 && packet.Body[0] == 0).Body;
            var previous = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            byte[] previousHistory = peer.CompletedQuestHistory;
            peer.Send(31, [31, 0, 150, 10]);
            That(peer.Expect(1, 31).Body[0] == 1, "真实2710先正常接取");
            peer.Expect(0, 21);
            byte[] history = peer.Expect(0, 356).Body;
            That(history.Length == 30004 && U32(history, 0) == 30000 && history.AsSpan().SequenceEqual(previousHistory),
                "授职接取后恢复原真实完成表，不凭空完成任务或留下未读取的356");
            peer.Barrier();
            peer.Send(34, [34, 0, 150, 10, 255, 255, 1, 0, 255, 255]);
            byte[] actor = peer.Expect(0, 2).Body;
            That(actor.Length == plain.Length && actor.Zip(plain).Count(pair => pair.First != pair.Second) == 1 &&
                actor.Zip(plain).Single(pair => pair.First != pair.Second) == ((byte)3, (byte)0),
                "授职成功先更新Actor副职业类型，不能仅下发205");
            var granted = SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body);
            That(granted.All(tree => tree.Skills.Any(skill => skill.Id == 194 && skill.Level == 1)) &&
                granted.Zip(previous).All(pair => pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp),
                "ACK34前完整双树已包含EXJ194且余额不变");
            peer.Expect(1, 34, Build(writer =>
            {
                writer.Write((byte)1); writer.Write((ushort)2710); writer.Write((byte)0); writer.Write(0u);
                writer.Write((byte)1); writer.Write((byte)0);
                writer.Write((ushort)ItemSlotBoundService.CurrencySlot(CurrencyKind.ClearCube)!.Value); writer.Write(100u);
                writer.Write((byte)20); writer.Write((byte)3); writer.Write((byte)0); writer.Write((byte)0);
            }));
            peer.Expect(0, 14); peer.Expect(0, 21); peer.Expect(0, 356);
            peer.Expect(0, 205, [0, 3, 1, 0, 0, 0, 44, 1, 0, 0]); peer.Barrier();
            string saved = Snapshot();
            peer.Send(34, [34, 0, 150, 10, 255, 255, 1, 0, 255, 255]);
            peer.Expect(1, 34, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == saved, "重发已授职请求不重复扣材料或刷新奖励");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1, prepared: true, expertJobInfo: [0, 3, 1, 0, 0, 0, 44, 1, 0, 0]);
            That(SkillPacketsTests.DecodeInitialized(peer.SkillInfo).All(tree => tree.Skills.Any(skill => skill.Id == 194)),
                "授职赠技重连恢复，无需补领任务");
            peer.Send(239, []);
            That(SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body).All(tree => tree.Skills.All(skill => skill.Id != 194)),
                "放弃时先移除双树副职业赠技");
            peer.Expect(0, 2);
            peer.Expect(1, 239, [1, 40, 35, 0, 0, 1, 0]);
            peer.Expect(0, 574); peer.Expect(0, 21); peer.Expect(0, 356); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_expert_job WHERE character_id=$p0", peer.Id) is [[0L]],
                "放弃资格、赠技和费用同次提交");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "学习TCP数据库结构和外键完整");

        InventorySlot Slot(int offset) => new(InventoryListType.Main, (short)(start + offset));
        void Bead(TcpPeer peer, int recipe) => peer.Send(238, Build(writer =>
        { writer.Write(recipe); writer.Write((ushort)2); writer.Write(cardSlot); }));
        void Craft(TcpPeer peer) => peer.Send(238, Build(writer =>
        { writer.Write(2600141); writer.Write((ushort)2); writer.Write(ushort.MaxValue); }));
        void Use(TcpPeer peer, int offset, int id) => peer.Send(44, Build(writer =>
        { writer.Write((ushort)(start + offset)); writer.Write((byte)0); writer.Write(uint.MaxValue); writer.Write(id); writer.Write(0u); }));
        void Ack(TcpPeer peer, int offset, int id, bool accepted) => peer.Expect(1, 44, Build(writer =>
        { writer.Write((byte)(accepted ? 1 : 0)); writer.Write((ushort)(accepted ? start + offset : 123)); writer.Write((byte)0); writer.Write(uint.MaxValue); writer.Write(id); }));
        string Snapshot() => JsonSerializer.Serialize(new[] { fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid"),
            fixture.Query("SELECT * FROM character_expert_job"), fixture.Query("SELECT id,revision FROM characters"), fixture.Query("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") });
    }

    // 独立按已核实的客户端读序编码，不调用被测生产builder。
    static byte[] Info(int[] recipes, byte[] rarities) => Build(writer =>
    {
        recipes = recipes.Append(10015129).Distinct().Order().ToArray();
        writer.Write((byte)0); writer.Write((byte)1); writer.Write((byte)recipes.Length);
        foreach (int id in recipes) writer.Write(id);
        writer.Write((byte)rarities.Length); writer.Write(rarities); writer.Write(1u); writer.Write(300u);
    });
}
