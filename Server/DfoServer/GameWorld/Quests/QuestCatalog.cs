using PvfLib;

namespace DfoServer.GameWorld.Quests;

// 完整任务目录；资源定义不等于接取、通关或领奖资格。
internal readonly record struct QuestRewardToken(byte Type, int Value, string? Text);

internal sealed record StoryQuest(
    int Id, string Path, string Condition, int SubType, IReadOnlyList<int> IntData,
    int Npc, int CompleteNpc, int MinimumLevel, int MaximumLevel,
    IReadOnlyList<int> Prerequisites, int? MainQuest, int? DungeonIndex, int? DungeonParameter,
    IReadOnlyList<QuestRewardToken> Rewards, int? BossTemplate, int? ShowNpcOnClear,
    IReadOnlyList<int> MapNpcIds)
{
    public string? Grade { get; init; }
    public string? Difficulty { get; init; }
    public string? RewardType { get; init; }
    public int? DeclaredGoldMultiple { get; init; }
    public int? DeclaredIgnoreLevelForExperience { get; init; }
    public IReadOnlyList<string> Attributes { get; init; } = Array.Empty<string>();
    public QuestDefinition? Definition { get; init; }
    public string? CinematicPath { get; init; }
}

internal sealed class QuestCatalog
{
    // 既有地图字段回归；不限制任务目录或可接任务。
    static readonly int[] VerifiedMapQuests = [3145, 4873, 3146, 3157, 3054, 3147, 3148, 4875, 3149];
    readonly Dictionary<int, StoryQuest> quests;

    internal QuestCatalog(Dictionary<int, StoryQuest> quests) => this.quests = quests;
    public IReadOnlyCollection<StoryQuest> All => quests.Values;
    public bool TryGet(int id, out StoryQuest quest) => quests.TryGetValue(id, out quest!);

    public StoryQuest Get(int id) => quests.TryGetValue(id, out var quest)
        ? quest : throw new InvalidDataException("任务目录没有这个任务: " + id);

    public static QuestCatalog Load(PvfArchive pvf)
    {

        var paths = Index(pvf);
        var maps = MapIndex(pvf);
        var cinematicTokens = pvf.ReadTokens("cinematic/cinematic.lst");
        var cinematics = new Dictionary<int,string>();
        for (int at = 0; at + 1 < cinematicTokens.Length; at += 2)
            if (cinematicTokens[at].Type == 0 && cinematicTokens[at + 1].Type == 6)
                cinematics[cinematicTokens[at].Value] = PvfArchive.NormalizePath("cinematic/" + pvf.ResolveString(cinematicTokens[at + 1].Value));
        return LoadDefinitions(paths, (id,path) =>
        {
            var tokens = pvf.ReadTokens(path);
            var definition = QuestDefinitionReader.Read(id, path, tokens, pvf.ResolveString);
            if (definition.Condition == "[look cinematic]" && definition.IntData is [> 0] &&
                cinematics.TryGetValue(definition.IntData[0],out string? cinematicPath))
            {
                // 只读取声明的演出；坏的废弃资源由LoadDefinitions隔离，不替换成其他演出。
                _ = pvf.ReadTokens(cinematicPath);
                definition = definition with { CinematicPath = cinematicPath };
            }
            if (VerifiedMapQuests.Contains(id))
                definition = Parse(id, path, tokens, pvf.ResolveString, maps, pvf) with { Definition = definition.Definition };
            return definition;
        });
    }

    internal static QuestCatalog LoadDefinitions(IReadOnlyDictionary<int,string> paths, Func<int,string,StoryQuest> read)
    {
        var quests = new Dictionary<int, StoryQuest>();
        foreach (var (id,path) in paths)
        {
            try { quests.Add(id, read(id,path)); }
            catch (Exception error) when (!VerifiedMapQuests.Contains(id) &&
                error is InvalidDataException or NotSupportedException or FileNotFoundException)
            {
                // 失效旧脚本不修补，也不阻断有效任务；已使用的新手资源仍要求真实完整装载。
                quests.Add(id, new(id,path,"",-1,[],0,0,0,0,[],null,null,null,[],null,null,[])
                {
                    Definition = new([]) { Diagnostics = ["任务脚本不可用: " + error.Message] }
                });
            }
        }
        return new(quests);
    }

    static Dictionary<int, string> Index(PvfArchive pvf)
    {
        var tokens = pvf.ReadTokens("n_quest/quest.lst");
        if (tokens.Length == 0 || tokens.Length % 2 != 0) throw new InvalidDataException("quest.lst 记录不完整");
        var paths = new Dictionary<int, string>();
        for (int i = 0; i < tokens.Length; i += 2)
        {
            if (tokens[i].Type != 0 || tokens[i + 1].Type != 6) throw new InvalidDataException("quest.lst 条目类型无效");
            string path = PvfArchive.NormalizePath("n_quest/" + pvf.ResolveString(tokens[i + 1].Value));
            if (!paths.TryAdd(tokens[i].Value, path)) throw new InvalidDataException("重复任务 ID: " + tokens[i].Value);
        }
        foreach (var (id, path) in paths)
            if (id is <= 0 or >= 30000) throw new InvalidDataException("任务编号越界: " + id);
        return paths;
    }

