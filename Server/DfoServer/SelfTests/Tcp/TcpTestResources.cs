using DfoServer.Infrastructure;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using PvfLib;

namespace DfoServer.Testing;

// 一轮回归只持有一份只读资源；纯命令/网络用例不触发驱动进程的 PVF 加载。
internal sealed class TcpTestResources : IDisposable
{
    readonly Lazy<PvfArchive> archive;
    readonly Lazy<CharacterCatalog> characters;
    readonly Lazy<ItemResources> items;
    readonly Lazy<TownCatalog> towns;
    readonly Lazy<SkillLearningCatalog> learning;
    readonly Lazy<DfoServer.GameWorld.Quests.QuestCatalog> quests;
    readonly Lazy<DfoServer.GameWorld.Adventure.AdventureCatalog> adventure;
    readonly Lazy<DfoServer.GameWorld.Adventure.AdventureExpeditionCatalog> expeditions;
    readonly Lazy<TutorialCatalog> tutorials;
    readonly Lazy<TutorialScenes> tutorialScenes;
    public PvfArchive Archive => archive.Value;
    public ItemResources ItemResources => items.Value;
    public CharacterCatalog Characters => characters.Value;
    public TownCatalog Towns => towns.Value;
    public SkillLearningCatalog Learning => learning.Value;
    public DfoServer.GameWorld.Quests.QuestCatalog Quests => quests.Value;
    public DfoServer.GameWorld.Adventure.AdventureCatalog Adventure => adventure.Value;
    public DfoServer.GameWorld.Adventure.AdventureExpeditionCatalog Expeditions => expeditions.Value;
    public TutorialCatalog Tutorials => tutorials.Value;
    public TutorialScenes TutorialScenes => tutorialScenes.Value;
    public bool IsLoaded => archive.IsValueCreated;
    // 共享资源（PVF 及各目录）首次构建的累计耗时；计入触发加载的那一组。
    public long LoadMs => Interlocked.Read(ref loadMs);
    long loadMs;

    public TcpTestResources(ServerOptions options)
    {
        archive = new(() => Timed(() =>
        {
            var pvf = PvfArchive.Open(options.PvfPath);

            Console.WriteLine("TCP 共享 PVF 已加载");
            return pvf;
        }));
        items = new(() => Timed(() => new ItemResources(Archive)));
        characters = new(() => Timed(() => CharacterCatalog.Load(Archive, ItemResources.Items)));
        towns = new(() => Timed(() => TownCatalog.Load(Archive)));
        learning = new(() => Timed(() => SkillLearningCatalog.Load(Archive, ItemResources.Contracts.ExpertSkillLevels)));
        quests = new(() => Timed(() => SelfTestCatalogs.Quests(Archive)));
        adventure = new(() => Timed(() => new DfoServer.GameWorld.Adventure.AdventureCatalog(Archive)));
        expeditions = new(() => Timed(() => new DfoServer.GameWorld.Adventure.AdventureExpeditionCatalog(Archive)));
        tutorials = new(() => Timed(() => new TutorialCatalog(Archive)));
        tutorialScenes = new(() => Timed(() => new TutorialScenes(Archive, Tutorials, SelfTestCatalogs.Dungeons(Archive))));
    }

    // 嵌套加载（目录依赖 PVF）只计外层，避免重复累加。
    [ThreadStatic] static int depth;
    T Timed<T>(Func<T> create)
    {
        var watch = depth++ == 0 ? System.Diagnostics.Stopwatch.StartNew() : null;
        try
        {
            return create();
        }
        finally
        {
            depth--;
            if (watch is not null)
                Interlocked.Add(ref loadMs, watch.ElapsedMilliseconds);
        }
    }

        public void Dispose()
    {
        if (archive.IsValueCreated)
            archive.Value.Dispose();
    }
}
