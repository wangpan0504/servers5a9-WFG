using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed class EquipmentCatalog
{
    private readonly PvfArchive pvf;
    private readonly IReadOnlyDictionary<int, string> paths;
    private readonly ConcurrentDictionary<int, Lazy<EquipmentFile>> definitions = new();

    private EquipmentCatalog(PvfArchive pvf, Dictionary<int, string> paths)
    {
        this.pvf = pvf;
        this.paths = new ReadOnlyDictionary<int, string>(paths);
        TemplateIds = Array.AsReadOnly(paths.Keys.Order().ToArray());
    }

    public int TemplateCount => paths.Count;
    public IReadOnlyList<int> TemplateIds { get; }

    public static EquipmentCatalog Load(PvfArchive pvf)
    {
        return new EquipmentCatalog(pvf, PvfItemIndex.Equipment(pvf.ReadTokens("equipment/equipment.lst"),
            pvf.ResolveString, path => pvf.FindFile(path)?.DataType == 1));
    }

    public EquipmentFile Get(int templateId)
    {
        if (!paths.TryGetValue(templateId, out string? path))
            throw new KeyNotFoundException("装备列表没有指定模板ID");
        return definitions.GetOrAdd(templateId, _ => new(() =>
            EquipmentFile.Parse(templateId, path, pvf.ReadTokens(path), pvf.ResolveString))).Value;
    }
}