    static Dictionary<int, string> MapIndex(PvfArchive pvf)
    {
        var tokens = pvf.ReadTokens("map/map.lst");
        if (tokens.Length == 0 || tokens.Length % 2 != 0) throw new InvalidDataException("map.lst 记录不完整");
        var maps = new Dictionary<int, string>();
        for (int i = 0; i < tokens.Length; i += 2)
        {
            if (tokens[i].Type != 0 || tokens[i + 1].Type != 6) throw new InvalidDataException("map.lst 条目类型无效");
            maps[tokens[i].Value] = PvfArchive.NormalizePath("map/" + pvf.ResolveString(tokens[i + 1].Value));
        }
        return maps;
    }

    static StoryQuest Parse(int id, string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve,
        IReadOnlyDictionary<int, string> maps, PvfArchive pvf)
    {
        var sections = new Dictionary<string, PvfToken[]>(StringComparer.Ordinal);
        for (int i = 0; i < tokens.Count;)
        {
            if (tokens[i].Type != 3) throw new InvalidDataException("任务脚本存在无标签 token: " + id);
            string raw = resolve(tokens[i++].Value);
            if (!raw.StartsWith('[') || !raw.EndsWith(']') || raw.Length < 3) throw new InvalidDataException("任务脚本标签无效: " + id);
            string tag = raw[1..^1].Trim().ToLowerInvariant();
            int start = i;
            while (i < tokens.Count && tokens[i].Type != 3) i++;
            if (tag.StartsWith('/')) continue;
            if (tag is not ("type" or "sub type" or "int data" or "npc index" or "complete npc index" or "level"
                or "pre required quest" or "main quest" or "dungeon info" or "reward int data" or "grade" or "difficulty"
                or "reward type" or "gold multiple" or "ignore quest level 4 exp" or "attribute")) continue;
            if (!sections.TryAdd(tag, tokens.Skip(start).Take(i - start).ToArray()))
                throw new InvalidDataException("任务脚本重复标签: " + tag);
        }
        string condition = Text(sections, "type", resolve);
        if (condition is not ("[clear map]" or "[meet npc]" or "[quest clear]" or "[hunt enemy]"))
            throw new NotSupportedException("unsupported: 主线条件 " + condition);
        int[] pair = Integers(sections, "level", 2);
        int[]? dungeon = sections.ContainsKey("dungeon info") ? Integers(sections, "dungeon info", 2) : null;
        return new(id, path, condition, One(sections, "sub type"), Integers(sections, "int data"),
            One(sections, "npc index"), One(sections, "complete npc index"), pair[0], pair[1],
            sections.ContainsKey("pre required quest") ? Integers(sections, "pre required quest") : [],
            sections.ContainsKey("main quest") ? One(sections, "main quest") : null,
            dungeon?[0], dungeon?[1], Rewards(sections, resolve),
            Boss(id, condition, Integers(sections, "int data"), maps, pvf),
            ShowNpc(id, tokens, resolve),
            MapNpcs(id, condition, Integers(sections, "int data"), maps, pvf))
        {
            Grade = sections.ContainsKey("grade") ? Text(sections, "grade", resolve) : null,
            Difficulty = sections.ContainsKey("difficulty") ? Text(sections, "difficulty", resolve) : null,
            RewardType = sections.ContainsKey("reward type") ? Text(sections, "reward type", resolve) : null,
            DeclaredGoldMultiple = sections.ContainsKey("gold multiple") ? One(sections, "gold multiple") : null,
            DeclaredIgnoreLevelForExperience = sections.ContainsKey("ignore quest level 4 exp") ? One(sections, "ignore quest level 4 exp") : null,
            Attributes = sections.TryGetValue("attribute", out var attributes) ? AttributeNames(attributes, resolve) : Array.Empty<string>()
        };
    }

    static IReadOnlyList<string> AttributeNames(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        if (tokens.Any(token => token.Type != 6))
            throw new NotSupportedException("任务attribute包含非字符串，不能推定属性位");
        return Array.AsReadOnly(tokens.Select(token => resolve(token.Value)).ToArray());
    }

