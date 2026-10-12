using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal enum EnhancementMode : byte { Reinforce, Amplify }

internal sealed record EnhancementDefinition(int ItemId, EnhancementMode Mode, byte Level, int SuccessWeight,
    bool Additional, int RawLevelLimit, int MinimumItemLevel, int MaximumItemLevel,
    ImmutableHashSet<int> AllowedItems, string? PendingRule = null)
{
    public bool Available => PendingRule is null;
    public bool TryTargetLevel(byte current, out byte desired)
    {
        int level = Additional ? current + Level : Level;
        desired = 0;
        if (!Available || current > 31 || level is < 1 or > 31 || Additional && RawLevelLimit >= 0 && current > RawLevelLimit)
            return false;
        desired = (byte)level;
        return true;
    }
}

// 两份共享LST仅用于实际请求的券和目标；不扫描STK或另载PVF。
internal sealed class EnhancementCatalog
{
    readonly Func<string, string> read;
    readonly IReadOnlyDictionary<int, string> stackable, equipment;
    readonly ConcurrentDictionary<int, Lazy<EnhancementDefinition?>> definitions = new();
    readonly ConcurrentDictionary<int, Lazy<(bool Reinforce, bool Amplify)>> targets = new();

    public EnhancementCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable, IReadOnlyDictionary<int, string> equipment)
        : this(archive.ReadText, stackable, equipment) { }
    internal EnhancementCatalog(Func<string, string> read, IReadOnlyDictionary<int, string> stackable, IReadOnlyDictionary<int, string> equipment)
    { this.read = read; this.stackable = stackable; this.equipment = equipment; }

    public EnhancementDefinition? Get(int id)
    {
        var pending = definitions.GetOrAdd(id, key => new(() => Load(key)));
        try { return pending.Value; }
        catch { definitions.TryRemove(new KeyValuePair<int, Lazy<EnhancementDefinition?>>(id, pending)); throw; }
    }
    public bool Allows(int id, EnhancementMode mode)
    {
        var pending = targets.GetOrAdd(id, key => new(() => Target(key)));
        try { var value = pending.Value; return mode == EnhancementMode.Reinforce ? value.Reinforce : value.Amplify; }
        catch { targets.TryRemove(new KeyValuePair<int, Lazy<(bool Reinforce, bool Amplify)>>(id, pending)); throw; }
    }

    (bool, bool) Target(int id)
    {
        if (!equipment.TryGetValue(id, out var path)) return (false, false);
        var script = EquipmentFile.ReadScript(read, "equipment/" + path);
        if (script.Has("impossible contents") && !script.Has("/impossible contents")) return (false, false);
        var forbidden = Regex.Matches(script.Block("impossible contents"), "`([^`]*)`")
            .Select(match => match.Groups[1].Value.Trim('[', ']', ' ').ToLowerInvariant()).ToHashSet();
        return (!forbidden.Contains("upgrade"), !forbidden.Contains("upgrade") && !forbidden.Contains("amplify upgrade"));
    }

    EnhancementDefinition? Load(int id)
    {
        if (!stackable.TryGetValue(id, out var path)) return null;
        return Parse(id, read("stackable/" + path));
    }

    internal static EnhancementDefinition? Parse(int id, string text)
    {
        var reinforce = Fields(text, "equipment reinforcement ticket");
        var amplify = Fields(text, "equipment amplify reinforcement ticket");
        var lucky = Fields(text, "lucky enchant deed");
        if (reinforce.Count + amplify.Count + lucky.Count == 0) return null;
        EnhancementDefinition Pending(string reason) => new(id, EnhancementMode.Reinforce, 0, 0, false, -1, 0, 0, [], reason);
        if (reinforce.Count + amplify.Count + lucky.Count != 1) return Pending("重复或混合的强化券资源族");
        if (lucky.Count != 0) return Pending("幸运强化/增幅券的本版资源及失败政策尚未闭合");
        var field = reinforce.Count == 1 ? reinforce[0] : amplify[0];
        var match = Regex.Match(Value(text, field), @"^\s*(\d+)\s+(\d+)\s+`(fixed|additional)`\s+(-?\d+)\s*$", RegexOptions.CultureInvariant);
        if (!match.Success || !byte.TryParse(match.Groups[1].Value, out byte level) || level is < 1 or > 31 ||
            !int.TryParse(match.Groups[2].Value, out int chance) || chance is < 0 or > 100 ||
            !int.TryParse(match.Groups[4].Value, out int maximum) || maximum < -1)
            return Pending("无效的等级/概率/覆盖模式或未知附加参数");
        if (match.Groups[3].Value == "fixed" && maximum != -1)
            return new(id, reinforce.Count == 1 ? EnhancementMode.Reinforce : EnhancementMode.Amplify,
                level, chance * 100, false, maximum, 0, byte.MaxValue, [], "固定券第四原始参数的限制语义尚未闭合");
        foreach (string name in new[] { "enchant random", "need material", "target item id", "cooltime maintenance", "effect maintenance",
            "total usable count", "total weekly usable count", "total accumulated usable count", "use count limit all server item",
            "usable datetime", "usable first growtype", "action usable server type", "usable game server type", "usable server type",
            "reinforcement usable rarity", "reinforcement usable equipment type" })
            if (Fields(text, name).Count != 0) return Pending("尚未闭合的券资格或附加消费: " + name);
        int min = 0, max = byte.MaxValue;
        var itemLevels = Fields(text, "check usable itemlevel");
        if (itemLevels.Count > 1) return Pending("重复的目标装备等级限制");
        if (itemLevels.Count == 1)
        {
            var numbers = Value(text, itemLevels[0]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (numbers.Length != 2 || !int.TryParse(numbers[0], out min) || !int.TryParse(numbers[1], out max) || min < 0 || max < min)
                return Pending("目标装备等级限制无效");
        }
        var allowed = ImmutableHashSet.CreateBuilder<int>();
        var lists = Blocks(text, "reinforcement usable item list");
        if (Fields(text, "reinforcement usable item list").Count != lists.Count || lists.Count > 1)
            return Pending("目标装备清单未闭合或重复");
        if (lists.Count == 1)
            foreach (string token in Value(text, lists[0]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value <= 0)
                    return Pending("目标装备清单无效");
                allowed.Add(value);
            }
        var places = Blocks(text, "action usable place");
        if (Fields(text, "action usable place").Count != places.Count || places.Count > 1) return Pending("使用地点限制未闭合");
        if (places.Count == 1 && !Regex.IsMatch(Value(text, places[0]), @"^\s*(?:`\[(?:village|seria room)\]`\s*)+$"))
            return Pending("尚未闭合的使用地点限制");
        if (places.Count == 1 && !Regex.Matches(Value(text, places[0]), "`([^`]*)`")
            .Any(place => place.Groups[1].Value == "[village]"))
            return Pending("仅赛丽亚房间的使用限制尚未接入，不能放宽成所有Town");
        return new(id, reinforce.Count == 1 ? EnhancementMode.Reinforce : EnhancementMode.Amplify,
            level, chance * 100, match.Groups[3].Value == "additional", maximum, min, max, allowed.ToImmutable());
    }

    static string Mask(string text) => Regex.Replace(text, "`[^`]*`", match => new string(' ', match.Length));
    static MatchCollection Fields(string text, string name) => Regex.Matches(Mask(text), @"\[" + Regex.Escape(name) + @"\]([^\[]*)");
    static MatchCollection Blocks(string text, string name) => Regex.Matches(Mask(text),
        @"\[" + Regex.Escape(name) + @"\](.*?)\[/" + Regex.Escape(name) + @"\]", RegexOptions.Singleline);
    static string Value(string text, Match match) => text.Substring(match.Groups[1].Index, match.Groups[1].Length);
}
