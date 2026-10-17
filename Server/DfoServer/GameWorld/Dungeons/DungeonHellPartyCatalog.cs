using PvfLib;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld.Dungeons;

// 实验性（#256，未在95客户端实证）：深渊派对房间的波次。依据参考项目 90 轻装上阵 go-server dungeon_hell_party_wave.go / dungeon_hell_party_catalog.go
// （对应 A21 BuildHellPartyWaves / PickHellPartyEntry / BuildHellPartyMonsterInfos）：
// - 深渊房间图上的柱子 [special passive object] 的 [hellparty] 子段是 (组号 权重 波次号) 三元组表；
// - 怪物本体在 etc/hellparty.etc 的 [hellparty monster group]：[group index] N → [group] `A` code entityType ...（字母=难度档，entityType 1=APC）；
// - 每个波次号内，只保留 etc 里存在该难度档条目的候选，按权重抽 1 个组，展开成怪物。
internal sealed record DungeonHellWaveMonster(int Order, int GroupId, int MonsterId, byte Level, byte Type, ushort ObjectKey, int PacketIndex);

internal sealed class DungeonHellPartyCatalog
{
    readonly Dictionary<int, Dictionary<char, List<(int Code, int EntityType)>>> groups = [];

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
