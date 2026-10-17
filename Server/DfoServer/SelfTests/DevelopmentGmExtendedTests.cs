using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
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
        using var f = new MailboxPersistenceTests.Fixture();
        f.OtherOwner.Dispose();
        var mailbox = new MailboxService(f.Repository, resources.Items, resources.GoldLimits, resources.EpicPieces);
        var catalog = new DevelopmentCatalog(pvf, resources, quests) { Stats = new DfoServer.Game.Characters.CharacterStatComputer(characters) };
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
        var dated = catalog.Items.Where(row => !row.Equipment).Take(200).Select(row => Preview(row.Id)).FirstOrDefault(value => value.GetProperty("expiration").GetProperty("absoluteExpireDate").GetString()!.Length > 0);
        TestCheck.That(dated.ValueKind == JsonValueKind.Object && DateTime.TryParse(dated.GetProperty("expiration").GetProperty("absoluteExpireDate").GetString(), out _), "绝对到期日按文本日期读取");
        var categories = Json(Answer("item-categories"));
        TestCheck.That(categories.GetProperty("categories").GetArrayLength() > 1 && categories.GetProperty("maxRefinement").GetInt32() > 0, "分类与锻造上限来自资源");

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
        TestCheck.That(Json(Answer("item-clear Other 0")).GetProperty("deleted").GetInt32() > 0 &&
            Load().Inventory.Items.All(item => item.Position.List != InventoryListType.Main), "分类清理整栏原子删除");

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
        TestCheck.That(Answer("mail-delete Other all", session).StartsWith("ERR "), "游戏中的角色拒绝删除邮件");
        session.ReturnToRoster();
        var deleted = Json(Answer("mail-delete Other all", session));
        TestCheck.That(deleted.GetProperty("deleted").GetInt32() == mails.Length &&
            Json(Answer("mails Other", session)).GetProperty("mails").GetArrayLength() == 0, "回到选角后经原owner删除已领取邮件");
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
}
