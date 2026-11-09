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
internal sealed partial class DevelopmentCatalog(PvfArchive pvf, ItemResources resources, QuestCatalog? quests)
{
    // Grows：空脚本为全部分支；[usable first growtype] 写成 0–5 的位。无法识别的非空声明为 0，选了角色时不匹配任何分支。
    // Sub：武器子类，取自 PVF 路径中 weapon/ 之后的目录名（如 katana、beamsword）；非武器为空。
    internal sealed record ItemEntry(int Id, string Name, bool Equipment, string Tag, int MinLevel, int Rarity, int SetIndex, ushort Jobs, ushort Grows, string Sub = "")
    {
        // PVF 里没有填名字或只写了 name_数字 的占位模板：游戏内不可正常显示，GM 列表可按需隐藏。
        public bool Placeholder => IsPlaceholderName(Name);
    }

    internal static bool IsPlaceholderName(string? name)
    {
        string value = (name ?? "").Trim();
        if (value.Length == 0) return true;
        return value.Length > 5 && value.StartsWith("name_", StringComparison.OrdinalIgnoreCase) && value[5..].All(char.IsAsciiDigit);
    }

    static readonly Regex WeaponDirectory = new("/weapon/([^/]+)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static string WeaponSub(string tag, string path) =>
        tag == "weapon" && WeaponDirectory.Match("equipment/" + path.Replace('\\', '/')) is { Success: true } match ? match.Groups[1].Value.ToLowerInvariant() : "";

    static readonly string[] JobNames = ["swordman", "fighter", "gunner", "mage", "priest", "at gunner", "thief", "at fighter",
        "at mage", "demonic swordman", "creator mage", "at swordman", "knight", "demonic lancer", "at priest", "gun blader"];

    // 索引只在每个服务进程里建一次：启动后由 StartWarmup 在后台预热；命中磁盘缓存（按 PVF 大小+修改时间校验）时跳过全量扫描。
    readonly object warmGate = new();
    readonly CancellationTokenSource warmupStop = new();
    Task<ImmutableArray<ItemEntry>>? itemsTask;
    Task<ImmutableDictionary<int, GmItemAttributes.Attrs>>? attributesTask;
    Task<ImmutableDictionary<int, string>>? questNamesTask;
    Task? stoppingTask;
    bool stopping;
    public string? PvfPath { get; init; }
    public string? CacheDirectory { get; init; }
    Task<ImmutableArray<ItemEntry>> ItemsTask => Start(ref itemsTask, () => Cached("items", () => BuildItems(pvf, warmupStop.Token), 3));
    Task<ImmutableDictionary<int, GmItemAttributes.Attrs>> AttributesTask => Start(ref attributesTask, () => Cached("attrs", () => BuildAttributes(pvf, warmupStop.Token)));
    Task<ImmutableDictionary<int, string>> QuestNamesTask => Start(ref questNamesTask, () => BuildQuestNames(pvf, quests, warmupStop.Token));

    Task<T> Start<T>(ref Task<T>? task, Func<T> build)
    {
        lock (warmGate)
        {
            if (task is not null) return task;
            ObjectDisposedException.ThrowIf(stopping, this);
            return task = Task.Run(build, warmupStop.Token);
        }
    }
    public void StartWarmup(bool attributesToo = true)
    {
        _ = ItemsTask; _ = QuestNamesTask;
        if (attributesToo) _ = AttributesTask;
    }
    public (bool Items, bool Quests, bool Attributes) IndexState
    {
        get { lock (warmGate) return (itemsTask?.IsCompletedSuccessfully == true, questNamesTask?.IsCompletedSuccessfully == true, attributesTask?.IsCompletedSuccessfully == true); }
    }

    // 停止后不再创建索引任务；所有调用者等待同一个退出屏障，再释放借用的PVF。
    public Task StopWarmupAsync()
    {
        lock (warmGate)
        {
            if (stoppingTask is not null) return stoppingTask;
            stopping = true;
            Task[] tasks = [.. itemsTask is null ? [] : new Task[] { itemsTask },
                .. attributesTask is null ? [] : new Task[] { attributesTask },
                .. questNamesTask is null ? [] : new Task[] { questNamesTask }];
            return stoppingTask = FinishAsync(tasks);
        }

        async Task FinishAsync(Task[] tasks)
        {
            warmupStop.Cancel();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) when (warmupStop.IsCancellationRequested) { }
            finally { warmupStop.Dispose(); }
        }
    }

