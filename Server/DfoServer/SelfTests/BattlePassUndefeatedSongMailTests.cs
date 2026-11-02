using System.Collections.Immutable;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassUndefeatedSongMailTests
{
    public static void RunCore()
    {
        int before = Count; var item = Song(7, 25);
        var plan = Plan([item], InventorySnapshot.Empty, new([item]));
        var fact = BattlePassUndefeatedSongMailSource.Prepare(1, plan)!;
        That(fact.Amount == 25 && fact.SourceId.Length == 32 && fact.SourceId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "原25材料实际提取按正数量生成符合真实表的32hex来源");
        That(BattlePassUndefeatedSongMailSource.Prepare(1, plan) == fact, "同一明确原成功附件来源稳定，不依赖随机或请求顺序");
        var old = Song(5, 10); var merged = old with { Core = old.Core with { Value = 35 } };
        That(BattlePassUndefeatedSongMailSource.Prepare(1, Plan([item], new([old]), new([merged])))!.Amount == 25,
            "原附件合堆只计本笔25，不把旧余额10计入且不要求heldUID仍占格");
        var walletBefore = InventorySnapshot.Empty with
            { AccountWallet = InventorySnapshot.Empty.AccountWallet.SetMaterial(10308807, new AccountMaterialBalance(10)) };
        var walletAfter = walletBefore with
            { AccountWallet = walletBefore.AccountWallet.SetMaterial(10308807, new AccountMaterialBalance(35)) };
        That(BattlePassUndefeatedSongMailSource.Prepare(1, Plan([item], walletBefore, walletAfter))!.Amount == 25,
            "账号材料余额从10增至35仅按原附件实际增加25计数");
        foreach (int balance in new[] { 9, 34, 36 })
        {
            var wrong = walletBefore with
                { AccountWallet = walletBefore.AccountWallet.SetMaterial(10308807, new AccountMaterialBalance(balance)) };
            Reject<InvalidDataException>(() => BattlePassUndefeatedSongMailSource.Prepare(1, Plan([item], walletBefore, wrong)),
                "账号材料余额减少或与附件数量不符必须拒绝");
        }
        Reject<InvalidDataException>(() => BattlePassUndefeatedSongMailSource.Prepare(1,
            Plan([item], walletBefore, walletAfter with { Items = [item] })),
            "实体与账号余额重复增加不能把25附件当成50取得");
        var second = Song(8, 15); var partial = Plan([item], InventorySnapshot.Empty, new([item]), authorized: [item, second]);
        var firstResult = partial.Results[0]; partial = partial with { Results = [firstResult, new(firstResult.MailId, partial.Claims[0].Mail.Attachments[1].ClaimId, MailboxError.InventoryFull)] };
        That(BattlePassUndefeatedSongMailSource.Prepare(1, partial) == fact,
            "整封原请求只成功可领前缀25时，不把另15未领取附件或错误项算入来源");
        That(BattlePassUndefeatedSongMailSource.Prepare(1, Plan([], InventorySnapshot.Empty, InventorySnapshot.Empty)) is null,
            "金币或其它非目标附件提取无不渝来源");
        foreach (var invalid in new[]
        {
            Plan([item], InventorySnapshot.Empty, new([item with { Core = item.Core with { Value = 24 } }])),
            Plan([item], InventorySnapshot.Empty, InventorySnapshot.Empty),
            Plan([item, item], InventorySnapshot.Empty, new([item])),
            Plan([item with { Uid = 0 }], InventorySnapshot.Empty, new([item])),
            Plan([item with { Core = item.Core with { Kind = ItemKind.Consumable } }], InventorySnapshot.Empty, new([item])),
            plan with { Inventory = new(plan.Inventory.Before, plan.Inventory.After, "reward_grant") },
            plan with { Items = [item with { Core = item.Core with { Value = 24 } }] },
            plan with { Results = [new(plan.Results[0].MailId, plan.Results[0].ClaimId, MailboxError.InventoryFull)] },
            Plan([item], InventorySnapshot.Empty, new([item]), recipient: 2)
        }) Reject<InvalidDataException>(() => BattlePassUndefeatedSongMailSource.Prepare(1, invalid),
            "错误核心/实物差量/重复UID/原附件或成功前缀不符时不能冒本次实际取得");
        Reject<InvalidDataException>(() => BattlePassUndefeatedSongMailSource.Prepare(0, plan), "空角色不注册邮件来源");
        foreach (var other in new[]
        {
            Plan([item], InventorySnapshot.Empty, new([item]), mail: 12),
            Plan([item], InventorySnapshot.Empty, new([item]), revision: 4),
            Plan([item], InventorySnapshot.Empty, new([item]), attachmentStart: 2),
            Plan([second], InventorySnapshot.Empty, new([second]))
        }) That(BattlePassUndefeatedSongMailSource.Prepare(1, other)!.SourceId != fact.SourceId,
            "原mail/revision/附件及heldUID数量身份各自隔离，不能借其它领取重复source");
        var otherRole = Plan([item], InventorySnapshot.Empty, new([item]), recipient: 2);
        That(BattlePassUndefeatedSongMailSource.Prepare(2, otherRole)!.SourceId != fact.SourceId, "不同真实原角色附件身份不串用");
        Console.WriteLine($"PASS {Count - before} BattlePassUndefeatedSongMailTests core");
    }

    static InventoryItem Song(long uid, int count) => new(uid, new(InventoryListType.Main, 121), new(10308807, ItemKind.Material, count));
    static MailboxClaimPlan Plan(ImmutableArray<InventoryItem> received, InventorySnapshot before, InventorySnapshot after,
        uint mail = 11, long revision = 3, uint attachmentStart = 1, ushort recipient = 1,
        ImmutableArray<InventoryItem>? authorized = null)
    {
        var attachments = (authorized ?? received).Select((item, index) => new MailboxAttachment(attachmentStart + checked((uint)index), item)).ToImmutableArray();
        var entry = new MailboxEntry(mail, revision, recipient, 0, "奖励发放", "原邮件", 0, 1, null, 1, true, attachments);
        var results = received.Select(item => new MailboxClaimChange(mail, attachments.First(a => a.Item == item).ClaimId)).ToImmutableArray();
        return new(new(before, after, "mail_claim"), [new(mail, entry, null)], received, [], 10, results);
    }
}
