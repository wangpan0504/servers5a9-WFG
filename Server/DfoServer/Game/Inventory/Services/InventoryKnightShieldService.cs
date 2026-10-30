using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryKnightShieldService(Func<int, ItemTemplate> items, KnightShieldCatalog shields, TimeProvider? clock = null)
{
    public const byte Job = 12, DeckList = 33, CollectionList = 34;
    public static InventorySlot MainSlot { get; } = new(InventoryListType.Equipment, 24);
    readonly TimeProvider time = clock ?? TimeProvider.System;
    public static bool Recognizes(InventoryMoveRequest request) =>
        (byte)request.Source.List is DeckList or CollectionList || (byte)request.Destination.List is DeckList or CollectionList;

    public bool Eligible(Character character, int id) => character.Job == Job && id > 0 &&
        shields.Definitions.TryGetValue(id, out var definition) &&
        (definition.GrowType == 0 || definition.GrowType == (character.GrowType & 0x0f)) &&
        (definition.RequiredLevel > 0 ? character.Level >= definition.RequiredLevel :
            character.CompletedQuests.Contains(definition.ClearQuest) || character.AccountCompletedQuests.Contains(definition.ClearQuest));

    public KnightShieldDeck EffectiveDeck(Character character)
    {
        var stored = character.Inventory.ShieldDeck;
        // 旧存档只读接管资格明确的真实主盾；首次玩家配置操作才与原实体一并保存。
        if (stored.Same(KnightShieldDeck.Empty) && character.Inventory.Find(MainSlot) is
            { Core.Kind: ItemKind.Equipment, Core.IsRental: false } existing && Eligible(character, existing.Core.ItemId))
            return new([existing.Core.ItemId, 0, 0, 0, 0]);
        return stored;
    }

    public InventoryResult Change(Character character, KnightShieldDeck requested)
    {
        try { requested.Validate(); }
        catch (InvalidDataException) { return Reject(); }
        // 主盾的实体重建必须经过CMD19；657仅保存客户端提交的备用位排列。
        if (requested.Items[0] != EffectiveDeck(character).Items[0]) return Reject();
        return Plan(character, requested);
    }

    public InventoryResult Move(Character character, InventoryMoveRequest request)
    {
        if (request.Actor is not (-1 or 0) || request.Count != 0 || request.DestinationCount != 0 ||
            request.Flag0 != 0 || request.Flag1 != 0 || request.DestinationItemId != 0) return Reject();
        var deck = EffectiveDeck(character).Items;
        byte source = (byte)request.Source.List, target = (byte)request.Destination.List;
        if (source == CollectionList && target == DeckList && request.Source.Index == 0 && request.Destination.Index == 0 &&
            Eligible(character, request.SourceItemId) && !deck.Skip(1).Contains(request.SourceItemId))
            deck = deck.SetItem(0, request.SourceItemId);
        else if (source == DeckList && target == DeckList && request.SourceItemId == 0 &&
            request.Source.Index is >= 0 and < KnightShieldDeck.Count && request.Destination.Index is >= 0 and < KnightShieldDeck.Count &&
            (request.Source.Index == 0 || request.Destination.Index == 0))
        {
            int old = deck[request.Source.Index];
            deck = deck.SetItem(request.Source.Index, deck[request.Destination.Index]).SetItem(request.Destination.Index, old);
        }
        else if (source == DeckList && target == CollectionList && request.SourceItemId == 0 &&
            request.Source.Index == 0 && request.Destination.Index == 5)
            deck = deck.SetItem(0, 0);
        else return Reject();
        return Plan(character, new(deck));
    }

    InventoryResult Plan(Character character, KnightShieldDeck requested)
    {
        if (character.Job != Job || requested.Items.Any(id => id != 0 && !Eligible(character, id))) return Reject();
        var before = character.Inventory;
        before.ShieldDeck.Validate();
        // 虚拟配置只能管理自身主盾，不删除存量角色独立持有的支持装备。
        int oldPrimary = EffectiveDeck(character).Items[0];
        if ((before.Find(MainSlot)?.Core.ItemId ?? 0) != oldPrimary) return Reject();
        var after = before with { ShieldDeck = requested };
        if (oldPrimary != requested.Items[0])
        {
            var previous = before.Find(MainSlot);
            if (previous is { Core.EquipmentLockId: not 0 } || previous is { Core.IsRental: true }) return Reject();
            // 五位配置只存模板，无法承载付费打孔、徽章或其它实例改造；不得重建吞掉这些状态。
            if (previous is not null && previous.Core != InventoryRewardGrantService.Create(items(oldPrimary), utcNow: time.GetUtcNow()))
                return InventoryResult.Reject(23) with { RejectReason = "customized_knight_shield" };
            var retained = before.Items.Where(item => item.Position != MainSlot).ToImmutableArray();
            if (requested.Items[0] != 0)
            {
                var template = items(requested.Items[0]);
                if (template.Kind != ItemKind.Equipment || template.EquipmentSlot != MainSlot.Index ||
                    !InventoryMoveService.WearLevelSatisfied(character, template, time.GetUtcNow().ToUnixTimeSeconds()) ||
                    !InventoryMoveService.RequiredSkillSatisfied(character, template)) return Reject();
                retained = retained.Add(new(0, MainSlot, InventoryRewardGrantService.Create(template, utcNow: time.GetUtcNow())));
            }
            after = after with { Items = retained };
        }
        return new(true, 0, new(before, after, "knight_shield_deck", new(character)));
    }

    static InventoryResult Reject() => InventoryResult.Reject(23) with { RejectReason = "invalid_knight_shield_deck" };
}
