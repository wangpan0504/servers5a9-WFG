using PvfLib;

namespace DfoServer.GameWorld.Items;

// 领域槽位只标识 CHR 声明，不提供客户端 wire 数值映射。
internal enum StarterEquipmentSlot
{
    Weapon,
    Coat,
    Pants,
    Waist,
    Shoes
}
internal sealed record StarterEquipmentDeclaration(StarterEquipmentSlot Slot, int TemplateId);
internal sealed record EquipmentDefinition(int TemplateId, string ScriptPath, int MinimumLevel, int MaximumDurability);

// 只加载普通创建引用的资源，不生成实例、持有数量或当前耐久。
internal sealed class StarterEquipmentCatalog
{
    readonly Dictionary<byte, IReadOnlyList<StarterEquipmentDeclaration>> starters;
    readonly Dictionary<int, EquipmentDefinition> equipment;

    StarterEquipmentCatalog(Dictionary<byte, IReadOnlyList<StarterEquipmentDeclaration>> starters, Dictionary<int, EquipmentDefinition> equipment, EquipmentCatalog resources)
    {
        this.starters = starters;
        this.equipment = equipment;
        Resources = resources;
    }

    public EquipmentCatalog Resources { get; }

    public IReadOnlyList<StarterEquipmentDeclaration> GetStarterEquipment(byte job) => starters.GetValueOrDefault(job)
        ?? throw new InvalidDataException("普通创建职业未实现");

    public EquipmentDefinition GetEquipment(int templateId) => equipment.GetValueOrDefault(templateId)
        ?? throw new InvalidDataException("模板不在普通创建资源中");

    internal static IReadOnlyList<StarterEquipmentDeclaration> ParseStarterEquipment(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        int start = -1;
        int end = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Type != 3)
                continue;
            string tag = resolve(tokens[i].Value);
            if (tag == "[create equipment list]")
            {
                if (start >= 0)
                    throw new InvalidDataException("重复普通创建装备块");
                start = i;
            }
            if (tag == "[/create equipment list]")
            {
                if (end >= 0)
                    throw new InvalidDataException("重复普通创建装备结束标记");
                end = i;
            }
        }
        if (start < 0 || end != start + 11)
            throw new InvalidDataException("普通创建装备声明缺失或布局变化");
        var items = new List<StarterEquipmentDeclaration>();
        for (int i = start + 1; i < end; i += 2)
        {
            if (tokens[i].Type != 6 || tokens[i + 1].Type != 0 || tokens[i + 1].Value <= 0)
                throw new InvalidDataException("普通创建装备声明类型或模板 ID 非法");
            StarterEquipmentSlot slot = resolve(tokens[i].Value) switch
            {
                "[weapon]" => StarterEquipmentSlot.Weapon,
                "[coat]" => StarterEquipmentSlot.Coat,
                "[pants]" => StarterEquipmentSlot.Pants,
                "[waist]" => StarterEquipmentSlot.Waist,
                "[shoes]" => StarterEquipmentSlot.Shoes,
                _ => throw new InvalidDataException("未知普通创建领域槽位")
            };
            items.Add(new StarterEquipmentDeclaration(slot, tokens[i + 1].Value));
        }
        if (items.Select(item => item.Slot).Distinct().Count() != 5)
            throw new InvalidDataException("普通创建领域槽位重复");
        return items.AsReadOnly();
    }

    public static StarterEquipmentCatalog Load(PvfArchive pvf)
    {

        var jobs = ReadList("character/character.lst", "character/");
        if (!jobs.Keys.Order().SequenceEqual(Enumerable.Range(0, 16)))
            throw new InvalidDataException("普通创建职业列表变化");
        var resources = EquipmentCatalog.Load(pvf);
        var starters = new Dictionary<byte, IReadOnlyList<StarterEquipmentDeclaration>>();
        var definitions = new Dictionary<int, EquipmentDefinition>();
        foreach (var job in jobs)
        {
            PvfToken[] tokens = pvf.ReadTokens(job.Value);
            var items = ParseStarterEquipment(tokens, pvf.ResolveString);
            foreach (var item in items)
            {
                int id = item.TemplateId;
                if (!definitions.ContainsKey(id))
                {
                    EquipmentFile resource = resources.Get(id);
                    if (resource.DeclaredMinimumLevel is not int minimumLevel || resource.DeclaredMaximumDurability is not int maximumDurability)
                        throw new InvalidDataException("普通创建装备等级或耐久声明缺失或非法");
                    definitions.Add(id, new EquipmentDefinition(id, resource.ScriptPath, minimumLevel, maximumDurability));
                }
            }
            starters.Add(checked((byte)job.Key), items);
        }
        return new StarterEquipmentCatalog(starters, definitions, resources);

        Dictionary<int, string> ReadList(string path, string prefix)
        {
            PvfToken[] tokens = pvf.ReadTokens(path);
            if (tokens.Length == 0 || tokens.Length % 2 != 0)
                throw new InvalidDataException("资源列表长度变化");
            var entries = new Dictionary<int, string>();
            for (int i = 0; i < tokens.Length; i += 2)
            {
                if (tokens[i].Type != 0 || tokens[i + 1].Type != 6)
                    throw new InvalidDataException("资源列表类型变化");
                string relative = pvf.ResolveString(tokens[i + 1].Value);
                if (tokens[i].Value < 0 || string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.Contains(':') || relative.Replace('\\', '/').Split('/').Any(part => part is "." or ".."))
                    throw new InvalidDataException("资源列表 ID 或路径非法");
                string resolved = PvfArchive.NormalizePath(prefix + relative);
                if (!resolved.StartsWith(prefix, StringComparison.Ordinal) || !pvf.Contains(resolved) || !entries.TryAdd(tokens[i].Value, resolved))
                    throw new InvalidDataException("资源列表重复 ID、路径越界或资源缺失");
            }
            return entries;
        }
    }
}
