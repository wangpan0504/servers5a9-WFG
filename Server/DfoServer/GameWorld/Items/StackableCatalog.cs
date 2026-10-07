using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed class StackableCatalog
{
    private readonly PvfArchive pvf;
    private readonly IReadOnlyDictionary<int, string> paths;
    private readonly ConcurrentDictionary<int, Lazy<StackableFile>> definitions = new();

    private StackableCatalog(PvfArchive pvf, Dictionary<int, string> paths)
    {
        this.pvf = pvf;
        this.paths = new ReadOnlyDictionary<int, string>(paths);
        TemplateIds = Array.AsReadOnly(paths.Keys.Order().ToArray());
    }

    public int TemplateCount => paths.Count;
    public IReadOnlyList<int> TemplateIds { get; }

    public static StackableCatalog Load(PvfArchive pvf)
    {
        Dictionary<int, string> paths = PvfItemIndex.Stackable(pvf.ReadTokens("stackable/stackable.lst"), pvf.ResolveString,
            path => pvf.FindFile(path)?.DataType == 1);
        return new StackableCatalog(pvf, paths);
    }

    public StackableFile Get(int templateId)
    {
        if (!paths.TryGetValue(templateId, out string? path))
            throw new KeyNotFoundException("堆叠物品列表没有指定模板ID");
        return definitions.GetOrAdd(templateId, _ => new(() =>
            StackableFile.Parse(templateId, path, pvf.ReadTokens(path), pvf.ResolveString))).Value;
    }
}
