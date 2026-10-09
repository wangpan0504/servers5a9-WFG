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
    public PvfArchive Archive => archive.Value;
    public ItemResources ItemResources => items.Value;
    public CharacterCatalog Characters => characters.Value;
    public TownCatalog Towns => towns.Value;
    public SkillLearningCatalog Learning => learning.Value;
    public DfoServer.GameWorld.Quests.QuestCatalog Quests => quests.Value;
    public bool IsLoaded => archive.IsValueCreated;

    public TcpTestResources(ServerOptions options)
    {
        archive = new(() =>
        {
            var pvf = PvfArchive.Open(options.PvfPath);

            Console.WriteLine("TCP 共享 PVF 已加载");
            return pvf;
        });
        items = new(() => new ItemResources(Archive));
        characters = new(() => CharacterCatalog.Load(Archive, ItemResources.Items));
        towns = new(() => TownCatalog.Load(Archive));
        learning = new(() => SkillLearningCatalog.Load(Archive));
        quests = new(() => DfoServer.GameWorld.Quests.QuestCatalog.Load(Archive));
    }

    public void Dispose()
    {
        if (archive.IsValueCreated)
            archive.Value.Dispose();
    }
}
