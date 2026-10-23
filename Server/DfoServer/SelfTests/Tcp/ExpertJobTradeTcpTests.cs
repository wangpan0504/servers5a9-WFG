using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ExpertJobTradeTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "expert-job-trade"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var items = resources.ItemResources.Items;
        var target = items.Get(10400);
        var scripts = resources.ItemResources.ExpertJobs;
        var recipe = scripts.KnownCards(1).Order().Select(id => scripts.TryCardRecipe(id, out var row) ? row : null)
            .First(row => row is { Rarity: 1 } && items.Get(row.CardId).MinimumLevel <= 50 && resources.ItemResources.Enchantments.Get(row.BeadId) is { } definition &&
                definition.Targets.Contains((target.Kind, target.EquipmentSlot)))!;
        int startingExperience = Enumerable.Range(0, 2000).First(xp => scripts.Level(1, xp) == 5) - 1;
        short cardSlot = ItemSlotBoundService.Range(items.Get(recipe.CardId).Kind, InventorySnapshot.Empty).Start;
        ushort machineId = Create("alpha", "TradeMachine", 3), enchantId = Create("gamma", "TradeEnchant", 1);
        ushort customerId;
        using (var lease = new AccountOwners().Acquire("beta", () => { }))
        {
            var customer = repository.Create(lease, new("TradeBuyer", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 50, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Bag(3), InventoryRewardGrantService.Create(target)),
                    new(0, Bag(4), InventoryRewardGrantService.Create(target)), new(0, Bag(5), InventoryRewardGrantService.Create(target)),
                    new(0, Bag(cardSlot), InventoryRewardGrantService.Create(items.Get(recipe.CardId)) with { Value = 2 })], Gold: 1000000)
            });
            customerId = customer.Id;
            var materials = InventoryRewardGrantService.TryGrantBatch(customer,
                recipe.Materials.Where(row => row.ItemId > 0).Select(row => new InventoryReward(row.ItemId, checked(row.Count * 2))).ToArray(),
                items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
            repository.CommitInventory(lease, customer, materials.Plan!);
        }
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        File.WriteAllText(Path.Combine(fixture.Options.DataDirectory, "expert-job-compatibility.json"),
            "{\"SelfServiceRewardPercent\":100,\"EnchantSuccessPercent\":100}");
        await fixture.StartAsync();
        using var buyer = Open(fixture, "beta"); buyer.Select();
        using (var machine = Open(fixture, "alpha"))
        {
            machine.Select(expertJobInfo: MachineInfo(300));
            OpenShop(machine, 25); Drain(machine);
            buyer.Send(599, Build(w => w.Write(machineId)));
            That(Drain(buyer).Single(p => p.Opcode == 599).Body.SequenceEqual(new byte[] { 0, 19, 0 }), "隔离房间不能进入他人机器");
            machine.Send(1426); Drain(machine); OpenShop(machine, 25); Drain(machine);
            buyer.Send(1426);
            That(Drain(buyer).Any(p => p.Opcode == 538), "后进入同屏的顾客也能收到既有店铺展示");
            Drain(machine);
            Enter(buyer, machineId);
            long beforeBuyer = Gold(customerId), beforeOwner = Gold(machineId);
            UseMachine(buyer, machineId, 3);
            var packets = Drain(buyer); var ownerPackets = Drain(machine);
            var ack = packets.Single(p => p.Kind == 1 && p.Opcode == 235).Body;
            That(ack[0] == 1 && U16(ack, 4) == machineId && U32(ack, ack.Length - 4) == 299,
                "顾客235应答引用真实摊主与已提交耐久");
            That(!Enumerable.Range(0, ack[6]).Any(i => U32(ack, 9 + i * 10) == 2610045), "他人机器不给本人专属零件");
            That(Gold(customerId) == beforeBuyer - 25 && Gold(machineId) == beforeOwner + 25 && Endurance(machineId) == 299,
                "顾客扣费与摊主收入耐久同笔生效");
            That(ownerPackets.Any(p => p.Opcode == 14) && ownerPackets.Any(p => p.Opcode == 205), "摊主收到金币和机器状态投影");
            string before = Snapshot(); UseMachine(buyer, machineId, 3);
            That(Drain(buyer).Single(p => p.Opcode == 235).Body[0] == 0 && Snapshot() == before, "跨玩家重放不再次收费发奖");
            OpenShop(machine, 50); Drain(machine); Drain(buyer);
            UseMachine(buyer, machineId, 4);
            That(Drain(buyer).Single(p => p.Opcode == 235).Body[0] == 0 && Gold(customerId) == beforeBuyer - 25,
                "改价后旧报价拒绝，不按未确认新价扣费");
            Enter(buyer, machineId); machine.Send(600); Drain(machine); Drain(buyer);
            before = Snapshot(); UseMachine(buyer, machineId, 4);
            That(Drain(buyer).Single(p => p.Opcode == 235).Body[0] == 0 && Snapshot() == before, "关店后已进入顾客也不能交易");
            OpenShop(machine, 50); Drain(machine); Drain(buyer); Enter(buyer, machineId);
            UseMachine(buyer, machineId, 4); packets = Drain(buyer); Drain(machine);
            That(packets.Single(p => p.Opcode == 235).Body[0] == 1 && Gold(machineId) == beforeOwner + 75 && Endurance(machineId) == 298,
                "新报价进入后仅按新价收费一次");
        }
        Drain(buyer);
        buyer.Send(599, Build(w => w.Write(machineId)));
        That(Drain(buyer).Single(p => p.Opcode == 599).Body[0] == 0, "店主断开后残留展示不授权交易");
        using (var enchanter = Open(fixture, "gamma"))
        {
            enchanter.Select(expertJobInfo: EnchantInfo(300)); enchanter.Send(1426); Drain(enchanter); Drain(buyer);
            OpenShop(enchanter, 35); Drain(enchanter); Drain(buyer); Enter(buyer, enchantId);
            long ownerGold = Gold(enchantId);
            buyer.Send(601, Build(w => { w.Write(enchantId); w.Write(recipe.RecipeId); w.Write((byte)2);
                w.Write((byte)0); w.Write((ushort)5); w.Write((byte)0); w.Write((ushort)cardSlot); }));
            var packets = Drain(buyer); var ownerPackets = Drain(enchanter);
            var ack = packets.Single(p => p.Opcode == 601).Body;
            That(ack[0] == 1 && ack[1] == 1 && U32(ack, 7) == 299 && U32(ack, 11) == 5 && U32(ack, 15) == recipe.Rarity && Endurance(enchantId) == 299 && Gold(enchantId) == ownerGold + 35,
                "只有稀有度1的摊主跨级附魔，ACK传真实等级5及资格1，不追加未知0，并支付摊主");
            using var lease = new AccountOwners().Acquire("beta", () => { });
            var restored = repository.LoadCharacter(lease, 0);
            That(restored.ExpertJobType == 0 && restored.Inventory.Find(Bag(5))!.Core.EnchantCardId == recipe.CardId &&
                restored.Inventory.Find(Bag(cardSlot))?.Core.Count == 1, "顾客装备获得效果并扣卡，不污染顾客职业状态");
            That(ownerPackets.Any(p => p.Opcode == 205), "附魔店主取得实际经验耐久投影");
            string beforeReplay = Snapshot();
            buyer.Send(601, Build(w => { w.Write(enchantId); w.Write(recipe.RecipeId); w.Write((byte)2);
                w.Write((byte)0); w.Write((ushort)5); w.Write((byte)0); w.Write((ushort)cardSlot); }));
            That(Drain(buyer).Single(p => p.Opcode == 601).Body[0] == 0 && Snapshot() == beforeReplay,
                "相同成功附魔包重放即使尚有卡和材料也不能再次收费或消耗");
        }
        await fixture.StopAsync();

        ushort Create(string account, string name, byte type)
        {
            using var lease = new AccountOwners().Acquire(account, () => { });
            var character = repository.Create(lease, new(name, resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 50, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = InventorySnapshot.Empty with { Gold = 100 } });
            fixture.Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness,learned_recipes) SELECT id,account,{type},{(type == 3 ? 51 : startingExperience)},'{(type == 3 ? "[]" : $"[{recipe.RecipeId}]")}' FROM characters WHERE id={character.Id}");
            return character.Id;
        }
        byte[] EnchantInfo(uint endurance) => Build(w => { w.Write((byte)0); w.Write((byte)1); w.Write((byte)1); w.Write(recipe.RecipeId);
            w.Write((byte)1); w.Write(recipe.Rarity); w.Write(4u); w.Write(endurance); });
        long Gold(ushort id) => Convert.ToInt64(fixture.Query($"SELECT gold FROM characters WHERE id={id}")[0][0]);
        long Endurance(ushort id) => Convert.ToInt64(fixture.Query($"SELECT machine_endurance FROM character_expert_meta WHERE character_id={id}")[0][0]);
        string Snapshot() => JsonSerializer.Serialize(new[] { fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid"),
            fixture.Query("SELECT * FROM character_expert_job ORDER BY character_id"), fixture.Query("SELECT * FROM character_expert_meta ORDER BY character_id"),
            fixture.Query("SELECT id,revision,gold FROM characters ORDER BY id") });
    }
    static InventorySlot Bag(int slot) => new(InventoryListType.Main, (short)slot);
    static byte[] MachineInfo(uint endurance) => Build(w => { w.Write((byte)0); w.Write((byte)3); w.Write(1u); w.Write(endurance); });
    static void OpenShop(TcpPeer peer, uint fee) => peer.Send(598, Build(w => { w.Write((byte)0); w.Write(0); w.Write(fee); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); }));
    static void Enter(TcpPeer peer, ushort actor)
    {
        peer.Send(599, Build(w => w.Write(actor)));
        That(Drain(peer).Single(p => p.Opcode == 599).Body.Length > 3, "同屏已开店可进入并记住报价");
    }
    static void UseMachine(TcpPeer peer, ushort actor, ushort slot) => peer.Send(235, Build(w => { w.Write(actor); w.Write(slot); w.Write((byte)0); }));
    static WirePacket[] Drain(TcpPeer peer)
    {
        peer.SendBarrier();
        var result = new List<WirePacket>();
        while (true)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == 1347) return result.ToArray();
            result.Add(packet);
        }
    }
}
