using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using PvfLib;

namespace DfoServer;

internal static class MailboxTests
{
    public static async Task RunAsync(TownCatalog towns, CharacterCatalog characters, ItemResources resources, PvfArchive pvf)
    {
        int before = TestCheck.Count;
        var material = TradableMaterial(pvf, resources);
        RunEpicExchangeOriginPropagation(towns, characters, resources, material);
        using var f = new Fixture(towns, characters, resources, material);
        var original = f.Sender.Selected!;
        var source = original.Inventory.Find(new(InventoryListType.Main, 9))!;
        var stack = original.Inventory.Find(new(InventoryListType.Main, 121))!;
        var request = new MailboxSendRequest("MailRecv", 1000, [new(0, 9, source.Core.ItemId, 1), new(0, 121, stack.Core.ItemId, 4)], "正文");
        var equipmentTemplate = resources.Items.Get(source.Core.ItemId);
        int equipmentValue = equipmentTemplate.ShopPrice.GetSellGold(source.Core.Durability, equipmentTemplate.Durability);
        int materialValue = resources.Items.Get(stack.Core.ItemId).ShopPrice.SellGold;
        long attachmentValue = equipmentValue + materialValue * 4L;
        uint expectedFee = checked((uint)(2150 + attachmentValue / 20));
        var nowForPlan = DateTimeOffset.UtcNow;
        TestCheck.That(f.Service.PlanSend(original, f.SenderOwner.Account, request, nowForPlan, out var planned) == MailboxError.None &&
            planned!.Fee == expectedFee, "实际资源寄送费用包含固定费、金币及附件当前出售价值");
        var poor = original with { Inventory = original.Inventory with { Gold = 1000 + expectedFee - 1 } };
        TestCheck.That(f.Service.PlanSend(poor, f.SenderOwner.Account, request, nowForPlan, out var rejected) == MailboxError.InsufficientGold && rejected is null &&
            f.Sender.Selected == original, "少一金币不能只按旧邮费寄送，也不改在线资产");
        var worn = original with { Inventory = original.Inventory with { Items = original.Inventory.Items.Replace(source,
            source with { Core = source.Core with { Durability = 0 } }) } };
        TestCheck.That(f.Service.PlanSend(worn, f.SenderOwner.Account, request, nowForPlan, out var wornPlan) == MailboxError.None &&
            wornPlan!.Fee == 2150 + (equipmentTemplate.ShopPrice.GetSellGold(0, equipmentTemplate.Durability) + materialValue * 4L) / 20,
            "邮寄装备价值使用当前耐久后的出售价格");
        foreach (int count in new[] { 1, 7, 10 })
            TestCheck.That(f.Service.PlanSend(original, f.SenderOwner.Account, request with { Gold = 0,
                Items = [new(0, 121, stack.Core.ItemId, count)] }, nowForPlan, out var quantityPlan) == MailboxError.None &&
                quantityPlan!.Fee == 1100 + materialValue * (long)count / 20, "材料按寄送数量计价，不按原堆叠数量或品级字段");
        TestCheck.That(f.Sender.SendMail(f.Service, request with { Recipient = "Nobody" }).Error == MailboxError.RecipientMissing &&
            f.Sender.SendMail(f.Service, request with { Recipient = "MailSender" }).Error == MailboxError.SelfSend &&
            f.Sender.SendMail(f.Service, request with { Items = [], Gold = 0, Text = "" }).Error == MailboxError.Empty,
            "实际邮件用例拒绝未知收件人、自己和空内容");
        var bound = InventoryRewardGrantService.Create(resources.Items.Get(3175), 1);
        TestCheck.That(MailboxService.TransferError(source with { Core = source.Core with { IsRental = true, ExpireTime = 0 } },
            equipmentTemplate, "a", "b", nowForPlan.ToUnixTimeSeconds()) == MailboxError.LimitedPeriod,
            "租赁装备即使未填写期限也不可寄送");
        TestCheck.That(MailboxService.TransferError(new(1, stack.Position, bound), resources.Items.Get(3175), "a", "b",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()) == MailboxError.NotTradable, "匹配资源中3175为绑定材料，继续拒绝寄送");
        var sent = f.Sender.SendMail(f.Service, request);
        TestCheck.That(sent.Accepted && sent.Inventory!.Receipt is { } receipt && f.Sender.CanPublishInventory(receipt) &&
            f.Sender.Selected!.Inventory.Gold == original.Inventory.Gold - 1000 - expectedFee &&
            f.Sender.Selected.Inventory.Find(stack.Position)!.Core.Value == 6,
            $"发送用例安装实际扣费、分堆、revision和回执；error={sent.Error}, gold={f.Sender.Selected?.Inventory.Gold}, sourceAttach={resources.Items.Get(source.Core.ItemId).AttachType}, stackAttach={resources.Items.Get(stack.Core.ItemId).AttachType}");
        var page = f.Receiver.OpenMailbox(f.Service);
        var mail = page.Page.Entries.Single();
        TestCheck.That(mail.Attachments[0].Item.Core == source.Core && mail.Attachments[0].Item.Uid == source.Uid &&
            mail.Attachments[1].Item.Uid != stack.Uid, "实际资源用例整件与分堆身份");
        var claimed = f.Receiver.ClaimMail(f.Service, mail.Attachments.Select(item => item.ClaimId).ToImmutableArray());
        TestCheck.That(claimed.Accepted && f.Receiver.Selected!.Inventory.Gold == 1000 &&
            f.Receiver.Selected.Inventory.Find(source.Position)!.Core == source.Core &&
            f.Receiver.Selected.Inventory.Find(source.Position)!.Uid == source.Uid &&
            f.Receiver.Selected.Inventory.Find(stack.Position)!.Core.Value == 4,
            "领取通过统一 grant 保留完整装备并发放指定数量");
        TestCheck.That(f.Receiver.ClaimMail(f.Service, [mail.Attachments[0].ClaimId]).Error == MailboxError.MailMissing,
            "在线会话重复领取拒绝");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var recipient = f.Service.FindRecipient("MailRecv")!;
        var more = f.Service.Deliver(new("merge", recipient, "系统", "合并", 0,
            [new(0, stack.Position, stack.Core with { Value = 3 })], now));
        var mergeMail = f.Receiver.OpenMailbox(f.Service).Page.Entries.Single(row => row.Id == more.MailId);
        var oldUid = f.Receiver.Selected!.Inventory.Find(stack.Position)!.Uid;
        TestCheck.That(f.Receiver.ClaimMail(f.Service, [mergeMail.Id]).Accepted && f.Receiver.Selected!.Inventory.Find(stack.Position) is { Core.Value: 7 } merged &&
            merged.Uid == oldUid && f.Repository.LoadCharacter(f.ReceiverOwner, 0).Inventory.Find(stack.Position)!.Uid == oldUid,
            "兼容堆叠使用既有 UID，合并后的邮箱实例不再保留为额外资产");

        var mixed = f.Service.Deliver(new("gold-owner", recipient, "系统", "金币摘要归属", 17,
            [new(0, stack.Position, stack.Core with { Value = 1 }), new(0, stack.Position, stack.Core with { Value = 1 })], now));
        var mixedMail = f.Receiver.OpenMailbox(f.Service).Page.Entries.Single(row => row.Id == mixed.MailId);
        var secondClaim = f.Receiver.ClaimMail(f.Service, [mixedMail.Attachments[1].ClaimId]);
        TestCheck.That(secondClaim.Accepted && f.Receiver.Selected!.Inventory.Gold == 1000 &&
            secondClaim.Claimed.SequenceEqual(new[] { new MailboxClaimChange(mixed.MailId, mixedMail.Attachments[1].ClaimId) }),
            "领取非金币摘要只领取该附件，回执携带实际合并 key 与摘要 key");
        var pendingGold = f.Receiver.OpenMailbox(f.Service).Page.Entries.Single(row => row.Id == mixed.MailId);
        TestCheck.That(pendingGold.Gold == 17 && pendingGold.Attachments.Length == 1, "第一摘要的金币与附件资格保留");
        var wholeClaim = f.Receiver.ClaimMail(f.Service, [mixed.MailId]);
        TestCheck.That(wholeClaim.Accepted && f.Receiver.Selected!.Inventory.Gold == 1017 &&
            wholeClaim.Claimed.SequenceEqual(new[] { new MailboxClaimChange(mixed.MailId, mixedMail.Attachments[0].ClaimId) }),
            "整封请求按实际摘要展开领取回执，金币只随金币摘要领取一次");

        var specialItems = new[] { (101550507, InventoryListType.Avatar), (63000, InventoryListType.Pet) }.Select(pair =>
            InventoryRewardGrantService.CreateItem(resources.Items.Get(pair.Item1), new(pair.Item2, 0), InventoryRewardGrantService.Create(resources.Items.Get(pair.Item1))))
            .Select(item => item.Avatar is not null ? item with { Avatar = item.Avatar with { Color1 = 2, Socket0 = new(2, 777) } } :
                item with { Creature = item.Creature! with { Name = "邮件宠物", Experience = 999, Level = 5, Satiety = 67 } }).ToImmutableArray();
        var special = f.Service.Deliver(new("special", recipient, "系统", "详情", 0, specialItems, now));
        var specialMail = f.Receiver.OpenMailbox(f.Service).Page.Entries.Single(row => row.Id == special.MailId);
        TestCheck.That(f.Receiver.ClaimMail(f.Service, [special.MailId]).Accepted &&
            f.Receiver.Selected!.Inventory.Items.Single(item => item.Avatar is not null).Avatar == specialMail.Attachments.Single(item => item.Item.Avatar is not null).Item.Avatar &&
            f.Receiver.Selected.Inventory.Items.Single(item => item.Creature is not null).Creature == specialMail.Attachments.Single(item => item.Item.Creature is not null).Item.Creature,
            "实际时装、宠物模板的邮件领取完整保留详情，不重新创建默认值");
        TestCheck.That(f.Service.Deliver(new("virtual", recipient, "系统", "虚拟", 0,
            [new(0, stack.Position, new(2681762, DfoServer.GameWorld.Items.ItemKind.Consumable, 1))], now)).Error == MailboxError.InvalidAttachment,
            "代币券托管不接受与PVF不符的消耗品核心");
        var overflow = f.Service.Deliver(new("capacity", recipient, "系统", "容量", 0,
            [new(0, source.Position, source.Core)], now));
        var filled = f.Receiver.MutateInventory(character =>
        {
            var items = character.Inventory.Items.ToBuilder();
            for (short index = 9; index < 65; index++)
                if (character.Inventory.Find(new(InventoryListType.Main, index)) is null)
                    items.Add(new(0, new(InventoryListType.Main, index), source.Core));
            return new(true, 0, new(character.Inventory, character.Inventory with { Items = items.ToImmutable() }, "fill_equipment"));
        });
        TestCheck.That(filled.Accepted && f.Receiver.ClaimMail(f.Service, [overflow.MailId]).Error == MailboxError.InventoryFull &&
            f.Receiver.OpenMailbox(f.Service).Page.Entries.Single(row => row.Id == overflow.MailId).Attachments.Length == 1,
            "容量不足整笔拒绝并保留领取资格");
        var gold = f.Service.Deliver(new("gold-limit", recipient, "系统", "金币", (uint)resources.GoldLimits.CarryLimit(60), [], now));
        TestCheck.That(f.Receiver.ClaimMail(f.Service, [gold.MailId]).Error == MailboxError.GoldCarryLimit,
            "领取金币使用当前角色携带上限");
        var smallGold = f.Service.Deliver(new("gold-prefix", recipient, "系统", "可领取金币前缀", 1, [], now));
        uint beforeGold = f.Receiver.Selected!.Inventory.Gold;
        var goldPrefix = f.Receiver.ClaimMail(f.Service, [smallGold.MailId, gold.MailId]);
        var blockedGold = f.Receiver.OpenMailbox(f.Service).Page.Entries.Single(row => row.Id == gold.MailId);
        TestCheck.That(goldPrefix.Accepted && f.Receiver.Selected!.Inventory.Gold == beforeGold + 1 &&
            goldPrefix.Claimed.SequenceEqual(new[] { new MailboxClaimChange(smallGold.MailId, smallGold.MailId),
                new MailboxClaimChange(gold.MailId, gold.MailId, MailboxError.GoldCarryLimit) }) &&
            blockedGold is { State: 1, Revision: 0 } && blockedGold.Gold == resources.GoldLimits.CarryLimit(60),
            "成功前缀按请求顺序提交金币，后续金币上限错误不消耗额度或改写未处理邮件");
        f.Receiver.MutateInventory(character => new(true, 0, new(character.Inventory,
            CurrencyWallet.Set(character.Inventory, CurrencyKind.ClearCube, int.MaxValue - 10), "mail_wallet_capacity_fixture")));
        var cubeCore = InventoryRewardGrantService.Create(resources.Items.Get(3037)) with { Value = 6 };
        var cubeMail = f.Service.Deliver(new("cube-capacity", recipient, "系统", "晶块容量", 0,
            [new(0, new(InventoryListType.Main, 358), cubeCore), new(0, new(InventoryListType.Main, 358), cubeCore)], now));
        var cubeRows = f.Receiver.OpenMailbox(f.Service).Page.Entries.Single(row => row.Id == cubeMail.MailId).Attachments;
        var cubePrefix = f.Receiver.ClaimMail(f.Service, [cubeMail.MailId]);
        TestCheck.That(cubePrefix.Accepted && f.Receiver.Selected!.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == int.MaxValue - 4 &&
            cubePrefix.Claimed.SequenceEqual(new[] { new MailboxClaimChange(cubeMail.MailId, cubeRows[0].ClaimId),
                new MailboxClaimChange(cubeMail.MailId, cubeRows[1].ClaimId, MailboxError.InventoryFull) }) &&
            f.Receiver.OpenMailbox(f.Service).Page.Entries.Single(row => row.Id == cubeMail.MailId).Attachments is [{ Item.Core.Count: 6 }] &&
            f.Receiver.Selected.Inventory.Items.All(item => item.Core.ItemId != 3037),
            "钱包溢出不能截断后吞附件：只领六个，剩余六个仍在邮件，背包不创建晶块实体");

        // 原快照之外的规划允许同账号 GM 修改；同 revision 的新快照同样使旧计划失效。
        using var race = new Fixture(towns, characters, resources, material);
        using var proxy = new BlockingStore(race.Repository);
        var service = new MailboxService(proxy, resources.Items, resources.GoldLimits, resources.EpicPieces);
        var text = new MailboxSendRequest("MailRecv", 0, [], "计划中");
        var pending = Task.Run(() => race.Sender.SendMail(service, text));
        TestCheck.That(proxy.Started.Wait(TimeSpan.FromSeconds(5)), "寄送在账号锁外进入查询规划");
        long revision = race.Sender.Selected!.Revision;
        race.Sender.UpdateProgress(new(CharacterProgressField.Level, race.SenderOwner.Account, "MailSender", 61, 0));
        proxy.Release.Set();
        TestCheck.That((await pending).Error == MailboxError.InvalidState && race.Sender.Selected!.Revision == revision &&
            race.Receiver.OpenMailbox(race.Service).Page.Entries.IsEmpty, "同 revision GM 修改不能被原规划覆盖或发信");
        race.Sender.ReturnToRoster();
        TestCheck.That(!race.Sender.CanPublishMailbox(new(original, race.SenderOwner.Generation, 1)), "旧选角 epoch 不发布邮件");
        Console.WriteLine($"PASS {TestCheck.Count - before} 实际资源邮件用例、grant、容量与会话规划断言");
    }

