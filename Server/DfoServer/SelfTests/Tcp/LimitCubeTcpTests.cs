using static DfoServer.Testing.CashMileageTcpChecks;
using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class LimitCubeTcpTests
{
    const short TargetSlot = 95;
    const short TicketSlot = 85;

    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await RunAsync(options, root, resources, null);
        await RunShenxuanTicketAsync(options, root, resources);
        await RunCooldownTicketAsync(options, root, resources);
    }

    static async Task RunCooldownTicketAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        const int ticketId = 10305519;
        var items = resources.ItemResources;
        var definition = items.LimitCubes.Get(ticketId)!;
        int targetId = definition.Targets[0].ItemId;
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "limit-cube-cooldown"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.Create(owner, new("CooldownCube", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, new(InventoryListType.Main,85), new(ticketId,ItemKind.Consumable,2)),
                    new(0, new(InventoryListType.Main,9), InventoryRewardGrantService.Create(items.Items.Get(targetId), shopExpiration:true))])
            });
            repo.CommitInventory(owner, role, new(role.Inventory, role.Inventory with
                { AccountWallet = role.Inventory.AccountWallet.Set(CurrencyKind.ClearCube,30) }, "cooldown_cube_fixture"));
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            string before = JsonSerializer.Serialize(Read());
            fixture.Sql("CREATE TRIGGER fail_cooldown_cube BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='use_limit_cube' BEGIN SELECT RAISE(ABORT,'cooldown cube rollback'); END");
            Send(peer);
            peer.Closed();
            That(JsonSerializer.Serialize(Read()) == before, "真实冷却券SQL末段失败同时回滚源、目标、材料、产出和冷却");
            fixture.Sql("DROP TRIGGER fail_cooldown_cube");
        }
        int resultId = 0;
        using (var peer = Open(fixture))
        {
            peer.Select();
            Send(peer);
            WirePacket? ack = null;
            for (int n = 0; n < 16; n++)
            {
                var packet = peer.Read();
                if (packet is { Kind:1, Opcode:338 }) { ack = packet; break; }
                That(packet.Kind == 0, "真实冷却券仍先同步已提交资产再应答338");
            }
            That(ack is { Body.Length:12 } && ack.Body[0] == 1, "回滚后重试真实冷却券成功，不残留失败冷却");
            resultId = I32(ack!.Body,1);
            peer.Barrier();
            var saved = Read().Inventory;
            That(definition.Results.Any(row => row.ItemId == resultId) &&
                saved.Items.Single(row => row.Core.ItemId == ticketId).Core.Count == 1 &&
                saved.AccountWallet.Get(CurrencyKind.ClearCube) == 20 &&
                saved.ConsumableStates is [{ Kind:ConsumableStateKind.Cooldown, ItemId:ticketId, CoolGroup:-ticketId }],
                "真实券一次提交保存一件消费、完整结果、十个无色及独立冷却账本");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            That(Read().Inventory.Items.Any(row => row.Core.ItemId == resultId) &&
                Read().Inventory.Items.Single(row => row.Core.ItemId == ticketId).Core.Count == 1,
                "重连恢复真实冷却券产出和剩余源，不再次消费");
        }

        Character Read() { using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }); return repo.List(owner).Single(); }
        void Send(TcpPeer peer) => peer.Send(338, Build(writer => { writer.Write((short)9); writer.Write(targetId); writer.Write((short)85); }));
    }

    internal static Task RunShenxuanTicketAsync(ServerOptions options, string root, TcpTestResources resources) =>
        RunAsync(options, root, resources, null, shenxuan: true);

    internal static async Task RunPurchasedTicketsAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        foreach (var purchase in new[] { (CommodityId: 105682, Count: 1, Price: 300), (CommodityId: 105683, Count: 10, Price: 2000) })
            await RunAsync(options, root, resources, purchase);
    }

    static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources,
        (int CommodityId, int Count, int Price)? purchase, bool shenxuan = false)
    {
        int ticketId = shenxuan ? 490703356 : LimitCubeTests.TicketId;
        int targetId = shenxuan ? 490703265 : LimitCubeTests.TargetId;
        int initialTicketCount = shenxuan ? 3 : 1;
        var seedNow = DateTimeOffset.UtcNow;
        var itemResources = resources.ItemResources;
        var definition = itemResources.LimitCubes.Get(ticketId) ??
            throw new InvalidDataException("现场PVF缺少指定宝珠变更券规则");
        int requiredTarget = definition.RequiredTargetCount(targetId) ??
            throw new InvalidDataException("现场变更券不接受指定原珠");
        if (shenxuan)
        {
            That(definition.Targets.SequenceEqual(Enumerable.Range(490703265, 91).Select(id => new ItemMaterialCost(id, 1))) &&
                definition.Results.SequenceEqual(Enumerable.Range(490703265, 91).Select(id => new LimitCubeResultItem(id, 1, 1000))) &&
                definition.Materials.SequenceEqual(new[] { new ItemMaterialCost(3037, 10) }) &&
                CurrencyWallet.RewardKind(3037) == CurrencyKind.ClearCube,
                "356保留91颗原珠及结果各一、权重1000与十个账号无色的真实资源合同");
            foreach (int id in new[] { ticketId, targetId })
            {
                var template = itemResources.Items.Get(id);
                That(template.AllowsUse(95, 0) && template.RequiredSkill == 0 && !template.UseForbidden,
                    "356网络夹具满足原券与原珠的正常等级、职业和技能资格");
            }
        }
        if (purchase is { } product)
        {
            var actual = itemResources.CashShop.Product(product.CommodityId);
            That(actual is { ItemId: LimitCubeTests.TicketId, Section: "item etc" } &&
                actual.Count == product.Count && actual.CeraPrice == product.Price,
                "精确商城商品保留395券的原数量和300／2000点券价格");
            That(requiredTarget == 1 && definition.Materials.SequenceEqual(new[] { new ItemMaterialCost(3037, 10) }),
                "395用途保留一颗原珠和十个账号无色的真实费用");
        }
        short actualTicketSlot = TicketSlot;
        InventoryItem? purchasedTicket = null;
        Character? purchasedCharacter = null;
        string? purchasedSnapshot = null;
        InventoryItem? resultItem = null;
        string? resultSnapshot = null;
        await using var fixture = new TcpTestFixture(options,
            Path.Combine(root, shenxuan ? "limit-cube-shenxuan356" : purchase is { } sku ? "limit-cube-buy-" + sku.CommodityId : "limit-cube"),
            gm: shenxuan, resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var initial = BuildInventory();
            var created = repository.Create(owner, new("LimitCubeTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95,
                AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(0).Skills,
                Inventory = initial with { AccountWallet = AccountWallet.Empty, EpicPieces = EpicPieceBook.Empty }
            });
            repository.CommitInventory(owner, created, new(created.Inventory,
                created.Inventory with { AccountWallet = initial.AccountWallet, EpicPieces = initial.EpicPieces },
                "limit_cube_fixture"));
        }
        var seeded = shenxuan ? Read() : null;
        if (seeded is not null)
            That(seeded.Inventory.Find(new(InventoryListType.Main, TicketSlot))?.Core.Count == 3 &&
                seeded.Inventory.Find(new(InventoryListType.Main, TargetSlot))?.Core.Count == 1 &&
                seeded.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 13,
                "356从三张真实券、一颗原珠和十三个账号无色开始");
        await fixture.StartAsync();
        if (shenxuan)
        {
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "cash-shop-expiration off", out string reply) &&
                reply == "OK {\"ignoreAbsoluteExpiration\":false}",
                "356运行时显式保留资源绝对期限");
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "cash-shop-usable-period", out reply) &&
                reply == "OK {\"ignoreUsablePeriod\":false}",
                "356运行时保留资源相对期限");
        }

        if (purchase is { } offer)
        {
            if (offer.Count == 1)
            {
                using var peer = Open(fixture);
                peer.Select();
                string unchanged = Snapshot();
                fixture.Sql("CREATE TRIGGER fail_r23_purchase_late BEFORE INSERT ON currency_audit_log " +
                    "WHEN NEW.action_name='cash_shop_buy' BEGIN SELECT RAISE(ABORT,'R23 purchase rollback'); END");
                Buy(peer, offer.CommodityId);
                peer.Closed();
                That(Snapshot() == unchanged, "精确SKU购买末段失败不扣点券、不创建券或推进UID和版本");
                fixture.Sql("DROP TRIGGER fail_r23_purchase_late");
            }
            using (var peer = Open(fixture))
            {
                peer.Select();
                var before = Read();
                var started = DateTimeOffset.UtcNow;
                var mileage = MileageBefore(fixture, ExpectedCashMileage(itemResources.CashShop.Product(offer.CommodityId)!));
                Buy(peer, offer.CommodityId);
                var ack = peer.Expect(1, 64).Body;
                That(ack.Length == 24 && ack[0] == 1 && U32(ack, 6) == offer.CommodityId,
                    "精确SKU购买先返回原完整商城ACK");
                var rows = Rows(peer.Expect(0, 14).Body).ToArray();
                var balance = peer.Expect(0, 53).Body;
                That(balance.Length == 13 && balance[0] == 1 && U32(balance, 1) == 0,
                    "原商城一次购买只扣资源价格并返回实际点券余额");
                MileageAfter(peer, fixture, mileage);
                peer.Barrier();
                var ended = DateTimeOffset.UtcNow;
                purchasedCharacter = Read();
                purchasedTicket = purchasedCharacter.Inventory.Items.Single(row => row.Core.ItemId == LimitCubeTests.TicketId);
                actualTicketSlot = purchasedTicket.Position.Index;
                var template = itemResources.Items.Get(LimitCubeTests.TicketId);
                var expected = new ItemCore(LimitCubeTests.TicketId, template.Kind, offer.Count)
                {
                    Durability = template.Durability,
                    SealFlag = template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0
                };
                That(purchasedTicket.Uid > before.Inventory.Items.Max(row => row.Uid) &&
                    purchasedTicket.Position.List == InventoryListType.Main &&
                    (purchasedTicket.Core with { ExpireTime = 0 }) == expected &&
                    purchasedTicket.ExpireTime >= template.Expiration.Resolve(started, ignoreAbsoluteExpiration: true) &&
                    purchasedTicket.ExpireTime <= template.Expiration.Resolve(ended, ignoreAbsoluteExpiration: true) &&
                    purchasedCharacter.Revision == before.Revision + 1 &&
                    purchasedCharacter.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 0 &&
                    before.Inventory.AccountWallet.Get(CurrencyKind.Cera) == offer.Price &&
                    purchasedCharacter.Inventory.Find(new(InventoryListType.Main, TargetSlot)) == before.Inventory.Find(new(InventoryListType.Main, TargetSlot)) &&
                    purchasedCharacter.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 10 &&
                    rows.Count(row => U16(row, 0) == actualTicketSlot && I32(row, 2) == LimitCubeTests.TicketId) == 1,
                    "105682／105683按服务器商品数量创建完整新UID券，目标与无色在购买阶段不消耗");
                purchasedSnapshot = JsonSerializer.Serialize(purchasedCharacter);
            }
        }

        if (purchase is null || purchase.Value.Count == 1)
        using (var peer = Open(fixture))
        {
            if (purchase is null)
            {
                Send(peer, actualTicketSlot, targetId);
                peer.Expect(1, 338, [0, 1, 0]);
            }
            peer.Select();
            if (purchase is null)
            {
                peer.Send(338, new byte[7]);
                peer.Expect(1, 338, [0, 1, 0]);
            }
            peer.Barrier();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_limit_cube_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='use_limit_cube' BEGIN SELECT RAISE(ABORT,'limit cube rollback'); END");
            Send(peer, actualTicketSlot, targetId);
            peer.Closed();
            That(Snapshot() == before,
                "CMD338审计末段失败关闭原连接并回滚目标、券、B材料、钱包、结果及UID");
            fixture.Sql("DROP TRIGGER fail_limit_cube_late");
        }

        int resultItemId;
        short resultSlot;
        byte[] resultRow;
        using (var peer = Open(fixture))
        {
            peer.Select();
            var beforeUse = shenxuan ? Read() : null;
            var useStarted = DateTimeOffset.UtcNow;
            Send(peer, actualTicketSlot, targetId);
            var deltas = new List<byte[]>();
            WirePacket ack = null!;
            for (int packets = 0; packets < 16; packets++)
            {
                var packet = peer.Read();
                if (packet is { Kind: 1, Opcode: 338 })
                {
                    ack = packet;
                    break;
                }
                That(packet.Kind == 0, "CMD338成功ACK之前只允许发布已提交通知");
                if (packet.Opcode == 14)
                    deltas.Add(packet.Body);
            }
            var response = ack ?? throw new InvalidOperationException("CMD338未返回成功ACK");
            That(response.Body.Length == 12 && response.Body[0] == 1 && response.Body[11] == 1,
                "CMD338成功ACK在全部投影后到达且保持完整12B");
            resultItemId = I32(response.Body, 1);
            ushort resultCount = U16(response.Body, 5);
            uint rawSlot = U32(response.Body, 7);
            That(rawSlot <= short.MaxValue && definition.Results.Any(row =>
                    row.ItemId == resultItemId && row.Count == resultCount),
                "成功ACK结果编号、数量和槽位来自现场PVF的实际抽取");
            resultSlot = (short)rawSlot;
            var matching = deltas.SelectMany(Rows)
                .Where(row => U16(row, 0) == resultSlot && I32(row, 2) == resultItemId).ToArray();
            That(deltas.Count != 0 && matching.Length == 1,
                "结果实体所在NOTI14严格先于ACK338，客户端查槽时已存在");
            resultRow = matching[0];
            peer.Barrier();
            var useEnded = DateTimeOffset.UtcNow;

            if (beforeUse is not null && seeded is not null)
            {
                var saved = Read();
                resultItem = saved.Inventory.Find(new(InventoryListType.Main, resultSlot))!;
                var originalTicket = beforeUse.Inventory.Find(new(InventoryListType.Main, TicketSlot))!;
                var originalTarget = beforeUse.Inventory.Find(new(InventoryListType.Main, TargetSlot))!;
                var remaining = originalTicket with { Core = originalTicket.Core with { Value = 2 } };
                var template = itemResources.Items.Get(resultItemId);
                var expectedCore = new ItemCore(resultItemId, template.Kind, 1)
                {
                    Durability = template.Durability,
                    SealFlag = template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0
                };
                var expected = new InventoryItem(resultItem.Uid, resultItem.Position,
                    expectedCore with { ExpireTime = resultItem.ExpireTime });
                int earliestExpiry = template.Expiration.Resolve(useStarted);
                int latestExpiry = template.Expiration.Resolve(useEnded);
                That(resultItem.Uid > beforeUse.Inventory.Items.Max(row => row.Uid) &&
                    resultItem == expected &&
                    resultItem.ExpireTime >= earliestExpiry && resultItem.ExpireTime <= latestExpiry,
                    "356按实际91项抽取产生完整新UID珠，全部核心/详情字段与严格期限保持资源规则");
                That(saved.Inventory.Items.Length == 2 &&
                    saved.Inventory.Items.All(row => row.Uid != originalTarget.Uid) &&
                    saved.Inventory.Find(new(InventoryListType.Main, TicketSlot)) == remaining &&
                    saved.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 3 &&
                    saved.Revision == beforeUse.Revision + 1,
                    "356只消费一颗原珠、一张同UID券及十个账号无色，余两券/三无色且只提交一次版本");
                var expectedInventory = beforeUse.Inventory with
                {
                    Items = saved.Inventory.Items,
                    AccountWallet = beforeUse.Inventory.AccountWallet.Set(CurrencyKind.ClearCube, 3) with
                    {
                        Revision = checked(beforeUse.Inventory.AccountWallet.Revision + 1)
                    }
                };
                That(JsonSerializer.Serialize(saved.Inventory) == JsonSerializer.Serialize(expectedInventory),
                    "356除原珠、券、结果和账号无色外完整库存不变");
                resultSnapshot = JsonSerializer.Serialize(saved.Inventory);
            }

            if (purchase is { } paid && purchasedTicket is not null && purchasedCharacter is not null)
            {
                var saved = Read();
                resultItem = saved.Inventory.Find(new(InventoryListType.Main, resultSlot))!;
                var remaining = saved.Inventory.Items.SingleOrDefault(row => row.Uid == purchasedTicket.Uid);
                var template = itemResources.Items.Get(resultItemId);
                var expected = new ItemCore(resultItemId, template.Kind, 1)
                {
                    Durability = template.Durability,
                    SealFlag = template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0
                };
                That(resultItem is { Uid: > 0 } && resultItem.Uid > purchasedCharacter.Inventory.Items.Max(row => row.Uid) &&
                    (resultItem.Core with { ExpireTime = 0 }) == expected &&
                    saved.Inventory.Items.All(row => row.Uid != purchasedCharacter.Inventory.Find(new(InventoryListType.Main, TargetSlot))!.Uid) &&
                    (paid.Count == 1 ? remaining is null : remaining == (purchasedTicket with { Core = purchasedTicket.Core with { Value = 9 } })) &&
                    saved.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 0 &&
                    saved.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 0 && saved.Revision == purchasedCharacter.Revision + 1 &&
                    saved.Inventory.Items.Select(row => row.Uid).Distinct().Count() == saved.Inventory.Items.Length &&
                    JsonSerializer.Serialize(purchasedCharacter) == purchasedSnapshot,
                    "真实购买券一次变更完整发出一颗新UID珠；单件耗尽，十件只扣一张并保留同UID九张，购买快照不变");
                resultSnapshot = JsonSerializer.Serialize(saved.Inventory);
            }

            string committed = Snapshot();
            Send(peer, actualTicketSlot, targetId);
            peer.Expect(1, 338, [0, 1, 0]);
            peer.Barrier();
            That(Snapshot() == committed, "旧目标槽和已扣材料的相同请求不重复扣耗或发放结果");
        }

        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            var main = entry.Single(packet => packet is { Kind: 0, Opcode: 13 } &&
                packet.Body.Length >= 5 && packet.Body[0] == (byte)InventoryListType.Main).Body;
            var restored = FullRows(main).Single(row => U16(row, 0) == resultSlot);
            Bytes(restored, resultRow, "重连从数据库恢复ACK引用槽的完整结果实例");
            peer.Barrier();
        }
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var saved = repository.List(owner).Single();
            That(saved.Inventory.Items.Where(row => row.Core.ItemId == ticketId).Sum(row => row.Core.Count) == (purchase?.Count ?? initialTicketCount) - 1 &&
                saved.Inventory.Find(new(InventoryListType.Main, resultSlot))?.Core.ItemId == resultItemId,
                "持久快照精确消费一张变更券并保存实际结果");
            if (shenxuan)
            {
                var reopened = new CharacterRepository(fixture.Database).List(owner).Single();
                That(reopened.Inventory.Find(new(InventoryListType.Main, resultSlot)) == resultItem &&
                    JsonSerializer.Serialize(reopened.Inventory) == resultSnapshot &&
                    JsonSerializer.Serialize(saved.Inventory) == resultSnapshot,
                    "356重连与新Repository完整恢复两张同UID券、实际结果、严格期限和全部钱包快照");
            }
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "变更券事务完成后数据库完整且无孤立实例");
        await fixture.StopAsync();
        That(fixture.Events().Any(row => row.GetProperty("Kind").GetString() == "startup" &&
            row.GetProperty("Detail").TryGetProperty("Stage", out var stage) && stage.GetString() == "Independent attribute pairs" &&
            row.GetProperty("Detail").GetProperty("ValidatedFamilies").GetInt32() == 1),
            "Runtime完成必需属性族装配；停服后读取日志，不与写入句柄争用");

        if (shenxuan)
        {
            var detail = fixture.Events().Single(row => row.GetProperty("Kind").GetString() == "limit_cube_use" &&
                row.GetProperty("Detail").GetProperty("Accepted").GetBoolean()).GetProperty("Detail");
            long roll = detail.GetProperty("Roll").GetInt64();
            That(detail.GetProperty("Ticket").GetInt32() == ticketId &&
                detail.GetProperty("Target").GetInt32() == targetId &&
                detail.GetProperty("Result").GetInt32() == resultItemId &&
                detail.GetProperty("TotalWeight").GetInt64() == 91000 &&
                roll is >= 0 and < 91000 &&
                resultItemId == 490703265 + (int)(roll / 1000) &&
                detail.GetProperty("SelectionSummary").ValueKind == JsonValueKind.Null,
                "356沿原91项权重单抽提交实际结果，不使用2020券的独立属性两抽策略");
        }

        if (purchase is not null)
        {
            var detail = fixture.Events().Single(row => row.GetProperty("Kind").GetString() == "limit_cube_use" &&
                row.GetProperty("Detail").GetProperty("Accepted").GetBoolean()).GetProperty("Detail");
            var summary = detail.GetProperty("SelectionSummary");
            That(detail.GetProperty("TotalWeight").GetInt64() == 121 &&
                summary.GetProperty("SelectionMode").GetString() == "independent-attribute-pair" &&
                summary.GetProperty("Bound").GetInt32() == 11 && summary.GetProperty("DrawCalls").GetInt32() == 2,
                "购买后的真实338沿已核收两次独立draw11策略及原提交日志，不退回66组合单抽");
            await fixture.StartAsync("purchased-ticket-restart");
            using (var peer = Open(fixture))
            {
                var entry = peer.Select();
                var main = entry.Single(packet => packet is { Kind: 0, Opcode: 13 } &&
                    packet.Body.Length >= 5 && packet.Body[0] == (byte)InventoryListType.Main).Body;
                Bytes(FullRows(main).Single(row => U16(row, 0) == resultSlot), resultRow,
                    "同一购买结果在Runtime重建后仍保持完整123B行");
                peer.Barrier();
            }
            await fixture.StopAsync();
            var reopened = new CharacterRepository(fixture.Database);
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            var restored = reopened.List(owner).Single();
            That(restored.Inventory.Find(new(InventoryListType.Main, resultSlot)) == resultItem &&
                JsonSerializer.Serialize(restored.Inventory) == resultSnapshot,
                "新Repository恢复购买、消费、同UID剩余券、完整结果及两个钱包的实际提交快照");
        }

        Character Read()
        {
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repository.List(owner).Single();
        }

        InventorySnapshot BuildInventory()
        {
            var rows = new List<InventoryItem>
            {
                new(0, new(InventoryListType.Main, TicketSlot),
                    InventoryRewardGrantService.Create(itemResources.Items.Get(ticketId), initialTicketCount,
                        seedNow, shopExpiration: !shenxuan)),
                new(0, new(InventoryListType.Main, TargetSlot),
                    InventoryRewardGrantService.Create(itemResources.Items.Get(targetId),
                        requiredTarget, seedNow, shopExpiration: !shenxuan))
            };
            if (purchase is not null)
                rows.RemoveAt(0);
            var used = new HashSet<InventorySlot>(rows.Select(row => row.Position));
            AccountWallet wallet = AccountWallet.Empty;
            EpicPieceBook pieces = EpicPieceBook.Empty;
            int reviveCoins = 0;
            int winPoints = 0;
            foreach (var cost in definition.Materials)
            {
                if (CurrencyWallet.RewardKind(cost.ItemId) is { } currency)
                {
                    if (CurrencyWallet.IsAccount(currency))
                        wallet = wallet.Set(currency, checked(cost.Count + (shenxuan ? 3 : 0)));
                    else if (currency == CurrencyKind.ReviveCoin)
                        reviveCoins = checked(reviveCoins + cost.Count);
                    else if (currency == CurrencyKind.WinPoint)
                        winPoints = checked(winPoints + cost.Count);
                    else
                        throw new InvalidDataException("变更券B条件不支持物品ID 0 的金币费用");
                    continue;
                }
                if (itemResources.EpicPieces.ByPiece.TryGetValue(cost.ItemId, out var piece))
                {
                    pieces = pieces.Set(itemResources.EpicPieces, piece.Index, cost.Count);
                    continue;
                }
                var template = itemResources.Items.Get(cost.ItemId);
                That(ItemSlotBoundService.List(template.Kind) == InventoryListType.Main,
                    "现场B条件实体材料属于Main容器");
                int remaining = cost.Count;
                var range = ItemSlotBoundService.Range(template.Kind, 24);
                for (short slot = range.Start; remaining > 0 && slot <= range.End; slot++)
                {
                    var position = new InventorySlot(InventoryListType.Main, slot);
                    if (used.Contains(position))
                        continue;
                    int count = Math.Min(remaining, template.StackLimit);
                    rows.Add(new(0, position, InventoryRewardGrantService.Create(template, count,
                        shopExpiration: true)));
                    used.Add(position);
                    remaining -= count;
                }
                That(remaining == 0, "TCP夹具完整准备现场B条件实体材料");
            }
            if (purchase is { } offer)
                wallet = wallet.Set(CurrencyKind.Cera, offer.Price);
            return new([.. rows])
            {
                ReviveCoins = reviveCoins,
                WinPoints = winPoints,
                AccountWallet = wallet,
                EpicPieces = pieces
            };
        }

        string Snapshot() => JsonSerializer.Serialize(fixture.Query(
                "SELECT id,revision,gold FROM characters ORDER BY id")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT item_uid,list_type,slot_index FROM character_inventory_slots ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT * FROM account_wallets ORDER BY account")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT * FROM account_currency_balances ORDER BY account,currency_kind")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT * FROM account_epic_books ORDER BY account")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT * FROM inventory_audit_log ORDER BY audit_id")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT * FROM currency_audit_log ORDER BY audit_id")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT * FROM epic_piece_audit_log ORDER BY audit_id")) +
            JsonSerializer.Serialize(fixture.Query(
                "SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }

    static void Buy(TcpPeer peer, int commodityId)
    {
        byte[] body = new byte[19];
        body[2] = 1;
        body[5] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), checked((uint)commodityId));
        peer.Send(64, body);
    }

    static void Send(TcpPeer peer, short ticketSlot = TicketSlot, int targetItemId = LimitCubeTests.TargetId) => peer.Send(338, Build(writer =>
    {
        writer.Write((ushort)TargetSlot);
        writer.Write(targetItemId);
        writer.Write((ushort)ticketSlot);
    }));

    static IEnumerable<byte[]> Rows(byte[] body)
    {
        That(body.Length >= 3 && body[0] == (byte)InventoryListType.Main,
            "CMD338库存差量属于Main容器");
        int count = U16(body, 1);
        That(body.Length == 3 + count * 123, "CMD338库存差量保持普通123B物品行");
        for (int index = 0; index < count; index++)
            yield return body.AsSpan(3 + index * 123, 123).ToArray();
    }

    static IEnumerable<byte[]> FullRows(byte[] body)
    {
        int count = U16(body, 3);
        That(body.Length == 5 + count * 123, "重连Main全量保持普通123B物品行");
        for (int index = 0; index < count; index++)
            yield return body.AsSpan(5 + index * 123, 123).ToArray();
    }

    static int I32(byte[] body, int at) =>
        BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at));
}
