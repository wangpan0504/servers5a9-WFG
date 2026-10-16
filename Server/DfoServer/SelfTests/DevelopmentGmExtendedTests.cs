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
    public static void RunResources(TownCatalog towns, ItemResources resources, PvfArchive pvf, QuestCatalog quests)
    {
        int before = TestCheck.Count;
        using var f = new MailboxPersistenceTests.Fixture();
        f.OtherOwner.Dispose();
        var mailbox = new MailboxService(f.Repository, resources.Items, resources.GoldLimits, resources.EpicPieces);
        var catalog = new DevelopmentCatalog(pvf, resources, quests);
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

        var template = resources.Items.Get(18400);
        TestCheck.That(Answer("grant Other 18400 2 5 0 direct").StartsWith("OK "), "装备直接入包");
        var granted = Load().Inventory.Items.Where(item => item.Core.ItemId == 18400).ToArray();
        TestCheck.That(granted.Length == 2 && granted.All(item => (item.Core.Attr & 31) == 5 && item.Uid > 0), "直接入包保留强化和独立UID");
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
        f.Integrity();
        Console.WriteLine($"PASS {TestCheck.Count - before} GM扩展命令：搜索、货币、发放、删除、邮箱、任务链与在线路由断言");
    }
}