    public static void RunEpicExchangeOriginPropagation(TownCatalog towns, CharacterCatalog characters,
        ItemResources resources, ItemTemplate material)
    {
        using var fixture = new Fixture(towns, characters, resources, material, withEpicOrigin: true);
        var sender = fixture.Sender.Selected!;
        var source = sender.Inventory.Find(new(InventoryListType.Main, 9))!;
        TestCheck.That(source.Core.ItemId == 18400 && source.Core.EpicExchangeOrigin is { Version: 1, SourceItemId: 490705251 },
            "来源邮件前置复用现有实际可邮寄装备18400与持久UID，不修改模板交易资格");
        var request = new MailboxSendRequest("MailRecv", 0,
            [new(0, source.Position.Index, source.Core.ItemId, 1)], "装备来源传播");
        var sent = fixture.Sender.SendMail(fixture.Service, request);
        TestCheck.That(sent.Accepted && fixture.Sender.Selected!.Inventory.Items.All(item => item.Uid != source.Uid),
            "合法来源装备真实寄送成功且原库存移除，不能新增禁邮寄");
        var message = fixture.Receiver.OpenMailbox(fixture.Service).Page.Entries.Single();
        var attachment = message.Attachments.Single();
        TestCheck.That(attachment.Item.Uid == source.Uid && attachment.Item.Core == source.Core,
            "邮箱持久附件保持整件UID、完整Core与装备来源");
        var claimed = fixture.Receiver.ClaimMail(fixture.Service, [attachment.ClaimId]);
        TestCheck.That(claimed.Accepted, "来源装备经真实邮箱领取提交成功");
        var received = fixture.Receiver.Selected!.Inventory.Items.Single(item => item.Uid == source.Uid);
        var persisted = fixture.Repository.LoadCharacter(fixture.ReceiverOwner, 0).Inventory.Items.Single(item => item.Uid == source.Uid);
        TestCheck.That(received.Core == source.Core && persisted.Core == source.Core &&
            received.Core.EpicExchangeOrigin == source.Core.EpicExchangeOrigin,
            "收件人在线及Repository实际重读均保留同UID来源与全部核心字段");
        TestCheck.That(fixture.Receiver.ClaimMail(fixture.Service, [attachment.ClaimId]).Error == MailboxError.MailMissing,
            "来源邮件已领取重放不得复制实例");
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-mail-resource-" + Guid.NewGuid().ToString("N"));
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease SenderOwner { get; }
        public AccountOwners.Lease ReceiverOwner { get; }
        public CharacterRepository Repository { get; }
        public GameSession Sender { get; }
        public GameSession Receiver { get; }
        public MailboxService Service { get; }
        public Fixture(TownCatalog towns, CharacterCatalog characters, ItemResources resources, ItemTemplate material, bool withEpicOrigin = false)
        {
            SenderOwner = Owners.Acquire("mail_sender", () => { });
            ReceiverOwner = Owners.Acquire("mail_receiver", () => { });
            Repository = new(Path.Combine(root, "game.db"));
            var equipment = InventoryRewardGrantService.Create(resources.Items.Get(18400)) with { Attr = 7, EnchantCardId = 123,
                EnchantUpgrade = 1, AmplifyType = 1, AmplifyValue = 25, Rune = 3, Random0 = new(1, 2, 3), RandomChangedIndex = 255,
                EpicExchangeOrigin = withEpicOrigin ? new EpicExchangeOrigin(1, 490705251) : null,
                DisjointForbidden = withEpicOrigin, DestructionSoulForbidden = withEpicOrigin };
            Repository.Create(SenderOwner, new("MailSender", towns.Room, towns.InitialReturn) { Level = 60,
                Inventory = new([new(0, new(InventoryListType.Main, 9), equipment),
                    new(0, new(InventoryListType.Main, 121), InventoryRewardGrantService.Create(material, 10))], Gold: 1_000_000) });
            Repository.Create(ReceiverOwner, new("MailRecv", towns.Room, towns.InitialReturn) { Level = 60, Inventory = new([]) });
            Sender = new(Repository, towns, SenderOwner);
            Receiver = new(Repository, towns, ReceiverOwner);
            Sender.Select(0, resources.GoldLimits); Sender.MarkReady();
            Receiver.Select(0, resources.GoldLimits); Receiver.MarkReady();
            Service = new(Repository, resources.Items, resources.GoldLimits, resources.EpicPieces);
        }
        public void Dispose() { ReceiverOwner.Dispose(); SenderOwner.Dispose(); TestFiles.DeleteDirectory(root, true); }
    }
    internal static ItemTemplate TradableMaterial(PvfArchive pvf, ItemResources resources)
    {
        foreach (var entry in PvfScript.List(pvf.ReadText("stackable/stackable.lst")).OrderBy(entry => entry.Key))
        {
            // 路径只缩小测试候选；类别、交易与期限最终都核对模板字段。
            if (!entry.Value.Contains("material", StringComparison.OrdinalIgnoreCase))
                continue;
            var template = resources.Items.Get(entry.Key);
            if (template.Kind == ItemKind.Material && template.StackLimit >= 10 &&
                template.AttachType.Replace("`", "").Trim().Trim('[', ']').Trim() == "free" &&
                InventoryRewardGrantService.Create(template).ExpireTime == 0 &&
                DfoServer.Game.Currency.CurrencyWallet.RewardKind(template.Id) is null && !resources.EpicPieces.ByPiece.ContainsKey(template.Id))
                return template;
        }
        throw new InvalidDataException("匹配资源缺少邮件回归所需的永久可交易材料");
    }
    sealed class BlockingStore(IMailboxStore inner) : IMailboxStore, IDisposable
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public MailboxRecipient? FindMailRecipient(string name)
        {
            var result = inner.FindMailRecipient(name);
            Started.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return result;
        }
        public MailboxPage LoadMailbox(AccountOwners.Lease owner, ushort id, long now) => inner.LoadMailbox(owner, id, now);
        public ImmutableArray<MailboxClaim> LoadMailClaims(AccountOwners.Lease owner, ushort id, ImmutableArray<uint> ids, long now) => inner.LoadMailClaims(owner, id, ids, now);
        public void ExpireMailbox(AccountOwners.Lease owner, ushort id, long now) => inner.ExpireMailbox(owner, id, now);
        public MailboxResult SendMail(AccountOwners.Lease owner, Character expected, MailboxSendPlan plan) => inner.SendMail(owner, expected, plan);
        public MailboxResult ClaimMail(AccountOwners.Lease owner, Character expected, MailboxClaimPlan plan) => inner.ClaimMail(owner, expected, plan);
        public MailboxResult ChangeMailState(AccountOwners.Lease owner, ushort id, ImmutableArray<uint> ids, ushort state, long now, bool force = false) => inner.ChangeMailState(owner, id, ids, state, now, force);
        public MailboxResult ForceDeleteMails(AccountOwners.Lease owner, ushort id, ImmutableArray<uint> ids, long now) => inner.ForceDeleteMails(owner, id, ids, now);
        public MailboxResult DeliverMail(MailboxDelivery delivery) => inner.DeliverMail(delivery);
        public void Dispose() { Release.Set(); Release.Dispose(); Started.Dispose(); }
    }
}
