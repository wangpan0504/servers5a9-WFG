using System.Data.Common;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class GuildDungeonScrollHandler(GuildDungeonScrollService service, InventoryProjection inventory, ServerLog log)
{
    public async Task<CommandResult> UseAsync(GameConnectionContext context, StackableActionUseRequest request, ulong requestSerial)
    {
        bool valid = request.Action == GameWorld.Guilds.GuildDungeonClearScrollDefinition.Action && request.Reserved == 0 &&
            request.FirstArgument == 0 && request.SecondArgument == 0 && request.List == (byte)InventoryListType.Main && request.Slot <= short.MaxValue;
        Game.Session.GuildDungeonScrollUseResult use;
        try
        {
            use = context.Session.UseGuildDungeonScroll(new(InventoryListType.Main, unchecked((short)request.Slot)), service,
                context.Session.GuildRequestIdentity(requestSerial), !valid || context.Activity?.BlocksTownActions == true);
        }
        catch (DbException error)
        {
            log.Write("guild_dungeon_scroll_commit_failed", new { Connection = context.Id, Error = error.Message });
            return CommandResult.CloseConnection;
        }
        if (use.CloseConnection) return CommandResult.CloseConnection;
        if (use.Commit.Replayed) return CommandResult.Handled;
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            use.Commit.Success ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request, use.Commit.Error),
            "guild_dungeon_scroll", use.IsCurrent);
        if (use.Inventory is { } result)
        {
            await inventory.RefreshAsync(context, result, new HashSet<InventorySlot> { new(InventoryListType.Main, (short)request.Slot) },
                refreshEquipment: false);
            service.Publish(use.Commit);
        }
        log.Write("guild_dungeon_scroll", new { Connection = context.Id, use.Commit.Error, use.Commit.Replayed,
            Guild = use.Commit.State?.Guild.Id, Dungeon = use.Commit.Progress?.Counts.FirstOrDefault()?.DungeonId });
        return CommandResult.Handled;
    }
}
