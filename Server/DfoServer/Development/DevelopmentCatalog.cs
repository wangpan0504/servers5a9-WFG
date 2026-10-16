using System.Collections.Immutable;
using System.Text.RegularExpressions;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.Infrastructure;
using PvfLib;

namespace DfoServer.Development;

// 开发GM专用的只读资源目录。搜索索引在第一次查询时于后台构建一次，只保存检索所需的少量字段；
// 构建期间查询返回进度，不阻塞GM端口的5秒读写窗口，也不进入游戏请求路径。
internal sealed class DevelopmentCatalog(PvfArchive pvf, ItemResources resources, QuestCatalog? quests)
{
    internal sealed record ItemEntry(int Id, string Name, bool Equipment, string Tag, int MinLevel, int Rarity, int SetIndex, ushort Jobs);

    static readonly string[] JobNames = ["swordman", "fighter", "gunner", "mage", "priest", "at gunner", "thief", "at fighter",
        "at mage", "demonic swordman", "creator mage", "at swordman", "knight", "demonic lancer", "at priest", "gun blader"];

    readonly Lazy<Task<ImmutableArray<ItemEntry>>> items = new(() => Task.Run(() => BuildItems(pvf)), LazyThreadSafetyMode.ExecutionAndPublication);
    readonly Lazy<Task<ImmutableDictionary<int, string>>> questNames = new(() => Task.Run(() => BuildQuestNames(pvf, quests)), LazyThreadSafetyMode.ExecutionAndPublication);

    public ItemResources Resources => resources;
    public QuestCatalog Quests => quests ?? throw new InvalidOperationException("任务资源未装配");
    public static string JobTag(byte job) => job < JobNames.Length ? "[" + JobNames[job] + "]" : "";

    public ImmutableArray<ItemEntry> Items => Ready(items.Value, "物品索引");
    public ImmutableDictionary<int, string> QuestNames => Ready(questNames.Value, "任务名称索引");

    static T Ready<T>(Task<T> task, string label)
    {
        if (task.IsCompletedSuccessfully)
            return task.Result;
        if (task.IsFaulted)
            throw new InvalidOperationException(label + "构建失败：" + task.Exception?.GetBaseException().Message);
        throw new InvalidOperationException(label + "正在后台构建，请几秒后重试");
    }

    // 测试与预热入口：等待构建完成；业务查询不使用。
    public void Warm(bool itemIndex, bool questIndex, TimeSpan timeout)
    {
        Task[] tasks = [.. itemIndex ? [items.Value] : Array.Empty<Task>(), .. questIndex ? [questNames.Value] : Array.Empty<Task>()];
        if (!Task.WaitAll(tasks, timeout))
            throw new TimeoutException("开发GM索引构建超时");
    }

    public string QuestName(int id) => questNames.Value.IsCompletedSuccessfully && questNames.Value.Result.TryGetValue(id, out var name) ? name : "任务 " + id;

    static ImmutableArray<ItemEntry> BuildItems(PvfArchive pvf)
    {
        var rows = ImmutableArray.CreateBuilder<ItemEntry>();
        foreach (var (equipment, list) in new[] { (true, "equipment/equipment.lst"), (false, "stackable/stackable.lst") })
            foreach (var (id, path) in PvfScript.List(pvf.ReadText(list)))
            {
                try
                {
                    var doc = equipment ? EquipmentFile.ReadScript(pvf.ReadText, "equipment/" + path) : new PvfDocument(pvf.ReadText("stackable/" + path));
                    string name = doc.Section("name").Trim('`', ' ', '\r', '\n');
                    string tag = Regex.Match(doc.Section(equipment ? "equipment type" : "stackable type"), @"\[([^\]]+)\]").Groups[1].Value.ToLowerInvariant();
                    int level = doc.TryGetInt32("minimum level", out int minimum) ? minimum : 0;
                    int rarity = doc.TryGetInt32("rarity", out int value) ? value : 0;
                    int set = PvfScript.Integers(doc.Section("part set index")).FirstOrDefault();
                    rows.Add(new(id, name, equipment, tag, level, rarity, set, Jobs(doc.Section("usable job"))));
                }
                catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException or FormatException or OverflowException)
                {
                    // 单个资源无法解析时不进入索引；发放仍以 ItemCatalog 的完整解析为准。
                }
            }
        return rows.ToImmutable();
    }

    static ushort Jobs(string text)
    {
        if (text.Length == 0 || text.Contains("[all]", StringComparison.OrdinalIgnoreCase))
            return ushort.MaxValue;
        ushort mask = 0;
        foreach (Match tag in Regex.Matches(text, @"\[([^\]]+)\]"))
        {
            int job = Array.IndexOf(JobNames, tag.Groups[1].Value.ToLowerInvariant());
            if (job >= 0) mask |= (ushort)(1 << job);
        }
        return mask;
    }

    static ImmutableDictionary<int, string> BuildQuestNames(PvfArchive pvf, QuestCatalog? quests)
    {
        var names = ImmutableDictionary.CreateBuilder<int, string>();
        foreach (var quest in quests?.All ?? [])
        {
            try { names[quest.Id] = PvfScript.Section(pvf.ReadText(quest.Path), "name").Trim().Trim('`'); }
            catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException) { }
        }
        return names.ToImmutable();
    }
}
