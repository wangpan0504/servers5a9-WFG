using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Settings;
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

        var potRequest = MagicBoxRequests.Single([3, 94, 0, 150, 0]);
        Check(potRequest == new MagicBoxOpenRequest(3, new(InventoryListType.Main, 94), 0,
            new(InventoryListType.Main, 150), 0, 1), "魔罐原生类型3保留两个Main绝对槽位");
        foreach (byte[] invalid in new byte[][] { [3, 94, 0], [2, 94, 0, 150, 0], [5, 94, 0, 150, 0] })
        {
            bool malformedRejected = false;
            try { MagicBoxRequests.Single(invalid); }
            catch (InvalidDataException) { malformedRejected = true; }
            Check(malformedRejected, "魔罐短包及未确认用途类型不放行");
        }
        Check(MagicBoxRequests.Single([0, 94, 0]).RawListType == 0 &&
            MagicBoxRequests.Single([4, 94, 0]).RawListType == 4, "既有0/4单开短包保持兼容");
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
2600014 2 0 2600014 18000 2 1 2600015 5000 1 0
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
            definition.Pools.Length == 2 && definition.Pools[0].Rewards[0] == new MagicBoxReward(2600014, 18000, 2, true) &&
            definition.Pools[0].Rewards[1] == new MagicBoxReward(2600015, 5000, 1, false) &&
            MagicBoxPackets.Broadcast("123", 2682272, 2600014).SequenceEqual(
                new byte[] { 18, 3, (byte)'1', (byte)'2', (byte)'3', 0xA0, 0xED, 0x28, 0x00, 0x4E, 0xAC, 0x27, 0x00 }),
            "random box parser reads independent reward pools, broadcast flag and NOTI 86 subType 18 packet");
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
        var potService = new InventoryMagicBoxService(resources.Items, resources.MagicBoxes, resources.GoldLimits, resources.EpicPieces,
            new FirstRandom(), contracts: resources.Contracts);
        var pot = new Character(1, 0, "MagicPot", 0, towns.Room, towns.InitialReturn)
        {
            Level = 95,
            Inventory = new([new(1, potRequest.BoxSlot, new(2681819, ItemKind.Consumable, 2)),
                new(2, potRequest.MaterialSlot, new(2681820, ItemKind.Material, 1))])
        };
        var potOpened = potService.Open(pot, potRequest, out var potReceipt);
        Check(potOpened.Accepted && potReceipt is { RawListType: 3, BoxSlot: 94, MaterialSlot: 150, OpenCount: 1 } &&
            potOpened.Plan!.After.Find(potRequest.BoxSlot) is { Core.Count: 1 } &&
            potOpened.Plan.After.Find(potRequest.MaterialSlot) is null && potReceipt.Rewards.Sum(row => row.Count) == 1,
            "魔罐原生类型3从Main消耗一罐一圣水并按真实PVF奖池发奖");
        foreach (var invalid in new[] { potRequest with { BoxSlot = potRequest.MaterialSlot },
            potRequest with { MaterialSlot = new(InventoryListType.Main, 149) } })
            Check(potService.Open(pot, invalid, out var rejectedReceipt) is { Accepted: false, Plan: null } && rejectedReceipt is null,
                "魔罐错误源槽或材料槽不生成扣费及奖励计划");
        Check(potService.Open(pot with { Inventory = potOpened.Plan!.After }, potRequest, out _) is { Accepted: false, Plan: null } &&
            pot.Inventory.Find(potRequest.BoxSlot) is { Core.Count: 2 } && pot.Inventory.Find(potRequest.MaterialSlot) is { Core.Count: 1 },
            "圣水耗尽拒绝再次开罐，规划不修改原快照");
        var fullPot = pot with
        {
            Inventory = new([.. Enumerable.Range(65, 32).Select(slot => new InventoryItem(slot, new(InventoryListType.Main, (short)slot),
                    new(2681819, ItemKind.Consumable, 3)))
                .Concat(Enumerable.Range(121, 32).Select(slot => new InventoryItem(slot, new(InventoryListType.Main, (short)slot),
                    new(2681820, ItemKind.Material, 3))))], Expansion: 0)
        };
        var overflowPot = potService.Open(fullPot, potRequest, out var overflowReceipt);
        Check(overflowPot.Accepted && overflowPot.Plan!.OverflowItems is [{ Core.Count: 1 }] &&
            overflowPot.Plan.After.Find(potRequest.BoxSlot) is { Core.Count: 2 } &&
            overflowPot.Plan.After.Find(potRequest.MaterialSlot) is { Core.Count: 2 } &&
            overflowReceipt is { Rewards.Count: 1 } && overflowReceipt.Rewards[0].Slot == -1,
            "魔罐满包沿现有原子溢出邮件合同，不伪装空间不足或丢失奖励");
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
            string eventPath = Path.Combine(Path.GetTempPath(), "seria-event-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var events = new DfoServer.Game.Events.ServerEventService(new DfoServer.Persistence.ServerEventRepository(eventPath));
                events.Override(2037, false);
                var controlled = new InventoryMagicBoxService(resources.Items, resources.MagicBoxes, resources.GoldLimits,
                    resources.EpicPieces, new FirstRandom(), contracts: resources.Contracts, events: events);
                var ordinary = controlled.Open(luckyCharacter, noMaterial with { BoxItemId = 2682272, Count = 2 }, out var ordinaryReceipt);
                Check(ordinary.Accepted && ordinaryReceipt is { SeriaLuckBefore: 8, SeriaLuckAfter: 8 } &&
                    ordinaryReceipt.DoubledRewards.Count == 0 && ordinary.Plan!.After.Find(new(InventoryListType.Main, 70)) is null,
                    "关闭幸运仍普通开盒，保留原幸运值且不发双倍");
                var guard = ordinary.Plan!.RequiredEvent!;
                Check(guard.Matches(events.Read(2037).Schedule), "关闭快照可以正常提交普通开盒");
                events.Override(2037, true);
                Check(!guard.Matches(events.Read(2037).Schedule), "开盒规划后切换开关使旧幸运规划失效");
                var resumed = controlled.Open(luckyCharacter, noMaterial with { BoxItemId = 2682272, Count = 2 }, out var resumedReceipt);
                Check(resumed.Accepted && resumedReceipt is { SeriaLuckBefore: 8, SeriaLuckAfter: 1 } &&
                    resumedReceipt.DoubledRewards.Count > 0, "重开后从保留幸运值继续双倍");
            }
            finally { TestFiles.DeleteFile(eventPath); }
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
        InventoryQualificationTests.CheckInputs(regularCharacter, role => service.Open(role, request, out _),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new(InventoryListType.Main, 69), new(InventoryListType.Main, 121));
        var boxSource = regularCharacter.Inventory.Find(new(InventoryListType.Main, 69))!;
        var wrongKind = regularCharacter with { Inventory = regularCharacter.Inventory with { Items = regularCharacter.Inventory.Items.Replace(
            boxSource, boxSource with { Core = boxSource.Core with { Kind = ItemKind.Material } }) } };
        int drawsBefore = random.Calls;
        Check(service.Open(wrongKind, request, out _) is { Accepted: false, Plan: null } && random.Calls == drawsBefore,
            "随机盒源种类与真实模板不符在随机前拒绝，不借合法奖池绕过源资格");
        CheckEveryReward();
        Console.WriteLine($"PASS {checks} magic-box assertions");

        void CheckEveryReward()
        {
            var candidate = character with
            {
                Level = 43,
                Inventory = new([new(0, new(InventoryListType.Main, 70), new(2682272, ItemKind.Consumable, 999))])
                {
                    Gold = 364755, ReviveCoins = 129,
                    AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 99881120)
                }
            };
            int cases = 0;
            int datedRewards = 0;
            int broadcastCandidates = 0;
            var failures = new List<string>();
            for (int pool = 0; pool < seriaDefinition.Pools.Length; pool++)
            {
                long offset = 0;
                foreach (var row in seriaDefinition.Pools[pool].Rewards)
                {
                    if (row.Weight <= 0) continue;
                    if (row.Broadcast) broadcastCandidates++;
                    var settings = new GameDebugSettings();
                    var forced = new PoolRandom(seriaDefinition.Pools.Length, pool, offset);
                    var targeted = new InventoryMagicBoxService(resources.Items, resources.MagicBoxes, resources.GoldLimits,
                        resources.EpicPieces, forced, resources.Contracts, settings);
                    try
                    {
                        var result = targeted.Open(candidate, noMaterial, out var actual);
                        if (!result.Accepted || actual is null)
                            failures.Add($"pool={pool} item={row.ItemId} count={row.Count} error={result.Error}");
                        else
                            Check(result.Plan!.After.Find(noMaterial.BoxSlot) is { Core.Count: 989 } &&
                                actual.OpenCount == 10 && actual.SeriaLuckAfter == 1 && forced.Calls == 30 &&
                                actual.Rewards.Any(reward => reward.ItemId == row.ItemId && reward.Count >= row.Count * 11) &&
                                (pool != 0 || actual.BroadcastRewards.Contains(row.ItemId) == row.Broadcast),
                                $"每个正权重候选十连完整交付且包含第九次双倍与极品广播标记：pool={pool} item={row.ItemId} count={row.Count}");
                        if (resources.Items.Get(row.ItemId).Expiration is { Days: 0, AbsoluteTime: > 0 } expiry &&
                            expiry.AbsoluteTime < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                        {
                            datedRewards++;
                            Check(result.Accepted && result.Plan!.After.Items.Any(item => item.Core.ItemId == row.ItemId &&
                                item.Core.ExpireTime == ItemExpiration.MaximumUnixTime), "开启忽略绝对日期后奖励使用客户端安全期限");
                            settings.IgnoreCashShopAbsoluteExpiration = false;
                            Check(targeted.Open(candidate, noMaterial, out var strict) is { Accepted: false, Plan: null } &&
                                strict is null && candidate.Inventory.Find(noMaterial.BoxSlot) is { Core.Count: 999 },
                                "关闭忽略日期后按资源拒绝已过期奖励且不扣源或改变幸运值");
                            if (datedRewards == 1)
                            {
                                settings.IgnoreCashShopAbsoluteExpiration = true;
                                var switching = new InventoryMagicBoxService(resources.Items, resources.MagicBoxes, resources.GoldLimits,
                                    resources.EpicPieces, new PoolRandom(seriaDefinition.Pools.Length, pool, offset,
                                        () => settings.IgnoreCashShopAbsoluteExpiration = false), resources.Contracts, settings);
                                Check(switching.Open(candidate, noMaterial, out _) is { Accepted: true },
                                    "随机期间切换设置不改变本次入口已冻结的期限规则");
                                settings.IgnoreCashShopAbsoluteExpiration = true;
                                var expiredSource = candidate with { Inventory = candidate.Inventory with
                                { Items = [candidate.Inventory.Items[0] with { Core = candidate.Inventory.Items[0].Core with { ExpireTime = 1 } }] } };
                                Check(targeted.Open(expiredSource, noMaterial, out _) is { Accepted: false, Plan: null },
                                    "奖励忽略期限不放过已经到期的源盒实例");
                            }
                        }
                    }
                    catch (Exception error) when (error is InvalidDataException or InvalidOperationException or OverflowException)
                    { failures.Add($"pool={pool} item={row.ItemId} count={row.Count} exception={error.GetType().Name}: {error.Message}"); }
                    cases++;
                    offset += row.Weight;
                }
            }
            Console.WriteLine($"Magic-box full pool audit: cases={cases} datedRewards={datedRewards} failures={failures.Count}");
            foreach (string failure in failures) Console.WriteLine("Magic-box candidate failure: " + failure);
            Check(failures.Count == 0, "真实赛丽亚全部正权重候选均有完整十连交付能力");
        }
    }

    sealed class PoolRandom(int pools, int selectedPool, long offset, Action? firstDraw = null) : Random
    {
        public int Calls { get; private set; }
        public override long NextInt64(long maxValue)
        {
            if (Calls == 0) firstDraw?.Invoke();
            return Calls++ % pools == selectedPool ? offset : 0;
        }
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
