using DfoServer.GameWorld.Adventure;
using System.Collections.Immutable;

namespace DfoServer.Game.Adventure;

internal sealed class AdventureRepresentativeService(AdventureCatalog catalog)
{
    public AdventureCatalog Catalog => catalog;
    public bool Busy(AdventureSnapshot snapshot, byte job, byte grow)
    {
        ushort? id = snapshot.State.Representatives.SingleOrDefault(row => row.Job == job && row.Grow == grow).CharacterId ??
            snapshot.Representatives(catalog).FirstOrDefault(row => row.Job == job && row.ProfessionGrow == grow)?.Id;
        return id is { } member && snapshot.State.Expeditions.Any(run => run.Members.Contains(member));
    }
    public AdventureRepresentativePlan? Prepare(AdventureSnapshot snapshot, ushort actor, byte job, byte grow, ushort? id)
    {
        if (Busy(snapshot, job, grow) || !catalog.Professions.ContainsKey((job, grow)) || id is { } selected &&
            !snapshot.Members.Any(member => member.Id == selected && member.Job == job && member.ProfessionGrow == grow &&
                catalog.Eligible(member.Job, member.Grow, member.Level))) return null;
        // 转职、等级等GM改写可能留下旧绑定；移除失效选择，恢复原候选规则。
        // 显式空位和有效选择保持不变，远征仍使用原冻结成员，不改动出征事实。
        var rows = snapshot.State.Representatives.Where(row => (row.Job != job || row.Grow != grow) &&
                (row.CharacterId is null || snapshot.Members.Any(member => member.Id == row.CharacterId &&
                    member.Job == row.Job && member.ProfessionGrow == row.Grow && catalog.Eligible(member.Job, member.Grow, member.Level))))
            .Append(new AdventureSelection(job, grow, id)).OrderBy(row => row.Job).ThenBy(row => row.Grow).ToImmutableArray();
        return new(snapshot, rows, actor);
    }
}
