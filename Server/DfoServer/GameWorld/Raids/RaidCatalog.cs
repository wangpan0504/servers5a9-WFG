using PvfLib;
using System.Collections.Immutable;

namespace DfoServer.GameWorld.Raids;

// etc/raid/raid.lst：团本类型 → 脚本（0 Anton、1 Luke、2 Fiendwar_KR、3 Prey、4/5 DNFAOS）。
// 客户端 01DDADC0 用记录 +0x24（0x24E/0x24F 的第一个 u8）按同一下标读 [团本管理器+0x120] 列表并取 PHASE NAME，
// 实机 +0x24=0 时窗口标题显示 Anton 第 0 阶段「阻截安徒恩」，与本表一致。
// 人数上限与开打最低人数只取脚本 [RAID MEMBER MAX]/[RAID START MINIMUM MEMBER]（超时空之战为 [FIENDWAR ...]），缺任一项的类型不可创建。
internal sealed record RaidDefinition(byte Type, string Path, int MemberMax, int StartMinimum, int PhaseMax)
{
    public RaidResources? Resources { get; init; }
    public RaidCost? EntryCost { get; init; }
    public RaidCost? GuideEntryCost { get; init; }
    public RaidCost? Cost(bool guide) => guide ? GuideEntryCost : EntryCost;
}

internal sealed class RaidCatalog(ImmutableDictionary<byte, RaidDefinition> definitions)
{
    public const string ListPath = "etc/raid/raid.lst";
    public static readonly RaidCatalog Empty = new(ImmutableDictionary<byte, RaidDefinition>.Empty);
    public ImmutableDictionary<byte, RaidDefinition> Definitions { get; } = definitions;

    public RaidDefinition? Find(byte type) => Definitions.GetValueOrDefault(type);

    public RaidCatalog PrepareResources(PvfArchive pvf, Dungeons.DungeonCatalog dungeons, Items.ItemCatalog items,
        Dungeons.DungeonRoomCatalog? rooms = null, IReadOnlyList<(int Dungeon, int MazeOrdinal)>? lukeObjectChoices = null,
        Func<int, string?>? antonMapPath = null,
        Func<RaidDungeonCatalog, IReadOnlyList<(int Dungeon, int MazeOrdinal)>>? lukeResourceChoices = null,
        IReadOnlyDictionary<int, string>? antonMapPoolPaths = null)
    {
        var prepared = Definitions;
        foreach (byte type in new byte[] { 0, 1, 3 })
            if (Find(type) is { } raid)
            {
                var resources = RaidResourceLoader.Load(pvf, raid, dungeons, items, type == 0 ? antonMapPath : null,
                    type == 0 ? antonMapPoolPaths : null);
                if (type == 1 && rooms is not null && (lukeObjectChoices is not null || lukeResourceChoices is not null) && resources.EntryCatalog is { } entry)
                    resources = resources with { LukeObjects = LukeRaidObjectCatalog.Load(pvf, entry, rooms, lukeResourceChoices?.Invoke(entry) ?? lukeObjectChoices!),
                        LukeCardSet = LukeCardSetCatalog.Load(pvf, entry, rooms) };
                prepared = prepared.SetItem(type, raid with { Resources = resources });
            }
        return new(prepared);
    }

    public static RaidCatalog Load(PvfArchive pvf)
    {
        if (!pvf.Contains(ListPath)) return Empty;
        var rows = ImmutableDictionary.CreateBuilder<byte, RaidDefinition>();
        foreach (var (index, path) in PvfScript.List(pvf.ReadText(ListPath)))
        {
            if (index is < 0 or > byte.MaxValue || !pvf.Contains(path)) continue;
            var definition = Parse((byte)index, path, pvf.ReadTokens(path), pvf.ResolveString);
            if (definition is not null) rows[(byte)index] = definition;
        }
        return new(rows.ToImmutable());
    }

    internal static RaidDefinition? Parse(byte type, string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        int? member = First(tokens, resolve, "[raid member max]") ?? First(tokens, resolve, "[fiendwar member max]");
        int? minimum = First(tokens, resolve, "[raid start minimum member]") ?? First(tokens, resolve, "[fiendwar start minimum member]");
        int? phases = First(tokens, resolve, "[phase max]");
        if (member is not > 0 || minimum is not > 0 || minimum > member || phases is not > 0) return null;
        var fields = RaidScriptParser.Fields(tokens, resolve, (_, _, _) => { });
        var headers = new List<RaidField>();
        int phaseDepth = 0;
        foreach (var field in fields)
        {
            if (field.Tag is "[phase]" or "[single phase]") { phaseDepth++; continue; }
            if (field.Tag is "[/phase]" or "[/single phase]") { phaseDepth = Math.Max(0, phaseDepth - 1); continue; }
            if (phaseDepth == 0) headers.Add(field);
        }
        RaidCost? Cost(string tag)
        {
            var rows = headers.Select((field, index) => (field, index)).Where(row => row.field.Tag == tag).ToArray();
            string end = "[/" + tag[1..];
            if (rows is not [var row] || row.field.Values is not [{ Type: 0, Number: > 0 } item, { Type: 0, Number: > 0 } count] ||
                headers.Count(field => field.Tag == end) != 1 || row.index + 1 >= headers.Count || headers[row.index + 1].Tag != end ||
                !headers[row.index + 1].Values.IsEmpty) return null;
            return new(item.Number, count.Number);
        }
        // 费用来自原始团本头，独立于战斗资源是否已装配；引导票不能替代普通票。
        return new(type, PvfArchive.NormalizePath(path), member.Value, minimum.Value, phases.Value)
        { EntryCost = Cost("[entry cost]"), GuideEntryCost = Cost("[single entry cost]") };
    }

    // 第一处顶层出现的整数值；同名标签再次出现（如各区 [MEMBER MAX]）不影响，因为标签名不同。
    static int? First(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, string tag)
    {
        for (int index = 0; index + 1 < tokens.Count; index++)
            if (tokens[index].Type == 3 && resolve(tokens[index].Value).Trim().Equals(tag, StringComparison.OrdinalIgnoreCase))
                return tokens[index + 1].Type == 0 ? tokens[index + 1].Value : null;
        return null;
    }
}
