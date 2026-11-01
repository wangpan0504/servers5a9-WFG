using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ExpertJobActionTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        ExpertJobActionTests.Run(resources.Towns, resources.ItemResources);
        DevelopmentGmExtendedTests.RunResources(resources.Towns, resources.ItemResources, resources.Archive,
            resources.Quests, resources.Characters);
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "expert-job-actions"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var items = resources.ItemResources.Items;
        var scripts = resources.ItemResources.ExpertJobs;
        var targetTemplate = items.Get(10400);
        var machineTemplate = items.Get(10114);
        var epicPiece = resources.ItemResources.EpicPieces.Entries.First();
        That(machineTemplate.Rarity > 1, "自用零件回归使用真实稀有度大于1的装备");
        var recipe = scripts.KnownCards(1).Order().Select(id => scripts.TryCardRecipe(id, out var row) ? row : null)
            .First(row => row is { Rarity: 1 } && items.Get(row.CardId).MinimumLevel <= 50 && resources.ItemResources.Enchantments.Get(row.BeadId) is { } definition &&
                definition.Targets.Contains((targetTemplate.Kind, targetTemplate.EquipmentSlot)))!;
        int startingExperience = Enumerable.Range(0, 2000).First(xp => scripts.Level(1, xp) == 7) - 1;
        short cardSlot = ItemSlotBoundService.Range(items.Get(recipe.CardId).Kind, InventorySnapshot.Empty).Start;
        ushort machineId, enchanterId;
        uint fragmentCount = 0;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var machine = repository.Create(owner, new("MachineTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 50, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Bag(3), InventoryRewardGrantService.Create(machineTemplate)),
                    new(0, Bag(4), InventoryRewardGrantService.Create(machineTemplate)),
                    new(0, Bag(5), InventoryRewardGrantService.Create(machineTemplate)),
                    new(0, Bag(6), InventoryRewardGrantService.Create(items.Get(epicPiece.EquipmentId))),
                    new(0, Bag(7), InventoryRewardGrantService.Create(items.Get(epicPiece.EquipmentId)))], Gold: 999)
            });
            machineId = machine.Id;
            var enchanter = repository.Create(owner, new("EnchantTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 50, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Bag(3), InventoryRewardGrantService.Create(targetTemplate)),
                    new(0, Bag(cardSlot), InventoryRewardGrantService.Create(items.Get(recipe.CardId)))], Gold: 1000000)
            });
            enchanterId = enchanter.Id;
            var grant = InventoryRewardGrantService.TryGrantBatch(enchanter,
                recipe.Materials.Where(row => row.ItemId > 0).Select(row => new InventoryReward(row.ItemId, row.Count)).ToArray(),
                items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
            repository.CommitInventory(owner, enchanter, grant.Plan!);
        }
        fixture.Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness) SELECT id,account,3,51 FROM characters WHERE id={machineId}");
        fixture.Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness,learned_recipes) SELECT id,account,1,{startingExperience},'[]' FROM characters WHERE id={enchanterId}");
        fixture.Sql($"INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) VALUES({enchanterId},0,11009,1,'11223344556677889900112233445566')");
        fixture.Sql($"INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) VALUES({machineId},0,10021,1,'22334455667788990011223344556677')");
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        File.WriteAllText(Path.Combine(fixture.Options.DataDirectory, "expert-job-compatibility.json"),
            "{\"SelfServiceRewardPercent\":100,\"EnchantSuccessPercent\":100}");
        string machineDaily = "", enchanterDaily = "";
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(expertJobInfo: MachineInfo(300));
            machineDaily = Daily(machineId);
            peer.Send(598, Build(w => { w.Write((byte)3); w.Write(1); w.Write((byte)1); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); }));
            peer.Expect(1, 598, [0, 19, 0]);
            UseMachine(peer, 3, machineId); ReadMachine(peer, 3, 299);
            That(fixture.Query($"SELECT condition_state FROM character_active_main_quests WHERE character_id={machineId} AND quest_id=10021") is [[1L]],
                "错类型开店被拒绝且不残留open，个人分解仍可用但不冒充店铺教学操作");
            string before = Snapshot();
            UseMachine(peer, 3, machineId); peer.Expect(1, 235, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == before, "个人分解重放空源不重复发奖");
            UseMachine(peer, 4, enchanterId); peer.Expect(1, 235, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == before, "跨玩家机主明确拒绝，不扣源不扣另一角色耐久");
            peer.Send(598, Build(w => { w.Write((byte)0); w.Write(1); w.Write((byte)1); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); })); peer.Expect(1, 598, [1]);
            peer.Send(599, Build(w => w.Write(machineId)));
            peer.Expect(1, 599, [1, 0, 1, 0, 0, 0, 0, 43, 1, 0, 0]);
            UseMachine(peer, 4, machineId); ReadMachine(peer, 4, 298, tutorial: true);
            peer.Send(599, Build(w => w.Write(machineId)));
            peer.Expect(1, 599, [1, 0, 1, 0, 0, 0, 0, 42, 1, 0, 0]);
            That(fixture.Query($"SELECT condition_state FROM character_active_main_quests WHERE character_id={machineId} AND quest_id=10021") is [[0L]],
                "进行中10021教学经开店、进入本人分解机、真实分解后归零");
            UseMachine(peer, 6, machineId);
            var fragmentAck = peer.Expect(1, 235).Body;
            int fragmentAt = Enumerable.Range(0, fragmentAck[6]).Select(index => 7 + index * 10)
                .Single(at => U32(fragmentAck, at + 2) == epicPiece.PieceId);
            fragmentCount = U32(fragmentAck, fragmentAt + 6);
            That(U16(fragmentAck, fragmentAt) == ushort.MaxValue && fragmentCount is >= 90 and <= 110 &&
                U32(fragmentAck, fragmentAck.Length - 4) == 297, "玩家机器235保留碎片FFFF展示和金币耐久尾部");
            var fragmentBalance = peer.Expect(0, 1152).Body;
            That(fragmentBalance.Length == 13 && fragmentBalance[0] == 1 && U32(fragmentBalance, 5) == epicPiece.PieceId &&
                U32(fragmentBalance, 9) == fragmentCount, "机器碎片只入图鉴，1152同步真实绝对余额而非Main实体");
            peer.Expect(0, 2); peer.Expect(0, 205, MachineInfo(297)); peer.Barrier();
            before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_action_tcp BEFORE INSERT ON character_expert_meta BEGIN SELECT RAISE(ABORT,'machine tcp failure'); END");
            UseMachine(peer, 7, machineId); peer.Closed();
            That(Snapshot() == before, "机器TCP末段失败保持装备、产物、金币、经验和耐久，不发成功");
            fixture.Sql("DROP TRIGGER fail_action_tcp");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(expertJobInfo: MachineInfo(297)); peer.Barrier();
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var restored = repository.LoadCharacter(owner, 0);
            That(restored.Inventory.Find(Bag(3)) is null && restored.Inventory.Find(Bag(4)) is null &&
                restored.Inventory.Find(Bag(5)) is not null && restored.Inventory.Find(Bag(7)) is not null && restored.Inventory.Find(Bag(6)) is null &&
                restored.ExpertMachineEndurance == 297 && restored.Inventory.EpicPieces.Get(epicPiece.Index) == fragmentCount,
                "机器重连恢复真实消费、图鉴碎片与剩余耐久");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1, expertJobInfo: EnchantInfo(300));
            enchanterDaily = Daily(enchanterId);
            peer.Send(598, Build(w => { w.Write((byte)3); w.Write(1); w.Write((byte)4); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); })); ReadOpen(peer);
            string before = Snapshot();
            SendEnchant(peer, machineId); peer.Expect(1, 601, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == before, "附魔跨玩家请求不冒充本人结算");
            fixture.Sql("CREATE TRIGGER fail_enchant_tcp BEFORE INSERT ON character_expert_meta BEGIN SELECT RAISE(ABORT,'enchant tcp failure'); END");
            SendEnchant(peer, enchanterId); peer.Closed();
            That(Snapshot() == before, "附魔TCP末段失败回滚卡片、材料、装备、经验与耐久");
            fixture.Sql("DROP TRIGGER fail_enchant_tcp");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1, expertJobInfo: EnchantInfo(300));
            peer.Send(598, Build(w => { w.Write((byte)3); w.Write(1); w.Write((byte)4); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); })); ReadOpen(peer);
            SendEnchant(peer, enchanterId);
            Bytes(peer.Expect(0, 538).Body[22..], [3, 0, 1, 2], "本人附魔升至Lv7同步店铺新增资格");
            peer.Expect(0, 14); peer.Expect(0, 574); peer.Expect(0, 2); peer.Expect(0, 205, EnchantInfo(299, false));
            peer.Expect(1, 601, Build(w => { w.Write((byte)1); w.Write((byte)1); w.Write(0u); w.Write((byte)0); w.Write(299u); w.Write(7u); w.Write((uint)recipe.Rarity); }));
            peer.Barrier();
            peer.Send(599, Build(w => w.Write(enchanterId)));
            Bytes(peer.Expect(0, 538).Body[22..], [3, 0, 1, 2], "升级后进入本人店铺保留三项资格");
            peer.Expect(1, 599, Build(w => { w.Write((byte)1); w.Write((byte)3); w.Write(enchanterId); w.Write(299u); }));
            That(fixture.Query($"SELECT condition_state FROM character_active_main_quests WHERE character_id={enchanterId} AND quest_id=11009") is [[0L]],
                "真实附魔成功且教程状态改变时仍送完整ACK，不被旧回执守卫吞掉");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1, expertJobInfo: EnchantInfo(299)); peer.Barrier();
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var restored = repository.LoadCharacter(owner, 1);
            That(restored.Inventory.Find(Bag(cardSlot)) is null && restored.Inventory.Find(Bag(3))!.Core.EnchantCardId == recipe.CardId &&
                restored.ExpertMachineEndurance == 299, "卡片附魔重连保留真实效果、扣卡与耐久");
        }
        That(Daily(machineId)==machineDaily && Daily(enchanterId)==enchanterDaily,
            "分解和附魔操作只推进各自教程，并存每日身份和计数不受影响");
        await fixture.StopAsync();
        await ExpertJobCompoundTutorialTcpTests.RunAsync(options, root, resources);
        await ExpertJobExtractionTcpTests.RunAsync(options, root, resources);

        string Daily(ushort id)
        {
            var rows=fixture.Query("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id<>10021 AND quest_id<>11009 ORDER BY slot",id);
            That(rows is [[13036L,1636L,string]],"50级副职业教程与真实地区每日独立共存");
            return System.Text.Json.JsonSerializer.Serialize(rows);
        }

        void SendEnchant(TcpPeer peer, ushort actor) => peer.Send(601, Build(writer =>
        { writer.Write(actor); writer.Write(recipe.RecipeId); writer.Write((byte)2); writer.Write((byte)0); writer.Write((ushort)3); writer.Write((byte)0); writer.Write((ushort)cardSlot); }));
        byte[] EnchantInfo(uint endurance, bool qualifications = true) => Build(writer =>
        {
            bool upgraded = endurance < 300;
            int[] recipes = upgraded ? [10015129, recipe.RecipeId, 10015131] : [10015129, recipe.RecipeId];
            byte[] rarities = qualifications ? (upgraded ? [0, recipe.Rarity, 2] : [0, recipe.Rarity]) : [2];
            writer.Write((byte)0); writer.Write((byte)1); writer.Write((byte)recipes.Length);
            foreach (int id in recipes) writer.Write(id);
            writer.Write((byte)rarities.Length); writer.Write(rarities);
            writer.Write(upgraded ? 7u : 6u); writer.Write(endurance);
        });
        string Snapshot() => JsonSerializer.Serialize(new[] { fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid"),
            fixture.Query("SELECT * FROM character_expert_job ORDER BY character_id"), fixture.Query("SELECT * FROM character_expert_meta ORDER BY character_id"),
            fixture.Query("SELECT id,revision,gold FROM characters ORDER BY id"), fixture.Query("SELECT * FROM account_currency_balances ORDER BY account,currency_kind"),
            fixture.Query("SELECT * FROM account_epic_books ORDER BY account"), fixture.Query("SELECT count(*) FROM epic_piece_audit_log") });
        void ReadMachine(TcpPeer peer, ushort slot, uint endurance, bool tutorial = false)
        {
            byte[] ack = peer.Expect(1, 235).Body;
            int count = ack[6];
            That(ack.Length == 15 + count * 10 && ack[0] == 1 && U16(ack, 1) == slot && ack[3] == 0 && U16(ack, 4) == machineId &&
                U32(ack, ack.Length - 8) == 999 && U32(ack, ack.Length - 4) == endurance, "235完整源头、实际产物数组、绝对金币与耐久尾部");
            That(Enumerable.Range(0, count).Any(i => U32(ack, 9 + 10 * i) == 2610045 && U32(ack, 13 + 10 * i) == 1),
                "自用零件在已提交的ACK产物中以增量1出现");
            if (tutorial) peer.Expect(0, 574);
            peer.Expect(0, 2); peer.Expect(0, 205, MachineInfo(endurance));
            peer.Barrier();
        }
    }
    static void ReadOpen(TcpPeer peer)
    {
        Bytes(peer.Expect(0, 538).Body[22..], [2, 0, 1], "Lv6本人开店只有两项资格");
        Bytes(peer.Expect(0, 544).Body[8..], [2, 0, 1], "本人544不清空资格");
        peer.Expect(1, 598, [1]);
    }
    static InventorySlot Bag(int slot) => new(InventoryListType.Main, (short)slot);
    static void UseMachine(TcpPeer peer, ushort slot, ushort actor) => peer.Send(235, Build(writer =>
    { writer.Write(actor); writer.Write(slot); writer.Write((byte)0); }));
    static byte[] MachineInfo(uint endurance) => Build(writer =>
    { writer.Write((byte)0); writer.Write((byte)3); writer.Write(1u); writer.Write(endurance); });
}
