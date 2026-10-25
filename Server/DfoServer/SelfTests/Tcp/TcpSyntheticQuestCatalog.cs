using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 合成任务仍是独立测试图；只显式补齐正式副本装配所必需的共享希望定义。
internal static class TcpSyntheticQuestCatalog
{
    public static QuestCatalog WithHopeMissions(QuestCatalog synthetic, QuestCatalog shared)
    {
        var dependencies = shared.All.Where(row => row.Grade == "[op hope mission]").OrderBy(row => row.Id).ToArray();
        if (!dependencies.Select(row => row.Id).SequenceEqual(Enumerable.Range(6171,9)) ||
            dependencies.Any(row => !OperationHopeCatalog.SupportsMission(row) || row.Definition!.PrerequisiteGroups.Length != 0))
            throw new InvalidDataException("合成TCP目录要求共享原九条无前置希望任务，不补造资源");
        var combined = synthetic.All.ToDictionary(row => row.Id);
        foreach (var quest in dependencies)
            if (!combined.TryAdd(quest.Id,quest))
                throw new InvalidDataException("合成TCP任务编号与共享希望依赖冲突，不能覆盖任意一方");
        var result = new QuestCatalog(combined);
        var before = QuestStageGraph.Build(synthetic);
        var after = QuestStageGraph.Build(result);
        foreach (var quest in synthetic.All)
        {
            That(ReferenceEquals(result.Get(quest.Id),quest),"补副本依赖保留原合成任务对象");
            That(before.Successors(quest.Id).SequenceEqual(after.Successors(quest.Id)),"补希望依赖不改变任何原合成阶段边");
        }
        foreach (var quest in dependencies)
            That(ReferenceEquals(result.Get(quest.Id),quest) && after.Successors(quest.Id).IsEmpty,
                "希望依赖复用共享真实对象，不引入任务阶段后继");
        That(result.All.Count() == synthetic.All.Count()+9,"合成目录只追加九条必需副本定义，不注入其它真实史诗链");
        return result;
    }
}
