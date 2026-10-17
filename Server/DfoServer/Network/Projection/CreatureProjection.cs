using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class CreatureProjection(CreatureService service, TownPresence presence, QuestProjection? quests = null)
{
    public Task QuestsAsync(GameConnectionContext context, Game.Characters.Character character, Func<bool> guard) =>
        quests?.RefreshAsync(context, character, guard) ?? Task.CompletedTask;

    public Task QuestsChangedAsync(GameConnectionContext context, InventorySnapshot previous, Game.Characters.Character character, Func<bool> guard) =>
        quests?.CreatureChangedAsync(context, character with { Inventory = previous }, character, guard) ?? Task.CompletedTask;

    public async Task ChangedAsync(GameConnectionContext context, InventoryResult result)
    {
        if (!result.Accepted || result.Plan is not { Changed: true } plan || result.Receipt is not { } receipt) return;
        await ChangedAsync(context, plan.Before, receipt.Character, receipt.Phase, () => context.Session.CanPublishInventory(receipt));
    }

    public async Task ChangedAsync(GameConnectionContext context, InventorySnapshot previous, Game.Characters.Character character,
        SessionPhase phase, Func<bool> guard)
    {
        var before = previous.Find(CreatureRules.EquippedSlot);
        var after = character.Inventory.Find(CreatureRules.EquippedSlot);
        if (before?.Creature is not { } old || after?.Creature is not { } current || before.Uid != after.Uid) return;
        bool Guard() => guard();
        if (!Guard()) return;
        if (phase == SessionPhase.Town)
            context.Owner.Execute(() =>
            {
                if (Guard()) presence.Refresh(context.Owner, context.Sender, character, broadcast: false);
                return true;
            });
        if (old.Experience != current.Experience || old.Level != current.Level || old.Mode != current.Mode ||
            old.Mode1Field0 != current.Mode1Field0 || old.Mode1Field1 != current.Mode1Field1)
            await context.Notify(NotiPacketType.GAIN_EXP_CREATURE, CreaturePackets.Experience(current), "creature_experience", Guard);
        if (old.Name != current.Name)
            await BroadcastAsync(context, NotiPacketType.RENAME_CREATURE, CreaturePackets.Renamed(character.Id, current.Name),
                "creature_renamed", Guard);
        if (before.Core.ItemId != after.Core.ItemId)
        {
            var target = service.Catalog.Get(after.Core.ItemId) ?? throw new InvalidOperationException("进化回执缺少目标资源");
            await BroadcastAsync(context, NotiPacketType.EVOLUTE_CREATURE,
                CreaturePackets.Evolution(checked((ushort)target.Species), character.Id), "creature_evolved", Guard);
            await context.Notify(NotiPacketType.CREATURE_ITEM_LIST, InventoryPackets.Creatures(character.Inventory),
                "creature_evolution_details", Guard);
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,
                InventoryPackets.Delta(character.Inventory, InventoryListType.Equipment, [CreatureRules.EquippedSlot]),
                "creature_evolution_item", Guard);
        }
        if (old.Satiety != current.Satiety)
            await context.Notify(NotiPacketType.CREATURE_STATE, CreaturePackets.State(after.Uid, current.Satiety), "creature_satiety", Guard);
        if (old.Satiety > 0 && current.Satiety == 0)
            await BroadcastAsync(context, NotiPacketType.DIED_CREATURE, CreaturePackets.Actor(character.Id), "creature_starved", Guard);
        else if (old.Satiety == 0 && current.Satiety > 0)
            await BroadcastAsync(context, NotiPacketType.REVIVAL_CREATURE, CreaturePackets.Actor(character.Id), "creature_revived", Guard);
        if (quests is not null && phase == SessionPhase.Town && (old.Level != current.Level || before.Core.ItemId != after.Core.ItemId))
            await QuestsChangedAsync(context, previous, character, Guard);
    }

    public async Task BroadcastAsync(GameConnectionContext context, NotiPacketType opcode, byte[] body, string action, Func<bool> guard)
    {
        await context.Notify(opcode, body, action, guard);
        context.Owner.Execute(() =>
        {
            if (guard() && context.Session.Phase == SessionPhase.Town)
                presence.Publish(context.Owner, context.Sender, (ushort)opcode, body, action);
            return true;
        });
    }
}
