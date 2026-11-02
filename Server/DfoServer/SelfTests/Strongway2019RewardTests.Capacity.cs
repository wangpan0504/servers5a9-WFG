using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class Strongway2019RewardTests
{
    public static void RunFullInventoryResources(ItemResources resources)
    {
        int before = Count;
        using var fixture = new Fixture();
        var now = DateTimeOffset.Parse("2026-11-04T06:00:00+08:00");
        var mailbox = new MailboxService(fixture.Repository, resources.Items, resources.GoldLimits, resources.EpicPieces);
        var current = fixture.Repository.LoadCharacter(fixture.Owner, fixture.First.Slot);
        var consumable = resources.Items.Get(490013318);
        That(consumable.Kind == ItemKind.Consumable && consumable.StackLimit == 1,
            "满消耗品页仅用真实旧318单张核心作合法夹具，不改活动奖励");
        current = Fill(current, ItemKind.Consumable, InventoryRewardGrantService.Create(consumable, utcNow: now));
        var plan = fixture.Clear(current.Slot, 5359, 490012735, now);
        plan = plan with { Item = InventoryRewardGrantService.CreateItem(resources.Items.Get(490012735),
            new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(resources.Items.Get(490012735), utcNow: now)) };
        var awarded = fixture.Repository.CommitStrongway2019Reward(fixture.Owner, plan);
        current = fixture.Repository.LoadCharacter(fixture.Owner, current.Slot);
        var mail = mailbox.Inspect(fixture.Owner, current.Id, now).Entries.Single(entry => entry.Id == awarded.MailId);
        var attachment = mail.Attachments.Single();
        That(awarded is { Granted: true, MailId: > 0 } && attachment.Item.Core.ItemId == 490012735 &&
            fixture.Repository.ReadStrongway2019RewardState(fixture.Owner, Strongway2019RewardPeriod.At(now),
                Strongway2019RewardBucket.Challenge) == new Strongway2019RewardState(1, 1),
            "满消耗品页仍将真实735附件、日周额度与奖励回执完整持久提交");
        string full = Saved();
        That(mailbox.PlanClaim(fixture.Owner, current, [attachment.ClaimId], now, out var blocked) == MailboxError.InventoryFull &&
            blocked is null && Saved() == full,
            "满消耗品页领取真实活动附件整拒，保原邮件附件UID、资产、revision和已用额度");
        var consumeRange = ItemSlotBoundService.Range(ItemKind.Consumable, current.Inventory);
        current = Remove(current, new(InventoryListType.Main, consumeRange.Start));
        That(mailbox.PlanClaim(fixture.Owner, current, [attachment.ClaimId], now, out var claim) == MailboxError.None && claim is not null,
            "只腾一消耗品格后同一活动附件原claim可正常规划领取");
        var claimed = mailbox.Claim(fixture.Owner, current, claim!);
        That(claimed.Accepted, "活动奖励附件沿正式Claim原子领取成功");
        current = new Persistence.CharacterRepository(fixture.Path).LoadCharacter(fixture.Owner, current.Slot);
        var box = current.Inventory.Items.Single(item => item.Uid == attachment.Item.Uid);
        That(box.Core == attachment.Item.Core && box.Position == new InventorySlot(InventoryListType.Main, consumeRange.Start) &&
            !mailbox.Inspect(fixture.Owner, current.Id, now).Entries.SelectMany(entry => entry.Attachments).Any(item => item.ClaimId == attachment.ClaimId),
            "领取后重读恢复原附件UID与完整核心，邮件不再保留可领取的同一附件");
        string claimedState = Saved();
        That(mailbox.PlanClaim(fixture.Owner, current, [attachment.ClaimId], now, out _) != MailboxError.None && Saved() == claimedState,
            "旧claim重放不复制真实活动盒或改变资产");

        var proof = resources.Items.Get(490012698);
        var choice = resources.Items.Get(490012699);
        That(proof.Kind == choice.Kind && proof.Kind != ItemKind.Consumable &&
            proof.AccountMaterial is null && choice.AccountMaterial is null,
            "735两项奖励沿真实同类实体材料分页，不能借消耗品腾格假造容量正控");
        var materialRange = ItemSlotBoundService.Range(proof.Kind, current.Inventory);
        // 合法实例属性隔离填充堆，避免奖励直接合入夹具而失去两种新产物需要两个槽的边界。
        var filler = InventoryRewardGrantService.Create(proof, utcNow: now) with { TradeRestriction = 1 };
        current = Fill(current, proof.Kind, filler, materialRange.End);
        var booster = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, contracts: resources.Contracts, avatars: resources.AvatarPackages);
        string crowded = Saved();
        var rejected = booster.Open(current, new(box.Position.Index, 1), out var rejectedReceipt, now);
        That(!rejected.Accepted && rejected.Error == 4 && rejected.Plan is null && rejectedReceipt is null && Saved() == crowded &&
            current.Inventory.Find(box.Position) == box,
            "735必得两种材料但只剩一格时整笔拒绝，不扣盒、不增任一材料、不溢出邮件或重签额度");
        current = Remove(current, new(InventoryListType.Main, materialRange.Start));
        long proofBefore = Amount(current, 490012698), choiceBefore = Amount(current, 490012699);
        var opened = booster.Open(current, new(box.Position.Index, 1), out var receipt, now);
        That(opened.Accepted && opened.Plan!.OverflowItems.IsEmpty && receipt is not null &&
            receipt.Rewards.OrderBy(row => row.ItemId).SequenceEqual(new BoosterGrantedItem[] { new(490012698, 20), new(490012699, 5) }),
            "腾第二个真实材料格后同一735完整规划20证明和5选择，不当作随机二选一");
        current = InventoryService.Commit(fixture.Repository, fixture.Owner, current, opened);
        var restored = new Persistence.CharacterRepository(fixture.Path).LoadCharacter(fixture.Owner, current.Slot);
        That(Amount(restored, 490012698) == proofBefore + 20 && Amount(restored, 490012699) == choiceBefore + 5 &&
            restored.Inventory.Items.All(item => item.Uid != box.Uid) && JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(current) &&
            fixture.Repository.ReadStrongway2019RewardState(fixture.Owner, Strongway2019RewardPeriod.At(now),
                Strongway2019RewardBucket.Challenge) == new Strongway2019RewardState(1, 1),
            "实际提交及新仓储重读只消费原盒一次，两材料足额持久，领取开盒不再占日周额度");
        Console.WriteLine($"PASS {Count - before} 首期满页奖励投递、领取恢复及双材料开盒容量原子性");

        Character Fill(Character character, ItemKind kind, ItemCore core, short? leaveFree = null)
        {
            var range = ItemSlotBoundService.Range(kind, character.Inventory);
            var occupied = character.Inventory.Items.Select(item => item.Position).ToHashSet();
            var added = Enumerable.Range(range.Start, range.End - range.Start + 1)
                .Select(index => new InventorySlot(InventoryListType.Main, checked((short)index)))
                .Where(position => position.Index != leaveFree && !occupied.Contains(position))
                .Select(position => InventoryRewardGrantService.CreateItem(resources.Items.Get(core.ItemId), position, core));
            var after = character.Inventory with { Items = character.Inventory.Items.AddRange(added)
                .OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray() };
            return InventoryService.Commit(fixture.Repository, fixture.Owner, character,
                new(true, 0, new(character.Inventory, after, "fixture_strongway_capacity", new(character))));
        }
        Character Remove(Character character, InventorySlot position)
        {
            var item = character.Inventory.Find(position)!;
            return InventoryService.Commit(fixture.Repository, fixture.Owner, character,
                InventoryDeleteService.Plan(character.Inventory, position, item.Core.ItemId, 1));
        }
        string Saved() => JsonSerializer.Serialize(fixture.Repository.List(fixture.Owner)) +
            JsonSerializer.Serialize(mailbox.Inspect(fixture.Owner, fixture.First.Id, now)) + fixture.Snapshot();
        static long Amount(Character character, int id) => character.Inventory.Items.Where(item => item.Core.ItemId == id).Sum(item => (long)item.Core.Count);
    }
}
