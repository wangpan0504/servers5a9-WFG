using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class LevelUpTicketProjection(InventoryProjection inventory, CharacterLevelProjection levels)
{
    public static CmdPacketType Opcode(LevelUpConsumableKind kind) => kind switch
    {
        LevelUpConsumableKind.Ticket => CmdPacketType.REQUEST_EVENT_SERVER_LEVEL_UP,
        LevelUpConsumableKind.StatusTable => CmdPacketType.INCREASE_STATUS,
        LevelUpConsumableKind.JumpingTable => CmdPacketType.USE_STACKABLE_ACTION,
        _ => throw new InvalidDataException("未知升级道具发布入口")
    };

    public async Task PublishAsync(GameConnectionContext context, QuestQuickClearReceipt receipt)
    {
        var commit = receipt.Commit ?? throw new InvalidOperationException("升级券缺少提交结果");
        var plan = commit.Plan;
        var source = plan.LevelUpTicket ?? throw new InvalidOperationException("升级券缺少原实例");
        var character = commit.Character;
        bool Guard() => context.Session.CanPublishQuestQuickClear(receipt);
        if (!receipt.TicketAcknowledged)
        {
            await context.Reply(Opcode(source.Kind), LevelUpTicketPackets.Used(source), "level_up_ticket_used", Guard);
            context.Session.MarkQuestTicketAcknowledged(receipt);
        }
        var inventoryResult = new InventoryResult(true,0,plan.Inventory)
        {
            Receipt=new(character,receipt.Generation,receipt.SelectionEpoch)
                { Phase=receipt.Phase,DungeonIdentity=receipt.Identity }
        };
        await inventory.RefreshAsync(context, inventoryResult, new HashSet<InventorySlot> { source.Item.Position }, refreshEquipment:false);
        var state = character.DungeonState ?? throw new InvalidOperationException("升级券缺少成长附属状态");
        var auxiliary = new CharacterGrowthAuxiliaryState(checked((uint)character.Inventory.WinPoints),state.GrowthCapsuleExperience,
            state.ProficiencyLevel,state.ProficiencyExperience);
        await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(character.Level,character.Learning,auxiliary,
            CharacterExperienceDisplay.Ordinary), "level_up_ticket_growth", Guard);
        if (SkillPackets.BindingsChanged(plan.Expected.Learning,character.Learning))
            await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(character.Learning), "level_up_ticket_skills", Guard);
        await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(character.ActiveMainQuests),"level_up_ticket_active",Guard);
        await levels.RefreshAsync(context,character,plan.Expected.Level,"level_up_ticket_level",Guard,
            previousGrowType:plan.Expected.GrowType,restoreCompletedQuests:false);
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST,TownPackets.CompletedQuests(character),"level_up_ticket_completed",Guard);
    }
}