    static QuestRewardToken[] Rewards(Dictionary<string, PvfToken[]> sections, Func<int, string> resolve)
    {
        if (!sections.TryGetValue("reward int data", out var tokens) || tokens.Length == 0) return [];
        var rewards = new QuestRewardToken[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Type == 0) rewards[i] = new(0, tokens[i].Value, null);
            else if (tokens[i].Type == 6) rewards[i] = new(6, tokens[i].Value, resolve(tokens[i].Value));
            else throw new NotSupportedException("unsupported: 奖励 token 类型 " + tokens[i].Type);
        }
        return rewards;
    }

    static int? ShowNpc(int questId, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        int? npc = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Type != 3 || resolve(tokens[i].Value) != "[npc visibility]") continue;
            if (npc is not null) throw new InvalidDataException("重复的 npc visibility: " + questId);
            string Tag(int index) => tokens[index].Type == 3 ? resolve(tokens[index].Value) : "";
            if (i + 8 >= tokens.Count || Tag(i + 1) != "[npc]" || tokens[i + 2].Type != 0 || Tag(i + 3) != "[/npc]"
                || Tag(i + 4) != "[condition]" || tokens[i + 5].Type != 6 || resolve(tokens[i + 5].Value) != "[clear]"
                || Tag(i + 6) != "[visibility]" || tokens[i + 7].Type != 6 || resolve(tokens[i + 7].Value) != "[show]"
                || Tag(i + 8) != "[/npc visibility]")
                throw new NotSupportedException("unsupported: npc visibility 形状 " + questId);
            npc = tokens[i + 2].Value;
        }
        return npc;
    }

    static int? Boss(int questId, string condition, int[] data, IReadOnlyDictionary<int, string> maps, PvfArchive pvf)
    {
        if (condition != "[clear map]") return null;
        if (data.Length != 1 || data[0] <= 0 || !maps.TryGetValue(data[0], out var mapPath) || !pvf.Contains(mapPath))
            throw new InvalidDataException("清图任务缺少地图: " + questId);
        var tokens = pvf.ReadTokens(mapPath);
        int? boss = null;
        bool monster = false;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Type == 3)
            {
                string tag = pvf.ResolveString(tokens[i].Value);
                if (tag == "[monster]") monster = true;
                else if (tag == "[/monster]") monster = false;
                continue;
            }
            if (!monster || tokens[i].Type != 6 || pvf.ResolveString(tokens[i].Value) != "[boss]") continue;
            int cursor = i;
            while (cursor > 0 && tokens[cursor].Type == 6) cursor--;
            if (cursor < 7 || tokens[cursor].Type != 0) throw new InvalidDataException("Boss 标记前面没有怪物记录: " + mapPath);
            for (int back = 0; back < 8; back++)
                if (tokens[cursor - back].Type != 0) throw new InvalidDataException("Boss 怪物记录不是 8 个整数: " + mapPath);
            if (boss is not null) throw new InvalidDataException("清图地图有多个 [boss]: " + mapPath);
            boss = tokens[cursor - 7].Value;
        }
        return boss ?? throw new InvalidDataException("清图地图没有 [boss]: " + mapPath);
    }

    static int[] MapNpcs(int questId, string condition, int[] data, IReadOnlyDictionary<int, string> maps, PvfArchive pvf)
    {
        if (condition != "[clear map]") return [];
        if (data.Length != 1 || !maps.TryGetValue(data[0], out var mapPath))
            throw new InvalidDataException("清图任务缺少地图: " + questId);
        var tokens = pvf.ReadTokens(mapPath);
        var ids = new List<int>();
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Type != 3 || pvf.ResolveString(tokens[i].Value) != "[NPC]") continue;
            if (i + 1 >= tokens.Length || tokens[i + 1].Type != 0) throw new InvalidDataException("地图 NPC 行没有编号: " + mapPath);
            ids.Add(tokens[i + 1].Value);
        }
        return ids.ToArray();
    }

    static string Text(Dictionary<string, PvfToken[]> sections, string tag, Func<int, string> resolve)
    {
        var tokens = Required(sections, tag);
        if (tokens.Length != 1 || tokens[0].Type != 6) throw new InvalidDataException("任务标签不是单个字符串: " + tag);
        return resolve(tokens[0].Value);
    }

    static int One(Dictionary<string, PvfToken[]> sections, string tag)
    {
        int[] values = Integers(sections, tag, 1);
        return values[0];
    }

    static int[] Integers(Dictionary<string, PvfToken[]> sections, string tag, int? count = null)
    {
        var tokens = Required(sections, tag);
        if (tokens.Length == 0 || tokens.Any(t => t.Type != 0) || (count is not null && tokens.Length != count))
            throw new InvalidDataException("任务整数标签无效: " + tag);
        return tokens.Select(t => t.Value).ToArray();
    }

    static PvfToken[] Required(Dictionary<string, PvfToken[]> sections, string tag) =>
        sections.TryGetValue(tag, out var tokens) ? tokens : throw new InvalidDataException("任务缺少标签: " + tag);
}
