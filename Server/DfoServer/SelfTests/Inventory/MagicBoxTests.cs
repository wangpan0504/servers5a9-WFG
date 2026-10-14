using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;

namespace DfoServer.Testing.Inventory;

internal static class MagicBoxTests
{
    public static void Run(DfoServer.GameWorld.Towns.TownCatalog towns, ItemResources resources)
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            checks++;
            if (!condition)
                throw new InvalidOperationException("FAIL magic-box: " + name);
        }

        var request = MagicBoxRequests.Expand(Convert.FromHexString("00450048B39800790047B398006400"));
        Check(request == new MagicBoxOpenRequest(0, new InventorySlot(InventoryListType.Main, 69), 0x0098B348,
            new InventorySlot(InventoryListType.Main, 121), 0x0098B347, 100),
            "expand parser preserves list, slots, ids and count");
        var noMaterial = MagicBoxRequests.Expand(Convert.FromHexString("044600A0ED2800FFFFFFFFFFFF0A00"));
        Check(noMaterial.RawListType == 4 && noMaterial.BoxSlot.Index == 70 && noMaterial.BoxItemId == 0x0028EDA0 &&
            noMaterial.MaterialSlot.Index == -1 && noMaterial.MaterialItemId == 0 && noMaterial.Count == 10,
            "no-material expand request accepts client sentinels");

        var definition = MagicBoxCatalog.Parse(@"
[stackable type]
`[random upgradable legacy]` 1
[/stackable type]
[RANDOMBOX]
[int data]
2600014 2 0 2600014 18000 2 0 2600015 5000 1 0
[/int data]
[int data]
2682272 1 0 2682272 50000 1 0
[/int data]
[sealing removal item]
2 10007367 1 10007368 1
[/sealing removal item]
[/RANDOMBOX]
");
        Check(definition.Available && definition.MaterialItemId == 10007367 && definition.MaterialCount == 1 &&
            definition.Pools.Length == 2 && definition.Pools[0].Rewards[0] == new MagicBoxReward(2600014, 18000, 2) &&
            definition.Pools[0].Rewards[1] == new MagicBoxReward(2600015, 5000, 1),
            "random box parser reads independent reward pools and material");
        var samsung = MagicBoxCatalog.Parse(@"
[stackable type]
`[random ss upgradable legacy]` 1
[/stackable type]
[RANDOMBOX_SAMSUNG]
[int data]
3 1 0 8 20000 100 0 201 20000 10 0
[/int data]
[sealing removal item]
2 203 1 202 1
[/sealing removal item]
[/RANDOMBOX_SAMSUNG]
");
        Check(samsung.Available && samsung.MaterialItemId == 203 && samsung.MaterialCount == 1 && samsung.Pools.Single().Rewards.Length == 2,
            "Samsung random box tag uses the same validated pool/material contract");
        var goblin = MagicBoxCatalog.Parse(@"
[stackable type]
`[random goblin upgradable legacy]` 1
[/stackable type]
[RANDOMBOX_GOBLIN]
[int data]
36 1 0 36 30000 3 0 36 20000 5 0
[/int data]
[/RANDOMBOX_GOBLIN]
");
        Check(goblin.Available && goblin.MaterialItemId == 0 && goblin.Pools.Single().Rewards.Length == 2,
            "Goblin random box tag is available without a material section");
        Check(MagicBoxPackets.Failure().SequenceEqual(new byte[] { 0 }), "failure ack is the client one-byte result");
        var expandAck = MagicBoxPackets.OpenedExpand(new MagicBoxReceipt(10007368, 69, 121, 3,
            [new MagicBoxGrantedItem(2600014, 2, 65, 0, false)]));
        Check(expandAck.Length == 14 + ItemListProtocolWriter.EntrySize && expandAck[0] == 1 && expandAck[1] == 4 &&
            BitConverter.ToInt16(expandAck, 2) == 3 && BitConverter.ToInt16(expandAck, 4) == 69 &&
            BitConverter.ToInt16(expandAck, 6) == 121 && BitConverter.ToInt16(expandAck, 12) == 1,
            "expand success uses the native variant/open-count/list header");
        var seriaAck = MagicBoxPackets.OpenedSeriaExpand(new MagicBoxReceipt(2682272, 70, 70, 10,
            [new MagicBoxGrantedItem(2600014, 5, 1, 0, false)]));
        Check(seriaAck[0] == 4 && seriaAck[1] == 4 && seriaAck[2] == 0 && BitConverter.ToInt16(seriaAck, 3) == 10 &&
            BitConverter.ToInt16(seriaAck, 9) == 1 && BitConverter.ToInt16(seriaAck, 11 + ItemListProtocolWriter.EntrySize) == 0 &&
            BitConverter.ToInt16(seriaAck, 11 + ItemListProtocolWriter.EntrySize + 2) == 0,
            "Seria ten-open response uses the native dual-list layout");
        var seriaVirtualRewardAck = MagicBoxPackets.OpenedSeriaExpand(new MagicBoxReceipt(2682272, 70, -1, 10,
            [
                new MagicBoxGrantedItem(31, 1, -1, 0, false),
                new MagicBoxGrantedItem(2600014, 5, 1, 0, false),
            ]));
        Check(BitConverter.ToInt16(seriaVirtualRewardAck, 9) == 1 &&
            BitConverter.ToInt16(seriaVirtualRewardAck, 11) == 1 &&
            BitConverter.ToInt32(seriaVirtualRewardAck, 13) == 2600014,
            "Seria result omits direct-effect rewards without a real inventory slot");
        var seriaCurrencyRewardAck = MagicBoxPackets.OpenedSeriaExpand(new MagicBoxReceipt(2682272, 70, -1, 10,
            [new MagicBoxGrantedItem(42, 8, 1, 0, false)]));
        Check(BitConverter.ToInt16(seriaCurrencyRewardAck, 9) == 1 &&
            BitConverter.ToInt32(seriaCurrencyRewardAck, 13) == 42 &&
            BitConverter.ToInt32(seriaCurrencyRewardAck, 17) == 8,
            "Seria result displays currency rewards in their virtual inventory slot");
        var seriaSingleAck = MagicBoxPackets.OpenedSingle(new MagicBoxReceipt(2682272, 70, -1, 1,
            [new MagicBoxGrantedItem(2600014, 1, 1, 0, false)], RawListType: 4));
        Check(seriaSingleAck[0] == 1 && seriaSingleAck[1] == 4 && seriaSingleAck[2] == 0,
            "single Seria response preserves the client list type");
        var singleDoubleAck = MagicBoxPackets.OpenedSingle(new MagicBoxReceipt(2682272, 70, -1, 1,
            [new MagicBoxGrantedItem(2600014, 2, 1, 0, false)], RawListType: 4,
            DoubleRewards: [new MagicBoxGrantedItem(2600015, 1, 2, 0, false)],
            DisplayRewardRows: [new MagicBoxGrantedItem(2600014, 2, 1, 0, false)]));
        Check(singleDoubleAck[2] == 1 && BitConverter.ToInt16(singleDoubleAck, 7) == 2 &&
            BitConverter.ToInt32(singleDoubleAck, 9 + ItemListProtocolWriter.EntrySize + 2) == 2600015,
            "single Seria double response appends the second reward with the native row width");
        var real = resources.MagicBoxes.Get(10007368);
        Check(real.Available && real.MaterialItemId == 10007367 && real.MaterialCount == 1 && real.Pools.Length >= 2,
            "matched PVF random box and lucky hammer are available to the runtime catalog");
        var noMaterialDefinition = resources.MagicBoxes.Get(0x0028EDA0);
        Check(noMaterialDefinition.Available && noMaterialDefinition.MaterialItemId == 0 && noMaterialDefinition.MaterialCount == 0,
            "matched no-material random box remains openable with client sentinel fields");
        var seriaDefinition = resources.MagicBoxes.Get(2682272);
        Check(seriaDefinition.Available && seriaDefinition.MaterialItemId == 0 && seriaDefinition.MaterialCount == 0 &&
            seriaDefinition.Pools.Length == 3 &&
            seriaDefinition.Pools[0].Rewards[0] == new MagicBoxReward(42, 40000, 1) &&
            seriaDefinition.Pools[1].Rewards[0] == new MagicBoxReward(490701711, 5000, 10) &&
            seriaDefinition.Pools[2].Rewards[0] == new MagicBoxReward(30, 10000, 1),
            "Seria lucky box reward groups and no-material rule come directly from the active PVF");
        Check(resources.Items.Get(2682272).Kind == ItemKind.Consumable,
            "Seria box fallback path is also available to ordinary inventory movement");
        Check(SeriaLuckPackets.Gauge(0, 8).SequenceEqual(new byte[] { 0, 40 }),
            "Seria luck HUD uses the eight-stage gauge contract");
        var random = new FirstRandom();
        var service = new InventoryMagicBoxService(resources.Items, resources.MagicBoxes, resources.GoldLimits, resources.EpicPieces,
            random, contracts: resources.Contracts);
        var character = new Character(1, 0, "MagicBox", 0, towns.Room, towns.InitialReturn)
        {
            Inventory = new([new(0, new(InventoryListType.Main, 70), new(2682272, ItemKind.Consumable, 10))])
        };
        var opened = service.Open(character, noMaterial with { Count = 1 }, out var receipt);
        var unsupportedSeria = seriaDefinition.Pools.SelectMany(pool => pool.Rewards).Where(row => row.Weight > 0 &&
            CurrencyWallet.RewardKind(row.ItemId) is null && resources.Contracts.Recognizes(row.ItemId) &&
            !resources.Contracts.TryAcquisitionContract(row.ItemId, out _)).Select(row => row.ItemId).Distinct().ToArray();
        if (unsupportedSeria.Length != 0)
        {
            Check(!opened.Accepted && opened.Plan is null && receipt is null && random.Calls == 0 &&
                character.Inventory.Find(new(InventoryListType.Main, 70))!.Core.Count == 10,
                "赛丽亚池含未完整支持premium时在随机前整包拒绝，保留全部源和奖项：" + string.Join(",", unsupportedSeria));
            Console.WriteLine($"PASS magic-box unsupported premium sample={unsupportedSeria[0]} type={resources.Contracts.Get(unsupportedSeria[0])?.Type}");
        }
        else
        {
            Check(opened.Accepted && receipt is { OpenCount: 1, MaterialSlot: -1 } && opened.Plan!.After.Find(new(InventoryListType.Main, 70)) is { Core.Value: 9 },
                "PVF-defined no-material box consumes one box and commits its rewards");
            var luckyCharacter = character with
            {
                Inventory = character.Inventory with
                {
                    AccountWallet = AccountWallet.Empty with { SeriaLuck = 8 },
                    Items = [new(0, new(InventoryListType.Main, 70), new(2682272, ItemKind.Consumable, 2))]
                }
            };
            var luckyOpened = service.Open(luckyCharacter, noMaterial with { BoxItemId = 2682272, Count = 2 }, out var luckyReceipt);
            Check(luckyOpened.Accepted && luckyReceipt is { SeriaLuckBefore: 8, SeriaLuckAfter: 1 } && (luckyReceipt.DoubleRewards?.Count ?? 0) != 0,
                "Seria lucky threshold doubles one draw and persists the post-open gauge");
            var luckyAck = MagicBoxPackets.OpenedSeriaExpand(luckyReceipt!);
            int luckyPrimaryCount = BitConverter.ToInt16(luckyAck, 9);
            int luckyDoubleCountOffset = 11 + luckyPrimaryCount * ItemListProtocolWriter.EntrySize + 2;
            var visibleDoubled = luckyReceipt!.DoubledRewards.Where(row => row.Slot >= 0).ToArray();
            Check(visibleDoubled.Length > 0 &&
                BitConverter.ToInt16(luckyAck, luckyDoubleCountOffset) == visibleDoubled.Length &&
                BitConverter.ToInt32(luckyAck, luckyDoubleCountOffset + 4) == visibleDoubled[0].ItemId,
                "Seria lucky threshold response renders the doubled reward in the right-hand list");
            var bulkOpened = service.Open(character, noMaterial, out var bulkReceipt);
            Check(bulkOpened.Accepted && bulkReceipt is { OpenCount: 10, MaterialSlot: -1 } && bulkOpened.Plan!.After.Find(new(InventoryListType.Main, 70)) is null,
                "PVF-defined Seria ten-open consumes all requested boxes when reward space is available");
        }
        var unsupportedId = seriaDefinition.Pools.SelectMany(pool => pool.Rewards)
            .First(row => CurrencyWallet.RewardKind(row.ItemId) is null && !resources.EpicPieces.ByPiece.ContainsKey(row.ItemId)).ItemId;
        var unsupportedCatalog = new PremiumContractCatalog(new Dictionary<int, PremiumContract> { [unsupportedId] = new(unsupportedId, 97, 86400) });
        var rejectRandom = new FirstRandom();
        var rejectingService = new InventoryMagicBoxService(resources.Items, resources.MagicBoxes, resources.GoldLimits, resources.EpicPieces,
            rejectRandom, unsupportedCatalog);
        Check(rejectingService.Open(character, noMaterial with { Count = 1 }, out var rejected) is { Accepted: false, Plan: null } &&
            rejected is null && rejectRandom.Calls == 0 && character.Inventory.Find(new(InventoryListType.Main, 70))!.Core.Count == 10,
            "已识别的未支持premium候选在任何随机之前拒绝，不抽后重选或单独吞掉权益");
        var regularCharacter = character with
        {
            Inventory = new([
                new(0, new(InventoryListType.Main, 69), new(10007368, ItemKind.Consumable, 1)),
                new(0, new(InventoryListType.Main, 121), new(10007367, ItemKind.Material, 1))])
        };
        var regularOpened = service.Open(regularCharacter, request with { Count = 100 }, out var regularReceipt);
        Check(regularOpened.Accepted && regularReceipt is { OpenCount: 1 } &&
            regularOpened.Plan!.After.Find(new(InventoryListType.Main, 69)) is null &&
            regularOpened.Plan.After.Find(new(InventoryListType.Main, 121)) is null,
            "PVF-defined hammer box converges the captured 100-open request to available stock");
        Console.WriteLine($"PASS {checks} magic-box assertions");
    }

    sealed class FirstRandom : Random
    {
        public int Calls { get; private set; }
        public override long NextInt64(long maxValue)
        {
            Calls++;
            return 0;
        }
    }
}
