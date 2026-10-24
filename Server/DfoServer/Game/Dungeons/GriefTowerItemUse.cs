using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 所有权归单次正式实例；只能在物品使用实际提交后推进，拒绝和规划不得占用次数。
internal sealed class GriefTowerItemUse(GriefTowerCatalog catalog)
{
    readonly Dictionary<int, int> used = new();

    public bool CanUse(int itemId) => catalog.ItemUseLimits.TryGetValue(itemId, out int limit) &&
        (limit < 0 || used.GetValueOrDefault(itemId) < limit);

    public void CommitUse(int itemId)
    {
        if (!CanUse(itemId)) throw new InvalidOperationException("悲叹物品不允许使用或本次挑战已达上限");
        // 原客户端负数上限始终放行；服务端无需为不限次物品累积可溢出的计数。
        if (catalog.ItemUseLimits[itemId] >= 0) used[itemId] = used.GetValueOrDefault(itemId) + 1;
    }
}
