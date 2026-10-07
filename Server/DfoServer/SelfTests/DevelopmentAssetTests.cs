using DfoServer.Development;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;

namespace DfoServer;

internal static class DevelopmentAssetTests
{
    public static void RunCore()
    {
        int before = TestCheck.Count;
        using var f = new MailboxPersistenceTests.Fixture();
        f.SenderOwner.Dispose();
        string Answer(string line, Func<string, GameSession?>? find = null) => DevelopmentGm.Answer(line, "sender", f.Repository, f.Owners, find ?? (_ => null));
        foreach (string invalid in new[] { "gold Sender -1", "gold Sender 2147483648", "gold Sender 1 extra", "item Sender 0 1", "item Sender 18400 0", "item Sender 18400 -1", "item Sender 18400 1 extra" })
            TestCheck.That(Answer(invalid).StartsWith("ERR "), "GM资产参数严格拒绝: " + invalid);
        string snapshot = f.Snapshot();
        TestCheck.That(Answer("gold Receiver 9").StartsWith("ERR ") && f.Snapshot() == snapshot, "金币按账号和角色归属，不跨账号修改");
        TestCheck.That(Answer("gold Sender 2147483647").StartsWith("OK "), "开发金币设置接受存档上限");
        snapshot = f.Snapshot();
        TestCheck.That(Answer("gold Sender 2147483647").StartsWith("OK ") && f.Snapshot() == snapshot, "同额设置不递增revision或重复审计");
        using var owner = f.Owners.Acquire("sender", () => { });
        var current = f.Repository.LoadCharacter(owner, 0);
        TestCheck.That(current.Inventory.Gold == int.MaxValue && current.Inventory.Items.Length == 4 &&
            current.Inventory.Items.Select(item => item.Uid).Order().SequenceEqual(f.Sender.Inventory.Items.Select(item => item.Uid).Order()),
            "设置金币保留完整实例和UID");
        f.Repository.Create(owner, new("Sibling", current.Position, current.Return));
        var point = current.Position;
        var map = new TownMap("synthetic-gm.map", [new(new(0, 0, 1000, 1000), [], [])], [], false, []);
        var area = new TownArea(point.Town, point.Area, 1, [], [], [], point, map, [], []);
        var towns = new TownCatalog(new() { [(point.Town, point.Area)] = area }, point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
        var session = new GameSession(f.Repository, towns, owner);
        session.Select(0);
        TestCheck.That(Answer("gold Sender 1", _ => session).StartsWith("OK ") && session.Selected!.Inventory.Gold == 1,
            "金币与原GM进度编辑一致，当前角色提交后更新在线快照");
        session.MarkReady();
        TestCheck.That(Answer("gold Sender 77", _ => session).StartsWith("OK ") && session.Selected!.Inventory.Gold == 77,
            "在线金币写入使用原owner和实际提交结果");
        var selected = session.Selected!;
        TestCheck.That(Answer("gold Sibling 55", _ => session).StartsWith("OK ") && ReferenceEquals(session.Selected, selected) &&
            f.Repository.LoadCharacter(owner, 1).Inventory.Gold == 55, "同账号其他角色的金币不覆盖Selected");
        TestCheck.That(Answer("gold Sender 88", _ => session).StartsWith("OK ") && session.Selected!.Inventory.Gold == 88,
            "在线快照在提交后安装实际金币及revision");
        session.ReturnToRoster();
        session.Select(0);
        TestCheck.That(session.Selected!.Inventory.Gold == 88, "重新选择角色后恢复已写入的金币");
        using var replacement = f.Owners.Acquire("sender", () => { });
        snapshot = f.Snapshot();
        TestCheck.That(Answer("gold Sender 99", _ => session).StartsWith("ERR ") && f.Snapshot() == snapshot && replacement.IsCurrent,
            "旧目录引用不能设置金币或释放新owner");
        f.Integrity();
        Console.WriteLine($"PASS {TestCheck.Count - before} GM金币解析、角色隔离、原子提交与重新选角断言");
    }

    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        int before = TestCheck.Count;
        using var f = new MailboxPersistenceTests.Fixture();
        var service = new MailboxService(f.Repository, resources.Items, resources.GoldLimits, resources.EpicPieces);
        string Send(string line, string account = "receiver") => DevelopmentGm.Answer(line, account, f.Repository, f.Owners, _ => null,
            service, resources.Items);
        TestCheck.That(Send("item Receiver 18400 3").StartsWith("OK "), "真实装备按角色发放系统邮件");
        var mail = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now).Entries.Single();
        TestCheck.That(mail.Attachments.Length == 3 && mail.Attachments.Select(item => item.Item.Uid).Distinct().Count() == 3 &&
            mail.Attachments.All(item => item.Item.Core.Value == 999999998 && item.Item.Core.Count == 1),
            "装备数量是件数，每件有独立UID及正常品级，不写数量为装备value");
        TestCheck.That(Send("item Receiver 3175 7").StartsWith("OK "), "系统邮件允许真实绑定材料");
        var stack = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now).Entries.Single(row => row.Id != mail.Id);
        TestCheck.That(stack.Attachments.Length == 1 && stack.Attachments[0].Item.Core.Value == 7 && stack.Attachments[0].Item.Core.IsStackable,
            "材料数量是一个堆叠的数量");
        var session = new GameSession(f.Repository, towns, f.ReceiverOwner);
        session.Select(0, resources.GoldLimits); session.MarkReady();
        TestCheck.That(session.ClaimMail(service, [mail.Id, stack.Id]).Accepted && session.Selected!.Inventory.Items.Count(item => item.Core.ItemId == 18400) == 3 &&
            session.Selected.Inventory.Items.Single(item => item.Core.ItemId == 3175).Core.Value == 7, "GM邮件领取沿用统一grant，装备与材料正确入包");
        string snapshot = f.Snapshot();
        foreach (var (command, account) in new[] { ("item Receiver 18400 11", "receiver"), ("item Receiver 18400 1", "sender"),
            ("item Receiver 2147483647 1", "receiver"), ("item Receiver 3037 1", "receiver"),
            ("item Receiver 3175 " + ((long)resources.Items.Get(3175).StackLimit + 1), "receiver") })
            TestCheck.That(Send(command, account).StartsWith("ERR ") && f.Snapshot() == snapshot, "无效GM发放不新增邮箱或UID: " + command);
        f.Execute("CREATE TRIGGER fail_gm_mail BEFORE INSERT ON mailbox_attachments WHEN NEW.ordinal=1 BEGIN SELECT RAISE(ABORT,'gm-mail-failure'); END;");
        TestCheck.That(Send("item Receiver 18400 3").StartsWith("ERR ") && f.Snapshot() == snapshot,
            "多装备邮件中途SQL失败回滚整封、全部UID及审计，不发成功提示");
        f.Integrity();
        Console.WriteLine($"PASS {TestCheck.Count - before} GM道具邮件真实资源、数量、领取与回滚断言");
    }
}
