using DfoServer.Network.Management;
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

    // 索引只在每个服务进程里建一次：启动后由 StartWarmup 在后台预热；命中磁盘缓存（按 PVF 大小+修改时间校验）时跳过全量扫描。
    readonly object warmGate = new();
    Task<ImmutableArray<ItemEntry>>? itemsTask;
    Task<ImmutableDictionary<int, GmItemAttributes.Attrs>>? attributesTask;
    public string? PvfPath { get; init; }
    public string? CacheDirectory { get; init; }
    Task<ImmutableArray<ItemEntry>> ItemsTask { get { lock (warmGate) return itemsTask ??= Task.Run(() => Cached("items", () => BuildItems(pvf))); } }
    Task<ImmutableDictionary<int, GmItemAttributes.Attrs>> AttributesTask { get { lock (warmGate) return attributesTask ??= Task.Run(() => Cached("attrs", () => BuildAttributes(pvf))); } }
    public void StartWarmup(bool attributesToo = true)
    {
        _ = ItemsTask; _ = questNames.Value;
        if (attributesToo) _ = AttributesTask;
    }
    public (bool Items, bool Quests, bool Attributes) IndexState
    {
        get { lock (warmGate) return (itemsTask?.IsCompletedSuccessfully == true, questNames.IsValueCreated && questNames.Value.IsCompletedSuccessfully, attributesTask?.IsCompletedSuccessfully == true); }
    }

    sealed record CacheFile<T>(int V, long Size, long Ticks, T Data);
    T Cached<T>(string name, Func<T> build)
    {
        string? path = CacheDirectory is { Length: > 0 } && PvfPath is { Length: > 0 } && File.Exists(PvfPath) ? Path.Combine(CacheDirectory, "gm-index-" + name + ".json") : null;
        if (path is null) return build();
        var info = new FileInfo(PvfPath!);
        try
        {
            if (File.Exists(path) && System.Text.Json.JsonSerializer.Deserialize<CacheFile<T>>(File.ReadAllBytes(path)) is { V: 1 } cached
                && cached.Size == info.Length && cached.Ticks == info.LastWriteTimeUtc.Ticks && cached.Data is not null) return cached.Data;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or NotSupportedException) { }
        var built = build();
        try
        {
            Directory.CreateDirectory(CacheDirectory!);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new CacheFile<T>(1, info.Length, info.LastWriteTimeUtc.Ticks, built)));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return built;
    }
    readonly Lazy<Task<ImmutableDictionary<int, string>>> questNames = new(() => Task.Run(() => BuildQuestNames(pvf, quests)), LazyThreadSafetyMode.ExecutionAndPublication);

    public ItemResources Resources => resources;
    // 服务端属性计算器（只读）；未装配时 stats 命令明确拒绝。
    public DfoServer.Game.Characters.CharacterStatComputer? Stats { get; init; }
    // 等级经验表（exp-table / 按经验换算等级）；未装配时 exp-table 明确拒绝。
    public DfoServer.GameWorld.Characters.CharacterGrowthCatalog? Growth { get; init; }

    // 技能名称：skill/<职业>list.lst 指向的 .skl 的 [name] 段；读不到名称时回退为“技能 ID”。
    readonly Lazy<ImmutableDictionary<(byte Job, ushort Id), string>> skillNames = new(() => BuildSkillNames(pvf), LazyThreadSafetyMode.ExecutionAndPublication);
    public string SkillName(byte job, ushort id) => skillNames.Value.TryGetValue((job, id), out var name) && name.Length > 0 ? name : "技能 " + id;

    static ImmutableDictionary<(byte Job, ushort Id), string> BuildSkillNames(PvfArchive pvf)
    {
        var names = ImmutableDictionary.CreateBuilder<(byte, ushort), string>();
        foreach (int job in DfoServer.GameWorld.Characters.CharacterCatalog.ReadJobList(pvf).Keys.Order())
        {
            if (job >= DfoServer.GameWorld.Characters.SkillLearningCatalog.Lists.Length) continue;
            try
            {
                var tokens = pvf.ReadTokens("skill/" + DfoServer.GameWorld.Characters.SkillLearningCatalog.Lists[job]);
                for (int i = 0; i + 1 < tokens.Length; i += 2)
                {
                    if (tokens[i].Type != 0 || tokens[i + 1].Type != 6 || tokens[i].Value is < 0 or > ushort.MaxValue) continue;
                    try
                    {
                        string path = PvfArchive.NormalizePath("skill/" + pvf.ResolveString(tokens[i + 1].Value));
                        if (path.Contains("..", StringComparison.Ordinal)) continue;
                        names[((byte)job, (ushort)tokens[i].Value)] = PvfScript.Section(pvf.ReadText(path), "name").Trim().Trim('`');
                    }
                    catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException) { }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException) { }
        }
        return names.ToImmutable();
    }
    readonly Lazy<(DfoServer.GameWorld.Quests.TitleCatalog Catalog, DfoServer.Game.Titles.TitleAchievementService Service)> titleParts = new(() =>
    {
        var catalog = new DfoServer.GameWorld.Quests.TitleCatalog(pvf, quests ?? throw new InvalidOperationException("任务资源未装配"));
        return (catalog, new(catalog, quests, resources.Items, resources.GoldLimits, resources.EpicPieces));
    }, LazyThreadSafetyMode.ExecutionAndPublication);
    // 称号簿/成就目录与规划服务：按需从已加载 PVF 读取一次，与游戏服务器使用同一实现。
    public DfoServer.GameWorld.Quests.TitleCatalog Titles => titleParts.Value.Catalog;
    public DfoServer.Game.Titles.TitleAchievementService TitleService => titleParts.Value.Service;
    public QuestCatalog Quests => quests ?? throw new InvalidOperationException("任务资源未装配");
    public static string JobTag(byte job) => job < JobNames.Length ? "[" + JobNames[job] + "]" : "";

    // 高级筛选用的属性索引：仅在第一次高级筛选时才在后台构建（全量扫描物品脚本，约数十秒），未就绪时明确提示稍后重试。
    public ImmutableDictionary<int, GmItemAttributes.Attrs> Attributes => Ready(AttributesTask, "物品属性索引");
    public bool AttributesReady => attributesTask?.IsCompletedSuccessfully == true;
    public void WarmAttributes(TimeSpan timeout)
    {
        if (!AttributesTask.Wait(timeout)) throw new TimeoutException("物品属性索引构建超时");
    }

    static ImmutableDictionary<int, GmItemAttributes.Attrs> BuildAttributes(PvfArchive pvf)
    {
        var map = ImmutableDictionary.CreateBuilder<int, GmItemAttributes.Attrs>();
        foreach (var (equipment, list) in new[] { (true, "equipment/equipment.lst"), (false, "stackable/stackable.lst") })
            foreach (var (id, path) in PvfScript.List(pvf.ReadText(list)))
            {
                try
                {
                    var attrs = GmItemAttributes.Parse(pvf.ReadText((equipment ? "equipment/" : "stackable/") + path));
                    if (attrs.Stats.Length > 0 || attrs.Period > 0 || attrs.SkillJobs.Length > 0 || attrs.AllSkill) map[id] = attrs;
                }
                catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException or FormatException or OverflowException) { }
            }
        return map.ToImmutable();
    }

    public ImmutableArray<ItemEntry> Items => Ready(ItemsTask, "物品索引");
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
        Task[] tasks = [.. itemIndex ? [ItemsTask] : Array.Empty<Task>(), .. questIndex ? [questNames.Value] : Array.Empty<Task>()];
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

    public static IReadOnlyList<string> JobList => JobNames;
    public static bool JobKnown(string tag) => Array.IndexOf(JobNames, tag) >= 0;

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
