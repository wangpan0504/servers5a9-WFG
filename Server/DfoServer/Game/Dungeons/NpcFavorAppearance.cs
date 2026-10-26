using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record NpcFavorAppearance(int NpcId, int NameColor, uint Colors);
internal sealed record NpcFavorDungeonCapture(Character Character, long Generation, long Epoch,
    DungeonEntryPlan Entry, DungeonRunSnapshot Snapshot, PartyDungeonInstance? Party, byte Slot = 0);

// 客户端负责抽选；服务端只验证原实例及已保存资格，不补抽或重新决定概率。
internal sealed class NpcFavorAppearanceService(NpcFavorCatalog npcs)
{
    public bool Validate(NpcFavorDungeonCapture context, NpcFavorAppearance appearance)
    {
        if (appearance.NpcId == 0) return appearance.Colors == 0 && appearance.NameColor == unchecked((int)0xFF5AD6F7);
        if (appearance.NpcId < 0 || npcs.Rules is not { } rules || context.Character.Level < rules.MinimumLevel) return false;
        var completed = QuestEligibility.Completed(context.Character);
        foreach (var row in context.Character.Inventory.NpcFavor)
        {
            var target = npcs.Get(row.NpcId);
            if (target is null || row.Points < target.Intimate || target.DungeonExceptions.Contains(context.Entry.DungeonId) ||
                target.Appearance(completed) != appearance.NpcId) continue;
            return appearance.Colors == row.Colors && appearance.NameColor == rules.NameColor(row.Points, target.Maximum);
        }
        return false;
    }
}
