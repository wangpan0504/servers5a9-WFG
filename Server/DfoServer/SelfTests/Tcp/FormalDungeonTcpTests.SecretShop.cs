using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunSecretShopAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        var items = resources.ItemResources;
        var service = new SecretShopService(items.SecretShop!, items.Items, items.GoldLimits, items.EpicPieces);
        That(service.Merchant(3, 95, 0, _ => 0) == 1000 && service.Merchant(3, 95, 0, _ => 9999) == 1003,
            "真实目录保留普通加百利出现和不出现两侧，隔离必出规则不泄漏生产");
        foreach (byte difficulty in Enumerable.Range(0, 5).Select(value => (byte)value))
        {
            int absent = 9500 - 10 * difficulty;
            That(service.Merchant(3, 95, difficulty, _ => absent - 1) == 1000 &&
                service.Merchant(3, 95, difficulty, _ => absent) == 1003,
                "真实五难度万分骰在各自不出现/出现边界切换，不把比例改成必出");
            int merchant = service.Merchant(3, 95, difficulty, _ => 9999);
            var offered = service.Offer(merchant, 3, 95, _ => 0);
            That(offered.Available && offered.Offers.Length > 0 && offered.Offers.All(row =>
                row.UnitGold >= 0 && row.Remaining > 0 && items.Items.Get(row.ItemId).Id == row.ItemId),
                "五种难度的真实出现表和95级普通商品池均能生成完整报价");
        }
        await Exercise(true);
        await Exercise(false);
        Console.WriteLine($"PASS {Count - checks} 普通加百利商店真实TCP断言");

        async Task Exercise(bool available)
        {
            await using var fixture = new TcpTestFixture(source, Path.Combine(root, available ? "secret-shop" : "no-secret-shop"),
                resources: resources, secretShopRules: available ? SecretShopTests.Guaranteed() : SecretShopTests.Silent());
            var repository = new CharacterRepository(fixture.Database);
            var slots = new ushort[available ? 3 : 1];
            for (int index = 0; index < slots.Length; index++)
            {
                using var owner = new AccountOwners().Acquire("secretshop" + index, () => { });
                var candidate = CharacterCreation.Prepare("Secret" + index, resources.Characters.Get(0),
                    resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                var character = repository.Create(owner, candidate with
                    { Level = 95, AcceptedStoryLevel = 95, InitialLearning = learning });
                var funded = character.Inventory with { Gold = index == 2 ? 0U : 50000U, GoldCarryLimit = 1000000 };
                slots[index] = InventoryService.Commit(repository, owner, character,
                    new(true, 0, new(character.Inventory, funded, "secret_shop_fixture", new(character)))).Slot;
            }
            await fixture.StartAsync();
            for (int scenario = 0; scenario < slots.Length; scenario++)
            {
                using var peer = Open(fixture, "secretshop" + scenario);
                peer.Select(slots[scenario], prepared: true);
                string town = fixture.Snapshot();
                peer.Send(297, [1]); peer.Barrier();
                peer.Send(296, Request(3167, 1)); peer.Expect(1, 296, [0, 17, 0]); peer.Barrier();
                That(fixture.Snapshot() == town, "城镇伪造开店和购买不创建报价、不扣金币或发货");
                bool shown = false, roomFinished = false;
                EntryTown(peer);
                SelectMap(peer);
                var selection = Selection();
                Array.Clear(selection, 16, 4);
                peer.Send(16, selection);
                That(peer.Expect(0, 28).Body.Length == 36, "普通商店按无剧情任务的真实36字节入口进入");
                byte[] map = peer.Expect(0, 29).Body;
                peer.Barrier(Observe);
                var targets = new (byte X, byte Y)[] { (1, 1), (2, 1), (2, 0), (3, 0), (4, 0) };
                for (int room = 0; room <= targets.Length; room++)
                {
                    Load(peer); peer.Barrier(Observe);
                    foreach (int at in Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                        .Where(at => map[at + 13] != 9 && map[at + 14] == 0).OrderBy(at => map[at + 13] == 3))
                    {
                        roomFinished = map[at + 13] == 3;
                        peer.Send(39, Death(U16(map, at + 6), peer.Id, map[at + 12]));
                        peer.Expect(0, 38); peer.Barrier(Observe);
                    }
                    if (shown || room == targets.Length) break;
                    var move = new byte[100]; move[0] = targets[room].X; move[1] = targets[room].Y;
                    peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
                }
                That(shown, "真实普通副本Boss通关后收到279商人决策，不在普通清房提前出现");
                if (!available)
                {
                    string absentState = fixture.Snapshot();
                    peer.Send(297, [1]); peer.Barrier();
                    peer.Send(296, Request(3167, 1)); peer.Expect(1, 296, [0, 17, 0]); peer.Barrier();
                    That(fixture.Snapshot() == absentState, "279无商人1000后不发布商品，伪造购买不消费资产");
                    continue;
                }
                peer.Send(297, [1]); var quote = peer.Expect(0, 280).Body; peer.Barrier();
                That(U32(quote, 0) == 2 && quote.Length == 48, "280是u32数量和两条22字节商品，不使用普通NPC商店格式");
                var rows = Enumerable.Range(0, 2).Select(index => 4 + 22 * index).ToArray();
                int stack = rows.Single(at => U32(quote, at) == 3167);
                int equipment = rows.Single(at => U32(quote, at) == 101010912);
                int price = checked((int)U32(quote, stack + 5));
                That(price is >= 900 and <= 1100 && U32(quote, stack + 9) == 10 && U32(quote, equipment + 9) == 1,
                    "报价保留资源价格浮动、独立库存和装备单件数量");
                peer.Send(297, [0]); peer.Barrier();
                string closed = fixture.Snapshot();
                peer.Send(296, Request(3167, 1)); peer.Expect(1, 296, [0, 17, 0]); peer.Barrier();
                That(fixture.Snapshot() == closed, "关店后拒绝旧购买请求，不继续消费旧打开状态");
                peer.Send(297, [1]); peer.Expect(0, 280, quote); peer.Barrier();
                foreach (byte[] invalid in new[] { Request(3167, 11), Request(3167, -1), Request(101010912, 2), Request(1, 1), new byte[7] })
                {
                    string before = fixture.Snapshot();
                    peer.Send(296, invalid);
                    peer.Expect(1, 296, invalid.Length != 8 || U32(invalid, 0) == 1 ? new byte[] { 0, 17, 0 } : new byte[] { 0, 19, 0 });
                    peer.Barrier();
                    That(fixture.Snapshot() == before, "越库存、负数、装备多件、非报价及截断请求不修改任何资产");
                }
                if (scenario == 2)
                {
                    string before = fixture.Snapshot();
                    peer.Send(296, Request(3167, 10)); peer.Expect(1, 296, [0, 10, 0]); peer.Barrier();
                    That(fixture.Snapshot() == before, "金币不足整笔拒绝，不发商品、不消费本次报价库存");
                    peer.Send(297, [1]); peer.Expect(0, 280, quote); peer.Barrier();
                    continue;
                }
                if (scenario == 1)
                {
                    string before = fixture.Snapshot();
                    fixture.Sql("CREATE TRIGGER fail_secret_shop BEFORE INSERT ON inventory_audit_log WHEN NEW.action_name='secret_shop_buy' BEGIN SELECT RAISE(ABORT,'secret shop rollback'); END");
                    peer.Send(296, Request(3167, 3)); peer.Closed();
                    That(fixture.Snapshot() == before, "晚期SQL异常回滚金币、完整库存、UID和revision，不先发成功");
                    fixture.Sql("DROP TRIGGER fail_secret_shop");
                    continue;
                }
                long gold = Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id).Single()[0]);
                peer.Send(296, Request(3167, 3));
                byte[] bought = peer.Expect(1, 296).Body;
                That(bought.Length == 30 && bought[0] == 1 && U32(bought, 1) == gold - price * 3 &&
                    U32(bought, 7) == 3167 && U32(bought, 11) == 3 && U32(bought, 18) == uint.MaxValue && U32(bought, 26) == 7,
                    "296成功按原生完整30字节投影已提交金币、槽位、商品累计数量及剩余库存");
                peer.Barrier(packet => That(packet is { Kind: 0, Opcode: 14 or 1152 }, "成功ACK后只发布受影响库存或钱包"));
                That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id) is [[long afterGold]] && afterGold == gold - price * 3,
                    "购买按本次三件扣费并与独立SQLite读取一致");
                peer.Send(297, [0]); peer.Barrier();
                peer.Send(297, [1]); var remaining = peer.Expect(0, 280).Body; peer.Barrier();
                That(U32(remaining, stack + 9) == 7 && U32(remaining, stack + 5) == price,
                    "重复打开保持原价和已提交库存，不能重抽报价或补满库存");
                peer.Send(296, Request(3167, 7)); peer.Expect(1, 296);
                peer.Barrier(packet => That(packet is { Kind: 0, Opcode: 14 or 1152 }, "耗尽库存仍发送完整差量"));
                string exhausted = fixture.Snapshot();
                peer.Send(296, Request(3167, 1)); peer.Expect(1, 296, [0, 19, 0]); peer.Barrier();
                That(fixture.Snapshot() == exhausted, "库存耗尽后重放不二次发货或扣款");
                peer.Send(296, Request(101010912, 0)); var gear = peer.Expect(1, 296).Body;
                That(gear.Length == 30 && U32(gear, 7) == 101010912 && U32(gear, 11) == 999999998 &&
                    gear[15] == 0 && U16(gear, 16) > 0 && U32(gear, 26) == 0,
                    "原生装备零数量归一单件，完整品级/耐久与耗尽状态回执保留");
                peer.Barrier(packet => That(packet is { Kind: 0, Opcode: 14 or 1152 }, "装备成功后完整库存差量"));

                void Observe(WirePacket packet)
                {
                    if (packet is { Kind: 0, Opcode: 279 })
                    {
                        That(!shown && roomFinished && packet.Body.SequenceEqual(available ? new byte[] { 235, 3, 0, 0 } : new byte[] { 232, 3, 0, 0 }),
                            "279按本次通关出现决策精确发送1003或1000，不能提前或重复通知");
                        shown = true; return;
                    }
                    if (packet is { Kind: 0, Opcode: 1349 }) { AssertAdventureProgress(fixture, resources, peer, packet); return; }
                    if (packet is { Kind: 0, Opcode: 37 })
                    {
                        That(packet.Body.Length == 91 && packet.Body[0] == 95 &&
                            U64(packet.Body, 1) == Convert.ToUInt64(fixture.Query(
                                "SELECT experience FROM characters WHERE id=$p0", peer.Id).Single()[0]),
                            "真实击杀成长通知保留完整91字节，并与已提交角色经验一致");
                        return;
                    }
                    That(packet is { Kind: 0, Opcode: 14 or 36 or 115 or 31 or 261 or 789 or 16 or 607 or 417 or 1152 },
                        $"真实通关只接受明确的成长、任务、库存、战令与结算通知，实际({packet.Kind},{packet.Opcode}) {Convert.ToHexString(packet.Body)}");
                }
            }
        }
        static byte[] Request(int item, int count) => Build(writer => { writer.Write(item); writer.Write(count); });
    }
}
