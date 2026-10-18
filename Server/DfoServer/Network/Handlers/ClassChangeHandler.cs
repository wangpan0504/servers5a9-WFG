using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class ClassChangeHandler(ClassChangeService service, CharacterLevelProjection level,
    InventoryProjection inventory, QuestProjection quests, TownProjection town, ServerLog log, TimeProvider? clock = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.USE_RIGHT_OF_CHANGE_GROW_TYPE, Handle, nameof(ClassChangeHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var request = ClassChangeRequests.Read(packet.Body);
        var result = context.Activity?.BlocksTownActions == true ? context.Owner.Execute(() =>
            new ClassChangeResult(30, "active-town-operation", context.Session.Selected, context.Owner.Generation,
                context.Session.SelectionEpoch, context.Session.Phase)) :
            context.Session.ChangeClass(service, request, (clock ?? TimeProvider.System).GetUtcNow());
        bool Guard() => context.Session.CanPublishClassChange(result);
        log.Write("class_change", new { Connection = context.Id, request.Slot, request.Target, request.Confirmed,
            Accepted = result.Error == 0, result.Error, result.Reason, Character = result.Character?.Id,
            PreviousGrow = result.Plan?.Expected.GrowType, Grow = result.Character?.GrowType, result.Character?.Revision });
        if (result.Error != 0)
        {
            await context.Reply(CmdPacketType.USE_RIGHT_OF_CHANGE_GROW_TYPE, ClassChangePackets.Result(result.Error), "class_change_rejected", Guard);
            return CommandResult.Handled;
        }
        var character = result.Character!;
        var plan = result.Plan!;
        // ACK502只收尾，不携带新职业；先安装实际Actor及双树，避免按旧分支解释成功结果。
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(character, context.Channel,
            adventureName: context.Session.ReadAdventureName(), premiumPcRoom: context.Session.PremiumPcRoom), "class_change_actor", Guard);
        await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(character.Learning), "class_change_skills", Guard);
        await level.PlayerAsync(context, character, "class_change_player", Guard);
        var changed = new InventoryResult(true, 0, plan.Inventory)
        {
            Receipt = new(character, result.Generation, result.Epoch)
            {
                Phase = result.Phase,
                PreviousQuests = plan.Expected.ActiveMainQuests,
                PreviousSkillSwitchInventory = plan.Expected.SkillSwitchInventory
            }
        };
        await inventory.RefreshAsync(context, changed, suppressMainNotice: true);
        if (!plan.Expected.ActiveMainQuests.SequenceEqual(character.ActiveMainQuests))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(character.ActiveMainQuests), "class_change_quest_progress", Guard);
        await quests.RefreshAsync(context, character, Guard);
        await context.Reply(CmdPacketType.USE_RIGHT_OF_CHANGE_GROW_TYPE, ClassChangePackets.Result(0), "class_change_committed", Guard);
        context.Owner.Execute(() =>
        {
            if (Guard()) town.Refresh(context);
            return true;
        });
        return CommandResult.Handled;
    }
}
