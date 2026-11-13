using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class ExpertJobActionTcpTests
{
    static async Task VerifyDungeonExpertJobsAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options with { DevelopmentDungeonEntryProbe = DfoServer.Development.Dungeons.DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "expert-job-dungeon"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var items = resources.ItemResources.Items;
        var scripts = resources.ItemResources.ExpertJobs;
        scripts.TryRepair(3, 2, out _, out int grade2Max);
        uint upgradedMax = checked((uint)grade2Max);
        int machineExperience = 200;
        var targetTemplate = items.Get(10400);
        var machineTemplate = items.Get(10114);
        var cardRecipe = scripts.KnownCards(1).Order().Select(id => scripts.TryCardRecipe(id, out var row) ? row : null)
            .First(row => row is { Rarity: 1 } && items.Get(row.CardId).MinimumLevel <= 50 && resources.ItemResources.Enchantments.Get(row.BeadId) is { } definition &&
                definition.Targets.Contains((targetTemplate.Kind, targetTemplate.EquipmentSlot)))!;
        short cardSlot = ItemSlotBoundService.Range(items.Get(cardRecipe.CardId).Kind, InventorySnapshot.Empty).Start;
        short scrollSlot = checked((short)(ItemSlotBoundService.Range(items.Get(2600141).Kind, InventorySnapshot.Empty).Start + 1));
        ushort machineId, enchanterId;
        short enchanterToolSlot;
        byte[] enchanterInfo;
        var craftCases = new List<(byte Job, ushort Slot, int RecipeId, short ToolSlot, byte[] Info)>();

        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var profile = resources.Characters.Get(0);
            var catalog = resources.Learning;
            var learning = DfoServer.Game.Skills.SkillLearning.Initialize(0, profile.Skills, catalog).State! with
            { Experience = SelfTestCatalogs.Growth(resources.Archive, catalog).GetLevelStartExperience(50) };
            learning = DfoServer.Game.Skills.SkillPointLedger.Synchronize(0, 0, 50, learning, catalog);
            var machine = repository.Create(owner, new("DungeonDisj", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 50, AcceptedStoryLevel = 1, Skills = profile.Skills, InitialLearning = learning,
                DungeonState = DfoServer.Game.Characters.CharacterDungeonState.NewOrdinary,
                Inventory = new([new(0, Bag(3), InventoryRewardGrantService.Create(machineTemplate)),
                    new(0, Bag(4), InventoryRewardGrantService.Create(machineTemplate))], Gold: 500000)
            });
            machineId = machine.Id;

            var enchanter = repository.Create(owner, new("DungeonEnch", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 50, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                DungeonState = DfoServer.Game.Characters.CharacterDungeonState.NewOrdinary,
                Inventory = new([new(0, Bag(3), InventoryRewardGrantService.Create(targetTemplate)),
                    new(0, Bag(4), InventoryRewardGrantService.Create(targetTemplate)),
                    new(0, Bag(cardSlot), InventoryRewardGrantService.Create(items.Get(cardRecipe.CardId))),
                    new(0, Bag(scrollSlot), InventoryRewardGrantService.Create(items.Get(2600141)))], Gold: 1000000)
            });
            enchanterId = enchanter.Id;
            var enchanterRewards = cardRecipe.Materials.Where(row => row.ItemId > 0)
                .Select(row => new InventoryReward(row.ItemId, row.Count))
                .Concat([new(2600482, 1), new(3033, 30), new(2610030, 3), new(3227, 10)]).ToArray();
            var enchanterGrant = InventoryRewardGrantService.TryGrantBatch(enchanter, enchanterRewards, items,
                resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
            enchanter = repository.CommitInventory(owner, enchanter, enchanterGrant.Plan!);
            enchanterToolSlot = enchanter.Inventory.Items.Single(item => item.Core.ItemId == 2600482).Position.Index;

            foreach (var (job, toolId) in new (byte Job, int ToolId)[] { (2, 2600463), (4, 2600474) })
            {
                int level = scripts.Level(job, 10000);
                var recipe = scripts.UnlockedRecipes(job, level).Select(id => scripts.TryRecipe(job, id, out var row) ? row : null)
                    .First(row => row is not null && row.Materials.Length > 0 && row.Materials.All(material => material.ItemId > 0) &&
                        scripts.Outcome(job, row, level).SuccessPercent == 100)!;
                var created = repository.Create(owner, new("DungeonJob" + job, resources.Towns.Room, resources.Towns.InitialReturn)
                {
                    Level = 50, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                    DungeonState = DfoServer.Game.Characters.CharacterDungeonState.NewOrdinary,
                    Inventory = new([new(0, Bag(3), InventoryRewardGrantService.Create(targetTemplate))], Gold: 1000000)
                });
                var rewards = recipe.Materials.Select(row => new InventoryReward(row.ItemId, checked(row.Count * 2)))
                    .Append(new(toolId, 1)).ToArray();
                var grant = InventoryRewardGrantService.TryGrantBatch(created, rewards, items,
                    resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
                created = repository.CommitInventory(owner, created, grant.Plan!);
                short toolSlot = created.Inventory.Items.Single(item => item.Core.ItemId == toolId).Position.Index;
                fixture.Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness,learned_recipes) VALUES({created.Id},'{fixture.Options.DevelopmentAccount}',{job},10000,'[{recipe.RecipeId}]')");
                var loaded = repository.LoadCharacter(owner, created.Slot);
                craftCases.Add((job, created.Slot, recipe.RecipeId, toolSlot,
                    DfoServer.Network.Builders.ExpertJobPackets.Info(loaded, scripts, includeQualifications: true)));
            }
            fixture.Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness) VALUES({machineId},'{fixture.Options.DevelopmentAccount}',3,200)");
            fixture.Sql($"INSERT INTO character_expert_meta(character_id,account,giveup_state,machine_grade,machine_endurance) VALUES({machineId},'{fixture.Options.DevelopmentAccount}',0,1,290)");
            fixture.Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness,learned_recipes) VALUES({enchanterId},'{fixture.Options.DevelopmentAccount}',1,10000,'[]')");
            fixture.Sql($"INSERT INTO character_expert_meta(character_id,account,giveup_state,machine_grade,machine_endurance) VALUES({enchanterId},'{fixture.Options.DevelopmentAccount}',0,1,290)");
            enchanterInfo = DfoServer.Network.Builders.ExpertJobPackets.Info(repository.LoadCharacter(owner, 1), scripts, includeQualifications: true);
        }
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        File.WriteAllText(Path.Combine(fixture.Options.DataDirectory, "expert-job-compatibility.json"),
            "{\"SelfServiceRewardPercent\":100,\"EnchantSuccessPercent\":100}");
        await fixture.StartAsync();

        using (var peer = Open(fixture))
        {
            peer.Select(0, expertJobInfo: MachineInfo(290));
            FormalDungeonTcpTests.EntryTown(peer);
            var map = FormalDungeonTcpTests.Enter(peer);
            string loadingBefore = Snapshot();
            UseMachine(peer, 3, machineId); peer.Expect(1, 235, [0, 19, 0]);
            peer.Send(236); peer.Expect(1, 236, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == loadingBefore, "副本加载阶段拒绝个人分解与分解机修理");

            FormalDungeonTcpTests.Load(peer);
            string dungeonBefore = Snapshot();
            peer.Send(598, new byte[15]); peer.Expect(1, 598, [0, 19, 0]);
            peer.Send(234, new byte[15]); peer.Expect(1, 234, [0, 19, 0]);
            peer.Send(599, Build(w => w.Write(machineId))); peer.Expect(1, 599, [0, 19, 0]);
            peer.Send(239); peer.Expect(1, 239, [0, 19, 0]);
            UseMachine(peer, 3, enchanterId); peer.Expect(1, 235, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == dungeonBefore, "战斗阶段拒绝摆摊开店、进店、跨玩家分解和放弃副职业");

            UseMachine(peer, 3, machineId);
            byte[] ack = peer.Expect(1, 235).Body;
            That(ack[0] == 1 && U16(ack, 1) == 3 && U16(ack, 4) == machineId && U32(ack, ack.Length - 4) == 289,
                "战斗阶段个人分解机成功分解装备并扣减耐久");
            if (ReadMachineProgress(peer, fixture, machineId, ref machineExperience) is { } disjointActor)
                That(ActorSceneState(disjointActor) == 1, "战斗阶段熟练度变化通知保持副本场景状态");
            peer.Barrier();

            peer.Send(236);
            peer.Expect(0, 14);
            That(ActorSceneState(peer.Expect(0, 2).Body) == 1, "战斗阶段修理分解机Actor通知保持副本场景状态");
            peer.Expect(0, 205, MachineInfo(300));
            byte[] repairAck = peer.Expect(1, 236).Body;
            That(repairAck[0] == 1 && U32(repairAck, 5) == 300, "战斗阶段修理分解机补满300耐久");
            peer.Barrier();

            peer.Send(240);
            peer.Expect(0, 14);
            That(ActorSceneState(peer.Expect(0, 2).Body) == 1, "战斗阶段升级分解机Actor通知保持副本场景状态");
            peer.Expect(0, 205, Build(w => { w.Write((byte)0); w.Write((byte)3); w.Write(2u); w.Write(upgradedMax); }));
            byte[] upgradeAck = peer.Expect(1, 240).Body;
            That(upgradeAck[0] == 1 && U32(upgradeAck, 5) == 2 && U32(upgradeAck, 9) == upgradedMax, "战斗阶段升级分解机升至2级并恢复最大耐久");
            peer.Barrier();

            void ClearRoom(byte[] roomMap)
            {
                var rows = Enumerable.Range(0, roomMap[18]).Select(index => 19 + index * 22)
                    .Where(offset => roomMap[offset + 13] != 9 && roomMap[offset + 14] == 0)
                    .OrderBy(offset => roomMap[offset + 13] == 3);
                foreach (int offset in rows)
                {
                    ushort key = U16(roomMap, offset + 6);
                    bool boss = roomMap[offset + 13] == 3;
                    byte[] report = FormalDungeonTcpTests.Death(key, peer.Id, roomMap[offset + 12]);
                    if (boss) { report[55] = 1; FormalDungeonTcpTests.SealDeath(report); }
                    peer.Send(39, report);
                }
                PartyDungeonTcpTests.Drain(peer);
            }

            ClearRoom(map);
            foreach (var target in new (byte X, byte Y)[] { (1, 1), (2, 1), (2, 0), (3, 0) })
            {
                byte[] move = new byte[100];
                move[0] = target.X;
                move[1] = target.Y;
                peer.Send(45, move);
                peer.Expect(1, 45, [1]);
                map = peer.Expect(0, 29).Body;
                FormalDungeonTcpTests.Load(peer, fatigue: false);
                peer.Expect(0, 36);
                ClearRoom(map);
            }

            string settlementBefore = Snapshot();
            peer.Send(598, new byte[15]); peer.Expect(1, 598, [0, 19, 0]);
            peer.Send(239); peer.Expect(1, 239, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == settlementBefore, "结算阶段仍拒绝摆摊开店与放弃副职业");

            UseMachine(peer, 4, machineId);
            ack = peer.Expect(1, 235).Body;
            That(ack[0] == 1 && U16(ack, 1) == 4 && U16(ack, 4) == machineId && U32(ack, ack.Length - 4) == upgradedMax - 1,
                "结算阶段个人分解机成功分解装备并扣减耐久");
            if (ReadMachineProgress(peer, fixture, machineId, ref machineExperience) is { } settlementActor)
                That(ActorSceneState(settlementActor) == 1, "结算阶段熟练度变化通知保持副本场景状态");
            peer.Send(600);
            peer.Barrier();
        }

        using (var peer = Open(fixture))
        {
            peer.Select(1, expertJobInfo: enchanterInfo);
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer);
            FormalDungeonTcpTests.Load(peer);

            string before = Snapshot();
            peer.Send(598, new byte[15]); peer.Expect(1, 598, [0, 19, 0]);
            peer.Send(601, Build(w =>
            {
                w.Write(machineId); w.Write(cardRecipe.RecipeId); w.Write((byte)2);
                w.Write((byte)0); w.Write((ushort)3); w.Write((byte)0); w.Write((ushort)cardSlot);
            }));
            peer.Expect(1, 601, [0, 19, 0]);
            peer.Send(239); peer.Expect(1, 239, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before, "战斗阶段附魔师拒绝摆摊、跨玩家附魔与放弃副职业");

            peer.Send(44, Build(w =>
            {
                w.Write((ushort)scrollSlot); w.Write((byte)0); w.Write(uint.MaxValue); w.Write(2600141); w.Write(0u);
            }));
            peer.Expect(1, 44, Build(w =>
            {
                w.Write((byte)1); w.Write((ushort)scrollSlot); w.Write((byte)0); w.Write(uint.MaxValue); w.Write(2600141);
            }));
            peer.Expect(0, 205);
            peer.Barrier();

            peer.Send(238, Build(w => { w.Write(2600141); w.Write((ushort)1); w.Write(ushort.MaxValue); }));
            peer.Expect(0, 14);
            That(ActorSceneState(peer.Expect(0, 2).Body) == 1, "战斗阶段附魔师个人制作Actor通知保持副本场景状态");
            peer.Expect(0, 205);
            That(peer.Expect(1, 238).Body[0] == 1, "战斗阶段附魔师个人制作成功");
            peer.Barrier();

            peer.Send(601, Build(w =>
            {
                w.Write(enchanterId); w.Write(cardRecipe.RecipeId); w.Write((byte)2);
                w.Write((byte)0); w.Write((ushort)3); w.Write((byte)0); w.Write((ushort)cardSlot);
            }));
            peer.Expect(0, 14);
            That(ActorSceneState(peer.Expect(0, 2).Body) == 1, "战斗阶段附魔师个人附魔Actor通知保持副本场景状态");
            peer.Expect(0, 205);
            byte[] enchantAck = peer.Expect(1, 601).Body;
            That(enchantAck[0] == 1 && enchantAck[1] == 1 && U32(enchantAck, 7) == 289,
                "战斗阶段附魔师个人卡片附魔成功并扣减耐久");
            peer.Barrier();

            peer.Send(416, Build(w => { w.Write((byte)0); w.Write(enchanterToolSlot); w.Write((byte)0); w.Write((ushort)4); }));
            That(peer.Expect(1, 416).Body[0] == 1, "战斗阶段附魔师个人灵魂提取成功");
            That(ActorSceneState(peer.Expect(0, 2).Body) == 1, "战斗阶段附魔师个人灵魂提取Actor通知保持副本场景状态");
            peer.Expect(0, 205);
            peer.Barrier();

            peer.Send(611);
            peer.Expect(0, 14);
            That(ActorSceneState(peer.Expect(0, 2).Body) == 1, "战斗阶段修理附魔机Actor通知保持副本场景状态");
            peer.Expect(0, 205);
            peer.Expect(1, 611, [1]);
            peer.Barrier();
        }

        foreach (var row in craftCases)
        {
            using var peer = Open(fixture);
            peer.Select(checked((byte)row.Slot), expertJobInfo: row.Info);
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer);
            FormalDungeonTcpTests.Load(peer);

            string before = Snapshot();
            peer.Send(598, new byte[15]); peer.Expect(1, 598, [0, 19, 0]);
            peer.Send(239); peer.Expect(1, 239, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before, $"战斗阶段副职业{row.Job}拒绝摆摊开店与放弃副职业");

            peer.Send(238, Build(w => { w.Write(row.RecipeId); w.Write((ushort)1); w.Write(ushort.MaxValue); }));
            peer.Expect(0, 14);
            That(ActorSceneState(peer.Expect(0, 2).Body) == 1, $"战斗阶段副职业{row.Job}个人制作Actor通知保持副本场景状态");
            peer.Expect(0, 205);
            That(peer.Expect(1, 238).Body[0] == 1, $"战斗阶段副职业{row.Job}个人制作成功");
            peer.Barrier();

            peer.Send(416, Build(w => { w.Write((byte)0); w.Write(row.ToolSlot); w.Write((byte)0); w.Write((ushort)3); }));
            That(peer.Expect(1, 416).Body[0] == 1, $"战斗阶段副职业{row.Job}个人提取成功");
            That(ActorSceneState(peer.Expect(0, 2).Body) == 1, $"战斗阶段副职业{row.Job}个人提取Actor通知保持副本场景状态");
            peer.Expect(0, 205);
            peer.Barrier();
        }

        await fixture.StopAsync();

        static byte ActorSceneState(byte[] actor) => actor[132 + checked((int)U32(actor, 123))];
        string Snapshot() => JsonSerializer.Serialize(new[]
        {
            fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid"),
            fixture.Query("SELECT * FROM character_expert_job ORDER BY character_id"),
            fixture.Query("SELECT * FROM character_expert_meta ORDER BY character_id"),
            fixture.Query("SELECT id,revision,gold FROM characters ORDER BY id"),
            fixture.Query("SELECT * FROM account_currency_balances ORDER BY account,currency_kind")
        });
    }
}
