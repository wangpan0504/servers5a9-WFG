using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

// CMD25 COMPOUND_ITEM：[recipe] 设计图把配方指定的装备改造成产物。
// 请求只给设计图所在的 Main 槽位（或物品编号），被改造装备与材料全部来自 PVF。
// 成功回包是 4 字节 `01 00 00 00`；结果窗口的行列表无法由回包填充，见 CompoundPackets 的说明。
internal sealed class CompoundHandler(InventoryRecipeCraftService crafts, InventoryProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
        => commands.Register(CmdPacketType.COMPOUND_ITEM, Compound, nameof(CompoundHandler));

    async Task<CommandResult> Compound(GameConnectionContext context, Packet packet)
    {
        var request = CompoundRequests.Read(packet.Body);
        // 只有按槽位取设计图这一种是本次实现的制造；按物品编号的入口是 NPC 商品菜单，不在本模块范围。
        var position = new InventorySlot(InventoryListType.Main, checked((short)request.Raw));
        RecipeCraftReceipt? receipt = null;
        bool supported = request.Mode == CompoundRequests.SourceIsSlot && request.Raw is > 0 and <= 32767 &&
            request.Count == 1;
        var result = !supported || context.BlocksTownActions ? InventoryResult.Reject(InventoryRecipeCraftService.Rejection) :
            context.Session.CraftRecipe(position, crafts, out receipt);
        var body = CompoundPackets.Result(result);
        log.Write("compound_item", new
        {
            Connection = context.Id, request.Raw, request.Mode, request.Count,
            Body = Convert.ToHexString(packet.Body), result.Accepted, result.Error, Reward = receipt,
            Reply = Convert.ToHexString(body)
        });
        await context.Reply(CmdPacketType.COMPOUND_ITEM, body,
            result.Accepted ? "compound_item" : "compound_item_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
