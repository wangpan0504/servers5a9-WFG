using PvfLib;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld.Dungeons;

// 实验性（#256，未在95客户端实证）：深渊派对房间的波次。依据参考项目 90 轻装上阵 go-server dungeon_hell_party_wave.go / dungeon_hell_party_catalog.go
// （对应 A21 BuildHellPartyWaves / PickHellPartyEntry / BuildHellPartyMonsterInfos）：
// - 深渊房间图上的柱子 [special passive object] 的 [hellparty] 子段是 (组号 权重 波次号) 三元组表；
// - 怪物本体在 etc/hellparty.etc 的 [hellparty monster group]：[group index] N → [group] `A` code entityType ...（字母=难度档，entityType 1=APC）；
// - 每个波次号内，只保留 etc 里存在该难度档条目的候选，按权重抽 1 个组，展开成怪物。
internal sealed record DungeonHellWaveMonster(int Order, int GroupId, int MonsterId, byte Level, byte Type, ushort ObjectKey, int PacketIndex)
{
    // 怪物/APC 的 PVF 资源路径（升格为真正的房间敌对怪时用于 DungeonRoomMonster/AiCharacter 身份）；测试夹具可为空。
    public string Path { get; init; } = "";
}

// etc/hellparty.etc [difficulty] 的一组 `字母 + 5 整数`（字段名照参考项目 HellPartyDifficultyRule）：
// RewardRollCount=第 1 个（最后一个怪死亡时的装备投骰次数），Probability=第 4 个（玩家未点名难度时按它加权随机档位）。
internal sealed record DungeonHellDifficultyRule(char Letter, int RewardRollCount, int Probability);

internal sealed class DungeonHellPartyCatalog
{
    readonly Dictionary<int, Dictionary<char, List<(int Code, int EntityType)>>> groups = [];
    readonly Dictionary<char, DungeonHellDifficultyRule> rules = [];

    public static DungeonHellPartyCatalog Load(PvfArchive pvf)
    {
        var catalog = new DungeonHellPartyCatalog();
        if (!pvf.Contains("etc/hellparty.etc")) return catalog;
        catalog.Parse(pvf.ReadText("etc/hellparty.etc"));
        return catalog;
    }

    internal static DungeonHellPartyCatalog FromText(string text)
    {
        var catalog = new DungeonHellPartyCatalog();
        catalog.Parse(text);
        return catalog;
    }

    void Parse(string text)
    {
        // [difficulty] 4 `A` 8 5 100 100 0 `B` 6 4 0 0 0 ...：首个整数是组数，之后每组 字母 + 5 整数。
        var difficultyTable = Regex.Match(text, @"\[difficulty\](.*?)\[/difficulty\]", RegexOptions.Singleline);
        if (difficultyTable.Success)
        {
            var tokens = difficultyTable.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!tokens[i].StartsWith('`') || tokens[i].Length < 3 || i + 5 >= tokens.Length) continue;
                var values = tokens.Skip(i + 1).Take(5).Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : int.MinValue).ToArray();
                if (values.Contains(int.MinValue)) continue;
                char letter = char.ToUpperInvariant(tokens[i].Trim('`')[0]);
                rules.TryAdd(letter, new(letter, Math.Max(0, values[0]), Math.Max(0, values[3])));
                i += 5;
            }
        }
        int start = text.IndexOf("[hellparty monster group]", StringComparison.Ordinal);
        int end = text.IndexOf("[/hellparty monster group]", StringComparison.Ordinal);
        if (start < 0 || end < start) return;
        string body = text[start..end];
        foreach (Match block in Regex.Matches(body, @"\[group index\]\s*(\d+)\s*((?:\[group\].*?\[/group\]\s*)+)", RegexOptions.Singleline))
        {
            int id = int.Parse(block.Groups[1].Value, CultureInfo.InvariantCulture);
            foreach (Match group in Regex.Matches(block.Groups[2].Value, @"\[group\](.*?)\[/group\]", RegexOptions.Singleline))
            {
                char difficulty = 'A';
                var numbers = new List<int>();
                foreach (string token in group.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.StartsWith('`')) { difficulty = char.ToUpperInvariant(token.Trim('`')[0]); continue; }
                    if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) numbers.Add(number);
                }
                var entries = new List<(int, int)>();
                for (int i = 0; i + 1 < numbers.Count; i += 2) entries.Add((numbers[i], numbers[i + 1] == 1 ? 1 : 0));
                if (!groups.TryGetValue(id, out var byDifficulty)) groups[id] = byDifficulty = [];
                if (!byDifficulty.ContainsKey(difficulty)) byDifficulty[difficulty] = entries;
            }
        }
    }

    public int GroupCount => groups.Count;
    public IReadOnlyDictionary<char, DungeonHellDifficultyRule> Rules => rules;
    // 档位 1..4 对应字母 A..D。
    public static char TierLetter(byte mode) => mode is >= 1 and <= 26 ? (char)('A' + mode - 1) : 'A';
    // 参考项目 ResolveManualHellPartyMode：玩家没点名难度（请求 [8]=0）时，按 [difficulty] 的 Probability 加权随机档位；权重不可用时回退 1（A 档）。
    public byte ResolveManualMode(Func<int, int> draw)
    {
        var weighted = rules.Values.OrderBy(rule => rule.Letter).Where(rule => rule.Probability > 0 && rule.Letter is >= 'A' and <= 'D').ToArray();
        int total = weighted.Sum(rule => rule.Probability);
        if (total <= 0) return 1;
        int roll = draw(total);
        foreach (var rule in weighted)
        {
            if (roll < rule.Probability) return checked((byte)(rule.Letter - 'A' + 1));
            roll -= rule.Probability;
        }
        return 1;
    }
    // 该档位在最后一个怪死亡时的装备投骰次数；档位表缺失时按 0（不发奖励），不编造默认值。
    public int RewardRollCount(byte mode) => rules.TryGetValue(TierLetter(mode), out var rule) ? rule.RewardRollCount : 0;
    // A21 HellModeToGroupKey：难度 2 取 'B'，其余取 'A'。
    public static char DifficultyKey(byte mode) => mode == 2 ? 'B' : 'A';
    public IReadOnlyList<(int Code, int EntityType)> Entries(int group, char difficulty) =>
        groups.TryGetValue(group, out var byDifficulty) && byDifficulty.TryGetValue(difficulty, out var entries) ? entries : [];

    // pillar [hellparty] 子段 RawValues = (组号 权重 波次号)*；按波次号升序，每波内只留有条目的候选，按权重抽 1 个。
    public IReadOnlyList<(int Order, int GroupId)> PickWaves(IEnumerable<IReadOnlyList<int>> pillarTables, byte mode, Func<int, int> draw)
    {
        char key = DifficultyKey(mode);
        var candidates = new List<(int Group, int Weight, int Order)>();
        foreach (var table in pillarTables)
            for (int i = 0; i + 2 < table.Count; i += 3)
                candidates.Add((table[i], table[i + 1], table[i + 2]));
        var picked = new List<(int, int)>();
        foreach (var order in candidates.GroupBy(row => row.Order).OrderBy(row => row.Key))
        {
            var usable = order.Where(row => row.Weight > 0 && Entries(row.Group, key).Count != 0).ToArray();
            if (usable.Length == 0) continue;
            int roll = draw(usable.Sum(row => row.Weight));
            foreach (var row in usable)
            {
                if (roll < row.Weight) { picked.Add((order.Key, row.Group)); break; }
                roll -= row.Weight;
            }
        }
        return picked;
    }
}
