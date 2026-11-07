using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using PvfLib;

namespace DfoServer;

// 移植自独立GM工具的扩展命令：真实资源下的搜索、货币、发放、删除、邮箱、任务链与在线路由。
internal static class DevelopmentGmExtendedTests
{
    public static void RunResources(TownCatalog towns, ItemResources resources, PvfArchive pvf, QuestCatalog quests, DfoServer.GameWorld.Characters.CharacterCatalog characters)
    {
        int before = TestCheck.Count;
        var stopping = new DevelopmentCatalog(pvf, resources, quests);
        stopping.StartWarmup(attributesToo: false);
        Task stopWarmup = stopping.StopWarmupAsync();
        TestCheck.That(ReferenceEquals(stopWarmup, stopping.StopWarmupAsync()), "并发停机等待同一个索引退出屏障");
        stopWarmup.GetAwaiter().GetResult();
        bool rejected = false;
        try { stopping.WarmAttributes(TimeSpan.FromSeconds(1)); }
        catch (ObjectDisposedException) { rejected = true; }
        TestCheck.That(rejected && !stopping.AttributesReady, "停机后拒绝启动尚未建立的属性索引，不再借用PVF");
        using var f = new MailboxPersistenceTests.Fixture();
        f.OtherOwner.Dispose();
        var mailbox = new MailboxService(f.Repository, resources.Items, resources.GoldLimits, resources.EpicPieces);
        var catalog = new DevelopmentCatalog(pvf, resources, quests) { Stats = new DfoServer.Game.Characters.CharacterStatComputer(characters) };
        MarriagePersistenceTests.RunDirectResources(catalog);
        string Answer(string line, GameSession? session = null) => DevelopmentGm.Answer(line, "other", f.Repository, f.Owners,
            _ => session, mailbox, resources.Items, catalog: catalog);
        JsonElement Json(string text)
        {
            TestCheck.That(text.StartsWith("OK ", StringComparison.Ordinal), "扩展GM命令成功: " + text);
            return JsonDocument.Parse(text[3..]).RootElement;
        }
        Character Load()
        {
            using var lease = f.Owners.Acquire("other", () => { });
            return f.Repository.LoadCharacter(lease, 0);
        }
        string first = Answer("item-search 0 0 0 * * 18400");
        TestCheck.That(first.StartsWith("OK ") || first.Contains("后台构建", StringComparison.Ordinal), "索引首次查询不阻塞GM端口");
        catalog.Warm(true, true, TimeSpan.FromMinutes(10));

        foreach (string invalid in new[] { "item-search", "currency Other gold set 1", "currency Other cera mul 1",
            "currency Other cera set -1", "grant Other 18400 1 32 0 direct", "grant Other 18400 1 0 0 bag", "grant Other 18400 0 0 0 direct",
            "item-delete Other 0:9", "item-clear Other 3", "quest-batch Other 10100,10100", "quest-batch Other 0", "mail-delete Other x",
            "set-grant Other 0 0 0 direct", "wallet", "quest-chain Other 999999" })
            TestCheck.That(Answer(invalid).StartsWith("ERR "), "扩展GM参数严格拒绝: " + invalid);

        var search = Json(Answer("item-search 0 0 0 * * 18400"));
        TestCheck.That(search.GetProperty("items").EnumerateArray().Any(row => row.GetProperty("id").GetInt32() == 18400),
            "物品索引按ID命中真实模板");
        string name = resources.Items.Get(18400).Name;
        TestCheck.That(Json(Answer("item-search 0 0 0 * * " + name)).GetProperty("total").GetInt32() >= 1, "物品索引按名称检索");
        // 高级筛选：词条 / 期限 / 技能职业，索引首次使用时后台构建；这里等待构建完成后逐项核对。
        foreach (string bad in new[] { "item-filter 0 0 0 * * nope * *", "item-filter 0 0 0 * * * sometimes *", "item-filter 0 0 0 * * * * nojob", "item-filter 0 0 0 * * *" })
            TestCheck.That(Answer(bad).StartsWith("ERR "), "高级筛选严格拒绝: " + bad);
        _ = Answer("item-filter 0 0 0 * * physical_attack * *"); // 首次可能提示后台构建，但不得阻塞
        catalog.WarmAttributes(TimeSpan.FromMinutes(10));
        var attributes = catalog.Attributes;
        var withStat = Json(Answer("item-filter 0 0 0 * * physical_attack * *"));
        TestCheck.That(withStat.GetProperty("total").GetInt32() > 0 && withStat.GetProperty("items").EnumerateArray().All(row => attributes[row.GetProperty("id").GetInt32()].Stats.Contains("physical attack")),
            "词条筛选命中的物品都含该词条");
        var absolute = Json(Answer("item-filter 0 0 0 * * * abs *"));
        TestCheck.That(absolute.GetProperty("total").GetInt32() > 0 && absolute.GetProperty("items").EnumerateArray().All(row => attributes[row.GetProperty("id").GetInt32()].Period == 2), "绝对到期日筛选");
        var skillJobs = Json(Answer("item-filter 0 0 0 * * * * swordman"));
        TestCheck.That(skillJobs.GetProperty("total").GetInt32() > 0 && skillJobs.GetProperty("items").EnumerateArray().All(row =>
            attributes[row.GetProperty("id").GetInt32()] is { } attrs && (attrs.AllSkill || attrs.SkillJobs.Contains("swordman"))), "技能职业筛选");
        TestCheck.That(Json(Answer("item-filter 0 0 0 * * * none *")).GetProperty("total").GetInt32() > absolute.GetProperty("total").GetInt32(), "无期限筛选");
        // 预览：附魔、时装属性、技能加成、套装效果与绝对到期日都来自已加载 PVF。
        var extras = new DfoServer.Network.Management.GmItemExtras(pvf, null);
        JsonElement Preview(int id) => JsonSerializer.SerializeToElement(extras.Preview(id));
        var firstSetItem = catalog.Items.Where(row => row.Equipment && row.SetIndex > 0).Select(row => Preview(row.Id)).FirstOrDefault(value =>
            value.TryGetProperty("setBonus", out var set) && set.ValueKind == JsonValueKind.Object && set.GetProperty("tiers").GetArrayLength() > 0);
        TestCheck.That(firstSetItem.ValueKind == JsonValueKind.Object, "套装物品预览带有套装件数效果");
        var previewSample = catalog.Items.Where(row => !row.Equipment).Select(row => Preview(row.Id)).FirstOrDefault(value => value.GetProperty("enchant").GetArrayLength() > 0);
        TestCheck.That(previewSample.ValueKind == JsonValueKind.Object, "附魔物品预览带有附魔属性行");
        var avatarSample = catalog.Items.Where(row => row.Equipment && row.Tag.EndsWith("avatar", StringComparison.Ordinal)).Take(3000).Select(row => Preview(row.Id)).FirstOrDefault(value => value.GetProperty("avatarAbility").GetArrayLength() > 0);
        TestCheck.That(avatarSample.ValueKind == JsonValueKind.Object, "时装预览带有可选属性行");
        var skillSample = catalog.Items.Where(row => row.Equipment).Take(20000).Select(row => Preview(row.Id)).FirstOrDefault(value => value.GetProperty("skillBonus").GetArrayLength() > 0);
        TestCheck.That(skillSample.ValueKind == JsonValueKind.Object, "装备预览带有技能加成行");
        // 更新后的 PVF 只剩 stackable/event/chn_event_20141230_sheep_puzzle 的 10004550 保留 [expiration date]，不再按前200项扫描。
        var dated = catalog.Items.Where(row => row.Id == 10004550).Select(row => Preview(row.Id)).FirstOrDefault(value => value.GetProperty("expiration").GetProperty("absoluteExpireDate").GetString()!.Length > 0);
        TestCheck.That(dated.ValueKind == JsonValueKind.Object && DateTime.TryParse(dated.GetProperty("expiration").GetProperty("absoluteExpireDate").GetString(), out _), "绝对到期日按文本日期读取");
        var categories = Json(Answer("item-categories"));
        TestCheck.That(categories.GetProperty("categories").GetArrayLength() > 1 && categories.GetProperty("maxRefinement").GetInt32() > 0, "分类与锻造上限来自资源");
        RunSearchOptions(Answer, Json, catalog, extras, categories);

        TestCheck.That(Json(Answer("currency Other cera set 500")).GetProperty("currencies").EnumerateArray()
            .Single(row => row.GetProperty("key").GetString() == "cera").GetProperty("amount").GetInt64() == 500, "账号点券按设置提交");
        long walletRevision = Load().Inventory.AccountWallet.Revision;
        TestCheck.That(Answer("currency Other cera sub 600").StartsWith("ERR ") && Load().Inventory.AccountWallet.Revision == walletRevision, "余额不足整笔拒绝");
        Answer("currency Other cera add 100");
        Answer("currency Other revive add 3");
        var wallet = Load().Inventory;
        TestCheck.That(wallet.AccountWallet.Get(Game.Currency.CurrencyKind.Cera) == 600 && wallet.ReviveCoins == 3, "账号与角色货币分别写入各自钱包");
        long revision = Load().Revision;
        Answer("currency Other revive set 3");
        TestCheck.That(Load().Revision == revision, "同额设置不推进revision");

        // 黑钻：wallet 只读结果带账号级到期时间与开通标志；0 天清除。
        TestCheck.That(Json(Answer("wallet Other")).GetProperty("blackDiamond").GetBoolean() == false, "默认未开通黑钻");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string bdGrant = Answer("black-diamond Other 30");
        JsonElement afterOpen = Json(Answer("wallet Other"));
        long bdExpiry = afterOpen.GetProperty("blackDiamondExpiresAt").GetInt64();
        TestCheck.That(bdGrant.StartsWith("OK ", StringComparison.Ordinal) && afterOpen.GetProperty("blackDiamond").GetBoolean()
            && bdExpiry >= now + 29 * 86400 && bdExpiry <= now + 31 * 86400, "黑钻按现在+30天写入并在 wallet 暴露到期时间");
        TestCheck.That(Answer("black-diamond Other 0").StartsWith("OK ", StringComparison.Ordinal)
            && Json(Answer("wallet Other")).GetProperty("blackDiamond").GetBoolean() == false, "黑钻 0 天清除到期时间");

        // 副职业：只读给出当前类型/熟练度/等级；写入按 PVF [expertness exp] 阈值落该等级的最低熟练度。
        var noJob = Json(Answer("expert-job Other"));
        TestCheck.That(noJob.GetProperty("type").GetInt32() == 0 && noJob.GetProperty("level").GetInt32() == 0, "默认没有副职业");
        long jobRevision = Load().Revision;
        var jobFive = Json(Answer("expert-job Other 1 5"));
        var jobFiveRow = Load();
        TestCheck.That(jobFive.GetProperty("expertness").GetInt32() == 359 && jobFive.GetProperty("level").GetInt32() == 5
            && jobFiveRow.ExpertJobType == 1 && jobFiveRow.ExpertJobExperience == 359 && jobFiveRow.Revision == jobRevision + 1,
            "副职业 Lv5 按阈值写入 359 熟练度并推进 revision");
        var jobEleven = Json(Answer("expert-job Other 2 11"));
        TestCheck.That(jobEleven.GetProperty("expertness").GetInt32() == 1067 && jobEleven.GetProperty("level").GetInt32() == 11
            && Json(Answer("expert-job Other")).GetProperty("typeLabel").GetString() == "炼金术师" && Load().ExpertJobType == 2,
            "副职业可改类型并写到 11 级上限");
        foreach (string bad in new[] { "expert-job Other 0 1", "expert-job Other 5 1", "expert-job Other 1 0", "expert-job Other 1 12", "expert-job Other 1" })
            TestCheck.That(Answer(bad).StartsWith("ERR "), "副职业参数严格拒绝: " + bad);

        var fullMachine = Json(Answer("expert-job Other 3 11"));
        var fullMachineRow = Load();
        TestCheck.That(resources.ExpertJobs.TryRepair(3, 11, out _, out int fullEndurance) &&
            fullMachine.GetProperty("machineGrade").GetInt32() == 11 &&
            fullMachineRow.ExpertMachineGrade == 11 && fullMachineRow.ExpertMachineEndurance == fullEndurance &&
            resources.ExpertJobs.Level(3, fullMachineRow.ExpertJobExperience) == 11,
            "GM分解师满级同时持久化11级分解机及对应满耐久");
        using (var machineLease = f.Owners.Acquire("other", () => { }))
        {
            var machineSession = new GameSession(f.Repository, towns, machineLease);
            machineSession.Select(0);
            machineSession.MarkReady();
            var online = Json(Answer("expert-job Other 3 5", machineSession));
            var saved = f.Repository.LoadCharacter(machineLease, 0);
            TestCheck.That(resources.ExpertJobs.TryRepair(3, 5, out _, out int endurance) &&
                online.GetProperty("machineGrade").GetInt32() == 5 &&
                machineSession.Selected!.ExpertMachineGrade == 5 && machineSession.Selected.ExpertMachineEndurance == endurance &&
                saved.ExpertMachineGrade == 5 && saved.Revision == machineSession.Selected.Revision,
                "在线GM调整同步机器与Selected，重读存档不恢复旧机器等级");
            var stale = saved with { Revision = saved.Revision - 1 };
            TestCheck.Reject<InvalidOperationException>(() => f.Repository.SetExpertJob(machineLease, stale, 3, 0, (1, 300)),
                "陈旧GM快照不能分别写入熟练度与机器等级");
            var unchanged = f.Repository.LoadCharacter(machineLease, 0);
            TestCheck.That(unchanged.ExpertJobExperience == saved.ExpertJobExperience &&
                unchanged.ExpertMachineGrade == saved.ExpertMachineGrade && unchanged.ExpertMachineEndurance == saved.ExpertMachineEndurance &&
                unchanged.Revision == saved.Revision, "陈旧快照的熟练度、机器和revision整笔回滚");
        }
        Json(Answer("expert-job Other 2 11"));

        // 账号金库：未开通（容量0）拒绝；已开通后按容量档位上限校验，同账号共享。
        TestCheck.That(Answer("currency Other vault-gold set 1").StartsWith("ERR ") && Answer("currency Other vault-gold mul 1").StartsWith("ERR "), "金库未开通或操作非法时拒绝");
        int tierLimit;
        using (var cargoLease = f.Owners.Acquire("other", () => { }))
        {
            var current = f.Repository.LoadCharacter(cargoLease, 0);
            var tier = resources.Cargo.AccountTiers.First(value => value.GoldLimit > 0);
            var opened = current.Inventory with { AccountCargo = current.Inventory.AccountCargo with { Capacity = tier.Capacity } };
            f.Repository.CommitInventory(cargoLease, current, new InventoryPlan(current.Inventory, opened, "test_open_vault"));
            tierLimit = tier.GoldLimit;
        }
        {
            TestCheck.That(Json(Answer("currency Other vault-gold set 1000")).GetProperty("vault").GetProperty("gold").GetInt32() == 1000, "金库金币按设置提交");
            TestCheck.That(Answer("currency Other vault-gold sub 1001").StartsWith("ERR ") && Answer("currency Other vault-gold add " + tierLimit).StartsWith("ERR "), "金库金币不足或超档位上限整笔拒绝");
            TestCheck.That(Answer("currency Other vault-gold add 5").StartsWith("OK ") && Load().Inventory.AccountCargo.Gold == 1005, "金库金币增量写入");
        }
        var template = resources.Items.Get(18400);
        TestCheck.That(Answer("grant Other 18400 2 5 0 direct").StartsWith("OK "), "装备直接入包");
        var granted = Load().Inventory.Items.Where(item => item.Core.ItemId == 18400).ToArray();
        TestCheck.That(granted.Length == 2 && granted.All(item => (item.Core.Attr & 31) == 5 && item.Uid > 0), "直接入包保留强化和独立UID");
        var medalInfo = Json(Answer("item-info 100380058"));
        var medalEdit = Preview(100380058).GetProperty("edit");
        TestCheck.That(medalInfo.GetProperty("kind").GetString() == "GuildMedal" &&
            medalInfo.GetProperty("upgradable").GetBoolean() && medalInfo.GetProperty("maxUpgrade").GetInt32() == 20 &&
            !medalInfo.GetProperty("amplifiable").GetBoolean() && !medalInfo.GetProperty("refinable").GetBoolean() &&
            medalEdit.GetProperty("reinforce").GetBoolean() && medalEdit.GetProperty("maxUpgrade").GetInt32() == 20 &&
            !medalEdit.GetProperty("amplify").GetBoolean() && !medalEdit.GetProperty("refine").GetBoolean() &&
            Json(Answer("item-info 18400")).GetProperty("maxUpgrade").GetInt32() == 31,
            "勋章真实模板与预览能力统一最高20，仅强化，不借普通装备的增幅和锻造能力");
        foreach (int level in new[] { 0, 1, 20 })
            TestCheck.That(Answer($"grant Other 100380058 1 {level} 0 direct").StartsWith("OK ") &&
                Load().Inventory.Items.Any(item => item.Core.ItemId == 100380058 && item.Core.Upgrade == level),
                "GM勋章直发保留强化0/1/20");
        var medalItem = Load().Inventory.Items.Single(item => item.Core.ItemId == 100380058 && item.Core.Upgrade == 20);
        using (var medalOwner = f.Owners.Acquire("other", () => { }))
        {
            var current = f.Repository.LoadCharacter(medalOwner, 0);
            medalItem = medalItem with { Core = medalItem.Core with { Attr = 0xB4, SortLockFlag = 1, GuardianGemKeys = 1, Extension97 = 71 } };
            f.Repository.CommitInventory(medalOwner, current, new(current.Inventory,
                current.Inventory with { Items = current.Inventory.Items.Replace(current.Inventory.Find(medalItem.Position)!, medalItem) }, "gm_medal_fixture"));
        }
        string medalRef = $"{(int)medalItem.Position.List}:{medalItem.Position.Index}:{medalItem.Uid}";
        TestCheck.That(Answer("item-edit Other " + medalRef + " up:1").StartsWith("OK ") &&
            Load().Inventory.Find(medalItem.Position) == medalItem with { Core = medalItem.Core with { Attr = 0xA1 } } &&
            Answer("item-edit Other " + medalRef + " up:20").StartsWith("OK ") && Load().Inventory.Find(medalItem.Position) == medalItem,
            "GM勋章编辑仅改强化低五位，保留UID、排序锁、守护珠与完整扩展核心");
        TestCheck.That(Json(Answer("items Other")).GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("uid").GetInt64() == medalItem.Uid).GetProperty("upgrade").GetInt32() == 20,
            "GM背包投影读取勋章真实强化而非固定0");
        string medalSnapshot = f.Snapshot();
        foreach (string bad in new[] { "grant Other 100380058 1 21 0 direct", "grant Other 100380058 1 21 0 mail",
            "grant Other 100380058 1 -1 0 direct", "grant Other 100380058 1 20 1 direct", "grant Other 100380058 1 20 1 mail",
            "grant Other 100380058 1 20 0 direct amp:1:5", "grant Other 100380058 1 20 0 mail amp:1:5",
            "grant Other 90000 1 1 0 direct", "item-edit Other " + medalRef + " up:21",
            "item-edit Other " + medalRef + " up:-1", "item-edit Other " + medalRef + " amp:0:0",
            "item-edit Other " + medalRef + " ref:1" })
            TestCheck.That(Answer(bad).StartsWith("ERR ") && f.Snapshot() == medalSnapshot,
                "勋章超限/负数/增幅/锻造与守护珠强化拒绝后不改变资产、邮件或revision：" + bad);
        var beforeBatch = Load();
        TestCheck.That(!InventoryRewardGrantService.TryGrantBatch(beforeBatch, [new(100380058, 1, 20), new(90000, 1, 1)],
            resources.Items, resources.GoldLimits, resources.EpicPieces).Accepted && f.Snapshot() == medalSnapshot,
            "批量逐件检查真实强化能力，后项守护珠非法时不产生前项勋章半份资产");
        uint medalMail = Json(Answer("grant Other 100380058 1 20 0 mail")).GetProperty("mail").GetUInt32();
        TestCheck.That(Json(Answer("mails Other")).GetProperty("mails").EnumerateArray()
            .Single(mail => mail.GetProperty("id").GetUInt32() == medalMail).GetProperty("attachments")[0].GetProperty("upgrade").GetInt32() == 20,
            "GM勋章邮件投影保留强化20");
        // 封印：[sealing] 封装装备默认带封装标记；seal:open 直接发为已解封，对其他物品拒绝。
        var sealing = catalog.Items.Where(row => row.Equipment).Select(row => resources.Items.Get(row.Id))
            .FirstOrDefault(value => value.Kind == ItemKind.Equipment && value.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase));
        if (sealing is not null)
        {
            TestCheck.That(Answer($"grant Other {sealing.Id} 1 0 0 direct").StartsWith("OK "), "封装装备默认发放");
            TestCheck.That(Answer($"grant Other {sealing.Id} 1 0 0 direct seal:open").StartsWith("OK "), "封装装备直接发为已解封");
            var sealedItems = Load().Inventory.Items.Where(item => item.Core.ItemId == sealing.Id).OrderBy(item => item.Uid).ToArray();
            TestCheck.That(sealedItems.Length == 2 && sealedItems[0].Core.SealFlag == 1 && sealedItems[1].Core.SealFlag == 0, "默认保持封装标记，seal:open 只清除新实例的封装标记");
            TestCheck.That(Answer($"grant Other {sealing.Id} 1 0 0 mail seal:open").StartsWith("OK "), "邮件发放可带 seal:open");
        }
        TestCheck.That(Answer("grant Other 18400 1 0 0 direct seal:open").StartsWith("ERR ") && Answer("grant Other 18400 1 0 0 direct seal:close").StartsWith("ERR ") &&
            Answer("grant Other 18400 1 0 0 direct seal:open seal:open").StartsWith("ERR "), "非封装物品或非法可选参数拒绝");
        TestCheck.That(Answer("grant Other 3175 1 1 0 direct").StartsWith("ERR "), "非装备拒绝强化");
        TestCheck.That(Answer("grant Other 18400 1 0 1 direct").StartsWith(template.EquipmentSlot == 12 ? "OK " : "ERR "), "锻造只用于普通武器");
        TestCheck.That(Answer("grant Other 3175 7 0 0 mail").StartsWith("OK "), "堆叠材料经系统邮件发放");
        string ampAnswer = Answer("grant Other 18400 1 6 0 direct amp:3:120");
        TestCheck.That(ampAnswer.StartsWith("OK "), "增幅装备直接入包：" + ampAnswer);
        var amplified = Load().Inventory.Items.Single(item => item.Core.ItemId == 18400 && item.Core.AmplifyType == 3);
        TestCheck.That((amplified.Core.Attr & 31) == 6 && amplified.Core.AmplifyValue == 120, "增幅等级写入Attr低五位，类型与异次元值写入核心");
        TestCheck.That(Answer("grant Other 18400 1 4 0 mail amp:2:50").StartsWith("OK "), "增幅装备经邮件发放");
        TestCheck.That(new[] { "grant Other 3175 1 0 0 mail amp:1:5", "grant Other 18400 1 0 0 mail amp:5:5", "grant Other 18400 1 0 0 mail amp:1:0",
            "grant Other 18400 1 0 0 mail amp:0:0", "grant Other 18400 1 0 0 mail amp1:3" }.All(line => Answer(line).StartsWith("ERR ")), "增幅参数与非装备拒绝");
        string amplifiedRef = (int)amplified.Position.List + ":" + amplified.Position.Index + ":" + amplified.Uid;
        TestCheck.That(Answer("item-amplify Other " + amplifiedRef + " amp:4:300 12").StartsWith("OK "), "修改已有装备的增幅");
        var edited = Load().Inventory.Find(amplified.Position)!;
        TestCheck.That(edited.Uid == amplified.Uid && edited.Core.AmplifyType == 4 && edited.Core.AmplifyValue == 300 && (edited.Core.Attr & 31) == 12, "增幅修改保留UID");
        TestCheck.That(Answer("item-amplify Other " + amplifiedRef + " amp:0:0 7").StartsWith("OK ") &&
            Load().Inventory.Find(amplified.Position)!.Core is { AmplifyType: 0, AmplifyValue: 0, Attr: var reAttr } && (reAttr & 31) == 7, "改回强化清除异次元属性");
        TestCheck.That(Answer("item-amplify Other " + (int)amplified.Position.List + ":" + amplified.Position.Index + ":" + (amplified.Uid + 1) + " amp:1:1 1").StartsWith("ERR "), "UID不符拒绝修改增幅");

        var target = Load().Inventory.Items.First(item => item.Core.ItemId == 18400);
        int count = Load().Inventory.Items.Length;
        string reference = (int)target.Position.List + ":" + target.Position.Index + ":";
        TestCheck.That(Answer("item-delete Other " + reference + (target.Uid + 1)).StartsWith("ERR ") && Load().Inventory.Items.Length == count, "UID不符整批保留");
        TestCheck.That(Answer("item-delete Other " + reference + target.Uid).StartsWith("OK ") && Load().Inventory.Items.Length == count - 1, "单件删除");
        // 实际玩家的消耗品分类混有普通背包、宠物消耗品和账号金库。
        using (var cargoLease = f.Owners.Acquire("other", () => { }))
        {
            var current = f.Repository.LoadCharacter(cargoLease, 0);
            var after = current.Inventory with { Items = current.Inventory.Items.AddRange(new InventoryItem[] {
                new InventoryItem(0, new(InventoryListType.AccountCargo, 0), new(490013209, ItemKind.Consumable, 10)),
                new InventoryItem(0, new(InventoryListType.Pet, 189), new(24, ItemKind.CreatureConsumable, 20))}) };
            f.Repository.CommitInventory(cargoLease, current, new InventoryPlan(current.Inventory, after, "test_gm_mixed_delete"));
        }
        var mixedBefore = Load();
        var mixed = mixedBefore.Inventory.Items.Where(item => item.Position.List == InventoryListType.AccountCargo ||
            item.Position.List == InventoryListType.Pet && item.Position.Index == 189 || item.Core.ItemId == 3175).ToArray();
        string MixedRef(InventoryItem item) => $"{(int)item.Position.List}:{item.Position.Index}:{item.Uid}";
        string mixedRefs = string.Join(',', mixed.Select(MixedRef));
        TestCheck.That(Answer("item-delete Other " + mixedRefs + ",0:352:999999").StartsWith("ERR ") &&
            Load().Inventory.Items.SequenceEqual(mixedBefore.Inventory.Items), "混合容器包含失效UID时所有容器均不变");
        TestCheck.That(Answer("item-delete Other " + mixedRefs).StartsWith("OK "), "GM混合背包宠物及账号金库原子删除");
        var mixedAfter = Load();
        TestCheck.That(mixed.All(item => mixedAfter.Inventory.Find(item.Position) is null) &&
            mixedAfter.Inventory.AccountCargo.Revision > mixedBefore.Inventory.AccountCargo.Revision,
            "重新读库确认全部删除并提升共享金库修订");
        TestCheck.That(Json(Answer("item-clear Other 0")).GetProperty("deleted").GetInt32() > 0 &&
            Load().Inventory.Items.All(item => item.Position.List != InventoryListType.Main), "分类清理整栏原子删除");

        // 品级：quality:<1-100> 换算成客户端实例品级种子（1..99 = 999999893+p，100 = 999999998）。
        TestCheck.That(Json(Answer("grant Other 18400 1 0 0 direct quality:37")).GetProperty("quality").GetInt32() == 37 &&
            Load().Inventory.Items.Single(item => item.Core.ItemId == 18400).Core.Value == 999_999_930, "品级百分比写入实例种子");
        TestCheck.That(Json(Answer("grant Other 18400 1 0 0 direct quality:100")).GetProperty("quality").GetInt32() == 100 &&
            Load().Inventory.Items.Where(item => item.Core.ItemId == 18400).Select(item => item.Core.Value).OrderBy(value => value)
                .SequenceEqual([999_999_930, 999_999_998]), "100 品级使用最上级常量，未指定沿用默认");
        TestCheck.That(new[] { "grant Other 18400 1 0 0 direct quality:0", "grant Other 18400 1 0 0 direct quality:101",
            "grant Other 18400 1 0 0 direct quality:50 quality:60", "grant Other 3175 1 0 0 direct quality:0",
            "grant Other 3175 1 0 0 mail quality:101" }
            .All(line => Answer(line).StartsWith("ERR ")), "品级范围与重复参数拒绝");
        TestCheck.That(Answer("grant Other 3175 2 0 0 mail quality:100").StartsWith("OK ") &&
            Answer("grant Other 3175 2 0 0 direct quality:100").StartsWith("OK "), "旧版请求附带品级时非装备邮件和直接发放成功");
        TestCheck.That(Answer("grant Other 18400 1 0 0 mail quality:50").StartsWith("OK "), "邮件发放可带品级");
        // 可选参数叠加：amp + quality + period:off 一共 8 个参数，不能被命令行参数上限挡掉。
        var stacked = Json(Answer("grant Other 18400 1 6 0 direct amp:3:120 quality:50 period:off"));
        TestCheck.That(stacked.GetProperty("quality").GetInt32() == 50 && stacked.GetProperty("amplifyValue").GetInt32() == 120 &&
            Load().Inventory.Items.Any(item => item.Core.ItemId == 18400 && item.Core.Value == 999_999_943 && item.Core.Attr != 0),
            "增幅、品级与期限可同时指定");
        TestCheck.That(Answer("item-clear Other 0").StartsWith("OK "), "品级用例后清理物品栏");

        var set = catalog.Items.Where(row => row.Equipment && row.SetIndex > 0 && (row.Jobs & 1) != 0).GroupBy(row => row.SetIndex)
            .FirstOrDefault(group => group.Select(row => resources.Items.Get(row.Id).EquipmentSlot).Distinct().Count() is >= 2 and <= 10);
        if (set is not null)
        {
            var result = Json(Answer("set-grant Other " + set.Key + " 3 0 direct"));
            var pieces = result.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToArray();
            var bag = Load().Inventory.Items;
            TestCheck.That(pieces.Length >= 2 && pieces.All(id => bag.Count(item => item.Core.ItemId == id) == 1) &&
                bag.Where(item => pieces.Contains(item.Core.ItemId) && item.Core.Kind == ItemKind.Equipment).All(item => (item.Core.Attr & 31) == 3), "整套直接入包每部位一件，强化只作用于普通装备");
            var setMail = Json(Answer("set-grant Other " + set.Key + " 0 0 mail amp:1:40"));
            TestCheck.That(setMail.GetProperty("mails").GetArrayLength() == (pieces.Length + 9) / 10 && !setMail.GetProperty("partial").GetBoolean(), "整套邮件发放（每封至多10件）");
        }
        else TestCheck.That(false, "资源中应存在可发放的套装");

        var mails = Json(Answer("mails Other")).GetProperty("mails").EnumerateArray().ToArray();
        TestCheck.That(mails.Length >= 1 && mails.All(mail => !mail.GetProperty("deletable").GetBoolean()), "邮箱只读查看，未领取附件不可删除");
        TestCheck.That(Answer("mail-delete Other all").StartsWith("ERR "), "没有可删除邮件时拒绝");
        uint mailId = mails[0].GetProperty("id").GetUInt32();
        TestCheck.That(Answer("mail-delete Other " + mailId).StartsWith("ERR "), "未领取资产的邮件拒绝删除");

        using var lease = f.Owners.Acquire("other", () => { });
        var session = new GameSession(f.Repository, towns, lease);
        session.Select(0, resources.GoldLimits);
        session.MarkReady();
        TestCheck.That(Answer("wallet Other").StartsWith("ERR "), "离线入口遇到在线owner拒绝接管");
        TestCheck.That(Answer("currency Other revive set 9", session).StartsWith("OK ") && session.Selected!.Inventory.ReviveCoins == 9, "在线货币经原owner提交并安装快照");
        TestCheck.That(Answer("grant Other 18400 1 0 0 direct", session).StartsWith("ERR ") && Answer("item-clear Other 0", session).StartsWith("ERR "),
            "游戏中的角色拒绝背包实例写入");
        TestCheck.That(session.ClaimMail(mailbox, [.. mails.Select(mail => mail.GetProperty("id").GetUInt32())]).Accepted, "领取GM邮件");
        var mailedMedal = session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 100380058 && item.Core.SortLockFlag == 0 && item.Core.Upgrade == 20);
        session.ReturnToRoster();
        session.Select(0, resources.GoldLimits);
        session.MarkReady();
        TestCheck.That(session.Selected!.Inventory.Find(mailedMedal.Position) == mailedMedal,
            "强化20勋章经真实邮件领取后重选角色，UID和完整核心保持");
        TestCheck.That(session.Selected!.Inventory.Items.Any(item => item.Core.ItemId == 18400 && item.Core.Value == 999_999_943),
            "邮件附件按指定品级种子到账");
        TestCheck.That(Answer("mail-delete Other all", session).StartsWith("ERR "), "游戏中的角色拒绝删除邮件");
        session.ReturnToRoster();
        var deleted = Json(Answer("mail-delete Other all", session));
        TestCheck.That(deleted.GetProperty("deleted").GetInt32() == mails.Length &&
            Json(Answer("mails Other", session)).GetProperty("mails").GetArrayLength() == 0, "回到选角后经原owner删除已领取邮件");
        // force：默认规则拒绝删带附件的邮件，加 force 才连附件一起删。
        TestCheck.That(Answer("grant Other 18400 1 0 0 mail quality:50", session).StartsWith("OK "), "再发一封带品级与附件的邮件");
        var forcedMail = Json(Answer("mails Other", session)).GetProperty("mails").EnumerateArray().Single();
        uint forcedId = forcedMail.GetProperty("id").GetUInt32();
        TestCheck.That(Answer("mail-delete Other " + forcedId, session).StartsWith("ERR "), "默认规则仍拒绝删除未领取附件");
        var forced = Json(Answer("mail-delete Other " + forcedId + " force", session));
        TestCheck.That(forced.GetProperty("deleted").GetInt32() == 1 && forced.GetProperty("force").GetBoolean() &&
            forced.GetProperty("forced").GetBoolean() &&
            Json(Answer("mails Other", session)).GetProperty("mails").GetArrayLength() == 0, "force 连附件一起删除");
        TestCheck.That(Answer("mail-delete Other " + forcedId + " hard", session).StartsWith("ERR "), "mail-delete 的可选参数只认 force");
        session.Select(0, resources.GoldLimits);
        session.MarkReady();
        TestCheck.That(Answer("grant Other 3175 2 0 0 mail", session).StartsWith("OK "), "游戏中的角色仍可邮件发放");
        session.ReturnToRoster();

        var search2 = Json(Answer("quest-search Other", session));
        TestCheck.That(search2.GetProperty("total").GetInt32() > 0, "主线任务按职业列出");
        var chain = quests.All.Where(quest => quest.Grade?.Trim('[', ']') == "epic" && quest.Prerequisites.Count > 0)
            .OrderBy(quest => quest.MinimumLevel).First();
        var chained = Json(Answer("quest-chain Other " + chain.Id, session));
        var done = session.QueryCharacters().Single().CompletedQuests;
        TestCheck.That(done.Contains(chain.Id) && chained.GetProperty("chain").GetInt32() >= 2 && done.Length == chained.GetProperty("completed").GetInt32(),
            "整链按职业前置原子完成");
        TestCheck.That(Answer("quest-batch Other 10100,10101", session).StartsWith("OK ") &&
            session.QueryCharacters().Single().CompletedQuests.Intersect([10100, 10101]).Count() == 2, "批量完成同事务写入");
        var dailySample = quests.All.First(quest => DevelopmentGm.QuestCategory(quest.Grade) == "daily");
        var repeatSample = quests.All.First(quest => DevelopmentGm.QuestCategory(quest.Grade) == "repeat");
        var dailySearch = Json(Answer("quest-search Other cat:daily size:5", session));
        TestCheck.That(dailySearch.GetProperty("ids").GetArrayLength() == 0 && dailySearch.GetProperty("total").GetInt32() > 0,
            "每日分类仍可检索，但不进入全部完成编号");
        var mixedBatch = Json(Answer($"quest-batch Other {dailySample.Id},{repeatSample.Id},10102", session));
        var completedAfter = session.QueryCharacters().Single().CompletedQuests;
        TestCheck.That(mixedBatch.GetProperty("skipped").GetInt32() == 2 && completedAfter.Contains(10102) &&
            !completedAfter.Contains(dailySample.Id) && !completedAfter.Contains(repeatSample.Id),
            "批量完成跳过每日和重复，只永久写入其它任务");
        TestCheck.That(Answer("quest-batch Other " + dailySample.Id, session).StartsWith("ERR "), "纯每日批量完成拒绝永久写入");
        TestCheck.That(Answer("quest-add Other " + dailySample.Id, session).StartsWith("OK "), "单独完成每日仍允许");
        var afterDailyAdd = session.QueryCharacters().Single();
        TestCheck.That(afterDailyAdd.CompletedQuests.Contains(dailySample.Id) && afterDailyAdd.QuestDailyClaims.ContainsKey(dailySample.Id),
            "单独 quest-add 每日写入当天周期而不是永久完成");
        var points = Json(Answer("points Other", session));
        if (points.GetProperty("bonusSp").ValueKind == JsonValueKind.Number && points.GetProperty("bonusTp").ValueKind == JsonValueKind.Number)
        {
            TestCheck.That(Answer("sp Other 7", session).StartsWith("OK ") && Answer("tp Other 9", session).StartsWith("OK "), "额外SP/TP写入");
            points = Json(Answer("points Other", session));
            TestCheck.That(points.GetProperty("bonusSp").GetInt32() == 7 && points.GetProperty("bonusTp").GetInt32() == 9, "points 读回额外SP/TP");
        }
        else TestCheck.That(Answer("sp Other 7", session).StartsWith("ERR "), "旧角色额外SP/TP来源未接管时写入被拒绝，不猜测补建");
        TestCheck.That(points.GetProperty("name").GetString() == "Other" && Answer("points Nobody", session).StartsWith("ERR ") && Answer("sp Other 50001", session).StartsWith("ERR "),
            "points 只读查询，非法参数拒绝");
        TestCheck.That(Answer("rename Other a_b", session).StartsWith("OK ") && Answer("rename Other a b", session).StartsWith("ERR ") && Answer("rename Other " + new string('x', 40), session).StartsWith("ERR "),
            "改名校验：合法名成功，含空白或超长拒绝");
        TestCheck.That(Json(Answer("points a_b", session)).GetProperty("name").GetString() == "a_b" && Answer("points Other", session).StartsWith("ERR ") &&
            Answer("rename a_b a_b", session).StartsWith("ERR ") && Answer("rename a_b Other", session).StartsWith("OK "), "改名后旧名失效、新名生效、可改回；同名拒绝");
        session.Select(0, resources.GoldLimits);
        session.MarkReady();
        TestCheck.That(Answer("rename Other Z_z", session).StartsWith("ERR ") && Answer("char-delete Other Other", session).StartsWith("ERR "), "游戏中的角色拒绝改名与删除");
        byte slotFlags = session.Selected!.EquipmentSlotFlags;
        TestCheck.That(Answer("equip-slots Other 1", session).StartsWith("ERR 角色正在游戏中", StringComparison.Ordinal) && Answer("equip-slots Other", session).StartsWith("OK ") &&
            session.Selected!.EquipmentSlotFlags == slotFlags, "游戏中的角色拒绝开扩展装备栏，只读查询仍可用");
        session.ReturnToRoster();
        TestCheck.That(Answer("equip-slots Other 16", session).StartsWith("OK ") && session.QueryCharacters().Single(row => row.Name == "Other").EquipmentSlotFlags == (slotFlags | 16),
            "回到选角后经原owner开启耳环栏");
        session.Select(0, resources.GoldLimits);
        session.MarkReady();
        session.ReturnToRoster();
        TestCheck.That(Answer("char-delete Other Wrong", session).StartsWith("ERR "), "删除须输入一致的角色名");
        f.Integrity();
        TestCheck.That(Answer("char-delete Other Other", session).StartsWith("OK ") && Answer("points Other", session).StartsWith("ERR "), "离线（选角）角色可永久删除，删除后查不到");
        {
            // 夹具：独立存档中已接管额外SP/TP来源（BonusSp/BonusTp=0）的1级角色，走真实离线写入路径（不扰动上面会话的名册世代）。
            using var g = new MailboxPersistenceTests.Fixture();
            g.OtherOwner.Dispose();
            string Skill(string line) => DevelopmentGm.Answer(line, "other", g.Repository, g.Owners, _ => null, mailbox, resources.Items, catalog: catalog);
            using (var skilledLease = g.Owners.Acquire("other", () => { }))
                g.Repository.Create(skilledLease, new("Skilled", new(38, 1, 450, 234, 0), null)
                {
                    Level = 1, Inventory = new([]),
                    InitialLearning = new(0, [new(0, 0, 0, []), new(1, 0, 0, [])]) { BonusSp = 0, BonusTp = 0 }
                });
            TestCheck.That(Skill("sp Skilled 7").StartsWith("OK ") && Skill("tp Skilled 9").StartsWith("OK "), "已接管来源的角色额外SP/TP写入: " + Skill("sp Skilled 7"));
            var skilled = Json(Skill("points Skilled"));
            TestCheck.That(skilled.GetProperty("bonusSp").GetInt32() == 7 && skilled.GetProperty("bonusTp").GetInt32() == 9, "points 读回额外SP/TP");
            TestCheck.That(Skill("sp Skilled 50001").StartsWith("ERR ") && Skill("sp Skilled 7").StartsWith("OK "), "额外SP越界拒绝、同值幂等");
            // 金币邮件上限（GM 网页发放路径）：超过收件人等级携带上限整笔拒绝，刚好等于上限可发。
            var goldMailbox = new MailboxService(g.Repository, resources.Items, resources.GoldLimits, resources.EpicPieces);
            var goldData = new DfoServer.Infrastructure.GmWebData(new ServerOptions(), DateTimeOffset.UtcNow, new OnlineSessionDirectory(),
                new DfoServer.Persistence.GmAccountDirectory(Path.Combine(Path.GetTempPath(), "gm-gold-" + Guid.NewGuid().ToString("N"), "game.db")), _ => "", goldMailbox,
                resources.GoldLimits, extras, new DfoServer.Network.Management.GmAudit(Path.Combine(Path.GetTempPath(), "gm-gold-" + Guid.NewGuid().ToString("N") + ".jsonl")));
            int recipientLevel = g.Repository.FindMailRecipient("Other")!.Level;
            long carry = resources.GoldLimits.CarryLimit(recipientLevel);
            TestCheck.That(goldData.DeliverGoldMail("other", "Other", (uint)(carry + 1), "上限").StartsWith("ERR 超过 Lv" + recipientLevel, StringComparison.Ordinal), "金币邮件超过携带上限被拒绝并说明等级与上限");
            TestCheck.That(goldData.DeliverGoldMail("someone-else", "Other", 1, "x").StartsWith("ERR ", StringComparison.Ordinal), "金币邮件账号与角色不符被拒绝");
            TestCheck.That(goldData.DeliverGoldMail("other", "Other", (uint)carry, "上限").StartsWith("OK ", StringComparison.Ordinal), "金币邮件刚好等于携带上限可发放");
            // 称号簿/成就：目录来自已加载PVF，写入走服务端同一规划与 CommitTitleAchievement。
            var list = Json(Skill("title-list Skilled"));
            TestCheck.That(list.GetProperty("total").GetInt32() > 0 && list.GetProperty("completed").GetInt32() == 0, "title-list 读取成就目录且新角色未完成");
            TestCheck.That(Skill("title-complete Skilled 999999999").StartsWith("ERR ") && Skill("title-complete Skilled book:nope").StartsWith("ERR "), "成就ID与分类严格校验");
            string titled = Skill("title-complete Skilled book");
            if (titled.StartsWith("OK ", StringComparison.Ordinal))
            {
                int completedNow = Json(titled).GetProperty("completed").GetInt32();
                TestCheck.That(completedNow > 0 && Json(Skill("title-list Skilled")).GetProperty("completed").GetInt32() == completedNow, "一键称号簿提交后成就读回一致");
                TestCheck.That(Skill("title-complete Skilled book").StartsWith("ERR "), "已完成的成就不重复发放");
            }
            else TestCheck.That(titled.Contains("成就", StringComparison.Ordinal), "称号簿无法发放时整批拒绝并给出原因: " + titled);
            var stats = Json(Skill("stats Skilled"));
            TestCheck.That(stats.GetProperty("scope").GetString() == "base" && stats.GetProperty("stats").GetProperty("maxHp").GetDouble() > 0 &&
                Skill("stats Nobody").StartsWith("ERR "), "stats 只读返回服务端基础属性，未知角色拒绝");
        }
        Console.WriteLine($"PASS {TestCheck.Count - before} GM扩展命令：搜索、货币、发放、删除、邮箱、任务链与在线路由断言");
    }

    // item-search 扩展选项（tags/kind/sub/real/sort）与关键字 ID 匹配修正；不带选项时结果与旧版一致。
    static void RunSearchOptions(Func<string, Game.Session.GameSession?, string> answer, Func<string, JsonElement> json, DevelopmentCatalog catalog,
        DfoServer.Network.Management.GmItemExtras extras, JsonElement categories)
    {
        string Answer(string line) => answer(line, null);
        int Total(string line) => json(Answer(line)).GetProperty("total").GetInt32();
        JsonElement[] All(string line)
        {
            var rows = new List<JsonElement>();
            for (int offset = 0; ; offset += 200)
            {
                var page = json(Answer(line.Replace("{o}", offset + "/200", StringComparison.Ordinal)));
                rows.AddRange(page.GetProperty("items").EnumerateArray().Select(row => row.Clone()));
                if (offset + 200 >= page.GetProperty("total").GetInt32()) return [.. rows];
            }
        }
        int Category(string tag, bool? equipment = null, string field = "count") => categories.GetProperty("categories").EnumerateArray()
            .Where(row => row.GetProperty("tag").GetString() == tag && (equipment is null || row.GetProperty("equipment").GetBoolean() == equipment)).Sum(row => row.GetProperty(field).GetInt32());

        // 关键字：不是正整数时不按 ID 匹配（以前 TryParse 失败的 0 会命中 ID 0 金币）。
        TestCheck.That(Total("item-search 0 0 0 * * 不存在的物品名zz") == 0, "非数字关键字不再按 ID 0 命中金币");
        TestCheck.That(json(Answer("item-search 0 0 0 * * 0")).GetProperty("items").EnumerateArray().All(row => row.GetProperty("name").GetString()!.Contains('0')),
            "关键字 0 只按名称匹配");
        // 旧参数不变：单分类总数等于分类表数量，回显 options 为空选项。
        var old = json(Answer("item-search 0 0 0 * coat"));
        TestCheck.That(old.GetProperty("total").GetInt32() == Category("coat") && old.GetProperty("options").GetProperty("tags").ValueKind == JsonValueKind.Null &&
            !old.GetProperty("options").GetProperty("real").GetBoolean(), "不带扩展选项时单分类结果与旧版一致");
        TestCheck.That(Total("item-search 0 0 0 * * tags:coat,pants") == Category("coat") + Category("pants"), "tags 多分类取并集");
        TestCheck.That(Total("item-search 0 0 0 * * tags:coat_avatar,hat_avatar") == Category("coat avatar") + Category("hat avatar"), "tags 中空格写作 _");
        var creatures = All("item-search {o} 0 0 * * tags:creature@e");
        TestCheck.That(creatures.Length == Category("creature", true) && creatures.All(row => row.GetProperty("equipment").GetBoolean()), "tags 的 @e 只要装备");
        TestCheck.That(Total("item-search 0 0 0 * * tags:creature@s") == Category("creature", false), "tags 的 @s 只要道具");
        TestCheck.That(Total("item-search 0 0 0 * * tags:g:peteq") == Category("artifact red") + Category("artifact blue") + Category("artifact green"), "tags 可写分组");
        TestCheck.That(json(Answer("item-search 0/200 0 0 * * kind:s 药")).GetProperty("items").EnumerateArray().All(row => !row.GetProperty("equipment").GetBoolean()), "kind:s 只要道具");
        int weapons = Category("weapon"), realWeapons = Category("weapon", null, "real");
        TestCheck.That(Total("item-search 0 0 0 * weapon real:1") == realWeapons && realWeapons <= weapons, "real:1 与分类表 real 数一致");
        TestCheck.That(All("item-search {o} 0 0 * * tags:title_name real:1").All(row => !DevelopmentCatalog.IsPlaceholderName(row.GetProperty("name").GetString())), "real:1 不含占位名");
        var subs = categories.GetProperty("weaponSubs").EnumerateArray().ToArray();
        TestCheck.That(subs.Length > 3 && subs.Sum(row => row.GetProperty("count").GetInt32()) <= weapons, "武器子类来自 PVF 路径");
        var sub = subs.OrderByDescending(row => row.GetProperty("count").GetInt32()).First();
        string subKey = sub.GetProperty("sub").GetString()!;
        var subRows = All("item-search {o} 0 0 * * sub:" + subKey.Replace(' ', '_'));
        TestCheck.That(subRows.Length == sub.GetProperty("count").GetInt32() && subRows.All(row => row.GetProperty("sub").GetString() == subKey && row.GetProperty("tag").GetString() == "weapon"),
            "sub 只返回该武器子类: " + subKey);
        var sorted = json(Answer("item-search 0/200 0 0 * * tags:coat,pants sort:id")).GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToArray();
        TestCheck.That(sorted.Length > 1 && sorted.SequenceEqual(sorted.Order()), "sort:id 按 ID 升序");
        var levels = json(Answer("item-search 0/200 0 0 * * tags:coat sort:-level")).GetProperty("items").EnumerateArray().Select(row => row.GetProperty("minLevel").GetInt32()).ToArray();
        TestCheck.That(levels.Length > 1 && levels.SequenceEqual(levels.OrderDescending()), "sort:-level 按等级降序");
        TestCheck.That(Total("item-filter 0 0 0 * * * * * tags:coat,pants") == Category("coat") + Category("pants"), "item-filter 同样支持 tags");
        foreach (string bad in new[] { "item-search 0 0 0 * coat tags:pants", "item-search 0 0 0 * * sort:price", "item-search 0 0 0 * * kind:x",
            "item-search 0 0 0 * * tags:g:nope", "item-search 0 0 0 * * real:2", "item-search 0 0 0 * * tags:coat tags:pants", "item-search 0 0 0 * sets tags:coat" })
            TestCheck.That(Answer(bad).StartsWith("ERR "), "扩展选项严格拒绝: " + bad);
        // 网页参数转命令选项。
        TestCheck.That(DfoServer.Network.Management.GmWebServer.SearchOptionTokens("coat avatar,creature@e", "e", "katana", "1", "id", out string extra) is null &&
            extra == " tags:coat_avatar,creature@e kind:e sub:katana real:1 sort:id", "网页扩展参数转换: " + extra);
        TestCheck.That(DfoServer.Network.Management.GmWebServer.SearchOptionTokens(null, null, null, null, null, out string none) is null && none.Length == 0, "网页不带扩展参数时不追加选项");
        TestCheck.That(DfoServer.Network.Management.GmWebServer.SearchOptionTokens(null, null, null, null, "price", out _) is not null &&
            DfoServer.Network.Management.GmWebServer.SearchOptionTokens(null, null, "Katana!", null, null, out _) is not null &&
            DfoServer.Network.Management.GmWebServer.SearchOptionTokens(null, "x", null, null, null, out _) is not null, "网页扩展参数严格校验");
        // 预览补充：交易限制、价格、武器子类、礼包内容。
        JsonElement Preview(int id) => JsonSerializer.SerializeToElement(extras.Preview(id));
        var weapon = Preview(subRows[0].GetProperty("id").GetInt32());
        TestCheck.That(weapon.GetProperty("weaponSub").GetString() == subKey && weapon.TryGetProperty("price", out _) && weapon.TryGetProperty("trade", out _), "武器预览带子类、价格与交易限制");
        var traded = catalog.Items.Where(row => row.Equipment && !row.Placeholder).Take(3000).Select(row => Preview(row.Id)).FirstOrDefault(value => value.GetProperty("attachType").GetString()!.Length > 0);
        TestCheck.That(traded.ValueKind == JsonValueKind.Object && traded.GetProperty("trade").GetString()!.Length > 0, "交易类型有中文说明");
        var box = catalog.Items.Where(row => !row.Equipment && row.Tag == "booster").Take(500).Select(row => Preview(row.Id))
            .FirstOrDefault(value => value.GetProperty("booster").ValueKind == JsonValueKind.Object && value.GetProperty("booster").GetProperty("items").GetArrayLength() > 0);
        TestCheck.That(box.ValueKind == JsonValueKind.Object && box.GetProperty("booster").GetProperty("items").EnumerateArray().All(row => row.GetProperty("min").GetInt32() > 0),
            "礼包预览列出内容物品与数量");
        TestCheck.That(DfoServer.Network.Management.GmItemExtras.TradeLabel("sealing") == "封装（解封后不可交易）" && DfoServer.Network.Management.GmItemExtras.TradeLabel("free") == "可交易" &&
            DfoServer.Network.Management.GmItemExtras.TradeLabel("account") == "账号绑定（不可交易）", "交易类型中文说明");
    }
}
