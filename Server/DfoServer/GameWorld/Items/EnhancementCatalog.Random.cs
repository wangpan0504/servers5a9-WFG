using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld.Items;

internal sealed partial class EnhancementCatalog
{
    static EnhancementDefinition ParseRandom(int id, string text)
    {
        EnhancementDefinition Pending(string reason) => new(id, EnhancementMode.RandomReinforce, 0, 0,
            false, -1, 0, 0, [], reason);
        // 幸运券和随机锻造也带enchant random，必须留给它们各自的动作，不能按强化券消费。
        if (Fields(text, "action type").Count != 0 || Fields(text, "lucky enchant deed").Count != 0)
            return Pending("随机资格属于独立的物品动作");
        var blocks = Blocks(text, "enchant random");
        if (blocks.Count != 1 || Fields(text, "enchant random").Count != 1)
            return Pending("随机强化声明重复或未闭合");
        string body = Value(text, blocks[0]);
        var names = Regex.Matches(Mask(body), @"\[([^\]]+)\]").Select(match => match.Groups[1].Value).ToArray();
        if (!names.SequenceEqual(new[] { "er_grade", "er_slot", "er_seal", "er_enchant", "/er_enchant" }) ||
            body[..body.IndexOf('[')].Trim().Length != 0 || Value(body, Fields(body, "/er_enchant")[0]).Trim().Length != 0)
            return Pending("随机强化字段缺失、重复或含未知资格");
        int[]? Numbers(string name)
        {
            var fields = Fields(body, name);
            if (fields.Count != 1) return null;
            var result = new List<int>();
            foreach (string token in Value(body, fields[0]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token, out int value)) return null;
                result.Add(value);
            }
            return result.ToArray();
        }
        var rarities = Numbers("er_grade");
        var slots = Numbers("er_slot");
        var seals = Numbers("er_seal");
        var levels = Numbers("er_enchant");
        if (rarities is not { Length: > 0 } || rarities.Any(value => value is < 0 or > 6) ||
            slots is not [>= 0 and <= 3] || seals is not [>= 0 and <= 2] ||
            levels is not { Length: >= 3 } || levels[0] < 1 || levels.Length != 1L + 2L * levels[0])
            return Pending("随机强化等级数、稀有度、部位或封装字段无效");
        var weights = ImmutableArray.CreateBuilder<EnhancementLevelWeight>();
        long total = 0;
        for (int at = 1; at < levels.Length; at += 2)
        {
            // ACK的随机结果分支支持+1至+13；未知等级保持拒绝，不能夹紧成另一个结果。
            if (levels[at] is < 1 or > 13 || levels[at + 1] < 0)
                return Pending("随机强化结果超出本版消费者边界");
            weights.Add(new((byte)levels[at], levels[at + 1]));
            total += levels[at + 1];
        }
        if (total is <= 0 or > int.MaxValue) return Pending("随机强化总权重无效");
        // 共用固定券已经闭合的装备清单、装备等级和使用资格，不从名称或说明猜限制。
        return ParseRestrictions(new(id, EnhancementMode.RandomReinforce, 0, 0, false, -1, 0, byte.MaxValue, [])
        {
            RandomLevels = weights.ToImmutable(), Rarities = rarities.ToImmutableHashSet(),
            SlotClass = slots[0], SealedOnly = seals[0] == 1
        }, text.Remove(blocks[0].Index, blocks[0].Length));
    }
}
