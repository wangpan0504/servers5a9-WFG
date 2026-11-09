using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 归本次角色正式Run所有；过房、死亡复活及原场景重返复用同一对象，新入场才重新创建。
internal sealed class DungeonPotionUse(StackableDungeonLimitCatalog catalog, int maximum)
{
    internal int Remaining { get; private set; } = maximum;
    internal bool CanUse(int item) => !catalog.Potions.Contains(item) || Remaining > 0;
    internal void Commit(int item)
    {
        if (!catalog.Potions.Contains(item)) return;
        if (Remaining <= 0) throw new InvalidOperationException("药品使用未通过本次地下城剩余额度检查");
        Remaining--;
    }
}
