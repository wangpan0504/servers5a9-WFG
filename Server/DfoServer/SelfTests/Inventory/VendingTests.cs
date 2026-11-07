using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing.Inventory;

internal static class VendingTests
{
    public static void Run(TcpTestResources resources)
    {
        int checks = Count;
        var items = resources.ItemResources;
        var random = new Draw();
        var service = new InventoryVendingService(items.VendingMachines, items.Items, items.GoldLimits,
            items.EpicPieces, items.Contracts, random: random);
        long expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        var role = new Character(1, 0, "Vending", 0, resources.Towns.Room, resources.Towns.InitialReturn);
        foreach (var machine in items.VendingMachines.All)
        {
            That(machine.Group == 1 && machine.CoinCount == 1 &&
                (machine.Id == 1 && machine.NpcId == 80 && machine.CoinId == 7454 ||
                 machine.Id == 3 && machine.NpcId == 112 && machine.CoinId == 2749933), "两台黑钻机沿真实索引读取独立奖池及费用");
            var coin = new InventoryItem(1, Bag(128), Core(machine.CoinId, 2));
            var source = role with { Inventory = new([coin]) { AccountWallet = AccountWallet.Empty.SetContractExpiry(56, expires) } };
            var request = new VendingRequest(machine.Id, 1, 128);
            long start = 0;
            foreach (var prize in machine.Prizes)
            {
                if (prize.Weight == 0) continue;
                foreach (long edge in new[] { start, start + prize.Weight - 1 })
                {
                    random.Value = edge;
                    var drawSource = CurrencyWallet.RewardKind(prize.ItemId) is { } rewardKind
                        ? source with { Inventory = CurrencyWallet.Set(source.Inventory, rewardKind, 10) } : source;
                    var result = service.Open(drawSource, request, out var receipt);
                    That(result.Accepted && receipt is not null && receipt.ItemId == prize.ItemId && receipt.Count == prize.Count,
                        $"真实奖池逐行边界可发放：{machine.Id}/{prize.ItemId}/{prize.Count}，错误{result.Error}");
                    That(result.Plan!.After.Find(coin.Position)!.Core.Count == 1 && source.Inventory.Find(coin.Position)!.Core.Count == 2,
                        "奖励与一枚币扣除在同一不可变计划中，源快照保持不变");
                    var ack = VendingPackets.Opened(result with { Receipt = new(drawSource with { Inventory = result.Plan.After }, 1, 1) }, receipt);
                    That(U32(ack, 7) == prize.ItemId && U32(ack, 11) == prize.Count && U16(ack, 23) > 0 &&
                        ack.Length == (receipt!.Contract ? 34 : 25 + U16(ack, 23) * 124), "合成提交回执按95格式完整编码每种奖品");
                    if (CurrencyWallet.RewardKind(prize.ItemId) is not null)
                        That(U32(ack, 32) == 10 + prize.Count && U32(ack, 32) - 10 == U32(ack, 11),
                            "货币记录给提交后余额，原生减旧余额得到本次奖励，标题仍为增量");
                    That(result.Plan.CharacterPrecondition!.Matches(source) && result.Plan.CharacterPrecondition.IsExpired(expires),
                        "角色位置及黑钻资格绑定计划，期限到达后不能再提交");
                    if (items.Contracts.TryAcquisitionContract(prize.ItemId, out var contract))
                        That(result.Plan.After.AccountWallet.ContractExpiry(contract.Type) > source.Inventory.AccountWallet.ContractExpiry(contract.Type),
                            "契约奖品真实激活到账号期限");
                    else if (CurrencyWallet.RewardKind(prize.ItemId) is { } currency)
                        That(CurrencyWallet.Get(result.Plan.After, currency) - CurrencyWallet.Get(drawSource.Inventory, currency) == prize.Count,
                            "虚拟货币奖品按实际数量到账");
                    else
                        That(result.Plan.After.Items.Where(item => item.Core.ItemId == prize.ItemId).Sum(item => item.Core.Count) == prize.Count,
                            "实体奖品数量完整，不以成功空回执代替到账");
                }
                start += prize.Weight;
            }
            That(start == machine.TotalWeight, "权重累加保留零权重及重复项，覆盖完整取值区间");
            long currencyStart = 0;
            foreach (var prize in machine.Prizes)
            {
                if (prize.Weight > 0 && prize.Count > 1 && CurrencyWallet.RewardKind(prize.ItemId) is { } kind)
                {
                    random.Value = currencyStart;
                    foreach (int balance in new[] { int.MaxValue, int.MaxValue - 1 })
                    {
                        var capped = source with { Inventory = CurrencyWallet.Set(source.Inventory, kind, balance) };
                        var overflow = service.Open(capped, request, out var overflowReceipt);
                        That(!overflow.Accepted && overflow.Error == 4 && overflowReceipt is null &&
                            capped.Inventory.Find(coin.Position)!.Core.Count == 2 && CurrencyWallet.Get(capped.Inventory, kind) == balance,
                            "虚拟货币已满或奖品部分溢出时整笔拒绝，不扣币也不宣称完整发奖");
                    }
                }
                currencyStart += prize.Weight;
            }
            random.Value = 0;
            foreach (int oldCount in new[] { 1, 5, 500 })
            {
                var stacked = source with { Inventory = source.Inventory with { Items = [coin, new(2, Bag(65), Core(7463, oldCount))] } };
                var merged = service.Open(stacked, request, out var mergedReceipt);
                var mergedAck = VendingPackets.Opened(merged with { Receipt = new(stacked with { Inventory = merged.Plan!.After }, 1, 1) }, mergedReceipt);
                That(merged.Plan.After.Find(Bag(65))!.Core.Count == oldCount + machine.Prizes[0].Count &&
                    U32(mergedAck, 32) == oldCount + machine.Prizes[0].Count &&
                    U32(mergedAck, 32) - oldCount == U32(mergedAck, 11),
                    "已有堆叠回包给总数，原生相减填入正奖励及本次获得提示");
            }
            foreach (var invalid in new[] { request with { Machine = 2 }, request with { Group = 2 }, request with { CoinSlot = -1 },
                request with { CoinSlot = 127 } })
                That(!service.Open(source, invalid, out _).Accepted, "其它机器、奖组及错误币槽均拒绝");
            That(!service.Open(source with { Inventory = source.Inventory with { AccountWallet = AccountWallet.Empty } }, request, out _).Accepted,
                "无有效黑钻不消费硬币");
            foreach (var invalid in new[] { coin.Core with { EquipmentLockId = 1 },
                coin.Core with { ExpireTime = checked((int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1)) }, Core(machine.CoinId == 7454 ? 2749933 : 7454) })
                That(!service.Open(source with { Inventory = source.Inventory with { Items = [coin with { Core = invalid }] } }, request, out _).Accepted,
                    "锁定、到期或另一币种均拒绝");
            var prizeTemplate = items.Items.Get(machine.Prizes[0].ItemId);
            var range = ItemSlotBoundService.Range(prizeTemplate.Kind, source.Inventory.Expansion);
            int filler = prizeTemplate.Id == 7463 ? 7464 : 7463;
            var full = source.Inventory with { Items = [coin, .. Enumerable.Range(range.Start, range.End - range.Start + 1)
                .Select(slot => new InventoryItem(slot + 2, Bag((short)slot), Core(filler)))] };
            var rejected = service.Open(source with { Inventory = full }, request, out _);
            That(!rejected.Accepted && full.Find(coin.Position)!.Core.Count == 2, "奖励分类满包时不扣币、不发部分奖品");
        }
        That(VendingRequests.Read(Convert.FromHexString("01000000010000008000")) == new VendingRequest(1, 1, 128),
            "原生215十字节正文不额外跳过opcode");
        foreach (int length in new[] { 0, 8, 9, 11, 12 })
            Reject<InvalidDataException>(() => VendingRequests.Read(new byte[length]), "拒绝不完整及额外尾字节请求");
        const string head = "[item group]\n[group num]\n1\n[material]\n7454 1\n[output]\n";
        const string tail = "\n[/output]\n[/item group]";
        foreach (string bad in new[] { "", "1 1 1", "1 -1 1 0", "1 0 1 0", "0 1 1 0", "1 1 0 0", "1 1 1 2" })
            Reject<InvalidDataException>(() => VendingMachineCatalog.Parse(1, 80, head + bad + tail), "无效奖池不启用");
        Console.WriteLine($"PASS {Count - checks} 两台黑钻机真实奖池边界、扣币发奖、资格、容量及协议断言");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(items.Items.Get(id), count, shopExpiration: true);
    }
    static InventorySlot Bag(short index) => new(InventoryListType.Main, index);
    internal sealed class Draw : Random
    {
        public long Value { get; set; }
        public override long NextInt64(long maxValue) => Value < maxValue ? Value : throw new InvalidOperationException("Invalid draw fixture");
    }
}