    sealed record CacheFile<T>(int V, long Size, long Ticks, T Data);
    T Cached<T>(string name, Func<T> build, int version = 1)
    {
        var stop = warmupStop.Token;
        stop.ThrowIfCancellationRequested();
        string? path = CacheDirectory is { Length: > 0 } && PvfPath is { Length: > 0 } && File.Exists(PvfPath) ? Path.Combine(CacheDirectory, "gm-index-" + name + ".json") : null;
        if (path is null) return build();
        var info = new FileInfo(PvfPath!);
        try
        {
            if (File.Exists(path) && System.Text.Json.JsonSerializer.Deserialize<CacheFile<T>>(File.ReadAllBytes(path)) is { } cached
                && cached.V == version && cached.Size == info.Length && cached.Ticks == info.LastWriteTimeUtc.Ticks && cached.Data is not null)
            {
                stop.ThrowIfCancellationRequested();
                return cached.Data;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or NotSupportedException) { }
        var built = build();
        stop.ThrowIfCancellationRequested();
        try
        {
            Directory.CreateDirectory(CacheDirectory!);
            string temp = path + ".tmp";
            byte[] content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new CacheFile<T>(version, info.Length, info.LastWriteTimeUtc.Ticks, built));
            stop.ThrowIfCancellationRequested();
            File.WriteAllBytes(temp, content);
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return built;
    }

    public NpcFavorCatalog? NpcFavor { get; init; }
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

    readonly Lazy<ImmutableDictionary<byte, DfoServer.GameWorld.Characters.CharacterGrowthOptions>> growthOptions = new(() =>
    {
        var map = ImmutableDictionary.CreateBuilder<byte, DfoServer.GameWorld.Characters.CharacterGrowthOptions>();
        foreach (var entry in DfoServer.GameWorld.Characters.CharacterCatalog.ReadJobList(pvf))
        {
            string text = pvf.ReadText("character/" + entry.Value);
            map[(byte)entry.Key] = DfoServer.GameWorld.Characters.CharacterCatalog.ParseGrowthOptions(text, (byte)entry.Key);
        }
        return map.ToImmutable();
    }, LazyThreadSafetyMode.ExecutionAndPublication);
    public DfoServer.GameWorld.Characters.CharacterGrowthOptions GrowthOptions(byte job) =>
        growthOptions.Value.TryGetValue(job, out var options) ? options : throw new InvalidDataException("职业成长名册不存在");

    // 高级筛选用的属性索引：仅在第一次高级筛选时才在后台构建（全量扫描物品脚本，约数十秒），未就绪时明确提示稍后重试。
    public ImmutableDictionary<int, GmItemAttributes.Attrs> Attributes => Ready(AttributesTask, "物品属性索引");
    public bool AttributesReady => attributesTask?.IsCompletedSuccessfully == true;
    public void WarmAttributes(TimeSpan timeout)
    {
        if (!AttributesTask.Wait(timeout)) throw new TimeoutException("物品属性索引构建超时");
    }

    static ImmutableDictionary<int, GmItemAttributes.Attrs> BuildAttributes(PvfArchive pvf, CancellationToken stop)
    {
        var map = ImmutableDictionary.CreateBuilder<int, GmItemAttributes.Attrs>();
        foreach (var (equipment, list) in new[] { (true, "equipment/equipment.lst"), (false, "stackable/stackable.lst") })
            foreach (var (id, path) in PvfScript.List(pvf.ReadText(list)))
            {
                stop.ThrowIfCancellationRequested();
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
    public ImmutableDictionary<int, string> QuestNames => Ready(QuestNamesTask, "任务名称索引");

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
        Task[] tasks = [.. itemIndex ? [ItemsTask] : Array.Empty<Task>(), .. questIndex ? [QuestNamesTask] : Array.Empty<Task>()];
        if (!Task.WaitAll(tasks, timeout))
            throw new TimeoutException("开发GM索引构建超时");
    }

    public string QuestName(int id) => QuestNamesTask.IsCompletedSuccessfully && QuestNamesTask.Result.TryGetValue(id, out var name) ? name : "任务 " + id;

    // 称号簿任务脚本通常没有 [name]，客户端显示的是称号簿槽位上的称号物品名。
    ImmutableDictionary<int, string>? titleItemNames;
    readonly object titleNameGate = new();
    public string DisplayQuestName(int id)
    {
        if (QuestNames.TryGetValue(id, out var name) && name.Length > 0) return name;
        return TitleItemNames.TryGetValue(id, out var itemName) && itemName.Length > 0 ? itemName : "任务 " + id;
    }

    ImmutableDictionary<int, string> TitleItemNames
    {
        get
        {
            if (titleItemNames is not null) return titleItemNames;
            lock (titleNameGate)
            {
                if (titleItemNames is not null) return titleItemNames;
                var byQuest = new Dictionary<int, int>();
                foreach (var slot in Titles.Slots.Values)
                    if (slot.QuestId > 0 && !slot.Items.IsDefaultOrEmpty && !byQuest.ContainsKey(slot.QuestId))
                        byQuest[slot.QuestId] = slot.Items[0];
                var map = ImmutableDictionary.CreateBuilder<int, string>();
                foreach (var quest in Titles.Quests.Values)
                {
                    if (QuestNames.ContainsKey(quest.Quest.Id) || !byQuest.TryGetValue(quest.Quest.Id, out int itemId)) continue;
                    try
                    {
                        string itemName = resources.Items.Get(itemId).Name.Trim();
                        if (itemName.Length > 0) map[quest.Quest.Id] = itemName;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException) { }
                }
                titleItemNames = map.ToImmutable();
                return titleItemNames;
            }
        }
    }

    static ImmutableArray<ItemEntry> BuildItems(PvfArchive pvf, CancellationToken stop)
    {
        var rows = ImmutableArray.CreateBuilder<ItemEntry>();
        foreach (var (equipment, list) in new[] { (true, "equipment/equipment.lst"), (false, "stackable/stackable.lst") })
            foreach (var (id, path) in PvfScript.List(pvf.ReadText(list)))
            {
                stop.ThrowIfCancellationRequested();
                try
                {
                    var doc = equipment ? EquipmentFile.ReadScript(pvf.ReadText, "equipment/" + path) : new PvfDocument(pvf.ReadText("stackable/" + path));
                    string name = doc.Section("name").Trim('`', ' ', '\r', '\n');
                    string tag = Regex.Match(doc.Section(equipment ? "equipment type" : "stackable type"), @"\[([^\]]+)\]").Groups[1].Value.ToLowerInvariant();
                    int level = doc.TryGetInt32("minimum level", out int minimum) ? minimum : 0;
                    int rarity = doc.TryGetInt32("rarity", out int value) ? value : 0;
                    int set = PvfScript.Integers(doc.Section("part set index")).FirstOrDefault();
                    rows.Add(new(id, name, equipment, tag, level, rarity, set, Jobs(doc.Section("usable job")), GrowMask(doc.Section("usable first growtype")),
                        equipment ? WeaponSub(tag, path) : ""));
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

    // 没有 [usable first growtype] 的装备、徽章、护石不按分支藏。写了 0–5 才锁分支；写了但认不出的不当成通用。
    static ushort GrowMask(string text)
    {
        if (text.Length == 0) return ushort.MaxValue;
        var values = PvfScript.Integers(text);
        if (values.Length == 0) return 0;
        ushort mask = 0;
        foreach (int value in values)
        {
            if (value is < 0 or > 5) return 0;
            mask |= (ushort)(1 << value);
        }
        return mask;
    }

    static ImmutableDictionary<int, string> BuildQuestNames(PvfArchive pvf, QuestCatalog? quests, CancellationToken stop)
    {
        var names = ImmutableDictionary.CreateBuilder<int, string>();
        foreach (var quest in quests?.All ?? [])
        {
            stop.ThrowIfCancellationRequested();
            try
            {
                string section = PvfScript.Section(pvf.ReadText(quest.Path), "name").Trim();
                var quoted = Regex.Match(section, "`([^`]*)`");
                string name = (quoted.Success ? quoted.Groups[1].Value : section).Trim().Trim('`');
                if (name.Length > 0) names[quest.Id] = name;
            }
            catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException) { }
        }
        return names.ToImmutable();
    }
}
