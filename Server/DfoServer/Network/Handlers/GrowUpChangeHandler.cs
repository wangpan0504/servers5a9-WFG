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

// 技能窗50级前职业变更 CMD785 RE_GROWUP_CHANGE（与502转职券、ACK34任务转职分开）。
internal sealed class GrowUpChangeHandler(GrowUpChangeService service, CharacterLevelProjection level,
    InventoryProjection inventory, QuestProjection quests, TownProjection town, ServerLog log, TimeProvider? clock = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.RE_GROWUP_CHANGE, Handle, nameof(GrowUpChangeHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var request = GrowUpChangeRequests.Read(packet.Body);
        var result = context.Activity?.BlocksTownActions == true ? context.Owner.Execute(() =>
            new GrowUpChangeResult(30, "active-town-operation", context.Session.Selected, context.Owner.Generation,
                context.Session.SelectionEpoch, context.Session.Phase, 0)) :
            context.Session.ChangeGrowUp(service, request, (clock ?? TimeProvider.System).GetUtcNow());
        bool Guard() => context.Session.CanPublishGrowUpChange(result);
        log.Write("growup_change", new { Connection = context.Id, request.Target, request.Confirmed, Checksum = request.ChecksumMatches,
            Accepted = result.Error == 0, result.Error, result.Reason, result.Quoted, result.Count, Fee = result.Plan?.Fee,
            Character = result.Character?.Id, PreviousGrow = result.Plan?.Expected.GrowType, Grow = result.Character?.GrowType,
            result.Character?.Revision });
        if (result.Error != 0)
        {
            await context.Reply(CmdPacketType.RE_GROWUP_CHANGE, GrowUpChangePackets.Failure(result.Error), "growup_change_rejected", Guard);
            return CommandResult.Handled;
        }
        if (result.Quoted)
        {
            await context.Reply(CmdPacketType.RE_GROWUP_CHANGE, GrowUpChangePackets.Quote(result.Count), "growup_change_quote", Guard);
            return CommandResult.Handled;
        }
        var character = result.Character!;
        var plan = result.Plan!;
        // ACK785 result0 的 012999A60 按本地当前职业重建穿戴/技能显示，所以先安装真实Actor、两树与Player。
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(character, context.Session.ReadPvpGrade(character), context.Channel,
            adventureName: context.Session.ReadAdventureName(), premiumPcRoom: context.Session.PremiumPcRoom), "growup_change_actor", Guard);
        await SkillProjection.PublishAsync(context, character, "growup_change_skills", "growup_change_combo", Guard);
        await level.PlayerAsync(context, character, "growup_change_player", Guard);
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
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(character.ActiveMainQuests), "growup_change_quest_progress", Guard);
        await quests.RefreshAsync(context, character, Guard);
        await context.Reply(CmdPacketType.RE_GROWUP_CHANGE, GrowUpChangePackets.Committed(result.Count), "growup_change_committed", Guard);
        context.Owner.Execute(() =>
        {
            if (Guard()) town.Refresh(context);
            return true;
        });
        return CommandResult.Handled;
    }
}
