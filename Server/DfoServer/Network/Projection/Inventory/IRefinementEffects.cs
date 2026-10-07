using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

// 为属性派生状态和全服通知保留提交后接入点；当前Runtime未注入实现。
// 只使用本次已提交快照，不再扣费/改资产；异步发布遵守context.Owner及发送泵的generation检查。
internal interface IRefinementEffects
{
    Task PublishAsync(GameConnectionContext context, Character committed, RefinementReceipt receipt);
}
