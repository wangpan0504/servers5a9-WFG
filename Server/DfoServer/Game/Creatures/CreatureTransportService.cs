using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Creatures;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Creatures;

internal sealed record CreatureTransportRequest(ushort StoneSlot, ushort PetSlot, byte Mode, uint RecipientIndex);
internal sealed record CreatureTransportPlan(Character Character, CharacterRosterEntry Recipient,
    InventoryPlan Inventory, InventoryItem Pet, long Created, InventoryItem Stone, CreatureTransportMailExpiry MailExpiry);
internal sealed record CreatureTransportCommit(Character Character, uint MailId);
internal sealed record CreatureTransportResult(InventoryResult Inventory, ushort RecipientId = 0, uint MailId = 0);

// 专用跨界资格与普通寄件交易资格分开，持久化仍复用同一库存及邮件托管事务。
internal interface ICreatureTransportStore
{
    CreatureTransportCommit CommitCreatureTransport(AccountOwners.Lease owner, CreatureTransportPlan plan,
        CreatureTransportMailPolicy currentPolicy);
}

internal sealed class CreatureTransportService(Func<int, ItemTemplate> items,
    Func<int, ActionConsumableDefinition?> definitions, CreatureCatalog creatures, ICreatureTransportStore store,
    Func<CreatureTransportMailPolicy> currentMailPolicy,
    TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;

    public ushort Plan(Character character, CharacterRosterEntry recipient, CreatureTransportRequest request,
        string place, out CreatureTransportPlan? plan)
    {
        plan = null;
        if (request.Mode != 1 || request.StoneSlot > short.MaxValue || request.PetSlot > short.MaxValue ||
            recipient.Id == 0 || recipient.Id == character.Id) return 7;
        var sourceSlot = new InventorySlot(InventoryListType.Main, (short)request.StoneSlot);
        var targetSlot = new InventorySlot(InventoryListType.Pet, (short)request.PetSlot);
        var source = character.Inventory.Find(sourceSlot);
        var target = character.Inventory.Find(targetSlot);
        if (source is null || target is null) return 17;
        if (source.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0 || target.Core.SortLockFlag != 0) return 214;
        var now = clock.GetUtcNow();
        if (target is not { Uid: > 0, Core.Kind: ItemKind.Creature, Avatar: null, Creature: not null } ||
            target.Core.Value != target.Uid || target.Core.IsRental || target.IsExpired(now.ToUnixTimeSeconds()) ||
            !ItemSlotBoundService.Allows(character.Inventory, targetSlot, target.Core.Kind)) return 7;
        var definition = definitions(source.Core.ItemId);
        // ARAD 024C7270 的26号部位分支排除宠物蛋，不套普通装备跨界的稀有度/等级成本。
        if (definition is not { Label: "[creature transport stone]", Parameters: [>= 0] } ||
            items(target.Core.ItemId) is not { Kind: ItemKind.Creature, EquipmentSlot: 26, CreatureSubType: 0 } ||
            creatures.Get(target.Core.ItemId) is not { Egg: false }) return 7;
        var paid = InventoryActionConsumableService.Plan(character, sourceSlot, definition, place, clock, now);
        if (!paid.Accepted) return 21;
        var after = paid.Plan!.After with { Items = paid.Plan.After.Items.Remove(target) };
        long? deadline = new[] { source.ExpireTime, target.ExpireTime }.Where(value => value > 0).Select(value => (long?)value).Min();
        if (definition.DailyLimit > 0) deadline = Math.Min(deadline ?? long.MaxValue, DailyBoosterUsage.NextReset(now).ToUnixTimeSeconds());
        long created = now.ToUnixTimeSeconds();
        var expiry = currentMailPolicy().Create(source.Core.ItemId, created);
        plan = new(character, recipient, new(character.Inventory, after, "creature_transport",
            new(character, deadline, clock, requirePosition: true)), target, created, source, expiry);
        return 0;
    }

    public CreatureTransportCommit Commit(AccountOwners.Lease owner, CreatureTransportPlan plan) =>
        store.CommitCreatureTransport(owner, plan, currentMailPolicy());
}
