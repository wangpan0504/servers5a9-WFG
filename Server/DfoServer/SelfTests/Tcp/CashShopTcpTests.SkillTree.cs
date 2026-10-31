using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class CashShopTcpTests
{
    static async Task SkillTreeAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources.Items;
        var product = resources.ItemResources.CashShop.Products.Values.Single(row => row.CommodityId == 100494);
        That(product.ItemId == 821 && product.Count == 1 && product.CeraPrice > 0 && items.IsSkillTreeExpansion(product.ItemId),
            "匹配资源的821与购买即生效商品绑定");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "skill-tree-shop"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            foreach (var row in new (string Name, int Mode)[] { ("TreePaid", 0), ("TreeVoucher", 1), ("TreeCargo", 2), ("TreeExpired", 3), ("TreeRollback", 1) })
            {
                var initial = SkillRepositoryTests.Fixture(row.Name, resources.Towns, resources.Characters, resources.Learning);
                var state = initial.InitialLearning with { BonusSp = 0, BonusTp = 0,
                    Trees = Array.AsReadOnly(initial.InitialLearning.Trees.Select(tree => tree.Context == 0 ? tree : tree with
                    {
                        Skills = Array.AsReadOnly(tree.Skills.Select(skill => skill.Id == 5 ? skill with { Slot = 7, Level = 2 } : skill).ToArray())
                    }).ToArray()) };
                var inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0));
                if (row.Mode != 0)
                {
                    var template = items.Get(821);
                    var core = InventoryRewardGrantService.Create(template, 2) with
                    { ExpireTime = row.Mode == 3 ? (int)DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds() : 0 };
                    var position = new InventorySlot(row.Mode == 2 ? InventoryListType.PersonalCargo : InventoryListType.Main, row.Mode == 2 ? (short)0 : (short)68);
                    inventory = inventory with { Items = inventory.Items.Add(new(0, position, core)) };
                }
                var character = repository.Create(owner, initial with
                { Level = 60, AcceptedStoryLevel = 1, InitialLearning = state, Inventory = inventory });
                if (row.Mode is 0 or 3)
                    character = InventoryService.Commit(repository, owner, character,
                        new CurrencyService(resources.ItemResources.GoldLimits).Grant(character, CurrencyKind.Cera, product.CeraPrice));
                if (row.Mode == 3)
                {
                    var service = new InventoryCashShopService(items, resources.ItemResources.CashShop, resources.ItemResources.GoldLimits,
                        resources.ItemResources.EpicPieces, resources.ItemResources.Cargo, contracts: resources.ItemResources.Contracts);
                    var planned = service.Buy(character, new(0, [new(product.CommodityId, 0)]), out _);
                    That(planned.Accepted, "过期旧券正常收费规划：" + planned.Error);
                    That(planned.Plan!.UnlockSkillTree && JsonSerializer.Serialize(planned.Plan.After.Items.OrderBy(item => item.Uid)) == JsonSerializer.Serialize(character.Inventory.Items.OrderBy(item => item.Uid)) &&
                        planned.Plan.After.AccountWallet.Get(CurrencyKind.Cera) == character.Inventory.AccountWallet.Get(CurrencyKind.Cera) - product.CeraPrice,
                        "过期旧券不作支付凭据，正常收费且购买规划不回收旧物");
                }
            }
        }
        await fixture.StartAsync();
        byte[] purchase = BuyAvatar(product.CommodityId, 0, 0);
        using (var peer = Open(fixture))
        {
            var login = peer.Select();
            That(login[2].Body[^9] == 255, "未开通Actor明确投影FF");
            peer.Send(260, [0]); peer.Expect(1, 260, [0, 1, 0]); peer.Barrier();
            peer.Send(64, purchase); Bought(peer, product.CommodityId);
            That(U32(peer.Expect(0, 53).Body, 1) == product.CeraPrice, "首次付费只扣本商品一次价格，保留同账号另一角色准备款");
            Changed(peer, 0);
            That(fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=821", peer.Id) is [[0L]],
                "付费开通不再发实体821");
            string before = Snapshot();
            peer.Send(64, purchase); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            That(Snapshot() == before, "每角色限购一次，重复不扣余额或发实物");
            peer.Send(260, [0]); peer.Expect(1, 260, [1, 1]); Changed(peer, 1);
            That(fixture.Query("SELECT selected_skill_tree FROM characters WHERE id=$p0", peer.Id) is [[1L]] &&
                fixture.Query("SELECT level FROM character_skills WHERE character_id=$p0 AND skill_id=5", peer.Id) is [[2L]],
                "ACK260之前第二方案与真实技能镜像已提交");
            string primaryCommands = JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_skill_commands WHERE character_id=$p0 AND context=0 ORDER BY skill_id", peer.Id));
            peer.Send(331, [1, 5, 0, 3, 2, 128, 4]); peer.Expect(1, 331, [1]);
            peer.Barrier();
            That(fixture.Query("SELECT hex(keys) FROM character_skill_commands WHERE character_id=$p0 AND context=1 AND skill_id=5", peer.Id) is [["028004"]] &&
                JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_skill_commands WHERE character_id=$p0 AND context=0 ORDER BY skill_id", peer.Id)) == primaryCommands,
                "无context指令请求编辑当前第二树，第一树全部指令保持");
            before = Snapshot();
            foreach (byte[] invalid in new byte[][] { [0], [2], [], [1, 0], new byte[6] })
            {
                peer.Send(260, invalid); peer.Expect(1, 260, [0, 1, 0]); peer.Barrier();
                That(Snapshot() == before, "过期当前页、非法页和畸形切树均拒绝且连接继续");
            }
        }
        using (var peer = Open(fixture))
        {
            var login = peer.Select();
            That(login[2].Body[^9] == 1, "重登Actor恢复第二方案及开通资格");
            var trees = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(trees[0].Skills.Single(skill => skill.Id == 5).Level == 1 && trees[1].Skills.Single(skill => skill.Id == 5) is { Level: 2, Slot: 7, Command: "028004" },
                "NOTI19重登恢复两套独立技能、快捷槽和第二树指令");
            peer.Send(260, [1, 0, 0, 0, 0]); peer.Expect(1, 260, [1, 0]); Changed(peer, 0);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(4); peer.Barrier();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_tree_unlock BEFORE UPDATE OF selected_skill_tree ON characters WHEN NEW.selected_skill_tree=0 BEGIN SELECT RAISE(ABORT,'tree unlock failure'); END");
            peer.Send(64, purchase); peer.Closed();
            fixture.Sql("DROP TRIGGER fail_tree_unlock");
            That(Snapshot() == before, "商城开通SQL失败不提前回成功，旧券、技能、权益及审计整笔回滚");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1); peer.Barrier();
            long balance = Balance();
            peer.Send(64, purchase); Bought(peer, product.CommodityId);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, 68, 821, 1); Changed(peer, 0);
            That(Balance() == balance && fixture.Query("SELECT selected_skill_tree FROM characters WHERE id=$p0", peer.Id) is [[0L]],
                "旧误发背包券只兑换一张，免再次扣款并保留多余券");
            string before = Snapshot();
            peer.Send(64, purchase); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            That(Snapshot() == before, "已开通后不消费剩余券");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(3); peer.Barrier();
            peer.Send(64, purchase); Bought(peer, product.CommodityId); peer.Expect(0, 53); Changed(peer, 0);
            That(Balance() == 0, "过期券角色仍须支付完整商品价格，登录既有过期清理规则继续生效");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(2); peer.Barrier();
            string before = Snapshot();
            peer.Send(64, purchase); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            That(Snapshot() == before, "仓库券不跨容器消费，余额不足时不自动开通");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            var login = peer.Select(1);
            That(login[2].Body[^9] == 0, "服务重启后保留旧券兑换开通结果");
            peer.Barrier();
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "商城权益和切换链路保持数据库完整性");

        void Changed(TcpPeer peer, byte selected)
        {
            var actor = peer.Expect(0, 2).Body;
            That(actor[0] == 0 && actor[^9] == selected, "已提交Actor立即同步开通及当前方案");
            That(peer.Expect(0, 2).Body[0] == 1, "随后刷新本人属性与双树技能");
            peer.Barrier();
        }
        long Balance() => Convert.ToInt64(fixture.Query("SELECT coalesce((SELECT balance FROM account_currency_balances WHERE currency_kind=15),0)")[0][0]);
        string Snapshot() => PurchaseSnapshot(fixture) + JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_skill_points ORDER BY character_id,context")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_skill_bindings ORDER BY character_id,context,slot")) +
            JsonSerializer.Serialize(fixture.Query("SELECT id,selected_skill_tree FROM characters ORDER BY id"));
    }
}
