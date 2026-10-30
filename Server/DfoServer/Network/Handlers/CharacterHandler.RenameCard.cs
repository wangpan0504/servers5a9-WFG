using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class CharacterHandler
{
    async Task<CommandResult> UseRenameCardAsync(GameConnectionContext context, Packet request)
    {
        ushort slot = CharacterRenameRequests.Use(request.Body);
        if (context.Creatures is { } creatures) await creatures.FlushAsync();
        InventoryCommitReceipt? receipt = null;
        try
        {
            if (renameItems is not null)
                receipt = context.Session.UseCharacterRenameCard(slot, renameItems, time ?? TimeProvider.System, context.BlocksTownActions);
        }
        catch (InventoryQualificationExpiredException) { }
        log.Write("character_rename_card", new { Connection = context.Id, Character = receipt?.Character.Id, Slot = slot, Accepted = receipt is not null });
        if (receipt is null)
        {
            await context.Reply(CmdPacketType.USE_RENAME_CARD, [0, 19, 0], "character_rename_card_rejected");
            return CommandResult.Handled;
        }
        bool Guard() => context.Session.CanPublishInventory(receipt);
        await context.Reply(CmdPacketType.USE_RENAME_CARD, [1], "character_rename_card_accepted", Guard);
        // 成功后使用既有完整小退流程移除同屏与组队占用，并恢复带资格的名册。
        await projection.ReturnToSelectionAsync(context, Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> RenameCharacterAsync(GameConnectionContext context, Packet request)
    {
        var input = CharacterRenameRequests.Complete(request.Body);
        var result = context.Session.CompleteCharacterRename(input.Index, input.OldName, input.NewName, input.Reason);
        ushort error = result switch { CharacterRenameFailure.InvalidName => 159, CharacterRenameFailure.Duplicate => 24, _ => 0 };
        log.Write("character_rename", new { Connection = context.Id, Index = input.Index, Result = result.ToString() });
        var roster = result == CharacterRenameFailure.None ? context.Session.PrepareRoster() : null;
        bool Guard() => roster is null || context.Session.CanPublishRoster(roster);
        await context.Reply(CmdPacketType.CHANGE_CHARAC_NAME,
            result == CharacterRenameFailure.None ? [1] : [0, (byte)error, (byte)(error >> 8)], "character_rename_ack", Guard);
        if (roster is not null)
        {
            await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(roster.Entries.ToArray(), roster.Capacity),
                "character_rename_roster", Guard);
            context.Session.MarkRosterPublished(roster);
        }
        return CommandResult.Handled;
    }
}
