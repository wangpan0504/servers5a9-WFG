using DfoServer.Development;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Currency;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using System.Text.Json;
using System.Buffers.Binary;
using DfoServer.Network.Builders.Inventory;

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
        foreach (string invalid in new[] { "items", "items Sender extra", "item-info 0", "item-info Sender", "item-info 1 2",
            "item Sender 18400 1 32", "item Sender 18400 1 -1", "item Sender 18400 1 1 extra" })
            TestCheck.That(Answer(invalid).StartsWith("ERR "), "GM查看与强化参数严格拒绝: " + invalid);
        TestCheck.That(Answer("items Sender").StartsWith("ERR "), "离线背包查看遇到在线owner时拒绝接管");
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
        snapshot = f.Snapshot();
        string view = Answer("items Sender", _ => session);
        TestCheck.That(view.StartsWith("OK ") && view.Contains("\"gold\":88", StringComparison.Ordinal) &&
            view.Split("\"uid\":").Length - 1 == session.Selected!.Inventory.Items.Length && f.Snapshot() == snapshot,
            "在线背包查看经原owner读取已提交快照，完整列出实例且不写库");
        TestCheck.That(Answer("items Sibling", _ => session).Contains("\"gold\":55", StringComparison.Ordinal) && session.Selected!.Inventory.Gold == 88,
            "同账号其他角色查看读取持久状态");
        TestCheck.That(Answer("items Receiver", _ => session).StartsWith("ERR ") && f.Snapshot() == snapshot, "背包查看不跨账号");
        session.ReturnToRoster();
        session.Select(0);
        TestCheck.That(session.Selected!.Inventory.Gold == 88, "重新选择角色后恢复已写入的金币");
        foreach (string invalid in new[] { "recharge", "recharge cera 0", "recharge cera 101", "recharge cera -100",
            "recharge cera 2147483648", "recharge gold 100", "recharge black-diamond 0", "recharge black-diamond 3651", "recharge-status extra" })
            TestCheck.That(Answer(invalid, _ => session).StartsWith("ERR "), "模拟充值参数严格拒绝: " + invalid);
        snapshot = f.Snapshot();
        TestCheck.That(Answer("account missing recharge-status").StartsWith("ERR ") &&
            Answer("account missing recharge cera 100").StartsWith("ERR ") && f.Snapshot() == snapshot,
            "未建角色账号不伪报成功、不生成钱包或虚构充值目标");
        JsonElement Recharge(string line) => JsonSerializer.Deserialize<JsonElement>(Answer(line, _ => session)[3..]);
        TestCheck.That(Recharge("recharge-status").GetProperty("cera").GetInt32() == 0 && f.Snapshot() == snapshot,
            "充值状态不写库、不额外发放会员或疲劳");
        TestCheck.That(Recharge("recharge cera 1000").GetProperty("cera").GetInt32() == 1000 &&
            Recharge("recharge cera 500").GetProperty("cera").GetInt32() == 1500 &&
            session.Selected.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 1500 && f.ReadReceiver().Inventory.AccountWallet.Get(CurrencyKind.Cera) == 0,
            "点券按原账号owner累加并同步Selected，不影响另一账号");
        snapshot = f.Snapshot();
        f.Execute("CREATE TRIGGER fail_recharge BEFORE INSERT ON currency_audit_log WHEN NEW.action_name='gm_recharge' BEGIN SELECT RAISE(ABORT,'recharge failure'); END");
        TestCheck.That(Answer("recharge cera 100", _ => session).StartsWith("ERR ") && f.Snapshot() == snapshot &&
            session.Selected.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 1500, "晚期SQL失败回滚充值、审计与版本，不安装成功快照");
        f.Execute("DROP TRIGGER fail_recharge");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long expires = Recharge("recharge black-diamond 30").GetProperty("blackDiamondExpiresAt").GetInt64();
        TestCheck.That(expires >= now + 30L * 86400 && expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30L * 86400 &&
            Recharge("recharge black-diamond 90").GetProperty("blackDiamondExpiresAt").GetInt64() == expires + 90L * 86400 &&
            f.Repository.LoadCharacter(owner, 1).BlackDiamondExpiresAt == expires + 90L * 86400,
            "黑钻使用正式账号契约56，有效期后累计续期，同账号其他角色共享");
        var credited = session.Selected;
        TestCheck.That(JsonSerializer.Serialize(credited.Inventory with { AccountWallet = current.Inventory.AccountWallet, Gold = current.Inventory.Gold }) ==
            JsonSerializer.Serialize(current.Inventory), "充值不重建UID、物品、角色金币或其他资产");
        session.ReturnToRoster();
        session.Select(1);
        TestCheck.That(Recharge("recharge cera 100").GetProperty("cera").GetInt32() == 1600 &&
            session.Selected!.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 1600, "自动选择存储角色不覆盖当前其他角色，账号钱包仍同步");
        TestCheck.That(session.IssueRechargeTicket(DateTimeOffset.UtcNow) is null, "选角尚未Ready时不能签发充值票据");
        session.MarkReady();
        var ticketNow = DateTimeOffset.UtcNow;
        snapshot = f.Snapshot();
        var ticket = session.IssueRechargeTicket(ticketNow)!;
        byte[] ticketPacket = CurrencyPackets.CeraTicket(ticket);
        uint ticketLength = BinaryPrimitives.ReadUInt32LittleEndian(ticketPacket.AsSpan(1));
        TestCheck.That(ticket.Token.StartsWith("sender.") && ticketLength == ticket.Token.Length && ticketLength is > 0 and < 64 &&
            ticketPacket.Length == 9 + ticketLength && ticketPacket[0] == 1 &&
            System.Text.Encoding.ASCII.GetString(ticketPacket, 5, (int)ticketLength) == ticket.Token &&
            BinaryPrimitives.ReadUInt32LittleEndian(ticketPacket.AsSpan(5 + (int)ticketLength)) == ticket.Key &&
            ticket.Key is > 0 and <= 65535 && ticket.ExpiresAt == ticketNow.AddMinutes(10) && f.Snapshot() == snapshot,
            "ACK65完整长度前缀ASCII串和4字节key，签发不写资产或建立平行在线状态");
        string ticketStatus = $"recharge-ticket-status {ticket.Token} {ticket.Key}";
        TestCheck.That(Recharge(ticketStatus).GetProperty("account").GetString() == "sender" && f.Snapshot() == snapshot &&
            Answer(ticketStatus).StartsWith("ERR "), "票据读取当前登录账号，不默认10000，不允许离线借用票据");
        foreach (string invalid in new[] { $"recharge-ticket-status {ticket.Token} {ticket.Key % 65535 + 1}",
            $"account sender {ticketStatus}", $"recharge-ticket-status receiver.{ticket.Token.Split('.')[1]} {ticket.Key}",
            "recharge-ticket-status invalid 1", $"recharge-ticket {ticket.Token} {ticket.Key} cera 1" })
            TestCheck.That(Answer(invalid, _ => session).StartsWith("ERR ") && f.Snapshot() == snapshot,
                "错误key、篡改账号、旧式前缀及非法金额不形成充值: " + invalid.Split(' ')[0]);
        TestCheck.Reject<InvalidDataException>(() => session.WithRechargeTicket(ticket.Token, ticket.Key, ticketNow.AddMinutes(10), () => true),
            "票据到期端点拒绝");
        TestCheck.That(Recharge($"recharge-ticket {ticket.Token} {ticket.Key} cera 100").GetProperty("cera").GetInt32() == 1700 &&
            session.Selected.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 1700,
            "验票和加值在同一owner内，使用本次实际提交的钱包");
        var nextTicket = session.IssueRechargeTicket(ticketNow)!;
        TestCheck.That(!session.CanPublishRechargeTicket(ticket) && session.CanPublishRechargeTicket(nextTicket) &&
            Answer(ticketStatus, _ => session).StartsWith("ERR "), "重新打开只保留一张票据，旧回执不能继续发布或充值");
        session.ReturnToRoster();
        session.Select(1);
        session.MarkReady();
        TestCheck.That(Answer($"recharge-ticket-status {nextTicket.Token} {nextTicket.Key}", _ => session).StartsWith("ERR "),
            "返回选角及重选使原窗口票据失效");
        var finalTicket = session.IssueRechargeTicket(ticketNow)!;
        session.CommitDevelopmentInventory("Sibling", false, role => new(true, 0,
            new(role.Inventory, CurrencyWallet.Set(role.Inventory, CurrencyKind.Cera, 2147483600), "test_limit")), "test");
        snapshot = f.Snapshot();
        TestCheck.That(Answer("recharge cera 100", _ => session).StartsWith("ERR ") && f.Snapshot() == snapshot,
            "余额上限整笔拒绝，不饱和截断充值数量");
        using var replacement = f.Owners.Acquire("sender", () => { });
        snapshot = f.Snapshot();
        TestCheck.That(Answer("gold Sender 99", _ => session).StartsWith("ERR ") && f.Snapshot() == snapshot && replacement.IsCurrent,
            "旧目录引用不能设置金币或释放新owner");
        TestCheck.That(Answer("recharge cera 100", _ => session).StartsWith("ERR ") && f.Snapshot() == snapshot && replacement.IsCurrent,
            "旧generation不能充值或顶掉新owner");
        TestCheck.That(Answer($"recharge-ticket {finalTicket.Token} {finalTicket.Key} cera 100", _ => session).StartsWith("ERR ") &&
            f.Snapshot() == snapshot && replacement.IsCurrent, "旧generation票据不能触碰新owner资产");
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
        TestCheck.That(Send("item Receiver 3037 1").StartsWith("OK "), "钱包材料可由原GM单物品命令投递邮件");
        var crystal = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now).Entries.Single(row =>
            row.Attachments.Any(attachment => attachment.Item.Core.ItemId == 3037));
        TestCheck.That(session.ClaimMail(service, [crystal.Id]).Accepted &&
            session.Selected!.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 1 &&
            session.Selected.Inventory.Items.All(item => item.Core.ItemId != 3037), "GM晶块领取进入真实账号钱包，不进入普通背包格");
        string inventoryBeforeTokens = System.Text.Json.JsonSerializer.Serialize(session.Selected!.Inventory.Items);
        uint goldBeforeTokens = session.Selected.Inventory.Gold;
        TestCheck.That(Send("item Receiver 2681762 7").StartsWith("OK ") && Send("item Receiver 2681917 11").StartsWith("OK "),
            "两种代币券可由原GM单物品命令投递邮件");
        var tokens = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now).Entries.Where(row =>
            row.Attachments.Any(attachment => attachment.Item.Core.ItemId is 2681762 or 2681917)).ToArray();
        TestCheck.That(tokens.Length == 2 && tokens.All(row => row.Attachments.Length == 1 &&
            row.Attachments[0].Item.Core.Value == (row.Attachments[0].Item.Core.ItemId == 2681762 ? 7 : 11)) &&
            tokens.Select(row => row.Attachments[0].Item.Uid).Distinct().Count() == 2 &&
            f.ReadReceiver().Inventory.AccountWallet.Get(CurrencyKind.TokenCera) == 0 &&
            f.ReadReceiver().Inventory.AccountWallet.Get(CurrencyKind.HappyTokenCera) == 0,
            "GM代币邮件独立托管真实面额及UID，未领取不增加钱包余额");
        TestCheck.That(session.ClaimMail(service, [tokens[0].Id, tokens[1].Id]).Accepted &&
            session.Selected!.Inventory.AccountWallet.Get(CurrencyKind.TokenCera) == 7 &&
            session.Selected.Inventory.AccountWallet.Get(CurrencyKind.HappyTokenCera) == 11 &&
            session.Selected.Inventory.Gold == goldBeforeTokens &&
            System.Text.Json.JsonSerializer.Serialize(session.Selected.Inventory.Items) == inventoryBeforeTokens,
            "GM双代币领取进入各自账号钱包，不增加角色金币或普通背包实例");
        var persistedTokens = f.ReadReceiver().Inventory.AccountWallet;
        TestCheck.That(persistedTokens.Get(CurrencyKind.TokenCera) == 7 && persistedTokens.Get(CurrencyKind.HappyTokenCera) == 11,
            "GM双代币领取余额已原子提交到持久账号钱包");
        string snapshot = f.Snapshot();
        TestCheck.That(!session.ClaimMail(service, [tokens[0].Id, tokens[1].Id]).Accepted && f.Snapshot() == snapshot,
            "已领取GM代币邮件重放不重复入账，不改邮件、UID或审计");
        foreach (var (command, account) in new[] { ("item Receiver 18400 11", "receiver"), ("item Receiver 18400 1", "sender"),
            ("item Receiver 2147483647 1", "receiver"),
            ("item Receiver 3175 " + ((long)resources.Items.Get(3175).StackLimit + 1), "receiver") })
            TestCheck.That(Send(command, account).StartsWith("ERR ") && f.Snapshot() == snapshot, "无效GM发放不新增邮箱或UID: " + command);
        f.Execute("CREATE TRIGGER fail_gm_mail BEFORE INSERT ON mailbox_attachments WHEN NEW.ordinal=1 BEGIN SELECT RAISE(ABORT,'gm-mail-failure'); END;");
        TestCheck.That(Send("item Receiver 18400 3").StartsWith("ERR ") && f.Snapshot() == snapshot,
            "多装备邮件中途SQL失败回滚整封、全部UID及审计，不发成功提示");
        f.Execute("DROP TRIGGER fail_gm_mail;");
        string info = Send("item-info 18400");
        TestCheck.That(info.StartsWith("OK ") && info.Contains("\"id\":18400", StringComparison.Ordinal) &&
            info.Contains("\"upgradable\":true", StringComparison.Ordinal) && f.Snapshot() == snapshot, "道具信息只读返回真实模板");
        TestCheck.That(Send("item-info 2147483647").StartsWith("ERR "), "未知道具信息明确拒绝");
        TestCheck.That(Send("item Receiver 3175 1 1").StartsWith("ERR ") && f.Snapshot() == snapshot, "非装备拒绝强化参数且不写邮箱");
        TestCheck.That(Send("item Receiver 18400 1 7").StartsWith("OK "), "装备可按指定强化发放");
        var upgraded = f.Repository.LoadMailbox(f.ReceiverOwner, f.Receiver.Id, f.Now).Entries.MaxBy(row => row.Id)!;
        TestCheck.That(upgraded.Attachments.Length == 1 && upgraded.Attachments[0].Item.Core.ItemId == 18400 &&
            upgraded.Attachments[0].Item.Core.Attr == 7 && upgraded.Attachments[0].Item.Core.Value == 999999998,
            "强化写入统一创建策略的Attr低五位，保留正常品级");
        string bag = DevelopmentGm.Answer("items Receiver", "receiver", f.Repository, f.Owners, _ => session, service, resources.Items);
        TestCheck.That(bag.StartsWith("OK ") && bag.Contains(System.Text.Json.JsonSerializer.Serialize(resources.Items.Get(18400).Name), StringComparison.Ordinal),
            "在线背包查看带出真实模板名称");
        f.Integrity();
        Console.WriteLine($"PASS {TestCheck.Count - before} GM道具邮件真实资源、数量、领取与回滚断言");
    }
}
