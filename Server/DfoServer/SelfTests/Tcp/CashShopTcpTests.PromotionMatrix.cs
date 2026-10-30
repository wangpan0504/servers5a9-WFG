using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class CashShopTcpTests
{
    // 商品和固定bonus取真实PVF；赠礼只启用已支持普通药品的测试规则，不代表候选外盒消费者通过。
    static async Task PromotionMatrixAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources;
        var shop = items.CashShop;
        (int Commodity, int ItemId, int Key, short Slot)[] coupons =
            [(108726, 490704645, 59, 66), (108727, 490705133, 60, 67)];
        var rules = new PromotionRules(new("tcp-permanent", "period-1", "v1", "test", "package"),
            10, 3, 31, 2058, 1, 5000,
            [new(105681, 490705385, 1), new(108726, 490705385, 1), new(108727, 490705385, 1)],
            Enumerable.Range(2, 9).Select(sequence => new PromotionTier(sequence, 2058, 1)).ToImmutableArray());
        CashShopPromotionCatalog Catalog() => CashShopPromotionCatalog.Parse(
            JsonSerializer.Serialize(new[] { new PromotionConfiguration(rules, true) }), shop, items.Items.Get,
            candidate => candidate.OverflowItemId == 2058 && candidate.Tiers.All(tier => tier.ItemId == 2058)
                ? null : "测试仅支持普通药品");
        var catalog = Catalog();
        That(rules.Products.All(row => catalog.Product(row.CommodityId)?.Availability == PromotionAvailability.Active) &&
            catalog.Product(105684) is null, "真实三个商品映射启用测试规则，105684不参加");
        var clock = new Inventory.DailyBoosterTestClock(DateTimeOffset.Parse("2035-12-31T23:59:59Z"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "promotion-matrix"), resources: resources,
            promotions: () => catalog, clock: clock);
        var repository = new CharacterRepository(fixture.Database, resources.Quests, promotions: () => catalog, promotionClock: clock);
        var owners = new AccountOwners();
        ushort fullCharacterId = 0;
        double? firstPurchaseMilliseconds = null;
        foreach (string account in new[] { "10000", "20001" })
        {
            using var owner = owners.Acquire(account, () => { });
            var character = repository.Create(owner, CharacterCreation.Prepare(account == "10000" ? "PromoMatrix" : "PromoSecond",
                resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
            var seeded = character.Inventory.Items;
            if (account == "10000")
                foreach (var coupon in coupons)
                {
                    var template = items.Items.Get(coupon.ItemId);
                    That(items.Items.CashShopCouponKey(coupon.ItemId) == coupon.Key &&
                        shop.AllowsCoupon(coupon.Key, coupon.Commodity) && template.StackLimit >= 64,
                        "保存原文的59/60券精确适用折扣SKU，真实模板支持64张源券");
                    var core = InventoryRewardGrantService.Create(template, 64, utcNow: clock.GetUtcNow()) with
                    { ExpireTime = checked((int)clock.GetUtcNow().AddDays(1).ToUnixTimeSeconds()) };
                    var sourceCoupon = new InventoryItem(0, new(InventoryListType.Main, coupon.Slot), core);
                    That(sourceCoupon.Core.IsStackable && sourceCoupon.Core.EquipmentLockId == 0 && !sourceCoupon.Core.IsRental &&
                        !sourceCoupon.IsExpired(clock.GetUtcNow().ToUnixTimeSeconds()) &&
                        InventoryAvatarPackageService.SourceEligible(character, sourceCoupon, template),
                        "真实券保留模板分类和使用资格，实例期限晚于2035矩阵时钟");
                    seeded = seeded.Add(sourceCoupon);
                }
            if (account == "20001") seeded = seeded.Add(new InventoryItem(0,
                new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(items.Items.Get(2058), 10)));
            repository.CommitInventory(owner, character, new(character.Inventory,
                CurrencyWallet.Set(character.Inventory, CurrencyKind.Cera, 1_000_000_000) with { Items = seeded }, "seed"));
            if (account != "10000") continue;
            var full = CharacterCreation.Prepare("PromoFull", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var fullCharacter = repository.Create(owner, full with { Inventory = full.Inventory with
            {
                Items = full.Inventory.Items.AddRange(Enumerable.Range(65, 56).Select(slot => new InventoryItem(0,
                    new(InventoryListType.Main, (short)slot), InventoryRewardGrantService.Create(items.Items.Get(2058), items.Items.Get(2058).StackLimit))))
                    .AddRange(Enumerable.Range(121, 56).Select(slot => new InventoryItem(0,
                    new(InventoryListType.Main, (short)slot), InventoryRewardGrantService.Create(items.Items.Get(10007367), items.Items.Get(10007367).StackLimit))))
            }});
            fullCharacterId = fullCharacter.Id;
            var recipient = new MailboxRecipient(fullCharacter.Id, account, fullCharacter.Name, fullCharacter.Job, fullCharacter.GrowType, fullCharacter.Level);
            for (int index = 0; index < MailboxService.StorageLimit; index++)
                That(repository.DeliverMail(new("full-storage-" + index, recipient, "系统", "保管箱夹具", 0, [],
                    clock.GetUtcNow().ToUnixTimeSeconds())).Accepted, "初始化真实保管箱邮件");
            fixture.Sql("UPDATE mailbox_messages SET state=3");
        }
        var seedSlot = fixture.Query("SELECT character_id,account,list_type,slot_index,item_uid FROM character_inventory_slots WHERE slot_index=65 AND character_id<>$p0 ORDER BY character_id DESC LIMIT 1", fullCharacterId).Single();
        fixture.Sql("DELETE FROM character_inventory_slots WHERE item_uid=$p0", seedSlot[4]);
        Reject<InvalidDataException>(() => PromotionDelivered(fixture, "20001"), "孤立实例不能被完整交付核验计为已发奖");
        fixture.Sql("INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES($p0,$p1,$p2,$p3,$p4)", seedSlot);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            foreach (int target in new[] { 1, 2, 10, 11, 12, 30, 31 })
            {
                int beforeCount = (int)Progress();
                Buy(peer, Enumerable.Range(beforeCount, target - beforeCount).Select(index => rules.Products[index % 3].CommodityId).ToArray());
                That(Progress() == target && Awards() == ExpectedAwards(target), "真实原Buy跨档至第" + target + "套，资格不漏不重");
                if (target == 10) clock.Advance(TimeSpan.FromSeconds(2));
            }
            That(fixture.Query("SELECT count(DISTINCT strftime('%Y',committed_at,'unixepoch')) FROM cash_shop_promotion_orders") is [[2L]],
                "远离历史销售期且跨日跨年累计持续，不按时间清零或停用");
            Buy(peer, Enumerable.Repeat(105681, 64).ToArray());
            That(Progress() == 95 && Awards() == ExpectedAwards(95), "64行跨档全部累计，64份促销实例和7封赠礼不截断");
            Buy(peer, [108727, 100004, 105684, 108726]);
            That(Progress() == 97, "非活动混车及105684主货正常购买，只累计两行活动商品");
            string before = Snapshot();
            foreach (var invalid in new[] { Cart([]), Cart(Enumerable.Repeat(105681, 65).ToArray()),
                Cart([105681], currency: 1), Cart([105681], gift: 1), Cart([105681], payment: 1) })
            {
                peer.Send(64, invalid); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
                That(Snapshot() == before, "非法支付或空/65行拒绝，全部权益保持原状");
            }
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2); peer.Select(1);
            long mails = Scalar("SELECT count(*) FROM mailbox_messages");
            Buy(peer, [105681, 105681]);
            That(Progress() == 99 && Scalar("SELECT count(*) FROM mailbox_messages") > mails &&
                Scalar("SELECT count(*) FROM mailbox_messages WHERE state=3") == MailboxService.StorageLimit,
                "同账号跨角共享计数；主包及保管箱满仍沿既有托管合同完整寄送主货和赠礼");
            That(Scalar("SELECT count(*) FROM mailbox_messages WHERE delivery_key LIKE 'inventory-overflow:%'") > 0,
                "满主包的真实主货进入原溢出邮件，不能只计算促销赠礼");
        }
        using (var peer = Open(fixture, "20001"))
        {
            peer.Select(); Buy(peer, [105681], "20001");
            That(Progress("20001") == 1 && Progress() == 99, "两个账号资格独立");
        }
        await fixture.StopAsync();
        string saved = Snapshot();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            peer.Select(1); peer.Barrier();
            That(Snapshot() == saved, "服务重建和登录不会再次发奖或重写购买事实");
            Buy(peer, [105681, 105681]);
            That(Progress() == 101, "相同包体的新请求仍是新购买");
            rules = rules with { Scope = rules.Scope with { Period = "period-2" } }; catalog = Catalog();
            Buy(peer, [105681, 105681]);
            That(Progress() == 2 && Scalar("SELECT count FROM cash_shop_promotion_progress WHERE account='10000' AND period='period-1'") == 101,
                "换活动期新计数与旧资格隔离");
            That(shop.Bonus(102925).Count == 2, "固定bonus成本样本保留真实PVF两项附赠");
            Buy(peer, [105681, 102925]);
            That(Progress() == 3 && shop.Bonus(102925).All(bonus => Scalar($"SELECT count(*) FROM mailbox_attachments a JOIN character_inventory_items i USING(item_uid) WHERE json_extract(i.item_core,'$.ItemId')={bonus.ItemId}") > 0),
                "活动混车的真实固定bonus随主货溢出寄信，仍完整提交全部权益");
            if (Environment.GetEnvironmentVariable("DFO_PROMOTION_COST") == "1")
            {
                var beforeState = PromotionDelivered(fixture, "10000");
                var beforeCost = beforeState.Quantities;
                int carts = await PromotionCostAsync(fixture, peer);
                var afterState = PromotionDelivered(fixture, "10000");
                VerifyPromotionDelivery(beforeState, afterState, peer.Id);
                var afterCost = afterState.Quantities;
                var expected = new Dictionary<int, long> { [490705385] = carts * 63L, [2058] = ExpectedAwards(3 + carts * 63) - ExpectedAwards(3) };
                var product = shop.Product(102925)!;
                expected[product.ItemId] = expected.GetValueOrDefault(product.ItemId) + carts * (long)product.Count;
                foreach (var bonus in shop.Bonus(102925)) expected[bonus.ItemId] = expected.GetValueOrDefault(bonus.ItemId) + carts * (long)bonus.Count;
                That(Progress() == 3 + carts * 63 && expected.All(row => afterCost.GetValueOrDefault(row.Key) - beforeCost.GetValueOrDefault(row.Key) == row.Value),
                    "成本轮所有主货、固定bonus及促销赠礼全量独立读库，不以少发奖满足预算");
            }
            if (Environment.GetEnvironmentVariable("DFO_PROMOTION_DELIVERY_REPLAY") == "1")
            {
                for (int index = 0; index < 42; index++) Buy(peer, [.. Enumerable.Repeat(105681, 63), 102925]);
                Console.WriteLine("Promotion delivery replay: 42 full carts verified by unique slot/mail reachability.");
            }
        }
        await fixture.StopAsync();
        var costs = Directory.EnumerateFiles(fixture.Root, "server.jsonl", SearchOption.AllDirectories).SelectMany(File.ReadLines)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).Where(entry => entry.GetProperty("Kind").GetString() == "cash_shop_buy")
            .Select(entry => entry.GetProperty("Detail")).Where(detail => detail.TryGetProperty("Cost", out _)).ToArray();
        That(costs.Any(detail => detail.GetProperty("Cost").GetProperty("OverflowInstances").GetInt32() >= 3 &&
            detail.GetProperty("Cost").GetProperty("OverflowMails").GetInt32() >= 1),
            "整车诊断明确包含主货与两项固定bonus的溢出成本");
        if (Environment.GetEnvironmentVariable("DFO_PROMOTION_COST_OUTPUT") is { Length: > 0 } output)
            File.WriteAllText(output + ".stages.json", JsonSerializer.Serialize(new { FirstPurchaseMilliseconds = firstPurchaseMilliseconds,
                DatabaseBytes = new FileInfo(fixture.Database).Length, Costs = costs }, new JsonSerializerOptions { WriteIndented = true }));
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "促销全矩阵数据库及外键完整");

        long Scalar(string sql) => (long)fixture.Query(sql)[0][0]!;
        long Progress(string account = "10000") => Scalar($"SELECT COALESCE((SELECT count FROM cash_shop_promotion_progress WHERE account='{account}' AND period='{rules.Scope.Period}'),0)");
        long Awards() => Scalar("SELECT count(*) FROM cash_shop_promotion_awards WHERE account='10000'");
        static int ExpectedAwards(int count) => Enumerable.Range(1, count).Count(number => number > 30 || number % 10 != 1);
        string Snapshot() => PurchaseSnapshot(fixture) + JsonSerializer.Serialize(fixture.Query("SELECT * FROM cash_shop_promotion_orders ORDER BY rowid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM cash_shop_promotion_progress ORDER BY rowid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM cash_shop_promotion_awards ORDER BY rowid"));
        Dictionary<CurrencyKind, long> Currencies(string account, ushort characterId)
        {
            using var owner = owners.Acquire(account, () => { });
            var character = repository.List(owner).Single(row => row.Id == characterId);
            return Enum.GetValues<CurrencyKind>().ToDictionary(kind => kind, kind => (long)CurrencyWallet.Get(character.Inventory, kind));
        }
        InventorySnapshot CouponInventory(string account, ushort characterId)
        {
            using var owner = owners.Acquire(account, () => { });
            return repository.List(owner).Single(row => row.Id == characterId).Inventory;
        }
        void Buy(TcpPeer peer, int[] commodities, string account = "10000")
        {
            var beforeState = PromotionDelivered(fixture, account);
            var before = beforeState.Quantities;
            var currencies = Currencies(account, peer.Id);
            long balance = Scalar($"SELECT balance FROM account_currency_balances WHERE account='{account}' AND currency_kind=15");
            int awarded = (int)(Progress(account) + commodities.Count(id => rules.Products.Any(row => row.CommodityId == id)));
            int giftCount = Enumerable.Range((int)Progress(account) + 1, awarded - (int)Progress(account)).Count(number => number > 30 || number % 10 != 1);
            var expected = new Dictionary<int, int>();
            var expectedCurrencies = new Dictionary<CurrencyKind, int>();
            var couponSources = new Dictionary<short, (InventoryItem Source, int Count)>();
            var inventory = CouponInventory(account, peer.Id);
            byte[] request = Cart(commodities);
            for (int index = 0; index < commodities.Length; index++)
            {
                int id = commodities[index];
                var product = shop.Product(id)!;
                AddReward(product.ItemId, product.Count);
                foreach (var bonus in shop.Bonus(id)) AddReward(bonus.ItemId, bonus.Count);
                var coupon = coupons.SingleOrDefault(row => row.Commodity == id);
                if (coupon.ItemId == 0) continue;
                var sourceCoupon = inventory.Find(new(InventoryListType.Main, coupon.Slot))
                    ?? throw new InvalidDataException("折扣购买缺少真实Main券源");
                int consumed = couponSources.GetValueOrDefault(coupon.Slot).Count + 1;
                That(sourceCoupon.Uid > 0 && sourceCoupon.Core.ItemId == coupon.ItemId && sourceCoupon.Core.Count > consumed,
                    "折扣行绑定指定正UID实体券且本车扣后仍有余量");
                couponSources[coupon.Slot] = (sourceCoupon, consumed);
                expected[coupon.ItemId] = expected.GetValueOrDefault(coupon.ItemId) - 1;
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(13 + index * 15), checked((uint)coupon.ItemId));
                BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(17 + index * 15), checked((ushort)coupon.Slot));
            }
            expected[2058] = expected.GetValueOrDefault(2058) + giftCount;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            peer.Send(64, request);
            foreach (int id in commodities)
            {
                var ack = peer.Expect(1, 64).Body; var bonuses = shop.Bonus(id);
                That(ack[0] == 1 && U32(ack, 6) == id && U16(ack, 22) == bonuses.Count && ack.Length == 24 + bonuses.Count * 8,
                    "逐行ACK商品、成功和固定bonus条数保持原协议");
                for (int index = 0; index < bonuses.Count; index++)
                    That(U32(ack, 24 + index * 8) == bonuses[index].ItemId && U32(ack, 28 + index * 8) == bonuses[index].Count,
                        "固定bonus逐项核对真实PVF");
            }
            var packet = peer.Read();
            if (packet is { Kind: 0, Opcode: 14 }) packet = peer.Read();
            That(packet is { Kind: 0, Opcode: 53 } && U32(packet.Body, 1) == balance - commodities.Sum(id => shop.Product(id)!.CeraPrice),
                "完整购买响应投影真实扣费余额");
            bool overflow = Scalar("SELECT count(*) FROM mailbox_messages WHERE delivery_key LIKE 'inventory-overflow:%'") != 0 && peer.Id == fullCharacterId;
            if (giftCount > 0 || overflow) peer.Expect(0, 99, [1, 0]);
            peer.Barrier();
            firstPurchaseMilliseconds ??= System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var afterState = PromotionDelivered(fixture, account);
            VerifyPromotionDelivery(beforeState, afterState, peer.Id);
            var after = afterState.Quantities;
            var afterInventory = CouponInventory(account, peer.Id);
            foreach (var (slot, used) in couponSources)
                That(afterInventory.Find(new(InventoryListType.Main, slot)) ==
                    (used.Source with { Core = used.Source.Core with { Value = used.Source.Core.Value - used.Count } }),
                    "指定券逐行精确扣1并保留同一UID、槽位、完整核心及期限，不改其余源属性");
            That(expected.All(row => after.GetValueOrDefault(row.Key) - before.GetValueOrDefault(row.Key) == row.Value),
                "主货、原固定bonus、促销实例在背包和全部邮件合计逐项完整：" +
                string.Join(';', expected.Select(row => $"{row.Key} expected={row.Value} actual={after.GetValueOrDefault(row.Key) - before.GetValueOrDefault(row.Key)}")));
            var currencyAfter = Currencies(account, peer.Id);
            That(expectedCurrencies.All(row => currencyAfter[row.Key] - currencies[row.Key] == row.Value),
                "非活动混车钱包奖励沿真实归属完整增加");
            That(Scalar($"SELECT balance FROM account_currency_balances WHERE account='{account}' AND currency_kind=15") ==
                balance - commodities.Sum(id => shop.Product(id)!.CeraPrice), "独立读库核点券");
            void AddReward(int itemId, int count)
            {
                if (CurrencyWallet.RewardKind(itemId) is { } kind)
                    expectedCurrencies[kind] = expectedCurrencies.GetValueOrDefault(kind) + count;
                else expected[itemId] = expected.GetValueOrDefault(itemId) + count;
            }
        }
    }

    // 只在共享只读资源/临时库启用精确策略；Stop/Start仅重建Runtime，不冒充进程重启或外盒开盒。
    public static Task PermanentContinuationAsync(ServerOptions source, string root, TcpTestResources resources) =>
        PermanentContinuationCoreAsync(source, root, resources, 0);

    // 两入口仅供受监督probe分两次OS进程调用；不走生产CLI，也不改变普通TCP组的启动方式。
    public static Task PermanentProcessSeedAsync(ServerOptions source, string root, TcpTestResources resources) =>
        PermanentContinuationCoreAsync(source, root, resources, 1);

    public static Task PermanentProcessResumeAsync(ServerOptions source, string root, TcpTestResources resources) =>
        PermanentContinuationCoreAsync(source, root, resources, 2);

    static async Task PermanentContinuationCoreAsync(ServerOptions source, string root, TcpTestResources resources, int processPhase)
    {
        int checks = Count;
        bool processSeed = processPhase == 1, processResume = processPhase == 2;
        string checkpointPath = Path.Combine(root, "permanent-process-checkpoint.json");
        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        int processId = currentProcess.Id;
        long creationTimeFileTimeUtc = currentProcess.StartTime.ToUniversalTime().ToFileTimeUtc();
        var checkpoint = processResume
            ? JsonSerializer.Deserialize<PermanentProcessCheckpoint>(File.ReadAllText(checkpointPath))
                ?? throw new InvalidDataException("缺少首进程已提交的原5000上限恢复检查点")
            : null;
        var items = resources.ItemResources;
        var shop = items.CashShop;
        var predecessor = Inventory.CashShopPromotionTests.PilotRules();
        var configuration = new PromotionConfiguration(predecessor, true);
        var catalog = CashShopPromotionCatalog.Parse(JsonSerializer.Serialize(new[] { configuration }),
            shop, items.Items.Get, _ => null);
        var entry = catalog.Activities.Single();
        That(entry.Error is null && entry.Availability == PromotionAvailability.Active &&
            entry.Policy is { IsPermanent: false } policy && policy.CounterScope == predecessor.Scope,
            "原5000上限TCP只在临时夹具启用原v1策略，累计继续原CounterScope");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "promotion-permanent"),
            resources: resources, promotions: () => catalog, sharedResourcePath: true);
        var repository = new CharacterRepository(fixture.Database, resources.Quests, promotions: () => catalog);
        var owners = new AccountOwners();
        ushort characterId;
        int beforeMain;
        if (processResume)
        {
            That(checkpoint!.ProcessId != processId && checkpoint.CreationTimeFileTimeUtc != creationTimeFileTimeUtc,
                "恢复阶段必须处于不同PID且不同创建时间的新OS进程；首进程退出及Job0由外层已绑定receipt核验");
            That(checkpoint.RulesSnapshot == CashShopPromotionCatalog.RuleSnapshot(predecessor, entry.Policy!) &&
                checkpoint.CharacterId > 0 && checkpoint.BeforeMain >= 0,
                "新进程重建同一冻结原5000上限catalog，不另造规则、累计或角色");
            characterId = SavedCharacter().Id;
            beforeMain = checkpoint.BeforeMain;
            That(characterId == checkpoint.CharacterId && !Directory.Exists(fixture.Options.LogDirectory) &&
                Directory.Exists(Path.Combine(fixture.Root, "process-seed-logs")),
                "同一临时Data原样恢复角色；首进程日志已保留归档，不删除ready现场或绕过fresh phase保护");
        }
        else
        {
            using (var owner = owners.Acquire("10000", () => { }))
            {
                var character = repository.Create(owner, CharacterCreation.Prepare("PermTcp", resources.Characters.Get(0),
                    resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
                character = repository.CommitInventory(owner, character, new(character.Inventory,
                    CurrencyWallet.Set(character.Inventory, CurrencyKind.Cera, 200000), "seed"));
                characterId = character.Id;
            }
            fixture.Sql("""
                INSERT INTO cash_shop_promotion_rules(campaign,period,realm,promotion_group,rules_version,rules_snapshot)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5);
                INSERT INTO cash_shop_promotion_progress(account,campaign,period,realm,promotion_group,count,revision)
                VALUES('10000',$p0,$p1,$p2,$p3,4999,9);
                """, predecessor.Scope.Campaign, predecessor.Scope.Period, predecessor.Scope.Realm,
                predecessor.Scope.Group, predecessor.Scope.RulesVersion, CashShopPromotionCatalog.RuleSnapshot(predecessor));
            beforeMain = SavedCharacter().Inventory.Items.Where(item => item.Core.ItemId == 490705385).Sum(item => item.Core.Count);
        }
        var carts = new[] { (Commodity: 105681, Price: 39900) };
        if (!processResume)
            That(fixture.Query("SELECT count,revision FROM cash_shop_promotion_progress") is [[4999L, 9L]],
                "原5000上限TCP从原4999/revision9开始，不先造新期累计");
        else
            That(Snapshot() == checkpoint!.Snapshot,
                "新进程启动监听前原库首单的库存、邮件、钱包、UID、审计及规则四表逐字恢复，不seed或复制库");
        await fixture.StartAsync();
        if (!processResume)
        {
            using (var peer = Open(fixture))
            {
                peer.Select();
                RejectCart(peer, [carts[0].Commodity, carts[0].Commodity], "原4999再购两套整车拒绝，不能部分扣费发货");
                Buy(peer, 0);
            }
        }
        string afterFirst = processResume ? checkpoint!.Snapshot : Snapshot();
        if (processSeed)
        {
            await fixture.StopAsync();
            Readback(1);
            That(Snapshot() == afterFirst && fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
                fixture.Query("PRAGMA foreign_key_check").Length == 0,
                "首单5000在Runtime正常关闭后全资产与四表无变化，临时库完整且没有部分外键");
            await ArchiveSeedLogs();
            using (var stream = new FileStream(checkpointPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, new PermanentProcessCheckpoint(processId, creationTimeFileTimeUtc,
                    characterId, beforeMain, CashShopPromotionCatalog.RuleSnapshot(predecessor, entry.Policy!), afterFirst));
            Console.WriteLine(JsonSerializer.Serialize(new { Target = "cash-shop-permanent-process-seed", Checks = Count - checks,
                Count = 5000, PaidCera = 39900, PromotionMails = 1, RuntimeRestarts = 0, ProcessRestarts = 0,
                ProcessId = processId, CreationTimeFileTimeUtc = creationTimeFileTimeUtc,
                State = "SEED_COMMITTED_AWAITING_PROCESS_EXIT", SharedReadOnlyPvf = true }));
            return;
        }
        using (var peer = Open(fixture))
        {
            CheckSelection(peer, 1);
            That(Snapshot() == afterFirst, processResume
                ? "不同OS进程的真实登录只恢复首单5000全部资产，不重复扣款、寄件或清累计"
                : "同Runtime真实断连重登只投影首单5000，不重复扣款、寄件或清累计");
            RejectCart(peer, [carts[0].Commodity], "达到5000后下一套整车拒绝，原资产和四表不变");
        }
        await fixture.StopAsync();
        string saved = Snapshot();
        await fixture.StartAsync("runtime-restarted");
        using (var peer = Open(fixture))
        {
            CheckSelection(peer, 1);
            That(Snapshot() == saved, "Stop/Start重建Runtime后5000及唯一订单资产原样恢复，越界拒绝不改变已提交事实");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "原5000上限TCP临时库与外键保持完整");
        Console.WriteLine(JsonSerializer.Serialize(new { Target = processResume
                ? "cash-shop-permanent-process-resume" : "cash-shop-permanent-continuation", Checks = Count - checks,
            Count = 5000, PaidCera = carts[0].Price, PromotionMails = 1,
            RuntimeRestarts = 1, ProcessRestarts = processResume ? 1 : 0,
            ProcessId = processId, CreationTimeFileTimeUtc = creationTimeFileTimeUtc,
            SeedProcessId = checkpoint?.ProcessId, SeedCreationTimeFileTimeUtc = checkpoint?.CreationTimeFileTimeUtc,
            SharedReadOnlyPvf = true }));

        async Task ArchiveSeedLogs()
        {
            // 新进程的fixture没有previousPhase；复用StartAsync原有同目录归档与Windows短暂共享冲突等待。
            string archived = Path.Combine(fixture.Root, "process-seed-logs");
            if (Directory.Exists(archived) || File.Exists(archived))
                throw new InvalidOperationException("首进程日志归档必须是新目录");
            var archiveWait = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try { Directory.Move(fixture.Options.LogDirectory, archived); break; }
                catch (IOException error) when (OperatingSystem.IsWindows() &&
                    (error.HResult & 0xFFFF) is 5 or 32 && archiveWait.Elapsed < TimeSpan.FromSeconds(5))
                {
                    // Runtime已正常退出；归档成功前不交付检查点或启动下一进程。
                    await Task.Delay(25);
                }
            }
        }

        Character SavedCharacter()
        {
            using var owner = owners.Acquire("10000", () => { });
            return repository.LoadCharacter(owner, 0)!;
        }
        ImmutableArray<MailboxEntry> Mails()
        {
            using var owner = owners.Acquire("10000", () => { });
            return repository.LoadMailbox(owner, characterId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Entries;
        }
        string Snapshot() => PurchaseSnapshot(fixture) + string.Concat(new[] { "rules", "progress", "orders", "awards" }
            .Select(table => JsonSerializer.Serialize(fixture.Query("SELECT * FROM cash_shop_promotion_" + table + " ORDER BY rowid"))));
        void Readback(int purchases)
        {
            var character = SavedCharacter();
            var mails = Mails();
            That(fixture.Query("""
                SELECT campaign,period,realm,promotion_group,count,revision
                FROM cash_shop_promotion_progress WHERE account='10000'
                """
                ) is [[string campaign, string period, string realm, string group, long count, long revision]] &&
                campaign == predecessor.Scope.Campaign && period == predecessor.Scope.Period &&
                realm == predecessor.Scope.Realm && group == predecessor.Scope.Group && count == 4999 + purchases && revision == 9 + purchases,
                "原5000上限TCP只推进唯一原scope，绝不创建v2平行累计");
            That(character.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 200000 - carts.Take(purchases).Sum(cart => cart.Price) &&
                character.Inventory.Items.Where(item => item.Core.ItemId == 490705385).Sum(item => item.Core.Count) == beforeMain + purchases &&
                character.Inventory.Items.All(item => item.Uid > 0), "每单真实折扣价格全额扣费、主货和持久UID完整");
            That(fixture.Query("SELECT count(*) FROM cash_shop_promotion_orders WHERE account='10000'") is [[long orders]] && orders == purchases &&
                fixture.Query("SELECT global_purchase FROM cash_shop_promotion_awards WHERE account='10000' ORDER BY global_purchase")
                    .Select(row => (long)row[0]!).SequenceEqual(Enumerable.Range(5000, purchases).Select(number => (long)number)) &&
                mails.Length == purchases && mails.Select(mail => mail.Id).Distinct().Count() == purchases &&
                mails.All(mail => mail.RecipientId == characterId && mail.System && mail.Expires is null && mail.Attachments.Length == 1 &&
                    mail.Attachments[0].Item.Uid > 0 && mail.Attachments[0].Item.Core.ItemId == 490705415 && mail.Attachments[0].Item.Core.Count == 1) &&
                mails.SelectMany(mail => mail.Attachments).Select(attachment => attachment.Item.Uid).Distinct().Count() == purchases,
                "仅第5000套一笔合法新单、一封永久415×1邮件和独立UID，不漏发或多发");
        }
        void MainPacket(byte[] body, bool full, IEnumerable<InventoryItem> expected)
        {
            int header = full ? 5 : 3;
            int count = U16(body, full ? 3 : 1);
            That(body[0] == (byte)InventoryListType.Main && body.Length == header + count * 123 && expected.Any() &&
                expected.All(item => Enumerable.Range(0, count).Select(index => header + index * 123).Any(offset =>
                    U16(body, offset) == item.Position.Index && U32(body, offset + 2) == 490705385 && U32(body, offset + 6) == item.Core.Value)),
                "独立解析123B主包条目，网络库存与实际385槽位和完整数量一致");
        }
        void CheckSelection(TcpPeer peer, int purchases)
        {
            var packets = peer.Select();
            var main = SavedCharacter().Inventory.Items.Where(item => item.Core.ItemId == 490705385).ToArray();
            MainPacket(packets.Single(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body, true, main);
            var summaries = peer.Mailbox.SelectMany(body => MailboxTcpTests.ReadList(body).Summaries).ToArray();
            That(summaries.Length == purchases && summaries.All(summary => summary.ItemId == 490705415 &&
                    U32(summary.Core, 2) == 490705415 && U32(summary.Core, 6) == 1) &&
                summaries.Select(summary => summary.MailId).Order().SequenceEqual(Mails().Select(mail => mail.Id).Order()),
                "真实登录邮箱包恢复同一批415邮件及数量，不以只读仓储代替网络重连");
            peer.Barrier();
            Readback(purchases);
        }
        void RejectCart(TcpPeer peer, int[] commodities, string message)
        {
            string before = Snapshot();
            peer.Send(64, Cart(commodities));
            peer.Expect(1, 64, [0, 0, 0]);
            peer.Barrier();
            That(Snapshot() == before, message);
        }
        void Buy(TcpPeer peer, int index)
        {
            var cart = carts[index];
            That(shop.Product(cart.Commodity) is { ItemId: 490705385, Count: 1 } product && product.CeraPrice == cart.Price,
                "真实三SKU中的目标商品按独立已存折扣价表购买，每行资格一套");
            var beforeCharacter = SavedCharacter();
            var before = PromotionDelivered(fixture, "10000");
            peer.Send(64, Cart([cart.Commodity]));
            var bonuses = shop.Bonus(cart.Commodity);
            Bytes(peer.Expect(1, 64).Body, Build(writer =>
            {
                writer.Write((byte)1); writer.Write((byte)0); writer.Write(-1); writer.Write(cart.Commodity);
                writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(checked((ushort)bonuses.Count));
                foreach (var bonus in bonuses) { writer.Write(bonus.ItemId); writer.Write(bonus.Count); }
            }), "真实CMD64成功ACK逐字包含商品及全部原固定bonus");
            var afterCharacter = SavedCharacter();
            var changed = afterCharacter.Inventory.Items.Where(item => item.Core.ItemId == 490705385 &&
                !beforeCharacter.Inventory.Items.Contains(item)).ToArray();
            MainPacket(peer.Expect(0, 14).Body, false, changed);
            var cera = peer.Expect(0, 53).Body;
            That(cera.Length == 13 && cera[0] == 1 && U32(cera, 1) == 200000 - carts.Take(index + 1).Sum(row => row.Price),
                "库存之后的Cera包投影本单真实完整扣费");
            peer.Expect(0, 99, [1, 0]);
            peer.Barrier();
            var after = PromotionDelivered(fixture, "10000");
            VerifyPromotionDelivery(before, after, peer.Id);
            var expected = new Dictionary<int, long> { [490705385] = 1, [490705415] = 1 };
            foreach (var bonus in bonuses)
                if (CurrencyWallet.RewardKind(bonus.ItemId) is null)
                    expected[bonus.ItemId] = expected.GetValueOrDefault(bonus.ItemId) + bonus.Count;
            That(before.Quantities.Keys.Concat(after.Quantities.Keys).Concat(expected.Keys).Distinct().All(id =>
                    after.Quantities.GetValueOrDefault(id) - before.Quantities.GetValueOrDefault(id) == expected.GetValueOrDefault(id)),
                "主货和原固定bonus及415全量交付，不提前发385内部奖或遗留孤立UID");
            Readback(index + 1);
        }
    }

    sealed record PermanentProcessCheckpoint(int ProcessId, long CreationTimeFileTimeUtc,
        ushort CharacterId, int BeforeMain, string RulesSnapshot, string Snapshot);

    sealed record PromotionDeliveryState(Dictionary<int, long> Quantities,
        Dictionary<long, (ItemCore Core, long Recipient)> Items);

    static PromotionDeliveryState PromotionDelivered(TcpTestFixture fixture, string account)
    {
        // UNION ALL保留双引用；仅认可合法槽位和归属正确的未领附件，不把孤立实例算作交付。
        var references = fixture.Query("""
            SELECT i.item_uid,i.item_core,s.list_type,s.slot_index,c.id
            FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid)
            JOIN characters c ON c.id=s.character_id
            WHERE c.account=$p0 AND s.account=c.account AND i.account=c.account AND i.character_id=c.id
            UNION ALL
            SELECT i.item_uid,i.item_core,NULL,NULL,m.recipient_id
            FROM character_inventory_items i JOIN mailbox_attachments a USING(item_uid)
            JOIN mailbox_messages m USING(mail_id) JOIN characters c ON c.id=m.recipient_id
            WHERE m.recipient_account=$p0 AND c.account=m.recipient_account AND i.account=m.recipient_account
              AND i.character_id=m.recipient_id AND a.claimed=0 AND m.state IN (1,2,3)
            """, account);
        var items = new Dictionary<long, (ItemCore Core, long Recipient)>();
        foreach (var row in references)
        {
            var core = JsonSerializer.Deserialize<ItemCore>((string)row[1]!)!;
            if (row[2] is long list && !ItemSlotBoundService.Allows(InventorySnapshot.Empty,
                new((InventoryListType)list, checked((short)(long)row[3]!)), core.Kind))
                throw new InvalidDataException("实例不在合法容器槽位");
            if (!items.TryAdd((long)row[0]!, (core, (long)row[4]!)))
                throw new InvalidDataException("实例存在多个槽位或未领附件引用");
        }
        var allocated = fixture.Query("SELECT item_uid FROM character_inventory_items WHERE account=$p0", account)
            .Select(row => (long)row[0]!).ToHashSet();
        if (!allocated.SetEquals(items.Keys)) throw new InvalidDataException("已分配实例没有正确且唯一的交付归属");
        return new(items.Values.GroupBy(item => item.Core.ItemId).ToDictionary(group => group.Key,
            group => group.Sum(item => (long)item.Core.Count)), items);
    }

    static void VerifyPromotionDelivery(PromotionDeliveryState before, PromotionDeliveryState after, ushort recipient)
    {
        foreach (var (uid, item) in after.Items)
            if ((!before.Items.TryGetValue(uid, out var old) || old != item) && item.Recipient != recipient)
                throw new InvalidDataException("本次新增或叠加实例交给了错误角色");
        That(before.Items.Keys.All(after.Items.ContainsKey), "购买不丢弃原UID，叠加增量仍由原UID唯一持有");
    }

    static byte[] Cart(int[] commodities, byte currency = 0, byte gift = 0, byte payment = 0)
    {
        byte[] body = new byte[4 + commodities.Length * 15]; body[0] = currency; body[1] = gift; body[2] = checked((byte)commodities.Length);
        for (int index = 0; index < commodities.Length; index++)
        {
            body[4 + index * 15] = payment;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7 + index * 15), checked((uint)commodities[index]));
        }
        return body;
    }
}
